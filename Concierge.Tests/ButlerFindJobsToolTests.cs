using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Butler;
using Concierge.Shared.Chat;
using Concierge.Shared.Planning;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Concierge.Tests;

public sealed class ButlerFindJobsToolTests
{
    [Fact]
    public async Task Project_search_dispatches_with_stable_ownership_and_returns_only_Butler_terminal_result()
    {
        var runId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var project = ConciergeProject.Create("Recruitment", "Find a suitable role");
        var api = new FakeButlerWorkRunApi();
        using var provider = CreateProvider(api);
        var store = provider.GetRequiredService<IProjectStore>();
        await store.CreateAsync(project);
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        var registry = new AgentToolRegistry([], [source]);

        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, null, conversationId, runId,
                   PermissionMode: ToolPermissionMode.AskFirst)))
        {
            var tool = Assert.Single(registry.Tools, item => item.Name == "butler_find_jobs");
            var result = await tool.InvokeAsync(new JsonObject { ["keyword"] = "plumber", ["location"] = "Cape Town" });

            Assert.True(result.Success, result.FailureMessage);
            using var output = JsonDocument.Parse(result.Output);
            Assert.Equal("Butler", output.RootElement.GetProperty("source").GetString());
            Assert.Equal("completed", output.RootElement.GetProperty("status").GetString());
            Assert.Equal("Cape Town", api.Submitted!.Arguments.GetProperty("location").GetString());
            Assert.Equal(runId, api.Submitted.ConciergeRunId);
            Assert.Equal(project.Id, api.Submitted.ProjectId);
            Assert.Equal("AskFirst", api.Submitted.ActionMode);
            Assert.Equal($"{runId:N}:butler:find_jobs", api.Submitted.IdempotencyKey);
            Assert.Equal("found 2 public listings", output.RootElement.GetProperty("result").GetProperty("message").GetString());

            var persisted = Assert.Single((await store.GetAsync(project.Id))!.ButlerRuns);
            Assert.Equal(api.ButlerRunId, persisted.ButlerRunId);
            Assert.Equal("completed", persisted.Status);
            Assert.Equal(1, persisted.LastEventSequence);
            Assert.Equal(conversationId, persisted.ConversationId);
            Assert.Equal("Searching The Job Center", JsonDocument.Parse(persisted.Events[1].Data).RootElement.GetProperty("stage").GetString());
            Assert.Contains("found 2 public listings", persisted.ResultJson);
        }
    }

    [Fact]
    public async Task Plan_only_never_calls_the_Butler_api()
    {
        var project = ConciergeProject.Create("Recruitment", "Find a suitable role");
        var api = new FakeButlerWorkRunApi();
        using var provider = CreateProvider(api);
        await provider.GetRequiredService<IProjectStore>().CreateAsync(project);
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        var registry = new AgentToolRegistry([], [source]);

        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, null, Guid.NewGuid(), Guid.NewGuid(),
                   PermissionMode: ToolPermissionMode.PlanOnly)))
        {
            var tool = Assert.Single(registry.Tools, item => item.Name == "butler_find_jobs");
            var result = await tool.InvokeAsync(new JsonObject { ["keyword"] = "plumber" });
            Assert.False(result.Success);
            Assert.Contains("Plan only", result.FailureMessage);
            Assert.Equal(0, api.CapabilityReads);
            Assert.Null(api.Submitted);
        }
    }

    [Fact]
    public async Task Search_refuses_without_a_linked_Butler_account()
    {
        var project = ConciergeProject.Create("Recruitment", "Find a suitable role");
        var api = new FakeButlerWorkRunApi { RequiredAccount = true };
        using var provider = CreateProvider(api);
        await provider.GetRequiredService<IProjectStore>().CreateAsync(project);
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        var registry = new AgentToolRegistry([], [source]);

        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, null, Guid.NewGuid(), Guid.NewGuid())))
        {
            var tool = Assert.Single(registry.Tools, item => item.Name == "butler_find_jobs");
            var result = await tool.InvokeAsync(new JsonObject { ["keyword"] = "plumber" });
            Assert.False(result.Success);
            Assert.Contains("Connect your Butler account", result.FailureMessage);
            Assert.Null(api.Submitted);
        }
    }

    private static ServiceProvider CreateProvider(FakeButlerWorkRunApi api)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(UnavailableToolApprovalService.Instance);
        services.AddSingleton<IButlerWorkRunApi>(api);
        services.AddConciergeChat(Path.Combine(Path.GetTempPath(), $"butler-tool-{Guid.NewGuid():N}", "concierge.db"));
        services.AddConciergeTools();
        return services.BuildServiceProvider();
    }

    private sealed class FakeButlerWorkRunApi : IButlerWorkRunApi
    {
        private readonly Guid _butlerRunId = Guid.NewGuid();
        public Guid ButlerRunId => _butlerRunId;
        public ButlerWorkRunRequest? Submitted { get; private set; }
        public int CapabilityReads { get; private set; }
        public bool RequiredAccount { get; init; }

        public Task<ButlerWorkCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            CapabilityReads++;
            if (RequiredAccount) throw new ButlerAccountRequiredException();
            return Task.FromResult(new ButlerWorkCapabilities("1.0", [new("find_jobs", "Find jobs", "Search public job listings", "read_only")]));
        }

        public Task<ButlerWorkRunAccepted> SubmitAsync(ButlerWorkRunRequest request, CancellationToken cancellationToken = default)
        {
            Submitted = request;
            return Task.FromResult(new ButlerWorkRunAccepted(_butlerRunId, request.ConciergeRunId, "queued", "status", "events"));
        }

        public Task<ButlerWorkRunEnvelope?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<ButlerWorkRunEnvelope?>(null);

        public Task<ButlerWorkRunEnvelope> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            using var result = JsonDocument.Parse("""{"message":"found 2 public listings","jobs":[{"title":"Plumber"}]}""");
            using var request = JsonDocument.Parse("{}");
            var run = new ButlerWorkRun(runId, "user-1", Submitted!.ConciergeRunId, Submitted.IdempotencyKey,
                Submitted.WorkId, Submitted.ProjectId, Submitted.WorkspaceId, Submitted.ConversationId,
                Submitted.OperationId, Submitted.ActionMode, "completed", request.RootElement.Clone(), result.RootElement.Clone(),
                null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return Task.FromResult(new ButlerWorkRunEnvelope(run,
                [new ButlerWorkRunEvent(0, "queued", JsonDocument.Parse("{}").RootElement.Clone(), DateTimeOffset.UtcNow),
                 new ButlerWorkRunEvent(1, "progress", JsonDocument.Parse("""{"stage":"Searching The Job Center"}""").RootElement.Clone(), DateTimeOffset.UtcNow)]));
        }

        public Task<IReadOnlyList<ButlerWorkRunEvent>> ReadEventsAsync(Guid runId, long afterSequence, CancellationToken cancellationToken = default)
        {
            var updates = new[]
            {
                new ButlerWorkRunEvent(0, "queued", JsonDocument.Parse("{}").RootElement.Clone(), DateTimeOffset.UtcNow),
                new ButlerWorkRunEvent(1, "progress", JsonDocument.Parse("""{"stage":"Searching The Job Center"}""").RootElement.Clone(), DateTimeOffset.UtcNow),
            }.Where(item => item.Sequence > afterSequence).ToArray();
            return Task.FromResult<IReadOnlyList<ButlerWorkRunEvent>>(updates);
        }
        public Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
