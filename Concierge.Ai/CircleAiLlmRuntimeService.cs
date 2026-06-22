using CircleAI.Core;
using CircleAI.Core.Models;
using Concierge.Shared;

namespace Concierge.Ai;

/// <summary>
/// CircleAI-backed implementation of <see cref="ILlmRuntimeService"/>. Reports the engine
/// version, a device summary lifted from the injected <see cref="IDeviceContext"/>, and
/// the result of probing the embedded model registry for a small set of well-known names.
/// </summary>
public sealed class CircleAiLlmRuntimeService : ILlmRuntimeService, IDisposable
{
    // Refreshed against CircleAI 3.x's embedded registry (Models array in
    // CircleAI.Core/Models/embedded_registry.json). 3.x switched the model
    // family from llama.cpp GGUF (-Q4 suffix) to MNN (-MNN suffix) and the
    // probe must match the new naming or RegistryAvailable is always false.
    private static readonly IReadOnlyList<string> KnownModelNames =
    [
        "Qwen3-0.6B-MNN",
        "Qwen3-1.7B-MNN",
        "Qwen3-4B-MNN",
        "Qwen3-8B-MNN",
        "Qwen3-14B-MNN",
    ];

    private readonly IDeviceContext _device;
    private readonly Lazy<ModelRegistryService> _registry = new(static () => new ModelRegistryService());

    public CircleAiLlmRuntimeService(IDeviceContext device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public LlmRuntimeSnapshot GetSnapshot()
    {
        var probed = new List<LlmModelDescriptor>(KnownModelNames.Count);
        var anyHit = false;
        var registry = _registry.Value;
        foreach (var name in KnownModelNames)
        {
            var entry = registry.GetLatestModel(name);
            if (entry is null)
            {
                probed.Add(new LlmModelDescriptor(name, "—", "—", Available: false));
            }
            else
            {
                probed.Add(new LlmModelDescriptor(entry.Name, entry.Version, entry.Quantization, Available: true));
                anyHit = true;
            }
        }

        var deviceSummary = BuildDeviceSummary(_device);
        var version = typeof(ModelRegistryService).Assembly.GetName().Version?.ToString() ?? "unknown";

        return new LlmRuntimeSnapshot(
            Engine: "CircleAI",
            EngineVersion: version,
            DeviceSummary: deviceSummary,
            RegistryAvailable: anyHit,
            ProbedModels: probed,
            Summary: anyHit
                ? "CircleAI runtime wired; embedded model registry resolved at least one known model."
                : "CircleAI runtime wired but the embedded model registry returned no matches — the host is still functional and can adopt models when CheckForUpdatesAsync runs.");
    }

    private static string BuildDeviceSummary(IDeviceContext device)
    {
        var parts = new List<string>(5);
        parts.Add($"locale={device.Locale ?? "unknown"}");
        parts.Add($"network={device.NetworkType ?? "unknown"}");
        if (device.BatteryLevel is { } battery)
        {
            parts.Add($"battery={(int)Math.Round(battery * 100)}%");
        }

        if (device.AvailableMemoryBytes is { } mem)
        {
            parts.Add($"mem={mem / (1024 * 1024)}MB");
        }

        if (device.ThermalState is { } thermal)
        {
            parts.Add($"thermal={thermal}");
        }

        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        if (_registry.IsValueCreated)
        {
            _registry.Value.Dispose();
        }
    }
}
