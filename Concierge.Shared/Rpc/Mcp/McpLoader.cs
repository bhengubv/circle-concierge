using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Concierge.Shared.Rpc.Mcp;

/// <summary>
/// Starts the configured MCP servers when the app starts.
///
/// On a background task rather than awaited: StartAsync holds the host back
/// from finishing start-up, and a server that is slow to answer its handshake
/// would hold the window closed. Nothing depends on the tools being present
/// before the first turn — the registry reads sources every time it is asked,
/// so a server that connects late simply appears.
/// </summary>
internal sealed class McpLoader : IHostedService
{
    private readonly McpToolSource _source;

    public McpLoader(McpToolSource source) => _source = source;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _source.ConnectAllAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Each server records its own status; a failure here must not
                // take the host down.
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
        => await _source.DisposeAsync().ConfigureAwait(false);
}

public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Wires MCP: the config file, the connections, and the tools they publish.
    ///
    /// Nothing happens without a config file — no servers ship by default,
    /// because an assistant that quietly starts talking to other software
    /// because a sample shipped with it is a poor trade for saving one paste.
    /// </summary>
    public static IServiceCollection AddConciergeMcp(
        this IServiceCollection services, string? configPath = null)
    {
        services.TryAddSingleton(_ => new McpServerOptions(configPath));
        services.TryAddSingleton<McpToolSource>();
        services.AddSingleton<IAgentToolSource>(sp => sp.GetRequiredService<McpToolSource>());
        services.AddHostedService<McpLoader>();

        return services;
    }
}
