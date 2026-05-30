namespace Concierge.Shared;

public enum ScalarCapabilityKind
{
    ApiReference,
    ApiClient,
    OpenApiValidation,
    CodeExamples,
    MockServer,
    SdkGeneration,
    ApiAgent,
    OfflineClient
}

public sealed record ScalarCapability(
    string Id,
    string Name,
    ScalarCapabilityKind Kind,
    string Summary,
    string ConciergeUse,
    bool BuiltInByDefault,
    string SafetyBoundary);

public sealed record ApiWorkspaceTemplate(
    string Id,
    string Name,
    string Purpose,
    IReadOnlyList<string> Steps);

public sealed record ScalarIntegrationSnapshot(
    string Name,
    string Repository,
    string License,
    string IntegrationMode,
    IReadOnlyList<ScalarCapability> Capabilities,
    IReadOnlyList<ApiWorkspaceTemplate> Templates);
