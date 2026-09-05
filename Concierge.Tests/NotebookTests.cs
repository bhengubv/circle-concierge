using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Notebooks;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Notebooks.
///
/// The last thing on the not-scheduled list, and the one with the clearest
/// reason to exist: read_file and write_file already open a .ipynb, so the
/// gap was never access. It was that both do the wrong thing with one.
/// Reading spends the context window on base64 PNGs to show a dozen lines of
/// Python; writing means the model reproducing the entire document from
/// memory, which is where recorded outputs go to die.
///
/// What is tested is the format handling, because that is where a notebook
/// gets quietly corrupted. The workspace boundary and the approval are the
/// ones read_file and write_file already have — these tools go through the
/// same harness rather than writing a second set that could disagree.
/// </summary>
public sealed class NotebookTests
{
    private const string Small = """
        {
          "cells": [
            { "cell_type": "markdown", "metadata": {}, "source": ["# Title\n", "Some prose.\n"] },
            { "cell_type": "code", "metadata": {}, "execution_count": 3,
              "source": ["print('hi')\n"],
              "outputs": [ { "output_type": "stream", "name": "stdout", "text": ["hi\n"] } ] }
          ],
          "metadata": { "kernelspec": { "language": "python", "name": "python3" } },
          "nbformat": 4, "nbformat_minor": 5
        }
        """;

    private static JsonNode Cells(string json) => JsonNode.Parse(json)!["cells"]!;

    // ── Reading ───────────────────────────────────────────────────────────

    [Fact]
    public void A_notebook_reads_as_numbered_cells()
    {
        var text = Notebook.Render(Small);

        Assert.Contains("2 cells, python", text);
        Assert.Contains("[0] markdown", text);
        Assert.Contains("[1] code", text);
        Assert.Contains("print('hi')", text);
    }

    /// <summary>
    /// The numbers are the contract between the two tools: what you read is
    /// what you edit. A rendering that numbered differently would make every
    /// edit a coin flip.
    /// </summary>
    [Fact]
    public void What_a_cell_printed_is_shown_with_it()
        => Assert.Contains("--- output ---", Notebook.Render(Small));

    /// <summary>
    /// An error is the most useful output in the file — it is usually the
    /// reason somebody is reading the notebook at all.
    /// </summary>
    [Fact]
    public void An_error_output_keeps_its_name_and_message()
    {
        var text = Notebook.Render("""
            { "cells": [ { "cell_type": "code", "source": ["1/0"],
              "outputs": [ { "output_type": "error", "ename": "ZeroDivisionError",
                             "evalue": "division by zero", "traceback": ["..."] } ] } ] }
            """);

        Assert.Contains("ZeroDivisionError: division by zero", text);
    }

    /// <summary>
    /// Images are named, not included. Six plots is six base64 PNGs, a text
    /// model cannot see them from here, and a line saying one is there is the
    /// whole of what is useful.
    /// </summary>
    [Fact]
    public void An_image_is_named_rather_than_pasted()
    {
        var text = Notebook.Render("""
            { "cells": [ { "cell_type": "code", "source": ["plot()"],
              "outputs": [ { "output_type": "display_data",
                             "data": { "image/png": "iVBORw0KGgoAAAANSUhEUg" },
                             "metadata": {} } ] } ] }
            """);

        Assert.Contains("[image/png]", text);
        Assert.DoesNotContain("iVBORw0KGgo", text);
    }

    [Fact]
    public void A_very_long_output_is_cut_and_says_so()
    {
        var flood = string.Join(string.Empty, Enumerable.Repeat("noise ", 2000));

        var text = Notebook.Render($$"""
            { "cells": [ { "cell_type": "code", "source": ["loud()"],
              "outputs": [ { "output_type": "stream", "text": ["{{flood}}"] } ] } ] }
            """);

        Assert.Contains("output truncated", text);
        Assert.True(text.Length < flood.Length);
    }

    /// <summary>Both shapes are in the wild and both are the format.</summary>
    [Fact]
    public void Source_written_as_one_string_reads_the_same_as_a_list()
    {
        var text = Notebook.Render("""
            { "cells": [ { "cell_type": "code", "source": "x = 1\ny = 2" } ] }
            """);

        Assert.Contains("x = 1", text);
        Assert.Contains("y = 2", text);
    }

