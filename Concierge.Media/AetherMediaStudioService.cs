using Aether.Media.Core;
using Aether.Media.Core.Models;
using Concierge.Shared;

namespace Concierge.Media;

/// <summary>
/// Aether-media-backed implementation of <see cref="IMediaStudioService"/>. Wraps an
/// <see cref="IMediaLibrary"/> (in-memory by default) and seeds it with two demo content
/// items so the Concierge UI has something concrete to render until a real catalogue is wired.
/// </summary>
public sealed class AetherMediaStudioService : IMediaStudioService
{
    private readonly IMediaLibrary _library;
    private int _seeded;

    public AetherMediaStudioService(IMediaLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    public MediaStudioSnapshot GetSnapshot()
    {
        EnsureSeeded();

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

    private void EnsureSeeded()
    {
        if (Interlocked.CompareExchange(ref _seeded, 1, 0) != 0)
        {
            return;
        }

        const string creator = "K2X8M-RT5VP";
        var now = DateTimeOffset.UtcNow;

        _library.AddAsync(new MediaContent(
            ContentHash: Guid.NewGuid().ToString("N"),
            Title: "Concierge onboarding (intro)",
            DurationMs: 138_000,
            Codec: "h264",
            ContentType: "video/mp4",
            CreatorUhid: creator,
            SizeBytes: 18_200_000,
            CreatedAtMs: now.AddMinutes(-23).ToUnixTimeMilliseconds(),
            ThumbnailHash: null,
            Tags: ["onboarding", "intro"])).GetAwaiter().GetResult();

        _library.AddAsync(new MediaContent(
            ContentHash: Guid.NewGuid().ToString("N"),
            Title: "Safe approvals — walkthrough",
            DurationMs: 95_000,
            Codec: "opus",
            ContentType: "audio/ogg",
            CreatorUhid: creator,
            SizeBytes: 1_400_000,
            CreatedAtMs: now.AddMinutes(-7).ToUnixTimeMilliseconds(),
            ThumbnailHash: null,
            Tags: ["walkthrough", "approvals"])).GetAwaiter().GetResult();
    }
}
