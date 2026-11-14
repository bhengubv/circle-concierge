using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Concierge.Shared.BookTrailers;
using Concierge.Shared.Rpc.Mcp;
using Concierge.Shared.Media;

namespace Concierge.Tests;

public sealed class SleptOnMediaPackageTests
{
    [Fact]
    public async Task Book_trailer_package_is_portable_verifiable_and_never_contains_the_source_book()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-handoff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var draftFolder = Path.Combine(root, "draft");
        Directory.CreateDirectory(draftFolder);
        var video = Write(draftFolder, "trailer.mp4", "video bytes");
        var captions = Write(draftFolder, "captions.srt", "captions");
        var script = Write(draftFolder, "script.json", "{\"shots\":[]}");
        var credits = Write(draftFolder, "credits.md", "credits");
        var sourceManifest = Write(draftFolder, "manifest.json", "{\"status\":\"draft\"}");
        var draft = new BookTrailerDraft(draftFolder, video, sourceManifest, captions, script, credits,
            new string('a', 64), "The Lantern", "source-excerpts");
        var writer = new SleptOnMediaPackageWriter(Path.Combine(root, "handoff"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.BookAsync(draft, rightsConfirmed: false));
        var result = await writer.BookAsync(draft, rightsConfirmed: true);

        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Equal(new[] { "captions.srt", "credits.md", "manifest.json", "script.json", "source-manifest.json", "trailer.mp4" },
            zip.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());
        using var manifest = ReadManifest(zip);
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("book-trailer", manifest.RootElement.GetProperty("kind").GetString());
        Assert.Equal("draft", manifest.RootElement.GetProperty("status").GetString());
        Assert.False(manifest.RootElement.GetProperty("published").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("rightsConfirmed").GetBoolean());
        Assert.Equal(30, manifest.RootElement.GetProperty("targetSeconds").GetInt32());
        Assert.Equal(draft.SourceSha256, manifest.RootElement.GetProperty("sourceSha256").GetString());
        Assert.All(manifest.RootElement.GetProperty("files").EnumerateArray(), file =>
        {
            var entry = zip.GetEntry(file.GetProperty("path").GetString()!)!;
            using var stream = entry.Open();
            Assert.Equal(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                file.GetProperty("sha256").GetString());
        });
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var reader = new StreamReader(manifestStream);
        Assert.DoesNotContain(root, await reader.ReadToEndAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audio_package_requires_rights_and_contains_only_the_verified_draft()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-handoff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var audio = Write(root, "song.wav", "audio bytes");
        var writer = new SleptOnMediaPackageWriter(Path.Combine(root, "handoff"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AudioAsync(new(audio, "wav", 11), false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AudioAsync(new NotaAudioDraft(audio, "wav", 11, false), true));
        var result = await writer.AudioAsync(new NotaAudioDraft(audio, "wav", 11, true), true);

        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Equal(new[] { "audio.wav", "manifest.json" }, zip.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());
        using var manifest = ReadManifest(zip);
        Assert.Equal("audio-draft", manifest.RootElement.GetProperty("kind").GetString());
        Assert.True(manifest.RootElement.GetProperty("rightsConfirmed").GetBoolean());
        Assert.Equal("audio/wav", manifest.RootElement.GetProperty("files")[0].GetProperty("mediaType").GetString());
        Assert.False(manifest.RootElement.GetProperty("published").GetBoolean());
    }

    private static string Write(string folder, string name, string data)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, data);
        return path;
    }

    private static JsonDocument ReadManifest(ZipArchive zip)
    {
        using var stream = zip.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(stream);
    }
}
