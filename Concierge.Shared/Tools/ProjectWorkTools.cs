using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Butler;
using Concierge.Shared.Planning;

namespace Concierge.Shared.Tools;

/// <summary>Publishes Project tools only while a Project-scoped turn is executing.</summary>
internal sealed class ProjectWorkToolSource(IProjectStore projects, IToolApprovalService approval, IButlerWorkRunApi butler) : IAgentToolSource
{
    private readonly IAgentTool[] _projectTools =
    [
        new ReadProjectWorkTool(projects),
        new ButlerFindJobsTool(butler, projects),
        new CreateProjectWorkflowTool(projects, approval),
        new ReviseProjectWorkflowTool(projects, approval),
        new UpdateProjectStepTool(projects, approval),
    ];

    private readonly IAgentTool[] _startupTools =
    [
        new ReadStartupContextTool(projects),
        new CreateStartupProjectTool(projects, approval),
    ];

    public IReadOnlyList<IAgentTool> Tools
    {
        get
        {
            var context = ProjectToolRunContext.Current;
            if (context is null)
                return [];
            return (context.ProjectId is null ? [] : _projectTools)
                .Concat(context.StartupJourneyId is null ? [] : _startupTools)
                .ToArray();
        }
    }
}

/// <summary>Reads the workflow, decisions, assumptions, and artifacts for the active Project.</summary>
internal sealed class ReadProjectWorkTool(IProjectStore projects) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    public string Name => "project_read_work";
    public string Description => "Read the current Project's workflows, steps, decisions, assumptions, and artifact references.";
    public JsonNode? ArgumentsSchema => Schema.DeepClone();
    public bool IsReadOnly => true;

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var context = ProjectToolRunContext.Current;
        if (context?.ProjectId is not { } projectId)
            return Failure("This tool is available only inside a Project conversation.");

        var project = await projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
            return Failure("The active Project no longer exists. Return to Projects and reopen its Workspace.");

        if (context.WorkspaceId is { } workspaceId)
        {
            var workspace = project.Workspaces.SingleOrDefault(item => item.Id == workspaceId);
            if (workspace is null)
                return Failure("The active Workspace is no longer part of this Project.");

            project = project with
            {
                Workflows = project.Workflows.Where(workflow => workspace.WorkflowIds.Contains(workflow.Id)).ToArray(),
                Decisions = project.Decisions.Where(decision => decision.ConversationId is null
                    || workspace.ConversationIds.Contains(decision.ConversationId.Value)).ToArray(),
                Artifacts = project.Artifacts.Where(artifact => workspace.ArtifactIds.Contains(artifact.Id)
                    || artifact.WorkflowId is { } workflowId && workspace.WorkflowIds.Contains(workflowId)).ToArray(),
            };
        }

        var view = new
        {
            project.Id,
            project.Name,
            project.DesiredOutcome,
            project.Status,
            project.Assumptions,
            Workflows = project.Workflows.Select(workflow => new
            {
                workflow.Id,
                workflow.Name,
                workflow.DesiredOutcome,
                workflow.Status,
                Steps = workflow.Steps.Select(step => new
                {
                    step.Id,
                    step.Name,
                    step.Description,
                    step.Status,
                    step.DependsOn,
                    step.ArtifactIds,
                }),
            }),
            project.Decisions,
            project.Artifacts,
        };
        return new AgentToolResult(true, JsonSerializer.Serialize(view, JsonOptions), null);
    }

    private static AgentToolResult Failure(string message) => new(false, string.Empty, message);
}

/// <summary>Reads the Start-Up journey and the Projects already connected to it.</summary>
internal sealed class ReadStartupContextTool(IProjectStore projects) : IAgentTool
{
    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    public string Name => "startup_read_context";
    public string Description => "Read the active Start-Up journey, including whether it is a new or existing business, the stated goal/current context, and its connected Projects and workflows.";
    public JsonNode? ArgumentsSchema => Schema.DeepClone();
    public bool IsReadOnly => true;

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var context = ProjectToolRunContext.Current;
        if (context?.StartupJourneyId is not { } startupId)
            return Failure("This tool is available only inside a Start-Up Mode conversation.");

        var startup = await projects.GetStartupAsync(startupId, cancellationToken).ConfigureAwait(false);
        if (startup is null || !startup.ConversationIds.Contains(context.ConversationId))
            return Failure("The active Start-Up journey is no longer linked to this conversation.");

        var linkedProjects = await projects.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var view = new
        {
            startup.Id,
            startup.Type,
            startup.Name,
            startup.Goal,
            startup.CurrentContext,
            startup.Status,
            Projects = linkedProjects.Where(project => startup.ProjectIds.Contains(project.Id)).Select(project => new
            {
                project.Id,
                project.Name,
                project.DesiredOutcome,
                project.WorkType,
                project.Status,
                project.Workflows,
            }),
        };
        return new AgentToolResult(true, JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web)), null);
    }

    private static AgentToolResult Failure(string message) => new(false, string.Empty, message);
}

