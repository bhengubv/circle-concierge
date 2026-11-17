namespace Concierge.Shared.Design;

/// <summary>A compact clip and still image made from an existing video for sharing.</summary>
public sealed record LightMediaResult(string ClipPath, string PosterPath);

/// <summary>
/// Makes an original pixel-style preview using the encoder already present on the device.
/// The input is a finished video: this export does not invent scenes or copy ASCILINE code.
/// </summary>
public static class LightMediaRender
{
    public const long MaxClipBytes = 2 * 1024 * 1024;
    public const long MaxPosterBytes = 1024 * 1024;
    private const string PixelFilter =
        "fps=10,scale=120:68:flags=area,scale=480:272:flags=neighbor,format=yuv420p";

    public static async Task<LightMediaResult> CreateAsync(string sourcePath, string exportRoot,
        IEncoderRunner encoder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        ArgumentNullException.ThrowIfNull(encoder);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The video to preview was not found.", sourcePath);
        if (!encoder.Here) throw new InvalidOperationException("Video export is unavailable on this device.");

        Directory.CreateDirectory(exportRoot);
        var stem = new string(Path.GetFileNameWithoutExtension(sourcePath)
            .Where(char.IsLetterOrDigit).Take(40).ToArray());
        if (stem.Length == 0) stem = "video";
        var prefix = $"{stem}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var clip = Path.Combine(exportRoot, prefix + "-preview.mp4");
        var poster = Path.Combine(exportRoot, prefix + "-poster.png");
        var clipPartial = clip + ".partial.mp4";
        var posterPartial = poster + ".partial.png";
        try
        {
            // ffmpeg-kit on Android need not carry an H.264 encoder. Use the same
            // portable preference order as the main Creator export.
            var available = await encoder.RunAsync(["-hide_banner", "-encoders"], cancellationToken)
                .ConfigureAwait(false);
            var videoCodec = available.Said.Contains(" libx264", StringComparison.Ordinal)
                ? "libx264"
                : available.Said.Contains(" libopenh264", StringComparison.Ordinal)
                    ? "libopenh264"
                    : "mpeg4";
            var video = await encoder.RunAsync(
                ["-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath,
                 "-map", "0:v:0", "-map", "0:a:0?", "-t", "6", "-vf", PixelFilter,
                 "-c:v", videoCodec, "-b:v", "220k", "-maxrate", "300k", "-bufsize", "600k",
                 "-c:a", "aac", "-b:a", "48k", "-ac", "1", "-movflags", "+faststart",
                 clipPartial], cancellationToken).ConfigureAwait(false);
            if (!video.Ok || !File.Exists(clipPartial))
                throw new InvalidDataException("Could not make the light media clip: " + LastLine(video.Said));
            if (new FileInfo(clipPartial).Length is < 1_000 or > MaxClipBytes)
                throw new InvalidDataException("The light media clip exceeds its 2 MB limit.");

            var still = await encoder.RunAsync(
                ["-hide_banner", "-loglevel", "error", "-y", "-ss", "0.1", "-i", sourcePath,
                 "-vf", "scale=120:68:flags=area,scale=480:272:flags=neighbor",
                 "-frames:v", "1", "-update", "1", posterPartial],
                cancellationToken).ConfigureAwait(false);
            if (!still.Ok || !File.Exists(posterPartial))
                throw new InvalidDataException("Could not make the light media poster: " + LastLine(still.Said));
            if (new FileInfo(posterPartial).Length is < 100 or > MaxPosterBytes)
                throw new InvalidDataException("The light media poster exceeds its 1 MB limit.");

            File.Move(clipPartial, clip);
            File.Move(posterPartial, poster);
            return new LightMediaResult(clip, poster);
        }
        catch
        {
            foreach (var path in new[] { clipPartial, posterPartial, clip, poster })
                if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    private static string LastLine(string said)
        => said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "The encoder returned no detail.";
}
