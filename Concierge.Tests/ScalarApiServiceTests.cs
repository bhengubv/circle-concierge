using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ScalarApiServiceTests
{
    [Fact]
    public void Scalar_api_room_is_registered_in_core_services()
    {
        var services = new ServiceCollection()
            .AddConciergeCore()
            .BuildServiceProvider();

        Assert.NotNull(services.GetRequiredService<IScalarApiService>());
    }

    [Fact]
    public void Scalar_api_room_covers_the_expected_api_workflow()
    {
        var snapshot = new ScalarApiService().GetSnapshot();
        var kinds = snapshot.Capabilities.Select(capability => capability.Kind).ToHashSet();

        Assert.Equal("Scalar", snapshot.Name);
        Assert.Equal("https://github.com/scalar/scalar", snapshot.Repository);
        Assert.Equal("MIT", snapshot.License);
        Assert.Contains(ScalarCapabilityKind.ApiReference, kinds);
        Assert.Contains(ScalarCapabilityKind.ApiClient, kinds);
        Assert.Contains(ScalarCapabilityKind.OpenApiValidation, kinds);
        Assert.Contains(ScalarCapabilityKind.CodeExamples, kinds);
        Assert.Contains(ScalarCapabilityKind.MockServer, kinds);
        Assert.Contains(ScalarCapabilityKind.SdkGeneration, kinds);
        Assert.Contains(ScalarCapabilityKind.ApiAgent, kinds);
        Assert.Contains(ScalarCapabilityKind.OfflineClient, kinds);
    }

    [Fact]
    public void Scalar_api_room_is_safe_by_default()
    {
        var snapshot = new ScalarApiService().GetSnapshot();

        Assert.All(snapshot.Capabilities, capability =>
        {
            Assert.True(capability.BuiltInByDefault);
            Assert.False(string.IsNullOrWhiteSpace(capability.SafetyBoundary));
            Assert.DoesNotContain("secret", capability.ConciergeUse, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Contains(snapshot.Capabilities, capability =>
            capability.Id == "api-client"
            && capability.SafetyBoundary.Contains("approved endpoints", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scalar_api_room_templates_include_business_internal_and_learning_paths()
    {
        var templates = new ScalarApiService().GetSnapshot().Templates;

        Assert.Contains(templates, template => template.Id == "business-integration");
        Assert.Contains(templates, template => template.Id == "internal-api");
        Assert.Contains(templates, template => template.Id == "api-learning");
        Assert.All(templates, template => Assert.True(template.Steps.Count >= 5));
    }
}
