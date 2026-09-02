using Concierge.Shared;
using Concierge.Shared.Diagnostics;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// What runtime diagnostics must do (parity feature 50): report what is actually wired right
/// now, rather than a list that says everything is fine.
/// </summary>
/// <remarks>
/// <c>ProductionReadinessService</c> returns eleven items all hardcoded to Ready. That is a
/// claim, not a check — it would report a healthy system with the model unwired and the mesh
/// dead. This reads the live container instead.
/// </remarks>
public sealed class RuntimeDiagnosticsTests
{
    [Fact]
    public void A_bare_container_reports_the_chat_engine_as_not_wired()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var report = new RuntimeDiagnostics(provider).Inspect();

        Assert.Contains(report.Checks, check => check.Name == "chat-runtime" && !check.IsHealthy);
    }

    [Fact]
    public void A_wired_service_is_reported_as_healthy()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IToolApprovalService>(UnavailableToolApprovalService.Instance)
            .BuildServiceProvider();

        var report = new RuntimeDiagnostics(provider).Inspect();

        Assert.Contains(report.Checks, check => check.Name == "tool-approval" && check.IsHealthy);
    }

    [Fact]
    public void A_report_is_unhealthy_when_any_check_is()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        Assert.False(new RuntimeDiagnostics(provider).Inspect().IsHealthy);
    }

    [Fact]
    public void Every_check_says_what_it_looked_at()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var report = new RuntimeDiagnostics(provider).Inspect();

        Assert.All(report.Checks, check => Assert.False(string.IsNullOrWhiteSpace(check.Detail)));
    }

    [Fact]
    public void A_report_is_taken_at_a_moment_and_says_when()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var report = new RuntimeDiagnostics(provider).Inspect();

        Assert.True(report.TakenAt > before);
    }
}

/// <summary>
/// What the feedback channel must do (parity feature 51): capture a complaint where it
/// happened, with enough context to act on.
/// </summary>
/// <remarks>
/// On a device you cannot reach, in a place with no signal, this is the only bug report you
/// are ever going to get. It has to work offline and keep what it captured until it can be
/// sent.
/// </remarks>
public sealed class FeedbackChannelTests : IDisposable
{
    private readonly string _root;

    public FeedbackChannelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"concierge-feedback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp directory.
        }
    }

    [Fact]
    public async Task A_report_is_kept()
    {
        var channel = NewChannel();

        await channel.ReportAsync("the answer was wrong", FeedbackKind.BadAnswer);

        Assert.Single(await channel.PendingAsync());
    }

    [Fact]
    public async Task A_report_keeps_what_was_said()
    {
        var channel = NewChannel();

        await channel.ReportAsync("the answer was wrong", FeedbackKind.BadAnswer);

        Assert.Equal("the answer was wrong", (await channel.PendingAsync())[0].Message);
    }

    [Fact]
    public async Task A_report_keeps_the_conversation_it_came_from()
    {
        var channel = NewChannel();
        var conversation = Guid.NewGuid();

        await channel.ReportAsync("wrong", FeedbackKind.BadAnswer, conversation);

        Assert.Equal(conversation, (await channel.PendingAsync())[0].ConversationId);
    }

    [Fact]
    public async Task A_report_survives_a_restart()
    {
        var path = Path.Combine(_root, "feedback.jsonl");
        await new FileFeedbackChannel(path).ReportAsync("still here", FeedbackKind.Crash);

        Assert.Single(await new FileFeedbackChannel(path).PendingAsync());
    }

    [Fact]
    public async Task Several_reports_are_all_kept()
    {
        var channel = NewChannel();

        await channel.ReportAsync("one", FeedbackKind.BadAnswer);
        await channel.ReportAsync("two", FeedbackKind.Crash);

        Assert.Equal(2, (await channel.PendingAsync()).Count);
    }

    [Fact]
    public async Task An_empty_report_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewChannel().ReportAsync("  ", FeedbackKind.BadAnswer));
    }

    [Fact]
    public async Task Sent_reports_are_no_longer_pending()
    {
        var channel = NewChannel();
        await channel.ReportAsync("one", FeedbackKind.BadAnswer);

        await channel.MarkSentAsync((await channel.PendingAsync()).Select(report => report.Id));

        Assert.Empty(await channel.PendingAsync());
    }

    [Fact]
    public async Task Sending_does_not_destroy_the_record()
    {
        var channel = NewChannel();
        await channel.ReportAsync("one", FeedbackKind.BadAnswer);

        await channel.MarkSentAsync((await channel.PendingAsync()).Select(report => report.Id));

        Assert.Single(await channel.AllAsync());
    }

    private FileFeedbackChannel NewChannel() => new(Path.Combine(_root, $"{Guid.NewGuid():N}.jsonl"));
}
