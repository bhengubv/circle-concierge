using Concierge.Shared;

namespace Concierge.Tests;

/// <summary>
/// What editing and windowed reading must do (parity features 15 and 16).
/// </summary>
/// <remarks>
/// Both exist because the model is small. Asking a 0.6B model to reproduce a whole file to
/// change one line wastes the window and usually corrupts the file; asking it to read a
/// 5,000-line file at all is impossible. An edit states only the change, and a window states
/// only the part being looked at.
/// </remarks>
public sealed class FileEditAndWindowTests : IDisposable
{
    private readonly string _workspace;
    private readonly AgentHarnessService _harness;

    public FileEditAndWindowTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"concierge-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _harness = new AgentHarnessService(_workspace);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp directory.
        }
    }

    // ── Editing in place ───────────────────────────────────────────────

    [Fact]
    public async Task An_approved_edit_changes_only_the_matched_text()
    {
        await WriteFile("code.cs", "line one\nline two\nline three");

        await _harness.EditFileAsync("code.cs", "line two", "LINE TWO", approved: true);

        Assert.Equal("line one\nLINE TWO\nline three", await ReadFile("code.cs"));
    }

    [Fact]
    public async Task An_unapproved_edit_changes_nothing()
    {
        await WriteFile("code.cs", "before");

        var result = await _harness.EditFileAsync("code.cs", "before", "after", approved: false);

        Assert.Equal(ConciergeToolOutcome.ApprovalRequired, result.Outcome);
        Assert.Equal("before", await ReadFile("code.cs"));
    }

    [Fact]
    public async Task Text_that_is_not_there_is_an_error_not_a_silent_no_op()
    {
        await WriteFile("code.cs", "the actual content");

        var result = await _harness.EditFileAsync("code.cs", "something else", "x", approved: true);

        Assert.Equal(ConciergeToolOutcome.Failed, result.Outcome);
        Assert.Equal("the actual content", await ReadFile("code.cs"));
    }

    [Fact]
    public async Task Text_that_appears_twice_is_refused_rather_than_guessed()
    {
        // Replacing the wrong one of two identical lines is worse than refusing: the model
        // is told to be more specific instead of silently corrupting the file.
        await WriteFile("code.cs", "same\nsame");

        var result = await _harness.EditFileAsync("code.cs", "same", "changed", approved: true);

        Assert.Equal(ConciergeToolOutcome.Failed, result.Outcome);
        Assert.Equal("same\nsame", await ReadFile("code.cs"));
    }

    [Fact]
    public async Task An_edit_can_be_previewed_before_it_is_approved()
    {
        await WriteFile("code.cs", "line one\nline two");

        var preview = await _harness.PreviewEditFileAsync("code.cs", "line two", "LINE TWO");

        Assert.Contains("- line two", preview.DiffPreview, StringComparison.Ordinal);
        Assert.Contains("+ LINE TWO", preview.DiffPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_a_file_outside_the_workspace_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _harness.EditFileAsync("..\\outside.txt", "a", "b", approved: true));
    }

    [Fact]
    public async Task Editing_a_protected_file_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _harness.EditFileAsync(".env", "a", "b", approved: true));
    }

    // ── Reading a window ───────────────────────────────────────────────

    [Fact]
    public async Task A_window_returns_only_the_lines_asked_for()
    {
        await WriteFile("big.txt", string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}")));

        var result = await _harness.ReadFileWindowAsync("big.txt", offsetLines: 10, maxLines: 3);

        Assert.Equal("line 11\nline 12\nline 13", result.Output.TrimEnd('\n'));
    }

    [Fact]
    public async Task A_window_past_the_end_returns_nothing_rather_than_failing()
    {
        await WriteFile("small.txt", "one\ntwo");

        var result = await _harness.ReadFileWindowAsync("small.txt", offsetLines: 50, maxLines: 10);

        Assert.Equal(ConciergeToolOutcome.Succeeded, result.Outcome);
        Assert.Equal(string.Empty, result.Output.Trim());
    }

    [Fact]
    public async Task A_window_that_runs_off_the_end_returns_what_is_there()
    {
        await WriteFile("small.txt", "one\ntwo\nthree");

        var result = await _harness.ReadFileWindowAsync("small.txt", offsetLines: 1, maxLines: 100);

        Assert.Equal("two\nthree", result.Output.TrimEnd('\n'));
    }

    [Fact]
    public async Task The_reader_is_told_how_much_more_there_is()
    {
        await WriteFile("big.txt", string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}")));

        var result = await _harness.ReadFileWindowAsync("big.txt", offsetLines: 0, maxLines: 5);

        Assert.Contains("100", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_a_window_of_a_missing_file_fails_cleanly()
    {
        var result = await _harness.ReadFileWindowAsync("nope.txt", offsetLines: 0, maxLines: 5);

        Assert.Equal(ConciergeToolOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_window_outside_the_workspace_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _harness.ReadFileWindowAsync("..\\outside.txt", offsetLines: 0, maxLines: 5));
    }

    [Fact]
    public async Task A_negative_offset_is_treated_as_the_start()
    {
        await WriteFile("small.txt", "one\ntwo");

        var result = await _harness.ReadFileWindowAsync("small.txt", offsetLines: -5, maxLines: 1);

        Assert.Equal("one", result.Output.TrimEnd('\n'));
    }

    private Task WriteFile(string name, string content)
        => File.WriteAllTextAsync(Path.Combine(_workspace, name), content);

    private Task<string> ReadFile(string name)
        => File.ReadAllTextAsync(Path.Combine(_workspace, name));
}
