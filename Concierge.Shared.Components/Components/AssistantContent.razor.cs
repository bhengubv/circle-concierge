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
using System.Text.RegularExpressions;

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

        var document = Markdown.Parse(NormalizeSimpleMath(text), MarkdownPipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeLink(link.Url))
            {
                link.Url = null;
            }
        }

        return Markdown.ToHtml(document, MarkdownPipeline);
    }

    private static readonly Regex InlineMath = new(@"(?<slash>\\{1,2})\((?<body>[^()\r\n]{1,100})\k<slash>\)", RegexOptions.Compiled);
    private static readonly Regex SimpleMathCommand = new(@"\\{1,2}(?<command>\$|dots|times|div)", RegexOptions.Compiled);
    private static readonly Regex PlainMath = new(@"^[\p{L}\p{N}\s$.,+\-*/=%×÷…]+$", RegexOptions.Compiled);

    /// <summary>
    /// Circle AI sometimes sends simple currency and arithmetic in LaTeX's \(...\)
    /// delimiters. The current Markdown viewer has no math typesetter, so show
    /// those simple expressions as readable text. Leave complex math and code
    /// untouched rather than pretending to render them correctly.
    /// </summary>
    private static string NormalizeSimpleMath(string markdown)
    {
        var lines = markdown.Split('\n');
        var fence = '\0';
        var fenceLength = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart(' ');
            var indent = line.Length - trimmed.Length;
            if (indent <= 3 && trimmed.Length >= 3 && trimmed[0] is '`' or '~')
            {
                var marker = trimmed[0];
                var count = 0;
                while (count < trimmed.Length && trimmed[count] == marker) count++;
                if (count >= 3 && (fence == '\0' || fence == marker && count >= fenceLength))
                {
                    if (fence == '\0') { fence = marker; fenceLength = count; }
                    else { fence = '\0'; fenceLength = 0; }
                    continue;
                }
            }
            if (fence != '\0') continue;
            lines[index] = InlineMath.Replace(line, match =>
            {
                // A backtick span is an example, not display math.
                if (line[..match.Index].Count(character => character == '`') % 2 != 0) return match.Value;
                var plain = SimpleMathCommand.Replace(match.Groups["body"].Value, command =>
                    command.Groups["command"].Value switch
                    {
                        "$" => "$",
                        "dots" => "…",
                        "times" => "×",
                        "div" => "÷",
                        _ => command.Value
                    });
                return PlainMath.IsMatch(plain) ? plain : match.Value;
            });
        }
        return string.Join("\n", lines);
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
