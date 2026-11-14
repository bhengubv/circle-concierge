using CommunityToolkit.Maui.Storage;
using Concierge.Shared.Media;

namespace Concierge.Media;

public sealed class MauiSleptOnPackageFileSave : ISleptOnPackageFileSave
{
    public async Task<string?> SaveAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("The SleptON! package is no longer available.", packagePath);

#if ANDROID
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var allowed = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var read = await Permissions.RequestAsync<Permissions.StorageRead>();
                var write = await Permissions.RequestAsync<Permissions.StorageWrite>();
                return read == PermissionStatus.Granted && write == PermissionStatus.Granted;
            });
            if (!allowed)
                throw new UnauthorizedAccessException("Allow file access to save a SleptON! package.");
        }
#endif

        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var result = await MainThread.InvokeOnMainThreadAsync(() =>
            FileSaver.Default.SaveAsync(Path.GetFileName(packagePath), stream, cancellationToken));

        if (result.IsSuccessful) return result.FilePath;
        if (result.Exception is OperationCanceledException) return null;
        throw result.Exception ?? new IOException("The package could not be saved to Files.");
    }
}