    [Fact]
    public void An_empty_notebook_says_so_rather_than_nothing()
        => Assert.Contains("0 cells", Notebook.Render("""{ "cells": [] }"""));

    [Fact]
    public void Something_that_is_not_a_notebook_is_named_as_such()
    {
        Assert.Contains("not valid JSON", Notebook.Render("def f(): pass"));
        Assert.Contains("no cells", Notebook.Render("""{ "nbformat": 4 }"""));
    }

    // ── Editing ───────────────────────────────────────────────────────────

    [Fact]
    public void Replacing_a_cell_changes_that_cell()
    {
        var result = Notebook.Edit(Small, 1, NotebookEditKind.Replace, "print('bye')\n");

        Assert.True(result.Success);
        Assert.Contains("print('bye')", Notebook.Render(result.Json!));
        Assert.DoesNotContain("print('hi')", Notebook.Render(result.Json!));
    }

    /// <summary>
    /// The reason this tool exists rather than write_file. Everything the edit
    /// did not name has to still be there afterwards — including the parts a
    /// model reproducing the file from memory would quietly get wrong.
    /// </summary>
    [Fact]
    public void Everything_the_edit_did_not_name_survives()
    {
        var result = Notebook.Edit(Small, 1, NotebookEditKind.Replace, "print('bye')\n");
        var after = JsonNode.Parse(result.Json!)!;

        Assert.Equal("python", after["metadata"]!["kernelspec"]!["language"]!.GetValue<string>());
        Assert.Equal(4, after["nbformat"]!.GetValue<int>());
        Assert.Contains("# Title", Notebook.SourceOf(after["cells"]![0]));
    }

    /// <summary>
    /// A changed cell has not been run. Leaving the old outputs beside new
    /// code is how a notebook starts lying about itself.
    /// </summary>
    [Fact]
    public void Changed_code_loses_the_outputs_it_did_not_produce()
    {
        var result = Notebook.Edit(Small, 1, NotebookEditKind.Replace, "print('bye')\n");
        var cell = Cells(result.Json!)[1]!;

        Assert.Empty((JsonArray)cell["outputs"]!);
        Assert.Null(cell["execution_count"]);
    }

    /// <summary>
    /// And markdown has none to lose, so nothing is invented for it — a
    /// markdown cell carrying an empty outputs array is not the format.
    /// </summary>
    [Fact]
    public void Markdown_gains_no_outputs_field()
    {
        var result = Notebook.Edit(Small, 0, NotebookEditKind.Replace, "# Other\n");

        Assert.Null(Cells(result.Json!)[0]!["outputs"]);
    }

    [Fact]
    public void Inserting_puts_the_cell_before_the_one_named()
    {
        var result = Notebook.Edit(Small, 0, NotebookEditKind.Insert, "import os\n");

        var cells = (JsonArray)Cells(result.Json!);
        Assert.Equal(3, cells.Count);
        Assert.Contains("import os", Notebook.SourceOf(cells[0]));
        Assert.Equal("markdown", cells[1]!["cell_type"]!.GetValue<string>());
    }

    /// <summary>Appending is inserting at the end, and must be reachable.</summary>
    [Fact]
    public void A_cell_can_be_added_at_the_end()
    {
        var result = Notebook.Edit(Small, 2, NotebookEditKind.Insert, "done()\n");

        Assert.True(result.Success);
        Assert.Equal(3, ((JsonArray)Cells(result.Json!)).Count);
    }

    [Fact]
    public void A_new_code_cell_has_the_fields_the_format_requires()
    {
        var result = Notebook.Edit(Small, 0, NotebookEditKind.Insert, "x = 1\n");
        var cell = Cells(result.Json!)[0]!;

        Assert.Equal("code", cell["cell_type"]!.GetValue<string>());
        Assert.NotNull(cell["metadata"]);
        Assert.NotNull(cell["outputs"]);
        Assert.True(cell.AsObject().ContainsKey("execution_count"));
    }

    [Fact]
    public void A_new_markdown_cell_is_markdown()
    {
        var result = Notebook.Edit(Small, 0, NotebookEditKind.Insert, "# New\n", cellType: "markdown");

        Assert.Equal("markdown", Cells(result.Json!)[0]!["cell_type"]!.GetValue<string>());
    }

