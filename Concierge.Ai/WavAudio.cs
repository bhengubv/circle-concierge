using System.Buffers.Binary;

namespace Concierge.Ai;

/// <summary>
/// Raw samples, wrapped so something can play them.
///
/// `OnnxTtsEngine` hands back PCM — numbers, with no header saying what they are. Nothing
/// plays that: not a browser, not a phone, not the sound medium on the canvas. Forty-four
/// bytes in front of it and the same numbers are a .wav file everything opens.
///
/// Written here rather than taken from a package because it is forty-four bytes and a
/// dependency for it would be the larger cost.
/// </summary>
public static class WavAudio
{
    /// <summary>The header a player needs, in front of the samples it already has.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> pcm, int sampleRate, int channels, int bitsPerSample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bitsPerSample, 8);

        var blockAlign = channels * bitsPerSample / 8;
        var file = new byte[44 + pcm.Length];
        var at = file.AsSpan();

        "RIFF"u8.CopyTo(at[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(at.Slice(4, 4), (uint)(36 + pcm.Length));
        "WAVE"u8.CopyTo(at.Slice(8, 4));

        "fmt "u8.CopyTo(at.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(at.Slice(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(at.Slice(20, 2), 1); // PCM, uncompressed.
        BinaryPrimitives.WriteUInt16LittleEndian(at.Slice(22, 2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(at.Slice(24, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(at.Slice(28, 4), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(at.Slice(32, 2), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(at.Slice(34, 2), (ushort)bitsPerSample);

        "data"u8.CopyTo(at.Slice(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(at.Slice(40, 4), (uint)pcm.Length);

        pcm.CopyTo(at[44..]);

        return file;
    }
}
