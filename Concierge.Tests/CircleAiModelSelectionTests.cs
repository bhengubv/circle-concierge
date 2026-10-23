using CircleAI.Core;
using CircleAI.Inference;
using CircleAI.Core.Models;
using Concierge.Ai;

namespace Concierge.Tests;

public sealed class CircleAiModelSelectionTests
{
    [Fact]
    public void CatalogueSelectionAccountsForMnnMappingModeAndOffersBestCompatibleModel()
    {
        var modelDirectory = Path.Combine(Path.GetTempPath(), $"concierge-model-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelDirectory);
        var previousMemoryProbe = DeviceProbe.PlatformMemoryProbe;

        try
        {
            const long gib = 1024L * 1024 * 1024;
            DeviceProbe.PlatformMemoryProbe = () => new DeviceProbe.PlatformMemory(
                (long)(20.1119254528 * gib),
                null,
                31L * gib + 805306368L);
            var probe = DeviceProbe.Snapshot(modelDirectory);

            using var selector = new CircleAiModelSelectorContext(modelDirectory, mmapAllowed: false);
            var assessment = selector.Assess(probe);
            var selected = selector.BestFit(probe, ChatCapability.Default | ChatCapability.Tools);

            Assert.True(assessment.Compatible < assessment.Assessed);
            // Version-pinned catalogue expectation: the 3.9.1 embedded registry
            // ranks this eligible entry first for this measured non-mmap probe.
            Assert.Equal("Qwen3.5-4B-MNN", selected.ModelId);
            Assert.Equal(SelectionQuality.Good, selected.Quality);
            Assert.True(selected.RequiresDownload);
            Assert.Empty(Directory.EnumerateFiles(modelDirectory, "*", SearchOption.AllDirectories));
        }
        finally
        {
            DeviceProbe.PlatformMemoryProbe = previousMemoryProbe;
            Directory.Delete(modelDirectory, recursive: true);
        }
    }

    [Fact]
    public void Eligible_tool_models_are_ordered_smallest_to_largest_for_progressive_setup()
    {
        var modelDirectory = Path.Combine(Path.GetTempPath(), $"concierge-model-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelDirectory);
        var previousMemoryProbe = DeviceProbe.PlatformMemoryProbe;

        try
        {
            const long gib = 1024L * 1024 * 1024;
            DeviceProbe.PlatformMemoryProbe = () => new DeviceProbe.PlatformMemory(
                (long)(20.1119254528 * gib),
                null,
                31L * gib + 805306368L);
            var probe = DeviceProbe.Snapshot(modelDirectory);

            using var selector = new CircleAiModelSelectorContext(modelDirectory, mmapAllowed: false);
            selector.Assess(probe);
            var best = selector.BestFit(probe, ChatCapability.Default | ChatCapability.Tools);
            var candidates = selector.EligibleCandidatesSmallestFirst(
                probe, ChatCapability.Default | ChatCapability.Tools);

            Assert.NotEmpty(candidates);
            Assert.Equal("Qwen3.5-0.8B-MNN", candidates[0].ModelId);
            Assert.Equal(best.ModelId, candidates[^1].ModelId);
            Assert.True(candidates.Zip(candidates.Skip(1), (left, right) =>
                left.EstimatedBytes <= right.EstimatedBytes).All(isAscending => isAscending));
            Assert.All(candidates, candidate => Assert.Equal(SelectionQuality.Good, candidate.Quality));
            Assert.Empty(Directory.EnumerateFiles(modelDirectory, "*", SearchOption.AllDirectories));
        }
        finally
        {
            DeviceProbe.PlatformMemoryProbe = previousMemoryProbe;
            Directory.Delete(modelDirectory, recursive: true);
        }
    }

    [Fact]
    public void Assessment_preserves_installed_models_until_a_replacement_is_proven()
    {
        var modelDirectory = Path.Combine(Path.GetTempPath(), $"concierge-model-housekeeping-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelDirectory);
        var previousMemoryProbe = DeviceProbe.PlatformMemoryProbe;

        try
        {
            const long gib = 1024L * 1024 * 1024;
            DeviceProbe.PlatformMemoryProbe = () => new DeviceProbe.PlatformMemory(
                20L * gib,
                null,
                40L * gib);
            foreach (var model in new[] { "Qwen3-0.6B-MNN", "Qwen3.5-0.8B-MNN" })
            {
                var directory = Path.Combine(modelDirectory, model);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "installed.json"), "{}");
            }
            var probe = DeviceProbe.Snapshot(modelDirectory);

            using var selector = new CircleAiModelSelectorContext(modelDirectory, mmapAllowed: false);
            selector.Assess(probe);

            // 3.9.1 requires a successful-load stamp before reclaiming the old
            // model. Merely downloading its replacement is not that proof.
            Assert.True(Directory.Exists(Path.Combine(modelDirectory, "Qwen3-0.6B-MNN")));
            Assert.True(Directory.Exists(Path.Combine(modelDirectory, "Qwen3.5-0.8B-MNN")));
        }
        finally
        {
            DeviceProbe.PlatformMemoryProbe = previousMemoryProbe;
            Directory.Delete(modelDirectory, recursive: true);
        }
    }

    [Fact]
    public void Published_housekeeping_reclaims_the_old_model_only_after_replacement_loaded()
    {
        using var registry = new ModelRegistryService((ModelScopeCatalogClient?)null, null);
        using var catalog = new SqliteModelCatalog("Data Source=:memory:");
        CatalogueSeed.SeedFromEmbedded(catalog, registry);
        new DeviceModelAssessor(catalog, [ModelEngine.Mnn], false).Assess(CircleAiTestDefaults.HighMemoryDevice);
        catalog.SetInstalled("Qwen3-0.6B-MNN", true);
        catalog.SetInstalled("Qwen3.5-0.8B-MNN", true);
        Assert.DoesNotContain(CatalogueHousekeeping.Plan(catalog), p => p.Id == "Qwen3-0.6B-MNN");
        catalog.RecordLoad("Qwen3.5-0.8B-MNN");
        Assert.Contains(CatalogueHousekeeping.Plan(catalog), p => p.Id == "Qwen3-0.6B-MNN");
    }
}
