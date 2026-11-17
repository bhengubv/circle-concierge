using Concierge.Shared.Design;

namespace Concierge.Tests;

public sealed class LightMediaRenderTests
{
    [Fact]
    public async Task Makes_a_small_playable_clip_and_poster_from_existing_video()
    {
        var ffmpeg = FfmpegMediaExport.Find();
        Assert.True(File.Exists(ffmpeg), $"FFmpeg is required for this export test: {ffmpeg}");
        var root = Path.Combine(Path.GetTempPath(), "concierge-light-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mp4");
            var encoder = new ProcessEncoder(ffmpeg);
            var made = await encoder.RunAsync(
                ["-hide_banner", "-loglevel", "error", "-y",
                 "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=12:duration=8",
                 "-f", "lavfi", "-i", "sine=frequency=440:duration=8",
                 "-c:v", "h264", "-pix_fmt", "yuv420p", "-c:a", "aac", source]);
            Assert.True(made.Ok, made.Said);

            var result = await LightMediaRender.CreateAsync(source, Path.Combine(root, "exports"), encoder);
            Assert.True(File.Exists(result.ClipPath));
            Assert.True(File.Exists(result.PosterPath));
            Assert.InRange(new FileInfo(result.ClipPath).Length, 1_000, LightMediaRender.MaxClipBytes);
            Assert.InRange(new FileInfo(result.PosterPath).Length, 100, LightMediaRender.MaxPosterBytes);
            var decoded = await encoder.RunAsync(
                ["-hide_banner", "-loglevel", "error", "-i", result.ClipPath,
                 "-f", "null", "-"]);
            Assert.True(decoded.Ok, decoded.Said);
            var header = await File.ReadAllBytesAsync(result.PosterPath);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header[..8]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Refuses_a_missing_source()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => LightMediaRender.CreateAsync(
            "missing.mp4", Path.GetTempPath(), new ProcessEncoder(FfmpegMediaExport.Find())));
    }
}