    [Fact]
    public void Deleting_removes_one_cell_and_leaves_the_rest()
    {
        var result = Notebook.Edit(Small, 0, NotebookEditKind.Delete, null);

        var cells = (JsonArray)Cells(result.Json!);
        Assert.Single(cells);
        Assert.Equal("code", cells[0]!["cell_type"]!.GetValue<string>());
    }

    /// <summary>
    /// Off the end is a mistake worth naming precisely — a model that gets
    /// told how many cells there are can correct itself on the next call.
    /// </summary>
    [Fact]
    public void An_index_off_the_end_says_how_many_there_are()
    {
        var result = Notebook.Edit(Small, 9, NotebookEditKind.Replace, "x");

        Assert.False(result.Success);
        Assert.Contains("numbered 0 to 1", result.Problem!);
    }

    [Fact]
    public void A_negative_index_is_refused()
        => Assert.False(Notebook.Edit(Small, -1, NotebookEditKind.Replace, "x").Success);

    [Fact]
    public void Insert_past_the_end_is_still_refused()
        => Assert.False(Notebook.Edit(Small, 5, NotebookEditKind.Insert, "x").Success);

    [Fact]
    public void An_edit_with_no_source_is_refused_rather_than_writing_nothing()
    {
        Assert.False(Notebook.Edit(Small, 0, NotebookEditKind.Replace, null).Success);
        Assert.False(Notebook.Edit(Small, 0, NotebookEditKind.Insert, null).Success);
    }

    [Fact]
    public void Editing_something_that_is_not_a_notebook_is_refused_intact()
    {
        var result = Notebook.Edit("not json at all", 0, NotebookEditKind.Replace, "x");

        Assert.False(result.Success);
        Assert.Null(result.Json);
        Assert.Contains("not valid JSON", result.Problem!);
    }

    // ── The shape it is written back in ───────────────────────────────────

    /// <summary>
    /// Lines keep their newlines, because a notebook written as one long
    /// string diffs as one long line and every tool that reads notebooks in
    /// version control gets worse.
    /// </summary>
    [Fact]
    public void Source_is_written_back_as_lines_that_keep_their_newlines()
    {
        var lines = Notebook.Lines("a\nb\nc");

        Assert.Equal(3, lines.Count);
        Assert.Equal("a\n", lines[0]!.GetValue<string>());
        Assert.Equal("c", lines[2]!.GetValue<string>());
    }

    [Fact]
    public void Windows_line_endings_do_not_survive_into_the_file()
    {
        var lines = Notebook.Lines("a\r\nb");

        Assert.Equal(2, lines.Count);
        Assert.Equal("a\n", lines[0]!.GetValue<string>());
    }

    [Fact]
    public void An_empty_cell_is_an_empty_list_rather_than_a_blank_line()
        => Assert.Empty(Notebook.Lines(string.Empty));

    /// <summary>
    /// Code comes back looking like code.
    ///
    /// System.Text.Json escapes anything that could matter inside an HTML
    /// document by default, so the first version of this wrote every
    /// apostrophe in a line of Python as a numeric escape and every accented
    /// character in a markdown cell along with it. Valid JSON, opens in
    /// Jupyter, and rewrites lines the edit never touched — which is the exact
    /// churn this tool exists to avoid.
    /// </summary>
    [Fact]
    public void An_edited_notebook_is_not_rewritten_as_escapes()
    {
        var result = Notebook.Edit(Small, 1, NotebookEditKind.Replace, "print('café')\n");

        Assert.Contains("print('café')", result.Json!);
        Assert.DoesNotContain("\\u00", result.Json!);
    }

    /// <summary>
    /// The round trip. An edit followed by a read has to agree with itself,
    /// or the two tools are describing different documents.
    /// </summary>
    [Fact]
    public void An_edited_notebook_still_reads()
    {
        var once = Notebook.Edit(Small, 1, NotebookEditKind.Replace, "print('bye')\n");
        var twice = Notebook.Edit(once.Json!, 0, NotebookEditKind.Delete, null);

        var text = Notebook.Render(twice.Json!);

        Assert.Contains("1 cell", text);
        Assert.Contains("print('bye')", text);
    }

    // ── The tools, on a real file ─────────────────────────────────────────

