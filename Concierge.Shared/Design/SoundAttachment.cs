namespace Concierge.Shared.Design;

/// <summary>Audio formats that the Creator can carry into a portable Sound draft.</summary>
public static class SoundAttachment
{
    public const long MaxBytes = BringItIn.BiggestSound;

    public static string? MediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) return "audio/wav";
        if (bytes.Length >= 8 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return "audio/m4a";
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual("OggS"u8)) return "audio/ogg";
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual("ID3"u8)) return "audio/mpeg";
        if (bytes.Length >= 2 && bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0) return "audio/mpeg";
        return null;
    }
}
