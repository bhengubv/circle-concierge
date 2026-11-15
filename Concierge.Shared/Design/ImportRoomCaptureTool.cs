using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Design;

/// <summary>Imports a private scan into the open Space canvas without copying its photos or mesh.</summary>
public sealed class ImportRoomCaptureTool(DesignWorkbench workbench) : IAgentTool
{
    private const long MaxJsonBytes = 8 * 1024 * 1024;

    public string Name => "design_import_room_scan";
    public string Description =>
        "Bring a 3D Model Anything room scan into Space as editable walls, door/window openings "
        + "and measured object boxes. Give a local capture ZIP, extracted folder, RoomPlan JSON "
        + "or converted room.json. If it contains several rooms, specify the room number.";
    public JsonNode? ArgumentsSchema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["room"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
        },
        ["required"] = new JsonArray("path"),
    };
    public bool IsReadOnly => false;

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        if (workbench.Session is not { } session)
            return new AgentToolResult(false, string.Empty, "Open Creator before importing a room.");
        var path = arguments?["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
            return new AgentToolResult(false, string.Empty, "Choose a local capture ZIP, folder or room JSON.");
        int? room;
        try { room = arguments?["room"]?.GetValue<int>(); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        { return new AgentToolResult(false, string.Empty, "Room must be a non-negative number."); }
        if (room < 0) return new AgentToolResult(false, string.Empty, "Room must be a non-negative number.");

        try
        {
            var full = Path.GetFullPath(path);
            var json = await ReadRoomAsync(full, room, cancellationToken).ConfigureAwait(false);
            var result = RoomCaptureImport.Into(session.Current, json, Path.GetFileNameWithoutExtension(full));
            session.Record(result.Document, "Imported a measured room");
            return new AgentToolResult(true,
                $"Imported {result.Walls} walls, {result.Openings} openings and {result.Objects} measured object boxes into Space. "
                + "The source photos, scan mesh and AR world map were not imported; review the geometry before using it in a game.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or FormatException or ArgumentException or InvalidDataException)
        {
            return new AgentToolResult(false, string.Empty, $"Room import failed: {exception.Message}");
        }
    }

    private static async Task<string> ReadRoomAsync(string path, int? room, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            var names = Directory.EnumerateFiles(path, "room-*.json", SearchOption.TopDirectoryOnly)
                .Concat(Directory.Exists(Path.Combine(path, "rooms"))
                    ? Directory.EnumerateFiles(Path.Combine(path, "rooms"), "room-*.json", SearchOption.TopDirectoryOnly)
                    : [])
                .ToArray();
            if (names.Length == 0)
                names = new[] { Path.Combine(path, "room.json"), Path.Combine(path, "structure.json") }
                    .Where(File.Exists).ToArray();
            return await ReadFileAsync(Choose(names, room), cancellationToken).ConfigureAwait(false);
        }

        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries.Where(entry =>
                    entry.FullName.Replace('\\', '/').Split('/').Last().StartsWith("room-", StringComparison.OrdinalIgnoreCase)
                    && entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (entries.Length == 0)
                entries = archive.Entries.Where(entry => entry.FullName.EndsWith("/structure.json", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.Equals("structure.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            var selected = Choose(entries, entry => entry.FullName, room);
            if (selected.Length > MaxJsonBytes)
                throw new InvalidDataException("The room JSON exceeds the 8 MB import limit.");
            await using var stream = selected.Open();
            using var reader = new StreamReader(stream);
            return await ReadLimitedAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Choose a capture ZIP, extracted folder or room JSON.");
        return await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaxJsonBytes)
            throw new InvalidDataException("The room JSON exceeds the 8 MB import limit.");
        using var reader = new StreamReader(path);
        return await ReadLimitedAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var text = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > MaxJsonBytes)
                throw new InvalidDataException("The room JSON exceeds the 8 MB import limit.");
        }
        return text.ToString();
    }

    private static string Choose(IReadOnlyList<string> names, int? room)
        => Choose(names, name => name, room);

    private static T Choose<T>(IReadOnlyList<T> candidates, Func<T, string> name, int? room)
    {
        if (room is { } index)
        {
            var suffix = $"room-{index}.json";
            candidates = candidates.Where(candidate => name(candidate).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (candidates.Count == 0)
            throw new FormatException("No matching measured room JSON was found.");
        if (candidates.Count > 1)
            throw new FormatException("This capture has several rooms. Specify the room number to import.");
        return candidates[0];
    }
}
