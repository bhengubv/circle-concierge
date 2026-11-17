using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Concierge.Shared.Design;

/// <summary>A source-linked, private 30-second trailer accepted by SleptON!'s importer.</summary>
public static class BookTrailerPackage
{
    private const int ShotCount = 5;
    private const int ShotSeconds = 6;
    private const int MaxVideoBytes = 49 * 1024 * 1024;

    public static async Task<string> CreateAsync(BookTrailerSource source, string title,
        string exportRoot, IMediaExport encoder, bool rightsConfirmed,
        CancellationToken cancellationToken = default, IBookTrailerScore? score = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        if (!rightsConfirmed)
            throw new InvalidOperationException("Confirm that you have the rights to share this book first.");
        title = title?.Trim() ?? string.Empty;
        if (title.Length is < 1 or > 100 || title.Any(char.IsControl))
            throw new ArgumentException("Give the trailer a title of 1–100 characters.", nameof(title));
        if (source.Passages.Count < ShotCount || source.Sha256.Length != 64)
            throw new InvalidDataException("The book does not have five source-linked passages.");

        var selected = Enumerable.Range(0, ShotCount)
            .Select(index => source.Passages[index * (source.Passages.Count - 1) / (ShotCount - 1)])
            .ToArray();
        var cards = selected.Select(passage => CardLine(passage.Text)).ToArray();
        var design = DesignDocument.Blank("Night", DesignMedium.Motion);
        foreach (var card in cards)
            design = design.Add(DesignNode.New(DesignNodeKind.Frame, null,
                ("text", card), ("seconds", ShotSeconds.ToString()), ("move", "grow")));

        Directory.CreateDirectory(exportRoot);
        var stem = new string(title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (stem.Length == 0) stem = "trailer";
        if (stem.Length > 48) stem = stem[..48];
        var unique = $"{stem}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var videoPath = Path.Combine(exportRoot, unique + ".mp4");
        var scoredPath = Path.Combine(exportRoot, unique + "-scored.mp4");
        var scratchDirectory = Path.Combine(exportRoot, unique + "-score-work");
        var packagePath = Path.Combine(exportRoot, unique + ".zip");
        var partial = packagePath + ".partial";
        try
        {
            var rendered = await encoder.VideoAsync(design, videoPath, cancellationToken);
            if (!rendered.Ok || rendered.Left is not null)
                throw new InvalidDataException(rendered.Problem ?? rendered.Left ?? "The trailer export is incomplete.");
            Directory.CreateDirectory(scratchDirectory);
            await (score ?? new BookTrailerScore(Encoders.Runner))
                .AddAsync(videoPath, scoredPath, scratchDirectory, cancellationToken);
            var video = await File.ReadAllBytesAsync(scoredPath, cancellationToken);
            if (video.Length is < 16 or > MaxVideoBytes || !video.AsSpan(4, 4).SequenceEqual("ftyp"u8))
                throw new InvalidDataException("The trailer is not a valid MP4 within the 49 MB limit.");

            var script = JsonSerializer.SerializeToUtf8Bytes(new
            {
                title,
                targetSeconds = ShotCount * ShotSeconds,
                shots = selected.Select((passage, index) => new
                {
                    startMs = index * ShotSeconds * 1000,
                    durationMs = ShotSeconds * 1000,
                    onScreenText = cards[index],
                    sourceLocator = passage.Locator,
                    sourceExcerpt = passage.Text
                })
            });
            var sourceManifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                source = new { source.FileName, source.MediaType, source.Bytes, source.Sha256 },
                passages = selected.Select((passage, index) => new
                {
                    shot = index + 1, passage.Locator, excerpt = passage.Text
                })
            });
            var captions = Encoding.UTF8.GetBytes(string.Join("\n\n", cards.Select((card, index) =>
                $"{index + 1}\n{Timestamp(index * ShotSeconds)} --> {Timestamp((index + 1) * ShotSeconds)}\n{card}")) + "\n");
            var credits = Encoding.UTF8.GetBytes($"# {title}\n\nSource: {source.FileName}\n"
                + $"Source SHA-256: {source.Sha256}\n\n"
                + "Original ambient score generated locally by Concierge.\n\n"
                + "The creator confirmed the rights to share this source. "
                + "Concierge made a private trailer draft; SleptON! has not published it.\n");
            var files = new (string Path, string MediaType, byte[] Data)[]
            {
                ("trailer.mp4", "video/mp4", video),
                ("captions.srt", "application/x-subrip", captions),
                ("script.json", "application/json", script),
                ("credits.md", "text/markdown", credits),
                ("source-manifest.json", "application/json", sourceManifest)
            };
            if (files.Skip(1).Any(file => file.Data.Length is 0 or > 256 * 1024))
                throw new InvalidDataException("The trailer's source notes exceed the import limit.");
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                kind = "book-trailer",
                status = "draft",
                published = false,
                rightsConfirmed,
                title,
                createdUtc = DateTimeOffset.UtcNow,
                sourceSha256 = source.Sha256,
                targetSeconds = ShotCount * ShotSeconds,
                files = files.Select(file => new
                {
                    path = file.Path, mediaType = file.MediaType, bytes = file.Data.Length,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Data))
                })
            });
            using (var archive = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var entry = archive.CreateEntry(file.Path, file.Path == "trailer.mp4"
                        ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using var output = entry.Open();
                    output.Write(file.Data);
                }
                var metadata = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using var manifestOutput = metadata.Open();
                manifestOutput.Write(manifest);
            }
            if (new FileInfo(partial).Length > 50L * 1024 * 1024)
                throw new InvalidDataException("The trailer package exceeds SleptON!'s 50 MB limit.");
            File.Move(partial, packagePath);
            return packagePath;
        }
        finally
        {
            if (File.Exists(videoPath)) File.Delete(videoPath);
            if (File.Exists(scoredPath)) File.Delete(scoredPath);
            if (File.Exists(partial)) File.Delete(partial);
            if (Directory.Exists(scratchDirectory)) Directory.Delete(scratchDirectory, recursive: true);
        }
    }

    private static string CardLine(string passage)
    {
        var text = string.Join(' ', passage.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var words = text.Split(' ');
        var lines = new List<string> { string.Empty };
        foreach (var word in words)
        {
            var line = lines[^1];
            var next = line.Length == 0 ? word : line + " " + word;
            if (next.Length <= 28)
            {
                lines[^1] = next;
                continue;
            }
            if (lines.Count == 2) break;
            lines.Add(word.Length <= 28 ? word : word[..27] + "…");
        }
        var displayed = string.Join(' ', lines);
        if (displayed.Length < text.Length)
            lines[^1] = lines[^1].TrimEnd(' ', ',', ';', ':', '.') + "…";
        return string.Join('\n', lines);
    }

    private static string Timestamp(int seconds) => $"00:00:{seconds:00},000";
}
