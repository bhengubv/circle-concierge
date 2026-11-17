using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Concierge.Shared.Design;

/// <summary>Local work. Platform destinations and times are suggestions, never publishing instructions.</summary>
public sealed record SocialCampaignDraft
{
    public string Title { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public IReadOnlyList<SocialPostDraft> Posts { get; init; } = [];
}

public sealed record SocialPostDraft
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Brand { get; init; } = string.Empty;
    public string Copy { get; init; } = string.Empty;
    public IReadOnlyList<string> DestinationHints { get; init; } = [];
    public DateTimeOffset? SuggestedAtUtc { get; init; }
    public string AltText { get; init; } = string.Empty;
    /// <summary>Private local path. It is never written to the transfer package.</summary>
    public string? MediaPath { get; init; }
    public string? MediaType { get; init; }
}

/// <summary>Creates a checked, private campaign draft for SleptON!'s incoming queue.</summary>
public static class SocialCampaignPackage
{
    private const long MaxAssetBytes = 49L * 1024 * 1024;
    // SleptON!'s incoming mobile queue accepts at most 50 MiB per package.
    private const long MaxPackageBytes = 50L * 1024 * 1024;

    public static string Create(SocialCampaignDraft draft, string exportRoot, bool rightsConfirmed)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        if (!rightsConfirmed) throw new InvalidOperationException("Confirm the rights to share this campaign and its media first.");
        var title = Required(draft.Title, 100, "campaign title");
        var audience = Required(draft.Audience, 300, "audience");
        if (draft.Posts.Count is < 1 or > 20) throw new InvalidDataException("Add 1–20 posts before sending the campaign.");
        if (draft.Posts.Select(post => post.Id).Distinct().Count() != draft.Posts.Count ||
            draft.Posts.Any(post => post.Id == Guid.Empty))
            throw new InvalidDataException("Every post needs its own stable identity.");

        var assets = new List<(string Path, string Source, string MediaType, long Bytes, string Hash)>();
        var posts = new List<object>();
        foreach (var post in draft.Posts)
        {
            var brand = Required(post.Brand, 80, "brand");
            var copy = Required(post.Copy, 5000, "post copy", allowNewlines: true);
            var hints = post.DestinationHints.Select(hint => Required(hint, 40, "destination hint"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (hints.Length is < 1 or > 12) throw new InvalidDataException("Choose 1–12 destination hints for each post.");
            string? media = null;
            if (!string.IsNullOrWhiteSpace(post.MediaPath))
            {
                var type = post.MediaType ?? string.Empty;
                var extension = type switch
                {
                    "image/png" => ".png", "image/jpeg" => ".jpg",
                    "video/mp4" => ".mp4", "audio/mp4" => ".m4a",
                    _ => throw new InvalidDataException("Use a PNG, JPEG, MP4 video, or M4A audio file.")
                };
                var info = new FileInfo(post.MediaPath);
                if (!info.Exists || info.Length is < 16 or > MaxAssetBytes)
                    throw new InvalidDataException("A campaign media file is missing or exceeds 49 MB.");
                using var source = info.OpenRead();
                var header = new byte[16];
                if (source.Read(header) != header.Length || !MatchesSignature(type, header))
                    throw new InvalidDataException("A campaign media file does not match its declared format.");
                source.Position = 0;
                var hash = Convert.ToHexStringLower(SHA256.HashData(source));
                media = "media/" + post.Id.ToString("N") + extension;
                assets.Add((media, info.FullName, type, info.Length, hash));
                if (type.StartsWith("image/", StringComparison.Ordinal) || type == "video/mp4")
                    Required(post.AltText, 500, "image or video description", allowNewlines: true);
            }
            else if (post.MediaType is not null)
                throw new InvalidDataException("A media type was supplied without a media file.");
            posts.Add(new
            {
                id = post.Id, brand, text = copy, destinationHints = hints,
                suggestedAtUtc = post.SuggestedAtUtc,
                altText = post.AltText.Trim(), mediaPath = media
            });
        }

        var postBytes = JsonSerializer.SerializeToUtf8Bytes(new { posts });
        if (postBytes.Length > 256 * 1024) throw new InvalidDataException("The campaign copy is too large.");
        var files = new[] { new { path = "posts.json", mediaType = "application/json", bytes = (long)postBytes.Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(postBytes)) } }
            .Concat(assets.Select(asset => new { path = asset.Path, mediaType = asset.MediaType,
                bytes = asset.Bytes, sha256 = asset.Hash })).ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, kind = "social-campaign", status = "draft", published = false,
            rightsConfirmed, title, audience, createdUtc = DateTimeOffset.UtcNow, files
        });

        Directory.CreateDirectory(exportRoot);
        var package = Path.Combine(exportRoot, "social-campaign-" + Guid.NewGuid().ToString("N") + ".zip");
        var partial = package + ".partial";
        try
        {
            using (var archive = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                Write(archive, "posts.json", postBytes);
                foreach (var asset in assets)
                {
                    var entry = archive.CreateEntry(asset.Path, CompressionLevel.NoCompression);
                    using var source = File.OpenRead(asset.Source);
                    using var destination = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    long copied = 0;
                    int count;
                    while ((count = source.Read(buffer)) > 0)
                    {
                        copied += count;
                        if (copied > MaxAssetBytes) throw new InvalidDataException("A campaign media file changed during export.");
                        hash.AppendData(buffer, 0, count);
                        destination.Write(buffer, 0, count);
                    }
                    if (copied != asset.Bytes ||
                        !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), asset.Hash,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("A campaign media file changed during export.");
                }
                Write(archive, "manifest.json", manifest);
            }
            if (new FileInfo(partial).Length > MaxPackageBytes)
                throw new InvalidDataException("The campaign exceeds SleptON!'s 50 MB transfer limit.");
            File.Move(partial, package);
            return package;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static void Write(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static string Required(string? value, int max, string label, bool allowNewlines = false)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length is < 1 || result.Length > max ||
            result.Any(character => char.IsControl(character) &&
                (!allowNewlines || character is not ('\r' or '\n' or '\t'))))
            throw new InvalidDataException($"Enter a valid {label} of at most {max} characters.");
        return result;
    }

    private static bool MatchesSignature(string type, ReadOnlySpan<byte> bytes) => type switch
    {
        "image/png" => bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "video/mp4" or "audio/mp4" => bytes[4..8].SequenceEqual("ftyp"u8),
        _ => false
    };
}
