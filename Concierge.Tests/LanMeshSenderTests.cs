using System.Net;
using Aether.Protocol;
using Concierge.Mesh;

namespace Concierge.Tests;

/// <summary>
/// A real radio.
///
/// The mesh stack was complete except for the one thing that makes it a mesh.
/// Aether identity, a route store, DTN bundles with custody transfer, opportunistic
/// delivery — all of it there, all of it talking over <see cref="NullMeshSender"/>,
/// which reports no peers and refuses every send. Bundles were created correctly
/// and queued forever.
///
/// The local network first, and not BLE or Wi-Fi Direct or a relay, because it is
/// the one that can be proven on the machine it was written on — and because a
/// relay means somebody else's server, which is the wrong first answer for a
/// product whose claim is that it works on your own machine.
///
/// Discovery is switched off in these tests and peers are paired by address. Not a
/// shortcut: a test suite that sprays multicast onto whatever network the machine
/// happens to be on is a bad neighbour, and pairing by address is a real path
/// anyway — it is how two devices find each other when they are not on the same
/// wifi.
/// </summary>
public sealed class LanMeshSenderTests
{
    private static LanMeshSender Node(string uhid) => new(uhid, uhid, listenPort: 0, discover: false);

    private static MeshPacket Packet(string from, string to, string body = "hello")
        => new()
        {
            Id = Guid.NewGuid(),
            Type = PacketType.Data,
            SourceUhid = from,
            DestinationUhid = to,
            Ttl = 8,
            Payload = System.Text.Encoding.UTF8.GetBytes(body),
            CreatedAt = DateTime.UtcNow,
        };

    private static IPEndPoint Reaching(LanMeshSender node)
        => new(IPAddress.Loopback, node.ListenPort);

    /// <summary>
    /// Waits for something to arrive. Polled rather than slept: a fixed delay is
    /// either slower than it needs to be or shorter than a loaded machine needs,
    /// and usually both on different days.
    /// </summary>
    private static async Task<bool> Within(TimeSpan limit, Func<bool> done)
    {
        var deadline = DateTime.UtcNow + limit;

        while (DateTime.UtcNow < deadline)
        {
            if (done())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return done();
    }

    // ── Delivery ──────────────────────────────────────────────────────────

    /// <summary>The thing that was missing: a packet reaching another node.</summary>
    [Fact]
    public async Task A_packet_reaches_the_node_it_was_sent_to()
    {
        await using var alice = Node("alice");
        await using var bob = Node("bob");

        MeshPacket? arrived = null;
        bob.PacketReceived += (_, packet) => arrived = packet;

        alice.AddPeer("bob", Reaching(bob));

        Assert.True(await alice.SendAsync(Packet("alice", "bob"), "bob"));
        Assert.True(await Within(TimeSpan.FromSeconds(5), () => arrived is not null));
        Assert.Equal("alice", arrived!.SourceUhid);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(arrived.Payload));
    }

    /// <summary>
    /// And back the other way, which is the entire point of the exercise: the
    /// wrist can be told something is waiting, and today it cannot answer.
    /// </summary>
    [Fact]
    public async Task An_answer_comes_back()
    {
        await using var desk = Node("desk");
        await using var watch = Node("watch");

        MeshPacket? answer = null;
        desk.PacketReceived += (_, packet) => answer = packet;

        desk.AddPeer("watch", Reaching(watch));
        watch.AddPeer("desk", Reaching(desk));

        await desk.SendAsync(Packet("desk", "watch", "may I push?"), "watch");
        await watch.SendAsync(Packet("watch", "desk", "allowed"), "desk");

        Assert.True(await Within(TimeSpan.FromSeconds(5), () => answer is not null));
        Assert.Equal("allowed", System.Text.Encoding.UTF8.GetString(answer!.Payload));
    }

    /// <summary>
    /// An unreachable peer is "not now", not an exception. The DTN service above
    /// expects that answer — the bundle stays in the store and goes when somebody
    /// turns up, which is the whole idea of delay-tolerant delivery.
    /// </summary>
    [Fact]
    public async Task A_peer_nobody_knows_is_not_an_error()
    {
        await using var alice = Node("alice");

        Assert.False(await alice.SendAsync(Packet("alice", "nobody"), "nobody"));
    }

