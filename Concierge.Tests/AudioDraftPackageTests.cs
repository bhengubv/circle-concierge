using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Concierge.Shared.Design;

namespace Concierge.Tests;

public sealed class AudioDraftPackageTests
{
    [Fact]
    public void Packages_a_rights_confirmed_M4A_with_exact_media_hash()
    {
        var folder = Path.Combine(Path.GetTempPath(), "concierge-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var audio = Path.Combine(folder, "original.m4a");
            var bytes = new byte[128];
            "ftyp"u8.CopyTo(bytes.AsSpan(4));
            File.WriteAllBytes(audio, bytes);
            var path = AudioDraftPackage.Create(audio, "My first song", Path.Combine(folder, "exports"), true);
            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(2, archive.Entries.Count);
            Assert.Equal(bytes, Read(archive, "audio.m4a"));
            using var document = JsonDocument.Parse(Read(archive, "manifest.json"));
            var manifest = document.RootElement;
            Assert.Equal("audio-draft", manifest.GetProperty("kind").GetString());
            Assert.Equal("draft", manifest.GetProperty("status").GetString());
            Assert.False(manifest.GetProperty("published").GetBoolean());
            Assert.True(manifest.GetProperty("rightsConfirmed").GetBoolean());
            var file = Assert.Single(manifest.GetProperty("files").EnumerateArray());
            Assert.Equal("audio.m4a", file.GetProperty("path").GetString());
            Assert.Equal("audio/mp4", file.GetProperty("mediaType").GetString());
            Assert.Equal(bytes.Length, file.GetProperty("bytes").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.GetProperty("sha256").GetString());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Refuses_to_package_without_rights_confirmation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "concierge-audio-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() =>
            AudioDraftPackage.Create("anything.m4a", "A song", folder, false));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Refuses_an_incorrect_media_signature_without_writing_a_package()
    {
        var folder = Path.Combine(Path.GetTempPath(), "concierge-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var audio = Path.Combine(folder, "wrong.m4a");
            File.WriteAllBytes(audio, new byte[128]);
            Assert.Throws<InvalidDataException>(() =>
                AudioDraftPackage.Create(audio, "A song", Path.Combine(folder, "exports"), true));
            Assert.False(Directory.Exists(Path.Combine(folder, "exports")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Creator_sound_export_can_become_a_real_audio_draft()
    {
        var ffmpeg = FfmpegMediaExport.Find();
        Assert.True(File.Exists(ffmpeg), $"FFmpeg is required for this end-to-end check: {ffmpeg}");
        var folder = Path.Combine(Path.GetTempPath(), "concierge-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var wav = Path.Combine(folder, "source.wav");
            var start = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono", "-t", "1", wav })
                start.ArgumentList.Add(argument);
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
            }
            var source = "data:audio/wav;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(wav));
            var document = DesignDocument.Blank(medium: DesignMedium.Sound).Add(
                DesignNode.New(DesignNodeKind.Sound, null, ("text", "Test tone"), ("src", source)));
            var m4a = Path.Combine(folder, "creator.m4a");
            var export = await new FfmpegMediaExport().SoundAsync(document, m4a);
            Assert.True(export.Ok, export.Problem);
            Assert.Null(export.Left);
            var package = AudioDraftPackage.Create(m4a, "Test tone", Path.Combine(folder, "exports"), true);
            using var archive = ZipFile.OpenRead(package);
            Assert.Equal(await File.ReadAllBytesAsync(m4a), Read(archive, "audio.m4a"));
            // An explicit acceptance run can hand this actual Concierge-produced
            // package to SleptON!'s independent importer tests.
            var sample = Environment.GetEnvironmentVariable("CONCIERGE_AUDIO_DRAFT_SAMPLE");
            if (!string.IsNullOrWhiteSpace(sample)) File.Copy(package, sample, overwrite: true);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static byte[] Read(ZipArchive archive, string path)
    {
        using var stream = archive.GetEntry(path)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
