using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Concierge.Shared.Chat;
using Concierge.Shared.Butler;
using Concierge.Shared.Components.Pages;
using Concierge.Shared.Planning;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Concierge.Tests;

public sealed class ProjectPageTests : BunitContext
{
    [Fact]
    public async Task Project_page_refreshes_and_cancels_persisted_Butler_work()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-butler-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        Services.AddSingleton<BackgroundRuns>();
        var api = new ProjectPageButlerApi();
        Services.AddSingleton<IButlerWorkRunApi>(api);
        var refreshRunId = Guid.NewGuid();
        var cancelRunId = Guid.NewGuid();
        var project = await Services.GetRequiredService<IProjectStore>().CreateAsync(
            ConciergeProject.Create("Recruitment", "Find the next right role") with
            {
                ButlerRuns =
                [
                    new ProjectButlerRun { ConciergeRunId = Guid.NewGuid(), ButlerRunId = refreshRunId, OperationId = "find_jobs", Summary = "Search roles", Status = "running" },
                    new ProjectButlerRun { ConciergeRunId = Guid.NewGuid(), ButlerRunId = cancelRunId, OperationId = "find_jobs", Summary = "Search nearby roles", Status = "running" },
                ],
            });
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects");

        var cut = Render<Projects>();
        cut.WaitForState(() => cut.FindAll(".projects-page[aria-busy='false']").Count > 0);
        cut.WaitForAssertion(() => Assert.Contains("Butler activity", cut.Find("#project-butler-title").TextContent));
        Assert.Contains("Search roles", cut.Find(".project-section[aria-labelledby='project-butler-title']").TextContent);

        await cut.InvokeAsync(() => cut.FindAll(".project-agent-item").Single(item => item.TextContent.Contains("Search roles"))
            .QuerySelector("button")!.ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("completed", cut.Find(".project-section[aria-labelledby='project-butler-title']").TextContent));
        Assert.Contains("two suitable roles", cut.Find(".project-section[aria-labelledby='project-butler-title']").TextContent);

        await cut.InvokeAsync(() => cut.FindAll(".project-agent-item").Single(item => item.TextContent.Contains("Search nearby roles"))
            .QuerySelectorAll("button").Single(button => button.TextContent.Contains("Stop Butler run")).ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Stop requested", cut.Find(".project-section[aria-labelledby='project-butler-title']").TextContent));
        Assert.Equal(cancelRunId, api.CancelledRunId);
        Assert.Equal(refreshRunId, api.RefreshedRunId);
        var saved = await Services.GetRequiredService<IProjectStore>().GetAsync(project.Id);
        Assert.Equal("completed", saved!.ButlerRuns.Single(run => run.ButlerRunId == refreshRunId).Status);
        Assert.Equal("cancellation_requested", saved.ButlerRuns.Single(run => run.ButlerRunId == cancelRunId).Status);
        Assert.True(saved.ButlerRuns.Single(run => run.ButlerRunId == cancelRunId).CancellationRequested);
    }

    [Fact]
    public async Task Starter_project_examples_seed_a_workflow_and_keep_the_agent_focus_visible()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-starters-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        Services.AddSingleton<BackgroundRuns>();

        var cut = Render<Projects>();
        cut.WaitForState(() => cut.FindAll(".projects-page[aria-busy='false']").Count > 0);
        cut.WaitForAssertion(() => Assert.Equal(9, cut.FindAll(".project-starter-card").Count));
        Assert.Equal(4, cut.Find(".project-starter-group[aria-label='Home starter Projects']").QuerySelectorAll(".project-starter-card").Length);
        Assert.Equal(5, cut.Find(".project-starter-group[aria-label='Work starter Projects']").QuerySelectorAll(".project-starter-card").Length);

        await cut.InvokeAsync(() => cut.FindAll(".project-starter-card").Single(card => card.TextContent.Contains("House Chores")).ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Allowance or pay planning", cut.Find(".project-starter-preview").TextContent));
        await cut.InvokeAsync(() => cut.Find("#project-name").ChangeAsync(new ChangeEventArgs { Value = "Our house routine" }));
        await cut.InvokeAsync(() => cut.Find(".project-primary").ClickAsync(new MouseEventArgs()));

        cut.WaitForAssertion(() => Assert.Equal("Our house routine", cut.Find(".project-detail-head h2").TextContent));
        var created = Assert.Single(await Services.GetRequiredService<IProjectStore>().ListAsync());
        var workflow = Assert.Single(created.Workflows);
        Assert.Equal("Weekly home routine", workflow.Name);
        Assert.Equal(3, workflow.Steps.Count);
        Assert.Contains("Allowance or pay planning", created.StarterAgentFocus);
        Assert.Contains("Starter assistant focus", cut.Find(".project-starter-focus").TextContent);
    }

