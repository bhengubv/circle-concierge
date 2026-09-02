using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Chat;

/// <summary>A tool call the model made through a provider's own function calling.</summary>
/// <param name="Id">The provider's id for this call, used to pair the result back to it.</param>
/// <param name="Name">The tool being called.</param>
/// <param name="Arguments">The arguments, already parsed.</param>
public sealed record NativeToolCall(string Id, string Name, JsonNode? Arguments);

/// <summary>
/// Translates Concierge's tools into each provider's function-calling vocabulary, and reads
/// their tool calls back out.
/// </summary>
/// <remarks>
/// <para>
/// The fenced-markdown protocol in <see cref="Tools.ToolCallProtocol"/> works with any model,
/// which is why it exists — an on-device Qwen has no native binding. But a cloud model asked
/// to emit fenced JSON when it has a real function-calling API is being handicapped: the
/// provider validates arguments against the schema, and the model was trained on that shape.
/// </para>
/// <para>
/// Three providers, three spellings of the same idea. The differences are small and entirely
/// arbitrary, which is exactly why they belong in one place rather than spread through three
/// runtimes.
/// </para>
/// </remarks>
public static class NativeToolBinding
{
    /// <summary>Tools as OpenAI's <c>tools</c> array.</summary>
    public static JsonArray ToOpenAi(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var rendered = new JsonArray();
        foreach (var tool in tools)
        {
            rendered.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.ArgumentsSchema?.DeepClone() ?? EmptySchema(),
                },
            });
        }

        return rendered;
    }

    /// <summary>Tools as Anthropic's <c>tools</c> array.</summary>
    public static JsonArray ToAnthropic(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var rendered = new JsonArray();
        foreach (var tool in tools)
        {
            rendered.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.ArgumentsSchema?.DeepClone() ?? EmptySchema(),
            });
        }

        return rendered;
    }

    /// <summary>Tools as Gemini's <c>functionDeclarations</c>, wrapped as it expects.</summary>
    public static JsonArray ToGemini(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var declarations = new JsonArray();
        foreach (var tool in tools)
        {
            declarations.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.ArgumentsSchema?.DeepClone() ?? EmptySchema(),
            });
        }

        // Gemini takes one entry holding all declarations, not one entry per tool.
        return new JsonArray { new JsonObject { ["functionDeclarations"] = declarations } };
    }

    /// <summary>Read a tool call out of an OpenAI streaming delta, if the delta carries one.</summary>
    public static NativeToolCall? ReadOpenAiCall(JsonNode? delta)
    {
        if (delta?["tool_calls"] is not JsonArray calls || calls.Count == 0)
        {
            return null;
        }

        var call = calls[0] as JsonObject;
        var function = call?["function"] as JsonObject;
        var name = function?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new NativeToolCall(
            call?["id"]?.GetValue<string>() ?? name,
            name,
            ParseArguments(function?["arguments"]?.GetValue<string>()));
    }

    /// <summary>Read a tool call out of an Anthropic content block, if it is one.</summary>
    public static NativeToolCall? ReadAnthropicCall(JsonNode? block)
    {
        if (block?["type"]?.GetValue<string>() != "tool_use")
        {
            return null;
        }

        var name = block["name"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new NativeToolCall(block["id"]?.GetValue<string>() ?? name, name, block["input"]?.DeepClone());
    }

    /// <summary>Read a tool call out of a Gemini part, if it is one.</summary>
    public static NativeToolCall? ReadGeminiCall(JsonNode? part)
    {
        if (part?["functionCall"] is not JsonObject call)
        {
            return null;
        }

        var name = call["name"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new NativeToolCall(name, name, call["args"]?.DeepClone());
    }

    /// <summary>
    /// A schema for a tool that takes nothing. Providers reject a missing parameters object,
    /// so "no arguments" has to be spelled out rather than omitted.
    /// </summary>
    private static JsonObject EmptySchema()
        => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    /// <summary>
    /// Arguments arrive as a JSON string that providers sometimes leave malformed when a
    /// stream is cut short. Unparseable arguments are null rather than an exception — the
    /// caller reports a failed tool call and the model can try again.
    /// </summary>
    private static JsonNode? ParseArguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
