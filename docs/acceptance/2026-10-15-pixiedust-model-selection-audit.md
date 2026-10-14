# PixieDust: installed service model-selection wiring

Read-only audit of the installed APK, not the Circle AI repository. The service
APK was pulled from PixieDust through its existing ADB pairing. Its compressed
assembly store was decoded and inspected with .NET metadata/IL readers. No
service code, model files or settings were changed.

APK SHA-256:
`0A79EDC8426AF5C61A881FDB2AA79720642F75BA2EFBC2357A94D683868AF43A`

## What the installed code actually does

### The selector is called on a cold node

`CircleAI.Service.Android.ServiceApplication.<OnCreate>b__2_2` constructs
AIOptions and sets NativeLibDir, ModelStorageDirectory, WarmOnStart,
SystemPrompt, OverviewAnswer and SkillStore. It does **not** set ModelId.
AIOptions' constructor does not assign a default ModelId either.

`CircleAI.Device.CircleNeuronService.<BuildNodeAsync>d__50.MoveNext` then:

1. Returns early if an existing Node is present (IL offsets 00D7–00F9).
2. Constructs a fresh ModelRegistryService with a null catalog client (0106–010C),
   BundleModelLoader (011D), and DeviceAwareModelSelector (0121–0126).
3. When ModelId is blank, calls DeviceProbe.Snapshot and
   DeviceAwareModelSelector.BestFit(required capabilities) (0181–019E).
4. If that model is absent, calls BestInstalledChatModel and substitutes that
   installed model (01AA–026D).
5. Checks the assessed catalogue for a crash-refused model, potentially using
   another installed fallback (02F3–03CF).
6. Assigns the resulting ModelId to AIOptions and creates the node (03E6–041F).

Therefore "no selector was called" is not supported by this binary. Selection
exists, but its target is not consistently carried through provisioning.

### Download planning can suppress a better target

`DeviceSetup.<PlanAsync>b__17_0` calls `FirstRun.Plan`, not the runtime's
DeviceAwareModelSelector selection result.

For an unnamed modality such as the brain, `FirstRun.Plan` calls Any with
`<Plan>b__1` (00FA–0111). That predicate checks matching modality and
BundleModelLoader.ModelPresent. It does not compare quality, selected target,
current compatibility or crash refusal. If any such model is present, the
planner skips the candidate-ranking path entirely.

Only when that installed-model shortcut is false does it filter candidates and
rank by QualityRank descending, then MinRamGb ascending (0116–01BB).

This explains how an empty setup plan can coexist with a runtime that would
prefer a different, not-yet-installed model. The loader can then substitute an
installed model instead of arranging to prepare its preferred target.

### Assessed catalogue and runtime choice are separate paths

Service startup creates a SqliteModelCatalog, assigns CircleNeuronService.Catalogue
and runs DeviceModelAssessor.ForThisBuild plus CatalogueBootstrap.Run in a
background task.

The runtime selection above constructs a separate ModelRegistryService, whose
AllModels combines its embedded registry with a remote or optional global Live
registry. It does not select the assessed SqliteModelCatalog.Best(Chat) result.
The runtime references the assessed catalogue for crash refusals, rather than
using its selected compatible model as the provisioning/loading target.

The service options factory also supplies no Router, CatalogClient or
CheckForUpgradesOnStart setting. CheckForUpgradesOnStart's default is false.
Earlier live inference logs reported "generalist: no router configured".
Existing-node reuse skips even the cold-node selector call.

## Model metadata and limits of this finding

The installed embedded registry contains 90 entries, re-derived from its resource.
Among them:

| Model | QualityRank | MinRamGb | MinStorageGb | MemoryHintBytes |
|---|---:|---:|---:|---:|
| Qwen3.5-2B-MNN | 9 | 1.9 | 1.5 | 1,950,000,000 |
| Qwen3.5-4B-MNN | 11 | 3.8 | 3.0 | 4,000,000,000 |

The earlier load log identifies Qwen3.5-2B-MNN. The historical selector snapshot
and full candidate verdicts were not recorded, so this audit does **not** claim
4B was eligible at that moment, or that switching to it would fix arithmetic.
BestFit filters by UsableRamGb and free storage before ranking. Total installed
RAM alone cannot prove eligibility.

## Correction required in the service

Use one authoritative service selection result for the download plan, preparation,
loading and later resource-driven upgrades. An arbitrary installed brain must not
make the selected target disappear from the plan. Apply supported-engine,
resource, refusal and user download-policy constraints consistently. When the
service retains a fallback, report both the selected target and fallback reason.

Verify with an older small model already installed and a different eligible
target: the plan must offer/prepare that target, the service must await its load,
and a normal Concierge query must be credited to the actually loaded model.
Also verify the already-running-node path; restart-only selection is not evidence
of autonomous upgrades.

Concierge's current Android transport sends no ModelId or model-selection
parameters. It passes the durable session ID, query and Agentic flag through the
published LinkTurn codec. The model-selection correction belongs to the service
implementation, not another selector added to Concierge.
