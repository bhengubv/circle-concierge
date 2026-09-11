using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A design that survives being closed.
///
/// The canvas kept everything in memory, so closing the app threw the work away —
/// while `DesignDocumentFormat`, which exists to write a design down, was
/// referenced by nothing at all. Same shape as `McpClient`, `FileTodoStore`,
/// `ProcessHookBridge`, the sandbox and `EstimatedTokens`: written, correct,
/// reached by nothing.
///
/// The tests worth having are about the bad day rather than the good one — a
/// half-written file, a design from a newer build, a folder that is not there yet.
/// A canvas that refuses to open leaves somebody with no way back to a working
/// surface.
/// </summary>
public sealed class DesignStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-design-tests", Guid.NewGuid().ToString("N"));

    private FileDesignStore Store(string name = "design.json")
        => new(Path.Combine(_root, name));

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

    private static DesignDocument Sample()
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Deck);
        var frame = DesignNode.New(DesignNodeKind.Frame, null, ("name", "One"));
        return document.Add(frame).Add(DesignNode.New(DesignNodeKind.Heading, frame.Id, ("text", "Hello")));
    }

    [Fact]
    public async Task With_nothing_saved_there_is_nothing_to_report()
    {
        var restored = await Store().LoadAsync();

        Assert.Null(restored.Document);

        // A first run is not a fault, so it carries no message to show anybody.
        Assert.Null(restored.Problem);
    }

    [Fact]
    public async Task A_design_comes_back_as_it_was_left()
    {
        var store = Store();
        await store.SaveAsync(Sample());

        var restored = await store.LoadAsync();

        Assert.NotNull(restored.Document);
        Assert.Equal(DesignMedium.Deck, restored.Document!.Medium);
        Assert.Single(restored.Document.Frames);
        Assert.Equal("Hello", restored.Document.ChildrenOf(restored.Document.Frames[0].Id)[0].Text);
    }

    [Fact]
    public async Task Saving_twice_keeps_the_second_one()
    {
        var store = Store();
        await store.SaveAsync(Sample());
        await store.SaveAsync(DesignDocument.Blank(medium: DesignMedium.Sound));

        var restored = await store.LoadAsync();

        Assert.Equal(DesignMedium.Sound, restored.Document!.Medium);
    }

    [Fact]
    public async Task The_folder_does_not_have_to_exist_yet()
    {
        var store = new FileDesignStore(Path.Combine(_root, "not", "there", "yet", "design.json"));

        await store.SaveAsync(Sample());

        Assert.NotNull((await store.LoadAsync()).Document);
    }

    /// <summary>
    /// The reason the file is written beside and moved into place. A save
    /// interrupted halfway would otherwise leave a half-written file where the
    /// design used to be. Losing the last change is recoverable; losing the design
    /// is not.
    /// </summary>
    [Fact]
    public async Task A_half_written_file_is_reported_rather_than_opened()
    {
        var store = Store();
        await store.SaveAsync(Sample());

        var written = await File.ReadAllTextAsync(store.Path);
        await File.WriteAllTextAsync(store.Path, written[..(written.Length / 2)]);

        var restored = await store.LoadAsync();

        Assert.Null(restored.Document);
        Assert.False(string.IsNullOrWhiteSpace(restored.Problem));
    }

    [Fact]
    public async Task A_design_from_a_newer_build_is_reported_rather_than_opened()
    {
        var store = Store();
        await store.SaveAsync(Sample());

        var written = await File.ReadAllTextAsync(store.Path);
        await File.WriteAllTextAsync(store.Path, written.Replace(
            $"\"SchemaVersion\": {DesignDocumentFormat.CurrentVersion}",
            $"\"SchemaVersion\": {DesignDocumentFormat.CurrentVersion + 99}",
            StringComparison.Ordinal));

        var restored = await store.LoadAsync();

        Assert.Null(restored.Document);
        Assert.Contains("newer", restored.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Something_that_is_not_a_design_at_all_is_reported()
    {
        var store = Store();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(store.Path, "this is not a design");

        var restored = await store.LoadAsync();

        Assert.Null(restored.Document);
        Assert.False(string.IsNullOrWhiteSpace(restored.Problem));
    }

    [Fact]
    public async Task Clearing_forgets_it()
    {
        var store = Store();
        await store.SaveAsync(Sample());

        await store.ClearAsync();

        Assert.Null((await store.LoadAsync()).Document);
    }

    [Fact]
    public async Task Clearing_something_that_is_not_there_is_fine()
    {
        await Store().ClearAsync();
    }

    /// <summary>
    /// Saves arrive from the typed sentence, from a tool call and from going back,
    /// and nothing coordinates them. Overlapping writes must not produce a file
    /// that cannot be opened.
    /// </summary>
    [Fact]
    public async Task Overlapping_saves_leave_a_readable_design()
    {
        var store = Store();

        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => store.SaveAsync(
            i % 2 == 0 ? Sample() : DesignDocument.Blank(medium: DesignMedium.Motion))));

        var restored = await store.LoadAsync();

        Assert.NotNull(restored.Document);
        Assert.Null(restored.Problem);
    }
}
