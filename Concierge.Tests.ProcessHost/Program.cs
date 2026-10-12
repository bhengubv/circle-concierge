using System.Text.Json;
using Concierge.Shared;
using Concierge.Shared.Away;
using Concierge.Shared.Chat;
using Concierge.Shared.Context;
using Concierge.Shared.Skills;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

if (args.Length > 0 && string.Equals(args[0], "conversation-gate", StringComparison.Ordinal))
{
    if (args.Length != 5)
        return 2;

    var lockDirectory = args[1];
    var conversationId = Guid.Parse(args[2]);
    var gateEnteredMarker = args[3];
    var holdMs = int.Parse(args[4]);
    using (await new ConversationRunGate(lockDirectory).EnterAsync(conversationId))
    {
        await File.WriteAllTextAsync(gateEnteredMarker, "entered");
        if (holdMs > 0)
            await Task.Delay(holdMs);
    }

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "turn-run", StringComparison.Ordinal))
    return await TurnRunWorker.RunAsync(args);

if (args.Length > 0 && string.Equals(args[0], "turn-create", StringComparison.Ordinal))
    return await TurnRunWorker.CreateConversationAsync(args);

if (args.Length != 6)
    return 2;

var folder = args[0];
var requestId = Guid.Parse(args[1]);
var fingerprint = args[2];
var answerText = args[3];
var enteredMarker = args[4];
var delayMs = int.Parse(args[5]);
var executed = false;

var answer = await new AwayRequestLedger(folder).ExecuteOnceAsync(requestId, fingerprint, async () =>
{
    executed = true;
    if (!string.IsNullOrWhiteSpace(enteredMarker))
        await File.WriteAllTextAsync(enteredMarker, "entered");
    if (delayMs > 0)
        await Task.Delay(delayMs);
    return new AwayAnswer(true, answerText, answerText);
});

Console.WriteLine(JsonSerializer.Serialize(new WorkerReply(executed, answer)));
return 0;

internal sealed record WorkerReply(bool Executed, AwayAnswer Answer);

internal static class TurnRunWorker
{
    internal static async Task<int> CreateConversationAsync(string[] args)
    {
        if (args.Length != 2)
            return 2;

        await using var services = new ServiceCollection()
            .AddConciergeChat(args[1])
            .BuildServiceProvider();
        var conversation = await services.GetRequiredService<IConversationStore>().StartAsync("process-acceptance");
        Console.WriteLine(conversation.Id.ToString("D"));
        return 0;
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 8)
            return 2;

        var databasePath = args[1];
        var lockDirectory = args[2];
        var conversationId = Guid.Parse(args[3]);
        var runId = Guid.Parse(args[4]);
        var answerText = args[5];
        var enteredMarker = args[6];
        var block = bool.Parse(args[7]);

        await using var services = new ServiceCollection()
            .AddConciergeChat(databasePath)
            .BuildServiceProvider();

        var store = services.GetRequiredService<IConversationStore>();
        var runtime = new ProcessChatRuntime(answerText, enteredMarker, block);
        var tools = new AgentToolRegistry([]);
        var runner = new TurnRunner(
            store,
            tools,
            new ToolCallScheduler(tools),
            new BasicCompactionEngine(new HeuristicTokenMeter(), runtime),
            new HeadTailToolResultPruner(),
            new RepeatToolReminder(),
            new EmptySkillRuntime(),
            new ConciergeToolLoopOptions(),
            new BackgroundRuns(),
            new CapturedImages(),
            runGate: new ConversationRunGate(lockDirectory));

        var result = await runner.RunAsync(new TurnRequest(
            conversationId,
            "Process acceptance question",
            runtime,
            RunId: runId));

        Console.WriteLine(JsonSerializer.Serialize(new TurnWorkerReply(runtime.Calls > 0, result)));
        return 0;
    }

    private sealed class EmptySkillRuntime : ISkillRuntime
    {
        public SkillActivation? Activate(string skillId) => null;
        public string ComposeSystemPrompt(IEnumerable<string> skillIds, int budgetTokens = SkillRuntime.DefaultBudgetTokens) => string.Empty;
    }

    private sealed class ProcessChatRuntime(string answer, string marker, bool block) : IChatRuntime
    {
        public int Calls { get; private set; }
        public string Id => "process-acceptance";
        public string EngineLabel => "Process acceptance runtime";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            if (!string.IsNullOrWhiteSpace(marker))
                await File.WriteAllTextAsync(marker, "entered", cancellationToken);
            if (block)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            yield return answer;
        }
    }

    private sealed record TurnWorkerReply(bool Executed, TurnResult Result);
}
