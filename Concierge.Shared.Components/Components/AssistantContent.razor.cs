// Code-behind for AssistantContent. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Concierge.Shared.Diagrams;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Concierge.Shared.Components.Components;

public partial class AssistantContent
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    [Parameter, EditorRequired] public string Content { get; set; } = string.Empty;

    private readonly List<Segment> _segments = new();

    /// <summary>
    /// Render assistant-authored Markdown as HTML with raw HTML disabled. Only
    /// ordinary web links are kept clickable; active-content URI schemes are
    /// removed before rendering.
    /// </summary>
    private static string RenderMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var document = Markdown.Parse(text, MarkdownPipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeLink(link.Url))
            {
                link.Url = null;
            }
        }

        return Markdown.ToHtml(document, MarkdownPipeline);
    }

    private static bool IsSafeLink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#'))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return !value.StartsWith("//", StringComparison.Ordinal)
                && !value.StartsWith('\\')
                && !value.StartsWith("\\\\", StringComparison.Ordinal);
        }

        return uri.Scheme is "http" or "https" or "mailto" or "tel";
    }

    protected override void OnParametersSet()
    {
        _segments.Clear();
        if (string.IsNullOrEmpty(Content))
        {
            return;
        }

        var blocks = MermaidDiagramRuntime.ExtractFencedBlocks(Content);
        if (blocks.Count == 0)
        {
            return;
        }

        var cursor = 0;
        foreach (var block in blocks)
        {
            if (block.Start > cursor)
            {
                var prefix = Content[cursor..block.Start].TrimEnd();
                if (prefix.Length > 0)
                {
                    _segments.Add(new Segment(prefix, IsMermaid: false));
                }
            }
            _segments.Add(new Segment(block.Source.Trim(), IsMermaid: true));
            cursor = block.Start + block.Length;
        }

        if (cursor < Content.Length)
        {
            var tail = Content[cursor..].TrimStart();
            if (tail.Length > 0)
            {
                _segments.Add(new Segment(tail, IsMermaid: false));
            }
        }
    }

    private sealed record Segment(string Text, bool IsMermaid);
}
