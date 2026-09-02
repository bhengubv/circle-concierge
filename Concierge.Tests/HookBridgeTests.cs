using System.Text.Json.Nodes;
using Concierge.Shared.Hooks;

namespace Concierge.Tests;

/// <summary>
/// What the hook bridge must do (parity feature 32): run hooks written for other harnesses,
/// obey a refusal, and shrug off a broken one.
/// </summary>
/// <remarks>
/// The format is JSON in, JSON out. Owning it means an existing ecosystem of hooks works here
/// unported, and nothing new is depended on.
/// </remarks>
public sealed class HookDecisionTests
{
    [Fact]
    public void A_hook_that_exits_cleanly_allows()
    {
        Assert.True(ProcessHookBridge.ReadDecision(null, exitCode: 0).Allowed);
    }

    [Fact]
    public void A_hook_that_exits_badly_refuses()
    {
        // The simplest possible hook is a script that exits 1. It has to work.
        Assert.False(ProcessHookBridge.ReadDecision(null, exitCode: 1).Allowed);
    }

    [Fact]
    public void A_refusal_by_exit_code_still_says_something()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProcessHookBridge.ReadDecision(null, 1).Reason));
    }

    [Fact]
    public void A_hook_can_refuse_in_json()
    {
        var decision = ProcessHookBridge.ReadDecision("""{"allow":false,"reason":"not on a school night"}""", 0);

        Assert.False(decision.Allowed);
        Assert.Equal("not on a school night", decision.Reason);
    }

    [Fact]
    public void A_hook_can_allow_in_json()
    {
        Assert.True(ProcessHookBridge.ReadDecision("""{"allow":true}""", 0).Allowed);
    }

    [Fact]
    public void The_continue_spelling_is_understood_too()
    {
        // Different harnesses spell the same field differently; both are accepted.
        Assert.False(ProcessHookBridge.ReadDecision("""{"continue":false}""", 0).Allowed);
    }

    [Fact]
    public void A_hook_can_add_context()
    {
        var decision = ProcessHookBridge.ReadDecision("""{"allow":true,"context":"the user is nine"}""", 0);

        Assert.Equal("the user is nine", decision.ExtraContext);
    }

    [Fact]
    public void The_additional_context_spelling_is_understood_too()
    {
        var decision = ProcessHookBridge.ReadDecision("""{"allow":true,"additionalContext":"school holidays"}""", 0);

        Assert.Equal("school holidays", decision.ExtraContext);
    }

    [Fact]
    public void A_hook_that_prints_nonsense_falls_back_to_its_exit_code()
    {
        Assert.True(ProcessHookBridge.ReadDecision("this is not json", 0).Allowed);
        Assert.False(ProcessHookBridge.ReadDecision("this is not json", 1).Allowed);
    }
}

/// <summary>Running real hook processes.</summary>
public sealed class ProcessHookBridgeTests
{
    [Fact]
    public async Task No_hooks_means_no_objection()
    {
        var bridge = new ProcessHookBridge([]);

        Assert.True((await bridge.RunAsync(HookEvent.PreToolUse, new JsonObject())).Allowed);
    }

    [Fact]
    public async Task A_hook_that_refuses_stops_the_thing_it_was_asked_about()
    {
        var bridge = new ProcessHookBridge([Refusing(HookEvent.PreToolUse)]);

        Assert.False((await bridge.RunAsync(HookEvent.PreToolUse, new JsonObject())).Allowed);
    }

    [Fact]
    public async Task A_hook_registered_for_another_event_is_not_consulted()
    {
        var bridge = new ProcessHookBridge([Refusing(HookEvent.Stop)]);

        Assert.True((await bridge.RunAsync(HookEvent.PreToolUse, new JsonObject())).Allowed);
    }

    [Fact]
    public async Task A_missing_hook_program_is_treated_as_no_opinion()
    {
        // A broken entry in someone's configuration must not make the assistant unusable.
        var bridge = new ProcessHookBridge([
            new HookRegistration(HookEvent.PreToolUse, "this-program-does-not-exist", []),
        ]);

        Assert.True((await bridge.RunAsync(HookEvent.PreToolUse, new JsonObject())).Allowed);
    }

    [Fact]
    public async Task One_refusal_among_several_hooks_is_enough()
    {
        var bridge = new ProcessHookBridge([
            Allowing(HookEvent.PreToolUse),
            Refusing(HookEvent.PreToolUse),
        ]);

        Assert.False((await bridge.RunAsync(HookEvent.PreToolUse, new JsonObject())).Allowed);
    }

    [Fact]
    public async Task A_hook_that_never_reads_its_input_is_still_obeyed()
    {
        // Most hooks decide from their arguments and never touch stdin. Writing the payload to
        // a program that has already exited breaks the pipe, and treating that as "the hook is
        // broken" silently turned a refusal into permission. It surfaced as a test that passed
        // alone and failed under load — the child usually exits after the write, not before.
        var payload = new JsonObject { ["blob"] = new string('x', 256 * 1024) };

        var decision = await new ProcessHookBridge([Refusing(HookEvent.PreToolUse)])
            .RunAsync(HookEvent.PreToolUse, payload);

        Assert.False(decision.Allowed);
    }

    private static HookRegistration Refusing(HookEvent hookEvent)
        => OperatingSystem.IsWindows()
            ? new HookRegistration(hookEvent, "cmd.exe", ["/c", "exit 1"])
            : new HookRegistration(hookEvent, "/bin/sh", ["-c", "exit 1"]);

    private static HookRegistration Allowing(HookEvent hookEvent)
        => OperatingSystem.IsWindows()
            ? new HookRegistration(hookEvent, "cmd.exe", ["/c", "exit 0"])
            : new HookRegistration(hookEvent, "/bin/sh", ["-c", "exit 0"]);
}
