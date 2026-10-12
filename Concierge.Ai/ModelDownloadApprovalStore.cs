using System.Text.Json;

namespace Concierge.Ai;

/// <summary>
/// The explicit, per-device approval to fetch one selected model and its staged tiers.
/// A surviving record means the transfer was interrupted, not that the person declined it.
/// </summary>
internal sealed record ModelDownloadApproval(
    int SchemaVersion,
    string SelectedModelId,
    IReadOnlyList<string> StagedModelIds,
    DateTimeOffset AcceptedAtUtc);

/// <summary>Atomic local storage for an accepted model download.</summary>
internal sealed class ModelDownloadApprovalStore(string modelsDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(modelsDirectory, "download-approval.json");

    public async Task<ModelDownloadApproval?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return null;

        await using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var approval = await JsonSerializer.DeserializeAsync<ModelDownloadApproval>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false);

        if (approval is null
            || approval.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(approval.SelectedModelId)
            || approval.StagedModelIds is null
            || approval.StagedModelIds.Count == 0
            || !approval.StagedModelIds.Contains(approval.SelectedModelId, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return approval;
    }

    public async Task WriteAsync(ModelDownloadApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, approval, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (FileNotFoundException) { }
        return Task.CompletedTask;
    }
}
