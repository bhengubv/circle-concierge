namespace Concierge.Shared.Safety;

// Front-end family controls are independent of any model or AI package.
public enum SafetyVerdict { Allow, Flag, Refuse }

public sealed record SafetyFinding(SafetyVerdict Verdict, string Category, string Reason, float Confidence);

public sealed record SafetyAuditEntry(DateTimeOffset AtUtc, string UserId, string Action,
    SafetyVerdict Verdict, string Reason);

public interface IContentFilter
{
    string BackendId { get; }
    ValueTask<SafetyFinding> ClassifyAsync(string text, CancellationToken ct = default);
}

public interface IRefusalPolicy
{
    string BackendId { get; }
    ValueTask<bool> ShouldRefuseAsync(IReadOnlyList<SafetyFinding> findings, CancellationToken ct = default);
}

public interface ISafetyAuditLog
{
    string BackendId { get; }
    ValueTask LogAsync(SafetyAuditEntry entry, CancellationToken ct = default);
    ValueTask<IReadOnlyList<SafetyAuditEntry>> ReadAsync(string? userId, int limit = 100,
        CancellationToken ct = default);
}
