using Concierge.Shared.Tools;

namespace Concierge.Tests;

public sealed class ToolCallProtocolTests
{
    [Fact]
    public void Extract_finds_single_block_with_name_and_arguments()
    {
        var text = """
        Looking up the file now.

        ```tool-call
        {"name": "read_file", "arguments": {"path": "Program.cs"}}
        ```
        """;

        var calls = ToolCallProtocol.Extract(text);

        Assert.Single(calls);
        Assert.Equal("read_file", calls[0].Name);
        Assert.Equal("Program.cs", calls[0].Arguments?["path"]?.GetValue<string>());
    }

    [Fact]
    public void Extract_returns_multiple_blocks_in_document_order()
    {
        var text = """
        ```tool-call
        {"name": "first", "arguments": {}}
        ```

        Some narration in between.

        ```tool-call
        {"name": "second", "arguments": {"k": 1}}
        ```
        """;

        var calls = ToolCallProtocol.Extract(text);

        Assert.Equal(2, calls.Count);
        Assert.Equal("first", calls[0].Name);
        Assert.Equal("second", calls[1].Name);
        Assert.True(calls[0].Start < calls[1].Start);
    }

    [Fact]
    public void Extract_skips_blocks_with_invalid_json_without_throwing()
    {
        var text = """
        ```tool-call
        not json at all
        ```

        ```tool-call
        {"name": "ok", "arguments": null}
        ```
        """;

        var calls = ToolCallProtocol.Extract(text);

        Assert.Single(calls);
        Assert.Equal("ok", calls[0].Name);
    }

    [Fact]
    public void Extract_skips_blocks_missing_a_name_field()
    {
        var text = """
        ```tool-call
        {"arguments": {"path": "foo"}}
        ```
        """;

        var calls = ToolCallProtocol.Extract(text);

        Assert.Empty(calls);
    }

    [Fact]
    public void FormatResult_emits_a_tool_result_block_with_success_and_output()
    {
        var result = new AgentToolResult(true, "hello world", null);

        var formatted = ToolCallProtocol.FormatResult("read_file", result);

        Assert.Contains("```tool-result", formatted, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"read_file\"", formatted, StringComparison.Ordinal);
        Assert.Contains("\"success\":true", formatted, StringComparison.Ordinal);
        Assert.Contains("\"output\":\"hello world\"", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatToolResultMessage_concatenates_multiple_results_with_lead_in()
    {
        var message = ToolCallProtocol.FormatToolResultMessage(new[]
        {
            ("a", new AgentToolResult(true, "first", null)),
            ("b", new AgentToolResult(false, string.Empty, "boom")),
        });

        Assert.Contains("Tool results follow", message, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"a\"", message, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"b\"", message, StringComparison.Ordinal);
        Assert.Contains("\"failure\":\"boom\"", message, StringComparison.Ordinal);
    }
}
