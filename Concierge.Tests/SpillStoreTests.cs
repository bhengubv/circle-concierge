using Concierge.Shared.Context;

namespace Concierge.Tests;

/// <summary>
/// What spilling must do (parity feature 11): keep a large output available without carrying
/// it in memory or in the conversation.
/// </summary>
/// <remarks>
/// Pruning throws the middle away. Spilling keeps it — on disk, retrievable — so the model
/// gets a preview and the user can still open the whole thing. The device with the least
/// memory is exactly the one that must not hold a megabyte of command output in a string.
/// </remarks>
public sealed class SpillStoreTests : IDisposable
{
    private readonly string _root;
    private readonly ISpillStore _store;

    public SpillStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"concierge-spill-{Guid.NewGuid():N}");
        _store = new FileSpillStore(_root, maxInlineChars: 100);
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
    public async Task Small_output_is_returned_inline()
    {
        var spilled = await _store.SpillAsync("short output");

        Assert.Equal("short output", spilled.Preview);
        Assert.False(spilled.WasSpilled);
    }

    [Fact]
    public async Task Small_output_writes_no_file()
    {
        await _store.SpillAsync("short output");

        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root).Any());
    }

    [Fact]
    public async Task Large_output_is_written_to_disk()
    {
        var spilled = await _store.SpillAsync(new string('x', 5_000));

        Assert.True(spilled.WasSpilled);
        Assert.True(File.Exists(spilled.Path));
    }

    [Fact]
    public async Task The_whole_output_is_recoverable_from_disk()
    {
        var output = new string('x', 5_000);

        var spilled = await _store.SpillAsync(output);

        Assert.Equal(output, await File.ReadAllTextAsync(spilled.Path!));
    }

    [Fact]
    public async Task The_preview_is_short_enough_to_send()
    {
        var spilled = await _store.SpillAsync(new string('x', 5_000));

        Assert.True(spilled.Preview.Length < 500);
    }

    [Fact]
    public async Task The_preview_says_where_the_rest_is()
    {
        var spilled = await _store.SpillAsync(new string('x', 5_000));

        Assert.Contains(Path.GetFileName(spilled.Path!), spilled.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_preview_says_how_big_the_whole_thing_was()
    {
        var spilled = await _store.SpillAsync(new string('x', 5_000));

        Assert.Contains("5000", spilled.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_spills_do_not_overwrite_each_other()
    {
        var first = await _store.SpillAsync(new string('a', 5_000));
        var second = await _store.SpillAsync(new string('b', 5_000));

        Assert.NotEqual(first.Path, second.Path);
        Assert.StartsWith("aaa", await File.ReadAllTextAsync(first.Path!), StringComparison.Ordinal);
        Assert.StartsWith("bbb", await File.ReadAllTextAsync(second.Path!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_output_is_handled()
    {
        var spilled = await _store.SpillAsync(string.Empty);

        Assert.Equal(string.Empty, spilled.Preview);
        Assert.False(spilled.WasSpilled);
    }

    [Fact]
    public async Task An_unwritable_location_still_returns_something_usable()
    {
        // A phone with a full disk must still get an answer. Losing the spill file is
        // acceptable; losing the output entirely, or throwing into the tool loop, is not.
        var store = new FileSpillStore(Path.Combine(_root, "\0invalid"), maxInlineChars: 10);

        var spilled = await store.SpillAsync(new string('x', 5_000));

        Assert.False(string.IsNullOrEmpty(spilled.Preview));
    }
}
