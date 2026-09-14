using System.Net;
using System.Net.Sockets;
using System.Text;
using Concierge.Away;

namespace Concierge.Tests;

/// <summary>
/// What was said while there was nowhere to send it.
///
/// **Out of range is the normal case for a wrist.** A lift, a basement, a train, a walk with
/// the phone left at home — and a sentence lost to that is the product failing at exactly
/// the moment it claims to be useful. Every one of these drives the real client against a
/// real listener that is stopped and started, rather than a stub of the thing under test.
/// </summary>
public sealed class AwayOutboxTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}");
    private readonly List<string> _arrived = [];
    private readonly int _port;

    private HttpListener? _listener;
    private CancellationTokenSource? _stopping;

    public AwayOutboxTests() => _port = FreePort();

    public void Dispose()
    {
        Silence();

        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp folder left behind is nobody's problem.
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private string Where => $"http://127.0.0.1:{_port}";

    /// <summary>Concierge is running again.</summary>
    private void Listen()
    {
        _stopping = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{Where}/");
        _listener.Start();

        var listener = _listener;
        var stopping = _stopping.Token;

        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                using (var body = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    lock (_arrived)
                    {
                        _arrived.Add(body.ReadToEnd());
                    }
                }

                var said = Encoding.UTF8.GetBytes("""{"understood":true,"what":"Done","reply":"Done"}""");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = said.Length;
                await context.Response.OutputStream.WriteAsync(said, stopping);
                context.Response.Close();
            }
        }, stopping);
    }

    /// <summary>Concierge is not reachable — a lift, a basement, a laptop asleep.</summary>
    private void Silence()
    {
        _stopping?.Cancel();
        _listener?.Close();
        _listener = null;
        _stopping?.Dispose();
        _stopping = null;
    }

    private AwayClient Pointed()
    {
        var client = new AwayClient(new AwayOutbox(_folder));
        client.PointAt(Where);
        return client;
    }

    private IReadOnlyList<string> Arrived()
    {
        lock (_arrived)
        {
            return [.. _arrived];
        }
    }

    /// <summary>
    /// The whole point: said out of range, and not lost.
    /// </summary>
    [Fact]
    public async Task Something_said_with_nowhere_to_send_it_is_kept()
    {
        var client = Pointed();

        var answer = await client.SayAsync("add a heading that says Sports Day", At("06:14"));

        Assert.False(answer.Understood);
        Assert.Equal(1, client.Waiting);
        Assert.Empty(Arrived());
    }

    /// <summary>
    /// And it goes when the network comes back — in the order it was said, carrying the time
    /// it was said, not the time it landed.
    /// </summary>
    [Fact]
    public async Task And_goes_in_order_when_it_comes_back()
    {
        var client = Pointed();

        await client.SayAsync("add a heading that says Sports Day", At("06:14"));
        await client.SayAsync("make it night", At("06:15"));

        Assert.Equal(2, client.Waiting);

        Listen();

        Assert.Equal(2, await client.FlushAsync());
        Assert.Equal(0, client.Waiting);

        var arrived = Arrived();

        Assert.Equal(2, arrived.Count);
        Assert.Contains("Sports Day", arrived[0], StringComparison.Ordinal);
        Assert.Contains("make it night", arrived[1], StringComparison.Ordinal);

        // The device's own clock, kept in the file and sent on. Something said on a train and
        // delivered forty minutes later is still something said on a train, and stamping it
        // on arrival throws away the only reason to carry the situation at all.
        Assert.Contains("06:14:00", arrived[0], StringComparison.Ordinal);
        Assert.Contains("06:15:00", arrived[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// **A sentence said while others are waiting joins the back rather than jumping it.**
    ///
    /// Out of order is worse than late: "make it bigger" arriving before "add a title" is
    /// two changes in the wrong sequence, which on a design is a different design.
    /// </summary>
    [Fact]
    public async Task A_new_one_never_jumps_the_queue()
    {
        var client = Pointed();

        await client.SayAsync("first", At("06:14"));

        Listen();

        await client.SayAsync("second", At("06:15"));

        var arrived = Arrived();

        Assert.Equal(2, arrived.Count);
        Assert.Contains("\"text\":\"first\"", arrived[0], StringComparison.Ordinal);
        Assert.Contains("\"text\":\"second\"", arrived[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Delivering stops at the first one that will not go, so the next attempt does not put
    /// later sentences ahead of earlier ones.
    /// </summary>
    [Fact]
    public async Task Delivery_stops_at_the_first_one_that_will_not_go()
    {
        var client = Pointed();

        await client.SayAsync("first", At("06:14"));
        await client.SayAsync("second", At("06:15"));

        Assert.Equal(0, await client.FlushAsync());
        Assert.Equal(2, client.Waiting);
    }

    /// <summary>
    /// With nothing waiting and Concierge there, it goes straight out and the answer is the
    /// real one — a receipt is what you get when it could not, and the two must not look the
    /// same.
    /// </summary>
    [Fact]
    public async Task With_nothing_waiting_the_answer_is_the_real_one()
    {
        Listen();

        var answer = await Pointed().SayAsync("add a heading that says Sports Day", At("06:14"));

        Assert.True(answer.Understood);
        Assert.Equal("Done", answer.Reply);
        Assert.Single(Arrived());
    }

    /// <summary>
    /// **A refusal is not a network failure.** A watch that is not allowed will still not be
    /// allowed tomorrow, so saying it again every time the network returns would be a queue
    /// that never empties.
    /// </summary>
    [Fact]
    public async Task Being_turned_away_is_not_kept_for_later()
    {
        _stopping = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{Where}/");
        _listener.Start();

        var listener = _listener;
        var stopping = _stopping.Token;

        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                context.Response.Close();
            }
        }, stopping);

        var client = Pointed();
        var answer = await client.SayAsync("add a heading", At("06:14"));

        Assert.False(answer.Understood);
        Assert.Equal(0, client.Waiting);
        Assert.Contains("not allowed", answer.Reply!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An entry that has gone is forgotten, so it is not said twice.</summary>
    [Fact]
    public void What_went_is_forgotten()
    {
        var outbox = new AwayOutbox(_folder);

        var kept = outbox.Keep("add a heading", At("06:14"));

        Assert.Equal(1, outbox.Count());

        outbox.Done(kept.Id);

        Assert.Equal(0, outbox.Count());
        Assert.Empty(outbox.Waiting());
    }

    /// <summary>
    /// And an unreadable entry does not hold up everything said after it — the failure mode
    /// of a single queue file, and the reason this is many.
    /// </summary>
    [Fact]
    public void One_bad_entry_does_not_stop_the_rest()
    {
        var outbox = new AwayOutbox(_folder);

        outbox.Keep("first", At("06:14"));
        File.WriteAllText(Path.Combine(_folder, "00000000000000000000-deadbeef.json"), "{ not json");
        outbox.Keep("second", At("06:15"));

        var waiting = outbox.Waiting();

        Assert.Equal(2, waiting.Count);
        Assert.Equal("first", waiting[0].Text);
        Assert.Equal("second", waiting[1].Text);
    }

    private static Situation At(string time)
        => new(DateTimeOffset.Parse($"2026-09-14T{time}:00Z", System.Globalization.CultureInfo.InvariantCulture));
}
