using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aether.Models;
using Aether.Protocol;
using Aether.Routing;

namespace Concierge.Mesh;

/// <summary>
/// A real radio, over the local network.
///
/// Everything in the mesh stack was already here — Aether identity, a route store,
/// DTN bundles with custody transfer, opportunistic delivery — and it had nothing
/// to talk over. <see cref="NullMeshSender"/> is a loopback that reports no peers
/// and refuses every send, which is honest and means bundles pile up in the store
/// forever. Its own summary says it is waiting for "BLE, Wi-Fi Direct, internet
/// relay" to attach. This is the first of those.
///
/// The local network, and not the other three, for a reason: it is the one that
/// can be built and *proven* on the machine this is being written on. BLE and
/// Wi-Fi Direct need two devices in a room and a platform API per head; a relay
/// needs somebody else's server, which is the wrong first answer for a product
/// whose claim is that it works on your own machine. A desktop and a phone on the
/// same wifi is the common case anyway.
///
/// How it works, in full:
///
///   Discovery is a UDP multicast beacon. Every node shouts its UHID and the TCP
///   port it listens on, every few seconds, and remembers everyone it hears from.
///   A node that goes quiet is forgotten, because a peer list that only grows is
///   a routing table full of machines that went home.
///
///   Delivery is TCP, length-prefixed, one packet per connection. Not UDP: a DTN
///   bundle can be larger than a datagram, and a transport that silently truncates
///   is worse than one that refuses.
///
/// What this does **not** do, and must not be mistaken for: authentication. Aether
/// packets carry a signature and a nonce, and verifying them is the protocol's
/// job, not this file's. Anything on the same wifi can send bytes to this port. So
/// this is a transport for things that are safe to receive from a stranger — and
/// an approval is not one of them until the signature is checked. That boundary is
/// stated here because it is the kind that gets forgotten between one commit and
/// the next.
/// </summary>
public sealed class LanMeshSender : IMeshSender, IAsyncDisposable
{
    /// <summary>
    /// The multicast group nodes shout on. Administratively scoped — this address
    /// range does not leave the local network, which is the whole intent.
    /// </summary>
    public const string DiscoveryGroup = "239.7.7.7";

    public const int DiscoveryPort = 47771;

    /// <summary>
    /// How often to announce. Five seconds is frequent enough that a phone joining
    /// the wifi is found before somebody gives up on it, and rare enough that a
    /// dozen nodes are not a conversation.
    /// </summary>
    public static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a silent peer is kept. Three missed beacons: one can be lost to a
    /// busy network, three means gone.
    /// </summary>
    public static readonly TimeSpan PeerLifetime = TimeSpan.FromSeconds(16);

    /// <summary>
    /// The largest packet accepted. A length prefix from a stranger is an
    /// allocation instruction, and without a ceiling one hostile number is an
    /// out-of-memory.
    /// </summary>
    public const int MaxPacketBytes = 4 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Known> _peers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TcpListener _listener;
    private readonly UdpClient? _discovery;
    private readonly List<Task> _running = new();

    public LanMeshSender(string localUhid, string? displayName = null, int listenPort = 0, bool discover = true)
    {
        LocalUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        DisplayName = displayName ?? Environment.MachineName;

        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _listener.Start();
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _running.Add(AcceptLoopAsync(_stopping.Token));

        if (!discover)
        {
            return;
        }

        // Discovery is optional so a test — or a host that pairs by address —
        // can use the transport without putting anything on the network.
        _discovery = new UdpClient(AddressFamily.InterNetwork);
        _discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _discovery.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _discovery.JoinMulticastGroup(IPAddress.Parse(DiscoveryGroup));

        _running.Add(BeaconLoopAsync(_stopping.Token));
        _running.Add(ListenForBeaconsAsync(_stopping.Token));
    }

    public string LocalUhid { get; }

    public string DisplayName { get; }

    /// <summary>The TCP port peers deliver to. Chosen by the OS unless one is given.</summary>
    public int ListenPort { get; }

    /// <summary>
    /// No location. A geohash is a position, and a mesh that reports one by default
    /// is a mesh that leaks where somebody is to everybody on the network.
    /// </summary>
    public string? LocalGeohash => null;

    /// <summary>Raised for every packet that arrives and is meant for someone.</summary>
    public event EventHandler<MeshPacket>? PacketReceived;

    /// <summary>
    /// Who is reachable right now. Peers not heard from recently are dropped as
    /// they are read, rather than by a timer — the answer is only ever needed when
    /// somebody asks, and a sweep that runs when nobody is listening is a wake-up
    /// on a battery for nothing.
    /// </summary>
    public IReadOnlyList<PeerInfo> GetConnectedPeers()
    {
        var cutoff = DateTime.UtcNow - PeerLifetime;

        foreach (var (uhid, known) in _peers)
        {
            if (known.LastSeen < cutoff)
            {
                _peers.TryRemove(uhid, out _);
            }
        }

        return _peers.Values.Select(k => k.Info).ToList();
    }

