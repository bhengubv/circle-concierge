namespace Concierge.Shared;

/// <summary>
/// Decides how long a tool call may run. A deployment-varying choice, so it belongs in
/// configuration rather than in the code that spawns the process.
/// </summary>
/// <remarks>
/// A phone on battery, a desktop on mains, and a test that must fail fast all want different
/// ceilings for the same command. Concierge already varies model choice by device state; the
/// time a command may burn belongs in the same place.
/// </remarks>
public interface IToolTimeoutPolicy
{
    /// <summary>How long the named tool may run before it is stopped.</summary>
    TimeSpan TimeoutFor(string toolName);
}

/// <summary>The same limit for every tool.</summary>
public sealed class FixedToolTimeoutPolicy : IToolTimeoutPolicy
{
    private readonly TimeSpan _timeout;

    public FixedToolTimeoutPolicy(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
    }

    /// <inheritdoc />
    public TimeSpan TimeoutFor(string toolName) => _timeout;
}

/// <summary>Shared policy instances.</summary>
public static class ToolTimeoutPolicy
{
    /// <summary>
    /// Five minutes, matching the ceiling the harness hardcoded before this seam existed —
    /// a host that sets nothing sees no change in behaviour.
    /// </summary>
    public static IToolTimeoutPolicy Default { get; } = new FixedToolTimeoutPolicy(TimeSpan.FromMinutes(5));
}
