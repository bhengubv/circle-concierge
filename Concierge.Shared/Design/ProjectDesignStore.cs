namespace Concierge.Shared.Design;

/// <summary>Gives each local Project its own restorable canvas history.</summary>
public sealed class ProjectDesignStore
{
    private readonly string _directory;
    private readonly Dictionary<Guid, IDesignStore> _stores = [];
    private readonly object _gate = new();

    public ProjectDesignStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _directory = Path.Combine(dataRoot, "project-designs");
    }

    public IDesignStore For(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A Project ID is required.", nameof(projectId));

        lock (_gate)
        {
            if (!_stores.TryGetValue(projectId, out var store))
            {
                store = new FileDesignStore(Path.Combine(_directory, $"{projectId:N}.json"));
                _stores.Add(projectId, store);
            }
            return store;
        }
    }
}
