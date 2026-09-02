using System.Text;
using System.Text.Json.Nodes;
using Concierge.Shared.Rpc;

namespace Concierge.Tests;

/// <summary>
/// What the JSON-RPC core must do: carry requests and replies over a pipe, in both framings,
/// so MCP, LSP, ACP and Concierge's own RPC surface can all sit on one implementation.
/// </summary>
/// <remarks>
/// Four of the remaining features are the same protocol wearing different method names.
/// Writing it once is what makes them small; it also means none of them needs a third-party
/// library, so none of them can be taken away.
/// </remarks>
public sealed class JsonRpcTests
{
    // ── Envelopes ──────────────────────────────────────────────────────

    [Fact]
    public void A_request_carries_its_method_and_id()
    {
        var json = JsonRpcMessage.Request(1, "tools/list", null).ToJson();

        Assert.Contains("\"method\":\"tools/list\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_declares_the_protocol_version()
    {
        Assert.Contains("\"jsonrpc\":\"2.0\"", JsonRpcMessage.Request(1, "x", null).ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_notification_has_no_id()
    {
        // The difference that matters: a notification is never answered, so a client that
        // gave it an id would wait for a reply that is not coming.
        Assert.DoesNotContain("\"id\"", JsonRpcMessage.Notification("cancelled", null).ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_reply_is_matched_to_its_request_by_id()
    {
        var parsed = JsonRpcMessage.Parse("""{"jsonrpc":"2.0","id":7,"result":{"ok":true}}""");

        Assert.Equal(7, parsed!.Id);
    }

    [Fact]
    public void A_result_is_readable()
    {
        var parsed = JsonRpcMessage.Parse("""{"jsonrpc":"2.0","id":1,"result":{"value":42}}""");

        Assert.Equal(42, parsed!.Result!["value"]!.GetValue<int>());
    }

    [Fact]
    public void An_error_reply_is_distinguishable_from_a_result()
    {
        var parsed = JsonRpcMessage.Parse("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"no such method"}}""");

        Assert.True(parsed!.IsError);
        Assert.Contains("no such method", parsed.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void Nonsense_parses_to_nothing_rather_than_throwing()
    {
        // A peer can send anything. A malformed frame must not take the connection with it.
        Assert.Null(JsonRpcMessage.Parse("this is not json"));
    }

    [Fact]
    public void An_empty_frame_parses_to_nothing()
    {
        Assert.Null(JsonRpcMessage.Parse("   "));
    }

    // ── Line framing (MCP, ACP) ────────────────────────────────────────

    [Fact]
    public async Task A_line_framed_message_is_written_as_one_line()
    {
        var buffer = new MemoryStream();
        var framing = new LineFraming();

        await framing.WriteAsync(buffer, """{"jsonrpc":"2.0","id":1,"method":"x"}""");

        Assert.EndsWith("\n", Encoding.UTF8.GetString(buffer.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_framed_message_is_read_back()
    {
        var framing = new LineFraming();
        var buffer = new MemoryStream(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1}\n"));

        var read = await framing.ReadAsync(buffer);

        Assert.Contains("\"id\":1", read!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_pipe_reads_as_nothing()
    {
        Assert.Null(await new LineFraming().ReadAsync(new MemoryStream()));
    }

    // ── Header framing (LSP) ───────────────────────────────────────────

    [Fact]
    public async Task A_header_framed_message_declares_its_length()
    {
        var buffer = new MemoryStream();

        await new HeaderFraming().WriteAsync(buffer, """{"id":1}""");

        Assert.Contains("Content-Length: 8", Encoding.UTF8.GetString(buffer.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_header_framed_message_is_read_back()
    {
        var framing = new HeaderFraming();
        var payload = """{"jsonrpc":"2.0","id":1}""";
        var raw = $"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}";

        var read = await framing.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw)));

        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Header_framing_reads_exactly_the_declared_length()
    {
        // Two messages back to back. Reading past the length would swallow the next one.
        var framing = new HeaderFraming();
        var first = """{"id":1}""";
        var second = """{"id":2}""";
        var raw = $"Content-Length: 8\r\n\r\n{first}Content-Length: 8\r\n\r\n{second}";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));

        Assert.Equal(first, await framing.ReadAsync(stream));
        Assert.Equal(second, await framing.ReadAsync(stream));
    }

    [Fact]
    public async Task A_header_framed_message_with_a_unicode_body_is_read_whole()
    {
        // Content-Length counts bytes, not characters. Reading characters truncates isiZulu,
        // Mandarin and emoji alike.
        var framing = new HeaderFraming();
        var payload = """{"text":"ngiyabonga 好 🙂"}""";
        var raw = $"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}";

        Assert.Equal(payload, await framing.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw))));
    }

    [Fact]
    public async Task A_truncated_header_framed_message_reads_as_nothing()
    {
        var raw = "Content-Length: 100\r\n\r\n{\"id\":1}";

        Assert.Null(await new HeaderFraming().ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw))));
    }

    // ── Round trip over a pipe ─────────────────────────────────────────

    [Fact]
    public async Task A_client_receives_the_reply_to_its_request()
    {
        await using var peer = new LoopbackPeer(request =>
            JsonRpcMessage.Reply(request.Id!.Value, new JsonObject { ["answered"] = true }));
        var client = new JsonRpcClient(peer.ClientToServer, peer.ServerToClient, new LineFraming());

        var result = await client.InvokeAsync("anything", null);

        Assert.True(result!["answered"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_error_reply_becomes_an_exception_the_caller_can_read()
    {
        await using var peer = new LoopbackPeer(request =>
            JsonRpcMessage.Error(request.Id!.Value, -32601, "no such method"));
        var client = new JsonRpcClient(peer.ClientToServer, peer.ServerToClient, new LineFraming());

        var failure = await Assert.ThrowsAsync<JsonRpcException>(() => client.InvokeAsync("missing", null));

        Assert.Contains("no such method", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replies_reach_the_right_caller_when_several_are_in_flight()
    {
        // Servers answer out of order. Matching by id is the only thing that keeps two
        // concurrent callers from receiving each other's answers.
        await using var peer = new LoopbackPeer(
            request => JsonRpcMessage.Reply(request.Id!.Value, new JsonObject { ["echo"] = request.Id!.Value }),
            answerInReverse: true);
        var client = new JsonRpcClient(peer.ClientToServer, peer.ServerToClient, new LineFraming());

        var results = await Task.WhenAll(
            client.InvokeAsync("first", null),
            client.InvokeAsync("second", null),
            client.InvokeAsync("third", null));

        Assert.Equal(3, results.Select(r => r!["echo"]!.GetValue<int>()).Distinct().Count());
    }

    [Fact]
    public async Task A_request_that_is_never_answered_gives_up_rather_than_hanging()
    {
        await using var peer = new LoopbackPeer(_ => null);
        var client = new JsonRpcClient(
            peer.ClientToServer,
            peer.ServerToClient,
            new LineFraming(),
            timeout: TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TimeoutException>(() => client.InvokeAsync("silence", null));
    }
}
