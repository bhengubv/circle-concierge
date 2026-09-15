using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Concierge.Away;

/// <summary>What came back.</summary>
public sealed record AwayReply(bool Understood, string? What, string? Reply);

/// <summary>Something waiting on a person.</summary>
public sealed record AwayWaiting(Guid Id, string Tool, string Summary, string Risk, DateTimeOffset AskedAt);

/// <summary>
/// The machine that does the making, reached from a device that is not it.
///
/// **This is what was missing.** The watch opened Android's recogniser, drew what it heard
/// on the face, and stopped — one project reference, no network code of any kind. Which made
/// the whole head a dictaphone with a nice palette.
///
/// Nothing here knows what answers. Today it is the web head over HTTP; at Stage 3 the same
/// sentence rides the mesh. A device asks a question and gets a sentence back.
///
/// **Plain .NET rather than Android**, which is why it is here rather than in the watch
/// project. A phone will want the same thing, and — the half that matters more — it can be
/// driven against a running web head from a test on a machine that is neither.
/// </summary>
public sealed class AwayClient
{
    /// <summary>Where the desk shouts from. Matches AwayBeacon, and the two are one decision.</summary>
    private const string Group = "239.7.7.7";

    private const int Port = 47772;

    private const string Prefix = "concierge-away|";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <param name="outbox">Where unsent sentences are kept. Its own default when not given.</param>
    public AwayClient(AwayOutbox? outbox = null) => _outbox = outbox ?? new AwayOutbox();

    /// <summary>How many sentences are waiting to go.</summary>
    public int Waiting => _outbox.Count();

    private string? _where;

    /// <summary>What was said while there was nowhere to send it.</summary>
    private readonly AwayOutbox _outbox;

    /// <summary>
    /// The key, when there is one. Held rather than typed: a watch has no keyboard worth
    /// using, so this arrives with the build or not at all until pairing exists.
    /// </summary>
    public string? Key { get; set; } = Environment.GetEnvironmentVariable("CONCIERGE_API_KEY");

    /// <summary>Where it is, once it has been heard. Null until then.</summary>
    public string? Where => _where;

    /// <summary>
    /// Say where it is outright, instead of listening for it.
    ///
    /// For a head that was told — and for driving this against a real listener from a test,
    /// which is the only way the wire gets checked at all: a watch emulator has no speech
    /// recogniser, so the microphone cannot drive this loop there.
    /// </summary>
    public void PointAt(string where)
    {
        _where = where;
        _told = where;
    }

    /// <summary>
    /// An address given outright, remembered past a failure.
    ///
    /// A failed attempt forgets <see cref="Where"/> so the next one looks again rather than
    /// retrying a machine that has gone to sleep. **That was right for a discovered address
    /// and wrong for a given one**: a head told where the desk is would forget permanently
    /// the first time the laptop slept, and then spend six seconds listening for a beacon
    /// before every sentence for the rest of the day.
    /// </summary>
    private string? _told;

