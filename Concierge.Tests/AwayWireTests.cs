using System.Net;
using System.Net.Sockets;
using System.Text;
using Concierge.Away;

namespace Concierge.Tests;

/// <summary>
/// The wire between a device that is not here and the machine that does the making.
///
/// **Driven against a real listener, not a stub of the thing under test.** The watch
/// emulator has no speech recogniser — it says so on the face rather than appearing to
/// listen — so the microphone cannot drive this loop there. That is a fact about a system
/// image, and it is not a reason to claim the wire works without watching it work.
///
/// So the client is plain .NET rather than Android, and this stands a small HTTP server up
/// and makes it answer. What is being checked is the half that actually breaks: the address
/// found from a beacon, the key on the header, the sentence and its situation crossing
/// intact, and a failure reported as a failure.
/// </summary>
public sealed class AwayWireTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<string> _bodies = [];
    private readonly List<string?> _keys = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _where;

    public AwayWireTests()
    {
        var port = FreePort();
        _where = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_where}/");
        _listener.Start();

        _ = Task.Run(() => AnswerAsync(_stopping.Token));
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _stopping.Dispose();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AnswerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _keys.Add(context.Request.Headers["X-Concierge-Key"]);

            using (var body = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                _bodies.Add(await body.ReadToEndAsync(cancellationToken));
            }

            var said = context.Request.Url?.AbsolutePath switch
            {
                "/api/away/waiting" =>
                    """[{"id":"11111111-1111-1111-1111-111111111111","tool":"write_file","summary":"Write notes.md","risk":"Caution","askedAt":"2026-09-13T06:14:00Z"}]""",
                "/api/away/answer" => """{"answered":true}""",
                _ => """{"understood":true,"what":"Added a title","reply":"Added a title"}""",
            };

            var bytes = Encoding.UTF8.GetBytes(said);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();
        }
    }

    /// <summary>A client already told where the desk is, so discovery is a separate question.</summary>
    private AwayClient Pointed()
    {
        var client = new AwayClient { Key = "watchtest" };
        client.PointAt(_where);
        return client;
    }

    /// <summary>
    /// The whole point: a sentence leaves the wrist and an answer comes back in words.
    /// </summary>
    [Fact]
    public async Task A_sentence_crosses_and_an_answer_comes_back()
    {
        var answer = await Pointed().SayAsync(
            "add a heading that says Sports Day",
            new Situation(DateTimeOffset.UtcNow, Motion: "walking"));

        Assert.True(answer.Understood, answer.Reply);
        Assert.Equal("Added a title", answer.Reply);
    }

    /// <summary>
    /// The situation travels with it, and the time is the device's own — something said on a
    /// train and delivered forty minutes later is still something said on a train.
    /// </summary>
    [Fact]
    public async Task And_carries_what_the_device_knew()
    {
        var at = new DateTimeOffset(2026, 9, 13, 6, 14, 0, TimeSpan.Zero);

        await Pointed().SayAsync("make it night", new Situation(at, Motion: "walking", AmbientLux: 3));

        var sent = Assert.Single(_bodies);

        Assert.Contains("walking", sent, StringComparison.Ordinal);
        Assert.Contains("2026-09-13T06:14:00", sent, StringComparison.Ordinal);
        Assert.Contains("\"device\":\"watch\"", sent, StringComparison.Ordinal);
    }

    /// <summary>
    /// **And says nothing about what it does not know.** With no location, the fields are
    /// null rather than zero — a location of 0,0 is worse than no location, because whatever
    /// reads it next believes it.
    /// </summary>
    [Fact]
    public async Task And_never_invents_a_reading()
    {
        await Pointed().SayAsync("make it night", new Situation(DateTimeOffset.UtcNow));

        var sent = Assert.Single(_bodies);

        Assert.Contains("\"latitude\":null", sent, StringComparison.Ordinal);
        Assert.Contains("\"heartRate\":null", sent, StringComparison.Ordinal);
    }

    /// <summary>The key goes on every request, because the endpoint runs turns.</summary>
    [Fact]
    public async Task And_every_request_is_signed()
    {
        var client = Pointed();

        await client.SayAsync("make it night", new Situation(DateTimeOffset.UtcNow));
        await client.WaitingAsync();

        Assert.All(_keys, key => Assert.Equal("watchtest", key));
    }

    /// <summary>What is waiting comes back whole, because a wrist has to show what it decides on.</summary>
    [Fact]
    public async Task What_is_waiting_comes_back()
    {
        var waiting = Assert.Single(await Pointed().WaitingAsync());

        Assert.Equal("write_file", waiting.Tool);
        Assert.Equal("Write notes.md", waiting.Summary);
    }

    /// <summary>And answering one says whether it landed.</summary>
    [Fact]
    public async Task And_answering_one_says_whether_it_landed()
        => Assert.True(await Pointed().AnswerAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), allowed: true));

    /// <summary>
    /// **Nowhere to send it is an answer, not a hang.** A watch asking a question must not
    /// sit on a black face because nothing is running at home.
    /// </summary>
    [Fact]
    public async Task With_nothing_listening_it_says_so()
    {
        var client = new AwayClient();
        client.PointAt($"http://127.0.0.1:{FreePort()}");

        var answer = await client.SayAsync("make it night", new Situation(DateTimeOffset.UtcNow));

        Assert.False(answer.Understood);
        Assert.False(string.IsNullOrWhiteSpace(answer.Reply));
    }

    /// <summary>
    /// And with no beacon at all it gives up rather than waiting — measured, because a
    /// patience that is actually infinite looks identical to one that works until somebody
    /// is standing in a field with it.
    /// </summary>
    [Fact]
    public async Task And_looking_for_a_desk_that_is_not_there_gives_up()
    {
        var began = DateTimeOffset.UtcNow;

        var found = await new AwayClient().FindAsync(TimeSpan.FromMilliseconds(400));

        Assert.False(found);
        Assert.True(DateTimeOffset.UtcNow - began < TimeSpan.FromSeconds(5), "it waited far longer than it was told to");
    }
}
