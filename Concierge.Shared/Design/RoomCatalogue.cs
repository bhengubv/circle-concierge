using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concierge.Shared.Design;

/// <summary>One part of a thing: a box, somewhere, of some size.</summary>
/// <param name="Shape">box, sphere, cylinder or cone.</param>
/// <param name="Width">Across.</param>
/// <param name="Depth">Back.</param>
/// <param name="Height">Up.</param>
/// <param name="X">Offset across from the middle of the thing.</param>
/// <param name="Y">Offset back.</param>
/// <param name="Sill">How far off the floor the bottom of this part sits.</param>
public sealed record CataloguePart(
    string Shape = "box",
    int Width = 40,
    int Depth = 40,
    int Height = 40,
    int X = 0,
    int Y = 0,
    int Sill = 0);

/// <summary>Something somebody can ask for by name.</summary>
/// <param name="Name">What it is called — "desk", "sofa", "bed".</param>
/// <param name="Parts">What it is made of.</param>
public sealed record CatalogueThing(string Name, IReadOnlyList<CataloguePart> Parts);

/// <summary>
/// The things a room can be furnished with, and how somebody adds their own.
///
/// **This is Pascal's plugin idea with the dangerous half left out.** Pascal lets
/// people write add-ons that contribute node types, renderers and panels — real
/// code, loaded into the editor. `PluginHost` in this repository does the
/// equivalent and is deliberately not wired up, because "any DLL on disk can add
/// tools" is a decision somebody makes on purpose rather than by finishing a
/// wiring job. That has not changed.
///
/// What people actually want from those add-ons, though, is nearly always *more
/// things to put in the room* — and that needs no code at all. A file naming a
/// desk as three boxes is an add-on anybody can write, cannot execute anything,
/// and cannot break the app.
///
/// Read from `%LOCALAPPDATA%/Concierge/shapes.json`, absent unless somebody wrote
/// one. A broken file adds nothing and says so rather than taking the catalogue
/// down with it — the same rule the hooks file follows, for the same reason.
/// </summary>
public sealed class RoomCatalogue
{
    private readonly string _path;
    private IReadOnlyList<CatalogueThing>? _read;

    /// <param name="path">Where the file is.</param>
    public RoomCatalogue(string path)
        => _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>Where it reads from, so a room can say so.</summary>
    public string Path => _path;

    /// <summary>What went wrong with the file, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// The things it knows about, built in ones first.
    ///
    /// A handful ship so the catalogue is useful before anybody writes anything,
    /// and because a feature that only works once you have configured it is a
    /// feature most people never see.
    /// </summary>
    public IReadOnlyList<CatalogueThing> Things => _read ??= Read();

    /// <summary>One by name, or null.</summary>
    public CatalogueThing? Find(string name)
        => Things.FirstOrDefault(thing =>
            thing.Name.Equals(name?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase));

    /// <summary>Forgets what it read, so a changed file is picked up.</summary>
    public void Again() => _read = null;

    private IReadOnlyList<CatalogueThing> Read()
    {
        Problem = null;

        var things = new List<CatalogueThing>(BuiltIn);

        try
        {
            if (!File.Exists(_path))
            {
                return things;
            }

            var written = JsonSerializer.Deserialize<List<CatalogueThing>>(
                File.ReadAllText(_path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            if (written is null)
            {
                return things;
            }

            foreach (var thing in written)
            {
                if (string.IsNullOrWhiteSpace(thing.Name) || thing.Parts is not { Count: > 0 })
                {
                    // A nameless thing cannot be asked for and a thing with no
                    // parts draws nothing. Both are skipped rather than failing
                    // the file, so one bad entry does not cost the rest.
                    continue;
                }

                // Somebody's own definition wins over a built-in of the same name.
                // That is the whole point of being able to write one.
                things.RemoveAll(had => had.Name.Equals(thing.Name, StringComparison.OrdinalIgnoreCase));
                things.Add(thing);
            }
        }
        catch (Exception problem) when (problem is JsonException or IOException or UnauthorizedAccessException)
        {
            // A memory aid must never break the thing it is helping with. The
            // built-ins still work and the room says what is wrong with the file.
            Problem = problem.Message;
        }

        return things;
    }

    /// <summary>
    /// What ships, so the catalogue is useful before anybody writes a file.
    ///
    /// Measured in centimetres, roughly, because a room built at the size of real
    /// furniture is a room somebody can judge. A desk that is a nice number of
    /// units and the wrong size is worse than no desk.
    /// </summary>
    private static readonly IReadOnlyList<CatalogueThing> BuiltIn =
    [
        new("desk",
        [
            new("box", 140, 70, 4, 0, 0, 72),
            new("box", 6, 60, 72, -62, 0),
            new("box", 6, 60, 72, 62, 0),
        ]),
        new("table",
        [
            new("box", 160, 90, 4, 0, 0, 74),
            new("cylinder", 8, 8, 74, -70, -38),
            new("cylinder", 8, 8, 74, 70, -38),
            new("cylinder", 8, 8, 74, -70, 38),
            new("cylinder", 8, 8, 74, 70, 38),
        ]),
        new("chair",
        [
            new("box", 45, 45, 4, 0, 0, 45),
            new("box", 45, 5, 45, 0, -20, 49),
            new("cylinder", 5, 5, 45, -18, -18),
            new("cylinder", 5, 5, 45, 18, -18),
            new("cylinder", 5, 5, 45, -18, 18),
            new("cylinder", 5, 5, 45, 18, 18),
        ]),
        new("sofa",
        [
            new("box", 200, 90, 40, 0, 0, 10),
            new("box", 200, 20, 50, 0, -35, 50),
            new("box", 20, 90, 60, -90, 0, 10),
            new("box", 20, 90, 60, 90, 0, 10),
        ]),
        new("bed",
        [
            new("box", 150, 200, 30, 0, 0, 20),
            new("box", 150, 10, 90, 0, -95, 20),
        ]),
        new("shelf",
        [
            new("box", 90, 25, 3, 0, 0, 0),
            new("box", 90, 25, 3, 0, 0, 35),
            new("box", 3, 25, 70, -44, 0),
            new("box", 3, 25, 70, 44, 0),
        ]),
        new("lamp",
        [
            new("cylinder", 30, 30, 22, 0, 0, 0),
            new("cylinder", 4, 4, 40, 0, 0, 22),
        ]),
        new("door",
        [
            new("box", 90, 4, 200, 0, 0, 0),
        ]),
    ];
}