/// <summary>Creates an approved Project and its starting workflows inside the active Start-Up journey.</summary>
internal sealed class CreateStartupProjectTool(IProjectStore projects, IToolApprovalService approval) : IAgentTool
{
    private static readonly JsonObject StepSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string" },
            ["description"] = new JsonObject { ["type"] = "string" },
            ["depends_on_indices"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            },
        },
        ["required"] = new JsonArray("name", "description", "depends_on_indices"),
        ["additionalProperties"] = false,
    };

    private static readonly JsonObject WorkflowSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string" },
            ["desired_outcome"] = new JsonObject { ["type"] = "string" },
            ["steps"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = 20,
                ["items"] = StepSchema.DeepClone(),
            },
        },
        ["required"] = new JsonArray("name", "desired_outcome", "steps"),
        ["additionalProperties"] = false,
    };

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string" },
            ["desired_outcome"] = new JsonObject { ["type"] = "string" },
            ["work_type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Business", "Personal & creative", "General") },
            ["workflows"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = 8,
                ["items"] = WorkflowSchema.DeepClone(),
            },
        },
        ["required"] = new JsonArray("name", "desired_outcome", "work_type", "workflows"),
        ["additionalProperties"] = false,
    };

    public string Name => "startup_create_project";
    public string Description => "Create a Project with its initial workflows inside the active Start-Up journey. Tailor the streams to the recorded new/existing-business context; step dependencies use zero-based indices and may reference only earlier steps in the same workflow. Nothing is saved until the person approves the complete outline.";
    public JsonNode? ArgumentsSchema => Schema.DeepClone();
    public bool IsReadOnly => false;

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var context = ProjectToolRunContext.Current;
        if (context?.StartupJourneyId is not { } startupId)
            return Failure("This tool is available only inside a Start-Up Mode conversation.");
        if (!TryReadString(arguments?["name"], out var rawName)
            || !TryReadString(arguments?["desired_outcome"], out var rawOutcome)
            || !TryReadString(arguments?["work_type"], out var rawWorkType)
            || arguments?["workflows"] is not JsonArray workflowArguments
            || workflowArguments.Count is < 1 or > 8)
            return Failure("Provide a project name, desired outcome, work type, and between 1 and 8 workflows.");
        if (string.IsNullOrWhiteSpace(rawName) || rawName.Length > 120
            || string.IsNullOrWhiteSpace(rawOutcome) || rawOutcome.Length > 500
            || !new[] { "Business", "Personal & creative", "General" }.Contains(rawWorkType, StringComparer.OrdinalIgnoreCase))
            return Failure("Keep the name under 120 characters, the outcome under 500, and choose a supported work type.");

        var workflows = new List<ProjectWorkflow>(workflowArguments.Count);
        for (var workflowIndex = 0; workflowIndex < workflowArguments.Count; workflowIndex++)
        {
            if (workflowArguments[workflowIndex] is not JsonObject workflowArgument
                || !TryReadString(workflowArgument["name"], out var workflowName)
                || !TryReadString(workflowArgument["desired_outcome"], out var workflowOutcome)
                || workflowArgument["steps"] is not JsonArray stepArguments
                || stepArguments.Count is < 1 or > 20)
                return Failure($"Workflow {workflowIndex + 1} needs a name, outcome, and between 1 and 20 steps.");
            if (string.IsNullOrWhiteSpace(workflowName) || workflowName.Length > 120
                || string.IsNullOrWhiteSpace(workflowOutcome) || workflowOutcome.Length > 500)
                return Failure($"Keep workflow {workflowIndex + 1}'s name under 120 characters and outcome under 500 characters.");

            var steps = new List<ProjectStep>(stepArguments.Count);
            for (var stepIndex = 0; stepIndex < stepArguments.Count; stepIndex++)
            {
                if (stepArguments[stepIndex] is not JsonObject stepArgument
                    || !TryReadString(stepArgument["name"], out var stepName)
                    || !TryReadString(stepArgument["description"], out var description)
                    || stepArgument["depends_on_indices"] is not JsonArray dependencyArguments)
                    return Failure($"Workflow {workflowIndex + 1}, step {stepIndex + 1} needs a name, description, and dependency list.");
                if (string.IsNullOrWhiteSpace(stepName) || stepName.Length > 120 || description.Length > 1000)
                    return Failure($"Keep workflow {workflowIndex + 1}, step {stepIndex + 1}'s name under 120 characters and description under 1,000 characters.");

                var dependencies = new List<Guid>();
                foreach (var dependencyArgument in dependencyArguments)
                {
                    if (dependencyArgument is not JsonValue dependencyValue
                        || !dependencyValue.TryGetValue<int>(out var dependencyIndex)
                        || dependencyIndex < 0 || dependencyIndex >= stepIndex)
                        return Failure($"Workflow {workflowIndex + 1}, step {stepIndex + 1} can depend only on earlier steps in that workflow.");
                    dependencies.Add(steps[dependencyIndex].Id);
                }
                steps.Add(new ProjectStep
                {
                    Name = stepName.Trim(),
                    Description = description.Trim(),
                    DependsOn = dependencies.Distinct().ToArray(),
                });
            }

            workflows.Add(new ProjectWorkflow
            {
                Name = workflowName.Trim(),
                DesiredOutcome = workflowOutcome.Trim(),
                Steps = steps,
            });
        }

        var startup = await projects.GetStartupAsync(startupId, cancellationToken).ConfigureAwait(false);
        if (startup is null || !startup.ConversationIds.Contains(context.ConversationId))
            return Failure("The active Start-Up journey is no longer linked to this conversation.");
        if (startup.Status is ProjectStatus.Completed or ProjectStatus.Archived)
            return Failure("This Start-Up journey is closed. Reopen it before creating another Project.");

        var name = rawName.Trim();
        var outcome = rawOutcome.Trim();
        var workType = new[] { "Business", "Personal & creative", "General" }
            .First(value => string.Equals(value, rawWorkType, StringComparison.OrdinalIgnoreCase));
        var outline = string.Join("\n", workflows.Select((workflow, index) =>
            $"{index + 1}. {workflow.Name} — {workflow.DesiredOutcome}\n"
                + string.Join("\n", workflow.Steps.Select((step, stepIndex) =>
                $"   {stepIndex + 1}. {step.Name} — {step.Description}"
                + (step.DependsOn.Count == 0 ? string.Empty : " (after " + string.Join(", ", step.DependsOn.Select(id =>
                    $"{workflow.Steps.ToList().FindIndex(candidate => candidate.Id == id) + 1}")) + ")")))));

        var decision = await approval.RequestAsync(
            new ToolApprovalRequest(
                Name,
                $"Add project '{name}' to the {startup.Type} business journey",
                ConciergeToolRisk.Medium,
                $"Start-Up: {startup.Name}\nBusiness type: {startup.Type}\nGoal: {startup.Goal}\nCurrent context: {startup.CurrentContext}\nProject outcome: {outcome}\nWork type: {workType}\nProposed workflows and steps:\n{outline}"),
            cancellationToken).ConfigureAwait(false);
        if (decision != ToolApprovalDecision.Allowed)
            return Failure(decision == ToolApprovalDecision.Denied
                ? "The Project was not approved, so no changes were made."
                : "The Project could not be approved, so no changes were made.");

        try
        {
            var project = ConciergeProject.Create(name, outcome, workType) with
            {
                ConversationIds = [context.ConversationId],
                Workflows = workflows,
            };
            var created = await projects.CreateProjectForStartupAsync(startupId, project, cancellationToken).ConfigureAwait(false);
            return new AgentToolResult(true,
                $"Created Project '{created.Name}' ({created.Id}) with {created.Workflows.Count} workflows and {created.Workflows.Sum(workflow => workflow.Steps.Count)} steps, linked to Start-Up '{startup.Name}'.",
                null);
        }
        catch (StartupJourneyRevisionConflictException)
        {
            return Failure("The Start-Up journey changed while the Project was being prepared. Refresh it and review its current work before trying again.");
        }
        catch (KeyNotFoundException)
        {
            return Failure("The Start-Up journey no longer exists, so the Project was not created.");
        }
        catch (ArgumentException exception)
        {
            return Failure($"The Project was rejected: {exception.Message}");
        }
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var parsed))
        {
            value = parsed;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static AgentToolResult Failure(string message) => new(false, string.Empty, message);
}

