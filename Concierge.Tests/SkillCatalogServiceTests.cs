using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class SkillCatalogServiceTests
{
    [Fact]
    public void Curated_skills_are_bundled_inside_the_solution()
    {
        var root = FindWorkspaceRoot();
        var curatedRoot = Path.Combine(root, "Concierge.Shared", "Skills", "Curated");

        Assert.True(Directory.Exists(curatedRoot));
        Assert.True(Directory.GetDirectories(curatedRoot).Length >= 69);
        Assert.True(Directory.GetFiles(curatedRoot, "SKILL.md", SearchOption.AllDirectories).Length >= 69);
    }

    [Fact]
    public void Skill_catalog_loads_the_bundled_curated_skills()
    {
        var skills = new SkillCatalogService().GetSkills();

        Assert.True(skills.Count >= 69);
        Assert.Equal(skills.Count, skills.Select(skill => skill.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(skills, skill => skill.Id == "finance-accounting");
        Assert.Contains(skills, skill => skill.Id == "human-resources");
        Assert.Contains(skills, skill => skill.Id == "legal-admin");
        Assert.Contains(skills, skill => skill.Id == "css-js-animation");
        Assert.Contains(skills, skill => skill.Id == "mobile-game-development");
        Assert.Contains(skills, skill => skill.Id == "console-game-development");
        Assert.Contains(skills, skill => skill.Id == "public-sector-government");
        Assert.Contains(skills, skill => skill.Id == "mining-resources-operations");
    }

    [Fact]
    public void Concierge_state_uses_bundled_skills_not_the_old_tiny_placeholder()
    {
        var services = new ServiceCollection()
            .AddConciergeCore()
            .BuildServiceProvider();

        var snapshot = services.GetRequiredService<IConciergeStateService>().GetSnapshot();

        Assert.True(snapshot.Skills.Count >= 69);
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Id == "skills"
            && diagnostic.Evidence.Contains("bundled into the solution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skill_catalog_covers_human_business_and_creative_areas()
    {
        var areas = new SkillCatalogService().GetSkills()
            .Select(skill => skill.Area)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("Business", areas);
        Assert.Contains("Engineering", areas);
        Assert.Contains(areas, area => area.Contains("Creative", StringComparison.OrdinalIgnoreCase) || area.Contains("Design", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Industry", areas);
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Concierge.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate Concierge.slnx from test output directory.");
    }
}
