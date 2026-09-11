namespace Concierge.Shared;

public interface IBeyondClaudeService
{
    BeyondClaudeSnapshot GetSnapshot();

    IReadOnlyList<BeyondCapability> GetCapabilities();
}

public sealed class BeyondClaudeService : IBeyondClaudeService
{
    private static readonly SafeResumePolicy ResumePolicy = new(
        RequiresUserApproval: true,
        KeepsOriginalSandbox: true,
        BlocksPermissionBypass: true,
        StopsWhenContextChanges: true,
        Summary: "Concierge can pick work back up, but it must keep the same safety rules and ask before doing anything risky.");

    private static readonly IReadOnlyList<BeyondCapability> Capabilities =
    [
        new(
            "memory-palace",
            "Memory Palace",
            "Remember useful work",
            "mempalace",
            "Keep local, searchable memory rooms for decisions, evidence, notes, and reusable context.",
            "Users stop repeating themselves, and the app can explain why a decision was made.",
            "Local first, user-owned, exportable, and never a hidden cloud dependency.",
            BeyondState.NotBuilt),
        new(
            "workforce-mode",
            "Workforce Mode",
            "Bring in the right helpers",
            "loki-mode and eigent",
            "Route work to small specialist teams for product, engineering, finance, growth, support, and compliance.",
            "Concierge becomes useful to a whole business, not only developers.",
            "Every helper has scoped tools, visible work, and approval gates before risky actions.",
            BeyondState.NotBuilt),
        new(
            "simulation-room",
            "Simulation Room",
            "Try the decision before spending money",
            "MiroFish",
            "Compare likely outcomes before hiring, launching, pricing, buying tools, or changing a process.",
            "One-person teams can think through consequences without needing a strategy department.",
            "Simulations are labelled as forecasts, not facts, and must show the evidence they used.",
            BeyondState.NotBuilt),
        new(
            "infrastructure-room",
            "Infrastructure Room",
            "Manage servers without fear",
            "LazySSH",
            "Track environments, SSH-style access, deployment steps, and operational checks in a safety-first way.",
            "Small teams get infrastructure confidence without exposing raw credentials everywhere.",
            "No secret display, no uncontrolled shell access, and owner approval for remote changes.",
            BeyondState.NotBuilt),
        new(
            "diagram-evidence",
            "Diagram Evidence",
            "Show the plan clearly",
            "mermaid",
            "Generate simple diagrams for systems, business processes, release evidence, and support handovers.",
            "People understand the work faster when the app can show flows instead of only text.",
            "Diagrams are generated from reviewable project facts and can be exported as evidence.",
            BeyondState.InTheApp,
            "The Diagrams room, which draws with a renderer carried in the app."),
        new(
            "safe-auto-resume",
            "Safe Auto-Resume",
            "Continue without taking over",
            "claude-auto-resume",
            "Resume interrupted work while preserving approvals, sandbox boundaries, and user control.",
            "Long work should survive disconnects without holding the machine hostage.",
            "No permission bypass, no silent destructive action, and stop if the workspace changed.",
            BeyondState.InTheApp,
            "The thread, unsent text and switched-on skills survive a restart."),
        new(
            "lightweight-mode",
            "Lightweight Mode",
            "Run well on small machines",
            "PicoClaw",
            "Offer small-machine profiles with lower concurrency, shorter jobs, and gentler background work.",
            "Concierge should respect laptops, phones, and older hardware.",
            "Resource caps are visible and can be tightened by the user.",
            BeyondState.NotBuilt),
        new(
            "security-lab",
            "Security Lab",
            "Check risks safely",
            "Claude-BugHunter and beheader",
            "Run defensive checks, threat models, dependency reviews, and evidence capture in a scoped lab.",
            "Security becomes part of ordinary work instead of a scary final step.",
            "Defensive-only by default, disabled for offensive actions, and always scoped to approved assets.",
            BeyondState.NotBuilt),
        new(
            "media-generation",
            "Media Studio",
            "Create useful visuals",
            "pollinations",
            "Connect low-friction image, audio, and creative generation providers through normal provider policy.",
            "Business users can make launch assets, mockups, and learning material without leaving the app.",
            "Provider-neutral, opt-in, labelled AI output, and subject to content and credential policy.",
            BeyondState.NeedsAKey,
            "The Images room, and make_picture on the canvas. Both need a provider key."),
        new(
            "api-room",
            "API Room",
            "Understand and test APIs",
            "Scalar",
            "Render OpenAPI references, test requests, validate contracts, generate examples, mock APIs, and let an agent explain API workflows.",
            "Concierge becomes stronger for business integrations because every API can be documented, tested, mocked, and explained in one safe workspace.",
            "Scalar-style tools are optional adapters; live requests still follow endpoint allowlists, credential policy, approvals, and audit logging.",
            BeyondState.NotBuilt),
        new(
            "cross-platform-release",
            "Cross-Platform Release",
            "Ship everywhere from one place",
            "CrossCode",
            "Track Apple, Android, Windows, Linux, and web packaging readiness from one release cockpit.",
            "The same idea can become a web app, desktop app, and mobile app without splitting the team.",
            "Signing credentials stay owner-controlled and platform checks remain explicit.",
            BeyondState.NotBuilt)
    ];

