using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Concierge.Shared.Chat;

/// <summary>
/// Hosted service that creates the SQLite schema on first run. Uses <c>EnsureCreated</c>
/// rather than migrations because the schema is still single-file and tiny — a proper
/// migration history can come later once the shape starts evolving across releases.
/// </summary>
internal sealed class ConciergeChatSchemaInitializer : IHostedService
{
    private readonly IDbContextFactory<ConciergeChatDbContext> _factory;

    public ConciergeChatSchemaInitializer(IDbContextFactory<ConciergeChatDbContext> factory)
    {
        _factory = factory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