/// <summary>Adds an editable, dependency-aware workflow to the active Project after approval.</summary>
internal sealed class CreateProjectWorkflowTool(IProjectStore projects, IToolApprovalService approval) : IAgentTool
{
    private static readonly JsonObject StepSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string" },
            ["description"] = new JsonObject { ["type"] = "string" },
            ["depends_on_indices"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            },
            ["depends_on_step_ids"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            },
        },
        ["required"] = new JsonArray("name", "description", "depends_on_indices"),
        ["additionalProperties"] = false,
    };

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string" },
            ["desired_outcome"] = new JsonObject { ["type"] = "string" },
            ["steps"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = 40,
                ["items"] = StepSchema.DeepClone(),
            },
        },
        ["required"] = new JsonArray("name", "desired_outcome", "steps"),
        ["additionalProperties"] = false,
    };

    public string Name => "project_create_workflow";
    public string Description => "Create a proposed workflow in the active Project. Keep steps specific and useful; dependency indices are zero-based and may reference only earlier steps in this workflow. For dependencies on steps in existing workflows, use their step IDs returned by project_read_work. Nothing is saved until the person approves.";
    public JsonNode? ArgumentsSchema => Schema.DeepClone();
    public bool IsReadOnly => false;

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var context = ProjectToolRunContext.Current;
        if (context?.ProjectId is not { } projectId)
            return Failure("This tool is available only inside a Project conversation.");

        if (!TryReadString(arguments?["name"], out var rawName)
            || !TryReadString(arguments?["desired_outcome"], out var rawOutcome)
            || arguments?["steps"] is not JsonArray stepArguments
            || stepArguments.Count is < 1 or > 40)
            return Failure("Provide a workflow name, desired outcome, and between 1 and 40 steps.");
        if (string.IsNullOrWhiteSpace(rawName) || rawName.Length > 120
            || string.IsNullOrWhiteSpace(rawOutcome) || rawOutcome.Length > 500)
            return Failure("Keep the workflow name under 120 characters and the desired outcome under 500 characters.");

        var steps = new List<ProjectStep>(stepArguments.Count);
        var existingDependencyIds = new List<Guid>[stepArguments.Count];
        for (var index = 0; index < stepArguments.Count; index++)
        {
            if (stepArguments[index] is not JsonObject stepArgument
                || !TryReadString(stepArgument["name"], out var rawStepName)
                || !TryReadString(stepArgument["description"], out var description)
                || stepArgument["depends_on_indices"] is not JsonArray dependencyIndices)
                return Failure($"Step {index + 1} needs a name, description, and dependency list.");
            if (string.IsNullOrWhiteSpace(rawStepName) || rawStepName.Length > 120 || description.Length > 1000)
                return Failure($"Keep step {index + 1}'s name under 120 characters and description under 1,000 characters.");

            var dependencies = new List<Guid>();
            foreach (var dependencyIndexNode in dependencyIndices)
            {
                if (dependencyIndexNode is not JsonValue dependencyIndexValue
                    || !dependencyIndexValue.TryGetValue<int>(out var dependencyIndex)
                    || dependencyIndex < 0 || dependencyIndex >= index)
                    return Failure($"Step {index + 1} can depend only on earlier step numbers, using zero-based indices.");
                var dependencyId = steps[dependencyIndex].Id;
                if (!dependencies.Contains(dependencyId))
                    dependencies.Add(dependencyId);
            }

            var externalDependencyIdsForStep = new List<Guid>();
            if (stepArgument["depends_on_step_ids"] is JsonArray externalDependencies)
            {
                foreach (var dependencyIdNode in externalDependencies)
                {
                    if (dependencyIdNode is not JsonValue dependencyIdValue
                        || !dependencyIdValue.TryGetValue<string>(out var dependencyIdText)
                        || !Guid.TryParse(dependencyIdText, out var dependencyId))
                        return Failure($"Step {index + 1} has an invalid existing step ID in its dependencies.");
                    if (!externalDependencyIdsForStep.Contains(dependencyId))
                        externalDependencyIdsForStep.Add(dependencyId);
                }
            }

            steps.Add(new ProjectStep
            {
                Name = rawStepName.Trim(),
                Description = description.Trim(),
                DependsOn = dependencies,
            });
            existingDependencyIds[index] = externalDependencyIdsForStep;
        }

        try
        {
            var project = await projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
                return Failure("The active Project no longer exists. Return to Projects and reopen its Workspace.");
            if (project.Status is ProjectStatus.Completed or ProjectStatus.Archived)
                return Failure("This Project is closed. Reopen it from Projects before adding a workflow.");

            ProjectWorkspace? workspace = null;
            if (context.WorkspaceId is { } workspaceId)
            {
                workspace = project.Workspaces.SingleOrDefault(item => item.Id == workspaceId);
                if (workspace is null)
                    return Failure("The active Workspace is no longer part of this Project.");
            }

            // Existing-workflow dependencies must be visible in the current Project/Workspace
            // context. This keeps a Workspace-scoped plan from depending on work it cannot read.
            var existingWorkflows = workspace is null
                ? project.Workflows
                : project.Workflows.Where(item => workspace.WorkflowIds.Contains(item.Id)).ToArray();
            var existingStepsById = existingWorkflows.SelectMany(item => item.Steps)
                .ToDictionary(item => item.Id);
            for (var index = 0; index < steps.Count; index++)
            {
                foreach (var dependencyId in existingDependencyIds[index])
                {
                    if (!existingStepsById.ContainsKey(dependencyId))
                        return Failure($"Step {index + 1} depends on an existing step that is not part of this Project or active Workspace.");
                    if (!steps[index].DependsOn.Contains(dependencyId))
                        steps[index] = steps[index] with
                        {
                            DependsOn = steps[index].DependsOn.Append(dependencyId).ToArray(),
                        };
                }
            }

            var name = rawName.Trim();
            var outcome = rawOutcome.Trim();
            var workflow = new ProjectWorkflow { Name = name, DesiredOutcome = outcome, Steps = steps };
            var stepOutline = string.Join("\n", steps.Select((step, index) =>
            {
                var dependencies = step.DependsOn.Count == 0
                    ? "no prerequisites"
                    : "after " + string.Join(", ", step.DependsOn.Select(id =>
                    {
                        var localIndex = steps.FindIndex(candidate => candidate.Id == id);
                        var dependencyName = localIndex >= 0
                            ? $"{localIndex + 1}. {steps[localIndex].Name}"
                            : existingStepsById[id].Name;
                        return dependencyName;
                    }));
                return $"{index + 1}. {step.Name} — {step.Description} ({dependencies})";
            }));

            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name,
                    $"Add workflow '{name}' with {steps.Count} steps",
                    ConciergeToolRisk.Medium,
                    $"Project: {project.Name}\nDesired outcome: {outcome}\nSteps:\n{stepOutline}"),
                cancellationToken).ConfigureAwait(false);
            if (decision != ToolApprovalDecision.Allowed)
                return Failure(decision == ToolApprovalDecision.Denied
                    ? "The workflow was not approved, so no changes were made."
                    : "The workflow could not be approved, so no changes were made.");

            var updated = project with
            {
                Status = project.Status == ProjectStatus.Planning ? ProjectStatus.Active : project.Status,
                Workflows = project.Workflows.Append(workflow).ToArray(),
                Workspaces = workspace is null
                    ? project.Workspaces
                    : project.Workspaces.Select(item => item.Id == workspace.Id
                        ? item with { WorkflowIds = item.WorkflowIds.Append(workflow.Id).ToArray() }
                        : item).ToArray(),
            };
            await projects.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

            return new AgentToolResult(true,
                $"Added workflow '{workflow.Name}' ({workflow.Id}) with {steps.Count} planned steps. "
                + string.Join(" ", steps.Select((step, index) => $"Step {index + 1} ID: {step.Id}.")),
                null);
        }
        catch (ProjectRevisionConflictException)
        {
            return Failure("The Project changed while this workflow was being prepared. Refresh the Project and review its current state before trying again.");
        }
        catch (ArgumentException exception)
        {
            return Failure($"The workflow was rejected: {exception.Message}");
        }
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var parsed))
        {
            value = parsed;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static AgentToolResult Failure(string message) => new(false, string.Empty, message);
}

