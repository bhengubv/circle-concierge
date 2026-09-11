namespace Concierge.Shared;

public interface IScalarApiService
{
    ScalarIntegrationSnapshot GetSnapshot();
}

/// <summary>
/// The API room, which said it had eight things and had none of them.
///
/// **Every capability below carried `BuiltInByDefault: true`**, and the room drew a green dot
/// and the words "Built in" beside each one — a reference renderer, a request client, a
/// validator, an example generator, a mock server, an SDK generator, an agent that explains an
/// API, and offline support. None of it exists. `ScalarCapabilityKind.MockServer` is an enum
/// value with nothing behind it, no tool in the registry reaches any of this, and the room's
/// own comment claimed that what is built and what is not was "stated per capability, not
/// averaged into a claim".
///
/// The flag is false on all eight now, which is what makes the room say "Not yet" — the honest
/// word was already written and simply never reached, because nothing was ever false.
/// </summary>
public sealed class ScalarApiService : IScalarApiService
{
    private static readonly IReadOnlyList<ScalarCapability> Capabilities =
    [
        new(
            "api-reference",
            "Beautiful API reference",
            ScalarCapabilityKind.ApiReference,
            "Render OpenAPI and Swagger documents as readable API documentation.",
            "Concierge can show a business-friendly API manual beside build, release, and integration work.",
            false,
            "Rendered docs are read-only until the user approves edits to the underlying OpenAPI file."),
        new(
            "api-client",
            "API testing client",
            ScalarCapabilityKind.ApiClient,
            "Send test requests from an API workspace instead of jumping between tools.",
            "Concierge can help teams test business integrations like Xero, Meta, Kakao, Parcel Ninja, and internal APIs.",
            false,
            "Requests must use approved endpoints, stored credentials, and visible request history."),
        new(
            "openapi-validation",
            "OpenAPI checks",
            ScalarCapabilityKind.OpenApiValidation,
            "Validate API descriptions before they become docs, SDKs, tests, or release evidence.",
            "Concierge can stop broken contracts before integrations reach customers.",
            false,
            "Validation is local-first and never uploads private API specs without explicit approval."),
        new(
            "code-examples",
            "Code examples",
            ScalarCapabilityKind.CodeExamples,
            "Generate request examples for common languages and frameworks.",
            "Concierge can turn an API into copyable examples for web, mobile, backend, support, and partner teams.",
            false,
            "Examples are labelled as generated guidance and must not include live secrets."),
        new(
            "mock-server",
            "Mock API server",
            ScalarCapabilityKind.MockServer,
            "Run a local mock from an OpenAPI document for safe testing before a live provider is connected.",
            "Concierge can let teams build and QA integrations before credentials or vendor approval exist.",
            false,
            "Mocks bind locally by default and cannot impersonate a live third-party endpoint without review."),
        new(
            "sdk-generation",
            "SDK generation",
            ScalarCapabilityKind.SdkGeneration,
            "Generate client libraries from an OpenAPI contract.",
            "Concierge can create starter clients for internal APIs and partner integrations.",
            false,
            "Generated code lands in reviewable generated zones and requires approval before replacing hand-written code."),
        new(
            "api-agent",
            "Chat with an API",
            ScalarCapabilityKind.ApiAgent,
            "Let an agent explain endpoints, payloads, auth, errors, and workflows from the API contract.",
            "Concierge can make APIs understandable to non-developers without hiding the contract.",
            false,
            "The agent can explain and prepare requests, but live calls still go through approval and credential policy."),
        new(
            "offline-api-client",
            "Offline-first API work",
            ScalarCapabilityKind.OfflineClient,
            "Keep API exploration useful when a user is away from a network.",
            "Concierge can support travel, field work, and low-connectivity teams.",
            false,
            "Offline state stays local and sync/export remains user controlled.")
    ];

    private static readonly IReadOnlyList<ApiWorkspaceTemplate> Templates =
    [
        new(
            "business-integration",
            "Connect a business tool",
            "Take a third-party API from contract to tested integration.",
            [
                "Import or link the OpenAPI document.",
                "Validate the contract and list missing auth details.",
                "Create safe test requests with no live secrets in examples.",
                "Generate mock responses for UI and workflow testing.",
                "Capture evidence for release and support handover."
            ]),
        new(
            "internal-api",
            "Document an internal API",
            "Turn a team API into something support, QA, partners, and builders can understand.",
            [
                "Find the API contract in the workspace.",
                "Render a readable reference.",
                "Generate examples for the languages the team uses.",
                "Create a mock server plan.",
                "Add release evidence and ownership notes."
            ]),
        new(
            "api-learning",
            "Learn an API",
            "Help a non-specialist understand what an API does and how to use it safely.",
            [
                "Explain the purpose of each endpoint in plain language.",
                "Show required inputs and likely errors.",
                "Prepare a safe first request.",
                "Record useful examples in the Memory Palace.",
                "Mark what still needs owner approval."
            ])
    ];

    public ScalarIntegrationSnapshot GetSnapshot()
    {
        return new ScalarIntegrationSnapshot(
            "Scalar",
            "https://github.com/scalar/scalar",
            "MIT",
            // Was "Built-in API Room capability with optional external Scalar tooling
            // adapters" — present tense, for a room with nothing in it.
            "Planned as an API Room in the app, with optional external Scalar tooling adapters.",
            Capabilities,
            Templates);
    }
}
