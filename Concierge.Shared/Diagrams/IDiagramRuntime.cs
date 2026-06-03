namespace Concierge.Shared.Diagrams;

/// <summary>
/// Host-neutral diagram surface — mirrors <see cref="Chat.IChatRuntime"/> for diagram tooling.
/// Implementations are pluggable: <c>MermaidDiagramRuntime</c> renders text-DSL diagrams in
/// the browser; <c>PenPotDiagramRuntime</c> reads and writes <c>.penpot</c> files; future
/// adapters can wrap Excalidraw, draw.io, PlantUML, etc.
/// </summary>
/// <remarks>
/// The runtime is intentionally narrow — it produces and consumes <see cref="DiagramArtifact"/>
/// values. UI components decide how to render them (inline SVG, embedded iframe, file list).
/// </remarks>
public interface IDiagramRuntime
{
    /// <summary>
    /// Short identifier used by the UI to route artifacts to the right runtime
    /// (e.g. <c>"mermaid"</c>, <c>"penpot"</c>, <c>"figma"</c>).
    /// </summary>
    string Id { get; }

    /// <summary>Human-readable label for settings UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// File extensions the runtime can read (lowercase, no dot — e.g. <c>"penpot"</c>,
    /// <c>"fig"</c>). Empty for text-DSL runtimes that only consume in-line source.
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Imports a diagram file from a stream. Implementations decide how much structure
    /// to surface; at minimum the returned artifact carries a parsed summary so the
    /// LLM has something to reason about.
    /// </summary>
    Task<DiagramArtifact> ImportAsync(string fileName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Materialises an artifact from a text DSL snippet (e.g. a fenced
    /// <c>```mermaid</c> block lifted from an assistant reply). Returns <c>null</c>
    /// if the runtime cannot handle the given source.
    /// </summary>
    DiagramArtifact? FromSource(string source);
}

/// <summary>
/// Parsed representation of a diagram. Carries the original source / payload so the UI
/// can re-render, plus a structured summary the LLM can read.
/// </summary>
/// <param name="RuntimeId">
/// Id of the runtime that produced this artifact — used to route back to the right renderer.
/// </param>
/// <param name="Kind">
/// Human-readable category — <c>"flowchart"</c>, <c>"sequence"</c>, <c>"ui-mockup"</c>,
/// <c>"design-system"</c>, <c>"unknown"</c>. Surfaces in the agent's tool-call context so
/// the model can decide whether the diagram is the right answer to a user's question.
/// </param>
/// <param name="Title">Best-effort title lifted from the source (frame name, first heading).</param>
/// <param name="Source">
/// Verbatim source for text-DSL diagrams (Mermaid, PlantUML). For binary file imports this
/// is the JSON serialisation of the structured tree.
/// </param>
/// <param name="Summary">
/// One-paragraph natural-language description of what the diagram shows. Fed to the LLM as
/// tool-call context so it can answer "what does this design do?" without rendering pixels.
/// </param>
/// <param name="Elements">Optional structured element list — names, types, parent/child links.</param>
public sealed record DiagramArtifact(
    string RuntimeId,
    string Kind,
    string Title,
    string Source,
    string Summary,
    IReadOnlyList<DiagramElement> Elements);

/// <summary>
/// Structured node lifted from an imported design file — used by the agent to reason about
/// composition without seeing pixels. Hierarchical via <see cref="ParentId"/>.
/// </summary>
public sealed record DiagramElement(
    string Id,
    string? ParentId,
    string ElementType,
    string Name,
    IReadOnlyDictionary<string, string> Metadata);
