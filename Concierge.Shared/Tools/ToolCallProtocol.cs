using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Concierge.Shared.Tools;

/// <summary>
/// Parses + formats the host-neutral tool-call protocol the chat client wires into the
/// system prompt. The protocol uses fenced markdown blocks so any LLM can emit it without
/// provider-native function-calling support:
///
///   ```tool-call
///   {"name": "read_file", "arguments": {"path": "foo.cs"}}
///   ```
///
/// The host then replies with a matching block:
///
///   ```tool-result
///   {"name": "read_file", "success": true, "output": "...", "failure": null}
///   ```
///
/// Provider-native bindings (OpenAI function calling, Anthropic tool_use, Gemini function
/// declarations) can wrap this protocol in their own schema — the conversation persisted
/// to SQLite stays normalised either way.
/// </summary>
public static class ToolCallProtocol
{
    /// <summary>
    /// Non-backtracking regex that captures fenced <c>```tool-call</c> blocks. Tolerates a
    /// leading language tag with optional whitespace and any body shape — JSON validity is
    /// checked downstream so a malformed payload reports as a tool error rather than
    /// crashing the parser.
    /// </summary>
    private static readonly Regex CallBlockRegex = new(
        @"```tool-call\b\s*(?<body>[\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    /// <summary>One parsed call lifted from assistant text.</summary>
    public sealed record ToolCallInvocation(int Start, int Length, string Name, JsonNode? Arguments);
    public sealed record ToolCallInspection(IReadOnlyList<ToolCallInvocation> Calls, string? Problem);

    /// <summary>
    /// Extracts every <c>tool-call</c> block in <paramref name="text"/>. Malformed JSON
    /// payloads are skipped silently so a noisy reply doesn't break the loop — the
    /// downstream invoker would have rejected them anyway.
    /// </summary>
    public static IReadOnlyList<ToolCallInvocation> Extract(string text) => Inspect(text).Calls;

    /// <summary>Also reports rejected or unfinished requests so a turn cannot silently pretend they ran.</summary>
    public static ToolCallInspection Inspect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new([], null);
        }

        var calls = new List<ToolCallInvocation>();
        var rejected = 0;
        var matched = 0;
        try
        {
            foreach (Match match in CallBlockRegex.Matches(text))
            {
                matched++;
                var body = match.Groups["body"].Value.Trim();
                if (body.Length == 0)
                {
                    rejected++;
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(body);
                }
                catch (JsonException)
                {
                    rejected++;
                    continue;
                }

                if (node is not JsonObject obj || obj["name"] is not JsonValue nameValue
                    || !nameValue.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name)
                    || obj["arguments"] is not (null or JsonObject))
                {
                    rejected++;
                    continue;
                }

                var arguments = node["arguments"];
                calls.Add(new ToolCallInvocation(match.Index, match.Length, name, arguments));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new([], "The tool request could not be parsed within the time limit.");
        }
        var openings = text.Split("```tool-call", StringSplitOptions.None).Length - 1;
        return new(calls, rejected > 0 || openings > matched
            ? "Circle AI returned an incomplete or invalid tool request. No tools from this response were run." : null);
    }

    /// <summary>
    /// Renders a tool-result fenced block matching the call. The chat client appends one
    /// of these to the conversation as a synthetic user turn so the LLM sees the result
    /// inline on the next stream.
    /// </summary>
    public static string FormatResult(string toolName, AgentToolResult result)
    {
        var payload = new JsonObject
        {
            ["name"] = toolName,
            ["success"] = result.Success,
            ["output"] = result.Output,
            ["failure"] = result.FailureMessage,
        };
        var sb = new StringBuilder();
        sb.AppendLine("```tool-result");
        sb.AppendLine(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>
    /// Concatenates one or more rendered tool-result blocks with a clarifying lead-in for
    /// the model. This is the body the chat client posts as the synthetic user turn that
    /// follows an assistant message containing tool calls.
    /// </summary>
    public static string FormatToolResultMessage(IEnumerable<(string ToolName, AgentToolResult Result)> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tool results follow. Continue the response using these.");
        sb.AppendLine();
        foreach (var (toolName, result) in results)
        {
            sb.Append(FormatResult(toolName, result));
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
