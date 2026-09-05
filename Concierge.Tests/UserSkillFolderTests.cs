using Concierge.Shared;
using Concierge.Shared.Skills;

namespace Concierge.Tests;

/// <summary>
/// Adding your own skills.
///
/// Seventy curated skills shipped, and the only way to add one was
/// CONCIERGE_SKILLS_ROOT set before launch — pointing at a folder with three
/// specific subfolder names, or at a hardcoded path on the machine this was
/// written on. That is a developer's arrangement in a product that is not only
/// for developers.
/// </summary>
public sealed class UserSkillFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");
    private readonly string _list;

    public UserSkillFolderTests()
    {
        Directory.CreateDirectory(_root);
        _list = Path.Combine(_root, "skills.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Swept with the test-run temp root regardless.
        }
    }

    private UserSkillFolders Folders() => new(_list);

    /// <summary>A skill is a folder with a SKILL.md in it.</summary>
    private string MakeSkillFolder(string id, string name = "A skill")
    {
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"""
            ---
            name: {id}
            profile: {name}
            category: Yours
            description: Something you wrote yourself.
            ---

            Do the thing.
            """);
        return folder;
    }

    // ── Adding ────────────────────────────────────────────────────────────

    [Fact]
    public void A_folder_of_skills_can_be_added_and_is_remembered()
    {
        MakeSkillFolder("mine");
        var folders = Folders();

        Assert.Null(folders.Add(_root));

        // A different instance, as a relaunch would be.
        Assert.Contains(_root, new UserSkillFolders(_list).All());
    }

    /// <summary>
    /// Checked rather than accepted. A path that does not exist is almost
    /// always a typo, and finding out now beats finding out when the catalogue
    /// silently does not change.
    /// </summary>
    [Fact]
    public void A_folder_that_is_not_there_is_refused_with_a_reason()
    {
        var problem = Folders().Add(Path.Combine(_root, "nope"));

        Assert.NotNull(problem);
        Assert.Contains("no folder", problem!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Folders().All());
    }

    [Fact]
    public void A_folder_with_no_skills_in_it_says_what_a_skill_is()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        var problem = Folders().Add(empty);

        Assert.NotNull(problem);
        Assert.Contains("SKILL.md", problem!);
    }

    [Fact]
    public void The_same_folder_is_not_added_twice()
    {
        MakeSkillFolder("mine");
        var folders = Folders();

        Assert.Null(folders.Add(_root));
        Assert.NotNull(folders.Add(_root));
        Assert.Single(folders.All());
    }

    [Fact]
    public void A_pasted_path_survives_its_quotes()
    {
        MakeSkillFolder("mine");

        Assert.Null(Folders().Add($"\"{_root}\""));
        Assert.Single(Folders().All());
    }

    [Fact]
    public void A_folder_can_be_removed()
    {
        MakeSkillFolder("mine");
        var folders = Folders();
        folders.Add(_root);

        folders.Remove(_root);

        Assert.Empty(folders.All());
    }

    // ── Reaching the catalogue ────────────────────────────────────────────

    /// <summary>
    /// The point of all of it: a skill in a folder you added is a skill the
    /// assistant can be given.
    /// </summary>
    [Fact]
    public void A_skill_in_an_added_folder_reaches_the_catalogue()
    {
        MakeSkillFolder("my-own-skill", "My own skill");
        var folders = Folders();
        folders.Add(_root);

        var catalogue = new SkillCatalogService([new UserFolderSkillSource(folders)]);

        Assert.Contains(catalogue.GetSkills(), s => s.Name == "My own skill");
    }

    /// <summary>
    /// And it says so. "Why is it answering like that?" is answered by which
    /// skills are on and where they came from, and the second half was not
    /// reaching the UI at all — SkillInfo dropped the source.
    /// </summary>
    [Fact]
    public void A_skill_says_where_it_came_from()
    {
        MakeSkillFolder("my-own-skill", "My own skill");
        var folders = Folders();
        folders.Add(_root);

        var catalogue = new SkillCatalogService([new UserFolderSkillSource(folders)]);
        var skills = catalogue.GetSkills();

        // "yours:<folder>" — FileSystemSkillSource appends the folder name,
        // which is more useful than a bare tag: two folders of your own are
        // told apart. The panel shows "yours" either way.
        Assert.StartsWith("yours", skills.Single(s => s.Name == "My own skill").Source);

        // And the ones that ship inside the assembly say so too.
        Assert.Contains(skills, s => s.Source == "bundled");
    }

    /// <summary>
    /// The catalogue cached forever, which was fine when every source was
    /// registered at start-up and wrong the moment a folder can be added while
    /// running. A folder that never appears looks exactly like a broken
    /// feature.
    /// </summary>
    [Fact]
    public void A_folder_added_while_running_appears_after_a_refresh()
    {
        var folders = Folders();
        var catalogue = new SkillCatalogService([new UserFolderSkillSource(folders)]);

        var before = catalogue.GetSkills().Count;

        MakeSkillFolder("added-later", "Added later");
        folders.Add(_root);

        // Still cached.
        Assert.Equal(before, catalogue.GetSkills().Count);

        catalogue.Refresh();

        Assert.Equal(before + 1, catalogue.GetSkills().Count);
        Assert.Contains(catalogue.GetSkills(), s => s.Name == "Added later");
    }

    /// <summary>
    /// A folder that has been moved or unplugged since it was added is skipped,
    /// not thrown over — the other folders still work.
    /// </summary>
    [Fact]
    public void A_folder_that_has_gone_away_is_skipped()
    {
        MakeSkillFolder("mine");
        var folders = Folders();
        folders.Add(_root);

        var gone = Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(gone);
        File.WriteAllText(Path.Combine(gone, "SKILL.md"), "---\nname: gone\n---\nbody");
        folders.Add(gone);
        Directory.Delete(gone, recursive: true);

        var source = new UserFolderSkillSource(folders);

        // Does not throw, and still finds the one that is there.
        Assert.NotEmpty(source.Discover());
    }
}
