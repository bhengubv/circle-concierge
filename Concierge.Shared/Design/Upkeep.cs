using System.Text.Json;
using Concierge.Shared.Web;
using Microsoft.Extensions.Hosting;

namespace Concierge.Shared.Design;

/// <summary>What upkeep should do, and how often.</summary>
/// <param name="On">Whether it runs at all. Off until somebody turns it on.</param>
/// <param name="EveryHours">How often, in hours.</param>
/// <param name="Folder">The music folder to look after, or empty for none.</param>
/// <param name="Shows">Whether to check the followed podcasts for new episodes.</param>
public sealed record UpkeepPlan(bool On = false, int EveryHours = 24, string Folder = "", bool Shows = true);

/// <summary>What the last run found.</summary>
/// <param name="When">When it ran.</param>
/// <param name="Found">What it found, one line each.</param>
public sealed record UpkeepReport(DateTimeOffset When, IReadOnlyList<string> Found);

/// <summary>
/// Looking after a library on a schedule.
///
/// Antra runs downloads on a schedule. **This runs the checking on a schedule and never the
/// downloading**, and that is a decision rather than a shortfall: everything here that
/// reaches the network or writes a file asks first, and a scheduled task runs at three in the
/// morning with nobody there to ask. A background job that downloaded would be the one thing
/// in this product that acts without anybody agreeing to it.
///
/// So it looks, and it leaves a note: new episodes of the shows being followed, and anything
/// that has arrived in the library twice. Acting on either is one sentence the next morning,
/// with the card in front of somebody who can say no.
///
/// **Off until somebody turns it on**, like the mesh radio and for the same reason: a
/// schedule that starts itself is a program deciding to use somebody's network and disk.
/// </summary>
public sealed class Upkeep
{
    private readonly string? _path;
    private readonly IWebAccess? _web;
    private readonly PodcastFollows _follows;
    private readonly MusicLibrary _library;

    /// <param name="path">Where the plan and the last report are kept.</param>
    /// <param name="web">For reading feeds. Null on a head with no web access; shows are skipped.</param>
    /// <param name="follows">The shows being followed.</param>
    /// <param name="library">The library reader.</param>
    public Upkeep(
        string? path = null,
        IWebAccess? web = null,
        PodcastFollows? follows = null,
        MusicLibrary? library = null)
    {
        _path = path;
        _web = web;
        _follows = follows ?? new PodcastFollows();
        _library = library ?? new MusicLibrary();
    }

    /// <summary>What it has been told to do.</summary>
    public UpkeepPlan Plan
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            {
                return new UpkeepPlan();
            }

