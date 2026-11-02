# `ToolUp.Platform.Server` is an umbrella over six part assemblies

**Ships in:** 0.39.0. New packages `ToolUp.Platform.Server.Remoting`, `.Infra`, `.Config`, `.Scope`, `.Jobs` and
`.Audit`; `ToolUp.Platform.Server` keeps its id and references all six.

**Affected:** nobody's source. Every `PackageReference` and `ProjectReference` to `ToolUp.Platform.Server` resolves
the whole tier transitively, and no namespace or module path moved, so every `open` line compiles unchanged. Three
things do change:

- **Binaries.** The members the parts hold are no longer in the `ToolUp.Platform.Server` assembly. A library
  compiled against 0.38 that names them must be recompiled against 0.39 (the draft is already breaking; no version
  moves for this).
- **Provenance.** `ComponentProvenance` reports the assembly a component's type is in, so the in-tree audit sink
  reports `ToolUp.Platform.Server.Audit` where it reported `ToolUp.Platform.Server`.
- **Your first build after pulling must be clean** (`dotnet build --no-incremental`, or delete `obj/`). F#'s
  reference assembly keeps its identity when a type it names moves to another assembly and when an
  `InternalsVisibleTo` is added, so MSBuild skips the copy into `obj/<config>/<tfm>/ref/` and a dependant compiles
  against the stale file. It shows as an internal that is "not accessible", or nonsense type errors in a project
  whose source did not change. This is the reference-assembly gap `1195-compiler-levers.md` records for literals.

## The cut

An F# compile order is a topological order, so a contiguous run of it is a legal assembly. The work was to make
each subsystem's run contiguous. The tree decided the runs. The file dependency graph is every symbol use the F#
compiler service reports, one file to another, over the 602 files: 1,655 file pairs. Uses of a namespace, and pairs
supported only by a module name, were dropped, because they record an `open` rather than a dependency. Each file
was labelled with the subsystem its path and name say. The order was then chosen that moves the fewest lines away
from their label, while no file is placed before a file it uses. Every order of the six non-umbrella runs was
evaluated, 720 in all. A file no rule labels belongs to the substrate, Infra, unless what it uses holds it later.
The regrouping built green at every one of its six steps, which proves the graph complete.

| # | Package | Default name it carries | Files | Lines | New positions | Old span | Labelled here | Census cost |
|---|---|---|---|---|---|---|---|---|
| 1 | `ToolUp.Platform.Server.Remoting` | Remoting | 30 | 15,655 | 1–30 | 8–325 | 30 | 14.5 s, fan-in 6 |
| 2 | `ToolUp.Platform.Server.Infra` | Infra | 160 | 40,920 | 31–190 | 1–507 | 160 | 21.4 s, fan-in 5 |
| 3 | `ToolUp.Platform.Server.Config` | Config | 73 | 17,443 | 191–263 | 3–509 | 51 | 14.8 s, fan-in 4 |
| 4 | `ToolUp.Platform.Server.Scope` | Scope, Teams and Auth | 76 | 18,110 | 264–339 | 84–506 | 75 | 8.4 s, fan-in 3 |
| 5 | `ToolUp.Platform.Server.Jobs` | Jobs and Notifications | 84 | 27,559 | 340–423 | 64–441 | 70 | 12.6 s, fan-in 2 |
| 6 | `ToolUp.Platform.Server.Audit` | Audit, Classification and Consent | 53 | 12,573 | 424–476 | 226–492 | 50 | 7.3 s, fan-in 1 |
| 7 | `ToolUp.Platform.Server` (umbrella) | Compose and Lifecycle, and Files | 126 | 63,235 | 477–602 | 173–602 | 113 | 19.4 s, fan-in 102 |

"Census cost" is the project's row in `build-census.json` after the cut: compile seconds, fan-in. Before the cut the
one Server project was 145.7 s, 195,368 lines, fan-in 102 (`build-census.json` at `6cdf75f2`). "Labelled here" counts
the files whose own subsystem is the part's; the rest are in the list below.

