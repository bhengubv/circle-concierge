using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Chat;
using Concierge.Shared.Planning;
using Concierge.Shared.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Concierge.Tests;

public sealed class ProjectWorkToolsTests
{
    [Fact]
    public async Task Project_tools_are_workspace_scoped_and_update_steps_with_dependency_checks()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var services = new ServiceCollection();
        var approval = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IProjectStore>();
        var firstStep = new ProjectStep { Name = "Confirm the customer problem" };
        var secondStep = new ProjectStep { Name = "Validate the proposed offer", DependsOn = [firstStep.Id] };
        var workflow = new ProjectWorkflow
        {
            Name = "Customer discovery",
            DesiredOutcome = "Evidence about the customer problem and offer",
            Steps = [firstStep, secondStep],
        };
        var otherWorkflow = new ProjectWorkflow
        {
            Name = "Launch website",
            DesiredOutcome = "A public landing page",
            Steps = [new ProjectStep { Name = "Draft the landing page" }],
        };
        var workspace = new ProjectWorkspace
        {
            Name = "Research workspace",
            DesiredOutcome = "Validate the offer",
            WorkflowIds = [workflow.Id],
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Find first customers") with
        {
            Workflows = [workflow, otherWorkflow],
            Workspaces = [workspace],
        });

        var projectToolSource = Assert.Single(provider.GetServices<IAgentToolSource>());
        var registry = new AgentToolRegistry([], [projectToolSource]);
        Assert.DoesNotContain(registry.Tools, tool => tool.Name.StartsWith("project_", StringComparison.Ordinal));

        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, workspace.Id, Guid.NewGuid(), Guid.NewGuid())))
        {
            var read = Assert.Single(registry.Tools, tool => tool.Name == "project_read_work");
            var update = Assert.Single(registry.Tools, tool => tool.Name == "project_update_step");
            Assert.True(read.IsReadOnly);
            Assert.False(update.IsReadOnly);
            Assert.Contains("[requires approval]", registry.BuildSystemPromptAddendum(), StringComparison.Ordinal);

            var view = await read.InvokeAsync(new JsonObject());
            Assert.True(view.Success);
            using (var document = JsonDocument.Parse(view.Output))
            {
                var workflows = document.RootElement.GetProperty("workflows");
                Assert.Equal(1, workflows.GetArrayLength());
                Assert.Equal(workflow.Id, workflows[0].GetProperty("id").GetGuid());
            }

            var blocked = await update.InvokeAsync(Arguments(otherWorkflow.Id, otherWorkflow.Steps[0].Id, "Completed"));
            Assert.False(blocked.Success);
            Assert.Contains("not part of the active Workspace", blocked.FailureMessage);

            var unmet = await update.InvokeAsync(Arguments(workflow.Id, secondStep.Id, "Completed"));
            Assert.False(unmet.Success);
            Assert.Contains("dependencies", unmet.FailureMessage);

            var completedFirst = await update.InvokeAsync(Arguments(workflow.Id, firstStep.Id, "Completed"));
            Assert.True(completedFirst.Success, completedFirst.FailureMessage);
            var completedSecond = await update.InvokeAsync(Arguments(workflow.Id, secondStep.Id, "Completed"));
            Assert.True(completedSecond.Success, completedSecond.FailureMessage);
            Assert.Equal(2, approval.Requests.Count);
            Assert.All(approval.Requests, request => Assert.Equal("project_update_step", request.ToolName));
            Assert.Contains("Current status: Planned", approval.Requests[0].Detail, StringComparison.Ordinal);
        }

        var saved = await store.GetAsync(project.Id);
        var savedWorkflow = Assert.Single(saved!.Workflows, item => item.Id == workflow.Id);
        Assert.Equal(ProjectWorkStatus.Completed, savedWorkflow.Status);
        Assert.All(savedWorkflow.Steps, step => Assert.Equal(ProjectWorkStatus.Completed, step.Status));
        Assert.Equal(ProjectWorkStatus.Planned,
            saved.Workflows.Single(item => item.Id == otherWorkflow.Id).Status);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Theory]
    [InlineData(ToolApprovalDecision.Denied)]
    [InlineData(ToolApprovalDecision.Unavailable)]
    public async Task Project_step_update_fails_closed_when_approval_is_not_granted(ToolApprovalDecision decision)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-tools-approval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(decision);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var step = new ProjectStep { Name = "Review the offer" };
        var workflow = new ProjectWorkflow
        {
            Name = "Customer discovery",
            DesiredOutcome = "A reviewed offer",
            Steps = [step],
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Find customers") with
        {
            Workflows = [workflow],
        });
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, null, Guid.NewGuid(), Guid.NewGuid())))
        {
            var update = Assert.Single(new AgentToolRegistry([], [source]).Tools,
                tool => tool.Name == "project_update_step");
            var result = await update.InvokeAsync(Arguments(workflow.Id, step.Id, "Active"));
            Assert.False(result.Success);
            Assert.Contains("no change was made", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        }

        var persisted = await store.GetAsync(project.Id);
        Assert.Equal(ProjectWorkStatus.Planned, Assert.Single(persisted!.Workflows).Steps[0].Status);
        Assert.Single(approval.Requests);
        Assert.Equal(decision, approval.Requests[0].Decision);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Project_tool_creates_approved_workflow_with_parallel_steps_and_workspace_link()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-tools-create-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var workspace = new ProjectWorkspace { Name = "Launch planning", DesiredOutcome = "A validated launch" };
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Open the business") with
        {
            Workspaces = [workspace],
        });
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, workspace.Id, Guid.NewGuid(), Guid.NewGuid())))
        {
            var create = Assert.Single(new AgentToolRegistry([], [source]).Tools,
                tool => tool.Name == "project_create_workflow");
            Assert.False(create.IsReadOnly);
            var result = await create.InvokeAsync(CreateWorkflowArguments());
            Assert.True(result.Success, result.FailureMessage);
            Assert.Contains("3 planned steps", result.Output, StringComparison.Ordinal);
        }

        var saved = await store.GetAsync(project.Id);
        var workflow = Assert.Single(saved!.Workflows);
        Assert.Equal("Validate the offer", workflow.Name);
        Assert.Equal(ProjectStatus.Active, saved.Status);
        Assert.Equal(workflow.Id, Assert.Single(saved.Workspaces).WorkflowIds.Single());
        Assert.Equal(3, workflow.Steps.Count);
        Assert.Empty(workflow.Steps[0].DependsOn);
        Assert.Empty(workflow.Steps[1].DependsOn);
        Assert.Equal(new[] { workflow.Steps[0].Id, workflow.Steps[1].Id }, workflow.Steps[2].DependsOn);
        Assert.Single(approval.Requests);
        Assert.Equal("project_create_workflow", approval.Requests[0].ToolName);
        Assert.Contains("2. Identify first customers", approval.Requests[0].Detail, StringComparison.Ordinal);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Project_tool_can_link_new_work_to_a_step_in_an_existing_workspace_workflow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-tools-cross-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();

        var discoveryStep = new ProjectStep { Name = "Validate customer demand" };
        var discovery = new ProjectWorkflow
        {
            Name = "Customer discovery",
            DesiredOutcome = "Evidence of demand",
            Steps = [discoveryStep],
        };
        var workspace = new ProjectWorkspace
        {
            Name = "Launch",
            DesiredOutcome = "Prepare a validated launch",
            WorkflowIds = [discovery.Id],
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Launch a validated offer") with
        {
            Workflows = [discovery],
            Workspaces = [workspace],
        });

        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, workspace.Id, Guid.NewGuid(), Guid.NewGuid())))
        {
            var create = Assert.Single(new AgentToolRegistry([], [source]).Tools,
                tool => tool.Name == "project_create_workflow");
            var arguments = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                name = "Launch preparation",
                desired_outcome = "A launch plan grounded in customer evidence",
                steps = new[]
                {
                    new
                    {
                        name = "Prepare launch messaging",
                        description = "Use the validated customer language.",
                        depends_on_indices = Array.Empty<int>(),
                        depends_on_step_ids = new[] { discoveryStep.Id.ToString() },
                    },
                },
            }))!;

            var result = await create.InvokeAsync(arguments);
            Assert.True(result.Success, result.FailureMessage);
            Assert.Contains("Validate customer demand", approval.Requests.Single().Detail, StringComparison.Ordinal);
        }

        var saved = await store.GetAsync(project.Id);
        var created = Assert.Single(saved!.Workflows.Where(workflow => workflow.Name == "Launch preparation"));
        Assert.Equal(new[] { discoveryStep.Id }, Assert.Single(created.Steps).DependsOn);
        Assert.Contains(created.Id, Assert.Single(saved.Workspaces).WorkflowIds);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Workflow_revision_preserves_completed_evidence_and_requires_approval()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-workflow-revise-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();

        var evidence = new ProjectArtifact
        {
            Name = "Interview notes",
            Location = "local://interviews",
            WorkflowId = Guid.Empty,
        };
        var completed = new ProjectStep
        {
            Name = "Confirm customer problem",
            Description = "Interview customers and record evidence.",
            Status = ProjectWorkStatus.Completed,
            ArtifactIds = [evidence.Id],
        };
        var pending = new ProjectStep
        {
            Name = "Draft the first offer",
            Description = "Use the interview findings.",
            DependsOn = [completed.Id],
        };
        var workflow = new ProjectWorkflow
        {
            Name = "Customer discovery",
            DesiredOutcome = "Evidence for the first offer",
            Status = ProjectWorkStatus.Active,
            Steps = [completed, pending],
        };
        evidence = evidence with { WorkflowId = workflow.Id };
        var workspace = new ProjectWorkspace
        {
            Name = "Offer planning",
            DesiredOutcome = "A validated first offer",
            WorkflowIds = [workflow.Id],
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Find the first customers") with
        {
            Workflows = [workflow],
            Workspaces = [workspace],
            Artifacts = [evidence],
        });

        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, workspace.Id, Guid.NewGuid(), Guid.NewGuid())))
        {
            var registry = new AgentToolRegistry([], [source]);
            var revise = Assert.Single(registry.Tools, tool => tool.Name == "project_revise_workflow");
            Assert.False(revise.IsReadOnly);
            var unsafeCompletedEdit = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                workflow_id = workflow.Id,
                desired_outcome = "A tested offer for local advertisers",
                steps = new object[]
                {
                    new { id = completed.Id, name = "Rewrite completed evidence", description = completed.Description, depends_on_step_ids = Array.Empty<Guid>() },
                    new { id = pending.Id, name = pending.Name, description = pending.Description, depends_on_step_ids = new[] { completed.Id } },
                },
            }))!;
            var refusedCompletedEdit = await revise.InvokeAsync(unsafeCompletedEdit);
            Assert.False(refusedCompletedEdit.Success);
            Assert.Contains("must stay unchanged", refusedCompletedEdit.FailureMessage, StringComparison.Ordinal);
            Assert.Empty(approval.Requests);

            var arguments = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                workflow_id = workflow.Id,
                desired_outcome = "A tested offer for local advertisers",
                steps = new object[]
                {
                    new { id = completed.Id, name = completed.Name, description = completed.Description, depends_on_step_ids = Array.Empty<Guid>() },
                    new { id = pending.Id, name = "Draft a measurable pilot offer", description = "Use the interview findings and define one success measure.", depends_on_step_ids = new[] { completed.Id } },
                    new { id = (Guid?)null, name = "Review the pilot with three businesses", description = "Ask for feedback before spending on a campaign.", depends_on_step_ids = new[] { pending.Id } },
                },
            }))!;

            var result = await revise.InvokeAsync(arguments);
            Assert.True(result.Success, result.FailureMessage);
            Assert.Contains("Completed work and saved artifacts were preserved", result.Output, StringComparison.Ordinal);
            Assert.Single(approval.Requests);
            Assert.Equal("project_revise_workflow", approval.Requests[0].ToolName);
            Assert.Contains("Current plan:", approval.Requests[0].Detail, StringComparison.Ordinal);
            Assert.Contains("Proposed plan:", approval.Requests[0].Detail, StringComparison.Ordinal);
            Assert.Contains("Interview notes", (await new ReadProjectWorkTool(store).InvokeAsync(new JsonObject())).Output, StringComparison.Ordinal);
        }

        var saved = await store.GetAsync(project.Id);
        var revised = Assert.Single(saved!.Workflows);
        Assert.Equal("A tested offer for local advertisers", revised.DesiredOutcome);
        Assert.Equal(3, revised.Steps.Count);
        Assert.Equal(ProjectWorkStatus.Completed, revised.Steps[0].Status);
        Assert.Equal(completed.Name, revised.Steps[0].Name);
        Assert.Equal(new[] { evidence.Id }, revised.Steps[0].ArtifactIds);
        Assert.Equal(ProjectWorkStatus.Planned, revised.Steps[1].Status);
        Assert.Equal(new[] { completed.Id }, revised.Steps[1].DependsOn);
        Assert.Equal(new[] { pending.Id }, revised.Steps[2].DependsOn);
        Assert.Contains(workflow.Id, Assert.Single(saved.Workspaces).WorkflowIds);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Workflow_revision_rejects_hidden_workspace_dependencies_and_cycles_before_approval()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-workflow-revise-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var first = new ProjectStep { Name = "Visible first step" };
        var second = new ProjectStep { Name = "Visible second step" };
        var workflow = new ProjectWorkflow { Name = "Visible plan", DesiredOutcome = "A safe plan", Steps = [first, second] };
        var hidden = new ProjectStep { Name = "Hidden prerequisite" };
        var hiddenWorkflow = new ProjectWorkflow { Name = "Other workspace", Steps = [hidden] };
        var workspace = new ProjectWorkspace
        {
            Name = "Current work",
            DesiredOutcome = "Revise only visible work",
            WorkflowIds = [workflow.Id],
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Reach a goal") with
        {
            Workflows = [workflow, hiddenWorkflow],
            Workspaces = [workspace],
        });
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());

        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, workspace.Id, Guid.NewGuid(), Guid.NewGuid())))
        {
            var revise = Assert.Single(new AgentToolRegistry([], [source]).Tools, tool => tool.Name == "project_revise_workflow");
            var hiddenDependency = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                workflow_id = workflow.Id,
                desired_outcome = "A revised plan",
                steps = new[]
                {
                    new { id = first.Id, name = first.Name, description = first.Description, depends_on_step_ids = new[] { hidden.Id } },
                    new { id = second.Id, name = second.Name, description = second.Description, depends_on_step_ids = Array.Empty<Guid>() },
                },
            }))!;
            var hiddenResult = await revise.InvokeAsync(hiddenDependency);
            Assert.False(hiddenResult.Success);
            Assert.Contains("outside the active Workspace", hiddenResult.FailureMessage, StringComparison.Ordinal);

            var cycle = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                workflow_id = workflow.Id,
                desired_outcome = "A revised plan",
                steps = new[]
                {
                    new { id = first.Id, name = first.Name, description = first.Description, depends_on_step_ids = new[] { second.Id } },
                    new { id = second.Id, name = second.Name, description = second.Description, depends_on_step_ids = new[] { first.Id } },
                },
            }))!;
            var cycleResult = await revise.InvokeAsync(cycle);
            Assert.False(cycleResult.Success);
            Assert.Contains("form a cycle", cycleResult.FailureMessage, StringComparison.Ordinal);
            Assert.Empty(approval.Requests);
        }

        var unchanged = await store.GetAsync(project.Id);
        Assert.Equal("A safe plan", unchanged!.Workflows.Single(item => item.Id == workflow.Id).DesiredOutcome);
        Assert.Equal(1, unchanged.Revision);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Theory]
    [InlineData(ToolApprovalDecision.Denied)]
    [InlineData(ToolApprovalDecision.Unavailable)]
    public async Task Workflow_revision_makes_no_change_without_approval(ToolApprovalDecision decision)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-workflow-revise-denied-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(decision);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var step = new ProjectStep { Name = "Research customers", Description = "Check demand." };
        var workflow = new ProjectWorkflow { Name = "Research", DesiredOutcome = "Evidence", Steps = [step] };
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Find customers") with { Workflows = [workflow] });
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());

        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, null, Guid.NewGuid(), Guid.NewGuid())))
        {
            var revise = Assert.Single(new AgentToolRegistry([], [source]).Tools, tool => tool.Name == "project_revise_workflow");
            var arguments = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                workflow_id = workflow.Id,
                desired_outcome = "A changed outcome",
                steps = new[]
                {
                    new { id = step.Id, name = step.Name, description = "Use verified customer evidence.", depends_on_step_ids = Array.Empty<Guid>() },
                },
            }))!;
            var result = await revise.InvokeAsync(arguments);
            Assert.False(result.Success);
            Assert.Contains("no changes were made", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        }

        var unchanged = await store.GetAsync(project.Id);
        Assert.Equal("Evidence", Assert.Single(unchanged!.Workflows).DesiredOutcome);
        Assert.Equal("Check demand.", Assert.Single(unchanged.Workflows[0].Steps).Description);
        Assert.Equal(decision, Assert.Single(approval.Requests).Decision);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Project_tool_rejects_cross_workflow_dependencies_outside_the_active_workspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-tools-hidden-dependency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();

        var visibleWorkflow = new ProjectWorkflow { Name = "Visible", Steps = [new ProjectStep { Name = "Visible step" }] };
        var hiddenStep = new ProjectStep { Name = "Hidden step" };
        var hiddenWorkflow = new ProjectWorkflow { Name = "Other Workspace", Steps = [hiddenStep] };
        var workspace = new ProjectWorkspace
        {
            Name = "Visible workspace",
            DesiredOutcome = "Create a launch plan from visible work",
            WorkflowIds = [visibleWorkflow.Id],
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Launch a product") with
        {
            Workflows = [visibleWorkflow, hiddenWorkflow],
            Workspaces = [workspace],
        });

        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, workspace.Id, Guid.NewGuid(), Guid.NewGuid())))
        {
            var create = Assert.Single(new AgentToolRegistry([], [source]).Tools,
                tool => tool.Name == "project_create_workflow");
            var arguments = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                name = "New workflow",
                desired_outcome = "A result",
                steps = new[]
                {
                    new
                    {
                        name = "Build on evidence",
                        description = "Use visible project results.",
                        depends_on_indices = Array.Empty<int>(),
                        depends_on_step_ids = new[] { hiddenStep.Id.ToString() },
                    },
                },
            }))!;

            var result = await create.InvokeAsync(arguments);
            Assert.False(result.Success);
            Assert.Contains("not part of this Project or active Workspace", result.FailureMessage, StringComparison.Ordinal);
        }

        var saved = await store.GetAsync(project.Id);
        Assert.Equal(2, saved!.Workflows.Count);
        Assert.Empty(approval.Requests);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Project_workflow_creation_fails_closed_without_approval()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-tools-create-denied-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(ToolApprovalDecision.Unavailable);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Open the business"));
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        using (ProjectToolRunContext.Enter(new ProjectToolRun(project.Id, null, Guid.NewGuid(), Guid.NewGuid())))
        {
            var create = Assert.Single(new AgentToolRegistry([], [source]).Tools,
                tool => tool.Name == "project_create_workflow");
            var result = await create.InvokeAsync(CreateWorkflowArguments());
            Assert.False(result.Success);
            Assert.Contains("no changes were made", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        }

        var saved = await store.GetAsync(project.Id);
        Assert.Empty(saved!.Workflows);
        Assert.Equal(ToolApprovalDecision.Unavailable, Assert.Single(approval.Requests).Decision);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Startup_tools_read_the_current_journey_and_create_approved_workflow_projects()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"startup-work-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var conversationId = Guid.NewGuid();
        var startup = await store.CreateStartupAsync(StartupJourney.Create(
            BusinessJourneyType.NewBusiness, "Neighbourhood Ads", "Start a local advertising business"));
        startup = await store.LinkConversationToStartupAsync(startup.Id, conversationId);
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());

        using (ProjectToolRunContext.Enter(new ProjectToolRun(null, null, conversationId, Guid.NewGuid(), startup.Id)))
        {
            var registry = new AgentToolRegistry([], [source]);
            Assert.DoesNotContain(registry.Tools, tool => tool.Name.StartsWith("project_", StringComparison.Ordinal));
            var read = Assert.Single(registry.Tools, tool => tool.Name == "startup_read_context");
            var create = Assert.Single(registry.Tools, tool => tool.Name == "startup_create_project");
            Assert.True(read.IsReadOnly);
            Assert.False(create.IsReadOnly);

            var context = await read.InvokeAsync(new JsonObject());
            Assert.True(context.Success, context.FailureMessage);
            using (var contextDocument = JsonDocument.Parse(context.Output))
                Assert.Equal((int)BusinessJourneyType.NewBusiness, contextDocument.RootElement.GetProperty("type").GetInt32());
            Assert.Contains("Start a local advertising business", context.Output, StringComparison.Ordinal);

            var arguments = JsonNode.Parse("""
            {
              "name": "First campaign launch",
              "desired_outcome": "A repeatable, measured local campaign",
              "work_type": "Business",
              "workflows": [
                {
                  "name": "Customer discovery",
                  "desired_outcome": "Validated customer segments",
                  "steps": [
                    { "name": "Interview local businesses", "description": "Learn current advertising needs.", "depends_on_indices": [] },
                    { "name": "Summarize customer evidence", "description": "Capture needs and objections.", "depends_on_indices": [0] }
                  ]
                },
                {
                  "name": "Campaign pilot",
                  "desired_outcome": "A measured first campaign",
                  "steps": [
                    { "name": "Draft the pilot offer", "description": "Use evidence from discovery.", "depends_on_indices": [] }
                  ]
                }
              ]
            }
            """)!;
            var result = await create.InvokeAsync(arguments);
            Assert.True(result.Success, result.FailureMessage);
            Assert.Contains("2 workflows and 3 steps", result.Output, StringComparison.Ordinal);
            Assert.Contains("NewBusiness", approval.Requests.Single().Detail, StringComparison.Ordinal);
            Assert.Contains("Summarize customer evidence", approval.Requests.Single().Detail, StringComparison.Ordinal);
        }

        var created = Assert.Single(await store.ListAsync());
        Assert.Equal(startup.Id, created.StartupJourneyId);
        Assert.Equal(conversationId, Assert.Single(created.ConversationIds));
        Assert.Equal(2, created.Workflows.Count);
        Assert.Equal(created.Workflows[0].Steps[0].Id, created.Workflows[0].Steps[1].DependsOn.Single());
        var refreshedStartup = await store.GetStartupAsync(startup.Id);
        Assert.Equal(created.Id, Assert.Single(refreshedStartup!.ProjectIds));
        Assert.Equal(3, refreshedStartup.Revision);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Theory]
    [InlineData(ToolApprovalDecision.Denied)]
    [InlineData(ToolApprovalDecision.Unavailable)]
    public async Task Startup_project_creation_makes_no_change_without_approval(ToolApprovalDecision decision)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"startup-tools-denied-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var approval = new RecordingApprovalService(decision);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddConciergeChat(Path.Combine(directory, "concierge.db"));
        services.AddConciergeTools();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var conversationId = Guid.NewGuid();
        var startup = await store.CreateStartupAsync(StartupJourney.Create(
            BusinessJourneyType.ExistingBusiness, "Local Ads", "Expand the service", "Three clients; no repeatable reporting."));
        startup = await store.LinkConversationToStartupAsync(startup.Id, conversationId);
        var source = Assert.Single(provider.GetServices<IAgentToolSource>());
        using (ProjectToolRunContext.Enter(new ProjectToolRun(null, null, conversationId, Guid.NewGuid(), startup.Id)))
        {
            var create = Assert.Single(new AgentToolRegistry([], [source]).Tools,
                tool => tool.Name == "startup_create_project");
            var arguments = JsonNode.Parse("""
            {
              "name": "Reporting",
              "desired_outcome": "Repeatable reporting",
              "work_type": "Business",
              "workflows": [{
                "name": "Reporting setup",
                "desired_outcome": "A monthly report",
                "steps": [{ "name": "Define measures", "description": "Choose useful indicators.", "depends_on_indices": [] }]
              }]
            }
            """)!;
            var result = await create.InvokeAsync(arguments);
            Assert.False(result.Success);
            Assert.Contains("no changes were made", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await store.ListAsync());
        var unchanged = await store.GetStartupAsync(startup.Id);
        Assert.Empty(unchanged!.ProjectIds);
        Assert.Equal(2, unchanged.Revision);
        Assert.Equal(decision, Assert.Single(approval.Requests).Decision);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private static JsonNode Arguments(Guid workflowId, Guid stepId, string status)
        => JsonNode.Parse(JsonSerializer.Serialize(new
        {
            workflow_id = workflowId,
            step_id = stepId,
            status,
        }))!;

    private static JsonNode CreateWorkflowArguments()
        => JsonNode.Parse("""
        {
          "name": "Validate the offer",
          "desired_outcome": "Evidence that customers want the offer",
          "steps": [
            { "name": "Interview customers", "description": "Ask about the problem and current alternatives.", "depends_on_indices": [] },
            { "name": "Identify first customers", "description": "Find the first likely buyers.", "depends_on_indices": [] },
            { "name": "Review the evidence", "description": "Summarize interview and buyer evidence.", "depends_on_indices": [0, 1] }
          ]
        }
        """)!;

    private sealed class RecordingApprovalService(ToolApprovalDecision decision) : IToolApprovalService
    {
        public List<RecordedRequest> Requests { get; } = [];

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new RecordedRequest(request.ToolName, request.Detail, decision));
            return ValueTask.FromResult(decision);
        }
    }

    private sealed record RecordedRequest(string ToolName, string? Detail, ToolApprovalDecision Decision);
}
