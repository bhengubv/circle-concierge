using System.Net;
using System.Net.Http;
using System.Text;
using Concierge.Chat.Cloud;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Tests;

public sealed class CloudChatRuntimeTests
{
    // ------------------------------------------------------------------ OpenAI

    [Fact]
    public async Task OpenAi_runtime_streams_delta_content_chunks_from_sse()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler((req, _) =>
        {
            Assert.Equal("Bearer test-key", req.Headers.Authorization?.ToString());
            Assert.Equal("/v1/chat/completions", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com") };
        var runtime = new OpenAiChatRuntime(http, new OpenAiChatOptions { ApiKey = "test-key" }, NullLogger<OpenAiChatRuntime>.Instance);

        var chunks = await CollectAsync(runtime.StreamAsync([new ChatTurn("user", "Hi")]));

        Assert.Equal("openai", runtime.Id);
        Assert.True(runtime.IsReady);
        Assert.Equal(new[] { "Hello", " world" }, chunks);
    }

    [Fact]
    public async Task OpenAi_runtime_without_api_key_yields_needs_key_status()
    {
        using var http = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("HTTP should not be reached")));
        var runtime = new OpenAiChatRuntime(http, new OpenAiChatOptions { ApiKey = null }, NullLogger<OpenAiChatRuntime>.Instance);

        Assert.False(runtime.IsReady);
        var chunks = await CollectAsync(runtime.StreamAsync([new ChatTurn("user", "Hi")]));
        Assert.Single(chunks);
        Assert.Contains("not configured", chunks[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAi_runtime_surfaces_http_error_in_the_stream()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"invalid key\"}", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com") };
        var runtime = new OpenAiChatRuntime(http, new OpenAiChatOptions { ApiKey = "bad-key" }, NullLogger<OpenAiChatRuntime>.Instance);

        var chunks = await CollectAsync(runtime.StreamAsync([new ChatTurn("user", "Hi")]));

        Assert.Single(chunks);
        Assert.Contains("401", chunks[0], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Anthropic

    [Fact]
    public async Task Anthropic_runtime_streams_content_block_delta_text_and_lifts_system_prompt()
    {
        var sse =
            "data: {\"type\":\"message_start\"}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi \"}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Bob.\"}}\n\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        string? sentBody = null;
        var handler = new StubHandler((req, _) =>
        {
            Assert.Equal("test-anthropic-key", req.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", req.Headers.GetValues("anthropic-version").Single());
            Assert.Equal("/v1/messages", req.RequestUri!.AbsolutePath);
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com") };
        var runtime = new AnthropicChatRuntime(http, new AnthropicChatOptions { ApiKey = "test-anthropic-key" }, NullLogger<AnthropicChatRuntime>.Instance);

        var chunks = await CollectAsync(runtime.StreamAsync([
            new ChatTurn("system", "You are helpful."),
            new ChatTurn("user", "Hi"),
        ]));

        Assert.Equal("anthropic", runtime.Id);
        Assert.Equal(new[] { "Hi ", "Bob." }, chunks);
        Assert.NotNull(sentBody);
        Assert.Contains("\"system\":", sentBody);
        Assert.Contains("\"You are helpful.\"", sentBody);
        // System must not appear in the messages array — Anthropic rejects role: "system" inline.
        var messagesStart = sentBody!.IndexOf("\"messages\":", StringComparison.Ordinal);
        Assert.True(messagesStart > 0);
        Assert.DoesNotContain("\"system\"", sentBody[messagesStart..]);
    }

    // ------------------------------------------------------------------ Gemini

    [Fact]
    public async Task Gemini_runtime_streams_candidate_parts_text_and_maps_assistant_role_to_model()
    {
        var sse =
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Once \"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"upon a time.\"}]}}]}\n\n";
        string? sentBody = null;
        var handler = new StubHandler((req, _) =>
        {
            Assert.StartsWith("/v1beta/models/gemini-2.0-flash:streamGenerateContent", req.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            Assert.Contains("alt=sse", req.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("key=", req.RequestUri.Query, StringComparison.Ordinal);
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com") };
        var runtime = new GeminiChatRuntime(http, new GeminiChatOptions { ApiKey = "test-gemini-key" }, NullLogger<GeminiChatRuntime>.Instance);

        var chunks = await CollectAsync(runtime.StreamAsync([
            new ChatTurn("system", "Tell stories."),
            new ChatTurn("user", "Begin."),
            new ChatTurn("assistant", "Sure."),
        ]));

        Assert.Equal("gemini", runtime.Id);
        Assert.Equal(new[] { "Once ", "upon a time." }, chunks);
        Assert.NotNull(sentBody);
        Assert.Contains("\"systemInstruction\":", sentBody);
        // "assistant" -> "model" mapping is required by Gemini's role vocabulary.
        Assert.Contains("\"role\":\"model\"", sentBody);
        Assert.DoesNotContain("\"role\":\"assistant\"", sentBody);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var result = new List<string>();
        await foreach (var chunk in source)
        {
            result.Add(chunk);
        }
        return result;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _send;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_send(request, cancellationToken));
    }
}
