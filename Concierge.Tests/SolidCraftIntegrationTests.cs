using Concierge.Shared.Rpc.Mcp;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

public sealed class SolidCraftIntegrationTests
{
    [Fact]
    public async Task Connect_configuration_preserves_other_servers_and_limits_model_files_to_a_local_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"concierge-solidcraft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configPath = Path.Combine(root, "mcp.json");
            var cliPath = Path.Combine(root, "solidcraft-cli.exe");
            var modelFolder = Path.Combine(root, "models");
            File.WriteAllText(cliPath, "test executable placeholder");
            File.WriteAllText(configPath, "[{\"name\":\"Existing\",\"command\":\"existing.exe\"}]");
            var options = new McpServerOptions(configPath);
            await using var source = new McpToolSource(options, UnavailableToolApprovalService.Instance);
            var integration = new SolidCraftIntegration(options, source, cliPath, modelFolder);

            Assert.Null(integration.Configure());
            Assert.Null(integration.Configure());

            var (servers, problem) = options.Load();
            Assert.Null(problem);
            Assert.Equal(2, servers.Count);
            Assert.Contains(servers, server => server.Name == "Existing" && server.Command == "existing.exe");
            var solidCraft = Assert.Single(servers, server => server.Name == "SolidCraft");
            Assert.Equal(cliPath, solidCraft.Command);
            Assert.Equal(new[] { "mcp", "--allow-path", modelFolder }, solidCraft.Arguments);
            Assert.True(Directory.Exists(modelFolder));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_existing_configuration_is_not_overwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), $"concierge-solidcraft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configPath = Path.Combine(root, "mcp.json");
            var cliPath = Path.Combine(root, "solidcraft-cli.exe");
            File.WriteAllText(cliPath, "test executable placeholder");
            File.WriteAllText(configPath, "{invalid json");
            var options = new McpServerOptions(configPath);
            await using var source = new McpToolSource(options, UnavailableToolApprovalService.Instance);
            var integration = new SolidCraftIntegration(options, source, cliPath, Path.Combine(root, "models"));

            Assert.Contains("Fix the MCP configuration", integration.Configure());
            Assert.Equal("{invalid json", File.ReadAllText(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
