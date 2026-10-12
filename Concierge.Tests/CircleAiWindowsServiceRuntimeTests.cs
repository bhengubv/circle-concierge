using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Concierge.Ai;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

public sealed class CircleAiWindowsServiceRuntimeTests
{
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
