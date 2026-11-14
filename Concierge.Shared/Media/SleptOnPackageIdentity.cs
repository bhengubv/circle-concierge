using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Concierge.Shared.Media;

/// <summary>
/// Computes SleptON!'s content revision identity after checking every packaged
/// file against the manifest. ZIP metadata and creation time are not part of it.
/// </summary>
public static class SleptOnPackageIdentity
{
    private const long MaxPackageBytes = 50L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] BookFiles =
        ["trailer.mp4", "captions.srt", "script.json", "credits.md", "source-manifest.json"];

    public static async Task<string> ComputeAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(packagePath).Length > MaxPackageBytes)
            throw new InvalidDataException("The SleptON! package is too large.");
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The SleptON! package has no manifest.");
        if (manifestEntry.Length is <= 0 or > 64 * 1024)
            throw new InvalidDataException("The SleptON! package manifest is invalid.");
        await using var manifestStream = manifestEntry.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
        var root = manifest.RootElement;
        var kind = Text(root, "kind");
        var title = Text(root, "title");
        var sourceSha256 = root.TryGetProperty("sourceSha256", out var source)
            && source.ValueKind == JsonValueKind.String ? source.GetString() : null;
        var files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The SleptON! package file list is invalid.");
        var expected = kind switch
        {
            "book-trailer" => BookFiles,
            "audio-draft" when files.GetArrayLength() == 1
                && files[0].TryGetProperty("path", out var audio)
                && audio.GetString() is "audio.wav" or "audio.flac" or "audio.mp3"
                => [audio.GetString()!],
            _ => throw new InvalidDataException("The SleptON! package kind is unsupported."),
        };
        if (files.GetArrayLength() != expected.Length || archive.Entries.Count != expected.Length + 1
            || archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
            throw new InvalidDataException("The SleptON! package contains unexpected files.");
        var verifiedHashes = new List<string>();
        long extractedBytes = manifestEntry.Length;
        foreach (var path in expected)
        {
            var declared = files.EnumerateArray().Where(file => file.ValueKind == JsonValueKind.Object
                && file.TryGetProperty("path", out var name) && name.GetString() == path).ToArray();
            var entry = archive.GetEntry(path);
            if (declared.Length != 1 || entry is null || entry.Length <= 0
                || entry.Length != declared[0].GetProperty("bytes").GetInt64())
                throw new InvalidDataException($"The SleptON! package file {path} is invalid.");
            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaxPackageBytes)
                throw new InvalidDataException("The SleptON! package contents are too large.");
            var recorded = Text(declared[0], "sha256");
            await using var content = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunk = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
            {
                copied += read;
                if (copied > entry.Length || copied > MaxPackageBytes)
                    throw new InvalidDataException($"The SleptON! package file {path} is too large.");
                hash.AppendData(chunk, 0, read);
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (copied != entry.Length || !string.Equals(actual, recorded, StringComparison.Ordinal))
                throw new InvalidDataException($"The SleptON! package file {path} failed its hash check.");
            verifiedHashes.Add(actual);
        }
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Kind = kind, Title = title, SourceSha256 = sourceSha256, Files = verifiedHashes,
        }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(identity));
    }

    private static string Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"The SleptON! package is missing {name}.");
        return value.GetString()!;
    }
}