**Departures from the eight default runs, and why:**

- **Files is merged into the umbrella.** Its run is five files (`FileNameSanitiser`, `ProcessedDataCodec`,
  `FileManagement`, `SessionFileStoreInstanceValidator`, `ColumnMappingApiHandler`), under the 15-file floor. Its
  nearest dependant is the umbrella: the narrative stores and `RemotingHelpers` use `FileManagement` and sit there.
- **The order is not the listed one.** Infra comes second, straight after Remoting, because every later subsystem
  uses the stores. Config precedes Scope, and Scope precedes Jobs and Audit, because that is the direction the uses
  run. Uses between the parts, as file pairs, read from the row's part to the column's:

  | uses → | Remoting | Infra | Config | Scope | Jobs | Audit |
  |---|---|---|---|---|---|---|
  | Infra | 26 | | | | | |
  | Config | 18 | 28 | | | | |
  | Scope | 15 | 17 | 46 | | | |
  | Jobs | 28 | 79 | 23 | 2 | | |
  | Audit | 14 | 21 | 9 | 0 | 4 | |
  | umbrella | 23 | 182 | 117 | 100 | 115 | 45 |

- **No run was split.** The order shows no default run as two independent blocks.
- A cheaper order existed: 4,460 line-steps against 4,656. It put Infra first and the data stores into the Remoting
  part, because they use the remoting types. It was not taken: a transport package carrying the entity store is
  the coupling the cut exists to make visible.

**The files that reach across two subsystems.** Each sits in the part the reach forces. "Uses" means it needs a
file of a later subsystem; "used by" means an earlier subsystem needs it. Fifty-three files of 602:

