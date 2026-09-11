using Aether.Media.Core;
using Aether.Media.Core.Models;
using Concierge.Shared;

namespace Concierge.Media;

/// <summary>
/// Aether-media-backed implementation of <see cref="IMediaStudioService"/>. Wraps an
/// <see cref="IMediaLibrary"/>, in-memory by default.
///
/// **It used to seed two demo items, and the UI never said they were demo items.** The
/// Business APIs room drew "Media library 2" over two named files — "Concierge onboarding
/// (intro)", 2:18, h264, 18.2 MB, and "Safe approvals — walkthrough", 1:35, opus — with
/// creation times recomputed on every launch, so they always looked freshly added twenty-three
/// and seven minutes ago. No such files exist and nothing plays them.
///
/// The seed is gone, on the owner's say-so. The room already had a correct empty state and
/// had simply never been able to reach it: it now says "Nothing in it yet", which is true.
///
/// Worth keeping the reason rather than only the change. Demo data exists so a screen has
/// something to show, and the cost is that the screen stops being a report and becomes an
/// illustration of one — with nothing on it marking which. That is the same defect as an
/// approvals badge that always said two, arrived at from the friendlier direction.
/// </summary>
public sealed class AetherMediaStudioService : IMediaStudioService
{
    private readonly IMediaLibrary _library;

    public AetherMediaStudioService(IMediaLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    public MediaStudioSnapshot GetSnapshot()
    {
        // The default in-memory library wraps Task.FromResult — no SynchronizationContext is
        // captured, so .GetAwaiter().GetResult() never deadlocks and completes synchronously.
        // If a future library implementation performs real async I/O, switch this to async and
        // propagate through to the UI render path rather than blocking the request thread.
        var items = _library.GetAllAsync().GetAwaiter().GetResult();

        var recent = items.Take(8).Select(item => new MediaStudioItem(
            Title: item.Title,
            Codec: item.Codec,
            ContentType: item.ContentType,
            FormattedDuration: item.FormattedDuration,
            CreatorTag: item.CreatorUhid,
            Tags: item.Tags)).ToList();

        var kinds = items
            .Select(item => item.IsVideo ? "video" : item.IsAudio ? "audio" : "other")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var version = typeof(IMediaLibrary).Assembly.GetName().Version?.ToString() ?? "unknown";

        return new MediaStudioSnapshot(
            Engine: "aether-media",
            EngineVersion: version,
            LibraryItemCount: items.Count,
            RecentItems: recent,
            SupportedKinds: kinds,
            Summary: "Aether Media library is active; production hosts replace InMemoryMediaLibrary with a persistent store.");
    }

}
