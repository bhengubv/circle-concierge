namespace Concierge.Shared.Media;

/// <summary>
/// Lets the person choose where a prepared package is saved so another app can
/// import it. The package remains private until the person takes this action.
/// </summary>
public interface ISleptOnPackageFileSave
{
    Task<string?> SaveAsync(string packagePath, CancellationToken cancellationToken = default);
}
