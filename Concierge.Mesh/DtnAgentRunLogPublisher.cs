using System.Text.Json;
using Aether.Dtn;
using Aether.Models;
using Concierge.Shared;

namespace Concierge.Mesh;

/// <summary>
/// Serializes a <see cref="ConciergeRunLog"/> to JSON and hands it to
/// <see cref="IDtnService.CreateBundleAsync"/> as an opaque encrypted payload. The bundle
/// lives in the local DTN store until a transport attaches and a peer accepts custody.
/// </summary>
public sealed class DtnAgentRunLogPublisher : IAgentRunLogPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDtnService _dtn;
    private readonly string _recipientUhid;

    public DtnAgentRunLogPublisher(IDtnService dtn, string recipientUhid)
    {
        _dtn = dtn ?? throw new ArgumentNullException(nameof(dtn));
        _recipientUhid = string.IsNullOrWhiteSpace(recipientUhid)
            ? throw new ArgumentException("Recipient UHID must be provided.", nameof(recipientUhid))
            : recipientUhid;
    }

    public async Task PublishAsync(ConciergeRunLog log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var payload = JsonSerializer.SerializeToUtf8Bytes(log, JsonOptions);
        await _dtn.CreateBundleAsync(_recipientUhid, payload, BundlePriority.Normal, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
