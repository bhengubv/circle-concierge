namespace Concierge.Shared;

public sealed record ProviderHealthPlan(
    string ProviderId,
    string Endpoint,
    ProviderAccessKind AccessKind,
    bool RequiresUserApiKey,
    bool SupportsHealthCheck,
    string SafetyBoundary);

public interface IProviderRuntimeService
{
    IReadOnlyList<ProviderHealthPlan> GetHealthPlans();
}

public sealed class ProviderRuntimeService(IConciergeStateService stateService) : IProviderRuntimeService
{
    public IReadOnlyList<ProviderHealthPlan> GetHealthPlans()
    {
        return stateService.GetSnapshot().Providers
            .Select(provider => new ProviderHealthPlan(
                provider.Id,
                provider.OfficialApi,
                provider.AccessKind,
                RequiresUserApiKey: true,
                SupportsHealthCheck: provider.AccessKind == ProviderAccessKind.DirectOfficialApi,
                SafetyBoundary: "Only official provider or official partner endpoints are allowed, and user keys stay owner-controlled."))
            .ToList();
    }
}
