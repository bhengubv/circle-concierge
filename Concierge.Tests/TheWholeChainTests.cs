using Concierge.Away;
using Concierge.Shared.Design;
using Concierge.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Concierge.Tests;

/// <summary>
/// The whole chain, in one run.
///
/// **Every piece of this was proven separately and the chain never was**, which is exactly
/// the shape this repository keeps finding: parts that each work, joined by something nobody
/// exercised. The wire tests drive the client against a stub listener. The outbox tests drive
/// the queue against a stub listener. The desk tests build `AwayDesk` directly. The endpoint
/// was proven by hand with curl, once, on a machine with a model on it.
///
/// So this is the real client, over real HTTP, to the real endpoints, into the real desk,
/// against a real design file — the only stub is the network being switched off, which is
/// the thing being tested.
/// </summary>
/// <remarks>
/// It stops short of the two ends that need hardware: a microphone on a wrist, and a model
/// that can answer. Both are named where they are missing rather than faked, because a test
/// that pretends to have them would report a working product on a machine that has neither.
/// What is covered is everything in between, which is all of the code.
/// </remarks>
public sealed class TheWholeChainTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"chain-{Guid.NewGuid():N}");

    private WebApplication? _desk;
    private string _where = string.Empty;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _port = FreePort();
        _where = $"http://127.0.0.1:{_port}";

        await OnAsync();
    }

    /// <summary>
    /// A free port, held only long enough to learn its number.
    ///
    /// The desk is built fresh every time it comes back rather than restarted, because a
    /// stopped host cannot be started again — its own token is already cancelled. Which is
    /// also closer to the thing being simulated: the machine at home was off, and now it is
    /// on, and it is not the same process.
    /// </summary>
    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Concierge is running at home.</summary>
    private async Task OnAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(_where);

        // The real desk over a real design file. Nothing about the away path is stubbed —
        // the only things left out are the model and the approver, and both are optional by
        // construction, which is the point of them being optional.
        builder.Services.AddSingleton<IDesignStore>(
            _ => new FileDesignStore(Path.Combine(_root, "design.json")));
        builder.Services.AddSingleton<Concierge.Shared.Away.IAway>(provider =>
            new Concierge.Shared.Away.AwayDesk(
                provider.GetRequiredService<IDesignStore>(),
                turn: () => null,
                conversations: null!,
                runtimes: []));

        _desk = builder.Build();
        _desk.MapConciergeAway();

        await _desk.StartAsync();
    }

    /// <summary>The lift doors close.</summary>
    private async Task OffAsync()
    {
        if (_desk is null)
        {
            return;
        }

        await _desk.StopAsync();
        await _desk.DisposeAsync();
        _desk = null;
    }

    public async Task DisposeAsync()
    {
        await OffAsync();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp folder left behind is nobody's problem.
        }
    }

    /// <summary>A watch, told where the desk is, with an outbox of its own.</summary>
    private AwayClient AWatch()
    {
        var watch = new AwayClient(new AwayOutbox(Path.Combine(_root, "outbox")));
        watch.PointAt(_where);
        return watch;
    }

    private string Design => File.Exists(Path.Combine(_root, "design.json"))
        ? File.ReadAllText(Path.Combine(_root, "design.json"))
        : string.Empty;

    /// <summary>
    /// **The morning this product is for.**
    ///
    /// Out of range, say two things, they are held. Signal comes back, they land in the order
    /// they were said. Glance at the wrist, see what changed. Say no. It goes back, and there
    /// is nothing left to take back.
    ///
    /// Every step is the real code. The only thing pretended is the lift.
    /// </summary>
    [Fact]
    public async Task Said_out_of_range_lands_later_and_can_be_taken_back()
    {
        var watch = AWatch();

        // ── In the lift ───────────────────────────────────────────────────
        await OffAsync();

        var kept = await watch.SayAsync(
            "add a heading that says Sports Day", new Situation(At("06:14")));

        Assert.False(kept.Understood, "it cannot have landed with nothing listening");

        await watch.SayAsync("make it night", new Situation(At("06:15")));

        Assert.Equal(2, watch.Waiting);
        Assert.DoesNotContain("Sports Day", Design, StringComparison.Ordinal);

        // ── Out of the lift ───────────────────────────────────────────────
        await OnAsync();

        Assert.Equal(2, await watch.FlushAsync());
        Assert.Equal(0, watch.Waiting);

        // Both landed, and the first one is in the file the desk actually keeps.
        Assert.Contains("Sports Day", Design, StringComparison.Ordinal);

        // ── The glance ────────────────────────────────────────────────────
        var changed = await watch.LastChangeAsync();

        Assert.NotNull(changed);
        Assert.False(string.IsNullOrWhiteSpace(changed!.What));
        Assert.True(changed.CanUndo, "there is nothing to say no to");

        // ── Saying no ─────────────────────────────────────────────────────
        var back = await watch.UndoAsync();

        Assert.NotNull(back);
        Assert.False(back!.CanUndo, "one step back is all there is with no canvas open");

        // And it is a design change, not a message: the look went back with it.
        Assert.DoesNotContain("\"Night\"", Design, StringComparison.Ordinal);

        // Nothing left to take back, said as nothing rather than as a failure.
        Assert.Null(await watch.UndoAsync());
    }

    /// <summary>
    /// A picture crosses whole, and is refused honestly when it is not one.
    ///
    /// The eye cannot be proven further than this on a machine with no vision model — what is
    /// checked is that the bytes arrive, are judged by what they are rather than what they are
    /// called, and that nothing pretends to have looked.
    /// </summary>
    [Fact]
    public async Task A_picture_crosses_and_bytes_that_are_not_one_are_refused()
    {
        var watch = AWatch();

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var looked = await watch.LookAsync(
            "what is this?", new Situation(At("06:20")), new Looked("wall.png", png));

        // With no model at all there is nothing to answer, and that is said rather than the
        // picture quietly becoming a design change.
        Assert.False(looked.Understood);
        Assert.False(string.IsNullOrWhiteSpace(looked.Reply));

        var rubbish = await watch.LookAsync(
            "what is this?",
            new Situation(At("06:21")),
            new Looked("wall.png", System.Text.Encoding.UTF8.GetBytes("not a picture at all")));

        Assert.False(rubbish.Understood);
        Assert.Contains("not a picture", rubbish.Reply!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **And a picture is never kept for later**, unlike a sentence. Megabytes on a small disk
    /// and a paid-for connection, and "what is this?" answered forty minutes after you walked
    /// away from the thing is not an answer anybody wanted.
    /// </summary>
    [Fact]
    public async Task But_a_picture_is_never_queued()
    {
        var watch = AWatch();

        await OffAsync();

        var looked = await watch.LookAsync(
            "what is this?", new Situation(At("06:20")), new Looked("wall.png", [1, 2, 3]));

        Assert.False(looked.Understood);
        Assert.Equal(0, watch.Waiting);

        await OnAsync();
    }

    private static DateTimeOffset At(string time)
        => DateTimeOffset.Parse(
            $"2026-09-25T{time}:00Z", System.Globalization.CultureInfo.InvariantCulture);
}
