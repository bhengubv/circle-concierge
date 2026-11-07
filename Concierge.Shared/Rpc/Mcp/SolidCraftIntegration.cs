using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Rpc.Mcp;

public static class SolidCraftServiceCollectionExtensions
{
    public static IServiceCollection AddConciergeSolidCraft(this IServiceCollection services)
    {
        services.TryAddSingleton<SolidCraftIntegration>();
        return services;
    }
}

/// <summary>Opt-in, local SolidCraft connection for the Desktop project workspace.</summary>
public sealed class SolidCraftIntegration
{
    private const string ServerName = "SolidCraft";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly McpServerOptions _options;
    private readonly McpToolSource _tools;
    private readonly string _modelFolder;
    private readonly string? _cliPath;

    public SolidCraftIntegration(McpServerOptions options, McpToolSource tools)
        : this(options, tools, null, null) { }

    internal SolidCraftIntegration(McpServerOptions options, McpToolSource tools, string? cliPath, string? modelFolder)
    {
        _options = options;
        _tools = tools;
        _cliPath = cliPath;
        _modelFolder = modelFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Concierge", "SolidCraft");
    }

    public string ModelFolder => _modelFolder;

    public bool IsConnected => _tools.CurrentHosts.Any(host =>
        string.Equals(host.DisplayName, $"MCP · {ServerName}", StringComparison.OrdinalIgnoreCase)
        && host.Snapshot is not null);

    public bool IsInstalled => FindCli() is not null;

    /// <summary>Connects only when requested; existing MCP entries remain configured.</summary>
    public async Task<string> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return "SolidCraft Desktop integration is available on Windows.";

        var problem = Configure();
        if (problem is not null)
            return problem;

        var status = await _tools.ConnectConfiguredAsync(ServerName, cancellationToken).ConfigureAwait(false);
        return status.Connected
            ? $"SolidCraft connected. Models and exports can be saved in {_modelFolder}."
            : $"SolidCraft could not connect: {status.Status}";
    }

    /// <summary>Writes the local MCP entry without starting an app or publishing anything.</summary>
    internal string? Configure()
    {
        var cli = FindCli();
        if (cli is null)
            return "SolidCraft CLI was not found. Install SolidCraft Desktop before connecting.";

        var (servers, problem) = _options.Load();
        if (problem is not null)
            return $"Fix the MCP configuration before connecting SolidCraft: {problem}";

        var configured = servers.Where(server => !string.Equals(server.Name, ServerName, StringComparison.OrdinalIgnoreCase))
            .Append(new McpServerConfig(ServerName, cli, ["mcp", "--allow-path", _modelFolder]))
            .ToArray();

        try
        {
            Directory.CreateDirectory(_modelFolder);
            var configDirectory = Path.GetDirectoryName(_options.Path)!;
            Directory.CreateDirectory(configDirectory);
            var temporary = Path.Combine(configDirectory, $"mcp-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(configured, Json));
                File.Move(temporary, _options.Path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return null;
        }
        catch (IOException exception)
        {
            return $"SolidCraft connection could not be saved: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            return $"SolidCraft connection could not be saved: {exception.Message}";
        }
    }

    private string? FindCli()
    {
        if (_cliPath is not null)
            return File.Exists(_cliPath) ? _cliPath : null;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SolidCraft", "solidcraft-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SolidCraft", "solidcraft-cli.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
