using Concierge.Shared.Design;

namespace Concierge;

public sealed class MauiDraftShare : IDraftShare
{
    public async Task<DraftShareResult> ShareAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("The draft package is missing.", path);
#if ANDROID
        var staged = await Task.Run(() => StageForAndroid(path, cancellationToken), cancellationToken);
        if (await OpenSleptOnOnMainThreadAsync(staged))
            return DraftShareResult.OpenedSleptOn;
#endif
        await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Send draft to SleptON!",
            File = new ShareFile(path, "application/zip")
        }));
        return DraftShareResult.OpenedSystemShare;
    }

#if ANDROID
    private static Task<bool> OpenSleptOnOnMainThreadAsync(string staged)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { completion.TrySetResult(OpenSleptOn(staged)); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    private static string StageForAndroid(string path, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(FileSystem.CacheDirectory, "slepton-handoff");
        Directory.CreateDirectory(folder);
        // SleptON! copies the content URI into its own private queue after launch.
        // Keep it available until then; remove only old completed handoff files.
        foreach (var old in Directory.EnumerateFiles(folder, "*.zip"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var staged = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
        File.Copy(path, staged);
        return staged;
    }

    private static bool OpenSleptOn(string staged)
    {
        var context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (context is null) return false;
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context,
            context.PackageName + ".fileProvider", new Java.IO.File(staged));
        foreach (var package in SleptOnPackages)
        {
            using var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionSend);
            intent.SetType("application/zip");
            intent.SetPackage(package);
            intent.PutExtra(global::Android.Content.Intent.ExtraStream, uri);
            intent.ClipData = global::Android.Content.ClipData.NewUri(context.ContentResolver, "Concierge draft", uri);
            intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission |
                global::Android.Content.ActivityFlags.NewTask);
            if (context.PackageManager is null || intent.ResolveActivity(context.PackageManager) is null) continue;
            try
            {
                context.StartActivity(intent);
                return true;
            }
            catch (global::Android.Content.ActivityNotFoundException)
            {
                // The app was removed between discovery and launch.
            }
        }
        return false;
    }

    private static readonly string[] SleptOnPackages =
    [
        "co.za.thegeeknetwork.slepton",
#if DEBUG
        "co.za.thegeeknetwork.slepton.conciergecheck"
#endif
    ];
#endif
}
