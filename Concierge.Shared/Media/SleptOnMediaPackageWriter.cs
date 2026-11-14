using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Concierge.Shared.BookTrailers;
using Concierge.Shared.Rpc.Mcp;

namespace Concierge.Shared.Media;

public sealed record SleptOnMediaPackage(string Path, string Kind);

/// <summary>
/// Creates a local, versioned draft archive for SleptON! to import. This never
/// connects to SleptON!, signs in, submits, or publishes anything.
/// </summary>
public sealed class SleptOnMediaPackageWriter(string outputRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Task<SleptOnMediaPackage> BookAsync(BookTrailerDraft draft, bool rightsConfirmed,
        CancellationToken cancellationToken = default)
        => WriteAsync("book-trailer", rightsConfirmed,
            [
                (draft.VideoPath, "trailer.mp4", "video/mp4"),
                (draft.CaptionsPath, "captions.srt", "application/x-subrip"),
                (draft.ScriptPath, "script.json", "application/json"),
                (draft.CreditsPath, "credits.md", "text/markdown"),
                (draft.ManifestPath, "source-manifest.json", "application/json"),
            ], draft.SourceTitle, draft.SourceSha256, 30, cancellationToken);

    public Task<SleptOnMediaPackage> AudioAsync(NotaAudioDraft draft, bool rightsConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (draft.Audible == false)
            throw new InvalidOperationException("This audio draft is silent. Make an audible draft before preparing it for SleptON!.");
        var extension = draft.Format.ToLowerInvariant();
        var mediaType = extension switch
        {
            "wav" => "audio/wav", "flac" => "audio/flac", "mp3" => "audio/mpeg",
            _ => throw new ArgumentException("Only WAV, FLAC and MP3 drafts can be packaged.", nameof(draft)),
        };
        return WriteAsync("audio-draft", rightsConfirmed,
            [(draft.Path, $"audio.{extension}", mediaType)],
            Path.GetFileNameWithoutExtension(draft.Path), null, null, cancellationToken);
    }

    private async Task<SleptOnMediaPackage> WriteAsync(string kind, bool rightsConfirmed,
        IReadOnlyList<(string Source, string Entry, string MediaType)> sources,
        string title, string? sourceSha256, int? targetSeconds, CancellationToken cancellationToken)
    {
        if (!rightsConfirmed)
            throw new InvalidOperationException("Confirm that you have permission to share this work first.");
        if (string.IsNullOrWhiteSpace(outputRoot) || !Path.IsPathRooted(outputRoot))
            throw new ArgumentException("The package folder must be absolute.", nameof(outputRoot));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputRoot);
        var final = Path.Combine(outputRoot, $"{kind}-{Guid.NewGuid():N}.zip");
        var temporary = final + ".partial";
        try
        {
            var files = new List<object>();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (source, entryName, mediaType) in sources)
                {
                    if (!File.Exists(source) || new FileInfo(source).Length == 0)
                        throw new IOException($"The {entryName} draft file is missing or empty.");
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var zipped = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    long size = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await zipped.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        size += read;
                    }
                    files.Add(new { path = entryName, mediaType, bytes = size,
                        sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() });
                }

                var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifest.Open();
                await JsonSerializer.SerializeAsync(manifestStream, new
                {
                    schemaVersion = 1, kind, status = "draft", published = false,
                    rightsConfirmed, title, sourceSha256, targetSeconds,
                    createdUtc = DateTimeOffset.UtcNow, files,
                }, Json, cancellationToken);
            }
            File.Move(temporary, final);
            return new(final, kind);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }
}
