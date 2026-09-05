namespace Concierge.Shared.Tools;

/// <summary>
/// How much the assistant may do without being asked, decided up front.
///
/// This replaces a switch labelled "May act on its own" / "Ask before acting"
/// that did neither of those things. It bound to whether tools ran at all: off
/// meant nothing executed — not that it asked — and on still asked before every
/// write, because the tools ask for themselves. Both labels were wrong, in the
/// one part of the product where being wrong about permission matters most.
///
/// Three modes, because there are genuinely three things people want, and the
/// middle one is the default.
/// </summary>
public enum ToolPermissionMode
{
    /// <summary>
    /// Nothing runs. The assistant is told what tools exist and says what it
    /// would do, which is what you want before letting something loose on a
    /// folder you care about.
    /// </summary>
    PlanOnly = 0,

    /// <summary>
    /// Tools run, and anything that can change or destroy something asks first.
    /// The default, and the product's actual promise.
    /// </summary>
    AskFirst = 1,

    /// <summary>
    /// Tools run without asking, including the ones that write and the ones
    /// that came from other software.
    ///
    /// Deliberately not the default and deliberately named for what it does.
    /// It exists because a long run that stops at every write is not a long
    /// run, and pretending otherwise would push people to leave approval off
    /// permanently in some worse way.
    /// </summary>
    ActFreely = 2,
}

public static class ToolPermissionModes
{
    /// <summary>The word on the control.</summary>
    public static string Label(this ToolPermissionMode mode) => mode switch
    {
        ToolPermissionMode.PlanOnly => "Plan only",
        ToolPermissionMode.ActFreely => "Act freely",
        _ => "Ask first",
    };

    /// <summary>
    /// What it means, in one line, said plainly. A person choosing this is
    /// deciding what software may do to their machine.
    /// </summary>
    public static string Explain(this ToolPermissionMode mode) => mode switch
    {
        ToolPermissionMode.PlanOnly => "It says what it would do, and does none of it.",
        ToolPermissionMode.ActFreely => "It acts without asking, including writing files.",
        _ => "It reads freely, and asks before changing anything.",
    };

    /// <summary>Whether tool calls execute at all.</summary>
    public static bool RunsTools(this ToolPermissionMode mode) => mode != ToolPermissionMode.PlanOnly;

    /// <summary>Whether an acting tool should stop and ask.</summary>
    public static bool AsksBeforeActing(this ToolPermissionMode mode) => mode == ToolPermissionMode.AskFirst;
}
