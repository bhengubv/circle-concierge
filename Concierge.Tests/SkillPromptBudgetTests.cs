using Concierge.Shared;
using Concierge.Shared.Skills;

namespace Concierge.Tests;

/// <summary>
/// How much of the prompt the switched-on skills may take.
///
/// `SkillActivation` has computed an `EstimatedTokens` since the day it was
/// written and nothing ever read it, so there was no budget at all: switch on
/// twenty skills and twenty full bodies went into every message, for the whole
/// conversation, whether or not any of them applied. That is the same
/// written-and-never-reached shape as `McpClient`, `FileTodoStore`,
/// `ProcessHookBridge` and the sandbox — a value computed for a decision nobody
/// makes.
///
/// Hallmark's rule is index-then-pick: load the index, then only the entries you
/// need, because pre-loading the rest "costs ~7K tokens for nothing". The
/// translation here is that a skill over budget is **named, not dropped** —
/// somebody who switched it on can still see it is on.
/// </summary>
public sealed class SkillPromptBudgetTests
{
    private sealed class FakeSource(params (string Id, string Name, string Body)[] skills) : ISkillSource
    {
        public string Name => "fake";

        public IReadOnlyList<SkillDescriptor> Discover() =>
            [.. skills.Select(s => new SkillDescriptor(
                s.Id, s.Name, "testing", $"The {s.Name} skill.", $"fake://{s.Id}", "bundled", [], null, []))];

        public string? LoadBody(SkillDescriptor descriptor)
            => skills.FirstOrDefault(s => s.Id == descriptor.Id).Body;
    }

    private static SkillRuntime Runtime(params (string Id, string Name, string Body)[] skills)
        => new(new StubCatalog(), [new FakeSource(skills)]);

    private sealed class StubCatalog : ISkillCatalogService
    {
        public IReadOnlyList<SkillInfo> GetSkills() => [];
    }

    private static string Body(int tokens) => new('x', tokens * 4);

    [Fact]
    public void One_skill_is_quoted_in_full_whatever_the_budget()
    {
        var prompt = Runtime(("a", "Alpha", Body(50_000)))
            .ComposeSystemPrompt(["a"], budgetTokens: 10);

        Assert.Contains("Alpha", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("not quoted here", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Skills_that_fit_are_all_quoted()
    {
        var prompt = Runtime(("a", "Alpha", Body(100)), ("b", "Bravo", Body(100)))
            .ComposeSystemPrompt(["a", "b"], budgetTokens: 8_000);

        Assert.Contains("## Skill: Alpha", prompt, StringComparison.Ordinal);
        Assert.Contains("## Skill: Bravo", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("not quoted here", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of the whole change: over budget, a skill is named rather than
    /// silently vanishing. Somebody who switched it on can still tell it is on.
    /// </summary>
    [Fact]
    public void A_skill_over_budget_is_named_rather_than_dropped()
    {
        var prompt = Runtime(
                ("a", "Alpha", Body(100)),
                ("b", "Bravo", Body(5_000)))
            .ComposeSystemPrompt(["a", "b"], budgetTokens: 1_000);

        Assert.Contains("## Skill: Alpha", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Skill: Bravo", prompt, StringComparison.Ordinal);

        Assert.Contains("not quoted here", prompt, StringComparison.Ordinal);
        Assert.Contains("Bravo", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A budget that can reject everything produces a prompt that mentions skills
    /// and contains none of them, which is worse than being over budget.
    /// </summary>
    [Fact]
    public void The_first_skill_is_always_quoted_even_when_it_alone_blows_the_budget()
    {
        var prompt = Runtime(
                ("a", "Alpha", Body(9_000)),
                ("b", "Bravo", Body(9_000)))
            .ComposeSystemPrompt(["a", "b"], budgetTokens: 100);

        Assert.Contains("## Skill: Alpha", prompt, StringComparison.Ordinal);
        Assert.Contains("Bravo", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Skill: Bravo", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_named_ones_carry_their_description_so_the_model_can_ask_for_one()
    {
        var prompt = Runtime(
                ("a", "Alpha", Body(100)),
                ("b", "Bravo", Body(5_000)))
            .ComposeSystemPrompt(["a", "b"], budgetTokens: 1_000);

        Assert.Contains("- Bravo — The Bravo skill.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_switched_on_produces_nothing()
        => Assert.Equal(string.Empty, Runtime(("a", "Alpha", Body(10))).ComposeSystemPrompt([]));

    [Fact]
    public void The_default_budget_is_generous_enough_for_ordinary_use()
    {
        // Three ordinary skills — a few hundred tokens each — must never be
        // trimmed. The budget exists for the pathological case, not to police
        // somebody switching on the three they use.
        var prompt = Runtime(
                ("a", "Alpha", Body(400)),
                ("b", "Bravo", Body(400)),
                ("c", "Charlie", Body(400)))
            .ComposeSystemPrompt(["a", "b", "c"]);

        Assert.DoesNotContain("not quoted here", prompt, StringComparison.Ordinal);
        Assert.True(SkillRuntime.DefaultBudgetTokens >= 4_000);
    }
}
