// Code-behind for AssistantContent. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Concierge.Shared.Diagrams;

namespace Concierge.Shared.Components.Components;

public partial class AssistantContent
{

    [Parameter, EditorRequired] public string Content { get; set; } = string.Empty;

    private readonly List<Segment> _segments = new();

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
