namespace Concierge.Shared.Planning;

/// <summary>Focused views over existing Concierge skills and tools, not separate AI models.</summary>
public sealed record VocationTool(string Name, string Label);

public sealed record VocationMode(
    string Id,
    string Name,
    string SkillId,
    IReadOnlyList<VocationTool> Tools);

public static class VocationModes
{
    public const string SocialMediaManagerId = "social-media-manager";

    public static VocationMode? Find(string? id) => id switch
    {
        SocialMediaManagerId => SocialMediaManager,
        _ => null,
    };

    public static string[] SkillIdsForProject(ConciergeProject? project, IEnumerable<string> selectedSkills)
        => Find(project?.VocationModeId) is { } vocation
            ? selectedSkills.Append(vocation.SkillId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : selectedSkills.ToArray();

    public static VocationMode SocialMediaManager { get; } = new(
        SocialMediaManagerId,
        "Social Media Manager",
        "marketing-growth",
        [new("web_search", "Research"),
         new("make_picture", "Create images"),
         new("design_save", "Export media"),
         new("schedule_create", "Schedule a reminder")]);
}
