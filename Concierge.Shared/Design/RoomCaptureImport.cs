using System.Globalization;
using System.Text.Json;

namespace Concierge.Shared.Design;

/// <summary>
/// Brings the measured geometry of a 3D Model Anything room into Concierge Space.
/// Accepts a house-capture/5 RoomPlan room JSON or the room.json produced by
/// scan-to-threejs/bundle_to_room.py. Photos, meshes and AR world maps stay in
/// their source bundle; this imports editable geometry, not a claimed photogrammetry result.
/// </summary>
public static class RoomCaptureImport
{
    public sealed record Result(DesignDocument Document, int Walls, int Openings, int Objects);
    private sealed record Wall(double X1, double Z1, double X2, double Z2, double Height,
        IReadOnlyList<Opening> Openings);
    private sealed record Opening(string Kind, double At, double Width, double Height, double Sill);
    private sealed record Item(string Name, double X, double Z, double Width, double Depth,
        double Height, double Rotation);

    public static Result Into(DesignDocument current, string json, string roomName)
    {
        ArgumentNullException.ThrowIfNull(current);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("walls", out var sourceWalls)
            || sourceWalls.ValueKind != JsonValueKind.Array || sourceWalls.GetArrayLength() is < 1 or > 256)
            throw new FormatException("Choose a room JSON with 1–256 measured walls.");

        var converted = sourceWalls[0].TryGetProperty("a", out _);
        var walls = converted ? ConvertedWalls(root) : RoomPlanWalls(root);
        var items = converted ? ConvertedItems(root) : RoomPlanItems(root);
        if (items.Count > 512)
            throw new FormatException("A room may contain at most 512 measured objects.");

        var minX = walls.Min(wall => Math.Min(wall.X1, wall.X2));
        var maxX = walls.Max(wall => Math.Max(wall.X1, wall.X2));
        var minZ = walls.Min(wall => Math.Min(wall.Z1, wall.Z2));
        var maxZ = walls.Max(wall => Math.Max(wall.Z1, wall.Z2));
        var width = maxX - minX;
        var depth = maxZ - minZ;
        if (!double.IsFinite(width) || !double.IsFinite(depth) || Math.Max(width, depth) <= 0
            || width > 100 || depth > 100)
            throw new FormatException("The measured room must fit within 100 metres on each side.");

        var cx = (minX + maxX) / 2;
        var cz = (minZ + maxZ) / 2;
        var document = current.As(DesignMedium.Scene);
        var frame = DesignNode.New(DesignNodeKind.Frame, document.RootId,
            ("text", string.IsNullOrWhiteSpace(roomName) ? "Scanned room" : roomName.Trim()),
            ("capture", converted ? "scan-to-threejs room" : "house-capture/5 RoomPlan"),
            ("units", "metres in source; centimetres on canvas"));
        document = document.Add(frame);
        document = document.Add(DesignNode.New(DesignNodeKind.Solid, frame.Id,
            [.. RoomPieces.Props("floor", "Floor preview", width: Math.Max(100, width * 100),
                depth: Math.Max(100, depth * 100), height: 0)]));

        foreach (var wall in walls)
        {
            Validate(wall.X1, wall.Z1, wall.X2, wall.Z2, wall.Height);
            if (wall.Height is <= 0 or > 20) throw new FormatException("A wall height is outside 0–20 metres.");
            var node = DesignNode.New(DesignNodeKind.Solid, frame.Id,
                [.. RoomPieces.Props("wall", "Measured wall", (wall.X1 - cx) * 100,
                    (wall.Z1 - cz) * 100, (wall.X2 - cx) * 100, (wall.Z2 - cz) * 100,
                    height: wall.Height * 100)]);
            document = document.Add(node);
            var length = Math.Sqrt(Math.Pow(wall.X2 - wall.X1, 2) + Math.Pow(wall.Z2 - wall.Z1, 2));
            foreach (var opening in wall.Openings)
            {
                Validate(opening.At, opening.Width, opening.Height, opening.Sill);
                if (opening.Width <= 0 || opening.Height <= 0 || opening.Sill < 0
                    || opening.At < -0.02 || opening.At + opening.Width > length + 0.02
                    || opening.Sill + opening.Height > wall.Height + 0.02)
                    throw new FormatException("An opening lies outside its measured wall.");
                document = document.Add(DesignNode.New(DesignNodeKind.Solid, node.Id,
                    [.. RoomPieces.Opening(opening.Kind, opening.At * 100,
                        opening.Width * 100, opening.Height * 100, opening.Sill * 100)]));
            }
        }

        foreach (var item in items)
        {
            Validate(item.X, item.Z, item.Width, item.Depth, item.Height, item.Rotation);
            if (item.Width <= 0 || item.Depth <= 0 || item.Height <= 0
                || item.Width > 20 || item.Depth > 20 || item.Height > 20)
                throw new FormatException("A measured object has invalid dimensions.");
            document = document.Add(DesignNode.New(DesignNodeKind.Solid, frame.Id,
                ("shape", "box"), ("text", item.Name),
                ("x", Cm(item.X - cx)), ("y", Cm(item.Z - cz)),
                ("width", Cm(item.Width)), ("depth", Cm(item.Depth)),
                ("height", Cm(item.Height)),
                ("rotation", item.Rotation.ToString("0.####", CultureInfo.InvariantCulture))));
        }

