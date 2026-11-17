using Concierge.Shared.Design;

namespace Concierge;

public sealed class MauiMediaShare : IMediaShare
{
    public async Task ShareAsync(string path, string mediaType, string title,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("The media file is missing.", path);
        if (mediaType is not ("video/mp4" or "image/png"))
            throw new ArgumentException("Only a video clip or poster can be shared here.", nameof(mediaType));
        await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = title,
            File = new ShareFile(path, mediaType)
        }));
    }
}
