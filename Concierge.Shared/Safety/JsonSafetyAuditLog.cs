using System.Text.Json;
using CircleAI.ContentPolicy;

namespace Concierge.Shared.Safety;

/// <summary>
/// JSONL-on-disk implementation of <see cref="ISafetyAuditLog"/>. Each line
/// is a single entry, append-only, never edited or deleted by the runtime —
/// the parental dashboard reads from the tail and lets the parent decide
/// whether to clear. Stored under <c>%LocalAppData%/Concierge/safety/audit.jsonl</c>
/// by default; override the path for tests.
/// </summary>
/// <remarks>
/// Append-only is deliberate. Tampering with safety logs is what
/// distinguishes "kid mode" from "parental controls"; if the runtime could
/// silently overwrite log entries the audit trail wouldn't be credible to a
/// parent reviewing what happened while they were out of the room.
/// </remarks>
public sealed class JsonSafetyAuditLog : ISafetyAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonSafetyAuditLog(string? path = null)
    {
        _path = path ?? DefaultPath();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string BackendId => "concierge.jsonl.v1";

    public async ValueTask LogAsync(SafetyAuditEntry entry, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<SafetyAuditEntry>> ReadAsync(
        string? userId,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<SafetyAuditEntry>();
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(_path, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        // Read tail-first so the dashboard surfaces the most recent activity
        // by default. Limit applies after filtering by userId so the parent
        // sees the latest N events for the active profile.
        var result = new List<SafetyAuditEntry>(Math.Min(limit, lines.Length));
        for (var i = lines.Length - 1; i >= 0 && result.Count < limit; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            SafetyAuditEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<SafetyAuditEntry>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // Corrupt line — skip. The audit log is supposed to be
                // append-only, but a half-finished write during an OS kill
                // can leave a partial line at the tail.
                continue;
            }
            if (entry is null) continue;
            if (userId is null || string.Equals(entry.UserId, userId, StringComparison.Ordinal))
            {
                result.Add(entry);
            }
        }
        return result;
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Concierge",
        "safety",
        "audit.jsonl");
}
