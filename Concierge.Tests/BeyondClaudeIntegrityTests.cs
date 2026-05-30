using Concierge.Shared;

namespace Concierge.Tests;

public sealed class BeyondClaudeIntegrityTests
{
    [Fact]
    public void Capability_ids_are_unique_and_human_readable()
    {
        var capabilities = new BeyondClaudeService().GetCapabilities();

        Assert.Equal(capabilities.Count, capabilities.Select(capability => capability.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(capabilities, capability =>
        {
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", capability.Id);
            Assert.False(string.IsNullOrWhiteSpace(capability.PlainEnglishName));
            Assert.DoesNotContain("TODO", capability.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TBD", capability.Summary, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Every_beyond_capability_has_a_safety_boundary()
    {
        var capabilities = new BeyondClaudeService().GetCapabilities();

        Assert.All(capabilities, capability =>
        {
            Assert.False(string.IsNullOrWhiteSpace(capability.SafetyBoundary));
            Assert.True(capability.SafetyBoundary.Length >= 40, $"{capability.Id} needs a meaningful safety boundary.");
            Assert.DoesNotContain("dangerously-skip-permissions", capability.SafetyBoundary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("zero human intervention", capability.SafetyBoundary, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Workforce_agents_have_clear_roles_and_limits()
    {
        var snapshot = new BeyondClaudeService().GetSnapshot();
        var agents = snapshot.WorkforceSwarms.SelectMany(swarm => swarm.Agents).ToList();

        Assert.True(agents.Count >= 10);
        Assert.All(agents, agent =>
        {
            Assert.False(string.IsNullOrWhiteSpace(agent.Name));
            Assert.False(string.IsNullOrWhiteSpace(agent.Role));
            Assert.False(string.IsNullOrWhiteSpace(agent.DefaultBoundary));
        });
    }

    [Fact]
    public void Simulation_scenarios_make_uncertainty_visible()
    {
        var simulations = new BeyondClaudeService().GetSnapshot().Simulations;

        Assert.NotEmpty(simulations);
        Assert.All(simulations, simulation =>
        {
            Assert.Contains("?", simulation.PlainEnglishQuestion, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(simulation.EvidenceNeeded));
            Assert.False(string.IsNullOrWhiteSpace(simulation.Output));
        });
    }

    [Fact]
    public void Infrastructure_lanes_do_not_normalize_raw_unbounded_access()
    {
        var lanes = new BeyondClaudeService().GetSnapshot().InfrastructureLanes;

        Assert.NotEmpty(lanes);
        Assert.All(lanes, lane =>
        {
            Assert.False(string.IsNullOrWhiteSpace(lane.SafetyBoundary));
            Assert.DoesNotContain("unrestricted", lane.SafetyBoundary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("raw shell", lane.SafetyBoundary, StringComparison.OrdinalIgnoreCase);
        });
    }
}
