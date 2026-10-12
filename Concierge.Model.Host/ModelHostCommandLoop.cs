using System.Text.Json;
using Concierge.Shared.Chat.Isolation;

namespace Concierge.Model.Host;

/// <summary>
/// Reads commands while long-running model operations execute. In particular,
/// a stream must not hold stdin inside its await: the matching Cancel command
/// arrives on that same pipe.
/// </summary>
public static class ModelHostCommandLoop
{
    public static async Task RunAsync(
        TextReader input,
        Func<ModelHostRequest, Task> stream,
        Func<ModelHostRequest, Task> rejectStream,
        Action<int> cancelStream,
        Func<ModelHostRequest, Task> download,
        Action cancelDownload,
        Action? pauseDownload = null,
        Task? resumedDownload = null,
        Func<ModelHostRequest, Task>? rejectDownload = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rejectStream);
        ArgumentNullException.ThrowIfNull(cancelStream);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(cancelDownload);

        Task? streamTask = null;
        var streamId = 0;
        Task? downloadTask = resumedDownload;
        var downloadCancelRequested = false;

        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            ModelHostRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ModelHostRequest>(line, ModelHostProtocol.Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (request is null)
                continue;

            switch (request.Op)
            {
                case ModelHostProtocol.Cancel when request.Id == streamId && streamTask is { IsCompleted: false }:
                    cancelStream(request.Id);
                    break;

                case ModelHostProtocol.Stream:
                    // The isolated adapter serializes requests. Treat a protocol
                    // violation as a host error rather than replacing the active
                    // stream's identity/cancellation source.
                    if (streamTask is not { IsCompleted: false })
                    {
                        streamId = request.Id;
                        streamTask = stream(request);
                    }
                    else
                    {
                        await rejectStream(request).ConfigureAwait(false);
                    }
                    break;

                case ModelHostProtocol.Download:
                    if (downloadTask is not { IsCompleted: false })
                    {
                        // Download startup does synchronous model selection and bundle
                        // presence checks before its first await. Keep that work off the
                        // command reader so CancelDownload remains serviceable even while
                        // Circle AI is doing setup.
                        downloadTask = Task.Run(() => download(request));
                        downloadCancelRequested = false;
                    }
                    else if (rejectDownload is not null)
                    {
                        await rejectDownload(request).ConfigureAwait(false);
                    }
                    break;

                case ModelHostProtocol.CancelDownload:
                    if (downloadTask is { IsCompleted: false } && !downloadCancelRequested)
                    {
                        downloadCancelRequested = true;
                        cancelDownload();
                    }
                    break;
            }
        }

        if (streamTask is { IsCompleted: false })
            cancelStream(streamId);
        if (downloadTask is { IsCompleted: false } && !downloadCancelRequested)
            (pauseDownload ?? cancelDownload)();

        var pending = new[] { streamTask, downloadTask }.Where(task => task is not null).Cast<Task>();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}
