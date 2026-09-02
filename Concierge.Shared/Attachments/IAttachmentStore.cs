using System.Security.Cryptography;
using System.Text.Json;

namespace Concierge.Shared.Attachments;

/// <summary>A stored attachment, referred to by content rather than carried inline.</summary>
/// <param name="Id">Content address — the same bytes always produce the same id.</param>
/// <param name="ContentType">MIME type, so a surface knows how to render it.</param>
/// <param name="FileName">The name it arrived with, for display and download.</param>
/// <param name="SizeBytes">How large it is.</param>
/// <param name="StoredAt">When it was first stored.</param>
public sealed record StoredAttachment(
    string Id,
    string ContentType,
    string FileName,
    long SizeBytes,
    DateTimeOffset StoredAt);

/// <summary>
/// Holds file and image bytes outside the conversation, so a message carries a reference
/// instead of the data.
/// </summary>
/// <remarks>
/// A photo inlined as base64 is paid for on every later request in that conversation, cannot
/// be deduplicated, and travels into the model's context whether or not it is still relevant.
/// Content addressing fixes all three: the same picture sent twice is stored once, and the
/// message holds a short id.
/// </remarks>
public interface IAttachmentStore
{
    /// <summary>Store bytes and return the reference to use in their place.</summary>
    Task<StoredAttachment> StoreAsync(
        byte[] content,
        string contentType,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>Read stored bytes back, or null when the id is unknown.</summary>
    Task<byte[]?> ReadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>What is known about a stored attachment, or null when the id is unknown.</summary>
    Task<StoredAttachment?> DescribeAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Attachments on the device's filesystem, addressed by the SHA-256 of their content.
/// </summary>
/// <remarks>
/// Files are sharded into subdirectories by the first two characters of the hash. A phone
/// gallery shared into the app can produce thousands of attachments, and some filesystems
/// slow markedly with that many entries in one directory.
/// </remarks>
public sealed class FileAttachmentStore : IAttachmentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;

    public FileAttachmentStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    /// <inheritdoc />
    public async Task<StoredAttachment> StoreAsync(
        byte[] content,
        string contentType,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
        {
            throw new ArgumentException("Attachment content is empty.", nameof(content));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var id = Convert.ToHexStringLower(SHA256.HashData(content));
        var (dataPath, metadataPath) = PathsFor(id);

        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);

        var attachment = new StoredAttachment(id, contentType, fileName, content.Length, DateTimeOffset.UtcNow);

        // Identical content is already here. Rewriting it would only reset its stored date
        // and risk tearing a file another message already references.
        if (!File.Exists(dataPath))
        {
            await File.WriteAllBytesAsync(dataPath, content, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(attachment, JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            return attachment;
        }

        return await DescribeAsync(id, cancellationToken).ConfigureAwait(false) ?? attachment;
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var (dataPath, _) = PathsFor(id);
        return File.Exists(dataPath)
            ? await File.ReadAllBytesAsync(dataPath, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc />
    public async Task<StoredAttachment?> DescribeAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var (_, metadataPath) = PathsFor(id);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<StoredAttachment>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // The bytes are still readable even when their description is not; losing the
            // filename is better than losing the picture.
            return null;
        }
    }

    private (string DataPath, string MetadataPath) PathsFor(string id)
    {
        // Shard by the first two hex characters: a shared photo gallery can produce
        // thousands of attachments, and some filesystems degrade badly in one flat directory.
        var shard = id.Length >= 2 ? id[..2] : "00";
        var directory = Path.Combine(_root, shard);
        return (Path.Combine(directory, $"{id}.bin"), Path.Combine(directory, $"{id}.json"));
    }
}