    [Fact]
    public async Task Project_activity_shows_live_and_interrupted_run_states_without_guessing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-activity-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        Services.AddSingleton<BackgroundRuns>();
        var conversations = Services.GetRequiredService<IConversationStore>();
        var conversation = await conversations.StartAsync("local", "Launch planning");
        var runId = Guid.NewGuid();
        await conversations.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new { RunId = runId, Fingerprint = "test-fingerprint" }),
            cancellationToken: default, eventId: runId);

        var projects = Services.GetRequiredService<IProjectStore>();
        var project = await projects.CreateAsync(ConciergeProject.Create("Launch", "Plan a launch") with
        {
            ConversationIds = [conversation.Id]
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects");
        var cut = Render<Projects>();
        cut.WaitForState(() => cut.FindAll(".projects-page[aria-busy='false']").Count > 0);
        cut.WaitForAssertion(() => Assert.Contains("Interrupted", cut.Find(".project-activity-state").TextContent));

        using var cancellation = new CancellationTokenSource();
        var runs = Services.GetRequiredService<BackgroundRuns>();
        runs.Started(conversation.Id, cancellation);
        cut.WaitForAssertion(() => Assert.Contains("In progress", cut.Find(".project-activity-state").TextContent));

        runs.Finished(conversation.Id);
        cut.WaitForAssertion(() => Assert.Contains("Interrupted", cut.Find(".project-activity-state").TextContent));
        Assert.Contains(project.Name, cut.Find(".project-detail-head").TextContent);
        Assert.Contains("Project agents", cut.Find("#project-agents-title").TextContent);
        Assert.Contains("one review at a time", cut.Find(".project-section[aria-labelledby='project-agents-title']").TextContent);
    }

    [Fact]
    public async Task Reopened_Project_shows_saved_agent_run_events_in_sequence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-agent-feed-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        Services.AddSingleton<BackgroundRuns>();
        var now = DateTimeOffset.UtcNow;
        var run = new ProjectAgentRun
        {
            Name = "Launch analyst",
            Task = "List launch steps.",
            Status = ProjectAgentStatus.Review,
            Output = "First, test the offer.",
            Events =
            [
                new ProjectAgentEvent(Guid.NewGuid(), 0, ProjectAgentEventType.Queued, "{}", now),
                new ProjectAgentEvent(Guid.NewGuid(), 1, ProjectAgentEventType.Started, "{}", now.AddSeconds(1)),
                new ProjectAgentEvent(Guid.NewGuid(), 2, ProjectAgentEventType.Output, "First, test the offer.", now.AddSeconds(2)),
                new ProjectAgentEvent(Guid.NewGuid(), 3, ProjectAgentEventType.ReviewReady, "{}", now.AddSeconds(3)),
            ],
        };
        await Services.GetRequiredService<IProjectStore>().CreateAsync(
            ConciergeProject.Create("Launch", "Plan a launch") with { AgentRuns = [run] });
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects");

        var cut = Render<Projects>();
        cut.WaitForState(() => cut.FindAll(".projects-page[aria-busy='false']").Count > 0);
        cut.WaitForAssertion(() =>
        {
            var feed = cut.Find(".project-agent-event-feed");
            Assert.Contains("Review queued", feed.TextContent);
            Assert.Contains("Analysis started", feed.TextContent);
            Assert.Contains("New notes saved", feed.TextContent);
            Assert.Contains("Findings ready to review", feed.TextContent);
        });
        Assert.Equal(new[] { "0", "1", "2", "3" },
            cut.FindAll(".project-agent-event").Select(item => item.GetAttribute("data-event-sequence")));
    }

    [Fact]
    public async Task A_chat_can_be_promoted_to_a_project_with_cross_workflow_dependencies()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-page-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        Services.AddSingleton<BackgroundRuns>();
        var conversationId = Guid.NewGuid();
        var conversationStore = Services.GetRequiredService<IConversationStore>();
        await conversationStore.StartWithIdAsync(conversationId, "local", "Customer discovery");
        var runId = Guid.NewGuid();
        await conversationStore.AppendEventAsync(conversationId, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new { RunId = runId, Fingerprint = "test-fingerprint" }),
            cancellationToken: default, eventId: runId);
        await conversationStore.AppendEventAsync(conversationId, ConversationEventType.AssistantMessage,
            "We confirmed the first launch segment.");
        await conversationStore.AppendEventAsync(conversationId, ConversationEventType.TurnEnd,
            System.Text.Json.JsonSerializer.Serialize(new TurnRunReceipt(runId, "completed",
                "We confirmed the first launch segment.", "Circle AI", [], 0, null)));

        Services.GetRequiredService<NavigationManager>().NavigateTo($"/projects?conversation={conversationId}");
        var cut = Render<Projects>();
        cut.WaitForState(() => cut.FindAll(".projects-page[aria-busy='false']").Count > 0);
        cut.WaitForAssertion(() => Assert.Equal(9, cut.FindAll(".project-starter-card").Count));
        Assert.Contains("House Chores", cut.Find(".project-starters").TextContent);
        await cut.InvokeAsync(() => cut.Find("#project-name").ChangeAsync(new ChangeEventArgs { Value = "Neighborhood launch" }));
        await cut.InvokeAsync(() => cut.Find("#project-outcome").ChangeAsync(new ChangeEventArgs { Value = "Find first customers and launch our offer" }));
        await cut.InvokeAsync(() => cut.Find(".project-primary").ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Neighborhood launch", cut.Find(".project-detail-head h2").TextContent));

        var store = Services.GetRequiredService<IProjectStore>();
        var created = Assert.Single(await store.ListAsync());
        Assert.Equal(conversationId, created.ConversationIds.Single());

        await cut.InvokeAsync(() => cut.Find("#workflow-name").ChangeAsync(new ChangeEventArgs { Value = "Validate the offer" }));
        await cut.InvokeAsync(() => cut.Find("#workflow-outcome").ChangeAsync(new ChangeEventArgs { Value = "A clear offer tested with real customers" }));
        await cut.InvokeAsync(() => cut.Find("#workflow-name").ParentElement!.QuerySelector("button")!.ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Validate the offer", cut.Find(".project-workflow h4").TextContent));

        var workflowId = (await store.GetAsync(created.Id))!.Workflows.Single().Id;
        await cut.InvokeAsync(() => cut.Find($"#step-{workflowId}").ChangeAsync(new ChangeEventArgs { Value = "Talk to local businesses" }));
        await cut.InvokeAsync(() => cut.Find($"#step-{workflowId}").ParentElement!.QuerySelector("button")!.ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Talk to local businesses", cut.Find(".project-steps").TextContent));

        var firstStep = (await store.GetAsync(created.Id))!.Workflows.Single().Steps.Single();

        await cut.InvokeAsync(() => cut.Find("#workspace-name").ChangeAsync(new ChangeEventArgs { Value = "Customer discovery" }));
        await cut.InvokeAsync(() => cut.Find("#workspace-outcome").ChangeAsync(new ChangeEventArgs { Value = "Keep research and offer testing together" }));
        await cut.InvokeAsync(() => cut.Find(".project-workspace-workflows input[type='checkbox']").ChangeAsync(new ChangeEventArgs { Value = true }));
        await cut.InvokeAsync(() => cut.Find(".project-add-workspace button").ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Customer discovery", cut.Find(".project-workspaces").TextContent));
        var workspace = Assert.Single((await store.GetAsync(created.Id))!.Workspaces);
        Assert.Contains("What should an agent investigate in this Workspace?", cut.Find(".project-workspace-agent").TextContent);
        Assert.Contains("Concierge's AI must be ready", cut.Find(".project-workspace-agent").TextContent);
        Assert.Equal(conversationId, workspace.ConversationIds.Single());
        Assert.Equal(workflowId, workspace.WorkflowIds.Single());
        Assert.Equal($"/chat/{conversationId}?project={created.Id}&workspace={workspace.Id}",
            cut.Find(".project-workspace-conversations a").GetAttribute("href"));
        Assert.Contains("Completed", cut.Find(".project-activity-state").TextContent);
        Assert.Contains("We confirmed the first launch segment.", cut.Find(".project-activity-item").TextContent);
        Assert.Equal($"/chat/{conversationId}?project={created.Id}",
            cut.Find(".project-activity-footer a").GetAttribute("href"));

        await cut.InvokeAsync(() => cut.Find($"#step-{workflowId}").ChangeAsync(new ChangeEventArgs { Value = "Refine the offer" }));
        await cut.InvokeAsync(() => cut.Find("select[aria-label='Depends on']").ChangeAsync(new ChangeEventArgs { Value = firstStep.Id.ToString() }));
        await cut.InvokeAsync(() => cut.Find($"#step-{workflowId}").ParentElement!.QuerySelector("button")!.ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Depends on: Validate the offer: Talk to local businesses", cut.Find(".project-steps").TextContent));

        await cut.InvokeAsync(() => cut.Find("#workflow-name").ChangeAsync(new ChangeEventArgs { Value = "Build the launch website" }));
        await cut.InvokeAsync(() => cut.Find("#workflow-outcome").ChangeAsync(new ChangeEventArgs { Value = "A live website for prospective customers" }));
        await cut.InvokeAsync(() => cut.Find("#workflow-name").ParentElement!.QuerySelector("button")!.ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".project-workflow").Count));

        var secondWorkflowId = (await store.GetAsync(created.Id))!.Workflows.Single(workflow => workflow.Name == "Build the launch website").Id;
        await cut.InvokeAsync(() => cut.Find($"#step-{secondWorkflowId}").ChangeAsync(new ChangeEventArgs { Value = "Publish the first page" }));
        var dependencySelect = cut.Find($"#step-{secondWorkflowId}").ParentElement!.QuerySelector("select[aria-label='Depends on']")!;
        Assert.Contains($"After Validate the offer: Talk to local businesses", dependencySelect.InnerHtml);
        await cut.InvokeAsync(() => cut.Find($"#step-{secondWorkflowId}").ParentElement!.QuerySelector("select[aria-label='Depends on']")!.ChangeAsync(new ChangeEventArgs { Value = firstStep.Id.ToString() }));
        await cut.InvokeAsync(() => cut.Find($"#step-{secondWorkflowId}").ParentElement!.QuerySelector("button")!.ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Depends on: Validate the offer: Talk to local businesses", cut.Find($"#step-{secondWorkflowId}").ParentElement!.ParentElement!.TextContent));

        var persisted = (await store.GetAsync(created.Id))!;
        Assert.Equal(2, persisted.Workflows.Single(workflow => workflow.Name == "Validate the offer").Steps.Count);
        Assert.Equal(firstStep.Id, persisted.Workflows.Single(workflow => workflow.Name == "Build the launch website").Steps.Single().DependsOn.Single());

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Edit Validate the offer workflow']").ClickAsync(new MouseEventArgs()));
        await cut.InvokeAsync(() => cut.Find($"#workflow-edit-name-{workflowId}").ChangeAsync(new ChangeEventArgs { Value = "Customer discovery" }));
        await cut.InvokeAsync(() => cut.Find($"#workflow-edit-outcome-{workflowId}").ChangeAsync(new ChangeEventArgs { Value = "Understand what local customers will pay for" }));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save workflow").ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Customer discovery", cut.FindAll(".project-workflow h4").First().TextContent));

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Edit Talk to local businesses step']").ClickAsync(new MouseEventArgs()));
        await cut.InvokeAsync(() => cut.Find($"#step-edit-name-{firstStep.Id}").ChangeAsync(new ChangeEventArgs { Value = "Interview ten local businesses" }));
        await cut.InvokeAsync(() => cut.Find($"#step-edit-description-{firstStep.Id}").ChangeAsync(new ChangeEventArgs { Value = "Ask about their current advertising and budget." }));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save step").ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Interview ten local businesses", cut.Find(".project-steps").TextContent));

        var edited = (await store.GetAsync(created.Id))!;
        var editedWorkflow = edited.Workflows.Single(workflow => workflow.Id == workflowId);
        Assert.Equal("Understand what local customers will pay for", editedWorkflow.DesiredOutcome);
        Assert.Equal("Ask about their current advertising and budget.", editedWorkflow.Steps.Single(step => step.Id == firstStep.Id).Description);
        Assert.Contains("Depends on: Customer discovery: Interview ten local businesses", cut.Find($"#step-{secondWorkflowId}").ParentElement!.ParentElement!.TextContent);

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Edit Publish the first page step']").ClickAsync(new MouseEventArgs()));
        var addedDependency = cut.Find("input[aria-label='Wait for Customer discovery: Refine the offer']");
        Assert.False(addedDependency.HasAttribute("checked"));
        await cut.InvokeAsync(() => cut.Find("input[aria-label='Wait for Customer discovery: Refine the offer']").ChangeAsync(new ChangeEventArgs { Value = true }));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save step").ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Depends on: Customer discovery: Interview ten local businesses, Customer discovery: Refine the offer",
            cut.Find($"#step-{secondWorkflowId}").ParentElement!.ParentElement!.TextContent));

        var withBranches = (await store.GetAsync(created.Id))!;
        Assert.Equal(new[] { firstStep.Id, persisted.Workflows.Single(workflow => workflow.Id == workflowId).Steps.Single(step => step.Name == "Refine the offer").Id }.Order(),
            withBranches.Workflows.Single(workflow => workflow.Id == secondWorkflowId).Steps.Single().DependsOn.Order());

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Edit Interview ten local businesses step']").ClickAsync(new MouseEventArgs()));
        Assert.True(cut.Find("input[aria-label='Wait for Customer discovery: Refine the offer']").HasAttribute("disabled"));
        Assert.True(cut.Find("input[aria-label='Wait for Build the launch website: Publish the first page']").HasAttribute("disabled"));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").ClickAsync(new MouseEventArgs()));
    }

    [Fact]
    public async Task A_chat_can_be_added_to_an_existing_project_from_the_project_page()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-link-chat-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        Services.AddSingleton<BackgroundRuns>();
        var conversations = Services.GetRequiredService<IConversationStore>();
        var conversation = await conversations.StartAsync("local", "Planning a second campaign");
        await conversations.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage,
            "Help me plan a second campaign for existing customers.");

        var store = Services.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Customer growth", "Grow repeat customers", "Business"));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/projects?conversation={conversation.Id}");
        var cut = Render<Projects>();
        cut.WaitForState(() => cut.FindAll(".projects-page[aria-busy='false']").Count > 0);
        await cut.InvokeAsync(() => cut.FindAll(".project-list-item").Single(button => button.TextContent.Contains(project.Name)).ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Add this conversation to the project", cut.Markup));

        await cut.InvokeAsync(() => cut.Find(".project-link-conversation").ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("This conversation is linked to the project.", cut.Markup));
        Assert.Contains(conversation.Title, cut.Find(".project-activity-list").TextContent);
        Assert.Contains("A request was added", cut.Find(".project-activity-list").TextContent);
        Assert.Contains($"/chat/{conversation.Id}?project={project.Id}", cut.Find(".project-activity-list a").GetAttribute("href"));
        Assert.Equal(conversation.Id, Assert.Single((await store.GetAsync(project.Id))!.ConversationIds));
        Assert.Contains("Help me plan a second campaign", (await conversations.ReadEventsAsync(conversation.Id)).Last().Data);
        Assert.Empty(cut.FindAll(".project-link-conversation"));
    }

    [Fact]
    public async Task Project_artifacts_include_generated_outputs_from_linked_conversation_events()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-artifact-event-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        Services.AddSingleton<BackgroundRuns>();
        var conversations = Services.GetRequiredService<IConversationStore>();
        var conversation = await conversations.StartAsync("local", "Campaign image");
        var source = await conversations.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage,
            "Here's what I came up with: ![generated](data:image/png;base64,AQID)", "Test images");
        var artifact = new ConversationArtifactRecord(Guid.NewGuid(), "Generated image", "image/png", source.Id);
        await conversations.AppendEventAsync(conversation.Id, ConversationEventType.ArtifactCreated,
            System.Text.Json.JsonSerializer.Serialize(artifact), "Test images");
        var toolSource = await conversations.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult,
            "```tool-result\n{\"name\":\"make_picture\",\"success\":true,\"output\":\"campaign-image.png\"}\n```");
        var toolArtifact = new ConversationArtifactRecord(Guid.NewGuid(), "campaign-image.png", "image/png", toolSource.Id,
            Guid.NewGuid());
        await conversations.AppendEventAsync(conversation.Id, ConversationEventType.ArtifactCreated,
            System.Text.Json.JsonSerializer.Serialize(toolArtifact), "Test images");
        var project = await Services.GetRequiredService<IProjectStore>().CreateAsync(
            ConciergeProject.Create("Spring campaign", "Prepare campaign assets", "Business") with
            {
                ConversationIds = [conversation.Id],
            });

        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects");
        var cut = Render<Projects>();
        cut.WaitForState(() => cut.FindAll(".projects-page[aria-busy='false']").Count > 0);
        cut.WaitForAssertion(() => Assert.Contains("Generated image", cut.Find(".project-artifacts").TextContent));
        var row = cut.Find($"[data-conversation-artifact='{artifact.ArtifactId}']");
        Assert.Contains("image/png", row.TextContent);
        Assert.Equal($"/chat/{conversation.Id}?project={project.Id}", row.QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("campaign-image.png",
            cut.Find($"[data-conversation-artifact='{toolArtifact.ArtifactId}']").TextContent);
    }

    [Fact]
    public async Task Start_up_mode_asks_new_or_existing_and_connects_multiple_projects()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"startup-page-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        Services.AddSingleton<BackgroundRuns>();
        var store = Services.GetRequiredService<IProjectStore>();
        var existingProject = await store.CreateAsync(ConciergeProject.Create(
            "First campaign", "Launch an initial campaign", "Business"));

        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects");
        var cut = Render<Projects>();
        cut.WaitForState(() => cut.FindAll(".projects-page[aria-busy='false']").Count > 0);
        await cut.InvokeAsync(() => cut.FindAll("button.projects-new").Single(button => button.TextContent.Contains("Start a business")).ClickAsync(new MouseEventArgs()));
        Assert.Contains("New business or existing business?", cut.Find(".startup-create-panel").TextContent);
        var newBusinessChoice = cut.FindAll(".startup-choice button").Single(button => button.TextContent.Contains("New business"));
        var existingBusinessChoice = cut.FindAll(".startup-choice button").Single(button => button.TextContent.Contains("Existing business"));
        Assert.Equal("true", newBusinessChoice.GetAttribute("aria-pressed"));
        Assert.Equal("false", existingBusinessChoice.GetAttribute("aria-pressed"));
        await cut.InvokeAsync(() => existingBusinessChoice.ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.FindAll(".startup-choice button").Single(button => button.TextContent.Contains("New business")).GetAttribute("aria-pressed"));
            Assert.Equal("true", cut.FindAll(".startup-choice button").Single(button => button.TextContent.Contains("Existing business")).GetAttribute("aria-pressed"));
            Assert.Equal("What is happening in the business today?", cut.Find("label[for='startup-context']").TextContent);
        });
        await cut.InvokeAsync(() => cut.Find("#startup-name").ChangeAsync(new ChangeEventArgs { Value = "Northside Ads" }));
        await cut.InvokeAsync(() => cut.Find("#startup-context").ChangeAsync(new ChangeEventArgs { Value = "Two people, ten local customers, mostly one-off campaigns." }));
        await cut.InvokeAsync(() => cut.Find("#startup-goal").ChangeAsync(new ChangeEventArgs { Value = "Grow recurring customers and expand to a second area" }));
        await cut.InvokeAsync(() => cut.FindAll("button.project-primary").Single(button => button.TextContent.Contains("Create Start-Up journey")).ClickAsync(new MouseEventArgs()));

        cut.WaitForAssertion(() => Assert.Contains("Northside Ads", cut.Find(".startup-detail-head").TextContent));
        var startup = Assert.Single(await store.ListStartupsAsync());
        Assert.Equal(BusinessJourneyType.ExistingBusiness, startup.Type);
        Assert.Contains("Two people", startup.CurrentContext);

        await cut.InvokeAsync(() => cut.Find("#startup-project-select").ChangeAsync(new ChangeEventArgs { Value = existingProject.Id.ToString() }));
        await cut.InvokeAsync(() => cut.FindAll(".startup-add-project button").Single(button => button.TextContent.Contains("Connect project")).ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".startup-linked-projects .project-list-item")));
        Assert.Equal(startup.Id, (await store.GetAsync(existingProject.Id))!.StartupJourneyId);

        await cut.InvokeAsync(() => cut.FindAll("button.project-primary").Single(button => button.TextContent.Contains("Create a project for this business")).ClickAsync(new MouseEventArgs()));
        await cut.InvokeAsync(() => cut.Find("#project-name").ChangeAsync(new ChangeEventArgs { Value = "Customer growth" }));
        await cut.InvokeAsync(() => cut.Find("#project-outcome").ChangeAsync(new ChangeEventArgs { Value = "Build a repeatable way to win recurring customers" }));
        await cut.InvokeAsync(() => cut.FindAll("button.project-primary").Single(button => button.TextContent.Contains("Create project for this business")).ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".project-list-item").Count(item => item.TextContent.Contains("workflow"))));

        var savedStartup = await store.GetStartupAsync(startup.Id);
        Assert.Equal(2, savedStartup!.ProjectIds.Count);
        var growthProject = (await store.ListAsync()).Single(project => project.Name == "Customer growth");
        Assert.Equal(startup.Id, growthProject.StartupJourneyId);

        await cut.InvokeAsync(() => cut.Find(".project-startup-list .project-list-item").ClickAsync(new MouseEventArgs()));
        Assert.Contains("Plan with Concierge", cut.Find(".startup-detail-head").ParentElement!.TextContent);
        await cut.InvokeAsync(() => cut.Find(".startup-edit-toggle").ClickAsync(new MouseEventArgs()));
        await cut.InvokeAsync(() => cut.Find("#startup-edit-name").ChangeAsync(new ChangeEventArgs { Value = "Northside Creative Ads" }));
        await cut.InvokeAsync(() => cut.Find("#startup-edit-context").ChangeAsync(new ChangeEventArgs { Value = "Three people, twelve recurring customers, and one part-time designer." }));
        await cut.InvokeAsync(() => cut.Find("#startup-edit-goal").ChangeAsync(new ChangeEventArgs { Value = "Build a profitable team and expand to three areas" }));
        await cut.InvokeAsync(() => cut.FindAll("button.project-primary").Single(button => button.TextContent.Contains("Save business details")).ClickAsync(new MouseEventArgs()));
        cut.WaitForAssertion(() => Assert.Contains("Northside Creative Ads", cut.Find(".startup-detail-head h2").TextContent));
        var updatedJourney = await store.GetStartupAsync(startup.Id);
        Assert.Equal("Three people, twelve recurring customers, and one part-time designer.", updatedJourney!.CurrentContext);
        Assert.Equal("Build a profitable team and expand to three areas", updatedJourney.Goal);
        Assert.Equal(2, updatedJourney.ProjectIds.Count);

        await cut.InvokeAsync(() => cut.Find("button.project-primary").ClickAsync(new MouseEventArgs()));
        Assert.Equal($"http://localhost/chat?startup={startup.Id}",
            Services.GetRequiredService<NavigationManager>().Uri);
    }
}

internal sealed class ProjectPageButlerApi : IButlerWorkRunApi
{
    public Guid? CancelledRunId { get; private set; }
    public Guid? RefreshedRunId { get; private set; }
    public Task<ButlerWorkCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ButlerWorkCapabilities("1", []));
    public Task<ButlerWorkRunAccepted> SubmitAsync(ButlerWorkRunRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<ButlerWorkRunEnvelope?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<ButlerWorkRunEnvelope?>(null);
    public Task<ButlerWorkRunEnvelope> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        RefreshedRunId = runId;
        using var request = JsonDocument.Parse("{}");
        using var result = JsonDocument.Parse("""{"message":"two suitable roles"}""");
        var run = new ButlerWorkRun(runId, "test-user", Guid.NewGuid(), "key", null, null, null, null,
            "find_jobs", "AskFirst", "completed", request.RootElement.Clone(), result.RootElement.Clone(), null,
            false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return Task.FromResult(new ButlerWorkRunEnvelope(run, []));
    }
    public Task<IReadOnlyList<ButlerWorkRunEvent>> ReadEventsAsync(Guid runId, long afterSequence, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ButlerWorkRunEvent>>([]);
    public Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        CancelledRunId = runId;
        return Task.FromResult(true);
    }
}
