using Concierge.Shared;

namespace Concierge.Tests;

public sealed class BeyondClaudeServiceTests
{
    [Fact]
    public void Beyond_capabilities_cover_reference_repo_audit()
    {
        var service = new BeyondClaudeService();
        var capabilities = service.GetCapabilities();

        Assert.True(capabilities.Count >= 10);
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("mempalace", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("loki-mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("eigent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("MiroFish", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("LazySSH", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("mermaid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("claude-auto-resume", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("PicoClaw", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("pollinations", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("CrossCode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capabilities, capability => capability.InspiredBy.Contains("Scalar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Safe_auto_resume_never_bypasses_permissions()
    {
        var service = new BeyondClaudeService();
        var snapshot = service.GetSnapshot();

        Assert.True(snapshot.SafeResume.RequiresUserApproval);
        Assert.True(snapshot.SafeResume.KeepsOriginalSandbox);
        Assert.True(snapshot.SafeResume.BlocksPermissionBypass);
        Assert.True(snapshot.SafeResume.StopsWhenContextChanges);
    }

    [Fact]
    public void Workforce_mode_includes_business_and_engineering_helpers()
    {
        var service = new BeyondClaudeService();
        var agents = service.GetSnapshot().WorkforceSwarms.SelectMany(swarm => swarm.Agents).ToList();

        Assert.Contains(agents, agent => agent.Name == "Build guide");
        Assert.Contains(agents, agent => agent.Name == "Finance");
        Assert.Contains(agents, agent => agent.Name == "HR");
        Assert.Contains(agents, agent => agent.Name == "Customer care");
    }

    [Fact]
    public void Security_lab_is_defensive_and_scoped()
    {
        var service = new BeyondClaudeService();
        var security = service.GetCapabilities().Single(capability => capability.Id == "security-lab");

        Assert.Contains("defensive", security.SafetyBoundary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved assets", security.SafetyBoundary, StringComparison.OrdinalIgnoreCase);

        // This line asserted `Ready`, which is how a test comes to hold a claim in place
        // rather than check it: there is no security lab, no defensive check, no threat
        // model and no dependency review anywhere in the tree. The boundary above is worth
        // pinning — it is what the thing would work inside if it existed — and the state is
        // now the true one.
        Assert.Equal(BeyondState.NotBuilt, security.State);
    }

    [Fact]
    public void Memory_palace_is_local_raw_and_portable()
    {
        var service = new BeyondClaudeService();
        var rooms = service.GetSnapshot().MemoryRooms;

        Assert.NotEmpty(rooms);
        Assert.All(rooms, room => Assert.True(room.StoresRawLocalEvidence));
        Assert.All(rooms, room => Assert.True(room.PortableAcrossDevices));
    }
}