/// <summary>Changes one step in the active Project after action-mode approval.</summary>
internal sealed class UpdateProjectStepTool(IProjectStore projects, IToolApprovalService approval) : IAgentTool
{
    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["workflow_id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["step_id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["status"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Enum.GetNames<ProjectWorkStatus>()
                    .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            },
        },
        ["required"] = new JsonArray("workflow_id", "step_id", "status"),
        ["additionalProperties"] = false,
    };

    public string Name => "project_update_step";
    public string Description => "Update a workflow step in the active Project. Choose a step state based only on verified work, and do not mark work complete without evidence.";
    public JsonNode? ArgumentsSchema => Schema.DeepClone();
    public bool IsReadOnly => false;

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var context = ProjectToolRunContext.Current;
        if (context?.ProjectId is not { } projectId)
            return Failure("This tool is available only inside a Project conversation.");
        if (!Guid.TryParse(arguments?["workflow_id"]?.GetValue<string>(), out var workflowId)
            || !Guid.TryParse(arguments?["step_id"]?.GetValue<string>(), out var stepId)
            || !Enum.TryParse(arguments?["status"]?.GetValue<string>(), ignoreCase: true, out ProjectWorkStatus status)
            || !Enum.IsDefined(status))
            return Failure("Provide a valid workflow ID, step ID, and step status.");

        try
        {
            var project = await projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
                return Failure("The active Project no longer exists. Return to Projects and reopen its Workspace.");
            if (project.Status is ProjectStatus.Completed or ProjectStatus.Archived)
                return Failure("This Project is closed. Reopen it from Projects before changing its workflow.");

            if (context.WorkspaceId is { } workspaceId
                && !project.Workspaces.Any(workspace => workspace.Id == workspaceId && workspace.WorkflowIds.Contains(workflowId)))
                return Failure("That workflow is not part of the active Workspace.");

            var workflow = project.Workflows.SingleOrDefault(item => item.Id == workflowId);
            var step = workflow?.Steps.SingleOrDefault(item => item.Id == stepId);
            if (workflow is null || step is null)
                return Failure("That workflow step does not belong to the active Project.");

            var allSteps = project.Workflows.SelectMany(item => item.Steps).ToDictionary(item => item.Id);
            var unmet = step.DependsOn
                .Where(id => !allSteps.TryGetValue(id, out var dependency)
                    || dependency.Status is not (ProjectWorkStatus.Completed or ProjectWorkStatus.Skipped))
                .ToArray();
            if (status is ProjectWorkStatus.Active or ProjectWorkStatus.Review or ProjectWorkStatus.Completed
                && unmet.Length > 0)
                return Failure("This step is blocked until its dependencies are completed or skipped.");

            if (step.Status is ProjectWorkStatus.Completed or ProjectWorkStatus.Skipped
                && status is not (ProjectWorkStatus.Completed or ProjectWorkStatus.Skipped)
                && project.Workflows.SelectMany(item => item.Steps)
                    .Any(candidate => candidate.DependsOn.Contains(stepId)
                        && candidate.Status is ProjectWorkStatus.Active or ProjectWorkStatus.Review
                            or ProjectWorkStatus.Completed or ProjectWorkStatus.Skipped))
                return Failure("A later dependent step has already progressed. Reopen those dependent steps first.");

            if (step.Status != status)
            {
                var decision = await approval.RequestAsync(
                    new ToolApprovalRequest(
                        Name,
                        $"Update '{step.Name}' to {status}",
                        ConciergeToolRisk.Medium,
                        $"Project: {project.Name}\nWorkflow: {workflow.Name}\nStep: {step.Name}\nCurrent status: {step.Status}\nRequested status: {status}"),
                    cancellationToken).ConfigureAwait(false);
                if (decision != ToolApprovalDecision.Allowed)
                    return Failure(decision == ToolApprovalDecision.Denied
                        ? "The Project update was not approved, so no change was made."
                        : "The Project update could not be approved, so no change was made.");
            }

            var updatedWorkflow = workflow with
            {
                Steps = workflow.Steps.Select(item => item.Id == stepId ? item with { Status = status } : item).ToArray(),
            };
            updatedWorkflow = updatedWorkflow with { Status = RollUp(updatedWorkflow.Steps) };
            var updated = project with
            {
                Workflows = project.Workflows.Select(item => item.Id == workflowId ? updatedWorkflow : item).ToArray(),
            };
            await projects.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

            return new AgentToolResult(true,
                $"Updated '{step.Name}' to {status}. Workflow '{workflow.Name}' is now {updatedWorkflow.Status}.", null);
        }
        catch (ProjectRevisionConflictException)
        {
            return Failure("The Project changed while this update was being prepared. Refresh the Project and review its current step state before trying again.");
        }
        catch (ArgumentException exception)
        {
            return Failure($"The Project update was rejected: {exception.Message}");
        }
    }

    private static ProjectWorkStatus RollUp(IReadOnlyList<ProjectStep> steps)
    {
        if (steps.Count > 0 && steps.All(step => step.Status is ProjectWorkStatus.Completed or ProjectWorkStatus.Skipped))
            return ProjectWorkStatus.Completed;
        if (steps.Any(step => step.Status == ProjectWorkStatus.Blocked))
            return ProjectWorkStatus.Blocked;
        if (steps.Any(step => step.Status == ProjectWorkStatus.AwaitingPerson))
            return ProjectWorkStatus.AwaitingPerson;
        if (steps.Any(step => step.Status == ProjectWorkStatus.Review))
            return ProjectWorkStatus.Review;
        if (steps.Any(step => step.Status == ProjectWorkStatus.Active))
            return ProjectWorkStatus.Active;
        return ProjectWorkStatus.Planned;
    }

    private static AgentToolResult Failure(string message) => new(false, string.Empty, message);
}

