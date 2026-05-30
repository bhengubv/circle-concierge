using Concierge.Ai;
using Concierge.Media;
using Concierge.Mesh;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class IntegrationContractTests
{
    [Fact]
    public void AddConciergeCore_registers_null_object_defaults_for_every_integration()
    {
        using var provider = new ServiceCollection()
            .AddConciergeCore()
            .BuildServiceProvider();

        var llm = provider.GetRequiredService<ILlmRuntimeService>().GetSnapshot();
        var mesh = provider.GetRequiredService<IMeshTransportService>().GetSnapshot();
        var media = provider.GetRequiredService<IMediaStudioService>().GetSnapshot();

        Assert.Equal("Null", llm.Engine);
        Assert.Equal("Null", mesh.Engine);
        Assert.Equal("Null", media.Engine);
        Assert.False(llm.RegistryAvailable);
        Assert.False(mesh.IsActive);
        Assert.Empty(media.RecentItems);
    }

    [Fact]
    public void AddConciergeAi_replaces_the_runtime_with_the_circleai_bridge()
    {
        using var provider = new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeAi()
            .BuildServiceProvider();

        var snapshot = provider.GetRequiredService<ILlmRuntimeService>().GetSnapshot();

        Assert.Equal("CircleAI", snapshot.Engine);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.EngineVersion));
        Assert.NotEqual("0.0.0", snapshot.EngineVersion);
        Assert.NotEmpty(snapshot.ProbedModels);
        Assert.All(snapshot.ProbedModels, model => Assert.False(string.IsNullOrWhiteSpace(model.Name)));
        Assert.Contains("locale=", snapshot.DeviceSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AddConciergeMesh_replaces_the_transport_with_an_aether_protocol_node()
    {
        using var provider = new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeMesh()
            .BuildServiceProvider();

        var snapshot = provider.GetRequiredService<IMeshTransportService>().GetSnapshot();

        Assert.Equal("aether-protocol", snapshot.Engine);
        Assert.True(snapshot.IsActive);
        // AetherTag formats as XXXXX-XXXXX (10 Crockford base-32 chars + hyphen).
        Assert.Matches("^[0-9A-HJ-NP-TV-Z]{5}-[0-9A-HJ-NP-TV-Z]{5}$", snapshot.NodeTag);
        Assert.Equal(0, snapshot.CachedRoutes);
    }

    [Fact]
    public void AddConciergeMedia_replaces_the_studio_with_the_aether_media_library()
    {
        using var provider = new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeMedia()
            .BuildServiceProvider();

        var studio = provider.GetRequiredService<IMediaStudioService>();
        var snapshot = studio.GetSnapshot();

        Assert.Equal("aether-media", snapshot.Engine);
        Assert.True(snapshot.LibraryItemCount >= 2, "Demo seed should produce at least two items.");
        Assert.Contains(snapshot.RecentItems, item => item.ContentType.StartsWith("video/", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentItems, item => item.ContentType.StartsWith("audio/", StringComparison.Ordinal));
        Assert.Contains("video", snapshot.SupportedKinds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("audio", snapshot.SupportedKinds, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repeated_media_snapshot_calls_do_not_reseed_the_library()
    {
        using var provider = new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeMedia()
            .BuildServiceProvider();

        var studio = provider.GetRequiredService<IMediaStudioService>();
        var first = studio.GetSnapshot();
        var second = studio.GetSnapshot();

        Assert.Equal(first.LibraryItemCount, second.LibraryItemCount);
    }

    [Fact]
    public void Full_integration_stack_wires_every_service_without_collision()
    {
        using var provider = new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeAi()
            .AddConciergeMesh()
            .AddConciergeMedia()
            .BuildServiceProvider();

        // Each surface resolves to exactly one concrete implementation; if anything
        // double-registered, GetServices(...) would return more than one entry.
        Assert.Single(provider.GetServices<ILlmRuntimeService>());
        Assert.Single(provider.GetServices<IMeshTransportService>());
        Assert.Single(provider.GetServices<IMediaStudioService>());
    }

    [Fact]
    public void Web_page_for_business_apis_surfaces_the_media_studio()
    {
        var root = FindWorkspaceRoot();
        var page = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Pages", "BusinessApis.razor"));

        Assert.Contains("@inject IMediaStudioService", page, StringComparison.Ordinal);
        Assert.Contains("media.RecentItems", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_page_for_settings_surfaces_the_on_device_runtime()
    {
        var root = FindWorkspaceRoot();
        var page = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Pages", "Settings.razor"));

        Assert.Contains("@inject ILlmRuntimeService", page, StringComparison.Ordinal);
        Assert.Contains("runtime.ProbedModels", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_page_for_beyond_surfaces_the_mesh_transport()
    {
        var root = FindWorkspaceRoot();
        var page = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Pages", "Beyond.razor"));

        Assert.Contains("@inject IMeshTransportService", page, StringComparison.Ordinal);
        Assert.Contains("mesh.NodeTag", page, StringComparison.Ordinal);
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Concierge.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate Concierge.slnx from test output directory.");
    }
}