    /// <summary>
    /// Listen for the desk saying where it is.
    ///
    /// **Short, and it gives up rather than waiting.** A watch asking a question should not
    /// sit on a black screen because nothing is running at home; it should say so and let
    /// the person get on with their morning.
    /// </summary>
    public async Task<bool> FindAsync(TimeSpan? patience = null, CancellationToken cancellationToken = default)
    {
        using var listen = new UdpClient(AddressFamily.InterNetwork);

        try
        {
            listen.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listen.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            listen.JoinMulticastGroup(IPAddress.Parse(Group));
        }
        catch (SocketException)
        {
            // Some networks refuse multicast outright. Not knowing where the desk is is a
            // fact to report, not an exception to throw at somebody looking at their wrist.
            return false;
        }

        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        giveUp.CancelAfter(patience ?? TimeSpan.FromSeconds(6));

        try
        {
            while (!giveUp.IsCancellationRequested)
            {
                var heard = await listen.ReceiveAsync(giveUp.Token).ConfigureAwait(false);
                var said = Encoding.UTF8.GetString(heard.Buffer);

                if (said.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    _where = said[Prefix.Length..];
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Nothing shouted in time.
        }
        catch (SocketException)
        {
            // The network went away mid-listen. Same answer.
        }

        return false;
    }

    /// <summary>Say something, and hear what came of it.</summary>
    /// <summary>
    /// Say something.
    ///
    /// **Anything that cannot go now is kept and goes later.** A wrist is out of range
    /// constantly — a lift, a basement, a train, a walk with the phone left at home — and a
    /// sentence lost to that is the product failing at the moment it claims to be useful.
    ///
    /// Whatever is already waiting goes first, and a new sentence joins the back of the
    /// queue rather than jumping it. Out of order is worse than late: "make it bigger"
    /// arriving before "add a title" is two changes in the wrong sequence, which on a design
    /// is a different design.
    /// </summary>
    public async Task<AwayReply> SayAsync(string text, Situation situation, CancellationToken cancellationToken = default)
    {
        // Nothing is waiting, so this can go straight out and be answered properly. The
        // common case, and the only one where a person hears a real reply rather than a
        // receipt.
        if (_outbox.Count() == 0)
        {
            var straight = await TryOnceAsync(text, situation, cancellationToken).ConfigureAwait(false);

            if (straight.Reached)
            {
                return straight.Reply;
            }

            _outbox.Keep(text, situation);

            return new AwayReply(false, null, "Kept. It will go when Concierge is back.");
        }

        // Something is already waiting, so this joins the back before anything is tried.
        _outbox.Keep(text, situation);

        var delivered = await FlushAsync(cancellationToken).ConfigureAwait(false);

        return delivered == 0
            ? new AwayReply(false, null, $"Kept. {_outbox.Count()} waiting to go.")
            : new AwayReply(true, null, $"Sent {delivered}.");
    }

    /// <summary>
    /// Deliver what is waiting, oldest first, and stop at the first one that will not go.
    ///
    /// Stopping matters: carrying on past a failure would deliver later sentences ahead of
    /// earlier ones the next time the network returns, which is the one thing the queue
    /// exists to prevent.
    /// </summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        var sent = 0;

        foreach (var pending in _outbox.Waiting())
        {
            var went = await TryOnceAsync(pending.Text, pending.Situation, cancellationToken).ConfigureAwait(false);

            if (!went.Reached)
            {
                break;
            }

            _outbox.Done(pending.Id);
            sent++;
        }

        return sent;
    }

    /// <summary>
    /// One attempt.
    ///
    /// <c>Reached</c> is a different question from whether the answer was a happy one: a 401
    /// means this watch is not allowed and saying it again tomorrow will not help, so it is
    /// reached and not queued. Only genuinely not getting there is worth keeping for later.
    /// </summary>
    private async Task<(bool Reached, AwayReply Reply)> TryOnceAsync(
        string text, Situation situation, CancellationToken cancellationToken)
    {
        // What was given comes back before anything is listened for.
        _where ??= _told;

        if (_where is null && !await FindAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return (false, new AwayReply(false, null, "Concierge is not on this network."));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_where}/api/away/say")
            {
                Content = JsonContent.Create(new
                {
                    text,
                    device = "watch",
                    at = situation.At,
                    latitude = situation.Latitude,
                    longitude = situation.Longitude,
                    motion = situation.Motion,
                    heartRate = situation.HeartRate,
                    ambientLux = situation.AmbientLux,
                }),
            };

            Sign(request);

            using var answered = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!answered.IsSuccessStatusCode)
            {
                // The status is said in words rather than as a number. 401 on a wrist means
                // "this watch is not allowed", which is a thing a person can act on.
                return (true, new AwayReply(false, null, answered.StatusCode == HttpStatusCode.Unauthorized
                    ? "This watch is not allowed yet."
                    : $"Concierge could not answer ({(int)answered.StatusCode})."));
            }

            return (true, await answered.Content
                .ReadFromJsonAsync<AwayReply>(cancellationToken: cancellationToken)
                .ConfigureAwait(false)
                ?? new AwayReply(false, null, "Nothing came back."));
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The address is forgotten so the next attempt looks again rather than retrying
            // a machine that has gone to sleep — which on a laptop is most of the day.
            _where = null;
            return (false, new AwayReply(false, null, "Concierge did not answer."));
        }
    }

    /// <summary>What is waiting on a person, oldest first.</summary>
    public async Task<IReadOnlyList<AwayWaiting>> WaitingAsync(CancellationToken cancellationToken = default)
    {
        if (_where is null && !await FindAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_where}/api/away/waiting");
            Sign(request);

            using var answered = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!answered.IsSuccessStatusCode)
            {
                return [];
            }

            return await answered.Content
                .ReadFromJsonAsync<List<AwayWaiting>>(cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            _where = null;
            return [];
        }
    }

    /// <summary>
    /// Allow or refuse one.
    ///
    /// False means it was no longer waiting — a turn that gave up while somebody walked to
    /// the kitchen. Reported rather than swallowed, because a face claiming it allowed a call
    /// that never ran is the defect this whole product exists to remove.
    /// </summary>
    public async Task<bool> AnswerAsync(Guid id, bool allowed, CancellationToken cancellationToken = default)
    {
        if (_where is null)
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_where}/api/away/answer")
            {
                Content = JsonContent.Create(new { id, allowed }),
            };

            Sign(request);

            using var answered = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!answered.IsSuccessStatusCode)
            {
                return false;
            }

            using var said = JsonDocument.Parse(
                await answered.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return said.RootElement.TryGetProperty("answered", out var was) && was.GetBoolean();
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private void Sign(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(Key))
        {
            request.Headers.Add("X-Concierge-Key", Key);
        }
    }
}

/// <summary>
/// What a device was looking at.
///
/// The eye is whatever has a camera — a phone, a tablet — and the desk is what does the
/// looking at it. A watch has none worth the name, which is exactly why this lives here
/// rather than on the watch: one road in, whichever device found the thing.
/// </summary>
public sealed record Looked(string FileName, byte[] Bytes);

/// <summary>
/// What the watch knew when it heard something.
///
/// Every field but the time is optional and stays null when the sensor did not answer. A
/// blank is a blank — a location of 0,0 is worse than no location, because whatever reads
/// it next will believe it.
/// </summary>
public sealed record Situation(
    DateTimeOffset At,
    double? Latitude = null,
    double? Longitude = null,
    string? Motion = null,
    int? HeartRate = null,
    double? AmbientLux = null);
