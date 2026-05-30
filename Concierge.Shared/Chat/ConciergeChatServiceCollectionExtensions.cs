using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Chat;

public static class ConciergeChatServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite-backed conversation store, a <see cref="NullChatRuntime"/> default
    /// (adapter packages override via <see cref="IServiceCollection"/> replacement), and the
    /// DbContext factory. Resolves the database path under the user's local-app-data folder so
    /// the store is per-machine and survives upgrades.
    /// </summary>
    public static IServiceCollection AddConciergeChat(this IServiceCollection services, string? databasePath = null)
    {
        var resolvedPath = databasePath ?? ResolveDefaultDatabasePath();
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        services.AddDbContextFactory<ConciergeChatDbContext>(options =>
        {
            options.UseSqlite($"Data Source={resolvedPath}");
        });
        services.AddSingleton<IConversationStore, ConversationStore>();
        services.TryAddSingleton<IChatRuntime, NullChatRuntime>();
        services.AddHostedService<ConciergeChatSchemaInitializer>();
        return services;
    }

    private static string ResolveDefaultDatabasePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Directory.GetCurrentDirectory(), ".concierge-artifacts");
        }
        return Path.Combine(root, "Concierge", "concierge.db");
    }
}