| File | Its subsystem | Placed in | Reach |
|---|---|---|---|
| `ConsoleLogger` | Infra | Config | uses `ConfigResolver`, `ConfigResolution`, `ConfigProfiles` |
| `Api` | Infra | Config | uses `ConfigResolution` |
| `NotificationChannel` | Jobs | Config | used by `PerScopeKeyResolverDistributedValidator` |
| `Background` | Jobs | Config | used by `ConversationStore`, `ResultStore`, `ShareTokenStore` |
| `IdentitySanitiser` | Scope | Config | used by `HeaderAuthProvider` |
| `HeaderAuthProvider` | Scope | Config | used by `SurfaceCoherenceValidator` |
| `BlobStorageFromEnv` | Infra | Config | uses `ConfigResolution` |
| `EmbeddingProviderFromEnv` | Infra | Config | uses `ConfigResolution` |
| `ResultStore` | Infra | Config | uses `Background`, `ConsoleLogger` |
| `ResourceEnvelopeEnforcement` | Infra | Jobs | uses `IJobScheduler`, `IJobHandler` |
| `ExternalModelFitProvider` | Infra | Jobs | uses `IJobProgressSink` |
| `ComposeModelExecution` | Infra | Umbrella | uses `ServiceComposition` |
| `ModelExecutionDepsValidator` | Config | Umbrella | uses `ComposeModelExecution` |
| `DatasetAssemblyExecutor` | Infra | Jobs | uses `RunJob`, `IJobHandler` |
| `ModelEvaluationRunner` | Infra | Jobs | uses `RunJob`, `IJobHandler` |
| `ModelPromotionPolicy` | Infra | Jobs | uses `RunJob`, `IJobHandler`, `ModelEvaluationRunner` |
| `InProcessOAuthTokenRefresher` | Infra | Jobs | uses `IJobScheduler` |
| `FileSecretStore` | Infra | Config | uses `ConfigResolution` |
| `EncryptedSecretStore` | Infra | Config | uses `ConfigResolution` |
| `SecretStoreFromEnv` | Infra | Config | uses `ConfigResolution`, `FileSecretStore`, `EncryptedSecretStore` |
| `ConversationStore` | Infra | Config | uses `Background`, `ConsoleLogger` |
| `PlatformAdminStore` | Infra | Jobs | uses `IDistributedLock`, `InProcessDistributedLock` |
| `ProviderOAuthConnect` | Infra | Config | uses `Background`, `ConsoleLogger` |
| `PerScopeKeyResolver` | Infra | Config | uses `Background` |
| `EncryptionAdminHandler` | Infra | Scope | uses `TokenAttemptThrottle` |
| `AutoBootstrapDevAdminModeValidator` | Scope | Jobs | uses `PlatformAdminStore` |
| `SmokeTests` | Infra | Jobs | uses `IJobScheduler`, `IJobHandler` |
| `SmokeTestHandler` | Infra | Config | uses `ConfigResolution`, `ConsoleLogger` |
| `IUserClaims` | Scope | Config | used by `FlagEvaluator` |
| `DeployPlaneDepsValidator` | Config | Jobs | uses `IJobScheduler` |
| `SignedExportDepsValidator` | Config | Audit | uses `IBackgroundExportStore` |
| `MetricsMiddleware` | Infra | Audit | uses `AuditLog`, `AdAnalyticsApiHandler` |
| `TeamManagement` | Scope | Config | used by `MigrationRunner`, `OAuth1aFlowHandler`, `FeatureFlagHandler` |
| `ModelExecutionApi` | Infra | Jobs | uses `IJobScheduler`, `ModelFitJobHandler` |
| `MaintenanceApiHandler` | Infra | Config | uses `TeamManagement` |
| `OAuthFlowHandler` | Infra | Jobs | uses `IJobScheduler`, `ProviderOAuthRefreshJobHandler`, `ProviderStatusProbeJobHandler` |
| `OAuth1aFlowHandler` | Infra | Config | uses `ConfigResolution`, `TeamManagement`, `Background` |
| `DataIngestionApiHandler` | Infra | Jobs | uses `IJobScheduler`, `DataIngestionJobHandler`, `OAuthFlowHandler` |
| `TeamQuotaPolicy` | Scope | Jobs | uses `IJobScheduler` |
| `UserSchemaApiHandler` | Infra | Config | uses `TeamManagement` |
| `INarrativeStore` | Infra | Umbrella | uses `FileManagement` |
| `InMemoryNarrativeStore` | Infra | Umbrella | uses `INarrativeStore` |
| `PersistentNarrativeStore` | Infra | Umbrella | uses `INarrativeStore` |
| `NotifyingNarrativeStore` | Infra | Umbrella | uses `INarrativeStore` |
| `NarrativeSupersession` | Infra | Umbrella | uses `INarrativeStore` |
| `GroundedNarrative` | Infra | Umbrella | uses `INarrativeStore` |
| `HealthMonitorApiHandler` | Infra | Umbrella | uses `ConfigValidatorAggregator` |
| `EgressPolicyHandler` | Infra | Umbrella | uses `DeploymentVerificationReport` |
| `RedactionAllowlist` | Audit | Config | used by `ConfigDriftDetector` |
| `DiagnosticBundleHandler` | Infra | Umbrella | uses `DevDiagnosticsHandler`, `ConfigValidatorAggregator` |
| `AuthDenialsDiagnosticsHandler` | Scope | Audit | uses `AuthAuditHook` |
| `RemotingHelpers` | Remoting | Umbrella | uses `FileManagement` |
| `ProviderProfileApiHandler` | Infra | Umbrella | uses `RemotingHelpers` |

The three couplings to resolve first, by size: `ConfigResolution` and the env binder are used by Infra's
`*FromEnv` factories and secret stores, which therefore sit in Config. `Background` is used by the result,
conversation and share-token stores, which pulls them into Config. `TeamManagement` is used by migrations, flags and
two OAuth handlers, which puts it in Config ahead of the rest of Scope. Each is a seam a later phase can cut. This
phase records them and resolves none.

## What a part is

