using System.Text.Json.Nodes;
using Concierge.Shared.Presets;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What a preset must do (parity feature 30): compose a session from one named choice —
/// which tools it has, who it sounds like, and what it may do.
/// </summary>
/// <remarks>
/// This is how Kid Mode should be built. Not an adult session with a filter in front of it,
/// but a session composed with a smaller tool set from the start, so the question of whether
/// the model can be talked past never arises.
/// </remarks>
public sealed class AgentPresetTests
{
    [Fact]
    public void A_preset_narrows_the_tools_to_its_permission_level()
    {
        var preset = new AgentPreset("kid", "You are Bell.", ConciergePermissionPreset.ReadOnly);

        var tools = preset.SelectTools(AllTools());

        Assert.All(tools, tool => Assert.True(tool.IsReadOnly));
    }

    [Fact]
    public void A_preset_can_hide_a_tool_its_level_would_otherwise_allow()
    {
        // Read-only is not the same as suitable for a nine-year-old. A preset may exclude
        // a tool by name without changing what the permission level means.
        var preset = new AgentPreset(
            "kid",
            "You are Bell.",
            ConciergePermissionPreset.ReadOnly,
            excludedTools: ["grep"]);

        Assert.DoesNotContain(preset.SelectTools(AllTools()), tool => tool.Name == "grep");
    }

    [Fact]
    public void A_preset_keeps_the_tools_it_did_not_exclude()
    {
        var preset = new AgentPreset(
            "kid",
            "You are Bell.",
            ConciergePermissionPreset.ReadOnly,
            excludedTools: ["grep"]);

        Assert.Contains(preset.SelectTools(AllTools()), tool => tool.Name == "read_file");
    }

    [Fact]
    public void A_preset_carries_its_persona()
    {
        var preset = new AgentPreset("kid", "You are Bell, and you talk to children.", ConciergePermissionPreset.ReadOnly);

        Assert.Contains("children", preset.Persona, StringComparison.Ordinal);
    }

    [Fact]
    public void A_preset_without_a_name_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => new AgentPreset("  ", "persona", ConciergePermissionPreset.ReadOnly));
    }

    [Fact]
    public void Two_sessions_on_different_presets_get_different_tools()
    {
        var kid = new AgentPreset("kid", "Bell", ConciergePermissionPreset.ReadOnly);
        var operatorPreset = new AgentPreset("operator", "Bell", ConciergePermissionPreset.FullAccess);

        Assert.True(operatorPreset.SelectTools(AllTools()).Count > kid.SelectTools(AllTools()).Count);
    }

    [Fact]
    public void A_registry_finds_a_preset_by_name()
    {
        var registry = new AgentPresetRegistry([
            new AgentPreset("kid", "Bell", ConciergePermissionPreset.ReadOnly),
        ]);

        Assert.Equal("kid", registry.Find("kid")!.Name);
    }

    [Fact]
    public void A_registry_finds_nothing_for_an_unknown_name()
    {
        Assert.Null(new AgentPresetRegistry([]).Find("nope"));
    }

    [Fact]
    public void The_built_in_kid_preset_cannot_run_commands()
    {
        var kid = AgentPresetRegistry.BuiltIn.Find("kid")!;

        Assert.DoesNotContain(kid.SelectTools(AllTools()), tool => tool.Name == "run_command");
    }

    [Fact]
    public void The_built_in_kid_preset_cannot_write_files()
    {
        var kid = AgentPresetRegistry.BuiltIn.Find("kid")!;

        Assert.DoesNotContain(kid.SelectTools(AllTools()), tool => tool.Name == "write_file");
    }

    private static IReadOnlyList<IAgentTool> AllTools() =>
    [
        new StubTool("read_file", readOnly: true),
        new StubTool("grep", readOnly: true),
        new StubTool("write_file", readOnly: false),
        new StubTool("run_command", readOnly: false),
    ];

    private sealed class StubTool(string name, bool readOnly) : IAgentTool
    {
        public string Name => name;
        public string Description => "a stub";
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => readOnly;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolResult(true, string.Empty, null));
    }
}