    /// <summary>
    /// The format handling above is most of the risk and none of the wiring.
    /// These go through the same harness that read_file and write_file use,
    /// against a real notebook on disk.
    /// </summary>
    private static (string Root, string File) AWorkspaceWithANotebook()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "concierge-notebook-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Concierge.slnx"), string.Empty);
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "work.ipynb"), Small);

        return (root, System.IO.Path.Combine(root, "work.ipynb"));
    }

    private static JsonNode Args(string json) => JsonNode.Parse(json)!;

    [Fact]
    public async Task Reading_a_notebook_off_disk_gives_cells_not_json()
    {
        var (root, _) = AWorkspaceWithANotebook();
        var tool = new NotebookReadTool(new AgentHarnessService(root));

        var result = await tool.InvokeAsync(Args("{ \"path\": \"work.ipynb\" }"));

        Assert.True(result.Success);
        Assert.Contains("[0] markdown", result.Output);
        Assert.DoesNotContain("nbformat", result.Output);
    }

    [Fact]
    public async Task An_approved_edit_reaches_the_file()
    {
        var (root, file) = AWorkspaceWithANotebook();
        var tool = new NotebookEditTool(new AgentHarnessService(root), new Always(ToolApprovalDecision.Allowed));

        var result = await tool.InvokeAsync(Args(
            "{ \"path\": \"work.ipynb\", \"cell\": 1, \"edit\": \"replace\", \"source\": \"print('bye')\\n\" }"));

        Assert.True(result.Success);
        Assert.Contains("print('bye')", System.IO.File.ReadAllText(file));
    }

    /// <summary>
    /// Written like every other write: refused means the file is untouched,
    /// not partially updated.
    /// </summary>
    [Fact]
    public async Task A_refused_edit_leaves_the_file_alone()
    {
        var (root, file) = AWorkspaceWithANotebook();
        var tool = new NotebookEditTool(new AgentHarnessService(root), new Always(ToolApprovalDecision.Denied));

        var result = await tool.InvokeAsync(Args(
            "{ \"path\": \"work.ipynb\", \"cell\": 1, \"edit\": \"replace\", \"source\": \"print('bye')\\n\" }"));

        Assert.False(result.Success);
        Assert.Equal(Small, System.IO.File.ReadAllText(file));
    }

    /// <summary>
    /// An edit that cannot happen must not interrupt anybody. Asking someone
    /// to approve a write to cell 9 of a two-cell notebook spends the only
    /// thing an approval prompt has: their attention.
    /// </summary>
    [Fact]
    public async Task An_impossible_edit_never_reaches_a_person()
    {
        var (root, _) = AWorkspaceWithANotebook();
        var approver = new Always(ToolApprovalDecision.Allowed);
        var tool = new NotebookEditTool(new AgentHarnessService(root), approver);

        var result = await tool.InvokeAsync(Args(
            "{ \"path\": \"work.ipynb\", \"cell\": 9, \"edit\": \"replace\", \"source\": \"x\" }"));

        Assert.False(result.Success);
        Assert.False(approver.WasAsked);
    }

    /// <summary>
    /// A model asked for an integer sometimes sends "1". Refusing that buys
    /// nothing — the alternative is a failed turn over a pair of quotes.
    /// </summary>
    [Fact]
    public async Task A_cell_number_sent_as_text_is_still_understood()
    {
        var (root, file) = AWorkspaceWithANotebook();
        var tool = new NotebookEditTool(new AgentHarnessService(root), new Always(ToolApprovalDecision.Allowed));

        var result = await tool.InvokeAsync(Args(
            "{ \"path\": \"work.ipynb\", \"cell\": \"1\", \"edit\": \"replace\", \"source\": \"print('bye')\\n\" }"));

        Assert.True(result.Success);
        Assert.Contains("print('bye')", System.IO.File.ReadAllText(file));
    }

    [Fact]
    public async Task An_edit_nobody_named_is_refused()
    {
        var (root, _) = AWorkspaceWithANotebook();
        var tool = new NotebookEditTool(new AgentHarnessService(root), new Always(ToolApprovalDecision.Allowed));

        var result = await tool.InvokeAsync(Args(
            "{ \"path\": \"work.ipynb\", \"cell\": 0, \"edit\": \"sideways\", \"source\": \"x\" }"));

        Assert.False(result.Success);
        Assert.Contains("replace, insert or delete", result.FailureMessage!);
    }

    private sealed class Always : IToolApprovalService
    {
        private readonly ToolApprovalDecision _decision;

        public Always(ToolApprovalDecision decision) => _decision = decision;

        public bool WasAsked { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            WasAsked = true;
            return ValueTask.FromResult(_decision);
        }
    }
}
