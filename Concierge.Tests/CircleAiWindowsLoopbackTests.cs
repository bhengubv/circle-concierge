using System.Net;
using System.Text;
using Concierge.Ai;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

public sealed class CircleAiWindowsLoopbackTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reports_preparation_then_ready_and_preserves_full_history(bool streaming)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{"BaseUrl":"http://127.0.0.1:50162","Token":"test-only"}""");
        var handler = new Service { Streaming = streaming };
        var preferencesPath = path + ".preferences";
        try
        {
            var preferences = new Concierge.Shared.Settings.CircleAiServicePreferences(preferencesPath);
            await preferences.SaveAsync(new(streaming, true));
            using var runtime = new CircleAiWindowsServiceRuntime(path, new HttpClient(handler), preferences);
            await runtime.InitializeAsync(default);
            Assert.False(runtime.IsReady);
            Assert.Contains("Preparing", runtime.StatusMessage);
            handler.Ready = true;
            await File.WriteAllTextAsync(path, """{"BaseUrl":"http://127.0.0.1:50163","Token":"test-only"}""");
            await runtime.InitializeAsync(default);
            Assert.True(runtime.IsReady);
            Assert.Equal(50163, handler.LastPort);
            var text = new StringBuilder();
            await foreach (var piece in runtime.StreamAsync([new("system", "policy"), new("user", "first question"),
                new("assistant", "previous answer"), new("user", "follow-up question")]))
                text.Append(piece);
            Assert.Equal("Answer", text.ToString());
            Assert.Contains("policy", handler.Payload);
            using var payload = System.Text.Json.JsonDocument.Parse(handler.Payload);
            Assert.Equal(4, payload.RootElement.GetProperty("messages").GetArrayLength());
            Assert.Contains("first question", handler.Payload);
            Assert.Contains("previous answer", handler.Payload);
            Assert.Contains("follow-up question", handler.Payload);
            Assert.DoesNotContain("\"model\"", handler.Payload);
            handler.Ready = false;
            await runtime.InitializeAsync(default);
            Assert.False(runtime.IsReady);
        }
        finally { File.Delete(path); File.Delete(preferencesPath); }
    }

    [Fact]
    public async Task Stream_without_done_event_is_a_failure()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{"BaseUrl":"http://127.0.0.1:50162","Token":"test-only"}""");
        try
        {
            using var runtime = new CircleAiWindowsServiceRuntime(path, new HttpClient(new Service { Ready = true, Complete = false }));
            await runtime.InitializeAsync(default);
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                await foreach (var piece in runtime.StreamAsync([new("user", "question")])) { }
            });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Installed_lazy_model_accepts_the_first_question()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{"BaseUrl":"http://127.0.0.1:50162","Token":"test-only"}""");
        try
        {
            var handler = new Service { Soon = true };
            using var runtime = new CircleAiWindowsServiceRuntime(path, new HttpClient(handler));
            await runtime.InitializeAsync(default);
            Assert.True(runtime.IsReady);
            Assert.Contains("when you ask", runtime.StatusMessage);
            var answer = new StringBuilder();
            await foreach (var part in runtime.StreamAsync([new("user", "first question")])) answer.Append(part);
            Assert.Equal("Answer", answer.ToString());
            Assert.Contains("first question", handler.Payload);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Connection_handoff_waits_for_the_active_answer()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{"BaseUrl":"http://127.0.0.1:50162","Token":"old-test"}""");
        try
        {
            var handler = new HandoffService();
            using var runtime = new CircleAiWindowsServiceRuntime(path, new HttpClient(handler));
            await runtime.InitializeAsync(default);
            var answer = Task.Run(async () =>
            {
                var text = new StringBuilder();
                await foreach (var part in runtime.StreamAsync([new("user", "question")])) text.Append(part);
                return text.ToString();
            });
            await handler.AnswerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(path, """{"BaseUrl":"http://127.0.0.1:50163","Token":"new-test"}""");
            var refresh = runtime.InitializeAsync(default);
            try
            {
                var observed = await Task.WhenAny(handler.NewEndpointEntered.Task, Task.Delay(200));
                Assert.NotSame(handler.NewEndpointEntered.Task, observed);
                Assert.False(refresh.IsCompleted);
                Assert.Equal(0, handler.NewEndpointCalls);
            }
            finally { handler.ReleaseAnswer.TrySetResult(); }
            Assert.Equal("Answer", await answer);
            await refresh;
            Assert.Equal(1, handler.NewEndpointCalls);
            Assert.True(runtime.IsReady);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Next_question_uses_rotated_service_connection_without_manual_refresh()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{"BaseUrl":"http://127.0.0.1:50162","Token":"old-test"}""");
        try
        {
            var handler = new HandoffService();
            handler.ReleaseAnswer.TrySetResult();
            using var runtime = new CircleAiWindowsServiceRuntime(path, new HttpClient(handler));
            await runtime.InitializeAsync(default);
            await File.WriteAllTextAsync(path, """{"BaseUrl":"http://127.0.0.1:50163","Token":"new-test"}""");
            var answer = new StringBuilder();
            await foreach (var part in runtime.StreamAsync([new("user", "after restart")])) answer.Append(part);
            Assert.Equal("Answer", answer.ToString());
            Assert.Equal(2, handler.NewEndpointCalls); // status and streamed answer
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Missing_handoff_file_never_sends_to_the_stale_endpoint()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{"BaseUrl":"http://127.0.0.1:50162","Token":"test-only"}""");
        try
        {
            var handler = new Service { Ready = true };
            using var runtime = new CircleAiWindowsServiceRuntime(path, new HttpClient(handler));
            await runtime.InitializeAsync(default);
            File.Delete(path);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in runtime.StreamAsync([new("user", "after invalid handoff")])) { }
            });
            Assert.False(runtime.IsReady);
            Assert.Equal(string.Empty, handler.Payload);
        }
        finally { File.Delete(path); }
    }

    private sealed class HandoffService : HttpMessageHandler
    {
        public readonly TaskCompletionSource AnswerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource NewEndpointEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int NewEndpointCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var isNew = request.RequestUri!.Port == 50163;
            Assert.Equal(isNew ? "new-test" : "old-test", request.Headers.GetValues("X-Butler-Token").Single());
            if (isNew) { Interlocked.Increment(ref NewEndpointCalls); NewEndpointEntered.TrySetResult(); }
            if (request.RequestUri.AbsolutePath == "/v1/status")
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"schema":1,"canAnswerNow":true,"brain":{"modelId":"selected","detail":"Ready"}}""", Encoding.UTF8, "application/json") };
            Assert.Equal("/butler/stream", request.RequestUri.AbsolutePath);
            AnswerEntered.TrySetResult();
            await ReleaseAnswer.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: \"Answer\"\n\nevent: done\ndata: {}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class Service : HttpMessageHandler
    {
        public bool Ready, Soon, Complete = true, Streaming = true;
        public string Payload = "";
        public int LastPort;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPort = request.RequestUri!.Port;
            Assert.Equal("test-only", request.Headers.GetValues("X-Butler-Token").Single());
            Assert.False(request.Headers.Contains("X-CircleAI-Api-Key"));
            if (request.RequestUri!.AbsolutePath == "/v1/status")
                return Reply(System.Text.Json.JsonSerializer.Serialize(new
                {
                    schema = 1, canAnswerNow = Ready, canAnswerSoon = Soon,
                    brain = new { modelId = "chosen-by-service", detail = Ready ? "Ready" : "Preparing" }
                }));
            Assert.Equal(Streaming ? "/butler/stream" : "/butler/chat", request.RequestUri.AbsolutePath);
            Payload = await request.Content!.ReadAsStringAsync(ct);
            return Reply(Streaming ? "data: \"Answer\"\n\n" + (Complete ? "event: done\ndata: {}\n\n" : "")
                : """{"content":"Answer"}""");
        }
        private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK)
            { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