    [Fact]
    public async Task A_peer_that_has_gone_away_fails_rather_than_throws()
    {
        await using var alice = Node("alice");

        var bob = Node("bob");
        alice.AddPeer("bob", Reaching(bob));
        await bob.DisposeAsync();

        Assert.False(await alice.SendAsync(Packet("alice", "bob"), "bob"));
    }

    // ── Who is out there ──────────────────────────────────────────────────

    [Fact]
    public async Task A_new_node_knows_nobody()
    {
        await using var alice = Node("alice");

        Assert.Empty(alice.GetConnectedPeers());
    }

    [Fact]
    public async Task A_paired_peer_is_reachable_and_named()
    {
        await using var alice = Node("alice");
        await using var bob = Node("bob");

        alice.AddPeer("bob", Reaching(bob), "Bob's phone");

        var peer = Assert.Single(alice.GetConnectedPeers());
        Assert.Equal("bob", peer.Uhid);
        Assert.Equal("Bob's phone", peer.DisplayName);
        Assert.Equal("lan", peer.TransportType);
    }

    [Fact]
    public async Task Broadcasting_counts_who_actually_took_it()
    {
        await using var alice = Node("alice");
        await using var bob = Node("bob");
        await using var carol = Node("carol");

        alice.AddPeer("bob", Reaching(bob));
        alice.AddPeer("carol", Reaching(carol));
        alice.AddPeer("ghost", new IPEndPoint(IPAddress.Loopback, 1));

        Assert.Equal(2, await alice.BroadcastAsync(Packet("alice", "*")));
    }

    // ── Not being a fool about it ─────────────────────────────────────────

    /// <summary>
    /// A node hearing its own packet would route it, forward it, and count it as
    /// somebody else's — on a mesh that is how a loop starts.
    /// </summary>
    [Fact]
    public async Task A_node_ignores_a_packet_it_sent_itself()
    {
        await using var alice = Node("alice");

        var heard = 0;
        alice.PacketReceived += (_, _) => heard++;

        alice.AddPeer("alice", Reaching(alice));
        await alice.SendAsync(Packet("alice", "alice"), "alice");

        await Task.Delay(300);
        Assert.Equal(0, heard);
    }

    /// <summary>
    /// A length prefix from a stranger is an allocation instruction. Without a
    /// ceiling, one hostile number is an out-of-memory — and this listens on a
    /// port that anything on the same network can reach.
    /// </summary>
    [Fact]
    public async Task A_packet_larger_than_the_ceiling_is_refused_rather_than_sent()
    {
        await using var alice = Node("alice");
        await using var bob = Node("bob");

        alice.AddPeer("bob", Reaching(bob));

        var enormous = Packet("alice", "bob");
        enormous.Payload = new byte[LanMeshSender.MaxPacketBytes + 1024];

        Assert.False(await alice.SendAsync(enormous, "bob"));
    }

    /// <summary>
    /// Rubbish on the port — a scanner, another protocol, a truncated write — must
    /// not stop the node listening to everybody else.
    /// </summary>
    [Fact]
    public async Task Rubbish_on_the_port_does_not_bring_the_node_down()
    {
        await using var alice = Node("alice");
        await using var bob = Node("bob");

        using (var junk = new System.Net.Sockets.TcpClient())
        {
            await junk.ConnectAsync(IPAddress.Loopback, bob.ListenPort);
            await junk.GetStream().WriteAsync(new byte[] { 9, 9, 9, 9, 1, 2, 3 });
        }

        MeshPacket? arrived = null;
        bob.PacketReceived += (_, packet) => arrived = packet;

        alice.AddPeer("bob", Reaching(bob));
        await alice.SendAsync(Packet("alice", "bob", "still here"), "bob");

        Assert.True(await Within(TimeSpan.FromSeconds(5), () => arrived is not null));
    }

    /// <summary>
    /// No geohash. A geohash is a position, and a transport that reports one by
    /// default tells everybody on the network where somebody is.
    /// </summary>
    [Fact]
    public async Task It_does_not_announce_where_you_are()
    {
        await using var alice = Node("alice");

        Assert.Null(alice.LocalGeohash);
    }

    [Fact]
    public async Task Closing_it_twice_is_survivable()
    {
        var alice = Node("alice");

        await alice.DisposeAsync();
        await alice.DisposeAsync();
    }
}
