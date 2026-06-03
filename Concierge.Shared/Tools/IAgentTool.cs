using System.Text.Json.Nodes;

namespace Concierge.Shared.Tools;

/// <summary>
/// Host-neutral tool a chat runtime can offer to the LLM. Each tool has a stable name
/// (used by the model when emitting a call), a one-line description, an optional JSON Schema
/// describing argument shape, and an <c>InvokeAsync</c> that executes the call.
/// </summary>
/// <remarks>
/// The interface is deliberately provider-agnostic. Adapters that wrap OpenAI's function
/// calling, Anthropic's tool use, Gemini's function declarations, or CircleAI's IToolBridge
/// all translate from this shape into the provider's wire format; the agent harness
/// implementations of this interface (file read, file write, shell run, list, grep) need
/// to be written once.
/// </remarks>
public interface IAgentTool
{
    /// <summary>Stable identifier. Use kebab-case or snake_case — never spaces.</summary>
    string Name { get; }

    /// <summary>One-line plain-English description for the model.</summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema describing the expected argument object. Returning <c>null</c> means
    /// "no arguments" — Boolean / void tools.
    /// </summary>
    JsonNode? ArgumentsSchema { get; }

    /// <summary>
    /// <c>true</c> if the tool only reads state. Read-only tools are eligible for unattended
    /// execution; destructive tools require approval before they run.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>Executes the tool with the given JSON arguments and returns the result text.</summary>
    Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of an <see cref="IAgentTool"/> call.</summary>
public sealed record AgentToolResult(bool Success, string Output, string? FailureMessage = null);

/// <summary>
/// Registry of every available tool. Hosts populate it via DI (AgentHarness adapter is the
/// default source; future bindings — MCP, custom tools — register more). Chat runtimes pull
/// the catalog when composing requests so the model knows what it can call.
/// </summary>
public interface IAgentToolRegistry
{
    IReadOnlyList<IAgentTool> Tools { get; }

    /// <summary>
    /// Returns the catalog as a single text block suitable for injecting at the top of a
    /// system prompt. Each tool is rendered on its own line as
    /// <c>- name(schema): description</c> so the model can spot the call shape without
    /// reading JSON Schema fully.
    /// </summary>
    string BuildSystemPromptAddendum();
}
