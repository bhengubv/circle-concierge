namespace Concierge.Shared.Context;

/// <summary>The outcome of offering a large output to the spill store.</summary>
/// <param name="Preview">What the model and the conversation see.</param>
/// <param name="WasSpilled">Whether the full text was written somewhere.</param>
/// <param name="Path">Where the full text lives, or null when it was small enough to inline.</param>
/// <param name="TotalChars">How long the original was.</param>
public sealed record SpilledOutput(string Preview, bool WasSpilled, string? Path, int TotalChars);

/// <summary>
/// Holds output too large to carry inline, returning a short preview that says where the
/// rest can be found.
/// </summary>
/// <remarks>
/// Distinct from pruning: a pruner discards the middle permanently, while spilling keeps
/// everything and moves it out of memory. A user can still open the file; the model is not
/// asked to read a megabyte to find one line.
/// </remarks>
public interface ISpillStore
{
    /// <summary>Store the output if it is large, and return what should be shown instead.</summary>
    Task<SpilledOutput> SpillAsync(string? output, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes oversized output to a directory, one file per spill.
/// </summary>
/// <remarks>
/// Failing to write is not fatal. On a device that is out of space the output still has to
/// reach the user in some form, so a failed spill degrades to a truncated preview rather
/// than throwing into the tool loop.
/// </remarks>
public sealed class FileSpillStore : ISpillStore
{
    private const int PreviewChars = 200;

    private readonly string _root;
    private readonly int _maxInlineChars;

    /// <param name="root">Directory that holds spill files.</param>
    /// <param name="maxInlineChars">Outputs at or below this length are returned as-is.</param>
    public FileSpillStore(string root, int maxInlineChars = 50_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInlineChars);

        _root = root;
        _maxInlineChars = maxInlineChars;
    }

    /// <inheritdoc />
    public async Task<SpilledOutput> SpillAsync(string? output, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(output))
        {
            return new SpilledOutput(string.Empty, WasSpilled: false, Path: null, TotalChars: 0);
        }

        if (output.Length <= _maxInlineChars)
        {
            return new SpilledOutput(output, WasSpilled: false, Path: null, TotalChars: output.Length);
        }

        var head = output[..Math.Min(PreviewChars, output.Length)];

        try
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, $"output-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, output, cancellationToken).ConfigureAwait(false);

            var preview = $"{head}\n… {output.Length} characters total, full output saved to {Path.GetFileName(path)} …";
            return new SpilledOutput(preview, WasSpilled: true, path, output.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Nowhere to put it. The user still needs the beginning of their output, so
            // degrade to a truncated preview and say plainly that the rest is gone.
            var preview = $"{head}\n… {output.Length} characters total, and the full output could not be saved …";
            return new SpilledOutput(preview, WasSpilled: false, Path: null, output.Length);
        }
    }
}
