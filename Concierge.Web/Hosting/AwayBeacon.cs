using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Concierge.Web.Hosting;

/// <summary>
/// Shouting where Concierge is, so a watch does not have to be told.
///
/// **There is no keyboard worth using on a 192dp face.** Typing an address there is the
/// kind of step that makes a thing never get set up, so the desk says where it is and the
/// wrist listens. The same shape as the mesh's own discovery, which has shouted a UHID and
/// a port on 239.7.7.7 since it was written.
/// </summary>
/// <remarks>
/// **Off unless a key is set**, and that is the whole of the decision. Announcing where
/// Concierge lives on somebody's network is a thing a host should do on purpose, and an
/// endpoint that runs turns with the whole tool catalogue in reach should not be advertised
/// while it is unguarded. Tying the two together means the careful answer is the default and
/// there is one switch rather than two to get wrong.
///
/// It only sends. Nothing is listened for here, and the port the beacon names is the web
/// head's own, already accepting connections — this opens nothing that was shut.
/// </remarks>
public sealed class AwayBeacon(IConfiguration configuration, IServer server, ILogger<AwayBeacon> log)
    : BackgroundService
{
    /// <summary>The group nodes shout on. Administratively scoped — it does not leave the network.</summary>
    public const string Group = "239.7.7.7";

    /// <summary>One past the mesh's discovery port, so the two never read each other's traffic.</summary>
    public const int Port = 47772;

    /// <summary>What a beacon starts with, so anything else on the group is ignored.</summary>
    public const string Prefix = "concierge-away|";

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ShouldAnnounce(configuration))
        {
            log.LogInformation(
                "Not announcing this Concierge on the network: no API key is set. "
                + "Set CONCIERGE_API_KEY to let a watch find it.");
            return;
        }

        var where = Reachable(server);

        if (where is null)
        {
            log.LogWarning("Nothing to announce: no address this machine can be reached on was found.");
            return;
        }

        using var shout = new UdpClient(AddressFamily.InterNetwork);
        var to = new IPEndPoint(IPAddress.Parse(Group), Port);
        var said = Encoding.UTF8.GetBytes(Prefix + where);

        log.LogInformation("Announcing Concierge at {Where} on {Group}:{Port}.", where, Group, Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await shout.SendAsync(said, to, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is SocketException or ObjectDisposedException)
            {
                // A network that comes and goes is the ordinary case for a laptop. Saying so
                // once per beat would fill a log with nothing; the next beat tries again.
            }

            await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether to announce at all. Explicit configuration wins; otherwise it follows whether
    /// there is a key, because an unguarded endpoint should not be advertised.
    /// </summary>
    public static bool ShouldAnnounce(IConfiguration configuration)
    {
        if (bool.TryParse(
                Environment.GetEnvironmentVariable("CONCIERGE_AWAY_ANNOUNCE")
                ?? configuration["Away:Announce"],
                out var asked))
        {
            return asked;
        }

        return !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("CONCIERGE_API_KEY") ?? configuration["Auth:ApiKey"]);
    }

    /// <summary>
    /// An address a watch on the same network can actually reach.
    ///
    /// Loopback is skipped rather than announced: it is the address this machine uses to talk
    /// to itself, and a watch that believed it would spend a beacon interval failing to
    /// connect to its own operating system.
    /// </summary>
    private static string? Reachable(IServer server)
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;

        if (addresses is null)
        {
            return null;
        }

        var mine = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

        if (mine is null)
        {
            return null;
        }

        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                continue;
            }

            // http only. A self-signed certificate on a watch is a dialog nobody can read at
            // that size, and this is a local network — the key is what guards it.
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
            {
                continue;
            }

            return $"http://{mine}:{uri.Port}";
        }

        return null;
    }
}
