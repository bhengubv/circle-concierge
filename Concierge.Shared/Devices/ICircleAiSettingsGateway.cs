namespace Concierge.Shared.Devices;

public sealed record CircleAiSettingFact(string Name, string Value, string? Explanation = null);
public sealed record CircleAiSettingsSection(string Id, string Title, string Explanation,
    IReadOnlyList<CircleAiSettingFact> Facts, bool ManageInCircleAi = false);
public sealed record CircleAiSettingsReport(string Status, IReadOnlyList<CircleAiSettingsSection> Sections,
    bool CanOpenCircleAi, bool CanOpenPermissions);

/// <summary>Service-reported settings and service-owned management. Never owns models or service files.</summary>
public interface ICircleAiSettingsGateway
{
    Task<CircleAiSettingsReport> ReadAsync(CancellationToken cancellationToken);
    Task OpenCircleAiAsync();
    Task OpenPermissionsAsync();
}

/// <summary>Opens Circle AI's own approval flow. Only that service can grant access.</summary>
public interface ICircleAiVoiceSettingsLink
{
    Task<bool> RequestVoiceConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>Explicit refresh/approval bypasses read snapshots; never caches settings writes.</summary>
public interface ICircleAiSettingsCacheControl
{
    void InvalidateSettings();
}
