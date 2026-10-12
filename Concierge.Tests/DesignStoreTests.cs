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
    public async Task Canvas_history_position_and_selection_survive_a_restart()
    {
        var store = Store();
        var session = new DesignSession();
        var frame = DesignNode.New(DesignNodeKind.Frame, null, ("text", "First"));
        session.Record(session.Current.Add(frame), "Added the first frame");
        session.Point(frame.Id);
        session.Record(session.Current.Add(DesignNode.New(DesignNodeKind.Heading, frame.Id, ("text", "Next"))), "Added a heading");
        session.Back();

        await store.SaveSessionAsync(session.Capture());

        var restored = await store.LoadSessionAsync();
        Assert.Null(restored.Problem);
        Assert.NotNull(restored.Session);
        var reopened = new DesignSession(restored.Session!);
        Assert.Equal(3, reopened.Moments.Count);
        Assert.Equal(1, reopened.Position);
        Assert.True(reopened.CanGoBack);
        Assert.True(reopened.CanGoForward);
        Assert.Equal(frame.Id, reopened.Selected);
        Assert.Equal("First", reopened.Current.Find(frame.Id)!.Text);
        reopened.Forward();
        Assert.Equal("Next", reopened.Current.ChildrenOf(frame.Id).Single().Text);
    }

    [Fact]
    public async Task Legacy_single_document_file_opens_as_one_history_moment()
    {
        var store = Store();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(store.Path, DesignDocumentFormat.Serialize(Sample()));

        var restored = await store.LoadSessionAsync();

        Assert.Null(restored.Problem);
        Assert.Single(restored.Session!.Moments);
        Assert.Equal(DesignMedium.Deck, restored.Session.Moments[0].Document.Medium);
    }

    [Fact]
    public async Task Document_only_update_keeps_the_existing_history_for_older_clients()
    {
        var store = Store();
        var session = new DesignSession();
        var frame = DesignNode.New(DesignNodeKind.Frame, null, ("text", "First"));
        session.Record(session.Current.Add(frame), "Added the first frame");
        await store.SaveSessionAsync(session.Capture());
        var updated = session.Current.Add(DesignNode.New(DesignNodeKind.Heading, frame.Id, ("text", "From Mobile")));

        await store.SaveAsync(updated);

        var restored = await store.LoadSessionAsync();
        Assert.Equal(3, restored.Session!.Moments.Count);
        Assert.Equal("Updated from a connected device", restored.Session.Moments[^1].What);
        Assert.Equal("From Mobile", restored.Session.Moments[^1].Document.ChildrenOf(frame.Id).Single().Text);
    }

    [Fact]
    public async Task Newer_session_file_is_not_overwritten_by_a_save()
    {
        var store = Store();
        var session = new DesignSession();
        session.Record(Sample(), "Added a design");
        await store.SaveSessionAsync(session.Capture());
        var future = (await File.ReadAllTextAsync(store.Path)).Replace(
            "\"SessionSchemaVersion\": 1",
            "\"SessionSchemaVersion\": 99",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(store.Path, future);

        await Assert.ThrowsAsync<IOException>(() => store.SaveSessionAsync(session.Capture()));

        Assert.Contains("\"SessionSchemaVersion\": 99", await File.ReadAllTextAsync(store.Path));
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
        var sessionWire = System.Text.Json.Nodes.JsonNode.Parse(written)!;
        var savedMoment = sessionWire["Moments"]![0]!;
        var documentWire = System.Text.Json.Nodes.JsonNode.Parse(savedMoment["Document"]!.GetValue<string>())!;
        documentWire["SchemaVersion"] = DesignDocumentFormat.CurrentVersion + 99;
        savedMoment["Document"] = documentWire.ToJsonString();
        await File.WriteAllTextAsync(store.Path, sessionWire.ToJsonString());

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
