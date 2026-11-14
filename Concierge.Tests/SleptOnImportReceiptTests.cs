using System.Text;
using Concierge.Shared.Media;

namespace Concierge.Tests;

public sealed class SleptOnImportReceiptTests
{
    [Fact]
    public async Task Accepted_receipt_keeps_the_local_draft_and_revision_identifiers()
    {
        var id = Guid.NewGuid().ToString();
        var revision = new string('a', 64);
        var receipt = await ReadAsync($$"""{"schemaVersion":1,"status":"accepted","draftId":"{{id}}","revisionId":"{{revision}}"}""");

        Assert.Equal("accepted", receipt.Status);
        Assert.Equal(id, receipt.DraftId);
        Assert.Equal(revision, receipt.RevisionId);
        Assert.Empty(receipt.Errors);
    }

    [Fact]
    public async Task Rejected_receipt_gives_a_specific_failure_without_a_draft_id()
    {
        var receipt = await ReadAsync("""{"schemaVersion":1,"status":"rejected","errors":[{"code":"hash","message":"audio.wav differs","path":"audio.wav"}]}""");

        Assert.Equal("rejected", receipt.Status);
        Assert.Null(receipt.DraftId);
        Assert.Equal("hash", Assert.Single(receipt.Errors).Code);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"status":"accepted","draftId":"x","revisionId":"x"}""")]
    [InlineData("""{"schemaVersion":1,"status":"accepted","draftId":"not-a-guid","revisionId":"x"}""")]
    [InlineData("""{"schemaVersion":1,"status":"rejected","errors":[]}""")]
    public async Task Unsupported_or_incomplete_receipts_are_rejected(string json)
        => await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(json));

    [Fact]
    public async Task Oversize_receipt_is_rejected_before_parsing()
        => await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(new string('x', 64 * 1024 + 1)));

    private static Task<SleptOnImportReceipt> ReadAsync(string json)
        => SleptOnImportReceiptReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)));
}

internal sealed class FixedSleptOnReceiptPicker : ISleptOnImportReceiptPicker
{
    public bool Called { get; private set; }
    public string RevisionId { get; set; } = new string('a', 64);

    public Task<SleptOnImportReceipt?> PickAsync(CancellationToken cancellationToken = default)
    {
        Called = true;
        return Task.FromResult<SleptOnImportReceipt?>(new SleptOnImportReceipt(
            "accepted", "26b690a5-5e28-4e6d-9d88-92c4bf835d4f", RevisionId, []));
    }
}
