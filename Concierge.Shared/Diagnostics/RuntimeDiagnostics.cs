using Concierge.Shared.Chat;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Shared.Diagnostics;

/// <summary>One thing that was looked at and what was found.</summary>
/// <param name="Name">Short stable identifier for the check.</param>
/// <param name="IsHealthy">Whether it is in the state it should be.</param>
/// <param name="Detail">What was actually observed, in plain words.</param>
public sealed record DiagnosticCheckResult(string Name, bool IsHealthy, string Detail);

/// <summary>A snapshot of what is wired right now.</summary>
/// <param name="TakenAt">When the snapshot was taken.</param>
/// <param name="Checks">Everything that was looked at.</param>
public sealed record RuntimeDiagnosticReport(DateTimeOffset TakenAt, IReadOnlyList<DiagnosticCheckResult> Checks)
{
    /// <summary>Whether everything looked at is in the state it should be.</summary>
    public bool IsHealthy => Checks.All(check => check.IsHealthy);
}

/// <summary>
/// Reports what the running application actually has, by asking the container.
/// </summary>
/// <remarks>
/// Deliberately different from <see cref="IProductionReadinessService"/>, which returns a
/// fixed list of items all marked ready. That list describes what was true when it was
/// written; this describes what is true now, on this device, in this host. On a phone where
/// a host forgot a registration, the difference is the whole bug.
/// </remarks>
public interface IRuntimeDiagnostics
{
    /// <summary>Look at the running system and report what is there.</summary>
    RuntimeDiagnosticReport Inspect();
}

/// <inheritdoc />
public sealed class RuntimeDiagnostics : IRuntimeDiagnostics
{
    private readonly IServiceProvider _services;

    public RuntimeDiagnostics(IServiceProvider services)
        => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public RuntimeDiagnosticReport Inspect()
    {
        var checks = new List<DiagnosticCheckResult>
        {
            Registered<IChatRuntime>("chat-runtime", "the model the user talks to"),
            Registered<IToolApprovalService>("tool-approval", "who is asked before a tool acts"),
            Registered<IConversationStore>("conversation-store", "where conversations are kept"),
            Registered<ILlmRuntimeService>("llm-runtime", "the on-device engine"),
            Registered<IMeshTransportService>("mesh-transport", "the offline transport"),
        };

        return new RuntimeDiagnosticReport(DateTimeOffset.UtcNow, checks);
    }

    private DiagnosticCheckResult Registered<T>(string name, string what) where T : class
    {
        var service = _services.GetService<T>();
        return service is null
            ? new DiagnosticCheckResult(name, IsHealthy: false, $"Not wired: {what} has no registration.")
            : new DiagnosticCheckResult(name, IsHealthy: true, $"Wired: {what} is {service.GetType().Name}.");
    }
}