            try
            {
                return JsonSerializer.Deserialize<Kept>(File.ReadAllText(_path))?.Plan ?? new UpkeepPlan();
            }
            catch (JsonException)
            {
                return new UpkeepPlan();
            }
            catch (IOException)
            {
                return new UpkeepPlan();
            }
        }
    }

    /// <summary>What the last run found, or null when it has never run.</summary>
    public UpkeepReport? Last
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<Kept>(File.ReadAllText(_path))?.Last;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>Tells it what to do. Returns false on a head with nowhere to keep it.</summary>
    public bool Tell(UpkeepPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Bounded so a mistake cannot turn into a program checking somebody's network every
        // few seconds, and cannot turn into one that never runs at all.
        return Write(new Kept(
            plan with { EveryHours = Math.Clamp(plan.EveryHours, 1, 24 * 14) },
            Last));
    }

    /// <summary>
    /// Runs it now, whatever the schedule says.
    ///
    /// Everything is caught: this runs unattended, and an upkeep pass that throws would take
    /// down whatever is hosting it for a podcast feed having a bad morning.
    /// </summary>
    public async Task<UpkeepReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var plan = Plan;
        var found = new List<string>();

        if (plan.Shows && _web is { } web)
        {
            foreach (var feed in _follows.All())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var (show, problem) = await new Podcasts(web).ReadAsync(feed, cancellationToken)
                        .ConfigureAwait(false);

                    if (show is null)
                    {
                        found.Add($"{feed} could not be read: {problem}");
                        continue;
                    }

                    // Newer than the last run, which is what "new" means to somebody who was
                    // told about the others yesterday.
                    var since = Last?.When ?? DateTimeOffset.MinValue;

                    var fresh = show.Episodes
                        .Where(episode => episode.When is { } when && when > since)
                        .ToList();

                    if (fresh.Count > 0)
                    {
                        found.Add(
                            $"{show.Title}: {fresh.Count} new — "
                            + string.Join("; ", fresh.Take(5).Select(episode => episode.Title)));
                    }
                }
                catch (Exception problem) when (problem is not OperationCanceledException)
                {
                    found.Add($"{feed} could not be checked: {problem.Message}");
                }
            }
        }

        if (plan.Folder.Length > 0 && Directory.Exists(plan.Folder))
        {
            try
            {
                var tracks = await _library.ReadAsync(plan.Folder, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var same = MusicLibrary.Duplicates(tracks);

                if (same.Count > 0)
                {
                    found.Add(
                        $"{same.Count} recordings are in {plan.Folder} more than once. Nothing "
                        + "has been removed.");
                }
            }
            catch (Exception problem) when (problem is not OperationCanceledException)
            {
                found.Add($"{plan.Folder} could not be checked: {problem.Message}");
            }
        }

        var report = new UpkeepReport(DateTimeOffset.Now, found);

        Write(new Kept(plan, report));

        return report;
    }

    private bool Write(Kept kept)
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var beside = _path + ".writing";

            File.WriteAllText(beside, JsonSerializer.Serialize(kept));
            File.Move(beside, _path, overwrite: true);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record Kept(UpkeepPlan Plan, UpkeepReport? Last);
}

/// <summary>
/// The thing that actually wakes up.
///
/// A timer rather than anything cleverer: this runs once a day at most, and a scheduler with
/// its own persistence and catch-up semantics would be a great deal of machinery for "check
/// whether there is anything new".
///
/// **A missed run is missed.** If the app was closed at the hour, the next start checks how
/// long it has been and runs if it is overdue — which is what somebody expects — rather than
/// running every missed interval in a row.
/// </summary>
public sealed class UpkeepService(Upkeep upkeep) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;

    /// <summary>How often it wakes to *consider* running. The plan decides whether it does.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(
            _ => _ = ConsiderAsync(),
            null,
            TimeSpan.FromMinutes(1),
            Tick);

        return Task.CompletedTask;
    }

    private async Task ConsiderAsync()
    {
        try
        {
            var plan = upkeep.Plan;

            if (!plan.On)
            {
                return;
            }

            var last = upkeep.Last?.When;

            if (last is { } when && DateTimeOffset.Now - when < TimeSpan.FromHours(plan.EveryHours))
            {
                return;
            }

            await upkeep.RunAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Unattended by definition. Whatever went wrong is already written into the
            // report where it can be read; throwing here would take the host down with it.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stopping.Dispose();
    }
}

/// <summary>
/// Upkeep, as something a model can set and read.
///
/// One tool rather than three, because there is one thing here: what should be looked after,
/// how often, and what did it find. Splitting that into set / see / run would be three names
/// for one idea.
/// </summary>
public sealed class UpkeepToolSource(Upkeep upkeep) : Tools.IAgentToolSource
{
    /// <inheritdoc />
    public IReadOnlyList<Tools.IAgentTool> Tools => [new LookAfterIt(upkeep)];

    private sealed class LookAfterIt(Upkeep upkeep) : Tools.IAgentTool
    {
        public string Name => "upkeep";

        public string Description =>
            "See or change what is looked after on a schedule — new episodes of followed shows, "
            + "and duplicates in a music folder — and read what the last check found. "
            + "It only ever looks; it never downloads or removes anything.";