    private static readonly IReadOnlyList<MemoryRoom> MemoryRooms =
    [
        new("decisions", "Decisions", "Why we chose something and what alternatives we rejected.", true, true),
        new("evidence", "Evidence", "Build logs, screenshots, approvals, release checks, and audit trails.", true, true),
        new("customers", "Customer signals", "Interviews, support notes, complaints, requests, and repeated patterns.", true, true),
        new("operations", "Operating notes", "SOPs, vendor details, handovers, and recurring business rhythms.", true, true)
    ];

    private static readonly IReadOnlyList<WorkforceSwarm> WorkforceSwarms =
    [
        new(
            "startup-core",
            "Start something",
            "Turn an idea into a small working operation.",
            [
                new("Product guide", "Clarifies the problem and first useful version.", "Cannot invent customer proof."),
                new("Build guide", "Plans and checks the software work.", "Needs approval before file or command changes."),
                new("Money guide", "Tracks simple costs, invoices, and cash-flow questions.", "Cannot connect finance APIs without owner credentials."),
                new("Launch guide", "Helps with website, social, outreach, and support basics.", "Cannot publish externally without review.")
            ]),
        new(
            "operating-company",
            "Run the company",
            "Coordinate normal business work after the first product exists.",
            [
                new("Finance", "Cash, invoices, reports, and controls.", "Never exposes secrets or bank details."),
                new("HR", "Hiring, onboarding, policies, and people tasks.", "Handles personal data with stricter review."),
                new("Legal and admin", "Contracts, filings, records, and compliance reminders.", "Provides support, not legal advice."),
                new("Customer care", "Support patterns, follow-ups, and service quality.", "Cannot send messages without approval.")
            ]),
        new(
            "safe-build-team",
            "Build safely",
            "A Claude Code style team for coding with stronger controls.",
            [
                new("Reader", "Inspects files and explains the workspace.", "Read-only."),
                new("Changer", "Prepares diffs and patches.", "Approval required before writes."),
                new("Tester", "Runs builds, tests, and smoke checks.", "Bounded by resource caps."),
                new("Reviewer", "Looks for regressions and missing evidence.", "Cannot auto-approve its own changes.")
            ])
    ];

    private static readonly IReadOnlyList<SimulationScenario> Simulations =
    [
        new("pricing", "Pricing check", "If we charge this price, what might break?", "Costs, competitors, customer signals, and support effort.", "Simple tradeoff table plus next experiment."),
        new("hiring", "Hiring check", "Should we hire, automate, outsource, or wait?", "Workload, cash runway, risk, and skills gap.", "Decision options with visible assumptions."),
        new("launch", "Launch check", "Are we ready to show this to real people?", "Product proof, support plan, compliance gates, and rollback path.", "Launch confidence score and blocker list.")
    ];

    private static readonly IReadOnlyList<InfrastructureLane> Infrastructure =
    [
        new("local", "This machine", "Run local builds, tests, previews, and cleanup.", "Respect concurrency caps and emergency stop."),
        new("remote", "Remote servers", "Prepare SSH-style operations and deployment checks.", "Credential unlock and explicit approval required."),
        new("cloud", "Cloud accounts", "Track app services, containers, storage, and health checks.", "Provider credentials stay in secure storage.")
    ];

    private static readonly IReadOnlyList<DiagramEvidenceTemplate> Diagrams =
    [
        new("system-map", "System map", "Someone asks how the product fits together.", "Mermaid component diagram."),
        new("process-flow", "Process flow", "A business process needs to be taught or audited.", "Step-by-step flowchart."),
        new("release-path", "Release path", "A build is being prepared for web, desktop, or mobile.", "Release evidence diagram.")
    ];

    public BeyondClaudeSnapshot GetSnapshot()
    {
        return new BeyondClaudeSnapshot(
            Capabilities,
            MemoryRooms,
            WorkforceSwarms,
            Simulations,
            Infrastructure,
            Diagrams,
            ResumePolicy);
    }

    public IReadOnlyList<BeyondCapability> GetCapabilities()
    {
        return Capabilities;
    }
}
