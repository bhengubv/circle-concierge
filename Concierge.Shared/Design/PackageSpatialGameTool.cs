using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Design;

/// <summary>Creates a private local draft; SleptON publishing remains a separate action.</summary>
public sealed class PackageSpatialGameTool(DesignWorkbench workbench) : IAgentTool
{
    public string Name => "design_package_spatial_game";
    public string Description => "Make a playable room game draft from the current Space canvas. The ZIP stays on this device in Concierge's SleptON exports. It is not published. Requires at least three walls and three objects.";
    public JsonNode? ArgumentsSchema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["title"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("title")
    };
    public bool IsReadOnly => false;

    public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        if (workbench.Session is not { } session || workbench.SpatialGameExportRoot is not { } root)
            return Task.FromResult(new AgentToolResult(false, string.Empty, "Open a Space canvas on a device that can save a draft."));
        try
        {
            var path = SpatialGamePackage.Create(session.Current,
                arguments?["title"]?.GetValue<string>() ?? string.Empty, root);
            return Task.FromResult(new AgentToolResult(true,
                $"Playable game draft saved at {path}. Open the ZIP's README to preview it. It has not been published to SleptON!."));
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        { return Task.FromResult(new AgentToolResult(false, string.Empty, error.Message)); }
    }
}
