using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Concierge.Shared.Notebooks;

/// <summary>What an edit does to the cell it names.</summary>
public enum NotebookEditKind
{
    /// <summary>Replace the cell's source.</summary>
    Replace,

    /// <summary>Put a new cell before the named one.</summary>
    Insert,

    /// <summary>Remove the named cell.</summary>
    Delete,
}

/// <summary>The outcome of an edit: the new document, or why there isn't one.</summary>
public sealed record NotebookEditResult(bool Success, string? Json, string? Problem);

/// <summary>
/// Reading and changing a Jupyter notebook without destroying it.
///
/// A notebook is JSON, so read_file and write_file already reach one — which
/// is the problem. Reading a notebook that way spends most of a context window
/// on base64 PNGs and execution metadata to show a dozen lines of Python, and
/// writing one that way means a model regenerating the whole document from
/// memory to change one cell. The second is the dangerous half: outputs,
/// execution counts, kernel spec and every untouched cell all get rewritten
/// from what the model remembers, and a notebook that comes back subtly
/// different in ways nobody asked about is worse than one that was never
/// editable at all.
///
/// So: read renders cells rather than JSON, and edit is surgical — the parsed
/// document is modified in place and re-serialised, so anything this code does
/// not name survives untouched.
///
/// Deliberately format-only. Nothing here runs anything, there is no kernel,
/// and executing a notebook stays where it already is: run_command, which asks
/// first.
/// </summary>
public static class Notebook
{
    /// <summary>
    /// How much of one output to show. Enough to see what a cell printed or
    /// what it raised, and not enough for an embedded image to take the
    /// context window with it.
    /// </summary>
    public const int MaxOutputChars = 2000;

