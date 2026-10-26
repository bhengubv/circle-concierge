using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Settings;

namespace Concierge.Shared.Tools;

public sealed class ScheduleReadTool(IScheduledTaskStore store) : IAgentTool
{
    public string Name => "schedule_read";
    public string Description => "Read scheduled work, its progress and last results on this device. Includes the current time and local time zone for planning.";
    public bool IsReadOnly => true;
    public JsonNode? ArgumentsSchema => null;
    public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentToolResult(true, JsonSerializer.Serialize(new
        {
            Now = DateTimeOffset.Now, TimeZone = TimeZoneInfo.Local.Id,
            Execution = "Runs while Concierge is open. Overdue work resumes when it opens.", Tasks = store.All()
        }), null));
    }
}

public sealed class ScheduleCreateTool(IScheduledTaskStore store, IToolApprovalService approval) : IAgentTool
{
    public string Name => "schedule_create";
    public string Description => "Schedule instructions for later on this device. Read schedule_read for the current time first. Use an ISO date/time with an explicit offset. Execution uses Ask first; permissions to change things will be requested when the task runs. Runs while Concierge is open; overdue tasks resume on reopening.";
    public bool IsReadOnly => false;
    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        {"type":"object","properties":{
          "instructions":{"type":"string","maxLength":16000},
          "dueAt":{"type":"string","description":"ISO 8601 including offset, for example 2026-10-26T09:00:00+09:00"},
          "repeatMinutes":{"type":"integer","minimum":1,"maximum":525600}
        },"required":["instructions","dueAt"],"additionalProperties":false}
        """);
    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        string instructions;
        DateTimeOffset due;
        TimeSpan? repeat;
        try
        {
            instructions = arguments?["instructions"]?.GetValue<string>()?.Trim() ?? "";
            var raw = arguments?["dueAt"]?.GetValue<string>() ?? "";
            var timeStart = raw.IndexOf('T');
            var explicitOffset = raw.EndsWith('Z') || timeStart > 0 && (raw.IndexOf('+', timeStart) >= 0 || raw.IndexOf('-', timeStart) >= 0);
            if (instructions.Length is 0 or > 16000 || !explicitOffset
                || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out due)
                || due <= DateTimeOffset.UtcNow)
                return new(false, "", "Provide instructions and a future ISO date/time including its UTC offset.");
            int? minutes = arguments?["repeatMinutes"]?.GetValue<int>();
            if (minutes is < 1 or > 525600) return new(false, "", "Repeat minutes must be between 1 and 525600, or omitted for a single run.");
            repeat = minutes is { } value ? TimeSpan.FromMinutes(value) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        { return new(false, "", "Invalid schedule arguments. Use instructions, dueAt and optional repeatMinutes."); }

        var decision = await approval.RequestAsync(new ToolApprovalRequest(Name,
            $"Schedule work for {due.ToLocalTime():f}", ConciergeToolRisk.Medium,
            $"{instructions}\nRepeat: {(repeat is null ? "once" : $"every {repeat.Value.TotalMinutes} minutes")}\nAsk first. Concierge must be running to execute."), cancellationToken);
        if (decision != ToolApprovalDecision.Allowed) return new(false, "", "Not approved. Nothing was scheduled.");
        cancellationToken.ThrowIfCancellationRequested();
        var saved = store.Schedule(instructions, due, repeat, ToolPermissionMode.AskFirst);
        return new(true, JsonSerializer.Serialize(saved), null);
    }
}

public sealed class SchedulePauseTool(ScheduledWorkService worker, IScheduledTaskStore store, IToolApprovalService approval) : IAgentTool
{
    public string Name => "schedule_pause";
    public string Description => "Pause a saved scheduled task by its ID from schedule_read. Stops its current run if running in this app. Keeps history and results.";
    public bool IsReadOnly => false;
    public JsonNode? ArgumentsSchema => JsonNode.Parse("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""");
    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(arguments?["id"]?.ToString(), out var id) || store.All().All(t => t.Id != id))
            return new(false, "", "No scheduled task has that ID. Read schedule_read for current tasks.");
        var decision = await approval.RequestAsync(new ToolApprovalRequest(Name, "Pause scheduled work", ConciergeToolRisk.Medium, id.ToString()), cancellationToken);
        if (decision != ToolApprovalDecision.Allowed) return new(false, "", "Not approved. The schedule was not changed.");
        cancellationToken.ThrowIfCancellationRequested();
        worker.Stop(id);
        return new(true, "Paused. The task and its results remain in Scheduled.", null);
    }
}
