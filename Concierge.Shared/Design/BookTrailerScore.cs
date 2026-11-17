using System.Buffers.Binary;

namespace Concierge.Shared.Design;

/// <summary>Finishes a silent visual trailer with an original, locally generated score.</summary>
public interface IBookTrailerScore
{
    Task AddAsync(string videoPath, string outputPath, string scratchDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A small deterministic score keeps a book trailer audible without a cloud model,
/// a licensed recording, or access to the creator's music library.
/// </summary>
public sealed class BookTrailerScore(IEncoderRunner encoder) : IBookTrailerScore
{
    private const int Seconds = 30;
    private const int SampleRate = 44_100;
    private static readonly double[][] Chords =
    [
        [174.61, 207.65, 261.63],
        [138.59, 174.61, 207.65],
        [155.56, 196.00, 233.08],
        [130.81, 164.81, 196.00],
        [174.61, 207.65, 261.63]
    ];

    public async Task AddAsync(string videoPath, string outputPath, string scratchDirectory,
        CancellationToken cancellationToken = default)
    {
        var wavPath = Path.Combine(scratchDirectory, "original-score.wav");
        await WriteWavAsync(wavPath, cancellationToken).ConfigureAwait(false);
        var result = await encoder.RunAsync(
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", videoPath, "-i", wavPath,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "160k",
            "-t", Seconds.ToString(), "-movflags", "+faststart", outputPath
        ], cancellationToken).ConfigureAwait(false);
        if (!result.Ok || !File.Exists(outputPath) || new FileInfo(outputPath).Length < 1_000)
        {
            var lines = result.Said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var reason = lines.LastOrDefault(line => !line.Equals("Conversion failed!", StringComparison.OrdinalIgnoreCase))
                ?? "The audio encoder did not produce a trailer.";
            throw new InvalidDataException("Could not add sound to the trailer: " + reason);
        }
    }

    internal static async Task WriteWavAsync(string path, CancellationToken cancellationToken = default)
    {
        const int samples = Seconds * SampleRate;
        await using var output = File.Create(path);
        using (var header = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            header.Write("RIFF"u8);
            header.Write(36 + samples * 2);
            header.Write("WAVEfmt "u8);
            header.Write(16);
            header.Write((short)1);
            header.Write((short)1);
            header.Write(SampleRate);
            header.Write(SampleRate * 2);
            header.Write((short)2);
            header.Write((short)16);
            header.Write("data"u8);
            header.Write(samples * 2);
        }

        var buffer = new byte[8192];
        for (var offset = 0; offset < samples; offset += buffer.Length / 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length / 2, samples - offset);
            for (var i = 0; i < count; i++)
            {
                var t = (offset + i) / (double)SampleRate;
                var chord = Chords[Math.Min(Chords.Length - 1, (int)(t / 6))];
                var step = (int)(t / 0.75);
                var pluckTime = t - step * 0.75;
                var beatTime = t % 1.5;
                var pad = 0.10 * Math.Sin(Tau(chord[0], t))
                    + 0.065 * Math.Sin(Tau(chord[1], t))
                    + 0.05 * Math.Sin(Tau(chord[2], t));
                var note = chord[(step + step / 4) % 3] * 2;
                var pluck = Math.Exp(-4.0 * pluckTime)
                    * (0.17 * Math.Sin(Tau(note, t)) + 0.045 * Math.Sin(Tau(note * 2, t)));
                var bass = Math.Exp(-3.0 * beatTime)
                    * 0.13 * Math.Sin(Tau(chord[0] / 2, t));
                var fade = Math.Min(1, Math.Min(t / 1.2, (Seconds - t) / 1.8));
                var sample = (short)Math.Clamp((pad + pluck + bass) * fade * short.MaxValue,
                    short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2, 2), sample);
            }
            await output.WriteAsync(buffer.AsMemory(0, count * 2), cancellationToken).ConfigureAwait(false);
        }
    }

    private static double Tau(double frequency, double time) => 2 * Math.PI * frequency * time;
}
