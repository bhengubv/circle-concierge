namespace Concierge.Shared.Skills;

/// <summary>Marks the user-selected skill instructions that a text-only service link can carry.</summary>
public static class SelectedSkillPrompt
{
    public const string Start = "<concierge_selected_skill_instructions>";
    public const string End = "</concierge_selected_skill_instructions>";

    public static string Wrap(string instructions)
        => Start + "\n" + instructions.Trim() + "\n" + End;

    public static string? Extract(string systemPrompt)
    {
        var start = systemPrompt.IndexOf(Start, StringComparison.Ordinal);
        if (start < 0) return null;
        start += Start.Length;
        var end = systemPrompt.IndexOf(End, start, StringComparison.Ordinal);
        return end < start ? null : systemPrompt[start..end].Trim();
    }
}
