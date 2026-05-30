using Concierge.Ai;
using Concierge.Media;
using Concierge.Mesh;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddConciergeCore()
    .AddConciergeAi()
    .AddConciergeMesh()
    .AddConciergeMedia()
    .BuildServiceProvider();

var harness = services.GetRequiredService<IAgentHarnessService>();
var beyond = services.GetRequiredService<IBeyondClaudeService>();
var scalar = services.GetRequiredService<IScalarApiService>();
var pricing = services.GetRequiredService<IPricingService>();
var llm = services.GetRequiredService<ILlmRuntimeService>();
var mesh = services.GetRequiredService<IMeshTransportService>();
var media = services.GetRequiredService<IMediaStudioService>();
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";

switch (command)
{
    case "tools":
        foreach (var tool in harness.GetTools())
        {
            Console.WriteLine($"{tool.Name} | readOnly={tool.IsReadOnly} | destructive={tool.IsDestructive} | risk={tool.Risk}");
            Console.WriteLine($"  {tool.Description}");
        }
        break;
    case "read":
        RequireArgs(args, 2, "read <relative-path>");
        var read = await harness.ReadFileAsync(args[1]);
        PrintResult(read);
        break;
    case "write":
        RequireArgs(args, 3, "write <relative-path> <content> [--approved]");
        var write = await harness.WriteFileAsync(args[1], string.Join(' ', args.Skip(2).Where(item => item != "--approved")), args.Contains("--approved"));
        PrintResult(write);
        break;
    case "preview-write":
        RequireArgs(args, 3, "preview-write <relative-path> <content>");
        var preview = await harness.PreviewWriteFileAsync(args[1], string.Join(' ', args.Skip(2)));
        Console.WriteLine(preview.DiffPreview);
        Console.WriteLine(preview.RequiresApproval ? "approval: required" : "approval: not required");
        break;
    case "run":
        RequireArgs(args, 2, "run <command> [--approved]");
        var run = await harness.RunCommandAsync(string.Join(' ', args.Skip(1).Where(item => item != "--approved")), args.Contains("--approved"));
        PrintResult(run);
        Environment.ExitCode = run.Outcome == ConciergeToolOutcome.Succeeded ? 0 : 2;
        break;
    case "logs":
        foreach (var log in harness.GetRunLogs())
        {
            Console.WriteLine($"{log.Id} | {log.CreatedAt:g} | {log.Goal}");
            foreach (var result in log.Results)
            {
                Console.WriteLine($"  {result.ToolName}: {result.Outcome} - {result.Summary}");
            }
        }
        break;
    case "beyond":
        var beyondSnapshot = beyond.GetSnapshot();
        Console.WriteLine($"beyond capabilities: {beyondSnapshot.Capabilities.Count}");
        Console.WriteLine($"workforce helpers: {beyondSnapshot.WorkforceSwarms.Sum(swarm => swarm.Agents.Count)}");
        Console.WriteLine($"memory rooms: {beyondSnapshot.MemoryRooms.Count}");
        Console.WriteLine($"safe resume approval: {beyondSnapshot.SafeResume.RequiresUserApproval}");
        foreach (var capability in beyondSnapshot.Capabilities)
        {
            Console.WriteLine($"{capability.Id} | {capability.PlainEnglishName} | {capability.InspiredBy}");
        }

        break;
    case "api-room":
        var scalarSnapshot = scalar.GetSnapshot();
        Console.WriteLine($"{scalarSnapshot.Name} | {scalarSnapshot.Repository} | {scalarSnapshot.License}");
        Console.WriteLine($"capabilities: {scalarSnapshot.Capabilities.Count}");
        Console.WriteLine($"templates: {scalarSnapshot.Templates.Count}");
        foreach (var capability in scalarSnapshot.Capabilities)
        {
            Console.WriteLine($"{capability.Id} | {capability.Name} | {capability.Kind}");
        }

        break;
    case "pricing":
        var pricingSnapshot = pricing.GetSnapshot();
        Console.WriteLine(pricingSnapshot.Positioning);
        Console.WriteLine($"plans: {pricingSnapshot.Plans.Count}");
        Console.WriteLine($"paths: {pricingSnapshot.Paths.Count}");
        Console.WriteLine($"usage bands: {pricingSnapshot.UsageBands.Count}");
        foreach (var plan in pricingSnapshot.Plans)
        {
            Console.WriteLine($"{plan.Id} | {plan.Name} | {plan.MonthlyPriceLabel} | contact={plan.IsContactSales}");
        }

        break;
    case "status":
        var snapshot = services.GetRequiredService<IConciergeStateService>().GetSnapshot();
        var llmSnapshot = llm.GetSnapshot();
        var meshSnapshot = mesh.GetSnapshot();
        var mediaSnapshot = media.GetSnapshot();
        Console.WriteLine($"providers: {snapshot.Providers.Count}");
        Console.WriteLine($"skills: {snapshot.Skills.Count}");
        Console.WriteLine($"production tasks: {snapshot.ProductionTasks.Count}");
        Console.WriteLine($"tools: {harness.GetTools().Count}");
        Console.WriteLine($"beyond capabilities: {beyond.GetCapabilities().Count}");
        Console.WriteLine($"api room capabilities: {scalar.GetSnapshot().Capabilities.Count}");
        Console.WriteLine($"pricing plans: {pricing.GetSnapshot().Plans.Count}");
        Console.WriteLine($"ai runtime: {llmSnapshot.Engine} v{llmSnapshot.EngineVersion} (registry: {(llmSnapshot.RegistryAvailable ? "available" : "unavailable")}, models probed: {llmSnapshot.ProbedModels.Count})");
        Console.WriteLine($"mesh transport: {meshSnapshot.Engine} v{meshSnapshot.EngineVersion} (tag: {meshSnapshot.NodeTag}, routes: {meshSnapshot.CachedRoutes}, active: {meshSnapshot.IsActive})");
        Console.WriteLine($"media studio: {mediaSnapshot.Engine} v{mediaSnapshot.EngineVersion} (items: {mediaSnapshot.LibraryItemCount}, kinds: {string.Join('/', mediaSnapshot.SupportedKinds)})");
        break;
    case "ai":
        var ai = llm.GetSnapshot();
        Console.WriteLine($"{ai.Engine} v{ai.EngineVersion}");
        Console.WriteLine($"device: {ai.DeviceSummary}");
        Console.WriteLine($"registry: {(ai.RegistryAvailable ? "available" : "unavailable")}");
        foreach (var model in ai.ProbedModels)
        {
            Console.WriteLine($"  {(model.Available ? "[ok]" : "[--]")} {model.Name} {model.Version} {model.Quantization}");
        }

        Console.WriteLine(ai.Summary);
        break;
    case "mesh":
        var m = mesh.GetSnapshot();
        Console.WriteLine($"{m.Engine} v{m.EngineVersion}");
        Console.WriteLine($"node tag: {m.NodeTag}");
        Console.WriteLine($"cached routes: {m.CachedRoutes}");
        Console.WriteLine($"known peers: {m.KnownPeers}");
        Console.WriteLine($"active: {m.IsActive}");
        Console.WriteLine(m.Summary);
        break;
    case "media":
        var med = media.GetSnapshot();
        Console.WriteLine($"{med.Engine} v{med.EngineVersion}");
        Console.WriteLine($"items: {med.LibraryItemCount}");
        Console.WriteLine($"kinds: {string.Join(", ", med.SupportedKinds)}");
        foreach (var item in med.RecentItems)
        {
            Console.WriteLine($"  - {item.Title} [{item.ContentType}, {item.FormattedDuration}] by {item.CreatorTag}");
        }

        Console.WriteLine(med.Summary);
        break;
    default:
        Console.WriteLine("Concierge CLI");
        Console.WriteLine("  status");
        Console.WriteLine("  tools");
        Console.WriteLine("  read <relative-path>");
        Console.WriteLine("  preview-write <relative-path> <content>");
        Console.WriteLine("  write <relative-path> <content> [--approved]");
        Console.WriteLine("  run <command> [--approved]");
        Console.WriteLine("  logs");
        Console.WriteLine("  beyond");
        Console.WriteLine("  api-room");
        Console.WriteLine("  pricing");
        Console.WriteLine("  ai");
        Console.WriteLine("  mesh");
        Console.WriteLine("  media");
        break;
}

static void PrintResult(ConciergeToolResult result)
{
    Console.WriteLine($"{result.ToolName}: {result.Outcome}");
    Console.WriteLine(result.Summary);
    if (!string.IsNullOrWhiteSpace(result.Output))
    {
        Console.WriteLine(result.Output);
    }
}

static void RequireArgs(string[] args, int count, string usage)
{
    if (args.Length >= count)
    {
        return;
    }

    Console.Error.WriteLine($"usage: concierge {usage}");
    Environment.Exit(64);
}
