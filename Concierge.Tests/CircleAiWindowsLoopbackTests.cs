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

    private sealed class Service : HttpMessageHandler
    {
        public bool Ready, Complete = true, Streaming = true;
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
                    schema = 1, canAnswerNow = Ready,
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
