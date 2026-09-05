using System.Text.Json;

namespace Concierge.Shared.Skills;

/// <summary>
/// Folders of your own skills, remembered between launches.
///
/// The only way to add a skill used to be CONCIERGE_SKILLS_ROOT, set before
/// launch, pointing at one folder with three specific subfolder names — or a
/// hardcoded path on the machine this was written on. That is a developer's
/// arrangement, and Concierge is not only for developers: seventy curated
/// skills and no way to add your own is a catalogue, not a capability.
///
/// A folder rather than a file, because a skill is a folder with a SKILL.md in
/// it, and people keep them in one place.
/// </summary>
public sealed class UserSkillFolders
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();

    // System.IO.Path qualified throughout: this exposes a Path of its own,
    // matching the other local-state classes, which shadows it.
    public UserSkillFolders(string? path = null)
        => _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Concierge",
            "skills.json");

    public string Path => _path;

    /// <summary>Every folder added, whether or not it still exists.</summary>
    public IReadOnlyList<string> All()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return Array.Empty<string>();
                }

                var json = File.ReadAllText(_path);
                return string.IsNullOrWhiteSpace(json)
                    ? Array.Empty<string>()
                    : JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
            }
            catch (Exception)
            {
                // An unreadable list is an empty one. It holds folder paths;
                // nothing here is worth failing a launch over.
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// Remembers a folder. Returns why not, or null when it was added.
    ///
    /// Checked rather than accepted: a path that does not exist, or has no
    /// skills in it, is almost always a typo, and finding that out now beats
    /// finding out when the catalogue silently does not change.
    /// </summary>
    public string? Add(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "Give it a folder.";
        }

        folder = folder.Trim().Trim('"');

        if (!Directory.Exists(folder))
        {
            return $"There is no folder at {folder}.";
        }

        if (!Directory.EnumerateFiles(folder, "SKILL.md", SearchOption.AllDirectories).Any())
        {
            return "No skills in there — a skill is a folder with a SKILL.md in it.";
        }

        lock (_gate)
        {
            var folders = All().ToList();

            if (folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            {
                return "That folder is already here.";
            }

            folders.Add(folder);
            return Save(folders);
        }
    }

    public string? Remove(string folder)
    {
        lock (_gate)
        {
            var folders = All()
                .Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return Save(folders);
        }
    }

    private string? Save(List<string> folders)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside and moved, so a crash mid-write cannot leave a
            // truncated list that reads as "no folders" next launch.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(folders, Json));
            File.Move(temporary, _path, overwrite: true);

            return null;
        }
        catch (Exception ex)
        {
            return $"Could not save the list: {ex.Message}";
        }
    }
}

/// <summary>
/// The skills in whatever folders you have added.
///
/// Discovery reads the list every time rather than at construction, so a folder
/// added while the app is running is found the next time the catalogue is
/// composed — which is what makes adding one from the Skills panel work at all.
/// </summary>
public sealed class UserFolderSkillSource : ISkillSource
{
    private readonly UserSkillFolders _folders;

    /// <summary>
    /// Reads bodies from the descriptor's own file:// URI, so it needs no root
    /// of its own — kept as one instance rather than built per call.
    /// </summary>
    private readonly FileSystemSkillSource _reader = new("yours");

    public UserFolderSkillSource(UserSkillFolders folders) => _folders = folders;

    public string Name => "yours";

    public IReadOnlyList<SkillDescriptor> Discover()
    {
        var found = new List<SkillDescriptor>();

        foreach (var folder in _folders.All())
        {
            if (!Directory.Exists(folder))
            {
                // Added once and since moved or unplugged. Skipped rather than
                // reported here; the panel lists it as missing.
                continue;
            }

            try
            {
                found.AddRange(new FileSystemSkillSource("yours", folder).Discover());
            }
            catch (Exception)
            {
                // One unreadable folder must not cost the others.
            }
        }

        return found;
    }

    public string? LoadBody(SkillDescriptor descriptor) => _reader.LoadBody(descriptor);
}
