using System.Text;

namespace Concierge.Shared.Design;

/// <summary>
/// Numbers to glance at.
///
/// The job a board does is being read in two seconds from across a room, which is
/// a different job from a page even when the shapes look similar. So the number is
/// enormous, its name is small above it, and how it is moving is a word rather
/// than a chart.
///
/// **No chart, and that is the decision this renderer turns on.** A line going up
/// is the thing every dashboard reaches for and almost nobody reads — from across
/// a room it is a shape, and up close it needs axes, a scale and a legend before
/// it says anything. What a person actually asks is "is it up or down, and by how
/// much", which is two words and a number.
///
/// open-design has live dashboards among its six artifact types. What makes one
/// live is where the numbers come from, which is the agent's job and not this
/// file's: a panel holds whatever it was last told, and something else tells it.
/// </summary>
public static class BoardRenderer
{
    public static string ToHtml(DesignDocument document, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var html = new StringBuilder();
        DesignMediums.OpenDocument(html, DesignLooks.Of(document.Look), Style);

        var panels = DesignMediums.FramesOf(document);

        if (panels.Count == 0)
        {
            html.AppendLine("<p class=\"nothing pad\">Nothing to show yet. Say: add a panel.</p>");
            html.AppendLine("</body></html>");
            return html.ToString();
        }

        html.AppendLine("<main class=\"board\">");

        foreach (var panel in panels)
        {
            var picked = string.Equals(panel.Id, selectedId, StringComparison.Ordinal) ? " picked" : string.Empty;

            var value = Prop(panel, "value");
            var moving = Prop(panel, "change");
            var note = Prop(panel, "note");

            html.AppendLine($"<section class=\"panel{picked}\" data-node=\"{DesignMediums.Escape(panel.Id)}\">");

            html.AppendLine(
                $"<h2>{DesignMediums.Escape(panel.Text.Length > 0 ? panel.Text : "A panel")}</h2>");

            // The blank is the point when there is no number. An invented one is
            // the defect this whole repository argues against, and open-design says
            // the same: an invented metric is slop the moment it is invented.
            html.AppendLine(value.Length > 0
                ? $"<p class=\"value\">{DesignMediums.Escape(value)}</p>"
                : "<p class=\"value blank\">—</p>");

            if (moving.Length > 0)
            {
                var way = Way(moving);

                html.AppendLine(
                    $"<p class=\"change {way}\">{Arrow(way)} {DesignMediums.Escape(moving)}</p>");
            }

            if (note.Length > 0)
            {
                html.AppendLine($"<p class=\"note\">{DesignMediums.Escape(note)}</p>");
            }

            // Anything else somebody put on the panel, drawn the ordinary way.
            //
            // A number, a direction or a note counts as something already drawn: without
            // that, a panel reading 48,200 printed "Nothing on this panel yet" immediately
            // under the number.
            DesignMediums.WriteContents(
                html,
                document,
                panel,
                selectedId,
                alreadyDrawn: value.Length > 0 || moving.Length > 0 || note.Length > 0);

            html.AppendLine("</section>");
        }

        html.AppendLine("</main>");
        html.AppendLine("</body></html>");

        return html.ToString();
    }

    private static string Prop(DesignNode node, string key)
        => node.Props.TryGetValue(key, out var value) && value is not null ? value.Trim() : string.Empty;

    /// <summary>
    /// Which way a number is going, read from how it was written.
    ///
    /// A leading minus is down and everything else is up, which is right far more
    /// often than it is wrong and needs nobody to learn a convention. "steady" says
    /// so outright for the case where neither is true.
    /// </summary>
    private static string Way(string change)
    {
        var said = change.Trim();

        if (said.StartsWith('-') || said.StartsWith('↓')
            || said.Contains("down", StringComparison.OrdinalIgnoreCase))
        {
            return "down";
        }

        return said.Contains("steady", StringComparison.OrdinalIgnoreCase)
            || said.Contains("flat", StringComparison.OrdinalIgnoreCase)
                ? "steady"
                : "up";
    }

    /// <summary>
    /// An arrow, not a colour.
    ///
    /// Colour alone says nothing to somebody who cannot tell red from green, and
    /// a board is read at a glance by whoever is walking past. The colour is still
    /// there; it is not carrying the meaning on its own.
    /// </summary>
    private static string Arrow(string way) => way switch
    {
        "down" => "▼",
        "steady" => "▬",
        _ => "▲",
    };

    private const string Style = """
        body { overflow: auto; }

        .board {
          display: grid;
          grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
          gap: 1rem; padding: 1.25rem;
          align-content: start;
        }

        .panel {
          background: var(--raised);
          border-radius: var(--radius);
          padding: 1.1rem 1.25rem 1.25rem;
          min-height: 150px;
        }

        /* Small name, enormous number. The other way round is how a dashboard
           comes to be unreadable from more than an arm away. */
        .panel h2 {
          margin: 0 0 .35rem;
          font-size: .82rem; font-weight: 600;
          letter-spacing: .04em; text-transform: uppercase;
          opacity: .6;
        }

        .value {
          margin: 0;
          font-size: 2.9rem; font-weight: 700; line-height: 1.05;
          font-variant-numeric: tabular-nums;
        }

        /* A labelled blank rather than a plausible number. An invented metric is
           slop the moment it is invented. */
        .value.blank { opacity: .3; }

        .change {
          margin: .45rem 0 0;
          font-size: .95rem; font-weight: 600;
          font-variant-numeric: tabular-nums;
        }
        .change.up { color: var(--accent); }
        .change.down { opacity: .75; }
        .change.steady { opacity: .55; }

        .note { margin: .5rem 0 0; font-size: .8rem; opacity: .6; }
        .pad { padding: 2rem; }
        """;
}
