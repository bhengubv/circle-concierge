using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Concierge.Shared.Design;

/// <summary>A private, source-verified audio draft for SleptON!; never publishes it.</summary>
public static class AudioDraftPackage
{
    private const long MaxMediaBytes = 49L * 1024 * 1024;

    public static string Create(string audioPath, string title, string exportRoot, bool rightsConfirmed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        if (!rightsConfirmed)
            throw new InvalidOperationException("Confirm that you have the rights to share this audio first.");
        title = title?.Trim() ?? string.Empty;
        if (title.Length is < 1 or > 100 || title.Any(char.IsControl))
            throw new ArgumentException("Give the audio a title of 1–100 characters.", nameof(title));
        if (!string.Equals(Path.GetExtension(audioPath), ".m4a", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The audio draft needs a Concierge M4A export.");
        var info = new FileInfo(audioPath);
        if (!info.Exists || info.Length is < 16 or > MaxMediaBytes)
            throw new InvalidDataException("The M4A export is missing or exceeds the draft size limit.");

        string hash;
        using (var input = File.OpenRead(audioPath))
        {
            Span<byte> header = stackalloc byte[8];
            if (input.Read(header) != header.Length || !header[4..].SequenceEqual("ftyp"u8))
                throw new InvalidDataException("The audio file is not an M4A container.");
            input.Position = 0;
            hash = Convert.ToHexStringLower(SHA256.HashData(input));
        }

        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            kind = "audio-draft",
            status = "draft",
            published = false,
            rightsConfirmed,
            title,
            createdUtc = DateTimeOffset.UtcNow,
            files = new[] { new { path = "audio.m4a", mediaType = "audio/mp4", bytes = info.Length, sha256 = hash } }
        });

        Directory.CreateDirectory(exportRoot);
        var safe = new string(title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (safe.Length == 0) safe = "audio";
        if (safe.Length > 48) safe = safe[..48];
        var path = Path.Combine(exportRoot, $"{safe}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.zip");
        var partial = path + ".partial";
        try
        {
            using (var archive = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                var media = archive.CreateEntry("audio.m4a", CompressionLevel.NoCompression);
                using (var source = File.OpenRead(audioPath))
                using (var destination = media.Open())
                    source.CopyTo(destination);
                var metadata = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using var output = metadata.Open();
                output.Write(manifest);
            }
            File.Move(partial, path);
            return path;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
