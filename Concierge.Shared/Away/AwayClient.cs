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

    private string? _where;

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
    public void PointAt(string where) => _where = where;

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
    public async Task<AwayReply> SayAsync(string text, Situation situation, CancellationToken cancellationToken = default)
    {
        if (_where is null && !await FindAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new AwayReply(false, null, "Concierge is not on this network.");
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
                return new AwayReply(false, null, answered.StatusCode == HttpStatusCode.Unauthorized
                    ? "This watch is not allowed yet."
                    : $"Concierge could not answer ({(int)answered.StatusCode}).");
            }

            return await answered.Content
                .ReadFromJsonAsync<AwayReply>(cancellationToken: cancellationToken)
                .ConfigureAwait(false)
                ?? new AwayReply(false, null, "Nothing came back.");
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The address is forgotten so the next attempt looks again rather than retrying
            // a machine that has gone to sleep — which on a laptop is most of the day.
            _where = null;
            return new AwayReply(false, null, "Concierge did not answer.");
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
