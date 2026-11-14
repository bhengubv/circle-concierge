using Concierge.Shared.Media;

namespace Concierge.Tests;

internal sealed class RecordingSleptOnPackageFileSave(string destinationRoot) : ISleptOnPackageFileSave
{
    public string? SavedPath { get; private set; }

    public Task<string?> SaveAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destinationRoot);
        SavedPath = Path.Combine(destinationRoot, Path.GetFileName(packagePath));
        File.Copy(packagePath, SavedPath);
        return Task.FromResult<string?>(SavedPath);
    }
}
