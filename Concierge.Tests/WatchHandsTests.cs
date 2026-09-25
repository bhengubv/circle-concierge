using Concierge.Away;
using Concierge.Shared.Design;
using Concierge.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Concierge.Tests;

/// <summary>
/// What a wrist actually reads, for every way a thing can go.
///
/// **None of this could be checked until it moved.** Sending, flushing, answering and undoing
/// lived inside the Android `Activity`, where nothing can render one — so five methods
/// deciding what a person reads were beyond reach, and "the watch needs hardware to prove"
/// covered far more than it had earned. Android's recogniser is hardware. None of this is.
///
/// `WatchFace` made the same argument for which screen shows and it was only half applied.
/// This is the other half.
/// </summary>
public sealed class WatchHandsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hands-{Guid.NewGuid():N}");

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

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task OnAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(_where);

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

    private WatchHands AWatch()
    {
        var client = new AwayClient(new AwayOutbox(Path.Combine(_root, "outbox")));
        client.PointAt(_where);
        return new WatchHands(client);
    }

    private static Situation Now() => new(DateTimeOffset.UtcNow);

    /// <summary>Something said that lands reads as what the desk called it.</summary>
    [Fact]
    public async Task What_lands_reads_as_what_the_desk_called_it()
    {
        var update = await AWatch().SaidAsync("add a heading that says Sports Day", Now());

        Assert.Equal("Added a title", update.Say);
        Assert.NotNull(update.Changed);
    }

    /// <summary>
    /// **And something that cannot go reads differently.** The one thing this screen must
    /// never do is look the same whether it worked or not.
    /// </summary>
    [Fact]
    public async Task And_something_that_cannot_go_reads_differently()
    {
        var hands = AWatch();
        await OffAsync();

        var update = await hands.SaidAsync("add a heading that says Sports Day", Now());

        Assert.NotNull(update.Say);
        Assert.Contains("Kept", update.Say!, StringComparison.OrdinalIgnoreCase);

        await OnAsync();
    }

    /// <summary>
    /// A face coming on delivers what was held and says how many — and says nothing at all
    /// when there was nothing, rather than reporting a delivery of zero.
    /// </summary>
    [Fact]
    public async Task Coming_back_on_delivers_and_says_so()
    {
        var hands = AWatch();
        await OffAsync();

        await hands.SaidAsync("first", Now());
        await hands.SaidAsync("make it night", Now());

        await OnAsync();

        var update = await hands.LookedAgainAsync();

        Assert.NotNull(update.Say);
        Assert.Contains("2", update.Say!, StringComparison.Ordinal);

        // Nothing held now, so nothing is said about it.
        Assert.Null((await hands.LookedAgainAsync()).Say);
    }

    /// <summary>
    /// Taking it back says what stands now, and taking back nothing says that.
    /// </summary>
    [Fact]
    public async Task Taking_it_back_says_what_stands_now()
    {
        var hands = AWatch();

        await hands.SaidAsync("add a heading that says Sports Day", Now());

        var back = await hands.TookItBackAsync();

        Assert.Equal("Back as you left it", back.Say);
        Assert.Null(back.Changed);

        var again = await hands.TookItBackAsync();

        Assert.Contains("nothing to go back to", again.Say!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **Answering something that has already gone says so**, rather than reporting "allowed"
    /// for a call that never ran — the approvals badge that always said two, on the one
    /// screen with room for a single sentence.
    /// </summary>
    [Fact]
    public async Task Answering_something_that_has_gone_says_so()
    {
        var update = await AWatch().AnsweredAsync(Guid.NewGuid(), allowed: true);

        Assert.NotNull(update.Say);
        Assert.Contains("already gone", update.Say!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And with the desk unreachable, answering is not claimed to have worked either.
    /// </summary>
    [Fact]
    public async Task And_with_nothing_listening_it_is_not_claimed_either()
    {
        var hands = AWatch();
        await OffAsync();

        var update = await hands.AnsweredAsync(Guid.NewGuid(), allowed: true);

        Assert.NotNull(update.Say);
        Assert.Contains("already gone", update.Say!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(update.Waiting);

        await OnAsync();
    }
}
