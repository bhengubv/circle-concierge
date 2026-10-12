#if WINDOWS && DEBUG
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Concierge.Away;
using Concierge.Shared.Away;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Concierge.Hosting;

/// <summary>
/// Debug-only LAN bridge so the handheld can prove a prompt on the PC runtime.
/// This temporary proof endpoint is intentionally unauthenticated and must not be
/// included in release builds; replace it with paired TLS before shipping.
/// </summary>
public sealed class DesktopAwayHost(IServiceProvider services, ILogger<DesktopAwayHost> log) : IAsyncDisposable
{
    public const int Port = 5198;
    private WebApplication? _app;
    private CancellationTokenSource? _beaconStop;
    private IPAddress? _lanAddress;
    private readonly DevicePresenceDirectory _devices = new();

    public async Task StartAsync()
    {
        if (_app is not null)
            return;

        try
        {
            _lanAddress = FindPrivateLanAddress();
            if (_lanAddress is null)
            {
                log.LogWarning("Not starting the temporary bridge: no private Wi-Fi or Ethernet address is available.");
                return;
            }
            var broadcastAddress = FindPrivateLanBroadcastAddress(_lanAddress);

            var builder = WebApplication.CreateSlimBuilder();
            // Bind the workstation's loopback for its own browser and one private
            // interface for the paired mobile proof. Never use a wildcard/public
            // interface: this endpoint is deliberately temporary and unauthenticated.
            builder.WebHost.UseUrls($"http://127.0.0.1:{Port};http://{_lanAddress}:{Port}");
            var app = builder.Build();

            app.MapGet("/healthz", () => Results.Ok(new { status = "ready", service = "Concierge Desktop" }));
            app.MapGet("/api/away/devices", () => Results.Json(_devices.Snapshot()));
            app.MapPost("/api/away/devices", (DeviceHeartbeat device) =>
            {
                try { return Results.Json(_devices.Observe(device)); }
                catch (ArgumentException) { return Results.BadRequest(); }
            });
            app.MapGet("/api/away/capabilities", () => Results.Json(Away.Capabilities));
            app.MapGet("/api/away/diagnostics/prompt", () => Results.Json(
                services.GetService<Concierge.Ai.CircleAiWindowsServiceRuntime>()?.LastPromptDiagnostics));
            app.MapGet("/api/away/conversations", async (CancellationToken ct) => Results.Json(
                await WristReadModel.ReadAsync(services.GetRequiredService<Concierge.Shared.Chat.IConversationStore>(),
                    services.GetService<Concierge.Shared.Chat.BackgroundRuns>(), ct).ConfigureAwait(false)));
            app.MapGet("/api/away/conversations/{conversationId:guid}/recent", async (Guid conversationId, CancellationToken ct) => Results.Json(
                await services.GetRequiredService<Concierge.Shared.Chat.IConversationStore>().ReadRecentEventsAsync(conversationId, 100, ct).ConfigureAwait(false)));
            app.MapPost("/api/away/cancel", async (CancelBody body, CancellationToken ct) =>
            {
                if (body.RunId == Guid.Empty)
                    return Results.BadRequest(new { cancelled = false, message = "A run ID is required." });

                var cancelled = await Away.CancelRunAsync(body.RunId, ct).ConfigureAwait(false);
                return Results.Json(new { cancelled });
            });
            // Return Task<IResult> directly. An async lambda here can bind to the
            // RequestDelegate overload and silently discard the IResult body.
            app.MapPost("/api/away/say",
                (Delegate)(Func<HttpContext, Task<IResult>>)(context => SayAsync(context)));
            app.MapGet("/api/away/conversations/{conversationId:guid}/events", async (Guid conversationId, int? afterSeq, CancellationToken ct) =>
                Results.Json(await Away.ReadConversationEventsAfterAsync(
                    conversationId, afterSeq ?? -1, ct).ConfigureAwait(false)));
            app.MapPost("/api/away/conversations/{conversationId:guid}/events", async (
                Guid conversationId, AwayConversationEvents body, CancellationToken ct) =>
            {
                if (conversationId == Guid.Empty || body.Events is null
                    || body.Events.Count > Concierge.Shared.Chat.ConversationEventPage.MaximumSize)
                    return Results.BadRequest(new { synchronized = false, message = "Send a valid event batch of at most 512 events." });

                var synchronized = await Away.SynchronizeConversationEventsAsync(
                    conversationId, body.Events, ct).ConfigureAwait(false);
                return synchronized
                    ? Results.Ok(new { synchronized = true })
                    : Results.Conflict(new { synchronized = false, message = "Those events do not match this conversation." });
            });
            app.MapGet("/api/away/waiting", async (CancellationToken ct) =>
                Results.Json(await Away.WaitingAsync(ct).ConfigureAwait(false)));
            app.MapPost("/api/away/answer", async (AnswerBody body, CancellationToken ct) =>
            {
                var answered = await Away.AnswerAsync(body.Id, body.Allowed, ct).ConfigureAwait(false);
                return Results.Json(new { answered });
            });
            app.MapGet("/api/away/change", async (CancellationToken ct) =>
            {
                var change = await Away.LastChangeAsync(ct).ConfigureAwait(false);
                return change is null ? Results.NoContent() : Results.Json(change);
            });
            app.MapPost("/api/away/undo", async (CancellationToken ct) =>
            {
                var change = await Away.UndoAsync(ct).ConfigureAwait(false);
                return change is null ? Results.NoContent() : Results.Json(change);
            });

            await app.StartAsync().ConfigureAwait(false);
            _app = app;
            _beaconStop = new CancellationTokenSource();
            _ = AnnounceAsync(_lanAddress, broadcastAddress, _beaconStop.Token);
            log.LogWarning("Temporary unauthenticated Concierge LAN bridge is listening on port {Port}; Debug builds only.", Port);
        }
        catch (Exception failure)
        {
            log.LogError(failure, "Could not start the temporary Concierge LAN bridge.");
        }
    }

