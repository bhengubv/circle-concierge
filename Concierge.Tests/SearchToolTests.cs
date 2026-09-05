using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Looking around.
///
/// The model was given read_file and no way to find a path, so it could only open a
/// file somebody had already named to it — every "where is…" and "which file handles…"
/// was unanswerable. Two of the four operations here were on the harness the whole
/// time, with tests, and simply never exposed to a conversation: a line window, and a
/// find/replace edit with a diff preview.
///
/// The last of those is the one that mattered most. write_file was the only way to
/// change anything, so altering one line meant the model reproducing the whole file
/// from memory. That is the same fault the notebook tools were built to avoid, and it
/// was sitting on every other file in the repository.
/// </summary>
public sealed class SearchToolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-search-tests", Guid.NewGuid().ToString("N"));

    private readonly AgentHarnessService _harness;

    public SearchToolTests()
    {
        Directory.CreateDirectory(_root);
        Write("Concierge.slnx", string.Empty);
        Write("Program.cs", "class Program\n{\n    static void Main() => Greet();\n}\n");
        Write("Greeter.cs", "static class G\n{\n    public static void Greet() { }\n}\n");
        Write("notes.md", "Some prose about Greet.\n");
        Write("bin/Generated.cs", "// Greet in a build folder\n");
        Write(".git/config", "[core]\n");

        _harness = new AgentHarnessService(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Swept with the test-run temp root regardless.
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static JsonNode Args(string json) => JsonNode.Parse(json)!;

    // ── Listing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Listing_names_the_files_that_are_there()
    {
        var result = await new ListFilesTool(_harness).InvokeAsync(null);

        Assert.True(result.Success);
        Assert.Contains("Program.cs", result.Output);
        Assert.Contains("Greeter.cs", result.Output);
    }

    /// <summary>
    /// The same areas read_file refuses, skipped rather than refused. A listing of the
    /// repository root that throws because .git exists is useless, and somebody asking
    /// what is in a folder is not asking about the folder they cannot see.
    /// </summary>
    [Fact]
    public async Task Listing_leaves_out_what_no_tool_may_open()
    {
        var result = await new ListFilesTool(_harness).InvokeAsync(null);

        Assert.DoesNotContain(".git", result.Output);
        Assert.DoesNotContain("bin/", result.Output);
    }

    [Fact]
    public async Task A_pattern_narrows_the_listing()
    {
        var result = await new ListFilesTool(_harness)
            .InvokeAsync(Args("{ \"pattern\": \"*.md\" }"));

        Assert.Contains("notes.md", result.Output);
        Assert.DoesNotContain("Program.cs", result.Output);
    }

    [Fact]
    public async Task A_folder_that_is_not_there_says_so()
    {
        var result = await new ListFilesTool(_harness)
            .InvokeAsync(Args("{ \"path\": \"nowhere\" }"));

        Assert.False(result.Success);
        Assert.Contains("not found", result.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Paths are workspace-relative and use forward slashes, so a path the model reads
    /// back from a listing is one it can hand straight to read_file on any platform.
    /// </summary>
    [Fact]
    public async Task Listed_paths_can_be_read_back_without_translation()
    {
        var listed = await new ListFilesTool(_harness)
            .InvokeAsync(Args("{ \"pattern\": \"Generated.cs\" }"));

        Assert.DoesNotContain("\\", listed.Output);

        var path = (await new ListFilesTool(_harness).InvokeAsync(Args("{ \"pattern\": \"notes.md\" }")))
            .Output.Split('\n').Last(l => l.Trim().Length > 0).Trim();

        var read = await new AgentHarnessReadTool(_harness)
            .InvokeAsync(Args($"{{ \"path\": \"{path}\" }}"));

        Assert.True(read.Success);
    }

    [Fact]
    public async Task Nothing_escapes_the_workspace()
    {
        var result = await new ListFilesTool(_harness)
            .InvokeAsync(Args("{ \"path\": \"../..\" }"));

        Assert.False(result.Success);
    }

    // ── Searching ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_search_says_which_file_and_which_line()
    {
        var result = await new SearchTextTool(_harness)
            .InvokeAsync(Args("{ \"query\": \"Greet\" }"));

        Assert.True(result.Success);
        Assert.Contains("Program.cs:3", result.Output);
        Assert.Contains("Greeter.cs:3", result.Output);
    }

    [Fact]
    public async Task A_search_can_be_narrowed_to_one_kind_of_file()
    {
        var result = await new SearchTextTool(_harness)
            .InvokeAsync(Args("{ \"query\": \"Greet\", \"pattern\": \"*.md\" }"));

        Assert.Contains("notes.md", result.Output);
        Assert.DoesNotContain("Program.cs", result.Output);
    }

    [Fact]
    public async Task A_search_skips_the_areas_a_listing_skips()
    {
        var result = await new SearchTextTool(_harness)
            .InvokeAsync(Args("{ \"query\": \"Greet\" }"));

        Assert.DoesNotContain("Generated.cs", result.Output);
    }

    /// <summary>
    /// No match is an answer, not a failure. A failed tool call reads to a model as
    /// something to retry; "not here" is what it needs to know.
    /// </summary>
    [Fact]
    public async Task Finding_nothing_succeeds_and_says_nothing_was_found()
    {
        var result = await new SearchTextTool(_harness)
            .InvokeAsync(Args("{ \"query\": \"Nonexistent\" }"));

        Assert.True(result.Success);
        Assert.Contains("No match", result.Output);
    }

    /// <summary>
    /// Literal, not a regex. A model looking for a symbol containing a dot or a bracket
    /// would otherwise get either everything or nothing, with no way to tell which.
    /// </summary>
    [Fact]
    public async Task The_query_is_matched_literally()
    {
        var result = await new SearchTextTool(_harness)
            .InvokeAsync(Args("{ \"query\": \"G.e.e.\" }"));

        Assert.True(result.Success);
        Assert.Contains("No match", result.Output);
    }

    [Fact]
    public async Task A_search_with_no_query_is_refused()
    {
        var result = await new SearchTextTool(_harness).InvokeAsync(Args("{ }"));

        Assert.False(result.Success);
    }

    /// <summary>A page of a PNG in a conversation helps nobody.</summary>
    [Fact]
    public async Task Binary_files_are_left_out()
    {
        File.WriteAllBytes(Path.Combine(_root, "image.dat"), [0x47, 0x72, 0x65, 0x65, 0x74, 0x00, 0x01]);

        var result = await new SearchTextTool(_harness)
            .InvokeAsync(Args("{ \"query\": \"Greet\" }"));

        Assert.DoesNotContain("image.dat", result.Output);
    }

    // ── Reading part of a file ────────────────────────────────────────────

    [Fact]
    public async Task A_window_reads_the_lines_asked_for()
    {
        var result = await new ReadFileWindowTool(_harness)
            .InvokeAsync(Args("{ \"path\": \"Program.cs\", \"offset\": 3, \"limit\": 1 }"));

        Assert.True(result.Success);
        Assert.Contains("Main()", result.Output);
        Assert.DoesNotContain("class Program", result.Output);
    }

    /// <summary>
    /// Lines count from 1, as they do to a person and in every editor. The harness
    /// counts from 0, and the translation belongs here rather than in the model's head.
    /// </summary>
    [Fact]
    public async Task Line_one_is_the_first_line()
    {
        var result = await new ReadFileWindowTool(_harness)
            .InvokeAsync(Args("{ \"path\": \"Program.cs\", \"offset\": 1, \"limit\": 1 }"));

        Assert.Contains("class Program", result.Output);
    }

    [Fact]
    public async Task A_window_past_the_end_is_empty_rather_than_an_error()
    {
        var result = await new ReadFileWindowTool(_harness)
            .InvokeAsync(Args("{ \"path\": \"Program.cs\", \"offset\": 500, \"limit\": 10 }"));

        Assert.True(result.Success);
    }

    // ── Editing part of a file ────────────────────────────────────────────

    [Fact]
    public async Task An_approved_edit_changes_only_what_it_named()
    {
        var tool = new EditFileTool(_harness, new Always(ToolApprovalDecision.Allowed));

        var result = await tool.InvokeAsync(Args(
            "{ \"path\": \"Greeter.cs\", \"find\": \"public static void Greet() { }\", \"replace\": \"public static void Greet() { Log(); }\" }"));

        var after = File.ReadAllText(Path.Combine(_root, "Greeter.cs"));

        Assert.True(result.Success);
        Assert.Contains("Log();", after);
        Assert.Contains("static class G", after);
    }

    [Fact]
    public async Task A_refused_edit_leaves_the_file_alone()
    {
        var before = File.ReadAllText(Path.Combine(_root, "Greeter.cs"));
        var tool = new EditFileTool(_harness, new Always(ToolApprovalDecision.Denied));

        var result = await tool.InvokeAsync(Args(
            "{ \"path\": \"Greeter.cs\", \"find\": \"public static void Greet() { }\", \"replace\": \"gone\" }"));

        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "Greeter.cs")));
    }

    /// <summary>
    /// Ambiguity is refused, not guessed. A wrong guess corrupts the file silently,
    /// and the count tells the model to come back with more surrounding text.
    /// </summary>
    [Fact]
    public async Task Text_that_appears_twice_is_refused_with_the_count()
    {
        Write("Twice.cs", "var x = 1;\nvar x = 1;\n");
        var tool = new EditFileTool(_harness, new Always(ToolApprovalDecision.Allowed));

        var result = await tool.InvokeAsync(Args(
            "{ \"path\": \"Twice.cs\", \"find\": \"var x = 1;\", \"replace\": \"var x = 2;\" }"));

        Assert.False(result.Success);
        Assert.Contains("2 times", result.FailureMessage!);
    }

    [Fact]
    public async Task Text_that_is_not_there_is_refused()
    {
        var tool = new EditFileTool(_harness, new Always(ToolApprovalDecision.Allowed));

        var result = await tool.InvokeAsync(Args(
            "{ \"path\": \"Greeter.cs\", \"find\": \"nothing like this\", \"replace\": \"x\" }"));

        Assert.False(result.Success);
        Assert.Contains("not found", result.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_edit_missing_an_argument_is_refused_before_anybody_is_asked()
    {
        var approver = new Always(ToolApprovalDecision.Allowed);
        var tool = new EditFileTool(_harness, approver);

        var result = await tool.InvokeAsync(Args("{ \"path\": \"Greeter.cs\" }"));

        Assert.False(result.Success);
        Assert.False(approver.WasAsked);
    }

    // ── What the model is handed ──────────────────────────────────────────

    /// <summary>
    /// Only the edit changes anything. Three of the four are as safe as read_file and
    /// must not interrupt anybody, or looking around a repository becomes a queue of
    /// approval prompts and the mode stops meaning anything.
    /// </summary>
    [Fact]
    public void Only_one_of_the_four_can_change_a_file()
    {
        Assert.True(new ListFilesTool(_harness).IsReadOnly);
        Assert.True(new SearchTextTool(_harness).IsReadOnly);
        Assert.True(new ReadFileWindowTool(_harness).IsReadOnly);
        Assert.False(new EditFileTool(_harness, new Always(ToolApprovalDecision.Denied)).IsReadOnly);
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
