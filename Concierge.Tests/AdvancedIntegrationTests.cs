using Aether.Routing;
using CircleAI.Core;
using Concierge.Ai;
using Concierge.Mesh;
using Concierge.Shared;
using Concierge.Web.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class AdvancedIntegrationTests
{
    [Fact]
    public void Persisted_mesh_identity_yields_the_same_node_tag_after_recreation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"concierge-mesh-identity-{Guid.NewGuid():N}.bin");
        try
        {
            var first = new AetherMeshTransportService(path, new InMemoryRouteStore(), services: null);
            var firstSnap = first.GetSnapshot();

            var second = new AetherMeshTransportService(path, new InMemoryRouteStore(), services: null);
            var secondSnap = second.GetSnapshot();

            Assert.Equal(firstSnap.NodeTag, secondSnap.NodeTag);
            Assert.True(firstSnap.IdentityIsPersisted, "Identity file should exist after the first constructor saves it.");
            Assert.True(secondSnap.IdentityIsPersisted, "Identity file should still exist for the second instance.");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Run_goal_publishes_a_dtn_bundle_when_mesh_is_wired()
    {
        using var provider = new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeMesh()
            .BuildServiceProvider();

        var harness = provider.GetRequiredService<IAgentHarnessService>();
        var mesh = provider.GetRequiredService<IMeshTransportService>();

        var before = mesh.GetSnapshot();
        Assert.Equal(0, before.PendingBundles);

        // The command is denied (shell chaining) — the harness still produces a run log and publishes it.
        await harness.RunGoalAsync("Verify publisher fires", ["dotnet --info && dotnet build"], approved: true);

        var after = mesh.GetSnapshot();
        Assert.True(after.PendingBundles >= 1, $"Expected at least one pending DTN bundle, got {after.PendingBundles}.");
    }

    [Fact]
    public async Task Run_goal_without_mesh_still_completes_using_the_null_publisher()
    {
        using var provider = new ServiceCollection()
            .AddConciergeCore()
            .BuildServiceProvider();

        var harness = provider.GetRequiredService<IAgentHarnessService>();

        var log = await harness.RunGoalAsync("Null publisher path", ["dotnet --info && dotnet build"], approved: true);

        Assert.Single(log.Results);
        Assert.Equal(ConciergeToolOutcome.Denied, log.Results[0].Outcome);
    }

    [Fact]
    public void Circleai_runtime_resolves_real_model_entries_from_the_embedded_registry()
    {
        using var provider = new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeAi()
            .BuildServiceProvider();

        var snapshot = provider.GetRequiredService<ILlmRuntimeService>().GetSnapshot();

        Assert.True(snapshot.RegistryAvailable, "Embedded registry should resolve known model names now that the JSON schema and resource path are fixed.");
        // 3.x registry ships the Qwen family with an "-MNN" suffix (was "-Q4"
        // on the 1.x line). Assert on the Qwen prefix only so a future 4.x
        // rename doesn't flap this test if the family stays.
        Assert.Contains(snapshot.ProbedModels, model => model.Available && model.Name.StartsWith("Qwen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Http_context_device_context_reads_locale_timezone_and_network_from_the_request()
    {
        var accessor = new TestHttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        accessor.HttpContext.Request.Headers.AcceptLanguage = "es-AR,en;q=0.8";
        accessor.HttpContext.Request.Headers["X-TimeZone"] = "America/Argentina/Buenos_Aires";

        var device = new HttpContextDeviceContext(accessor);

        Assert.Equal("es-AR", device.Locale);
        Assert.Equal("America/Argentina/Buenos_Aires", device.TimeZoneId);
        Assert.Equal("internet", device.NetworkType);
        Assert.Equal("concierge-web", device.ActiveAppId);
    }

    [Fact]
    public void Http_context_device_context_falls_back_to_local_culture_when_no_request_is_active()
    {
        var device = new HttpContextDeviceContext(new TestHttpContextAccessor());

        Assert.False(string.IsNullOrWhiteSpace(device.Locale), "Locale should fall back to CurrentCulture when no request is active.");
        Assert.Null(device.NetworkType);
    }

    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
