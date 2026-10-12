namespace Concierge.Shared.Planning;

public enum ProjectStarterGroup
{
    Home,
    Work,
}

/// <summary>A practical example that seeds one editable workflow in a new Project.</summary>
public sealed record ProjectStarterTemplate(
    string Id,
    string Group,
    string Name,
    string Outcome,
    string WorkType,
    string WorkflowName,
    string WorkflowOutcome,
    string AssistantFocus,
    IReadOnlyList<(string Name, string Description)> Steps)
{
    public ConciergeProject CreateProject()
    {
        var workflow = new ProjectWorkflow
        {
            Name = WorkflowName,
            DesiredOutcome = WorkflowOutcome,
            Steps = Steps.Select(step => new ProjectStep { Name = step.Name, Description = step.Description }).ToList(),
        };
        return ConciergeProject.Create(Name, Outcome, WorkType) with
        {
            StarterAgentFocus = AssistantFocus,
            Workflows = [workflow],
        };
    }
}

/// <summary>Default examples for first-time Project users. Templates seed editable examples, not live agents.</summary>
public static class ProjectStarterTemplates
{
    public static IReadOnlyList<ProjectStarterTemplate> All { get; } =
    [
        new("house-chores", "Home", "House Chores", "Keep recurring chores clear, fair, and completed.", "Personal & creative",
            "Weekly home routine", "A manageable schedule with clear ownership and completion",
            "Allowance or pay planning can be included or left out; no payment is made by this example.",
            [("List the recurring chores", "Add the chores and how often they need doing."), ("Choose who handles each chore", "Assign people and agree how the workload stays fair."), ("Set reminders and review completion", "Choose a reminder rhythm and a weekly check-in.")]),
        new("home-executive", "Home", "Home Executive", "Coordinate household schedules, supplies, and routines.", "Personal & creative",
            "Household week", "A shared view of upcoming routines and household needs",
            "An online-shopping assistant can compare options and prepare a basket for your review; it does not place orders.",
            [("Collect household dates", "Add appointments, school dates, and recurring routines."), ("List supplies to replenish", "Record what is running low and any preferred brands."), ("Review a shopping list", "Compare options and prepare a list for your approval.")]),
        new("personal-mechanic", "Home", "Personal Mechanic", "Keep vehicle maintenance, repair history, and tuning plans together.", "Personal & creative",
            "Vehicle care plan", "A reliable maintenance record and reviewed next steps",
            "A tuning assistant can organize goals and research options; it does not change a vehicle or replace a qualified mechanic.",
            [("Record the vehicle baseline", "Add model, mileage, known issues, and current modifications."), ("Collect service history", "Record dates, parts, and work already completed."), ("Plan the next inspection", "List questions and checks for a qualified mechanic.")]),
        new("off-grid-living", "Home", "Off-Grid Living", "Coordinate borehole, solar, smallholding, and household systems.", "Personal & creative",
            "Off-grid systems overview", "A clear inventory with safe maintenance and improvement priorities",
            "An automation assistant can map dependencies and propose coordination; physical systems remain under human control.",
            [("Map the systems", "List water, power, farming, storage, and communications equipment."), ("Record maintenance needs", "Add service intervals, owners, and safety notes."), ("Choose one improvement", "Compare a practical next step against available time and budget.")]),
        new("front-desk", "Work", "Front Desk", "Keep incoming enquiries, messages, and follow-ups organized.", "Business",
            "Enquiry handling", "Every enquiry has an owner, next step, and follow-up date",
            "An assistant's-assistant can summarize new requests and surface overdue follow-ups for review.",
            [("Choose intake channels", "List where enquiries arrive and what information to capture."), ("Set routing and ownership", "Decide who handles each request type."), ("Review follow-ups", "Set response expectations and a daily follow-up check.")]),
        new("office-management", "Work", "Office Management", "Track office supplies, equipment, and recurring needs.", "Business",
            "Office operations", "A dependable view of supplies, equipment, and upcoming needs",
            "A supply-management assistant can flag low stock and prepare reorder suggestions for approval.",
            [("Inventory supplies and equipment", "Record quantities, locations, and responsible people."), ("Set reorder points", "Agree minimum quantities and preferred suppliers."), ("Prepare a purchase review", "Collect needs and compare options before ordering.")]),
        new("recruitment", "Work", "Recruitment", "Coordinate recruiter and candidate flows with appropriate updates.", "Business",
            "Recruitment flow", "Candidates and recruiters know the next step and timing",
            "A recruiter assistant can organize stages and draft role-appropriate alerts; messages require review before sending.",
            [("Define the role and stages", "Record requirements, stages, and decision owners."), ("Map candidate updates", "Choose what candidates should hear at each stage."), ("Set recruiter alerts", "Identify deadlines and internal follow-up reminders.")]),
        new("task-automation", "Work", "Task Automation", "Map repeatable work and connect supported third-party services.", "Business",
            "Automation discovery", "A reviewed automation plan with clear triggers and safeguards",
            "Third-party integration is the focus; connected actions must be explicitly configured and approved.",
            [("Choose a repetitive task", "Describe the trigger, inputs, and expected result."), ("Map current steps and tools", "List systems, handoffs, and failure cases."), ("Review a safe automation", "Define permissions, tests, and a manual fallback.")]),
        new("development", "Work", "Development", "Organize software maintenance, fixes, and product improvements.", "Business",
            "Software maintenance", "A prioritized, reviewable path from issue to verified change",
            "Wolverine by Circle AI is the intended development assistant; availability depends on the connected Circle AI runtime.",
            [("Describe the product and its users", "Capture purpose, stack, and the people affected."), ("Collect maintenance work", "Add defects, dependency updates, and small improvements."), ("Plan a reviewed release", "Order changes, verification, and release notes.")]),
    ];

    public static IReadOnlyList<ProjectStarterTemplate> Home => All.Where(template => template.Group == "Home").ToList();
    public static IReadOnlyList<ProjectStarterTemplate> Work => All.Where(template => template.Group == "Work").ToList();
}