/// <summary>Revises a Project workflow while protecting completed work and artifacts.</summary>
internal sealed class ReviseProjectWorkflowTool(IProjectStore projects, IToolApprovalService approval) : IAgentTool
{
    private static readonly JsonObject StepSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["name"] = new JsonObject { ["type"] = "string" },
            ["description"] = new JsonObject { ["type"] = "string" },
            ["depends_on_step_ids"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            },
        },
        ["required"] = new JsonArray("name", "description", "depends_on_step_ids"),
        ["additionalProperties"] = false,
    };

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["workflow_id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["desired_outcome"] = new JsonObject { ["type"] = "string" },
            ["steps"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = 40,
                ["items"] = StepSchema.DeepClone(),
            },
        },
        ["required"] = new JsonArray("workflow_id", "desired_outcome", "steps"),
        ["additionalProperties"] = false,
    };

    public string Name => "project_revise_workflow";
    public string Description => "Revise the active Project's workflow to reflect a changed goal or new information. Keep completed steps and their evidence unchanged. Reuse existing step IDs to revise pending work, omit only unstarted steps that are no longer needed, and omit id for new steps. Dependencies use stable step IDs returned by project_read_work. Nothing is saved until the person approves the before-and-after plan.";
    public JsonNode? ArgumentsSchema => Schema.DeepClone();
    public bool IsReadOnly => false;

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var context = ProjectToolRunContext.Current;
        if (context?.ProjectId is not { } projectId)
            return Failure("This tool is available only inside a Project conversation.");
        if (!Guid.TryParse(arguments?["workflow_id"]?.GetValue<string>(), out var workflowId)
            || !TryReadString(arguments?["desired_outcome"], out var rawOutcome)
            || string.IsNullOrWhiteSpace(rawOutcome) || rawOutcome.Length > 500
            || arguments?["steps"] is not JsonArray stepArguments
            || stepArguments.Count is < 1 or > 40)
            return Failure("Provide a valid workflow ID, a desired outcome under 500 characters, and between 1 and 40 revised steps.");

        try
        {
            var project = await projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
                return Failure("The active Project no longer exists. Return to Projects and reopen its Workspace.");
            if (project.Status is ProjectStatus.Completed or ProjectStatus.Archived)
                return Failure("This Project is closed. Reopen it from Projects before changing its workflow.");

            ProjectWorkspace? workspace = null;
            if (context.WorkspaceId is { } workspaceId)
            {
                workspace = project.Workspaces.SingleOrDefault(item => item.Id == workspaceId);
                if (workspace is null || !workspace.WorkflowIds.Contains(workflowId))
                    return Failure("That workflow is not part of the active Workspace.");
            }

            var workflow = project.Workflows.SingleOrDefault(item => item.Id == workflowId);
            if (workflow is null)
                return Failure("That workflow does not belong to the active Project.");
            if (workflow.Status is ProjectWorkStatus.Completed or ProjectWorkStatus.Skipped)
                return Failure("This workflow is complete. Create a follow-up workflow instead of rewriting its finished record.");

            var existingById = workflow.Steps.ToDictionary(step => step.Id);
            var projectSteps = project.Workflows.SelectMany(item => item.Steps).ToDictionary(step => step.Id);
            var proposed = new List<ProjectStep>(stepArguments.Count);
            var proposedIds = new HashSet<Guid>();
            for (var index = 0; index < stepArguments.Count; index++)
            {
                if (stepArguments[index] is not JsonObject stepArgument
                    || !TryReadString(stepArgument["name"], out var name)
                    || !TryReadString(stepArgument["description"], out var description)
                    || stepArgument["depends_on_step_ids"] is not JsonArray dependencyArguments
                    || string.IsNullOrWhiteSpace(name) || name.Length > 120 || description.Length > 1000)
                    return Failure($"Revised step {index + 1} needs a name under 120 characters, a description under 1,000, and a dependency list.");

                var id = Guid.NewGuid();
                if (stepArgument["id"] is { } idArgument)
                {
                    if (!TryReadString(idArgument, out var stepIdText) || !Guid.TryParse(stepIdText, out id))
                        return Failure($"Revised step {index + 1} has an invalid step ID.");
                    if (!existingById.ContainsKey(id))
                        return Failure("A reused step ID must belong to this workflow. Read the active Project before revising it.");
                }
                if (!proposedIds.Add(id))
                    return Failure("Each revised step must have a unique ID.");

                var dependencies = new List<Guid>(dependencyArguments.Count);
                foreach (var dependencyArgument in dependencyArguments)
                {
                    if (dependencyArgument is not JsonValue value
                        || !value.TryGetValue<string>(out var dependencyText)
                        || !Guid.TryParse(dependencyText, out var dependencyId)
                        || dependencies.Contains(dependencyId))
                        return Failure($"Revised step {index + 1} has an invalid or repeated dependency ID.");
                    dependencies.Add(dependencyId);
                }

                if (existingById.TryGetValue(id, out var existing)
                    && existing.Status is ProjectWorkStatus.Completed or ProjectWorkStatus.Skipped
                    && (!string.Equals(existing.Name, name.Trim(), StringComparison.Ordinal)
                        || !string.Equals(existing.Description, description.Trim(), StringComparison.Ordinal)
                        || !existing.DependsOn.ToHashSet().SetEquals(dependencies)))
                    return Failure($"Completed step '{existing.Name}' and its evidence must stay unchanged in a revised plan.");

                proposed.Add(existingById.TryGetValue(id, out existing)
                    ? existing with { Name = name.Trim(), Description = description.Trim(), DependsOn = dependencies }
                    : new ProjectStep { Id = id, Name = name.Trim(), Description = description.Trim(), DependsOn = dependencies });
            }

            var removed = workflow.Steps.Where(step => !proposedIds.Contains(step.Id)).ToArray();
            foreach (var step in removed)
            {
                if (step.Status is not (ProjectWorkStatus.Planned or ProjectWorkStatus.Blocked))
                    return Failure($"'{step.Name}' has started or needs review. Keep it in the revised plan and correct its next step instead of removing it.");
                if (step.ArtifactIds.Count > 0)
                    return Failure($"'{step.Name}' has saved artifacts. Keep it in the revised plan so its work remains attached.");
                if (projectSteps.Values.Any(candidate => candidate.Id != step.Id && candidate.DependsOn.Contains(step.Id)))
                    return Failure($"Another step depends on '{step.Name}'. Repoint those dependencies before removing it.");
            }

            var allowedWorkflowIds = workspace is null
                ? project.Workflows.Select(item => item.Id).ToHashSet()
                : workspace.WorkflowIds.ToHashSet();
            var availableSteps = project.Workflows
                .Where(item => item.Id != workflowId && allowedWorkflowIds.Contains(item.Id))
                .SelectMany(item => item.Steps)
                .Select(step => step.Id)
                .Concat(proposedIds)
                .ToHashSet();
            foreach (var step in proposed)
            {
                if (step.DependsOn.Any(id => id == step.Id || !availableSteps.Contains(id)))
                    return Failure($"'{step.Name}' depends on a missing step or one outside the active Workspace.");
            }

            var outcome = rawOutcome.Trim();
            var revised = workflow with
            {
                DesiredOutcome = outcome,
                Steps = proposed,
                Status = RollUp(proposed),
            };
            var resultingSteps = project.Workflows
                .SelectMany(item => item.Id == workflowId ? revised.Steps : item.Steps)
                .ToDictionary(step => step.Id);
            if (ContainsCycle(resultingSteps))
                return Failure("The revised dependencies form a cycle. Adjust the prerequisites before asking for approval.");

            if (string.Equals(revised.DesiredOutcome, workflow.DesiredOutcome, StringComparison.Ordinal)
                && SameSteps(revised.Steps, workflow.Steps)
                && revised.Status == workflow.Status)
                return new AgentToolResult(true, $"Workflow '{workflow.Name}' already matches that plan; nothing changed.", null);

            var dependencyNames = project.Workflows.SelectMany(item => item.Steps)
                .ToDictionary(step => step.Id, step => step.Name);
            foreach (var step in proposed)
                dependencyNames[step.Id] = step.Name;
            var before = Format(workflow.Steps, dependencyNames);
            var after = Format(revised.Steps, dependencyNames);
            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name,
                    $"Revise workflow '{workflow.Name}'",
                    ConciergeToolRisk.Medium,
                    $"Project: {project.Name}\nWorkspace: {workspace?.Name ?? "Project"}\nWorkflow: {workflow.Name}\nPrevious outcome: {workflow.DesiredOutcome}\nNew outcome: {outcome}\nRemoved unstarted steps: {(removed.Length == 0 ? "none" : string.Join(", ", removed.Select(step => step.Name)))}\n\nCurrent plan:\n{before}\n\nProposed plan:\n{after}"),
                cancellationToken).ConfigureAwait(false);
            if (decision != ToolApprovalDecision.Allowed)
                return Failure(decision == ToolApprovalDecision.Denied
                    ? "The workflow revision was not approved, so no changes were made."
                    : "The workflow revision could not be approved, so no changes were made.");

            var updated = project with
            {
                Workflows = project.Workflows.Select(item => item.Id == workflowId ? revised : item).ToArray(),
            };
            await projects.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return new AgentToolResult(true, $"Revised workflow '{workflow.Name}' with {proposed.Count} steps. Completed work and saved artifacts were preserved.", null);
        }
        catch (ProjectRevisionConflictException)
        {
            return Failure("The Project changed while this revision was being prepared. Refresh the Project and review its current plan before trying again.");
        }
        catch (ArgumentException exception)
        {
            return Failure($"The workflow revision was rejected: {exception.Message}");
        }
    }

    private static ProjectWorkStatus RollUp(IReadOnlyList<ProjectStep> steps)
    {
        if (steps.Count > 0 && steps.All(step => step.Status is ProjectWorkStatus.Completed or ProjectWorkStatus.Skipped))
            return ProjectWorkStatus.Completed;
        if (steps.Any(step => step.Status == ProjectWorkStatus.Blocked)) return ProjectWorkStatus.Blocked;
        if (steps.Any(step => step.Status == ProjectWorkStatus.AwaitingPerson)) return ProjectWorkStatus.AwaitingPerson;
        if (steps.Any(step => step.Status == ProjectWorkStatus.Review)) return ProjectWorkStatus.Review;
        if (steps.Any(step => step.Status == ProjectWorkStatus.Active)) return ProjectWorkStatus.Active;
        return ProjectWorkStatus.Planned;
    }

    private static bool SameSteps(IReadOnlyList<ProjectStep> left, IReadOnlyList<ProjectStep> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id
            && string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
            && string.Equals(pair.First.Description, pair.Second.Description, StringComparison.Ordinal)
            && pair.First.Status == pair.Second.Status
            && pair.First.DependsOn.ToHashSet().SetEquals(pair.Second.DependsOn)
            && pair.First.ArtifactIds.ToHashSet().SetEquals(pair.Second.ArtifactIds));

    private static bool ContainsCycle(IReadOnlyDictionary<Guid, ProjectStep> steps)
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        bool Visit(Guid id)
        {
            if (visited.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            foreach (var dependency in steps[id].DependsOn)
            {
                if (steps.ContainsKey(dependency) && Visit(dependency)) return true;
            }
            visiting.Remove(id);
            visited.Add(id);
            return false;
        }
        return steps.Keys.Any(Visit);
    }

    private static string Format(IReadOnlyList<ProjectStep> steps, IReadOnlyDictionary<Guid, string> dependencyNames)
        => string.Join("\n", steps.Select((step, index) =>
        {
            var line = $"{index + 1}. {step.Name} — {step.Description} [{step.Status}]";
            return step.DependsOn.Count == 0
                ? line
                : $"{line} (depends on {string.Join(", ", step.DependsOn.Select(id => dependencyNames.GetValueOrDefault(id, id.ToString())))})";
        }));

    private static bool TryReadString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var parsed))
        {
            value = parsed;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static AgentToolResult Failure(string message) => new(false, string.Empty, message);
}
