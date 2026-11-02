using Concierge.Away;

namespace Concierge.Shared.Planning;

/// <summary>Exchange complete, bounded notebook records over the existing Desktop link.</summary>
public static class NotebookExchange
{
    public static async Task<bool> SyncAsync(IKnowledgeNotebookStore store, AwayClient client,
        CancellationToken cancellationToken = default)
    {
        var remote = await client.ReadNotebookSyncItemsAsync(cancellationToken).ConfigureAwait(false);
        if (remote is null) return false;
        var local = store.SyncItems();
        // Upload first. Otherwise a remote tombstone could erase a newer local
        // edit before the Desktop has had a chance to compare revisions.
        if (!await client.SynchronizeNotebooksAsync(local, cancellationToken).ConfigureAwait(false))
            return false;
        store.MergeSyncItems(remote);
        return true;
    }
}
