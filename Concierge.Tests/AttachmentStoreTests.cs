using System.Text;
using Concierge.Shared.Attachments;

namespace Concierge.Tests;

/// <summary>
/// What the attachment store must do (parity feature 47): hold bytes outside the
/// conversation and hand back a reference.
/// </summary>
/// <remarks>
/// Today a photo taken on the home screen is base64-encoded into a chat message. That is the
/// worst available place for it: it inflates the conversation on every subsequent request,
/// it cannot be deduplicated, and it is carried into the model's context forever. Content
/// addressing means the same picture sent twice is stored once.
/// </remarks>
public sealed class AttachmentStoreTests : IDisposable
{
    private readonly string _root;
    private readonly IAttachmentStore _store;

    public AttachmentStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"concierge-attachments-{Guid.NewGuid():N}");
        _store = new FileAttachmentStore(_root);
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
    public async Task Stored_bytes_come_back_unchanged()
    {
        var bytes = Encoding.UTF8.GetBytes("the original content");

        var stored = await _store.StoreAsync(bytes, "text/plain", "notes.txt");

        Assert.Equal(bytes, await _store.ReadAsync(stored.Id));
    }

    [Fact]
    public async Task A_stored_attachment_keeps_its_type()
    {
        var stored = await _store.StoreAsync([1, 2, 3], "image/jpeg", "photo.jpg");

        Assert.Equal("image/jpeg", (await _store.DescribeAsync(stored.Id))!.ContentType);
    }

    [Fact]
    public async Task A_stored_attachment_keeps_its_name()
    {
        var stored = await _store.StoreAsync([1, 2, 3], "image/jpeg", "photo.jpg");

        Assert.Equal("photo.jpg", (await _store.DescribeAsync(stored.Id))!.FileName);
    }

    [Fact]
    public async Task A_stored_attachment_knows_its_size()
    {
        var stored = await _store.StoreAsync(new byte[1_234], "application/octet-stream", "blob.bin");

        Assert.Equal(1_234, stored.SizeBytes);
    }

    [Fact]
    public async Task The_same_content_stored_twice_is_stored_once()
    {
        var bytes = Encoding.UTF8.GetBytes("identical");

        var first = await _store.StoreAsync(bytes, "text/plain", "a.txt");
        var second = await _store.StoreAsync(bytes, "text/plain", "b.txt");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(Directory.EnumerateFiles(_root, "*.bin", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Different_content_gets_different_identities()
    {
        var first = await _store.StoreAsync(Encoding.UTF8.GetBytes("one"), "text/plain", "a.txt");
        var second = await _store.StoreAsync(Encoding.UTF8.GetBytes("two"), "text/plain", "b.txt");

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Reading_something_that_was_never_stored_returns_nothing()
    {
        Assert.Null(await _store.ReadAsync("nope"));
    }

    [Fact]
    public async Task Describing_something_that_was_never_stored_returns_nothing()
    {
        Assert.Null(await _store.DescribeAsync("nope"));
    }

    [Fact]
    public async Task An_attachment_survives_a_restart()
    {
        var stored = await _store.StoreAsync(Encoding.UTF8.GetBytes("durable"), "text/plain", "d.txt");

        var reopened = new FileAttachmentStore(_root);

        Assert.Equal("durable", Encoding.UTF8.GetString((await reopened.ReadAsync(stored.Id))!));
    }

    [Fact]
    public async Task Empty_content_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.StoreAsync([], "text/plain", "empty.txt"));
    }

    [Fact]
    public async Task A_reference_is_short_enough_to_put_in_a_message()
    {
        var stored = await _store.StoreAsync(new byte[500_000], "image/jpeg", "big.jpg");

        Assert.True(stored.Id.Length < 100);
    }
}
