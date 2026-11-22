using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Concierge.Ai;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

public sealed class CircleAiWindowsServiceRuntimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_only_reads_the_service_and_never_selects_downloads_or_loads_models(bool ready)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, "{\"BaseUrl\":\"http://127.0.0.1:5208\",\"ApiKey\":\"test-only\"}");
        var handler = new ReadOnlyStartup(ready);
        try
        {
            using var runtime = new CircleAiWindowsServiceRuntime(path, new HttpClient(handler));
            await runtime.InitializeAsync(default);
            Assert.Equal(ready, runtime.IsReady);
            Assert.Equal(new[] { "/v1/diagnostics", "/v1/models" }, handler.Paths);
            Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
        }
        finally { File.Delete(path); }
    }
    private sealed class ReadOnlyStartup(bool ready) : HttpMessageHandler
    {
        public List<string> Paths = []; public List<HttpMethod> Methods = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath); Methods.Add(request.Method);
            var json = request.RequestUri.AbsolutePath == "/v1/diagnostics" ? "{\"server_version\":\"3.8.1\"}" : ready
                ? "{\"data\":[{\"id\":\"service-chosen\",\"supports_streaming\":true}]}" : "{\"data\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    [Fact]
    public void Prompt_diagnostics_include_all_system_layers_without_retaining_text()
    {
        var messages = new ChatTurn[] { new("system", new string('x', 20000)), new("system", "abcd"), new("user", "12345678") };
        var diagnostics = CircleAiWindowsServiceRuntime.MeasurePrompt(messages, "selected");
        Assert.Equal(3, diagnostics.MessageCount);
        Assert.Equal(20012, diagnostics.Characters);
        Assert.Equal(5009, diagnostics.EstimatedSystemTokens);
        Assert.Equal(5015, diagnostics.EstimatedTokens);
        Assert.DoesNotContain(new string('x', 100), JsonSerializer.Serialize(diagnostics));
    }
    [Theory]
    [InlineData("{\"data\":[{\"id\":\"resident\",\"supports_streaming\":true}]}", "resident")]
    [InlineData("{\"data\":[]}", null)]
    [InlineData("{\"data\":[{\"id\":\"resident\",\"supports_streaming\":false}]}", null)]
    [InlineData("{\"data\":[{\"id\":\"a\",\"supports_streaming\":true},{\"id\":\"b\",\"supports_streaming\":true}]}", null)]
    public void Resident_model_comes_from_the_services_single_streaming_entry(string json, string? expected)
    {
        using var models = JsonDocument.Parse(json);
        Assert.Equal(expected, CircleAiWindowsServiceRuntime.SingleLoadedStreamingModel(models));
    }
    // These tests isolate HTTP delivery after readiness. Actual preparation is
    // verified separately against the installed Windows service.
    private static CircleAiWindowsServiceRuntime Ready(HttpMessageHandler handler)
    {
        var runtime = new CircleAiWindowsServiceRuntime();
        var httpField = typeof(CircleAiWindowsServiceRuntime).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)httpField.GetValue(runtime)!).Dispose();
        httpField.SetValue(runtime, new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5208") });
        typeof(CircleAiWindowsServiceRuntime).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, "test-model");
        typeof(CircleAiWindowsServiceRuntime).GetProperty(nameof(runtime.IsReady))!.SetValue(runtime, true);
        return runtime;
    }

    private static async Task<string> Answer(CircleAiWindowsServiceRuntime runtime, CancellationToken token = default)
    {
        var text = new StringBuilder();
        await foreach (var delta in runtime.StreamAsync([new ChatTurn("user", "A question")], token)) text.Append(delta);
        return text.ToString();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Streaming_choice_reaches_HTTP_and_unavailable_agentic_is_not_claimed(bool stream, bool agentic)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var preferences = new Concierge.Shared.Settings.CircleAiServicePreferences(path);
            await preferences.SaveAsync(new(stream, agentic));
            using var runtime = new CircleAiWindowsServiceRuntime(null,
                new HttpClient(new ChoiceHandler(stream)) { BaseAddress = new("http://127.0.0.1:5208") }, preferences);
            typeof(CircleAiWindowsServiceRuntime).GetProperty(nameof(runtime.IsReady))!.SetValue(runtime, true);
            Assert.Equal("13", await Answer(runtime));
            if (agentic) Assert.Contains(stream ? "does not combine" : "not connected", runtime.StatusMessage);
        }
        finally { File.Delete(path); }
    }

    private sealed class ChoiceHandler(bool stream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(stream, body.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(body.RootElement.TryGetProperty("agentic", out _)); // Not a field in this endpoint's contract.
            return new(HttpStatusCode.OK) { Content = new StringContent(stream
                ? "data: {\"choices\":[{\"delta\":{\"content\":\"13\"}}]}\n\ndata: [DONE]\n\n"
                : "{\"choices\":[{\"message\":{\"content\":\"13\"}}]}", Encoding.UTF8,
                stream ? "text/event-stream" : "application/json") };
        }
    }

    [Fact]
    public async Task Loopback_service_receives_the_separate_allowance_rules_and_prior_turns()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, "{\"BaseUrl\":\"http://127.0.0.1:5208\",\"Token\":\"test-only\"}");
        var handler = new AllowancePayloadHandler();
        try
        {
            using var runtime = new CircleAiWindowsServiceRuntime(path, new HttpClient(handler));
            await runtime.InitializeAsync(default);
            Assert.True(runtime.IsReady);
            var turns = new ChatTurn[]
            {
                new("system", "Answer the person's actual request directly."),
                new("user", "Each child gets $8 a week if they finish their own list."),
                new("assistant", "I understand there are two children."),
                new("user", "One has four chores and the other has five. Split the allowance evenly per completed chore, but cap each child at $8."),
                new("assistant", "I will calculate each allowance separately."),
                new("user", "What do I pay if the first completes three chores and the second completes all five?")
            };
            var answer = new StringBuilder();
            await foreach (var piece in runtime.StreamAsync(turns)) answer.Append(piece);
            Assert.Equal("ok", answer.ToString());
            Assert.True(handler.SawAllTurns);
        }
        finally { File.Delete(path); }
    }

    private sealed class AllowancePayloadHandler : HttpMessageHandler
    {
        public bool SawAllTurns { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/status")
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"schema\":2,\"canAnswerNow\":true,\"brain\":{\"modelId\":\"service-chosen\",\"detail\":\"Ready\"}}", Encoding.UTF8, "application/json")
                };

            Assert.Equal("/butler/stream", request.RequestUri.AbsolutePath);
            Assert.Equal("test-only", request.Headers.GetValues("X-Butler-Token").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(["system", "user", "assistant", "user", "assistant", "user"],
                messages.Select(message => message.GetProperty("role").GetString()));
            Assert.Contains("Each child gets $8", messages[1].GetProperty("content").GetString());
            Assert.Contains("four chores and the other has five", messages[3].GetProperty("content").GetString());
            Assert.Contains("cap each child at $8", messages[3].GetProperty("content").GetString());
            Assert.Contains("first completes three chores", messages[5].GetProperty("content").GetString());
            SawAllTurns = true;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("event: token\ndata: \"ok\"\n\nevent: done\ndata: null\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }

    [Fact]
    public async Task Desktop_and_watch_wait_for_one_service_slot()
    {
        var handler = new BlockingHandler();
        using var runtime = Ready(handler);
        var desktop = Answer(runtime);
        await handler.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var watch = Answer(runtime);
        try
        {
            var observed = await Task.WhenAny(handler.SecondEntered.Task, Task.Delay(200));
            Assert.NotSame(handler.SecondEntered.Task, observed);
            Assert.Equal(1, handler.Calls);
        }
        finally { handler.Release.TrySetResult(); }
        Assert.Equal(["13", "13"], await Task.WhenAll(desktop, watch));
        Assert.Equal(1, handler.MaxActive);
        Assert.True(handler.Deterministic);
    }

    [Fact]
    public async Task Cancelling_a_waiting_turn_does_not_cancel_the_active_turn()
    {
        var handler = new BlockingHandler();
        using var runtime = Ready(handler);
        var desktop = Answer(runtime);
        await handler.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        var watch = Answer(runtime, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
        Assert.Equal(1, handler.Calls);
        handler.Release.TrySetResult();
        Assert.Equal("13", await desktop);
        Assert.Equal("13", await Answer(runtime));
    }

    [Fact]
    public async Task A_silent_service_cannot_hold_the_Desktop_turn_forever()
    {
        using var runtime = Ready(new SilentHandler());
        runtime.ResponseIdleTimeout = TimeSpan.FromMilliseconds(80);
        await Assert.ThrowsAsync<TimeoutException>(() => Answer(runtime));
        Assert.Contains("has not sent an answer", runtime.StatusMessage);
    }

    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The canceled request should not finish.");
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SecondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls, MaxActive;
        private int _active;
        public bool Deterministic = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            int call;
            lock (this) { call = ++Calls; MaxActive = Math.Max(MaxActive, ++_active); }
            try
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Deterministic &= body.RootElement.GetProperty("temperature").GetDouble() == 0;
                Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
                if (call == 1) { FirstEntered.TrySetResult(); await Release.Task.WaitAsync(token); }
                else SecondEntered.TrySetResult();
                return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"13\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
            }
            finally { lock (this) _active--; }
        }
    }
}
