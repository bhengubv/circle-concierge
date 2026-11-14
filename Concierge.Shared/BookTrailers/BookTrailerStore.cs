using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Concierge.Shared.BookTrailers;

/// <summary>Private SQLite index for imported book text and reviewable local drafts.</summary>
public sealed class BookTrailerStore(string root)
{
    private readonly string _database = Path.Combine(Path.GetFullPath(root), "trailers.db");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SaveBookAsync(BookSourceDocument book, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO books (hash, document) VALUES ($hash, $document) "
            + "ON CONFLICT(hash) DO UPDATE SET document = excluded.document";
        command.Parameters.AddWithValue("$hash", book.Sha256);
        command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(book, Json));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BookSourceDocument?> LoadBookAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM books WHERE hash = $hash";
        command.Parameters.AddWithValue("$hash", hash);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize<BookSourceDocument>(json, Json);
    }

    public async Task SaveDraftAsync(BookTrailerDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO drafts (folder, source_hash, created_at, document) "
            + "VALUES ($folder, $hash, $at, $document)";
        command.Parameters.AddWithValue("$folder", draft.Folder);
        command.Parameters.AddWithValue("$hash", draft.SourceSha256);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(draft, Json));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BookTrailerDraft>> RecentAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM drafts ORDER BY created_at DESC LIMIT 30";
        var drafts = new List<BookTrailerDraft>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var draft = JsonSerializer.Deserialize<BookTrailerDraft>(reader.GetString(0), Json);
            if (draft is not null && File.Exists(draft.VideoPath)) drafts.Add(draft);
        }
        return drafts;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_database)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS books (
                hash TEXT PRIMARY KEY NOT NULL,
                document TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS drafts (
                folder TEXT PRIMARY KEY NOT NULL,
                source_hash TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                document TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_drafts_created_at ON drafts (created_at DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
