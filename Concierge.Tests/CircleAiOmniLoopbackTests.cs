using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concierge.Ai;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

public sealed class CircleAiOmniLoopbackTests
{
    [Fact]
    public async Task Sends_prior_turns_without_a_system_role_and_returns_the_service_answer()
    {
        var paths = new List<string>();
        using var runtime = Make(async request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get) return Json("""{"service":"up","engine":"up","model":"Qwen3-Omni"}""");
            Assert.True(request.Content!.Headers.ContentLength > 0);
            using var body = await request.Content!.ReadFromJsonAsync<JsonDocument>();
            var conversation = body!.RootElement.GetProperty("conversation");
            Assert.Equal("Now?", body.RootElement.GetProperty("text").GetString());
            Assert.Equal(3, conversation.GetArrayLength());
            Assert.Equal("user", conversation[0].GetProperty("role").GetString());
            Assert.Equal("Earlier", conversation[0].GetProperty("content").GetString());
            Assert.Equal("assistant", conversation[1].GetProperty("role").GetString());
            Assert.Equal("Now?", conversation[2].GetProperty("content").GetString());
            Assert.Null(request.Headers.Authorization);
            return Json("""{"answer":"Here is the answer.","seconds":38.0}""");
        });
        await runtime.RefreshAsync();
        Assert.True(runtime.IsReady);
        Assert.Equal("Circle AI Service is ready.", runtime.StatusMessage);
        Assert.Equal("Here is the answer.", await Answer(runtime,
            [new("system", "Concierge rules"), new("user", "Earlier"),
                new("assistant", "Previous answer"), new("user", "Now?")]));
        Assert.Equal(new[] { "/status", "/status", "/ask" }, paths);
    }

    [Fact]
    public async Task Starting_engine_and_503_wait_for_readiness_before_one_successful_retry()
    {
        var statusReads = 0;
        var asks = 0;
        using var runtime = Make(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Json(++statusReads < 3
                    ? """{"engine":"starting"}""" : """{"engine":"up"}"""));
            asks++;
            return Task.FromResult(asks == 1
                ? Json("""{"error":"Circle AI is still starting"}""", HttpStatusCode.ServiceUnavailable)
                : Json("""{"answer":"Ready now."}"""));
        });
        runtime.StartingRetryDelay = TimeSpan.FromMilliseconds(1);
        Assert.Equal("Ready now.", await Answer(runtime, [new("user", "Question")]));
        Assert.Equal(2, asks);
        Assert.Equal(4, statusReads);
    }

    [Fact]
    public async Task Connection_refused_shows_not_running_without_posting()
    {
        using var runtime = Make(_ => throw new HttpRequestException("Connection refused"));
        await runtime.RefreshAsync();
        Assert.False(runtime.IsReady);
        Assert.Equal("Circle AI Service is not running", runtime.StatusMessage);
        await Assert.ThrowsAsync<ChatRuntimeUnavailableException>(
            () => Answer(runtime, [new("user", "Question")]));
    }

    [Fact]
    public async Task Service_stopping_after_status_does_not_leave_thinking_state()
    {
        using var runtime = Make(request => request.Method == HttpMethod.Get
            ? Task.FromResult(Json("""{"engine":"up"}"""))
            : throw new HttpRequestException("Connection refused"));
        var error = await Assert.ThrowsAsync<ChatRuntimeUnavailableException>(
            () => Answer(runtime, [new("user", "Question")]));
        Assert.Equal("Circle AI Service is not running", error.Message);
        Assert.False(runtime.IsReady);
        Assert.Equal("Circle AI Service is not running", runtime.StatusMessage);
    }

    [Fact]
    public async Task Other_transport_failure_is_not_mislabeled_as_service_stopped()
    {
        using var runtime = Make(request => request.Method == HttpMethod.Get
            ? Task.FromResult(Json("""{"engine":"up"}"""))
            : throw new HttpRequestException("Response ended prematurely"));
        var error = await Assert.ThrowsAsync<IOException>(
            () => Answer(runtime, [new("user", "Question")]));
        Assert.Contains("Response ended prematurely", error.Message);
        Assert.DoesNotContain("not running", runtime.StatusMessage);
    }

    [Fact]
    public async Task Server_error_is_visible_and_is_not_retried()
    {
        var asks = 0;
        using var runtime = Make(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Json("""{"engine":"up"}""")
            : Error()));
        HttpResponseMessage Error()
        {
            asks++;
            return Json("""{"error":"Model failed to load"}""", HttpStatusCode.InternalServerError);
        }
        var error = await Assert.ThrowsAsync<IOException>(
            () => Answer(runtime, [new("user", "Question")]));
        Assert.Contains("Model failed to load", error.Message);
        Assert.Equal(1, asks);
    }

    [Fact]
    public async Task Concurrent_questions_are_serialized_within_the_app()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maxActive = 0;
        using var runtime = Make(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json("""{"engine":"up"}""");
            maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
            entered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
            return Json("""{"answer":"Done."}""");
        });
        var first = Answer(runtime, [new("user", "First")]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Answer(runtime, [new("user", "Second")]);
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.Equal("Done.", await first);
        Assert.Equal("Done.", await second);
        Assert.Equal(1, maxActive);
    }

    private static CircleAiOmniLoopbackRuntime Make(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        => new(new HttpClient(new Handler(send)));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static async Task<string> Answer(CircleAiOmniLoopbackRuntime runtime, IReadOnlyList<ChatTurn> turns)
    {
        var parts = new List<string>();
        await foreach (var piece in runtime.StreamAsync(turns)) parts.Add(piece);
        return string.Concat(parts);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