        public System.Text.Json.Nodes.JsonNode? ArgumentsSchema
            => new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "object",
                ["properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["on"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Turn the schedule on or off.",
                    },
                    ["every_hours"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "How often to check, in hours.",
                    },
                    ["folder"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "A music folder to watch for duplicates.",
                    },
                    ["now"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Run the check now rather than waiting.",
                    },
                },
            };

        public bool IsReadOnly => false;

        public async Task<Tools.AgentToolResult> InvokeAsync(
            System.Text.Json.Nodes.JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var plan = upkeep.Plan;
            var changed = false;

            if (arguments?["on"] is System.Text.Json.Nodes.JsonValue onValue
                && onValue.TryGetValue<bool>(out var on))
            {
                plan = plan with { On = on };
                changed = true;
            }

            if (arguments?["every_hours"] is System.Text.Json.Nodes.JsonValue everyValue)
            {
                var hours = everyValue.TryGetValue<int>(out var whole) ? whole
                    : everyValue.TryGetValue<double>(out var number) ? (int)number
                    : 0;

                if (hours > 0)
                {
                    plan = plan with { EveryHours = hours };
                    changed = true;
                }
            }

            if (arguments?["folder"] is System.Text.Json.Nodes.JsonValue folderValue
                && folderValue.TryGetValue<string>(out var folder) && folder.Trim().Length > 0)
            {
                // Checked now rather than at three in the morning, when nobody is reading.
                if (!Directory.Exists(folder.Trim()))
                {
                    return new Tools.AgentToolResult(
                        false, string.Empty, $"There is no folder at {folder.Trim()}.");
                }

                plan = plan with { Folder = folder.Trim() };
                changed = true;
            }

            if (changed && !upkeep.Tell(plan))
            {
                return new Tools.AgentToolResult(
                    false, string.Empty, "This head has nowhere to keep a schedule.");
            }

            var running = arguments?["now"] is System.Text.Json.Nodes.JsonValue nowValue
                && nowValue.TryGetValue<bool>(out var now) && now;

            var report = running
                ? await upkeep.RunAsync(cancellationToken).ConfigureAwait(false)
                : upkeep.Last;

            var said = new List<string>
            {
                plan.On
                    ? $"Checking every {plan.EveryHours} hours."
                    : "The schedule is off, so nothing is checked until it is turned on.",
                plan.Folder.Length > 0 ? $"Watching {plan.Folder} for duplicates." : "No folder is watched.",
                plan.Shows ? "Checking followed shows for new episodes." : "Not checking shows.",
            };

            if (report is null)
            {
                said.Add("It has not run yet.");
            }
            else
            {
                said.Add($"Last checked {report.When:yyyy-MM-dd HH:mm}.");
                said.AddRange(report.Found.Count == 0 ? ["Nothing new."] : report.Found);
            }

            return new Tools.AgentToolResult(true, string.Join(Environment.NewLine, said));
        }
    }
}

/// <summary>Registering it.</summary>
public static class UpkeepRegistration
{
    /// <summary>
    /// Adds the upkeep schedule and the thing that wakes it.
    ///
    /// The hosted service is registered whether or not the schedule is on, because the
    /// schedule is a file somebody can change while the app is running — a service that was
    /// only registered when it was already on would mean turning it on did nothing until the
    /// next restart, with nothing anywhere saying so.
    /// </summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddConciergeUpkeep(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(services);

        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .TryAddSingleton(services, sp => new Upkeep(
                string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "upkeep.json"),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetService<IWebAccess>(sp),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetService<PodcastFollows>(sp)));

        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .TryAddEnumerable(
                services,
                Microsoft.Extensions.DependencyInjection.ServiceDescriptor
                    .Singleton<Tools.IAgentToolSource, UpkeepToolSource>());

        Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions
            .AddHostedService<UpkeepService>(services);

        return services;
    }
}
