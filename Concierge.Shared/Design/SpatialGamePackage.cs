using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Concierge.Shared.Design;

/// <summary>A local, playable draft. This does not publish or send the source scan.</summary>
public static class SpatialGamePackage
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly string[] TemplateFiles = ["index.html", "game.js", "preview.mjs",
        "vendor/three.module.js", "vendor/three.core.js", "vendor/LICENSE"];

    public static string Create(DesignDocument document, string title, string exportRoot, bool rightsConfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Medium != DesignMedium.Scene || document.Frames.Count == 0)
            throw new InvalidDataException("Open a Space room before making a game.");
        if (string.IsNullOrWhiteSpace(exportRoot)) throw new ArgumentException("An export folder is required.", nameof(exportRoot));
        title = title.Trim();
        if (title.Length is < 1 or > 100) throw new ArgumentException("Give the game a title of 1–100 characters.", nameof(title));
        var frame = document.Frames[^1];
        var solids = document.ChildrenOf(frame.Id).Where(n => n.Kind == DesignNodeKind.Solid).ToArray();
        var walls = solids.Where(n => Shape(n) == "wall").Select(n => new Wall(
            [Cm(n, "x"), Cm(n, "y")], [Cm(n, "x2"), Cm(n, "y2")], Cm(n, "height", 240),
            document.ChildrenOf(n.Id).Where(h => Shape(h) is "door" or "window")
                .Select(h => new Opening(Shape(h), Cm(h, "at"), Cm(h, "width"), Cm(h, "height"), Cm(h, "sill"))).ToArray())).ToArray();
        var objects = solids.Where(n => Shape(n) is not ("wall" or "floor" or "ceiling" or "roof" or "plan"))
            .Select(n => new GameObject(n.Id, string.IsNullOrWhiteSpace(n.Text) ? "Object" : n.Text,
                Cm(n, "x"), Cm(n, "y"), Cm(n, "width", 100), Cm(n, "depth", 100),
                Cm(n, "height", 80), Number(n, "rotation"))).ToArray();
        if (walls.Length < 3 || objects.Length < 3)
            throw new InvalidDataException("A playable room needs at least three walls and three objects.");
        if (walls.Length > 256 || objects.Length > 512)
            throw new InvalidDataException("This room exceeds the game size limit.");
        if (walls.Any(w => w.Height is <= 0 or > 20 || w.Openings.Any(o => o.Width <= 0 || o.Height <= 0 || o.Sill < 0))
            || objects.Any(o => o.Width is <= 0 or > 20 || o.Depth is <= 0 or > 20 || o.Height is <= 0 or > 20))
            throw new InvalidDataException("A wall, opening or object has invalid dimensions.");
        var minX = walls.SelectMany(w => new[] { w.A[0], w.B[0] }).Min();
        var maxX = walls.SelectMany(w => new[] { w.A[0], w.B[0] }).Max();
        var minZ = walls.SelectMany(w => new[] { w.A[1], w.B[1] }).Min();
        var maxZ = walls.SelectMany(w => new[] { w.A[1], w.B[1] }).Max();
        var width = maxX - minX;
        var depth = maxZ - minZ;
        if (width is <= 0 or > 100 || depth is <= 0 or > 100)
            throw new InvalidDataException("The room must be between 0 and 100 metres wide and deep.");
        var cx = (minX + maxX) / 2;
        var cz = (minZ + maxZ) / 2;
        var centeredWalls = walls.Select(w => w with
        {
            A = [w.A[0] - cx, w.A[1] - cz], B = [w.B[0] - cx, w.B[1] - cz]
        }).ToArray();
        var centeredObjects = objects.Select(o => o with { X = o.X - cx, Z = o.Z - cz }).ToArray();
        var scene = new GameScene(title, width, depth, centeredWalls, centeredObjects,
            centeredObjects.Take(3).Select(o => o.Id).ToArray());
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in TemplateFiles) files[file] = Template(file);
        files["scene.json"] = JsonSerializer.SerializeToUtf8Bytes(scene, Json);
        files["README.txt"] = Encoding.UTF8.GetBytes("Spatial game draft for SleptON!\nRun node preview.mjs and open http://127.0.0.1:8765.\nThe browser game works with pointer and touch. Optional AR/VR needs compatible hardware and a secure browser context. AR places a miniature room and does not align to the original building.\nThis package contains geometry only, not source photos, scan mesh, or an AR world map. Publishing requires a SleptON game importer, rights review, and device acceptance.\n");
        var manifest = new
        {
            schemaVersion = 1, kind = "spatial-game-draft", status = "draft", published = false,
            rightsConfirmed, title, createdUtc = DateTimeOffset.UtcNow, entry = "index.html",
            files = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                path = pair.Key, mediaType = MediaType(pair.Key), bytes = pair.Value.LongLength,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value))
            }).ToArray()
        };
        files["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        Directory.CreateDirectory(exportRoot);
        var safe = new string(title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (safe.Length == 0) safe = "spatial-game";
        if (safe.Length > 48) safe = safe[..48];
        var path = Path.Combine(exportRoot, $"{safe}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.zip");
        var pending = path + ".partial";
        try
        {
            using (var archive = ZipFile.Open(pending, ZipArchiveMode.Create))
                foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(pair.Value);
                }
            File.Move(pending, path);
            return path;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private static string Shape(DesignNode node) => node.Props.TryGetValue("shape", out var value) ? value.ToLowerInvariant() : "box";
    private static double Cm(DesignNode node, string key, double fallback = 0) => Number(node, key, fallback) / 100;
    private static double Number(DesignNode node, string key, double fallback = 0)
    {
        if (!node.Props.TryGetValue(key, out var raw)) return fallback;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || Math.Abs(value) > 100_000)
            throw new InvalidDataException($"Invalid {key} measurement.");
        return value;
    }
    private static byte[] Template(string path)
    {
        var name = "Concierge.Shared.Design.SpatialGameTemplate." + path.Replace('/', '.');
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Missing game template {path}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
    private static string MediaType(string path) => Path.GetExtension(path) switch
    {
        ".html" => "text/html", ".js" or ".mjs" => "text/javascript", ".json" => "application/json",
        _ => "text/plain"
    };
    private sealed record Opening(string Kind, double At, double Width, double Height, double Sill);
    private sealed record Wall(double[] A, double[] B, double Height, Opening[] Openings);
    private sealed record GameObject(string Id, string Name, double X, double Z, double Width, double Depth, double Height, double Rotation);
    private sealed record GameScene(string Title, double WidthM, double DepthM, Wall[] Walls, GameObject[] Objects, string[] Targets);
}
