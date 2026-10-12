using Concierge.Shared.Chat;
using Concierge.Shared.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ProjectStoreTests
{
    [Fact]
    public async Task Project_and_workflow_documents_survive_a_new_store_instance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db");
        var workflowId = Guid.NewGuid();
        var firstStep = new ProjectStep { Name = "Learn what customers need" };
        var secondStep = new ProjectStep { Name = "Shape the first release", DependsOn = [firstStep.Id] };
        var conversationId = Guid.NewGuid();
        var project = ConciergeProject.Create("Neighborhood advertising", "Launch a small advertising business", "Business") with
        {
            ConversationIds = [conversationId],
            Workflows =
            [
                new ProjectWorkflow
                {
                    Id = workflowId,
                    Name = "Validate the idea",
                    DesiredOutcome = "Find a clear first customer and offer",
                    Steps = [firstStep, secondStep],
                }
            ],
            Workspaces =
            [
                new ProjectWorkspace
                {
                    Name = "Customer discovery",
                    DesiredOutcome = "Keep customer research together",
                    WorkflowIds = [workflowId],
                    ConversationIds = [conversationId],
                }
            ],
            Assumptions = ["Start with one local area"],
            BudgetLimit = 250m,
        };

        ConciergeProject created;
        using (var provider = CreateProvider(path))
            created = await provider.GetRequiredService<IProjectStore>().CreateAsync(project);

        using var nextProvider = CreateProvider(path);
        var restored = await nextProvider.GetRequiredService<IProjectStore>().GetAsync(created.Id);

        Assert.NotNull(restored);
        Assert.Equal(created.Id, restored.Id);
        Assert.Equal(1, restored.Revision);
        Assert.Equal("Business", restored.WorkType);
        Assert.Equal(250m, restored.BudgetLimit);
        Assert.Equal(2, Assert.Single(restored.Workflows).Steps.Count);
        Assert.Equal(firstStep.Id, restored.Workflows[0].Steps[1].DependsOn.Single());
        var workspace = Assert.Single(restored.Workspaces);
        Assert.Equal(workflowId, workspace.WorkflowIds.Single());
        Assert.Equal(conversationId, workspace.ConversationIds.Single());
        Assert.Equal("Start with one local area", restored.Assumptions.Single());
    }

    [Fact]
    public async Task Saving_requires_the_current_revision_and_conflicts_are_explicit()
    {
        using var provider = CreateProvider(Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db"));
        var store = provider.GetRequiredService<IProjectStore>();
        var created = await store.CreateAsync(ConciergeProject.Create("Website", "Publish a business website", "Business"));
        var firstReader = await store.GetAsync(created.Id);
        var secondReader = await store.GetAsync(created.Id);
        Assert.NotNull(firstReader);
        Assert.NotNull(secondReader);

        var firstSaved = await store.SaveAsync(firstReader with { Status = ProjectStatus.Active });
        Assert.Equal(2, firstSaved.Revision);

        var stale = await Assert.ThrowsAsync<ProjectRevisionConflictException>(
            () => store.SaveAsync(secondReader with { Status = ProjectStatus.Completed }));
        Assert.Equal(created.Id, stale.ProjectId);
        Assert.Equal(ProjectStatus.Active, (await store.GetAsync(created.Id))!.Status);
    }

    [Fact]
    public async Task Start_up_journeys_preserve_the_new_or_existing_business_decision_and_context()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db");
        StartupJourney created;
        using (var provider = CreateProvider(databasePath))
        {
            var store = provider.GetRequiredService<IProjectStore>();
            created = await store.CreateStartupAsync(StartupJourney.Create(
                BusinessJourneyType.ExistingBusiness,
                "Northside Ads",
                "Grow recurring customers and expand to a second area",
                "Two people, ten local customers, mostly one-off campaigns."));
            Assert.Equal(1, created.Revision);
        }

        using var reopened = CreateProvider(databasePath);
        var restored = await reopened.GetRequiredService<IProjectStore>().GetStartupAsync(created.Id);
        Assert.NotNull(restored);
        Assert.Equal(BusinessJourneyType.ExistingBusiness, restored.Type);
        Assert.Equal("Northside Ads", restored.Name);
        Assert.Equal("Two people, ten local customers, mostly one-off campaigns.", restored.CurrentContext);
        Assert.Equal("Grow recurring customers and expand to a second area", restored.Goal);
    }

    [Fact]
    public async Task Linking_projects_to_a_business_journey_is_persistent_and_single_parent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db");
        StartupJourney linked;
        Guid researchId;
        Guid launchId;
        using (var provider = CreateProvider(databasePath))
        {
            var store = provider.GetRequiredService<IProjectStore>();
            var startup = await store.CreateStartupAsync(StartupJourney.Create(
                BusinessJourneyType.NewBusiness, "Neighborhood Ads", "Build a sustainable local advertising business"));
            var research = await store.CreateAsync(ConciergeProject.Create("Customer research", "Learn what local businesses need", "Business"));
            var launch = await store.CreateAsync(ConciergeProject.Create("First campaign", "Launch the first customer campaign", "Business"));
            researchId = research.Id;
            launchId = launch.Id;

            linked = await store.LinkProjectToStartupAsync(startup.Id, research.Id);
            linked = await store.LinkProjectToStartupAsync(startup.Id, launch.Id);
            var replayed = await store.LinkProjectToStartupAsync(startup.Id, launch.Id);

            Assert.Equal(2, linked.ProjectIds.Count);
            Assert.Equal(linked.ProjectIds, replayed.ProjectIds);
            Assert.Equal(startup.Id, (await store.GetAsync(research.Id))!.StartupJourneyId);
            Assert.Equal(startup.Id, (await store.GetAsync(launch.Id))!.StartupJourneyId);
        }

        using var reopened = CreateProvider(databasePath);
        var secondStore = reopened.GetRequiredService<IProjectStore>();
        var restoredStartup = await secondStore.GetStartupAsync(linked.Id);
        Assert.Equal(2, restoredStartup!.ProjectIds.Count);
        Assert.Contains(researchId, restoredStartup.ProjectIds);
        Assert.Contains(launchId, restoredStartup.ProjectIds);
    }

    [Fact]
    public async Task Linking_a_planning_conversation_to_a_business_journey_is_persistent_and_single_parent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db");
        var conversationId = Guid.NewGuid();
        StartupJourney first;
        using (var provider = CreateProvider(databasePath))
        {
            var store = provider.GetRequiredService<IProjectStore>();
            var journey = await store.CreateStartupAsync(StartupJourney.Create(
                BusinessJourneyType.NewBusiness, "Northside Ads", "Build a sustainable advertising business"));
            var other = await store.CreateStartupAsync(StartupJourney.Create(
                BusinessJourneyType.ExistingBusiness, "Current Agency", "Expand to a second area", "Two local staff and ten customers."));

            first = await store.LinkConversationToStartupAsync(journey.Id, conversationId);
            var replayed = await store.LinkConversationToStartupAsync(journey.Id, conversationId);
            Assert.Equal(first.Revision, replayed.Revision);
            Assert.Equal(conversationId, Assert.Single(first.ConversationIds));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.LinkConversationToStartupAsync(other.Id, conversationId));

            var current = (await store.GetStartupAsync(journey.Id))!;
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.SaveStartupAsync(current with { ConversationIds = [] }));
        }

        using var reopened = CreateProvider(databasePath);
        var restored = await reopened.GetRequiredService<IProjectStore>().GetStartupAsync(first.Id);
        Assert.Equal(conversationId, Assert.Single(restored!.ConversationIds));
    }

    [Fact]
    public async Task Existing_project_database_gets_the_startup_table_without_replacing_project_rows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"projects-legacy-{Guid.NewGuid():N}", "concierge.db");
        var projectDatabasePath = Path.Combine(Path.GetDirectoryName(databasePath)!, "projects.db");
        Directory.CreateDirectory(Path.GetDirectoryName(projectDatabasePath)!);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={projectDatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Projects (Id TEXT NOT NULL PRIMARY KEY, Revision INTEGER NOT NULL, Json TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }

        using var provider = CreateProvider(databasePath);
        var store = provider.GetRequiredService<IProjectStore>();
        var startup = await store.CreateStartupAsync(StartupJourney.Create(
            BusinessJourneyType.NewBusiness, "A new idea", "Test and validate the idea"));
        Assert.NotNull(await store.GetStartupAsync(startup.Id));

        await using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={projectDatabasePath}");
        await verify.OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Projects', 'StartupJourneys');";
        Assert.Equal(2L, (long)(await query.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Existing_business_requires_context_but_new_business_does_not()
    {
        Assert.Throws<ArgumentException>(() => StartupJourney.Create(
            BusinessJourneyType.ExistingBusiness, "Existing business", "Grow sales"));
        var newBusiness = StartupJourney.Create(BusinessJourneyType.NewBusiness, "New idea", "Explore the market");
        Assert.Equal(string.Empty, newBusiness.CurrentContext);
    }

    [Fact]
    public async Task Workflow_dependencies_must_resolve_to_a_different_step_in_the_project()
    {
        using var provider = CreateProvider(Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db"));
        var project = ConciergeProject.Create("MVP", "Build the first version") with
        {
            Workflows =
            [
                new ProjectWorkflow
                {
                    Name = "Build",
                    Steps = [new ProjectStep { Name = "Ship", DependsOn = [Guid.NewGuid()] }],
                }
            ],
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetRequiredService<IProjectStore>().CreateAsync(project));
    }

    [Fact]
    public async Task Workflow_dependencies_cannot_form_a_cycle()
    {
        using var provider = CreateProvider(Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db"));
        var first = new ProjectStep { Name = "Plan", DependsOn = [] };
        var second = new ProjectStep { Name = "Build", DependsOn = [first.Id] };
        first = first with { DependsOn = [second.Id] };
        var project = ConciergeProject.Create("MVP", "Build the first version") with
        {
            Workflows =
            [
                new ProjectWorkflow
                {
                    Name = "Build",
                    Steps = [first, second],
                }
            ],
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetRequiredService<IProjectStore>().CreateAsync(project));
    }

    [Fact]
    public async Task Workspace_links_must_point_to_project_workflows_and_conversations()
    {
        using var provider = CreateProvider(Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db"));
        var project = ConciergeProject.Create("MVP", "Build the first version") with
        {
            Workspaces =
            [
                new ProjectWorkspace
                {
                    Name = "Launch",
                    DesiredOutcome = "Prepare release",
                    WorkflowIds = [Guid.NewGuid()],
                    ConversationIds = [Guid.NewGuid()],
                }
            ],
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetRequiredService<IProjectStore>().CreateAsync(project));
    }

    [Fact]
    public async Task Archived_projects_are_hidden_by_default_but_remain_recoverable()
    {
        using var provider = CreateProvider(Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db"));
        var store = provider.GetRequiredService<IProjectStore>();
        var archived = await store.CreateAsync(ConciergeProject.Create("Old idea", "Keep the notes") with
        {
            Status = ProjectStatus.Archived,
        });

        Assert.Empty(await store.ListAsync());
        Assert.Equal(archived.Id, Assert.Single(await store.ListAsync(includeArchived: true)).Id);
    }

    [Fact]
    public async Task Project_database_is_separate_and_does_not_replace_existing_chat_data()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}", "concierge.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var options = new DbContextOptionsBuilder<ConciergeChatDbContext>()
            .UseSqlite($"Data Source={databasePath}").Options;
        await using (var db = new ConciergeChatDbContext(options))
            await db.Database.EnsureCreatedAsync();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddConciergeChat(databasePath)
            .BuildServiceProvider();
        var conversations = provider.GetRequiredService<IConversationStore>();
        var conversation = await conversations.StartAsync("local", title: "Keep this chat");
        var project = await provider.GetRequiredService<IProjectStore>()
            .CreateAsync(ConciergeProject.Create("Keep this project", "Persist both stores"));

        Assert.NotNull(await conversations.GetAsync(conversation.Id));
        Assert.NotNull(await provider.GetRequiredService<IProjectStore>().GetAsync(project.Id));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(databasePath)!, "projects.db")));
    }

    private static ServiceProvider CreateProvider(string databasePath)
        => new ServiceCollection()
            .AddLogging()
            .AddConciergeProjects(databasePath)
            .BuildServiceProvider();
}
