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
        @"```tool-call\s*\n(?<body>[\s\S]*?)\n```",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    /// <summary>One parsed call lifted from assistant text.</summary>
    public sealed record ToolCallInvocation(int Start, int Length, string Name, JsonNode? Arguments);

    /// <summary>
    /// Extracts every <c>tool-call</c> block in <paramref name="text"/>. Malformed JSON
    /// payloads are skipped silently so a noisy reply doesn't break the loop — the
    /// downstream invoker would have rejected them anyway.
    /// </summary>
    public static IReadOnlyList<ToolCallInvocation> Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<ToolCallInvocation>();
        }

        var calls = new List<ToolCallInvocation>();
        try
        {
            foreach (Match match in CallBlockRegex.Matches(text))
            {
                var body = match.Groups["body"].Value.Trim();
                if (body.Length == 0)
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(body);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (node is null)
                {
                    continue;
                }

                var name = node["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var arguments = node["arguments"];
                calls.Add(new ToolCallInvocation(match.Index, match.Length, name, arguments));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input — fail closed (no calls extracted) so the chat continues
            // without the tool-loop. The user can always retry with a smaller request.
        }

        return calls;
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