        return new Result(document, walls.Count, walls.Sum(wall => wall.Openings.Count), items.Count);
    }

    private static List<Wall> ConvertedWalls(JsonElement root)
    {
        var walls = new List<Wall>();
        foreach (var wall in root.GetProperty("walls").EnumerateArray())
        {
            var a = Pair(wall.GetProperty("a"));
            var b = Pair(wall.GetProperty("b"));
            var openings = new List<Opening>();
            if (wall.TryGetProperty("openings", out var holes))
                foreach (var hole in holes.EnumerateArray())
                {
                    var kind = hole.GetProperty("kind").GetString();
                    if (kind is not ("door" or "window")) continue;
                    var u0 = Number(hole.GetProperty("u0"));
                    var u1 = Number(hole.GetProperty("u1"));
                    var v0 = Number(hole.GetProperty("v0"));
                    var v1 = Number(hole.GetProperty("v1"));
                    openings.Add(new Opening(kind, u0, u1 - u0, v1 - v0, v0));
                }
            walls.Add(new Wall(a.X, a.Y, b.X, b.Y, Number(wall.GetProperty("height")), openings));
        }
        return walls;
    }

    private static List<Item> ConvertedItems(JsonElement root)
    {
        var items = new List<Item>();
        if (!root.TryGetProperty("objects", out var source)) return items;
        foreach (var entry in source.EnumerateArray())
        {
            var center = entry.GetProperty("center").EnumerateArray().Select(Number).ToArray();
            var size = entry.GetProperty("size").EnumerateArray().Select(Number).ToArray();
            if (center.Length != 3 || size.Length != 3) throw new FormatException("An object needs three coordinates and dimensions.");
            items.Add(new Item(entry.GetProperty("category").GetString() ?? "Object",
                center[0], center[2], size[0], size[2], size[1],
                entry.TryGetProperty("rotY", out var rot) ? Number(rot) : 0));
        }
        return items;
    }

    private static List<Wall> RoomPlanWalls(JsonElement root)
    {
        var children = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var kind in new[] { "doors", "windows" })
        {
            if (!root.TryGetProperty(kind, out var entries)) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("parentIdentifier", out var parent)) continue;
                var id = parent.GetString();
                if (string.IsNullOrEmpty(id)) continue;
                if (!children.TryGetValue(id, out var list)) children[id] = list = [];
                list.Add(entry);
            }
        }

        var walls = new List<Wall>();
        foreach (var wall in root.GetProperty("walls").EnumerateArray())
        {
            var transform = Matrix(wall);
            var dimensions = Dimensions(wall);
            var half = dimensions[0] / 2;
            var aX = transform[12] - transform[0] * half;
            var aZ = transform[14] - transform[2] * half;
            var bX = transform[12] + transform[0] * half;
            var bZ = transform[14] + transform[2] * half;
            var openings = new List<Opening>();
            var id = wall.TryGetProperty("identifier", out var identifier) ? identifier.GetString() : null;
            if (id is not null && children.TryGetValue(id, out var holes))
                foreach (var hole in holes)
                {
                    var h = Matrix(hole);
                    var size = Dimensions(hole);
                    var at = (h[12] - aX) * transform[0] + (h[14] - aZ) * transform[2] - size[0] / 2;
                    var category = hole.GetProperty("category");
                    var kind = category.TryGetProperty("door", out _) ? "door" : "window";
                    // RoomPlan's wall and opening transforms share a vertical frame.
                    var sill = Math.Max(0, h[13] - size[1] / 2 - (transform[13] - dimensions[1] / 2));
                    openings.Add(new Opening(kind, at, size[0], size[1], sill));
                }
            walls.Add(new Wall(aX, aZ, bX, bZ, dimensions[1], openings));
        }
        return walls;
    }

    private static List<Item> RoomPlanItems(JsonElement root)
    {
        var items = new List<Item>();
        if (!root.TryGetProperty("objects", out var source)) return items;
        foreach (var entry in source.EnumerateArray())
        {
            var m = Matrix(entry);
            var size = Dimensions(entry);
            var category = entry.GetProperty("category");
            var name = category.EnumerateObject().FirstOrDefault().Name ?? "Object";
            items.Add(new Item(name, m[12], m[14], size[0], size[2], size[1], Math.Atan2(m[8], m[10])));
        }
        return items;
    }

    private static double[] Matrix(JsonElement entry)
    {
        var values = entry.GetProperty("transform").EnumerateArray().Select(Number).ToArray();
        if (values.Length != 16) throw new FormatException("A RoomPlan transform needs 16 values.");
        return values;
    }

    private static double[] Dimensions(JsonElement entry)
    {
        var values = entry.GetProperty("dimensions").EnumerateArray().Select(Number).ToArray();
        if (values.Length != 3) throw new FormatException("RoomPlan dimensions need three values.");
        return values;
    }

    private static (double X, double Y) Pair(JsonElement value)
    {
        var values = value.EnumerateArray().Select(Number).ToArray();
        if (values.Length != 2) throw new FormatException("A wall point needs two coordinates.");
        return (values[0], values[1]);
    }

    private static double Number(JsonElement value)
    {
        var number = value.GetDouble();
        if (!double.IsFinite(number)) throw new FormatException("Room measurements must be finite.");
        return number;
    }

    private static void Validate(params double[] values)
    {
        if (values.Any(value => !double.IsFinite(value) || Math.Abs(value) > 100_000))
            throw new FormatException("A room measurement is invalid or unreasonably large.");
    }

    private static string Cm(double metres)
        => Math.Round(metres * 100).ToString(CultureInfo.InvariantCulture);
}
