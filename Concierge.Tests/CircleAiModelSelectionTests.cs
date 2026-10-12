using CircleAI.Core;
using CircleAI.Inference;
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
            Assert.Equal("Qwen3-30B-A3B-MNN", selected.ModelId);
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
            Assert.Equal("Qwen3-0.6B-MNN", candidates[0].ModelId);
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
    public void Assessment_reclaims_a_complete_inferior_model_that_a_similar_fitting_model_supersedes()
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

            Assert.False(Directory.Exists(Path.Combine(modelDirectory, "Qwen3-0.6B-MNN")));
            Assert.True(Directory.Exists(Path.Combine(modelDirectory, "Qwen3.5-0.8B-MNN")));
        }
        finally
        {
            DeviceProbe.PlatformMemoryProbe = previousMemoryProbe;
            Directory.Delete(modelDirectory, recursive: true);
        }
    }
}
