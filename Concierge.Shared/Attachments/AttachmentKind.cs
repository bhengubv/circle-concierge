namespace Concierge.Shared.Attachments;

/// <summary>
/// Telling a picture from a text file, before it reaches the prompt.
///
/// This exists because the workspace did not do it. Every attachment was
/// folded into the prompt as <c>Encoding.UTF8.GetString(bytes)</c>, pictures
/// included — and the camera hand-off attaches a JPEG, so photographing
/// something sent the model a few hundred kilobytes of decoded binary and
/// asked it what was in the picture.
///
/// Content first, filename second. A file called <c>notes.txt</c> holding a
/// PNG is still a PNG, and a model asked to read it will be handed noise
/// either way; the magic bytes are the only thing that actually knows.
/// </summary>
public static class AttachmentKind
{
    /// <summary>
    /// The media type of an image, or null if the bytes are not one this
    /// recognises. Only the formats every vision provider accepts — a format
    /// that has to be converted first is not a format this can promise.
    /// </summary>
    public static string? ImageMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 6
            && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
        {
            return "image/gif";
        }

        // WebP is RIFF....WEBP — the size sits between the two markers.
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>True when these bytes are a picture.</summary>
    public static bool IsImage(ReadOnlySpan<byte> bytes) => ImageMediaType(bytes) is not null;

    /// <summary>
    /// Whether the bytes look like text a model can usefully read.
    ///
    /// A NUL byte is the giveaway: no encoding a person types produces one, and
    /// every binary format is full of them. Checking a prefix is enough — a
    /// file that is text for its first few kilobytes and binary afterwards is
    /// not a case worth the whole scan.
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return true;
        }

        if (IsImage(bytes))
        {
            return false;
        }

        var prefix = bytes.Length > 4096 ? bytes[..4096] : bytes;
        return prefix.IndexOf((byte)0) < 0;
    }
}