    /// <summary>
    /// Adds a peer whose address is already known, without waiting to hear it
    /// announce. Pairing by address is how a watch and a desktop find each other
    /// when they are not on the same wifi, and it is how this gets tested without
    /// putting multicast traffic on somebody's network.
    /// </summary>
    public void AddPeer(string uhid, IPEndPoint endpoint, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(uhid);
        ArgumentNullException.ThrowIfNull(endpoint);

        _peers[uhid] = new Known(
            new PeerInfo
            {
                Uhid = uhid,
                DisplayName = displayName ?? uhid,
                TransportType = "lan",
                DiscoveredAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                ReliabilityScore = 1.0,
            },
            endpoint,
            DateTime.UtcNow);
    }

    public async Task<bool> SendAsync(
        MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (!_peers.TryGetValue(nextHopUhid ?? string.Empty, out var peer))
        {
            // Not reachable. False rather than an exception, because the DTN
            // service above expects to be told "not now" — the bundle stays in
            // the store and goes when a peer turns up.
            return false;
        }

        return await DeliverAsync(packet, peer.Endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var delivered = 0;

        foreach (var peer in GetConnectedPeers())
        {
            if (_peers.TryGetValue(peer.Uhid, out var known)
                && await DeliverAsync(packet, known.Endpoint, cancellationToken).ConfigureAwait(false))
            {
                delivered++;
            }
        }

        return delivered;
    }

    private async Task<bool> DeliverAsync(
        MeshPacket packet, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
            if (bytes.Length > MaxPacketBytes)
            {
                return false;
            }

            using var client = new TcpClient();
            await client.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

            var stream = client.GetStream();
            var length = BitConverter.GetBytes(bytes.Length);

            await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A peer that has gone, a refused connection, a half-open socket. All
            // of it means the same thing to the layer above: not delivered, try
            // again when something changes.
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Deliberately not awaited: one slow sender must not stop everybody
            // else being heard.
            _ = ReadOneAsync(client, cancellationToken);
        }
    }

    private async Task ReadOneAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                var header = new byte[4];
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

                var length = BitConverter.ToInt32(header);
                if (length <= 0 || length > MaxPacketBytes)
                {
                    return;
                }

                var body = new byte[length];
                await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

                if (JsonSerializer.Deserialize<MeshPacket>(body) is { } packet
                    && !string.Equals(packet.SourceUhid, LocalUhid, StringComparison.Ordinal))
                {
                    PacketReceived?.Invoke(this, packet);
                }
            }
            catch (Exception)
            {
                // Malformed, truncated, or a port scanner. One bad connection is
                // not a reason to stop listening.
            }
        }
    }

    private async Task BeaconLoopAsync(CancellationToken cancellationToken)
    {
        var group = new IPEndPoint(IPAddress.Parse(DiscoveryGroup), DiscoveryPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var beacon = JsonSerializer.SerializeToUtf8Bytes(
                    new Beacon(LocalUhid, DisplayName, ListenPort));

                await _discovery!.SendAsync(beacon, group, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // No network, or a machine with multicast switched off. Keep
                // announcing: the network may come back, and a transport that
                // gives up permanently on one failure is worse than a null one.
            }

            try
            {
                await Task.Delay(BeaconInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ListenForBeaconsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var received = await _discovery!.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                if (JsonSerializer.Deserialize<Beacon>(received.Buffer) is not { } beacon
                    || string.IsNullOrEmpty(beacon.Uhid)
                    || string.Equals(beacon.Uhid, LocalUhid, StringComparison.Ordinal))
                {
                    continue;
                }

                AddPeer(
                    beacon.Uhid,
                    new IPEndPoint(received.RemoteEndPoint.Address, beacon.Port),
                    beacon.Name);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception)
            {
                // Anything else on the port: another protocol, a stray datagram.
            }
        }
    }

    private bool _closed;

    public async ValueTask DisposeAsync()
    {
        // Closing twice is ordinary — a host disposing a container that a test or
        // a `using` already closed. Without this the second call cancels a source
        // that has been disposed and throws on the way out of shutdown, which is
        // the worst moment to raise an exception nobody is going to catch.
        if (_closed)
        {
            return;
        }

        _closed = true;

        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
        }

        _discovery?.Dispose();

        try
        {
            await Task.WhenAll(_running).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutting down. A loop that will not stop in two seconds is not
            // worth holding the application open for.
        }

        _stopping.Dispose();
    }

    private sealed record Known(PeerInfo Info, IPEndPoint Endpoint, DateTime LastSeen)
    {
        public DateTime LastSeen { get; init; } = LastSeen;
    }

    /// <summary>
    /// What a node shouts. Deliberately three fields: who, what to call them, and
    /// where to deliver. Anything more is a fingerprint broadcast to a network
    /// nobody vetted.
    /// </summary>
    private sealed record Beacon(string Uhid, string Name, int Port);
}