Each part's `.fsproj` compiles its run from the sources' existing place under `src/ToolUp.Platform.Server/`. Nothing
moved on disk, so no source citation or test path changed. A file's part is read from
the project files, not its folder. It references only the parts before it, and grants `InternalsVisibleTo` to the
parts after it, the umbrella, and the four test packs that already read the tier's internals. Of 81 file pairs
joined by an internal symbol, 38 cross a part. `ToolUp.Platform.Core` grants the parts the friendship it granted the
Server. Each vendor package is declared by the first part that compiles against it: Giraffe and
`Microsoft.IO.RecyclableMemoryStream` by Remoting, `Microsoft.Data.Sqlite` by Infra, `BouncyCastle.Cryptography` by
Audit. Each part ships its own `README.md`.

## Baselines

The approval gate renders one baseline per assembly. `api-baselines/ToolUp.Platform.Server.approved.txt` now holds
the umbrella's 5,421 tokens, beside one file per part (863, 3,573, 658, 778, 1,662 and 680). At `e1fad671` a
Build-pack test held the union of the seven equal, token for token, to the 13,635-token pre-cut file kept as a
fixture. It was made to fail once by deleting a line, and it reported the token. One normalisation was needed:
`EncryptedSecretStore.summariseRotation` returns an anonymous record, and the CLR name of one hashes its defining
assembly. The check and the fixture were then retired: they read a frozen file and would fail on the next legitimate
change. `ServerPartsBaselineTests` holds what stays true: each part references only earlier parts in the umbrella's
order, and no token is in two server assemblies' baselines. The doc-coverage floor is split the same way.

## Packages

`ToolUp.Sdk`'s manifest lists the parts. `GenerateSdkManifest` found them in the core lockstep group through its
`ToolUp.Platform.*` glob, with no edit to `version-groups.json`. `TemplateGatePackages.templates` packs them, and
so do both closures built on it. **`publish-nuget.yml` needs no edit.** Its `Publish` target packs
`src/**/*.fsproj` less `PackScope.excludedDirs` and `IsPackable=false`, and the parts are packable `ToolUp.*` ids
under `src/`. A dry pack at `0.0.0-p1193` produced the umbrella's nupkg depending on all six parts, and each part's
with its README and icon. A consumer restored with only `<PackageReference Include="ToolUp.Platform.Server">`
against that folder, the local feed and nuget.org, from an empty packages folder. It compiled against one type
from each part and ran.

## What it cost and saved

Measured on one Windows Arm64 machine, 12 logical processors, Debug, 2026-11-02, on a shared machine (other sessions
were building). Counts are the evidence; seconds are one busy machine's.

| | Before (`a1a61218`) | After (census at `1ad90d66`; closures at `da1add2a`) |
|---|---|---|
| Solution build, clean (`Census`: `-graph --no-incremental`) | 889.8 s (census at `6cdf75f2`) | 336.5 s. Not comparable: the census before summed 9,628.7 compile-seconds over its run and this one 2,573.7, so the machine was far busier then. The server tier's own chain is 98.4 s over seven projects that compile in sequence, against 145.7 s for one. |
| Projects in the solution | 230 | 236 |
| Implementation-only edit, one server file: projects recompiled | 2 (`1195-compiler-levers.md`) | 2: the edited part, and `ToolUp.AI.Evaluation` |
| …lines that edit recompiles | 195,495 | the part's: 12,573 for an Audit file, 63,235 at most (umbrella) |
| Public-surface edit, one server file: projects recompiled | 129 (Server and its 128 dependants) | 130 for an Audit file: Audit, the umbrella, the 128. A part recompiles the parts after it, so an Infra file is 134. Nothing before the edited part. |
| Gate selection on that edit | pending Phase 1194 | pending Phase 1194 |

So the cut does not shrink the public-edit closure, which is the umbrella's dependants either way. What it buys is
smaller units: an implementation edit recompiles one part, not 195k lines. And the project graph now says which
subsystem an edit touched, which is what Phase 1194's test selection reads.

## Rollback

Revert the phase's commits. Parts published under their own ids stay on nuget.org, unreferenced by a reverted
umbrella.
