namespace Concierge.Shared.Design;

/// <summary>Offers a private local draft to another app through the operating system.</summary>
public interface IDraftShare
{
    Task<DraftShareResult> ShareAsync(string path, CancellationToken cancellationToken = default);
}

public enum DraftShareResult
{
    OpenedSleptOn,
    OpenedSystemShare
}