    private IAway Away => services.GetRequiredService<IAway>();

    private async Task<IResult> SayAsync(HttpContext context)
    {
        var body = await context.Request.ReadFromJsonAsync<SaidBody>(context.RequestAborted).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.Text))
            return Results.BadRequest(new { reply = "Nothing was said." });

        AwayPicture? picture = null;
        if (!string.IsNullOrWhiteSpace(body.PictureBase64))
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(body.PictureBase64); }
            catch (FormatException) { return Results.BadRequest(new { reply = "The picture was not valid." }); }
            if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024)
                return Results.BadRequest(new { reply = "Send a picture under 4 MB." });
            var mediaType = Concierge.Shared.Attachments.AttachmentKind.ImageMediaType(bytes);
            if (mediaType is null)
                return Results.BadRequest(new { reply = "Those bytes are not a picture this can read." });
            picture = new AwayPicture(body.PictureName ?? "picture", mediaType, bytes);
        }

        var said = new AwaySaid(body.Text, new AwayContext(
            body.At ?? DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(body.Device) ? "phone" : body.Device,
            body.Latitude, body.Longitude, body.Motion, body.HeartRate, body.AmbientLux), picture, body.ConversationId,
            body.ConversationEvents, body.RequestId, body.PermissionMode, body.ProjectId,
            body.ProjectWorkspaceId, body.ProjectContext, body.IncludeToolCatalog);
        var answer = await Away.SayAsync(said, context.RequestAborted).ConfigureAwait(false);
        if (answer.ConversationId is { } conversationId)
            answer = answer with { Events = await Away.ReadConversationEventsAsync(conversationId, context.RequestAborted).ConfigureAwait(false) };
        return Results.Json(answer);
    }

    private static async Task AnnounceAsync(IPAddress address, IPAddress? broadcastAddress, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        // Bind the sender to the same physical interface as the HTTP host. The
        // Windows default multicast route can otherwise select a virtual adapter.
        udp.Client.Bind(new IPEndPoint(address, 0));
        udp.EnableBroadcast = broadcastAddress is not null;
        var multicastDestination = new IPEndPoint(IPAddress.Parse(AwayBeacon.MulticastGroup), AwayBeacon.Port);
        var broadcastDestination = broadcastAddress is null ? null : new IPEndPoint(broadcastAddress, AwayBeacon.Port);
        var bytes = Encoding.UTF8.GetBytes($"{AwayBeacon.Prefix}http://{address}:{Port}");
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await udp.SendAsync(bytes, multicastDestination, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            if (broadcastDestination is not null)
            {
                try { await udp.SendAsync(bytes, broadcastDestination, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private static IPAddress? FindPrivateLanBroadcastAddress(IPAddress address)
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(network => network.OperationalStatus == OperationalStatus.Up))
        {
            var unicast = network.GetIPProperties().UnicastAddresses
                .FirstOrDefault(candidate => candidate.Address.Equals(address));
            if (unicast?.IPv4Mask is { } mask)
                return AwayBeacon.DirectedBroadcastAddress(address, mask);
        }
        return null;
    }

    private static IPAddress? FindPrivateLanAddress()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(network => network.OperationalStatus == OperationalStatus.Up
                         && network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                     // Prefer the real Wi-Fi path used by phones. Hyper-V/WSL
                     // virtual Ethernet adapters can also have private IPv4
                     // addresses, but they are not reachable from the handset.
                     .OrderByDescending(network => network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                     .ThenByDescending(network => network.GetIPProperties().GatewayAddresses
                         .Any(gateway => !gateway.Address.Equals(IPAddress.Any))))
        {
            var address = network.GetIPProperties().UnicastAddresses
                .Select(unicast => unicast.Address)
                .FirstOrDefault(IsPrivateIpv4);
            if (address is not null)
                return address;
        }
        return null;
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    public async ValueTask DisposeAsync()
    {
        if (_beaconStop is not null)
        {
            await _beaconStop.CancelAsync().ConfigureAwait(false);
            _beaconStop.Dispose();
        }
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record SaidBody(
        string? Text, string? Device, DateTimeOffset? At, double? Latitude, double? Longitude,
        string? Motion, int? HeartRate, double? AmbientLux, string? PictureBase64, string? PictureName,
        Guid? ConversationId, IReadOnlyList<Concierge.Shared.Chat.ConversationEvent>? ConversationEvents,
        Guid? RequestId = null,
        Concierge.Shared.Tools.ToolPermissionMode? PermissionMode = null,
        Guid? ProjectId = null,
        Guid? ProjectWorkspaceId = null,
        string? ProjectContext = null,
        bool IncludeToolCatalog = true);
    private sealed record AnswerBody(Guid Id, bool Allowed);
    private sealed record CancelBody(Guid RunId);
}
#endif
