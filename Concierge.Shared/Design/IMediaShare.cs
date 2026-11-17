namespace Concierge.Shared.Design;

/// <summary>Asks the operating system to share one finished local media file.</summary>
public interface IMediaShare
{
    Task ShareAsync(string path, string mediaType, string title,
        CancellationToken cancellationToken = default);
}
