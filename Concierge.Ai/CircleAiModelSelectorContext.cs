using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace Concierge.Ai;

/// <summary>
/// Per-assessment Circle AI catalogue pipeline. It seeds the 3.6.0 embedded
/// registry, assesses fit against the active MNN memory-mapping mode, then
/// selects through Circle AI's compatible-catalogue selector.
/// </summary>
internal sealed class CircleAiModelSelectorContext : IDisposable
{
    private readonly string _modelsDirectory;
    private readonly ModelRegistryService _registry;
    private readonly SqliteModelCatalog _catalog;
    private readonly DeviceModelAssessor _assessor;
    private readonly CatalogModelSelector _selector;

    public CircleAiModelSelectorContext(string modelsDirectory, bool? mmapAllowed = null)
    {
        _modelsDirectory = modelsDirectory;
        _registry = new ModelRegistryService((ModelScopeCatalogClient?)null, null);
        _catalog = new SqliteModelCatalog("Data Source=:memory:");
        _assessor = new DeviceModelAssessor(_catalog, [ModelEngine.Mnn], mmapAllowed);
        _selector = new CatalogModelSelector(_catalog);
    }

    public AssessmentResult Assess(DeviceProbe probe)
        => CatalogueBootstrap.Run(
            _catalog,
            _assessor,
            probe,
            seedFrom: _registry,
            modelsDirectory: _modelsDirectory,
            reclaimInferior: true);

    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required)
        => _selector.BestFit(probe, required);

    public IReadOnlyList<ModelSelection> AllCandidates(DeviceProbe probe)
        => _selector.AllCandidates(probe);

    /// <summary>
    /// Eligible chat models for the declared capabilities, ordered by bundle size so setup
    /// can make a small usable tier available before it prepares larger tiers.
    /// The Circle AI assessor has already filtered this catalogue to models that fit the
    /// current device's measured memory and storage.
    /// </summary>
    public IReadOnlyList<ModelSelection> EligibleCandidatesSmallestFirst(
        DeviceProbe probe,
        ChatCapability required)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var tier = probe.Classify();

        return _catalog.Compatible(ModelModality.Chat)
            .Where(entry => Satisfies(entry.Capabilities, required))
            .Select(entry => new ModelSelection(
                entry.Name,
                RequiresDownload: !_catalog.IsInstalled(entry.Name),
                EstimatedBytes: entry.TotalBytes,
                Tier: tier,
                Quality: SelectionQuality.Good))
            .OrderBy(candidate => candidate.EstimatedBytes)
            .ThenBy(candidate => candidate.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Satisfies(IReadOnlyList<string>? labels, ChatCapability required)
    {
        if (required == ChatCapability.None)
            return true;

        var declared = ChatCapability.None;
        if (labels is not null)
        {
            foreach (var label in labels)
            {
                if (Enum.TryParse<ChatCapability>(label, ignoreCase: true, out var capability))
                    declared |= capability;
            }
        }

        return (declared & required) == required;
    }

    public void Dispose()
    {
        _catalog.Dispose();
        _registry.Dispose();
    }
}