    /// <summary>
    /// Renders a notebook as something worth reading.
    ///
    /// Cells are numbered from 0, and the number is the one an edit takes, so
    /// what you read and what you change agree.
    /// </summary>
    public static string Render(string json)
    {
        var document = Parse(json, out var problem);
        if (document is null)
        {
            return problem!;
        }

        var cells = document["cells"] as JsonArray ?? [];
        var text = new StringBuilder();

        var language = document["metadata"]?["kernelspec"]?["language"]?.GetValue<string>()
                       ?? document["metadata"]?["language_info"]?["name"]?.GetValue<string>();

        text.Append(cells.Count == 1 ? "1 cell" : $"{cells.Count} cells");
        text.AppendLine(language is null ? string.Empty : $", {language}");

        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var kind = cell?["cell_type"]?.GetValue<string>() ?? "unknown";

            text.AppendLine();
            text.AppendLine($"[{i}] {kind}");

            var source = SourceOf(cell);
            if (source.Length > 0)
            {
                text.AppendLine(source);
            }

            var output = OutputOf(cell);
            if (output.Length > 0)
            {
                text.AppendLine("  --- output ---");
                text.AppendLine(output);
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Changes one cell and leaves everything else exactly where it was.
    ///
    /// The whole point: the model supplies the cell, not the file. Nothing it
    /// did not name is regenerated, so outputs, execution counts and the
    /// kernel spec survive an edit intact.
    /// </summary>
    public static NotebookEditResult Edit(
        string json, int index, NotebookEditKind kind, string? source, string cellType = "code")
    {
        var document = Parse(json, out var problem);
        if (document is null)
        {
            return new NotebookEditResult(false, null, problem);
        }

        if (document["cells"] is not JsonArray cells)
        {
            return new NotebookEditResult(false, null, "This file has no cells, so it is not a notebook.");
        }

        // Insert may name the position just past the end — that is appending.
        var limit = kind == NotebookEditKind.Insert ? cells.Count : cells.Count - 1;
        if (index < 0 || index > limit)
        {
            return new NotebookEditResult(false, null,
                cells.Count == 0
                    ? "This notebook has no cells."
                    : $"There is no cell {index}. The notebook has {cells.Count}, numbered 0 to {cells.Count - 1}.");
        }

        switch (kind)
        {
            case NotebookEditKind.Delete:
                cells.RemoveAt(index);
                break;

            case NotebookEditKind.Insert:
                if (source is null)
                {
                    return new NotebookEditResult(false, null, "A new cell needs its source.");
                }

                cells.Insert(index, NewCell(cellType, source));
                break;

            case NotebookEditKind.Replace:
                if (source is null)
                {
                    return new NotebookEditResult(false, null, "A replacement needs its source.");
                }

                if (cells[index] is not JsonObject cell)
                {
                    return new NotebookEditResult(false, null, $"Cell {index} is not readable.");
                }

                cell["source"] = Lines(source);

                // A changed cell has not been run. Leaving old outputs beside
                // new code is how a notebook starts lying about itself.
                if (cell["cell_type"]?.GetValue<string>() == "code")
                {
                    cell["outputs"] = new JsonArray();
                    cell["execution_count"] = null;
                }

                break;
        }

        return new NotebookEditResult(true, document.ToJsonString(Indented), null);
    }

    // ── The parts ─────────────────────────────────────────────────────────

    /// <summary>
    /// Indented, and not escaped beyond what JSON requires.
    ///
    /// The default encoder escapes anything that could matter in an HTML
    /// document, so every apostrophe in a line of Python comes back as a
    /// numeric escape, and every accented character in a markdown cell goes
    /// the same way — a two-line cell rewritten unrecognisably. It is
    /// still valid JSON and Jupyter still opens it — and the file has been
    /// rewritten in a way nobody asked for, on a line the edit did not touch,
    /// which is exactly the churn this tool exists to avoid. A notebook on
    /// disk is not an HTML document.
    /// </summary>
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonObject? Parse(string json, out string? problem)
    {
        problem = null;

        try
        {
            if (JsonNode.Parse(json) is JsonObject document && document["cells"] is JsonArray)
            {
                return document;
            }

            problem = "This is valid JSON but not a notebook — it has no cells.";
            return null;
        }
        catch (JsonException)
        {
            problem = "This file is not valid JSON, so it cannot be read as a notebook.";
            return null;
        }
    }

    /// <summary>
    /// Source is a list of lines with their newlines kept, or occasionally one
    /// string. Both are in the wild and both are the format.
    /// </summary>
    internal static string SourceOf(JsonNode? cell) => Flatten(cell?["source"]);

    private static string Flatten(JsonNode? node) => node switch
    {
        JsonArray lines => string.Concat(lines.Select(l => l?.GetValue<string>() ?? string.Empty)).TrimEnd(),
        JsonValue value when value.TryGetValue<string>(out var text) => text.TrimEnd(),
        _ => string.Empty,
    };

    /// <summary>
    /// What a cell printed, raised, or returned — as text.
    ///
    /// Images are named rather than included. A notebook with six plots in it
    /// is six base64 PNGs, and a text model cannot see them from here anyway;
    /// a line saying an image is there is the whole of what is useful.
    /// </summary>
    internal static string OutputOf(JsonNode? cell)
    {
        if (cell?["outputs"] is not JsonArray outputs || outputs.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (var output in outputs)
        {
            var kind = output?["output_type"]?.GetValue<string>();

            if (kind == "error")
            {
                var name = output?["ename"]?.GetValue<string>() ?? "Error";
                var message = output?["evalue"]?.GetValue<string>() ?? string.Empty;
                text.AppendLine($"{name}: {message}".TrimEnd());
                continue;
            }

            if (kind == "stream")
            {
                text.AppendLine(Flatten(output?["text"]));
                continue;
            }

            if (output?["data"] is not JsonObject data)
            {
                continue;
            }

            if (data["text/plain"] is { } plain)
            {
                text.AppendLine(Flatten(plain));
            }

            foreach (var entry in data)
            {
                if (entry.Key.StartsWith("image/", StringComparison.Ordinal))
                {
                    text.AppendLine($"[{entry.Key}]");
                }
            }
        }

        var rendered = text.ToString().TrimEnd();

        return rendered.Length <= MaxOutputChars
            ? rendered
            : rendered[..MaxOutputChars] + $"\n… output truncated at {MaxOutputChars} characters";
    }

    /// <summary>
    /// A new cell, with the fields nbformat requires and nothing more. A code
    /// cell needs outputs and an execution count even when it has never run.
    /// </summary>
    private static JsonObject NewCell(string cellType, string source)
    {
        var kind = cellType == "markdown" ? "markdown" : "code";

        var cell = new JsonObject
        {
            ["cell_type"] = kind,
            ["metadata"] = new JsonObject(),
            ["source"] = Lines(source),
        };

        if (kind == "code")
        {
            cell["execution_count"] = null;
            cell["outputs"] = new JsonArray();
        }

        return cell;
    }

    /// <summary>
    /// Back into the list-of-lines-with-newlines shape, because a notebook
    /// written as one long string diffs as one long line, and every tool that
    /// reads notebooks in version control gets worse.
    /// </summary>
    internal static JsonArray Lines(string source)
    {
        var array = new JsonArray();

        if (source.Length == 0)
        {
            return array;
        }

        var lines = source.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            array.Add(i == lines.Length - 1 ? lines[i] : lines[i] + "\n");
        }

        return array;
    }
}
