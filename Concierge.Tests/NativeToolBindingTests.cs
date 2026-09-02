using System.Text.Json.Nodes;
using Concierge.Shared.Chat;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What native function calling must do (parity feature 13): give a cloud model the tool
/// schema in its own vocabulary, and read its calls back.
/// </summary>
/// <remarks>
/// The fenced-markdown protocol works with any model and stays as the fallback for on-device
/// ones. But asking Claude or GPT to emit fenced JSON when they have a real function-calling
/// API is handicapping them — the provider validates against the schema, and the model was
/// trained on that shape.
/// </remarks>
public sealed class NativeToolBindingTests
{
    private static readonly IAgentTool[] Tools =
    [
        new StubTool("read_file", "Read a file", """{"type":"object","properties":{"path":{"type":"string"}}}"""),
    ];

    // ── Sending the schema ─────────────────────────────────────────────

    [Fact]
    public void OpenAi_tools_are_wrapped_as_functions()
    {
        var rendered = NativeToolBinding.ToOpenAi(Tools);

        Assert.Equal("function", rendered[0]!["type"]!.GetValue<string>());
        Assert.Equal("read_file", rendered[0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_tools_carry_the_argument_schema()
    {
        var rendered = NativeToolBinding.ToOpenAi(Tools);

        Assert.Contains("path", rendered[0]!["function"]!["parameters"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Anthropic_tools_use_input_schema()
    {
        var rendered = NativeToolBinding.ToAnthropic(Tools);

        Assert.Equal("read_file", rendered[0]!["name"]!.GetValue<string>());
        Assert.NotNull(rendered[0]!["input_schema"]);
    }

    [Fact]
    public void Gemini_wraps_every_tool_in_one_declaration_list()
    {
        // Gemini takes a single entry holding all declarations, not one entry per tool.
        var rendered = NativeToolBinding.ToGemini(Tools);

        Assert.Single(rendered);
        Assert.Single((rendered[0]!["functionDeclarations"] as JsonArray)!);
    }

    [Fact]
    public void A_tool_with_no_arguments_still_gets_a_schema()
    {
        // Providers reject a missing parameters object, so "takes nothing" has to be said
        // rather than left out.
        var rendered = NativeToolBinding.ToOpenAi([new StubTool("ping", "Ping", null)]);

        Assert.Equal("object", rendered[0]!["function"]!["parameters"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Every_provider_receives_every_tool()
    {
        IAgentTool[] several = [
            new StubTool("one", "One", null),
            new StubTool("two", "Two", null),
            new StubTool("three", "Three", null),
        ];

        Assert.Equal(3, NativeToolBinding.ToOpenAi(several).Count);
        Assert.Equal(3, NativeToolBinding.ToAnthropic(several).Count);
        Assert.Equal(3, (NativeToolBinding.ToGemini(several)[0]!["functionDeclarations"] as JsonArray)!.Count);
    }

    // ── Reading the calls back ─────────────────────────────────────────

    [Fact]
    public void An_openai_delta_with_a_call_is_read()
    {
        var delta = JsonNode.Parse("""
            {"tool_calls":[{"id":"call_1","function":{"name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}]}
            """);

        var call = NativeToolBinding.ReadOpenAiCall(delta);

        Assert.Equal("read_file", call!.Name);
        Assert.Equal("a.txt", call.Arguments!["path"]!.GetValue<string>());
    }

    [Fact]
    public void An_openai_delta_with_only_text_is_not_a_call()
    {
        Assert.Null(NativeToolBinding.ReadOpenAiCall(JsonNode.Parse("""{"content":"just talking"}""")));
    }

    [Fact]
    public void An_openai_call_keeps_the_id_that_pairs_its_result()
    {
        var delta = JsonNode.Parse("""
            {"tool_calls":[{"id":"call_abc","function":{"name":"read_file","arguments":"{}"}}]}
            """);

        Assert.Equal("call_abc", NativeToolBinding.ReadOpenAiCall(delta)!.Id);
    }

    [Fact]
    public void An_anthropic_tool_use_block_is_read()
    {
        var block = JsonNode.Parse("""
            {"type":"tool_use","id":"toolu_1","name":"read_file","input":{"path":"a.txt"}}
            """);

        var call = NativeToolBinding.ReadAnthropicCall(block);

        Assert.Equal("read_file", call!.Name);
        Assert.Equal("a.txt", call.Arguments!["path"]!.GetValue<string>());
    }

    [Fact]
    public void An_anthropic_text_block_is_not_a_call()
    {
        Assert.Null(NativeToolBinding.ReadAnthropicCall(JsonNode.Parse("""{"type":"text","text":"talking"}""")));
    }

    [Fact]
    public void A_gemini_function_call_part_is_read()
    {
        var part = JsonNode.Parse("""{"functionCall":{"name":"read_file","args":{"path":"a.txt"}}}""");

        var call = NativeToolBinding.ReadGeminiCall(part);

        Assert.Equal("read_file", call!.Name);
        Assert.Equal("a.txt", call.Arguments!["path"]!.GetValue<string>());
    }

    [Fact]
    public void A_gemini_text_part_is_not_a_call()
    {
        Assert.Null(NativeToolBinding.ReadGeminiCall(JsonNode.Parse("""{"text":"talking"}""")));
    }

    [Fact]
    public void A_call_with_no_name_is_not_a_call()
    {
        var delta = JsonNode.Parse("""{"tool_calls":[{"id":"x","function":{"arguments":"{}"}}]}""");

        Assert.Null(NativeToolBinding.ReadOpenAiCall(delta));
    }

    [Fact]
    public void Arguments_cut_off_mid_stream_read_as_nothing_rather_than_throwing()
    {
        // A truncated stream leaves half a JSON object. The call is reported with no
        // arguments so the caller can fail it cleanly and let the model retry.
        var delta = JsonNode.Parse("""
            {"tool_calls":[{"id":"x","function":{"name":"read_file","arguments":"{\"path\":"}}]}
            """);

        Assert.Null(NativeToolBinding.ReadOpenAiCall(delta)!.Arguments);
    }

    [Fact]
    public void A_call_with_no_arguments_reads_as_an_empty_object()
    {
        var delta = JsonNode.Parse("""{"tool_calls":[{"id":"x","function":{"name":"ping","arguments":""}}]}""");

        Assert.Empty((NativeToolBinding.ReadOpenAiCall(delta)!.Arguments as JsonObject)!);
    }

    private sealed class StubTool(string name, string description, string? schema) : IAgentTool
    {
        public string Name => name;
        public string Description => description;
        public JsonNode? ArgumentsSchema => schema is null ? null : JsonNode.Parse(schema);
        public bool IsReadOnly => true;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolResult(true, string.Empty, null));
    }
}
