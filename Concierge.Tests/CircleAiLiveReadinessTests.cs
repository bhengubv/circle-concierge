using CircleAI.Assistant.Lifecycle;
using CircleAI.Linking;
using Concierge.Shared.Devices;

namespace Concierge.Tests;

public sealed class CircleAiLiveReadinessTests
{
    private static ComponentState State(string component, BrainPhase phase)
        => ComponentState.Of(component, phase, "Reported by service", DateTimeOffset.UtcNow, "service-selected");
    private static ReadinessSnapshot Snapshot(BrainPhase brain, params ComponentState[] components)
        => new(ReadinessSnapshot.CurrentSchema, 1, "boot-one", State(ReadinessComponents.Brain, brain), components, [], []);

    [Theory]
    [InlineData(BrainPhase.NoModel)]
    [InlineData(BrainPhase.Installed)]
    [InlineData(BrainPhase.Loading)]
    [InlineData(BrainPhase.Unknown)]
    [InlineData(BrainPhase.Paused)]
    [InlineData(BrainPhase.Failed)]
    public void Ready_voice_or_downloaded_files_cannot_make_text_ready(BrainPhase phase)
    {
        var snapshot = CircleAiLiveReadiness.Read(LinkAskRows.Status(Snapshot(phase,
            State(ReadinessComponents.Voice, BrainPhase.Ready))));
        Assert.False(CircleAiLiveReadiness.Chat(snapshot, "60").Ready);
    }

    [Fact]
    public void Text_can_be_ready_while_voice_and_downloads_are_not()
    {
        var snapshot = Snapshot(BrainPhase.Ready, State(ReadinessComponents.Voice, BrainPhase.NoModel),
            State(ReadinessComponents.Fetch, BrainPhase.Paused));
        Assert.True(CircleAiLiveReadiness.Chat(snapshot, "60").Ready);
        var facts = CircleAiLiveReadiness.Facts(snapshot);
        Assert.Equal("Ready", facts.Single(f => f.Name == "Text conversations").Value);
        Assert.Equal("Model not ready", facts.Single(f => f.Name == "Spoken replies").Value);
        Assert.StartsWith("Paused", facts.Single(f => f.Name == "Resource downloads").Value);
        Assert.Equal("Not reported", facts.Single(f => f.Name == "Wake word").Value);
    }

    [Fact]
    public void Automatic_fetch_is_waitable_but_missing_resources_alone_are_not()
    {
        Assert.True(CircleAiLiveReadiness.Chat(Snapshot(BrainPhase.NoModel,
            State(ReadinessComponents.Fetch, BrainPhase.Loading)), "60").CanWaitForPreparation);
        Assert.False(CircleAiLiveReadiness.Chat(Snapshot(BrainPhase.NoModel), "60").CanWaitForPreparation);
    }

    [Fact]
    public void Malformed_unknown_schema_or_unchanged_without_a_snapshot_never_claims_ready()
    {
        Assert.Throws<InvalidOperationException>(() => CircleAiLiveReadiness.Read([]));
        Assert.Throws<InvalidOperationException>(() => CircleAiLiveReadiness.Read([LinkAskRows.Unchanged(1)]));
        Assert.Throws<InvalidOperationException>(() => CircleAiLiveReadiness.Read(LinkAskRows.Status(
            Snapshot(BrainPhase.Ready) with { Schema = 999 })));
    }

    [Fact]
    public void Too_big_is_a_service_resource_plan_problem_not_a_user_setting()
    {
        var fact = Assert.Single(CircleAiSettingsRows.Project("Abilities", [["Talking", "Speak", "TooBig", "", ""]]));
        Assert.Equal("Blocked", fact.Value);
        Assert.Contains("Model selection belongs to the service", fact.Explanation);
    }
}
