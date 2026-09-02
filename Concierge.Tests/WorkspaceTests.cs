using Concierge.Shared;

namespace Concierge.Tests;

/// <summary>
/// What a workspace must do (parity feature 48): be an explicit, named place the session
/// works in, supplied by the host rather than discovered from wherever the process happens
/// to be running.
/// </summary>
/// <remarks>
/// This is the fix for a real defect. <c>AgentHarnessService</c> located its trusted root by
/// walking up for <c>Concierge.slnx</c> and falling back to the current directory. On a
/// phone there is no solution file, so every path guard — the containment check, the denied
/// segments, the protected filenames — was anchored to an accident.
/// </remarks>
public sealed class WorkspaceTests : IDisposable
{
    private readonly string _root;

    public WorkspaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"concierge-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp directory.
        }
    }

    [Fact]
    public void A_workspace_has_the_root_it_was_given()
    {
        var workspace = new ConciergeWorkspace("Homework", _root);

        Assert.Equal(Path.GetFullPath(_root), workspace.Root);
    }

    [Fact]
    public void A_workspace_has_the_name_it_was_given()
    {
        Assert.Equal("Homework", new ConciergeWorkspace("Homework", _root).Name);
    }

    [Fact]
    public void A_workspace_creates_its_root_if_it_is_missing()
    {
        var path = Path.Combine(_root, "not-yet-there");

        _ = new ConciergeWorkspace("New", path);

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void A_workspace_without_a_root_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ConciergeWorkspace("Nameless", "  "));
    }

    [Fact]
    public void A_workspace_without_a_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ConciergeWorkspace("  ", _root));
    }

    [Fact]
    public void A_harness_built_on_a_workspace_uses_that_root()
    {
        var workspace = new ConciergeWorkspace("Homework", _root);

        var harness = new AgentHarnessService(workspace);

        Assert.Equal(Path.GetFullPath(_root), harness.WorkspaceRoot);
    }

    [Fact]
    public async Task A_harness_built_on_a_workspace_confines_paths_to_it()
    {
        var harness = new AgentHarnessService(new ConciergeWorkspace("Homework", _root));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ReadFileAsync("..\\escape.txt"));
    }

    [Fact]
    public void Two_workspaces_do_not_share_a_root()
    {
        var first = new ConciergeWorkspace("One", Path.Combine(_root, "one"));
        var second = new ConciergeWorkspace("Two", Path.Combine(_root, "two"));

        Assert.NotEqual(first.Root, second.Root);
    }

    [Fact]
    public void A_workspace_is_identified_by_its_root_not_its_name()
    {
        // Two sessions naming the same folder differently are the same working context;
        // treating them as different would let one escape the other's guards.
        var first = new ConciergeWorkspace("Homework", _root);
        var second = new ConciergeWorkspace("School", _root);

        Assert.Equal(first.Root, second.Root);
    }
}
