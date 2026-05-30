using Aether.Models;
using Aether.Protocol;
using Aether.Routing;

namespace Concierge.Mesh;

/// <summary>
/// Loopback <see cref="IMeshSender"/>: knows the local UHID and never has connected peers.
/// Lets <see cref="Aether.Dtn.DtnService"/> live inside a single-node Concierge host so
/// agent run logs can be created as real DTN bundles and queued for opportunistic delivery
/// once a real transport (BLE, Wi-Fi Direct, internet relay) attaches.
/// </summary>
public sealed class NullMeshSender : IMeshSender
{
    public NullMeshSender(string localUhid)
    {
        LocalUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
    }

    public string LocalUhid { get; }

    public string? LocalGeohash => null;

    public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
