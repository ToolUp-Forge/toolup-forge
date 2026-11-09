# The Platform test pack, partitioned by server part

**Ships in:** the repository's test layout only. Eight new projects, all `IsPackable=false`: one test pack per
server part (`ToolUp.Platform.Tests.Remoting`, `.Infra`, `.Config`, `.Scope`, `.Jobs`, `.Audit`), the public-API
approval gate's pack (`ToolUp.Platform.Tests.PublicApi`), and the harness they share
(`ToolUp.Platform.Tests.Support`). `ToolUp.Platform.Tests` stays as the residue: the umbrella's pack. No packable
surface moves and no API baseline changes. The six server parts and the client tier grant their internals to the packs
that read them. **No consumer action is needed.**

**Affected:** anyone adding or editing a Platform test, anyone regenerating an API baseline, and the `affected` lane,
which gains a second selection source: the project graph.

## The map: which list is in which pack

**The rule.** A registered list goes to the pack of the **latest server part its test file reaches**, in the parts'
order (Remoting, Infra, Config, Scope, Jobs, Audit), or to the residue when the file reaches the umbrella or a
companion built on it. A file reaches a part when:

1. **it uses a symbol of the part's assembly**, or of a project whose closure holds the part. The facts are every
   symbol use the F# compiler service reports over the pack's 846 files, attributed to the assembly that declares the
   symbol. Uses of a namespace or a module name alone were dropped: they record an `open`, not a dependency;
2. **it names the assembly in a string literal**, as a path segment or whole: a run-time load
   (`Assembly.Load "ToolUp.Platform.Server"`) or a scan of that project's sources, which the compiler cannot see. 15
   files were raised this way; the first build of the Remoting pack showed why, when `ArchitectureFitnessTests` failed
   loading the umbrella by name;
3. **it uses a symbol of another test file that reaches the part.**

A list whose file reaches two parts is in the later one. A `[<Tests>]`-bearing file that a file of a later pack uses
moves to that pack, so that no binding is in two assemblies' guard sweeps. A file with no bindings that two packs use
is compiled by both (22 files, listed below). A list that reaches no server part is in the earliest pack, Remoting,
whose closure is the smallest; this is the one placement the phase's rule does not decide, and the choice is made so
that such a list is skipped by every diff that cannot reach it. No part's pack holds fewer than twenty lists, so no
pack was merged into a later one.

**The pack set, as ruled before any file moved:** one pack per 1193 part, the residue for the lists that reach the
umbrella (Compose, Lifecycle, Files), and the approval gate's own pack.

| Row | Lists | Files compiled | Cases | Projects in its closure | Server parts in its closure |
|---|---|---|---|---|---|
| `Platform.Remoting` | 129 | 125 | 2,458 | 32 | Remoting |
| `Platform.Infra` | 59 | 53 | 723 | 13 | Remoting, Infra |
| `Platform.Config` | 32 | 38 | 581 | 9 | Remoting to Config |
| `Platform.Scope` | 51 | 50 | 597 | 16 | Remoting to Scope |
| `Platform.Jobs` | 42 | 49 | 583 | 10 | Remoting to Jobs |
| `Platform.Audit` | 38 | 36 | 330 | 11 | all six |
| `Platform.PublicApi` | 1 | 3 | 239 | 193 | all six and the umbrella (built, not referenced) |
| `Platform` (residue) | 629 | 516 | 9,754 | 112 | all six and the umbrella |

"Cases" is the pack's `--list-tests` count, its registration guard's three cases included. The residue's closure is
the umbrella's, which holds every part. Before the partition the one pack's closure was 111 projects.

**Every former list runs in exactly one pack.** The union of the eight packs' `--list-tests` output, each pack's root
label removed and the guards' cases set aside, equals the cut commit's list (`8b055eff`) case for case: 15,241
cases (11 of them pending arms), no case missing and none in two packs. The check was run after every move. It is not
kept as a Build-pack test: it compares the packs with a frozen list that the next added case falsifies, the reason
1193 retired its union test. What stays true is held where it belongs: each pack's registration guard fails on an
unregistered binding of its own assembly, `GateReconcile` fails on a test project with no row, and a list registered in
two packs would run twice, which the union check above found nowhere.

The table below is every registered entry (an argument-taking contract binding is one entry per instantiation, and a
sequenced group is one entry per pack), with what placed it. Paths are under `src/ToolUp.Platform.Tests/`.

| Pack | Registered list | Test file | What placed it |
|---|---|---|---|
| Platform.Remoting | `ColumnMatcherTests.tests` | `InProcess/ColumnMatcherTests.fs` | reaches no server part |
| Platform.Remoting | `ColumnMappingTests.tests` | `InProcess/ColumnMappingTests.fs` | reaches no server part |
| Platform.Remoting | `DerivedColumnEvalTests.tests` | `InProcess/DerivedColumnEvalTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `ExperimentSubstrateTests.tests` | `InProcess/ExperimentSubstrateTests.fs` | reaches no server part |
| Platform.Remoting | `WorkflowEngineTests.tests` | `InProcess/WorkflowEngineTests.fs` | reaches no server part |
| Platform.Remoting | `ClientBrandLiftTests.tests` | `InProcess/ClientBrandLiftTests.fs` | reaches no server part |
| Platform.Remoting | `CellProvenanceTests.tests` | `InProcess/CellProvenanceTests.fs` | reaches no server part |
| Platform.Remoting | `AgChartExportTests.tests` | `InProcess/AgChartExportTests.fs` | reaches no server part |
| Platform.Remoting | `ValidationTests.tests` | `InProcess/ValidationTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Infra | `IdempotencyReplayAuditTests.tests` | `InProcess/IdempotencyReplayAuditTests.fs` | uses `BlobIdempotencyStore` (`ToolUp.Platform.Server.Infra`, since Phase 1061) |
| Platform.Remoting | `IdempotencyStoreEvictionTests.tests` | `InProcess/IdempotencyStoreEvictionTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `InMemoryJobDispatcherTests.tests` | `InProcess/InMemoryJobDispatcherTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `AuditBodyDisposalTests.tests` | `InProcess/AuditBodyDisposalTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `PostDispatchBodyReadTests.tests` | `InProcess/PostDispatchBodyReadTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `StreamingTests.tests` | `InProcess/StreamingTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `StreamingDispatchTests.tests` | `InProcess/StreamingDispatchTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `JobHandleTests.tests` | `InProcess/JobHandleTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `MarkdownRendererTests.tests` | `InProcess/MarkdownRendererTests.fs` | reaches no server part |
| Platform.Remoting | `HtmlRendererTests.tests` | `InProcess/HtmlRendererTests.fs` | reaches no server part |
| Platform.Remoting | `NarrativeElementTests.tests` | `InProcess/NarrativeElementTests.fs` | reaches no server part |
| Platform.Remoting | `HealthyHealthCheckTests.tests` | `InProcess/HealthyHealthCheckTests.fs` | reaches no server part |
| Platform.Remoting | `DegradedHealthCheckTests.tests` | `InProcess/DegradedHealthCheckTests.fs` | reaches no server part |
| Platform.Remoting | `UnhealthyHealthCheckTests.tests` | `InProcess/UnhealthyHealthCheckTests.fs` | reaches no server part |
| Platform.Remoting | `OkConfigValidatorTests.tests` | `InProcess/OkConfigValidatorTests.fs` | reaches no server part |
| Platform.Remoting | `WarningConfigValidatorTests.tests` | `InProcess/WarningConfigValidatorTests.fs` | reaches no server part |
| Platform.Remoting | `ErrorConfigValidatorTests.tests` | `InProcess/ErrorConfigValidatorTests.fs` | reaches no server part |
| Platform.Remoting | `EffectJoinTests.tests` | `InProcess/EffectJoinTests.fs` | reaches no server part |
| Platform.Remoting | `OidcClassifyTokenTests.tests` | `InProcess/OidcClassifyTokenTests.fs` | reaches no server part |
| Platform.Remoting | `OidcRefreshPlanTests.tests` | `InProcess/OidcRefreshPlanTests.fs` | reaches no server part |
| Platform.Remoting | `OidcDiagnoseTests.tests` | `InProcess/OidcDiagnoseTests.fs` | reaches no server part |
| Platform.Remoting | `OidcTracerTests.tests` | `InProcess/OidcTracerTests.fs` | reaches no server part |
| Platform.Remoting | `OidcStateMachineTests.tests` | `InProcess/OidcStateMachineTests.fs` | reaches no server part |
| Platform.Remoting | `OidcPresetsTests.tests` | `InProcess/OidcPresetsTests.fs` | reaches no server part |
| Platform.Remoting | `OidcCoherenceValidatorTests.tests` | `InProcess/OidcCoherenceValidatorTests.fs` | reaches no server part |
| Platform.Remoting | `OidcSecondaryFlowTests.tests` | `InProcess/OidcSecondaryFlowTests.fs` | reaches no server part |
| Platform.Remoting | `InMemoryGraphStoreTests.tests` | `Graph/InMemoryGraphStoreTests.fs` | uses `IGraphStoreContract`, which reaches Remoting |
| Platform.Remoting | `Neo4jConformanceTests.pureTests` | `Graph/Neo4jConformance.fs` | uses `IGraphStoreContract`, which reaches Remoting |
| Platform.Remoting | `Neo4jConformanceTests.liveTests` | `Graph/Neo4jConformance.fs` | uses `IGraphStoreContract`, which reaches Remoting |
| Platform.Remoting | `AgeConformanceTests.pureTests` | `Graph/AgeConformance.fs` | uses `IGraphStoreContract`, which reaches Remoting |
| Platform.Remoting | `AgeConformanceTests.liveTests` | `Graph/AgeConformance.fs` | uses `IGraphStoreContract`, which reaches Remoting |
| Platform.Remoting | `DatadogReadbackClientTests.tests` | `InProcess/DatadogReadbackClientTests.fs` | reaches no server part |
| Platform.Remoting | `RateLimitConfigHelpersTests.tests` | `InProcess/RateLimitConfigHelpersTests.fs` | reaches no server part |
| Platform.Remoting | `I18nCoverageTests.tests` | `InProcess/I18nCoverageTests.fs` | reaches no server part |
| Platform.Remoting | `MultimodalAIProviderTests.tests` | `InProcess/MultimodalAIProviderTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `SyntheticClientToolAuthorizerTests.tests` | `InProcess/SyntheticClientToolAuthorizerTests.fs` | reaches no server part |
| Platform.Remoting | `HostCapabilityNegotiationTests.tests` | `InProcess/HostCapabilityNegotiationTests.fs` | reaches no server part |
| Platform.Remoting | `HostRenderTelemetryTests.tests` | `InProcess/HostRenderTelemetryTests.fs` | reaches no server part |
| Platform.Remoting | `HostContentSanitizerTests.tests` | `InProcess/HostContentSanitizerTests.fs` | reaches no server part |
| Platform.Remoting | `HostI18nResolverTests.tests` | `InProcess/HostI18nResolverTests.fs` | reaches no server part |
| Platform.Remoting | `HostRenderBudgetTests.tests` | `InProcess/HostRenderBudgetTests.fs` | reaches no server part |
| Platform.Remoting | `NoActiveTeamLandingTests.tests` | `InProcess/NoActiveTeamLandingTests.fs` | reaches no server part |
| Platform.Remoting | `ScopeDenialTests.tests` | `InProcess/ScopeDenialTests.fs` | reaches no server part |
| Platform.Remoting | `HostingSeamComponentIdTests.tests` | `InProcess/HostingSeamComponentIdTests.fs` | reaches no server part |
| Platform.Remoting | `HostedTreeLayoutTests.tests` | `InProcess/HostedTreeLayoutTests.fs` | reaches no server part |
| Platform.Remoting | `UnreducedViewPreviewTests.tests` | `InProcess/UnreducedViewPreviewTests.fs` | reaches no server part |
| Platform.Remoting | `FactRenderingTests.tests` | `InProcess/FactRenderingTests.fs` | reaches no server part |
| Platform.Remoting | `FactRenderingTests.invariantGlobalizationTests` | `InProcess/FactRenderingTests.fs` | reaches no server part |
| Platform.Remoting | `BrandKitTests.tests` | `InProcess/BrandKitTests.fs` | reaches no server part |
| Platform.Remoting | `HostThemeTokensTests.tests` | `InProcess/HostThemeTokensTests.fs` | reaches no server part |
| Platform.Remoting | `BrandKitLayoutTests.tests` | `InProcess/BrandKitLayoutTests.fs` | reaches no server part |
| Platform.Remoting | `GoogleDirectoryTests.tests` | `InProcess/GoogleDirectoryTests.fs` | reaches no server part |
| Platform.Remoting | `WithRequestHeadersPassthroughTests.tests` | `InProcess/WithRequestHeadersPassthroughTests.fs` | reaches no server part |
| Platform.Remoting | `ConsentProviderTests.tests` | `InProcess/ConsentProviderTests.fs` | reaches no server part |
| Platform.Remoting | `ConsentProviderTests.subscriptionFiringTests` | `InProcess/ConsentProviderTests.fs` | reaches no server part |
| Platform.Remoting | `ConsentGatedScriptTests.tests` | `InProcess/ConsentGatedScriptTests.fs` | reaches no server part |
| Platform.Remoting | `AdAnalyticsSinkTests.tests` | `InProcess/AdAnalyticsSinkTests.fs` | reaches no server part |
| Platform.Remoting | `AdAnalyticsSinkTests.noOpTests` | `InProcess/AdAnalyticsSinkTests.fs` | reaches no server part |
| Platform.Remoting | `ModuleGroupingValidatorTests.tests` | `InProcess/ModuleGroupingValidatorTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarVisibilityContractTests.tests` | `InProcess/SidebarVisibilityContractTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarVisibilityContractTests.orderingTests` | `InProcess/SidebarVisibilityContractTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarVisibilityContractTests.groupSetTests` | `InProcess/SidebarVisibilityContractTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarVisibilityContractTests.navRoleTests` | `InProcess/SidebarVisibilityContractTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarVisibilityContractTests.accessibleNameTests` | `InProcess/SidebarVisibilityContractTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarVisibilityContractTests.placementTests` | `InProcess/SidebarVisibilityContractTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarVisibilityContractTests.keyboardNavigationTests` | `InProcess/SidebarVisibilityContractTests.fs` | reaches no server part |
| Platform.Remoting | `RouteGuardContractTests.routeGuardTests` | `InProcess/RouteGuardContractTests.fs` | reaches no server part |
| Platform.Remoting | `RouteGuardContractTests.sharedPredicateTests` | `InProcess/RouteGuardContractTests.fs` | reaches no server part |
| Platform.Remoting | `CommandPaletteContractTests.paletteVisibilityTests` | `InProcess/CommandPaletteContractTests.fs` | reaches no server part |
| Platform.Remoting | `CommandPaletteContractTests.expansionTests` | `InProcess/CommandPaletteContractTests.fs` | reaches no server part |
| Platform.Remoting | `CommandPaletteContractTests.fuzzyTests` | `InProcess/CommandPaletteContractTests.fs` | reaches no server part |
| Platform.Remoting | `CommandPaletteContractTests.paletteStateTests` | `InProcess/CommandPaletteContractTests.fs` | reaches no server part |
| Platform.Remoting | `MessageCatalogContractTests.tests` | `InProcess/MessageCatalogContractTests.fs` | reaches no server part |
| Platform.Remoting | `MessageCatalogContractTests.teamDefaultTests` | `InProcess/MessageCatalogContractTests.fs` | reaches no server part |
| Platform.Remoting | `MessageCatalogContractTests.consumerOverrideTests` | `InProcess/MessageCatalogContractTests.fs` | reaches no server part |
| Platform.Remoting | `MessageCatalogContractTests.localeSwitchTests` | `InProcess/MessageCatalogContractTests.fs` | reaches no server part |
| Platform.Remoting | `LocalizationTests.tests` | `InProcess/LocalizationTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarHidingContractTests.hideRestoreTests` | `InProcess/SidebarHidingContractTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarHidingContractTests.pinHideRuleTests` | `InProcess/SidebarHidingContractTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarHidingContractTests.legacyBlobTests` | `InProcess/SidebarHidingContractTests.fs` | reaches no server part |
| Platform.Remoting | `SidebarHidingContractTests.paletteParityTests` | `InProcess/SidebarHidingContractTests.fs` | reaches no server part |
| Platform.Remoting | `AdminLandingContractTests.compositionTests` | `InProcess/AdminLandingContractTests.fs` | reaches no server part |
| Platform.Remoting | `AdminLandingContractTests.roleFilterTests` | `InProcess/AdminLandingContractTests.fs` | reaches no server part |
| Platform.Remoting | `AdminLandingContractTests.clickThroughTests` | `InProcess/AdminLandingContractTests.fs` | reaches no server part |
| Platform.Remoting | `AdminLandingContractTests.emptyStateTests` | `InProcess/AdminLandingContractTests.fs` | reaches no server part |
| Platform.Remoting | `CsrfPrefetchAnonymousGateTests.tests` | `InProcess/CsrfPrefetchAnonymousGateTests.fs` | reaches no server part |
| Platform.Remoting | `SubjectKindClientFlowTests.tests` | `InProcess/SubjectKindClientFlowTests.fs` | reaches no server part |
| Platform.Remoting | `SubjectWildcardAnalyzerTests.tests` | `InProcess/SubjectWildcardAnalyzerTests.fs` | reaches no server part |
| Platform.Remoting | `GeneratedInvocationChainTests.tests` | `InProcess/GeneratedInvocationChainTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `IAIProviderContract.tests` | `Contracts/IAIProviderContract.fs` | reaches no server part |
| Platform.Remoting | `SvgPropTests.tests` | `InProcess/SvgPropTests.fs` | reaches no server part |
| Platform.Remoting | `DataPropTests.tests` | `InProcess/DataPropTests.fs` | reaches no server part |
| Platform.Remoting | `AriaPropTests.tests` | `InProcess/AriaPropTests.fs` | reaches no server part |
| Platform.Remoting | `ClientToolkitThemingTests.tests` | `InProcess/ClientToolkitThemingTests.fs` | reaches no server part |
| Platform.Remoting | `DomAttrCustomAuditTests.tests` | `InProcess/DomAttrCustomAuditTests.fs` | reaches no server part |
| Platform.Remoting | `StjBackwardCompatTests.tests` | `InProcess/StjBackwardCompatTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `RemotingAnalyzerRecognitionTests.tests` | `InProcess/RemotingAnalyzerRecognitionTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `RemotingDecodeRefusalTests.tests` | `InProcess/RemotingDecodeRefusalTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `SchemaVersionTests.tests` | `InProcess/SchemaVersionTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `MsgPackHostileInputTests.tests` | `Remoting/MsgPackHostileInputTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `OpenCoreVocabularyNeutralityTests.tests` | `InProcess/OpenCoreVocabularyNeutralityTests.fs` | reaches no server part |
| Platform.Remoting | `ClientHostCapabilitiesContract.tests` | `Contracts/ClientHostCapabilitiesContract.fs` | reaches no server part |
| Platform.Remoting | `Conformance.ModelExecutionSpecConformance.tests` | `Conformance/ModelExecutionSpecConformance.fs` | reaches no server part |
| Platform.Remoting | `MsgPackRoundTripTests.tests` | `Remoting/MsgPackRoundTripTests.fs` | uses `WireCorpus`, which reaches Remoting |
| Platform.Remoting | `StjRoundTripTests.tests` | `Remoting/StjRoundTripTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `JsonTextTests.tests` | `Remoting/JsonTextTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `JsonWideValueTests.tests` | `Remoting/JsonWideValueTests.fs` | reaches no server part |
| Platform.Remoting | `ReadPolicyAttributeTests.tests` | `Remoting/ReadPolicyAttributeTests.fs` | reaches no server part |
| Platform.Remoting | `RemotingBatchTests.tests` | `Remoting/RemotingBatchTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `GeneratorFidelityTests.tests` | `Remoting/GeneratorFidelityTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `ClientProxyGenerationTests.tests` | `Remoting/ClientProxyGenerationTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `PlatformDecodersGenerationTests.tests` | `Remoting/PlatformDecodersGenerationTests.fs` | uses `ToolUp.Platform.Server.Remoting` |
| Platform.Remoting | `ApiRecordCensusTests.tests` | `Remoting/ApiRecordCensusTests.fs` | reaches no server part |
| Platform.Remoting | `ProofOracleTests.tests` | `Remoting/ProofOracleTests.fs` | uses `WireCorpus`, which reaches Remoting |
| Platform.Remoting | `DisclosureProofOracleTests.tests` | `InProcess/DisclosureProofOracleTests.fs` | reaches no server part |
| Platform.Remoting | `ModelInputProofOracleTests.tests` | `InProcess/ModelInputProofOracleTests.fs` | reaches no server part |
| Platform.Remoting | `ElmishProofOracleTests.tests` | `InProcess/ElmishProofOracleTests.fs` | reaches no server part |
| Platform.Remoting | `ElmishLoopProofOracleTests.tests` | `InProcess/ElmishLoopProofOracleTests.fs` | reaches no server part |
| Platform.Remoting | `FactPresentationTests.tests` | `InProcess/FactPresentationTests.fs` | reaches no server part |
| Platform.Remoting | `AppCompositionConformanceTests.tests` | `InProcess/AppCompositionConformanceTests.fs` | reaches no server part |
| Platform.Infra | `LocalFileStorageTests.tests` | `InProcess/LocalFileStorageTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `InMemoryEventStoreTests.tests` | `InProcess/InMemoryEventStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `PersistentEventStoreTests.tests` | `InProcess/PersistentEventStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `DataObjectStoreTests.tests` | `InProcess/DataObjectStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `DatasetStoreTests.tests` | `InProcess/DatasetStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `VirtualDatasetTests.tests` | `InProcess/VirtualDatasetTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `IModelRegistryContract.tests` | `Contracts/IModelRegistryContract.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `IExternalHandleStoreContract.tests` | `Contracts/IExternalHandleStoreContract.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `IExternalHandleStoreContract.selfTests` | `Contracts/IExternalHandleStoreContract.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `SidecarStoreTests.tests` | `InProcess/SidecarStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `IColumnMappingStoreContract.tests` | `Contracts/IColumnMappingStoreContract.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `DataCatalogTests.tests` | `InProcess/DataCatalogTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `LineageStoreTests.tests` | `InProcess/LineageStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `InMemoryDataSourceTests.tests` | `InProcess/InMemoryDataSourceTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `InMemoryDataSourceTests.payloadFormatTests` | `InProcess/InMemoryDataSourceTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `DataSourceFidelityContract.selfTests` | `Contracts/DataSourceFidelityContract.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `OAuth1aSubstrateTests.tests` | `InProcess/OAuth1aSubstrateTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `OAuth1aSubstrateTests.flowTests` | `InProcess/OAuth1aSubstrateTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `TransientFaultPolicyTests.tests` | `InProcess/TransientFaultPolicyTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `TimeSeriesStoreTests.tests` | `InProcess/TimeSeriesStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `TelemetrySinkTests.tests` | `InProcess/TelemetrySinkTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `HealthCheckAggregatorTests.tests` | `InProcess/HealthCheckAggregatorTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `HealthStateTrackerTests.tests` | `InProcess/HealthStateTrackerTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `EntityOutboxTests.tests` | `InProcess/EntityOutboxTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `RelationshipTests.tests` | `InProcess/RelationshipTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `PrometheusMetricsSinkTests.tests` | `InProcess/PrometheusMetricsSinkTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `JsonConsoleLoggerTests.tests` | `InProcess/JsonConsoleLoggerTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BlobProviderProfileTests.tests` | `InProcess/BlobProviderProfileTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `DegradedCapabilityRegistryTests.tests` | `InProcess/DegradedCapabilityRegistryTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `InMemoryRateLimitStoreTests.tests` | `InProcess/InMemoryRateLimitStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `InMemoryRateLimitStoreTests.contractTests` | `InProcess/InMemoryRateLimitStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BuildTranscriptTests.transcriptDeterminismTests` | `InProcess/BuildTranscriptTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BuildTranscriptTests.transcriptSensitivityTests` | `InProcess/BuildTranscriptTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BuildTranscriptTests.provenanceTests` | `InProcess/BuildTranscriptTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BuildTranscriptTests.deployRecordCanonicalFormTests` | `InProcess/BuildTranscriptTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BuildTranscriptTests.deployRecordVerificationTests` | `InProcess/BuildTranscriptTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BuildTranscriptTests.closureCanonicalFormTests` | `InProcess/BuildTranscriptTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BuildTranscriptTests.closureAttestationTests` | `InProcess/BuildTranscriptTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BuildTranscriptTests.closureCaptureTests` | `InProcess/BuildTranscriptTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BuildTranscriptTests.closureBindingTests` | `InProcess/BuildTranscriptTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `WorkProvenanceSourceTests.tests` | `InProcess/WorkProvenanceSourceTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `SbomProjectionTests.sbomSchemaConformanceTests` | `InProcess/SbomProjectionTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `SbomProjectionTests.sbomUnattestedPresenceTests` | `InProcess/SbomProjectionTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `SbomProjectionTests.sbomDeterminismTests` | `InProcess/SbomProjectionTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `SbomProjectionTests.sbomHonestyTests` | `InProcess/SbomProjectionTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `SbomProjectionTests.sbomScopeStatementTests` | `InProcess/SbomProjectionTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `SbomProjectionTests.sbomEndpointTests` | `InProcess/SbomProjectionTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `PrerenderDeterminismTests.tests` | `InProcess/PrerenderDeterminismTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `MemoizedComputeDispatcherTests.memoizationTests` | `InProcess/MemoizedComputeDispatcherTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `MemoizedComputeDispatcherTests.coalescingTests` | `InProcess/MemoizedComputeDispatcherTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `MemoizedComputeDispatcherTests.scopeIsolationTests` | `InProcess/MemoizedComputeDispatcherTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `MemoizedComputeDispatcherTests.ttlTests` | `InProcess/MemoizedComputeDispatcherTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `MemoizedComputeDispatcherTests.optInTests` | `InProcess/MemoizedComputeDispatcherTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `MemoizedComputeDispatcherTests.budgetCompositionTests` | `InProcess/MemoizedComputeDispatcherTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `MemoizedComputeDispatcherTests.durabilityTests` | `InProcess/MemoizedComputeDispatcherTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `MemoizedComputeDispatcherTests.evictionTests` | `InProcess/MemoizedComputeDispatcherTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `BudgetSeamTests.tests` | `InProcess/BudgetSeamTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `SignedWorkerOutcomeTests.tests` | `InProcess/SignedWorkerOutcomeTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Infra | `SqliteLogStoreTests.tests` | `InProcess/SqliteLogStoreTests.fs` | uses `ToolUp.Platform.Server.Infra` |
| Platform.Config | `DataMigrationTests.tests` | `InProcess/DataMigrationTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `MappingDryRunValidationTests.tests` | `InProcess/MappingDryRunValidationTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `ResultStoreTests.tests` | `InProcess/ResultStoreTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `ConversationStoreTests.tests` | `InProcess/ConversationStoreTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `OAuthSecretEncryptionModeValidatorTests.tests` | `InProcess/OAuthSecretEncryptionModeValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `FileSecretStoreTests.tests` | `InProcess/FileSecretStoreTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `FileSecretStoreAtomicityTests.tests` | `InProcess/FileSecretStoreAtomicityTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `FileSecretStoreCancellationTests.tests` | `InProcess/FileSecretStoreCancellationTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `FeatureFlagStoreTests.tests` | `InProcess/FeatureFlagStoreTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `FlagEvaluatorTests.tests` | `InProcess/FlagEvaluatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `ServerConfigFromEnvTests.tests` | `InProcess/ServerConfigFromEnvTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `ServerConfigSnapshotTests.tests` | `InProcess/ServerConfigSnapshotTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `AuthClassifierAttributeIdentityTests.tests` | `InProcess/AuthClassifierAttributeIdentityTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `AttributeRecognitionSweepTests.tests` | `InProcess/AttributeRecognitionSweepTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `InMemoryNotificationChannelTests.tests` | `InProcess/InMemoryNotificationChannelTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `ConfigKeyReadBoolTests.tests` | `InProcess/ConfigKeyReadBoolTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `UnknownConfigKeyValidatorTests.tests` | `InProcess/UnknownConfigKeyValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `TraceCategoriesValidatorTests.tests` | `InProcess/TraceCategoriesValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `env-mutating config validators (Phase 653)` group: ConfigBoundsValidatorTests.intKeyTests, ConfigBoundsValidatorTests.retentionTests` | `InProcess/ConfigBoundsValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `EncryptedBlobStorageTests.tests` | `InProcess/EncryptedBlobStorageTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `CrossReplicaKeyDestructionTests.tests` | `InProcess/CrossReplicaKeyDestructionTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `LocalStorageEncryptionValidatorTests.tests` | `InProcess/LocalStorageEncryptionValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `EncryptedSecretStoreModeValidatorTests.tests` | `InProcess/EncryptedSecretStoreModeValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `OAuthStateStoreInstanceValidatorTests.tests` | `InProcess/OAuthStateStoreInstanceValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `IdempotencyStoreInstanceValidatorTests.tests` | `InProcess/IdempotencyStoreInstanceValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `RateLimitModeValidatorTests.tests` | `InProcess/RateLimitModeValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `RateLimitConfigValidatorTests.tests` | `InProcess/RateLimitConfigValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `LocalSecretFilePermissionsValidatorTests.tests` | `InProcess/LocalSecretFilePermissionsValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `IdentitySanitiserTests.tests` | `InProcess/IdentitySanitiserTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `I18nInfrastructureTests.tests` | `InProcess/I18nInfrastructureTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `UserClaimsTests.tests` | `InProcess/UserClaimsTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Config | `SurfaceCoherenceValidatorTests.tests` | `InProcess/SurfaceCoherenceValidatorTests.fs` | uses `ToolUp.Platform.Server.Config` |
| Platform.Scope | `IGrantConsentStoreContract.tests` | `Contracts/IGrantConsentStoreContract.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ShareTokenSigningKeyProvenanceValidatorTests.tests` | `InProcess/ShareTokenSigningKeyProvenanceValidatorTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ShareTokenSigningKeyGovernanceTests.tests` | `InProcess/ShareTokenSigningKeyGovernanceTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `PermissionStoreTests.tests` | `InProcess/PermissionStoreTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `StorageScopeResolverTests.tests` | `InProcess/StorageScopeResolverTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `AuthorizationTests.tests` | `InProcess/AuthorizationTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `GoogleIdentityCompanionTests.tests` | `InProcess/GoogleIdentityCompanionTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ShareTokenStoreTests.tests` | `InProcess/ShareTokenStoreTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ShareTokenStoreTests.readPathTests` | `InProcess/ShareTokenStoreTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ShareTokenAuthMiddlewareTests.tests` | `InProcess/ShareTokenAuthMiddlewareTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ShareTokenMiddlewareRateLimitTests.tests` | `InProcess/ShareTokenMiddlewareRateLimitTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ServiceAccountStoreTests.tests` | `InProcess/ServiceAccountStoreTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ServiceAccountStoreTests.pureTests` | `InProcess/ServiceAccountStoreTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ServiceAccountStoreTests.persistenceTests` | `InProcess/ServiceAccountStoreTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `AnonymousSessionMigrationTests.tests` | `InProcess/AnonymousSessionMigrationTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `StoreIdSanitisingTests.tests` | `InProcess/StoreIdSanitisingTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `HeaderAuthProviderModeValidatorTests.tests` | `InProcess/HeaderAuthProviderModeValidatorTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `MultiInstanceAdminCoherenceValidatorTests.tests` | `InProcess/MultiInstanceAdminCoherenceValidatorTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `OidcAudienceBindingValidatorTests.tests` | `InProcess/OidcAudienceBindingValidatorTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `OidcClaimMappingAdvisoryValidatorTests.tests` | `InProcess/OidcClaimMappingAdvisoryValidatorTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `InviteEmailCapabilityValidatorTests.tests` | `InProcess/InviteEmailCapabilityValidatorTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `SecurityHardeningTests.tests` | `InProcess/SecurityHardeningTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `SecurityHardeningTests.cspMiddlewareTests` | `InProcess/SecurityHardeningTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ForwardedHeadersTrustValidatorTests.tests` | `InProcess/ForwardedHeadersTrustValidatorTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `PeerBearerAuthTests.tests` | `InProcess/PeerBearerAuthTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `PermissionStoreFailClosedTests.tests` | `InProcess/PermissionStoreFailClosedTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ActionAuthorizerTests.tests` | `InProcess/ActionAuthorizerTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `HostCapabilityRegistryTests.tests` | `InProcess/HostCapabilityRegistryTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ModuleExposureMigrationTests.tests` | `InProcess/ModuleExposureMigrationTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `AdminMutationApprovalTests.tests` | `InProcess/AdminMutationApprovalTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `GrantConsentTests.tests` | `InProcess/GrantConsentTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `MembershipDoctorTests.tests` | `InProcess/MembershipDoctorTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `SessionRegistryTests.tests` | `InProcess/SessionRegistryTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `SessionRegistryTests.derivationTests` | `InProcess/SessionRegistryTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `SessionRegistryTests.blobBackedTests` | `InProcess/SessionRegistryTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `BuiltInModuleSurfaceTests.tests` | `InProcess/BuiltInModuleSurfaceTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ModuleVisibilityContractTests.resolutionTests` | `InProcess/ModuleVisibilityContractTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ModuleVisibilityContractTests.ungovernedTests` | `InProcess/ModuleVisibilityContractTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ModuleVisibilityContractTests.scopeWalkTests` | `InProcess/ModuleVisibilityContractTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ModuleVisibilityContractTests.foldTests` | `InProcess/ModuleVisibilityContractTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `ModuleVisibilityContractTests.routeRegistryTests` | `InProcess/ModuleVisibilityContractTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `AnonymousSessionMigratorTests.tests` | `InProcess/AnonymousSessionMigratorTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `DefaultSubjectResolverTests.tests` | `InProcess/DefaultSubjectResolverTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `CsrfCarveOutDerivationTests.tests` | `InProcess/CsrfCarveOutDerivationTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `SurfaceEnforcementMiddlewareTests.tests` | `InProcess/SurfaceEnforcementMiddlewareTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `AnonymousSessionMigrationMiddlewareTests.tests` | `InProcess/AnonymousSessionMigrationMiddlewareTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `PeerAuthPostureTests.ladderTests` | `InProcess/PeerAuthPostureTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `PeerAuthPostureTests.overlapTests` | `InProcess/PeerAuthPostureTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `PeerAuthPostureTests.advisoryTests` | `InProcess/PeerAuthPostureTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `FailClosedDispatchTests.tests` | `InProcess/FailClosedDispatchTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Scope | `Phase971PlatformTeamsTests.tests` | `InProcess/Phase971PlatformTeamsTests.fs` | uses `ToolUp.Platform.Server.Scope` |
| Platform.Jobs | `DatasetAssemblyTests.tests` | `InProcess/DatasetAssemblyTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `DatasetRevintageTests.tests` | `InProcess/DatasetRevintageTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `ProvenanceLabelTests.tests` | `InProcess/ProvenanceLabelTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `ModelPromotionPolicyTests.tests` | `InProcess/ModelPromotionPolicyTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `ModelRegistrationObserverTests.tests` | `InProcess/ModelRegistrationObserverTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `IUserSchemaStoreContract.tests` | `Contracts/IUserSchemaStoreContract.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `INotificationPreferenceStoreContract.tests` | `Contracts/INotificationPreferenceStoreContract.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `JobStoreTests.tests` | `InProcess/JobStoreTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `CronExpressionTests.tests` | `InProcess/CronExpressionTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `DataIngestorTests.tests` | `InProcess/DataIngestorTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `OAuthSubstrateTests.stateStoreTests` | `InProcess/OAuthSubstrateTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `OAuthSubstrateTests.credentialFlowTests` | `InProcess/OAuthSubstrateTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `OAuthSubstrateTests.pkceCredentialFlowTests` | `InProcess/OAuthSubstrateTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `OAuthSubstrateTests.pkceFlowTests` | `InProcess/OAuthSubstrateTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `OAuthSubstrateTests.refresherScrubTests` | `InProcess/OAuthSubstrateTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `WebhookSecretRotationTests.tests` | `InProcess/WebhookSecretRotationTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `WebhookSecretMigrationTests.tests` | `InProcess/WebhookSecretMigrationTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `WebhookFailureStateLeaseTests.tests` | `InProcess/WebhookFailureStateLeaseTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `CronTickElectionTests.tests` | `InProcess/CronTickElectionTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `WebhookDispatcherTests.tests` | `InProcess/WebhookDispatcherTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `PresenceChannelTests.tests` | `InProcess/PresenceChannelTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `IPresenceTrackerContract.tests` | `Contracts/IPresenceTrackerContract.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `IEntityLockStoreContract.tests` | `Contracts/IEntityLockStoreContract.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `ICrdtDocumentStoreContract.tests` | `Contracts/ICrdtDocumentStoreContract.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `NotificationAddressBookTests.tests` | `InProcess/NotificationAddressBookTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `ExternalContactTests.tests` | `InProcess/ExternalContactTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `SSEHandshakeTests.tests` | `InProcess/SSEHandshakeTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `WebhookSecretRotationBroadcastTests.tests` | `InProcess/WebhookSecretRotationBroadcastTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `IDistributedLockContract.tests` | `Contracts/IDistributedLockContract.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `UsageLogTests.tests` | `InProcess/UsageLogTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `SecureByDefaultValidatorTests.tests` | `InProcess/SecureByDefaultValidatorTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `InProcessOAuthTokenRefresherTests.tests` | `InProcess/InProcessOAuthTokenRefresherTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `SseTraceContributorTests.tests` | `InProcess/SseTraceContributorTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `SsePerConnectionWriterTests.tests` | `InProcess/SsePerConnectionWriterTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `JobSchedulerInstanceValidatorTests.tests` | `InProcess/JobSchedulerInstanceValidatorTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `NotificationChannelInstanceValidatorTests.tests` | `InProcess/NotificationChannelInstanceValidatorTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `NotificationsExplicitOffTests.tests` | `InProcess/NotificationsExplicitOffTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `SseAuthModeValidatorTests.tests` | `InProcess/SseAuthModeValidatorTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `SmokeTestDefaultsTests.tests` | `InProcess/SmokeTestDefaultsTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `GrantNotificationTests.tests` | `InProcess/GrantNotificationTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `JobProgressTests.tests` | `InProcess/JobProgressTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Jobs | `StoreFireAndForgetTests.tests` | `InProcess/StoreFireAndForgetTests.fs` | uses `ToolUp.Platform.Server.Jobs` |
| Platform.Audit | `SignedExportTests.tests` | `InProcess/SignedExportTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `MinimumViableShapeTests.tests` | `InProcess/MinimumViableShapeTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `InMemoryAuditSinkTests.tests` | `InProcess/InMemoryAuditSinkTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditReplicatorTests.tests` | `InProcess/AuditReplicatorTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `Ed25519ArtifactSubstrateTests.tests` | `InProcess/Ed25519ArtifactSubstrateTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `InMemoryPendingInviteStoreTests.tests` | `InProcess/InMemoryPendingInviteStoreTests.fs` | uses `CacheReset`, which reaches Audit |
| Platform.Audit | `BlobPendingInviteStoreTests.tests` | `InProcess/BlobPendingInviteStoreTests.fs` | uses `CacheReset`, which reaches Audit |
| Platform.Audit | `BlobPendingInviteStoreTests.validatorGateTests` | `InProcess/BlobPendingInviteStoreTests.fs` | uses `CacheReset`, which reaches Audit |
| Platform.Audit | `AuditLogModeValidatorTests.tests` | `InProcess/AuditLogModeValidatorTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `EventStoreChainValidatorTests.tests` | `InProcess/EventStoreChainValidatorTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditLogHealthCheckTests.tests` | `InProcess/AuditLogHealthCheckTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditWriteFailureMetricTests.tests` | `InProcess/AuditWriteFailureMetricTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditFailurePolicyTests.tests` | `InProcess/AuditFailurePolicyTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `PermissionAuditChainTests.tests` | `InProcess/PermissionAuditChainTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuthAuditHookTests.tests` | `InProcess/AuthAuditHookTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `HostActionAuditTests.tests` | `InProcess/HostActionAuditTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `NotificationSilentlySkippedTests.tests` | `InProcess/NotificationSilentlySkippedTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `ConversationExportAuditTests.tests` | `InProcess/ConversationExportAuditTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `ConversationExporterTests.tests` | `InProcess/ConversationExporterTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditStreamKeyTests.tests` | `InProcess/AuditStreamKeyTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `InProcessRateLimiterTests.tests` | `InProcess/InProcessRateLimiterTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `ConsentStateStoreTests.tests` | `InProcess/ConsentStateStoreTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `ConsentStateStoreTests.entityBackedTests` | `InProcess/ConsentStateStoreTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `ConsentStateStoreTests.restartPersistenceTests` | `InProcess/ConsentStateStoreTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AdAnalyticsObservabilityTests.tests` | `InProcess/AdAnalyticsObservabilityTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditViewApiHandlerTests.roleGateTests` | `InProcess/AuditViewApiHandlerTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditViewApiHandlerTests.scopeIsolationTests` | `InProcess/AuditViewApiHandlerTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditViewApiHandlerTests.filterTests` | `InProcess/AuditViewApiHandlerTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditViewApiHandlerTests.pagingTests` | `InProcess/AuditViewApiHandlerTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditViewApiHandlerTests.exportTests` | `InProcess/AuditViewApiHandlerTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `AuditViewApiHandlerTests.substratePairingTests` | `InProcess/AuditViewApiHandlerTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `EvidenceEnumerationCompletenessTests.derivationTests` | `InProcess/EvidenceEnumerationCompletenessTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `EvidenceEnumerationCompletenessTests.missingPositionTests` | `InProcess/EvidenceEnumerationCompletenessTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `EvidenceEnumerationCompletenessTests.boundedTests` | `InProcess/EvidenceEnumerationCompletenessTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `EvidenceEnumerationCompletenessTests.shorterRenderTests` | `InProcess/EvidenceEnumerationCompletenessTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `EvidenceEnumerationCompletenessTests.bundleStatementTests` | `InProcess/EvidenceEnumerationCompletenessTests.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `IEgressGateContract.tests` | `Contracts/IEgressGateContract.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.Audit | `IEvidencePackGeneratorContract.tests` | `Contracts/IEvidencePackGeneratorContract.fs` | uses `ToolUp.Platform.Server.Audit` |
| Platform.PublicApi | `PublicApiApprovalTests.tests` | `InProcess/PublicApiApprovalTests.fs` | reaches no server part |
| Platform | `ConfigMigrationTests.tests` | `InProcess/ConfigMigrationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `SeedDataLoaderTests.tests` | `InProcess/SeedDataLoaderTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ParquetDatasetCodecTests.contractTests` | `InProcess/ParquetDatasetCodecTests.fs` | uses `ToolUp.DataSources.Parquet` |
| Platform | `ParquetDatasetCodecTests.codecTests` | `InProcess/ParquetDatasetCodecTests.fs` | uses `ToolUp.DataSources.Parquet` |
| Platform | `IModelFitProviderContract.tests` | `Contracts/IModelFitProviderContract.fs` | uses `ToolUp.ModelProviders.Reference` |
| Platform | `IModelFitProviderContract.opacityTests` | `Contracts/IModelFitProviderContract.fs` | uses `ToolUp.ModelProviders.Reference` |
| Platform | `IModelScorerContract.tests` | `Contracts/IModelScorerContract.fs` | uses `ToolUp.ModelProviders.Reference` |
| Platform | `IModelEvaluationPlanMetricsContract.tests` | `Contracts/IModelEvaluationPlanMetricsContract.fs` | uses `ToolUp.ModelProviders.Reference` |
| Platform | `IExternalComputeDispatcherContract.tests` | `Contracts/IExternalComputeDispatcherContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IExternalComputeDispatcherContract.wireTests` | `Contracts/IExternalComputeDispatcherContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IExternalComputeDispatcherContract.portabilityAudit` | `Contracts/IExternalComputeDispatcherContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IExternalComputeDispatcherContract.conformanceTests` | `Contracts/IExternalComputeDispatcherContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IExternalComputeDispatcherContract.selfTests` | `Contracts/IExternalComputeDispatcherContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModelEvaluationTests.tests` | `InProcess/ModelEvaluationTests.fs` | uses `ToolUp.ModelProviders.Reference` |
| Platform | `ModelPromotionTransferTests.tests` | `InProcess/ModelPromotionTransferTests.fs` | uses `TestScope`, which reaches the umbrella |
| Platform | `RunJobTests.tests` | `InProcess/RunJobTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `ModelFitBatchTests.tests` | `InProcess/ModelFitBatchTests.fs` | uses `ToolUp.ModelProviders.Reference` |
| Platform | `ModelExecutionApiTests.tests` | `InProcess/ModelExecutionApiTests.fs` | uses `ToolUp.ModelProviders.Reference` |
| Platform | `ModelExecutionComposeTests.tests` | `InProcess/ModelExecutionComposeTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IngestionStatusTests.tests` | `InProcess/IngestionStatusTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `IngestionRetryTests.tests` | `InProcess/IngestionRetryTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `IngestionEmbedderRetryTests.tests` | `InProcess/IngestionEmbedderRetryTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `IngestionBackpressureTests.tests` | `InProcess/IngestionBackpressureTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `DurableIngestionQueueTests.tests` | `InProcess/DurableIngestionQueueTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `MetricRegistryTests.tests` | `InProcess/MetricRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.tests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.defaultWriterObligationTests` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.preflightTests` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.tests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.provenanceTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.provenanceTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.provenanceTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.provenanceTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.decoratorTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.decoratorTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.exclusivityTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.exclusivityTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.exclusivityTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.exclusivityTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.runOptionsTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.runOptionsTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.runOptionsTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.runOptionsTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableOrphanSweepContract.tests …` | `Contracts/IFactTableOrphanSweepContract.fs` | uses `ToolUp.Facts.Server` |
| Platform | `IFactTableOrphanSweepContract.tests …` | `Contracts/IFactTableOrphanSweepContract.fs` | uses `ToolUp.Facts.Server` |
| Platform | `IFactTableWriterContract.runRecordTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.runRecordTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IFactTableWriterContract.runRecordTests …` | `Contracts/IFactTableWriterContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FactTableRunTests.tests` | `InProcess/FactTableRunTests.fs` | uses `TestScope`, which reaches the umbrella |
| Platform | `IFactTableOrphanSweepContract.tests …` | `Contracts/IFactTableOrphanSweepContract.fs` | uses `ToolUp.Facts.Server` |
| Platform | `CanonicalFrameTests.tests` | `InProcess/CanonicalFrameTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FactStoreTests.tests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactWriteTests.tests` | `InProcess/FactWriteTests.fs` | names `ToolUp.Facts.Server` at run time |
| Platform | `FactStoreTests.factIdTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.auditAndFreshnessTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.batchAssertTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.populationRegistryTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.populationScaleTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.surfaceTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.surfacePopulationRegistryTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.metricSurfaceTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.metricSurfaceScaleTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.indexTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.indexPopulationRegistryTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.pointReadIndexTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactStoreTests.taintIndexSourceTests` | `InProcess/FactStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `CanonicalMethodTests.tests` | `InProcess/CanonicalMethodTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactInvalidationTests.tests` | `InProcess/FactInvalidationTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `CoherenceCheckTests.tests` | `InProcess/CoherenceCheckTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ProvenanceGraphTests.tests` | `InProcess/ProvenanceGraphTests.fs` | uses `TestScope`, which reaches the umbrella |
| Platform | `ProvenanceGraphWireTests.tests` | `InProcess/ProvenanceGraphWireTests.fs` | uses `TestScope`, which reaches the umbrella |
| Platform | `GroundingWiringTests.tests` | `InProcess/GroundingWiringTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `JobSchedulerTests.tests` | `InProcess/JobSchedulerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ScheduledJobDeclarationTests.tests` | `InProcess/ScheduledJobDeclarationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleQueryBusTests.tests` | `InProcess/ModuleQueryBusTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `SecretStoreAtRestPostureValidatorTests.tests` | `InProcess/SecretStoreAtRestPostureValidatorTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DataProtectionBackendTests.tests` | `InProcess/DataProtectionBackendTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PresenceApiTests.tests` | `InProcess/PresenceApiTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AuthProviderTests.tests` | `InProcess/AuthProviderTests.fs` | uses `ToolUp.AuthProviders.Oidc` |
| Platform | `OidcJwksTtlTests.tests` | `InProcess/OidcJwksTtlTests.fs` | uses `ToolUp.AuthProviders.Oidc` |
| Platform | `AuditTests.tests` | `InProcess/AuditTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `RateLimitTests.tests` | `InProcess/RateLimitTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `IdempotencyTests.tests` | `InProcess/IdempotencyTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `ReportingComposeTests.tests` | `InProcess/ReportingComposeTests.fs` | uses `ToolUp.Reporting.Server` |
| Platform | `ReportingAuthorizationTests.tests` | `InProcess/ReportingAuthorizationTests.fs` | uses `ToolUp.Reporting.Server` |
| Platform | `DeckExportSeamTests.tests` | `InProcess/DeckExportSeamTests.fs` | uses `ToolUp.Reporting.Server` |
| Platform | `DeckExportSeamTests.bundleTests` | `InProcess/DeckExportSeamTests.fs` | uses `ToolUp.Reporting.Server` |
| Platform | `DataSubjectRequestTests.tests` | `InProcess/DataSubjectRequestTests.fs` | uses `IDataSubjectRequestContract`, which reaches the umbrella |
| Platform | `DataSubjectRequestTests.authorizationTests` | `InProcess/DataSubjectRequestTests.fs` | uses `IDataSubjectRequestContract`, which reaches the umbrella |
| Platform | `PostgresFactStoreTests.offlineTests` | `InProcess/PostgresFactStoreTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PostgresFactStoreTests.tests` | `InProcess/PostgresFactStoreTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TransactionalDispatcherTests.tests` | `InProcess/TransactionalDispatcherTests.fs` | a `[<Tests>]` file `WhatsAppChannelTests` uses, so it moves with it |
| Platform | `NotificationPreferenceTests.tests` | `InProcess/NotificationPreferenceTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `WhatsAppChannelTests.tests` | `InProcess/WhatsAppChannelTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HnswVectorStoreTests.tests` | `InProcess/HnswVectorStoreTests.fs` | uses `ToolUp.VectorStores.Hnsw` |
| Platform | `PgvectorVectorStoreTests.tests` | `InProcess/PgvectorVectorStoreTests.fs` | uses `ToolUp.VectorStores.Pgvector` |
| Platform | `PostgresFullTextIndexTests.tests` | `InProcess/PostgresFullTextIndexTests.fs` | uses `ToolUp.VectorStores.Pgvector` |
| Platform | `SparseIndexCompositionTests.tests` | `InProcess/SparseIndexCompositionTests.fs` | uses `ToolUp.VectorStores.Pgvector` |
| Platform | `RedisEmbeddingCacheTests.tests` | `InProcess/RedisEmbeddingCacheTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `LocalEmbeddingScopeTests.scopeKeyTests` | `InProcess/LocalEmbeddingScopeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `LocalEmbeddingScopeTests.isolationTests` | `InProcess/LocalEmbeddingScopeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `LocalEmbeddingScopeTests.resetTests` | `InProcess/LocalEmbeddingScopeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `LocalEmbeddingScopeTests.persistenceTests` | `InProcess/LocalEmbeddingScopeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `LocalEmbeddingScopeTests.backwardCompatibilityTests` | `InProcess/LocalEmbeddingScopeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `LocalEmbeddingScopeTests.capabilityProbeTests` | `InProcess/LocalEmbeddingScopeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `LocalEmbeddingScopeTests.cacheKeyingTests` | `InProcess/LocalEmbeddingScopeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `LocalEmbeddingScopeTests.crossScopeRetrievalTests` | `InProcess/LocalEmbeddingScopeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `OcrProviderTests.tests` | `InProcess/OcrProviderTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `ContentScannerTests.tests` | `InProcess/ContentScannerTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KnowledgeUserScopeIsolationTests.tests` | `InProcess/KnowledgeUserScopeIsolationTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `VectorisationHookTests.tests` | `InProcess/VectorisationHookTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `VectorisationHookTests.stagedTests` | `InProcess/VectorisationHookTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `RagTelemetryTests.tests` | `InProcess/RagTelemetryTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `ExtractorTests.tests` | `InProcess/ExtractorTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `TerminalStatusAnnouncementTests.tests` | `InProcess/IngestionStatusStreamTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `ConfigValidatorAggregatorTests.tests` | `InProcess/ConfigValidatorAggregatorTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ConfigValidatorSecurityClassTests.tests` | `InProcess/ConfigValidatorSecurityClassTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PreflightRuleClassTests.tests` | `InProcess/PreflightRuleClassTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CorsCredentialsWildcardBootTests.tests` | `InProcess/CorsCredentialsWildcardBootTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComponentIdentityTests.tests` | `InProcess/ComponentIdentityTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionManifestTests.tests` | `InProcess/CompositionManifestTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HostedServiceGateTests.tests` | `InProcess/HostedServiceGateTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `BootstrapRefusalTests.tests` | `InProcess/BootstrapRefusalTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionDiffTests.tests` | `InProcess/CompositionDiffTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ToolUp.Platform.Tests.Composition.CompositionBaselineTests.tests` | `Composition/CompositionBaselineTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `CompanionCapabilityTests.tests` | `InProcess/CompanionCapabilityTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionCapabilityGateTests.tests` | `InProcess/CompositionCapabilityGateTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `SeamAuthorityTests.tests` | `InProcess/SeamAuthorityTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `SeamAuthorityEnforcementTests.tests` | `InProcess/SeamAuthorityEnforcementTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `SeamAwareServiceProviderTests.tests` | `InProcess/SeamAwareServiceProviderTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionValidatorTests.tests` | `InProcess/CompositionValidatorTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `InvariantRuleManifestTests.tests` | `InProcess/InvariantRuleManifestTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleGraphRuleTests.tests` | `InProcess/ModuleGraphRuleTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `RuleVersioningTests.tests` | `InProcess/RuleVersioningTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DataVocabularyTests.tests` | `InProcess/DataVocabularyTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComponentIdCorrelationTests.tests` | `InProcess/ComponentIdCorrelationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComponentConfigTests.tests` | `InProcess/ComponentConfigTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComponentRequirementsTests.tests` | `InProcess/ComponentRequirementsTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComposableSurfaceTests.tests` | `InProcess/ComposableSurfaceTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionProfileSurfaceTests.tests` | `InProcess/CompositionProfileSurfaceTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EventTopologyTests.tests` | `InProcess/EventTopologyTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DataFootprintTests.tests` | `InProcess/DataFootprintTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ScaleReadinessTests.tests` | `InProcess/ScaleReadinessTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ApplianceProfileTests.tests` | `InProcess/ApplianceProfileTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EntitlementTokenTests.tests` | `InProcess/EntitlementTokenTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AuthorizationSurfaceTests.tests` | `InProcess/AuthorizationSurfaceTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleSurfaceTests.tests` | `InProcess/ModuleSurfaceTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleBuilderSemanticsTests.tests` | `InProcess/ModuleBuilderSemanticsTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionGroundingTests.tests` | `InProcess/CompositionGroundingTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComponentProvenanceTests.tests` | `InProcess/ComponentProvenanceTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HostEnvelopeTests.tests` | `InProcess/HostEnvelopeTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComponentHealthRollupTests.tests` | `InProcess/ComponentHealthRollupTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ResourceEnvelopeTests.tests` | `InProcess/ResourceEnvelopeTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComponentLifecycleTests.tests` | `InProcess/ComponentLifecycleTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionHotSwapTests.tests` | `InProcess/CompositionHotSwapTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionDescriptorTests.tests` | `InProcess/CompositionDescriptorTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionDescriptorVersionTests.tests` | `InProcess/CompositionDescriptorVersionTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DescriptorCompletenessTests.tests` | `InProcess/DescriptorCompletenessTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionDryRunTests.tests` | `InProcess/CompositionDryRunTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionUpgradePlanTests.tests` | `InProcess/CompositionUpgradePlanTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TenantCompositionPresetTests.tests` | `InProcess/TenantCompositionPresetTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ConfigReferenceTests.tests` | `InProcess/ConfigReferenceTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `ConfigStartupModeTests.tests` | `InProcess/ConfigStartupModeTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ConfigResolverTests.tests` | `InProcess/ConfigResolverTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ConfigProfileTests.tests` | `InProcess/ConfigProfileTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AlertRuleEngineTests.tests` | `InProcess/AlertRuleEngineTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ServiceStatusBoardApiHandlerTests.tests` | `InProcess/ServiceStatusBoardApiHandlerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DeploymentReadinessReportTests.tests` | `InProcess/DeploymentReadinessReportTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `env-mutating config validators (Phase 653)` group: AIProviderEnvValidatorTests.providerTests, AIProviderEnvValidatorTests.modelTests, AIProviderProbeValidatorTests.tests, OidcAuthValidatorTimeoutTests.tests` | `InProcess/AIProviderEnvValidatorTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AIProviderHealthTests.claudeTests` | `InProcess/AIProviderHealthTests.fs` | uses `ToolUp.AIProviders.OpenAI` |
| Platform | `AIProviderHealthTests.openAiTests` | `InProcess/AIProviderHealthTests.fs` | uses `ToolUp.AIProviders.OpenAI` |
| Platform | `RedactionAllowlistParityTests.tests` | `InProcess/RedactionAllowlistParityTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `OidcSignInContractTests.tests` | `InProcess/OidcSignInContractTests.fs` | uses `ToolUp.AuthProviders.Oidc` |
| Platform | `OidcBearerStrategyTests.tests` | `InProcess/OidcBearerStrategyTests.fs` | uses `ToolUp.AuthProviders.Oidc` |
| Platform | `GitHubAuthProviderTests.tests` | `InProcess/GitHubAuthProviderTests.fs` | uses `ToolUp.AuthProviders.GitHub` |
| Platform | `GitHubAppFlowTests.tests` | `InProcess/GitHubAppFlowTests.fs` | uses `ToolUp.DataSources.GitHub` |
| Platform | `GoogleAnalyticsTests.tests` | `InProcess/GoogleAnalyticsTests.fs` | uses `ToolUp.DataSources.GoogleAnalytics` |
| Platform | `LdapAuthProviderTests.tests` | `InProcess/LdapAuthProviderTests.fs` | uses `ToolUp.AuthProviders.LdapActiveDirectory` |
| Platform | `ScimProvisioningTests.tests` | `InProcess/ScimProvisioningTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PasskeyAuthProviderTests.tests` | `InProcess/PasskeyAuthProviderTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PerScopeKeyResolverWiringTests.unwiredDestroyTests` | `InProcess/PerScopeKeyResolverWiringTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PerScopeKeyResolverWiringTests.wiringValidatorTests` | `InProcess/PerScopeKeyResolverWiringTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PerScopeKeyResolverWiringTests.wiringDiagnosticsTests` | `InProcess/PerScopeKeyResolverWiringTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PerScopeKeyResolverWiringTests.keyMaterialTests` | `InProcess/PerScopeKeyResolverWiringTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TenantLifecycleAggregatorTests.tests` | `InProcess/TenantLifecycleAggregatorTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `LifecycleSummaryStoreTests.tests` | `InProcess/LifecycleSummaryStoreTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `OffboardConfirmationTests.tests` | `InProcess/OffboardConfirmationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ScheduledDeprovisionTests.tests` | `InProcess/ScheduledDeprovisionTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PrincipalRegistryTests.tests` | `InProcess/PrincipalRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ITenantLifecycleContract.tests` | `Contracts/ITenantLifecycleContract.fs` | uses `ToolUp.RAG.Server` |
| Platform | `ILifecycleLockContract.tests` | `Contracts/ILifecycleLockContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `BlobEntityStoreTests.tests` | `InProcess/BlobEntityStoreTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ConditionalDataObjectStoreTests.tests` | `InProcess/ConditionalDataObjectStoreTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `EntityQueryTests.tests` | `InProcess/EntityQueryTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `EntityGraphProjectionTests.tests` | `Graph/EntityGraphProjectionTests.fs` | uses `ToolUp.Graph.Projection` |
| Platform | `OtelActivitySinkTests.tests` | `InProcess/OtelActivitySinkTests.fs` | uses `ToolUp.Metrics.OpenTelemetry` |
| Platform | `ServerModuleMetricsTests.tests` | `InProcess/ServerModuleMetricsTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ObservabilityHandlersTests.tests` | `InProcess/ObservabilityHandlersTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ObservabilityHandlersTests.datadogReadbackTests` | `InProcess/ObservabilityHandlersTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AnonymousSessionBindingTests.tests` | `InProcess/AnonymousSessionBindingTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `BlobCorruptionChaosTests.tests` | `InProcess/BlobCorruptionChaosTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `BlobPlatformAIKeyStoreTests.tests` | `InProcess/BlobPlatformAIKeyStoreTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `MultiPlatformProviderResolutionTests.tests` | `AI/MultiPlatformProviderResolutionTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AIProviderFallbackTests.tests` | `InProcess/AIProviderFallbackTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `PlatformAIKeysHandlerRbacTests.tests` | `AI/PlatformAIKeysHandlerRbacTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AISurfaceCapabilityTests.tests` | `AI/AISurfaceCapabilityTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AIStreamFramingPinTests.tests` | `AI/AIStreamFramingPinTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AIChatWorkerTests.tests` | `AI/AIChatWorkerTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AITokenBudgetTests.tests` | `AI/AITokenBudgetTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AIProviderModelOverrideTests.tests` | `AI/AIProviderModelOverrideTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AISpendBudgetTests.tests` | `InProcess/AISpendBudgetTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `ConversationReplayStreamTests.tests` | `AI/ConversationReplayStreamTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `IngestionStatusStreamTests.tests` | `AI/IngestionStatusStreamTests.fs` | uses `ToolUp.KnowledgeBase.Server` |
| Platform | `ToolOutcomeTests.tests` | `AI/ToolOutcomeTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `ConversationRecordTests.tests` | `AI/ConversationRecordTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AgentLoopRailsTests.tests` | `AI/AgentLoopRailsTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `TurnOutcomeStatusTests.tests` | `AI/TurnOutcomeStatusTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `ProviderProfileApiTests.tests` | `InProcess/ProviderProfileApiTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamProviderTests.tests` | `InProcess/TeamProviderTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ProviderOAuthFlowTests.claudeOAuthContractTests` | `InProcess/ProviderOAuthFlowTests.fs` | uses `ToolUp.AIProviders.OpenAI` |
| Platform | `ProviderOAuthFlowTests.openAiOAuthContractTests` | `InProcess/ProviderOAuthFlowTests.fs` | uses `ToolUp.AIProviders.OpenAI` |
| Platform | `ProviderOAuthFlowTests.claudeProviderFlowContractTests` | `InProcess/ProviderOAuthFlowTests.fs` | uses `ToolUp.AIProviders.OpenAI` |
| Platform | `ProviderOAuthFlowTests.openAiProviderFlowContractTests` | `InProcess/ProviderOAuthFlowTests.fs` | uses `ToolUp.AIProviders.OpenAI` |
| Platform | `ProviderOAuthFlowTests.tests` | `InProcess/ProviderOAuthFlowTests.fs` | uses `ToolUp.AIProviders.OpenAI` |
| Platform | `AuditEventRegistryTests.tests` | `InProcess/AuditEventRegistryTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `AICancellationDispatchInstanceValidatorTests.tests` | `InProcess/AICancellationDispatchInstanceValidatorTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AnonymousAIModeValidatorTests.tests` | `InProcess/AnonymousAIModeValidatorTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `SecurityHeadersValidatorTests.tests` | `InProcess/SecurityHeadersValidatorTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `SecurityHeadersValidatorTests.cspNonceCacheValidatorTests` | `InProcess/SecurityHeadersValidatorTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `SecurityHeadersValidatorTests.baselineFloorTests` | `InProcess/SecurityHeadersValidatorTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `InternetReadinessScorecardTests.tests` | `InProcess/InternetReadinessScorecardTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ForwardedHeadersTrustTests.tests` | `InProcess/ForwardedHeadersTrustTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FileManagementTests.tests` | `InProcess/FileManagementTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleApiFactoryTests.tests` | `InProcess/ModuleApiFactoryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FastPathBeaconOwnershipTests.tests` | `InProcess/FastPathBeaconOwnershipTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `FastPathBeaconIdempotencyTests.tests` | `InProcess/FastPathBeaconIdempotencyTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `FastPathSequencerTelemetryTests.tests` | `InProcess/FastPathSequencerTelemetryTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `FastPathTriageResolverTests.tests` | `InProcess/FastPathTriageResolverTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `HnswFidelityTests.tests` | `InProcess/HnswFidelityTests.fs` | uses `ToolUp.VectorStores.Hnsw` |
| Platform | `ScopeResolutionFailClosedTests.tests` | `InProcess/ScopeResolutionFailClosedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AccessContextFactoryTests.tests` | `InProcess/AccessContextFactoryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ScopeKeyTests.tests` | `InProcess/ScopeKeyTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IExternalApiConnectorContract.kitTests` | `Contracts/IExternalApiConnectorContract.fs` | uses `ToolUp.DataSources.Common` |
| Platform | `ExternalApiConnectorBindingTests.tests` | `InProcess/ExternalApiConnectorBindingTests.fs` | uses `ToolUp.DataSources.Common` |
| Platform | `HttpTransportBindingTests.tests` | `InProcess/HttpTransportBindingTests.fs` | uses `ToolUp.DataSources.Common` |
| Platform | `PlatformTestingFrameworkTests.tests` | `InProcess/PlatformTestingFrameworkTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `CitationNormaliserTests.tests` | `InProcess/CitationNormaliserTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `InMemoryBM25IndexTests.tests` | `InProcess/InMemoryBM25IndexTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `IndexLifecycleTests.tests` | `InProcess/IndexLifecycleTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `CrossIndexErasureConformanceTests.tests` | `InProcess/CrossIndexErasureConformanceTests.fs` | uses `ToolUp.SparseIndices.Postgres` |
| Platform | `AIAgentEngineClientResidentAuthorizationTests.tests` | `InProcess/AIAgentEngineClientResidentAuthorizationTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AIAllowlistDiagnosticsTests.tests` | `InProcess/AIAllowlistDiagnosticsTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `DevVersionEndpointTests.tests` | `InProcess/DevVersionEndpointTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `BuildIdentityEndpointTests.tests` | `InProcess/BuildIdentityEndpointTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `ClientToolDispatchContractBindings.tests` | `InProcess/ClientToolDispatchContractBindings.fs` | uses `IClientToolDispatchContract`, which reaches the umbrella |
| Platform | `SampleClientToolDispatchTests.tests` | `InProcess/SampleClientToolDispatchTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `UiAwarenessInspectToolTests.tests` | `InProcess/UiAwarenessInspectToolTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `UiInspectionContractBindings.tests` | `InProcess/UiInspectionContractBindings.fs` | uses `ToolUp.AI.UiAwareness.Server` |
| Platform | `ListActiveActionsToolTests.tests` | `InProcess/ListActiveActionsToolTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `McpHostTests.tests` | `InProcess/McpHostTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HostRenderBoundaryTests.tests` | `InProcess/HostRenderBoundaryTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `HostedTreeUsageExportTests.tests` | `InProcess/HostedTreeUsageExportTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HostedTreeA11yTests.tests` | `InProcess/HostedTreeA11yTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `AccessibilityAssertionsTests.tests` | `InProcess/AccessibilityAssertionsTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `LiveSessionHostTests.tests` | `InProcess/LiveSessionHostTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TreePatchChannelTests.tests` | `InProcess/TreePatchChannelTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `HostStateProjectionTests.tests` | `InProcess/HostStateProjectionTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `HostRouteContractTests.tests` | `InProcess/HostRouteContractTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `PublicRenderingTests.tests` | `InProcess/PublicRenderingTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `SitemapSearchIndexTests.tests` | `InProcess/SitemapSearchIndexTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `PublicEnumerationGateTests.tests` | `InProcess/PublicEnumerationGateTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `NarrativeFromDataTests.tests` | `InProcess/NarrativeFromDataTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `NarrativeFactsTests.tests` | `InProcess/NarrativeFactsTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `NarrativeChartsTests.tests` | `InProcess/NarrativeChartsTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `EnumerableRoutesTests.tests` | `InProcess/EnumerableRoutesTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `NavStructuredDataTests.tests` | `InProcess/NavStructuredDataTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `TagFeedTests.tests` | `InProcess/TagFeedTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `NarrativeDataExportTests.tests` | `InProcess/NarrativeDataExportTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `PaginationTests.tests` | `InProcess/PaginationTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `FacetedBrowseTests.tests` | `InProcess/FacetedBrowseTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `NavComposeTests.tests` | `InProcess/NavComposeTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `SiteStructureTests.tests` | `InProcess/SiteStructureTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `RenderCacheTests.tests` | `InProcess/RenderCacheTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `RenderCoalescingTests.tests` | `InProcess/RenderCoalescingTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `ResolvedContentTests.tests` | `InProcess/ResolvedContentTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `IndexNowTests.tests` | `InProcess/IndexNowTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `MultiSiteTests.tests` | `InProcess/MultiSiteTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `StructuredDataConformanceTests.tests` | `InProcess/StructuredDataConformanceTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `GatedSsrTests.tests` | `InProcess/GatedSsrTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `DirectoryRoleApiGrantTests.tests` | `InProcess/DirectoryRoleApiGrantTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `KnowledgeSurfaceTests.tests` | `InProcess/KnowledgeSurfaceTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `KnowledgeOriginalRetrievalTests.tests` | `InProcess/KnowledgeOriginalRetrievalTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KnowledgeSignedOriginalUrlTests.tests` | `InProcess/KnowledgeSignedOriginalUrlTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KnowledgeScopeResetAuditTests.tests` | `InProcess/KnowledgeScopeResetAuditTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `RAGVacuumJobHandlerTests.tests` | `InProcess/RAGVacuumJobHandlerTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `ToolAwareRagFramingTests.tests` | `InProcess/ToolAwareRagFramingTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `FactFirstRetrievalTests.tests` | `InProcess/FactFirstRetrievalTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `AnswerVerifierTests.tests` | `InProcess/AnswerVerifierTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `DisclosureEgressTests.tests` | `InProcess/DisclosureEgressTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `DisclosureExportWebhookTests.tests` | `InProcess/DisclosureExportWebhookTests.fs` | uses `ToolUp.Reporting.Server` |
| Platform | `DisclosureTaintTests.tests` | `InProcess/DisclosureTaintTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `MultiPartyDisclosureTests.tests` | `InProcess/MultiPartyDisclosureTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `DeclassificationBudgetTests.tests` | `InProcess/DeclassificationBudgetTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DeclassificationBudgetAmendmentTests.subjectTests` | `InProcess/DeclassificationBudgetAmendmentTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DeclassificationBudgetAmendmentTests.applicationTests` | `InProcess/DeclassificationBudgetAmendmentTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DeclassificationBudgetAmendmentTests.loweringTests` | `InProcess/DeclassificationBudgetAmendmentTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DeclassificationBudgetAmendmentTests.arcTests` | `InProcess/DeclassificationBudgetAmendmentTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DeclassificationBudgetAmendmentTests.costTests` | `InProcess/DeclassificationBudgetAmendmentTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FactResolverComposeTests.tests` | `InProcess/FactResolverComposeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `FactQueryToolTests.tests` | `InProcess/FactQueryToolTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PopulationQueryToolTests.tests` | `InProcess/PopulationQueryToolTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ScopeChokePointTests.tests` | `InProcess/ScopeChokePointTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `ModelInputEntryTests.tests` | `InProcess/ModelInputEntryTests.fs` | names `ToolUp.RAG.Server` at run time |
| Platform | `ModelInputEntryTests.renderTests` | `InProcess/ModelInputEntryTests.fs` | names `ToolUp.RAG.Server` at run time |
| Platform | `CoverageToolTests.tests` | `InProcess/CoverageToolTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CoverageNarrativeTests.tests` | `InProcess/CoverageNarrativeTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AnswerPlannerTests.tests` | `InProcess/AnswerPlannerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FactClauseFeederTests.tests` | `InProcess/FactClauseFeederTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `RagFactTierTests.tests` | `InProcess/RagFactTierTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `GroundedNarrativeTests.tests` | `InProcess/GroundedNarrativeTests.fs` | uses `ToolUp.Reporting.Server` |
| Platform | `NarrativeOffenceHistoryTests.tests` | `InProcess/NarrativeOffenceHistoryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ReportSubscriptionScopeTests.tests` | `InProcess/ReportSubscriptionScopeTests.fs` | uses `ToolUp.Reporting.Server` |
| Platform | `AssistantFactScopeTests.tests` | `InProcess/AssistantFactScopeTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AIToolResultBudgetTests.tests` | `InProcess/AIToolResultBudgetTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AIToolSchemaTests.tests` | `InProcess/AIToolSchemaTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AIToolDispatchRbacTests.tests` | `InProcess/AIToolDispatchRbacTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `ToolApprovalTests.tests` | `InProcess/ToolApprovalTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `GrantPolicyTests.tests` | `InProcess/GrantPolicyTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AccessAttestationTests.tests` | `InProcess/AccessAttestationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `GrantGovernanceTests.tests` | `InProcess/GrantGovernanceTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AIQueryabilityOptInTests.tests` | `InProcess/AIQueryabilityOptInTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AIConsentTests.tests` | `InProcess/AIConsentTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `AICrossModuleAuditTests.tests` | `InProcess/AICrossModuleAuditTests.fs` | uses `ToolUp.AuditSinks.SplunkHec` |
| Platform | `GroundingCertificateTests.tests` | `InProcess/GroundingCertificateTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `CertificateEnvelopeTests.tests` | `InProcess/CertificateEnvelopeTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `GroundingSigningConvergenceTests.tests` | `InProcess/GroundingSigningConvergenceTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactImportTests.tests` | `InProcess/FactImportTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CertificateIssuanceTransparencyTests.tests` | `InProcess/CertificateIssuanceTransparencyTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `DeploymentVerificationReportTests.tests` | `InProcess/DeploymentVerificationReportTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EgressPolicyTests.tests` | `InProcess/EgressPolicyTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HostingBaselineTests.tests` | `InProcess/HostingBaselineTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `RouteEffectContainmentTests.tests` | `InProcess/RouteEffectContainmentTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TrustBoundaryTests.tests` | `InProcess/TrustBoundaryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `StaticCorpusContract.tests` | `RAG/StaticCorpusContract.fs` | uses `ToolUp.RAG.StaticCorpus.Server` |
| Platform | `MetadataFilterContract.tests` | `RAG/MetadataFilterContract.fs` | uses `ToolUp.RAG.StaticCorpus.Server` |
| Platform | `QueryRewriteContract.tests` | `RAG/QueryRewriteContract.fs` | uses `ToolUp.RAG.Server` |
| Platform | `QueryRewriteComposeTests.tests` | `InProcess/QueryRewriteComposeTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `CitationSpanContract.tests` | `RAG/CitationSpanContract.fs` | uses `ToolUp.RAG.Server` |
| Platform | `SparseAnalyzerContract.tests` | `RAG/SparseAnalyzerContract.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KnowledgeUploadPolicyTests.tests` | `InProcess/KnowledgeUploadPolicyTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KnowledgeDedupTests.tests` | `InProcess/KnowledgeDedupTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KbQuotaRetentionTests.tests` | `InProcess/KbQuotaRetentionTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KbVersioningTests.tests` | `InProcess/KbVersioningTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KbVersioningTests.ingestionStatusTruth` | `InProcess/KbVersioningTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KbObjectRetentionTests.tests` | `InProcess/KbObjectRetentionTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KbDocumentTagsTests.tests` | `InProcess/KbDocumentTagsTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `KbBulkImportTests.tests` | `InProcess/KbBulkImportTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `BrandingTests.tests` | `InProcess/BrandingTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FelizLayoutAdapterTests.tests` | `InProcess/FelizLayoutAdapterTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `AssetStoreTests.tests` | `InProcess/AssetStoreTests.fs` | uses `ToolUp.AssetStore` |
| Platform | `AssetStoreTests.rendererContract` | `InProcess/AssetStoreTests.fs` | uses `ToolUp.AssetStore` |
| Platform | `AssetStoreTests.phase967Tests` | `InProcess/AssetStoreTests.fs` | uses `ToolUp.AssetStore` |
| Platform | `EdgeCacheTests.tests` | `InProcess/EdgeCacheTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `DeliveredEgressTests.tests` | `InProcess/DeliveredEgressTests.fs` | uses `ToolUp.MediaLibrary` |
| Platform | `ContentAuthoringTests.tests` | `InProcess/ContentAuthoringTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `ContentAdminAuthorizationTests.tests` | `InProcess/ContentAdminAuthorizationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamCreationPolicyTests.tests` | `InProcess/TeamCreationPolicyTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamCreationPolicyTests.integrityTests` | `InProcess/TeamCreationPolicyTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamCreationPolicyTests.quotaTests` | `InProcess/TeamCreationPolicyTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DirectAddIdentityProofTests.tests` | `InProcess/DirectAddIdentityProofTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamOwnershipTransferTests.tests` | `InProcess/TeamOwnershipTransferTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamAdminLifecycleTests.tests` | `InProcess/TeamAdminLifecycleTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `UserMembershipTeardownTests.tests` | `InProcess/UserMembershipTeardownTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamInvitationTests.tests` | `InProcess/TeamInvitationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamInvitationTests.pendingInviteExpiryAuditTests` | `InProcess/TeamInvitationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamInvitationTests.expiredInviteVisibilityTests` | `InProcess/TeamInvitationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamInvitationTests.inviteExpiryNotificationTests` | `InProcess/TeamInvitationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamInvitationTests.activeTeamPolicyTests` | `InProcess/TeamInvitationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TeamInvitationTests.checkMyInvitesTests` | `InProcess/TeamInvitationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HttpBodyBindingTests.tests` | `InProcess/HttpBodyBindingTests.fs` | uses `ToolUp.AuthProviders.Passkey` |
| Platform | `ModuleIdentityTests.tests` | `InProcess/ModuleIdentityTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AnonymousModeContractTests.tests` | `InProcess/AnonymousModeContractTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AnonymousModeContractTests.exposureTests` | `InProcess/AnonymousModeContractTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleSurfaceRequirementTests.tests` | `InProcess/ModuleSurfaceRequirementTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleSurfaceRequirementTests.visibilityTests` | `InProcess/ModuleSurfaceRequirementTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionInspectorHandlerTests.roleGateTests` | `InProcess/CompositionInspectorHandlerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionInspectorHandlerTests.panelTests` | `InProcess/CompositionInspectorHandlerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionInspectorHandlerTests.emptyStateTests` | `InProcess/CompositionInspectorHandlerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionInspectorHandlerTests.exportTests` | `InProcess/CompositionInspectorHandlerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionInspectorHandlerTests.contractShapeTests` | `InProcess/CompositionInspectorHandlerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ReflectionToolsTests.tests` | `InProcess/ReflectionToolsTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HomeOverviewTests.countAffordanceTests` | `InProcess/HomeOverviewTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HomeOverviewTests.overviewScopeAiTests` | `InProcess/HomeOverviewTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HomeOverviewTests.overviewActiveAiPresentTests` | `InProcess/HomeOverviewTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HomeOverviewTests.widgetDataTests` | `InProcess/HomeOverviewTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HomeOverviewTests.pinningTests` | `InProcess/HomeOverviewTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `RevokeOnIssuerRemovedTests.tests` | `InProcess/RevokeOnIssuerRemovedTests.fs` | uses `ToolUp.ShareTokenStoreDecorators.RevokeOnIssuerRemoved` |
| Platform | `SubjectDowngradeObservabilityTests.tests` | `InProcess/SubjectDowngradeObservabilityTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TelemetryZeroCostGateTests.tests` | `InProcess/TelemetryZeroCostGateTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `DispatcherBodyAndArgFastpathTests.tests` | `InProcess/DispatcherBodyAndArgFastpathTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `ServerRemotingTailTests.tests` | `InProcess/ServerRemotingTailTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ShutdownDrainTests.tests` | `InProcess/ShutdownDrainTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `FromContextAsyncBuildOnceTests.tests` | `InProcess/FromContextAsyncBuildOnceTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `DeployPlaneTests.tenantFleetTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `DeployPlaneTests.buildOrchestratorTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `DeployPlaneTests.deployPipelineTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `DeployPlaneTests.containerSchedulerInMemoryTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `DeployPlaneTests.containerSchedulerDockerLocalTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `DeployPlaneTests.dockerWireDtoTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `DeployPlaneTests.deployPlanDefaultPipelineTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `DeployPlaneTests.deployPlanFallbackTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `DeployPlaneTests.deployPlanMutationCheck` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `DeployPlaneTests.deployPlanDiffTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `EvidenceChainWalkerTests.hopCountInvarianceTests` | `InProcess/EvidenceChainWalkerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainWalkerTests.honestAbsenceTests` | `InProcess/EvidenceChainWalkerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainWalkerTests.verdictReachabilityTests` | `InProcess/EvidenceChainWalkerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainWalkerTests.capRefusalTests` | `InProcess/EvidenceChainWalkerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainWalkerTests.auditedReadTests` | `InProcess/EvidenceChainWalkerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainWalkerTests.digestTests` | `InProcess/EvidenceChainWalkerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainWalkerTests.reportSectionTests` | `InProcess/EvidenceChainWalkerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceBundleExportTests.bundleDeterminismTests` | `InProcess/EvidenceBundleExportTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `EvidenceBundleExportTests.statementWrappingTests` | `InProcess/EvidenceBundleExportTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `EvidenceBundleExportTests.nestedAttestationRulingTests` | `InProcess/EvidenceBundleExportTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `EvidenceBundleExportTests.pureVerifierTests` | `InProcess/EvidenceBundleExportTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `EvidenceBundleExportTests.claimBoundaryTests` | `InProcess/EvidenceBundleExportTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `EvidenceBundleExportTests.signedRoundTripTests` | `InProcess/EvidenceBundleExportTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `EvidenceBundleExportTests.fixtureEmissionTests` | `InProcess/EvidenceBundleExportTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `EvidenceChainBreakTests.corpusPlacementTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainBreakTests.healthyBaselineTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainBreakTests.chainBreakCorpusTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainBreakTests.chainFalsificationTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainBreakTests.bundleTamperCorpusTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainBreakTests.bundleFalsificationTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainBreakTests.documentTamperCorpusTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainBreakTests.absentVsBrokenTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainBreakTests.severedAncestorEdgeTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `EvidenceChainBreakTests.severedAncestorReportTests` | `InProcess/EvidenceChainBreakTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `BootVerificationPreflightTests.tests` | `InProcess/BootVerificationPreflightTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `GroundedAnswerChainJoinTests.tests` | `InProcess/GroundedAnswerChainJoinTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `GroundingEnvelopeSealTests.tests` | `InProcess/GroundingEnvelopeSealTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CompositionManifestLiveInterfaceTests.tests` | `InProcess/CompositionManifestLiveInterfaceTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `DeployPlaneTests.defaultPipelineRollbackTests` | `InProcess/DeployPlaneTests.fs` | uses `ToolUp.ContainerSchedulers.DockerLocal` |
| Platform | `PlatformPeerTests.inProcessTests` | `InProcess/PlatformPeerTests.fs` | uses `InterPlatform` |
| Platform | `PlatformPeerTests.workedExampleTests` | `InProcess/PlatformPeerTests.fs` | uses `InterPlatform` |
| Platform | `PlatformPeerTests.audienceBindingTests` | `InProcess/PlatformPeerTests.fs` | uses `InterPlatform` |
| Platform | `PlatformPeerTests.jobPollOwnershipTests` | `InProcess/PlatformPeerTests.fs` | uses `InterPlatform` |
| Platform | `PeerFederationTests.fanoutTests` | `InProcess/PeerFederationTests.fs` | uses `InterPlatform` |
| Platform | `PeerFederationTests.cascadeTests` | `InProcess/PeerFederationTests.fs` | uses `InterPlatform` |
| Platform | `PeerFederationTests.proxyForwardingTests` | `InProcess/PeerFederationTests.fs` | uses `InterPlatform` |
| Platform | `CleanRoomBrokerTests.tests` | `InProcess/CleanRoomBrokerTests.fs` | uses `InterPlatform` |
| Platform | `CleanRoomGateTests.structuralTests` | `InProcess/CleanRoomGateTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CleanRoomGateTests.enforcementTests` | `InProcess/CleanRoomGateTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CleanRoomGateTests.substitutionTests` | `InProcess/CleanRoomGateTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CleanRoomGateTests.compositionTests` | `InProcess/CleanRoomGateTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CleanRoomGateTests.wireTests` | `InProcess/CleanRoomGateTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TemplateApprovalTests.mutationTests` | `InProcess/TemplateApprovalTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TemplateApprovalTests.signatureTests` | `InProcess/TemplateApprovalTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TemplateApprovalTests.lifecycleTests` | `InProcess/TemplateApprovalTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TemplateApprovalTests.gateTests` | `InProcess/TemplateApprovalTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TemplateApprovalTests.compositionTests` | `InProcess/TemplateApprovalTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TemplateApprovalTests.handshakeTests` | `InProcess/TemplateApprovalTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `TemplateApprovalTests.queueTests` | `InProcess/TemplateApprovalTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PrivacyBudgetLedgerTests.policyTests` | `InProcess/PrivacyBudgetLedgerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PrivacyBudgetLedgerTests.ledgerContractTests` | `InProcess/PrivacyBudgetLedgerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PrivacyBudgetLedgerTests.atomicityTests` | `InProcess/PrivacyBudgetLedgerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PrivacyBudgetLedgerTests.gateTests` | `InProcess/PrivacyBudgetLedgerTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `NoiseMechanismTests.samplerTests` | `InProcess/NoiseMechanismTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `NoiseMechanismTests.policyTests` | `InProcess/NoiseMechanismTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `NoiseMechanismTests.cohortTests` | `InProcess/NoiseMechanismTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `NoiseMechanismTests.gateTests` | `InProcess/NoiseMechanismTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `NoiseMechanismTests.compositionTests` | `InProcess/NoiseMechanismTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CohortActivationTests.canonicalTests` | `InProcess/CohortActivationTests.fs` | uses `InterPlatform` |
| Platform | `CohortActivationTests.authorisationTests` | `InProcess/CohortActivationTests.fs` | uses `InterPlatform` |
| Platform | `CohortActivationTests.purposeTests` | `InProcess/CohortActivationTests.fs` | uses `InterPlatform` |
| Platform | `CohortActivationTests.revocationTests` | `InProcess/CohortActivationTests.fs` | uses `InterPlatform` |
| Platform | `CohortActivationTests.pipelineTests` | `InProcess/CohortActivationTests.fs` | uses `InterPlatform` |
| Platform | `CohortActivationTests.egressTests` | `InProcess/CohortActivationTests.fs` | uses `InterPlatform` |
| Platform | `CohortActivationTests.tokenTests` | `InProcess/CohortActivationTests.fs` | uses `InterPlatform` |
| Platform | `CohortActivationTests.compositionTests` | `InProcess/CohortActivationTests.fs` | uses `InterPlatform` |
| Platform | `OutboundSignalFeedTests.revocationTests` | `InProcess/OutboundSignalFeedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `OutboundSignalFeedTests.boundTests` | `InProcess/OutboundSignalFeedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `OutboundSignalFeedTests.budgetTests` | `InProcess/OutboundSignalFeedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `OutboundSignalFeedTests.restartTests` | `InProcess/OutboundSignalFeedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `OutboundSignalFeedTests.noiseTests` | `InProcess/OutboundSignalFeedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `OutboundSignalFeedTests.validationTests` | `InProcess/OutboundSignalFeedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `OutboundSignalFeedTests.operatorTests` | `InProcess/OutboundSignalFeedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `OutboundSignalFeedTests.stateStoreTests` | `InProcess/OutboundSignalFeedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `SignedShapeSeparatorTests.registryTests` | `InProcess/SignedShapeSeparatorTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `SignedShapeSeparatorTests.pinnedDigestTests` | `InProcess/SignedShapeSeparatorTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PeerSurfaceTests.tests` | `InProcess/PeerSurfaceTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AggregatePeerSurfaceTests.tests` | `InProcess/AggregatePeerSurfaceTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AggregateLongRunningFrontingTests.tests` | `InProcess/AggregateLongRunningFrontingTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FederationPreflightTests.tests` | `InProcess/FederationPreflightTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FederationWireConformanceTests.tests` | `InProcess/FederationWireConformanceTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CrossRuntimeFederationConformanceTests.tests` | `InProcess/CrossRuntimeFederationConformanceTests.fs` | uses `InterPlatform` |
| Platform | `PeerJobRetentionTests.tests` | `InProcess/PeerJobRetentionTests.fs` | uses `InterPlatform` |
| Platform | `PeerJwtReplayTests.guardTests` | `InProcess/PeerJwtReplayTests.fs` | uses `InterPlatform` |
| Platform | `PeerJwtReplayTests.replayDefenceTests` | `InProcess/PeerJwtReplayTests.fs` | uses `InterPlatform` |
| Platform | `PeerJwtReplayTests.callScopingTests` | `InProcess/PeerJwtReplayTests.fs` | uses `InterPlatform` |
| Platform | `PeerComposeKnobTests.jobRetentionTests` | `InProcess/PeerComposeKnobTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PeerComposeKnobTests.roundOrchestratorTests` | `InProcess/PeerComposeKnobTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PeerComposeKnobTests.tokenPolicyTests` | `InProcess/PeerComposeKnobTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PeerComposeKnobTests.reachabilityTests` | `InProcess/PeerComposeKnobTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PeerAudienceBindingTests.bindingTests` | `InProcess/PeerAudienceBindingTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PeerAudienceBindingTests.postureTests` | `InProcess/PeerAudienceBindingTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PeerDelegationVerificationTests.hostVerificationTests` | `InProcess/PeerDelegationVerificationTests.fs` | uses `InterPlatform` |
| Platform | `PeerDelegationVerificationTests.userContextClaimTests` | `InProcess/PeerDelegationVerificationTests.fs` | uses `InterPlatform` |
| Platform | `PeerRobustnessTests.base64UrlSignatureTests` | `InProcess/PeerRobustnessTests.fs` | uses `InterPlatform` |
| Platform | `PeerRobustnessTests.profileNoDowngradeTests` | `InProcess/PeerRobustnessTests.fs` | uses `InterPlatform` |
| Platform | `PeerRobustnessTests.asymmetricProviderTests` | `InProcess/PeerRobustnessTests.fs` | uses `InterPlatform` |
| Platform | `PeerRobustnessTests.defaultCompositionUnchangedTests` | `InProcess/PeerRobustnessTests.fs` | uses `InterPlatform` |
| Platform | `PeerWireHardeningTests.requestBodyCapTests` | `InProcess/PeerWireHardeningTests.fs` | uses `InterPlatform` |
| Platform | `PeerWireHardeningTests.pollCorrelationTests` | `InProcess/PeerWireHardeningTests.fs` | uses `InterPlatform` |
| Platform | `PeerWireHardeningTests.contractBindingTests` | `InProcess/PeerWireHardeningTests.fs` | uses `InterPlatform` |
| Platform | `PeerCascadeBudgetAuthorityTests.budgetAuthorityTests` | `InProcess/PeerCascadeBudgetAuthorityTests.fs` | uses `InterPlatform` |
| Platform | `PeerCascadeBudgetAuthorityTests.cascadeShapeTests` | `InProcess/PeerCascadeBudgetAuthorityTests.fs` | uses `InterPlatform` |
| Platform | `PeerCascadeBudgetAuthorityTests.cascadeCompatibilityTests` | `InProcess/PeerCascadeBudgetAuthorityTests.fs` | uses `InterPlatform` |
| Platform | `PeerTransportTimeoutTests.deadlineTests` | `InProcess/PeerTransportTimeoutTests.fs` | uses `InterPlatform` |
| Platform | `PeerTransportTimeoutTests.cancellationTests` | `InProcess/PeerTransportTimeoutTests.fs` | uses `InterPlatform` |
| Platform | `PeerTransportTimeoutTests.fanoutReachTests` | `InProcess/PeerTransportTimeoutTests.fs` | uses `InterPlatform` |
| Platform | `PeerTransportTlsTests.acceptRuleTests` | `InProcess/PeerTransportTlsTests.fs` | uses `InterPlatform` |
| Platform | `PeerTransportTlsTests.transportTests` | `InProcess/PeerTransportTlsTests.fs` | uses `InterPlatform` |
| Platform | `PeerTransportTlsTests.handshakeFetchTests` | `InProcess/PeerTransportTlsTests.fs` | uses `InterPlatform` |
| Platform | `PeerTransportTlsTests.registryTests` | `InProcess/PeerTransportTlsTests.fs` | uses `InterPlatform` |
| Platform | `PeerTransportTlsTests.registryRemoveTests` | `InProcess/PeerTransportTlsTests.fs` | uses `InterPlatform` |
| Platform | `PeerTransportTlsTests.composeTests` | `InProcess/PeerTransportTlsTests.fs` | uses `InterPlatform` |
| Platform | `FederatedIdentitySanitisationTests.parityTests` | `InProcess/FederatedIdentitySanitisationTests.fs` | uses `ToolUp.AuthProviders.Oidc` |
| Platform | `FederatedIdentitySanitisationTests.negativeControlTests` | `InProcess/FederatedIdentitySanitisationTests.fs` | uses `ToolUp.AuthProviders.Oidc` |
| Platform | `FederatedIdentitySanitisationTests.boundaryDetailTests` | `InProcess/FederatedIdentitySanitisationTests.fs` | uses `ToolUp.AuthProviders.Oidc` |
| Platform | `RoundOrchestratorTests.tests` | `InProcess/RoundOrchestratorTests.fs` | uses `InterPlatform` |
| Platform | `CommutativeCipherTests.tests` | `InProcess/CommutativeCipherTests.fs` | uses `InterPlatform` |
| Platform | `CommutativeCipherTests.selfTests` | `InProcess/CommutativeCipherTests.fs` | uses `InterPlatform` |
| Platform | `GiraffeStockHelperTests.tests` | `InProcess/GiraffeStockHelperTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IPeerAuditTransparencyContract.tests` | `Contracts/IPeerAuditTransparencyContract.fs` | uses `InterPlatform` |
| Platform | `IPeerCapabilityNegotiationContract.tests` | `Contracts/IPeerCapabilityNegotiationContract.fs` | uses `InterPlatform` |
| Platform | `IPeerNonFSharpSdkContract.tests` | `Contracts/IPeerNonFSharpSdkContract.fs` | uses `InterPlatform` |
| Platform | `ArchitectureFitnessTests.tests` | `InProcess/ArchitectureFitnessTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `BaselineMergeDriverTests.tests` | `InProcess/BaselineMergeDriverTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `BackupCoordinatorTests.tests` | `InProcess/BackupCoordinatorTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `SeamPrincipalTests.tests` | `InProcess/SeamPrincipalTests.fs` | uses `ToolUp.Reporting.Server` |
| Platform | `AdversarialFailClosedTests.tests` | `InProcess/AdversarialFailClosedTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `ConstantTimeCompareTests.tests` | `InProcess/ConstantTimeCompareTests.fs` | names `ToolUp.Platform.Server` at run time |
| Platform | `ModuleLoadOutcomeTests.tests` | `InProcess/ModuleLoadOutcomeTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HydrationParityTests.tests` | `InProcess/HydrationParityTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `SecondBindingNeutralityTests.tests` | `InProcess/SecondBindingNeutralityTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `ComponentRegistryContract.tests` | `Contracts/ComponentRegistryContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComponentRegistryContract.selfTests` | `Contracts/ComponentRegistryContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleContract.tests` | `Contracts/ModuleContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleContract.referenceTests` | `Contracts/ModuleContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ModuleContract.selfTests` | `Contracts/ModuleContract.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ThreatLensRegressionSuite.tests` | `InProcess/ThreatLensRegressionSuite.fs` | uses `ToolUp.AuthProviders.Oidc` |
| Platform | `ReactiveRecomputeComposeTests.tests` | `InProcess/ReactiveRecomputeComposeTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `UploadValidationTests.contractTests` | `InProcess/UploadValidationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `UploadValidationTests.tests` | `InProcess/UploadValidationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `SessionStoreResetTests.tests` | `InProcess/SessionStoreResetTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `IsolatedExecutionProfileTests.profileTests` | `InProcess/IsolatedExecutionProfileTests.fs` | uses `InterPlatform` |
| Platform | `IsolatedExecutionProfileTests.postureTests` | `InProcess/IsolatedExecutionProfileTests.fs` | uses `InterPlatform` |
| Platform | `IsolatedExecutionProfileTests.gateTests` | `InProcess/IsolatedExecutionProfileTests.fs` | uses `InterPlatform` |
| Platform | `IsolatedExecutionProfileTests.structuralTests` | `InProcess/IsolatedExecutionProfileTests.fs` | uses `InterPlatform` |
| Platform | `IsolatedExecutionProfileTests.releaseTests` | `InProcess/IsolatedExecutionProfileTests.fs` | uses `InterPlatform` |
| Platform | `ComputeBackendRegistryTests.registryTests` | `InProcess/ComputeBackendRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComputeBackendRegistryTests.routingTests` | `InProcess/ComputeBackendRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComputeBackendRegistryTests.refusalTests` | `InProcess/ComputeBackendRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComputeBackendRegistryTests.handleRoundTripTests` | `InProcess/ComputeBackendRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComputeBackendRegistryTests.observabilityTests` | `InProcess/ComputeBackendRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComputeBackendRegistryTests.gp13Tests` | `InProcess/ComputeBackendRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComputeBackendRegistryTests.structuralTests` | `InProcess/ComputeBackendRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ComputeBudgetTests.tests` | `InProcess/ComputeBudgetTests.fs` | uses `ToolUp.ModelProviders.Reference` |
| Platform | `DataObjectOrphanSweepTests.tests` | `InProcess/DataObjectOrphanSweepTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DataObjectOrphanSweepTests.validatorTests` | `InProcess/DataObjectOrphanSweepTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DataObjectOrphanSweepTests.composeTests` | `InProcess/DataObjectOrphanSweepTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DataObjectOrphanSweepTests.inBandGraceTests` | `InProcess/DataObjectOrphanSweepTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `RagConfigValidatorExtensionTests.tests` | `InProcess/RagConfigValidatorExtensionTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `ExternalCallbackTests.tests` | `InProcess/ExternalCallbackTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `HttpComputeDispatcherTests.tests` | `InProcess/HttpComputeDispatcherTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FederatedModelExecutionTests.tests` | `InProcess/FederatedModelExecutionTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `ExternalModelFitTests.tests` | `InProcess/ExternalModelFitTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ScopeEnumerationSweepTests.tests` | `InProcess/ScopeEnumerationSweepTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CountersignatureRegistryTests.subjectTests` | `InProcess/CountersignatureRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CountersignatureRegistryTests.completenessTests` | `InProcess/CountersignatureRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CountersignatureRegistryTests.invalidationTests` | `InProcess/CountersignatureRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CountersignatureRegistryTests.revocationTests` | `InProcess/CountersignatureRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CountersignatureRegistryTests.signatureTests` | `InProcess/CountersignatureRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CountersignatureRegistryTests.storeTests` | `InProcess/CountersignatureRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CountersignatureRegistryTests.queueTests` | `InProcess/CountersignatureRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `CountersignatureRegistryTests.parityTests` | `InProcess/CountersignatureRegistryTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DecoderAlgebraTests.tests` | `Remoting/DecoderAlgebraTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `JsonDecoderAlgebraTests.tests` | `Remoting/JsonDecoderAlgebraTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ToolEffectEnvelopeTests.tests` | `InProcess/ToolEffectEnvelopeTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ToolGateProofOracleTests.tests` | `InProcess/ToolGateProofOracleTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `TaintFlowProofOracleTests.tests` | `InProcess/TaintFlowProofOracleTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `MetricsHistoryFlusherTests.tests` | `InProcess/MetricsHistoryFlusherTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ConversationRetentionTests.tests` | `InProcess/ConversationRetentionTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `ReportingAIToolsTests.tests` | `InProcess/ReportingAIToolsTests.fs` | uses `ToolUp.Reporting.Server` |
| Platform | `ConversationListingTests.tests` | `InProcess/ConversationListingTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `ConversationVisibilityTests.tests` | `InProcess/ConversationVisibilityTests.fs` | uses `ToolUp.AI.Server` |
| Platform | `FactBrowseTests.tests` | `InProcess/FactBrowseTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FactDeclarationsValidatorTests.tests` | `InProcess/FactDeclarationsValidatorTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `DelegateFactTests.tests` | `InProcess/DelegateFactTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FactsOperatorTaskTests.tests` | `InProcess/FactsOperatorTaskTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `TeamOutputVisibilityTests.tests` | `InProcess/TeamOutputVisibilityTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FactPublicationTests.tests` | `InProcess/FactPublicationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `Phase971InterPlatformTests.tests` | `InProcess/Phase971InterPlatformTests.fs` | uses `InterPlatform` |
| Platform | `Phase971KnowledgeBaseTests.tests` | `InProcess/Phase971KnowledgeBaseTests.fs` | uses `ToolUp.RAG.Server` |
| Platform | `FactPublicationTests.phase971Tests` | `InProcess/FactPublicationTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `Phase971PlatformStoresTests.tests` | `InProcess/Phase971PlatformStoresTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `Phase971PlatformRecordsTests.tests` | `InProcess/Phase971PlatformRecordsTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `Phase971AITests.tests` | `InProcess/Phase971AITests.fs` | uses `ToolUp.AI.Server` |
| Platform | `Phase971PublicRenderingTests.tests` | `InProcess/Phase971PublicRenderingTests.fs` | uses `ToolUp.PublicRendering` |
| Platform | `Phase971PlatformTenantTests.tests` | `InProcess/Phase971PlatformTenantTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ProviderExceptionTests.tests` | `InProcess/ProviderExceptionTests.fs` | uses `ToolUp.RateLimit.AzureTableStorage` |
| Platform | `OidcSignInSpaPrecedenceTests.tests` | `InProcess/OidcSignInSpaPrecedenceTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `ServerAppOrderTests.tests` | `InProcess/ServerAppOrderTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `ServerBindAddressTests.tests` | `InProcess/ServerBindAddressTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PageDocumentStoreTests.tests` | `InProcess/PageDocumentStoreTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AuditFireAndForgetTests.tests` | `InProcess/AuditFireAndForgetTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `LineageDurabilityTests.tests` | `InProcess/LineageDurabilityTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AuditCallSiteFireAndForgetTests.tests` | `InProcess/AuditCallSiteFireAndForgetTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FileApiMountTests.tests` | `InProcess/FileApiMountTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `StreamAuthBothSignInsTests.tests` | `InProcess/StreamAuthBothSignInsTests.fs` | uses `ToolUp.Platform.Testing` |
| Platform | `DeliveryCompletedTests.tests` | `InProcess/DeliveryCompletedTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `FactRenderToolTests.tests` | `InProcess/FactRenderToolTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PipelineToolTests.tests` | `InProcess/PipelineToolTests.fs` | uses `ToolUp.Facts.Server` |
| Platform | `FactTablesToolTests.tests` | `InProcess/FactTablesToolTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `AggregateMetricToolTests.tests` | `InProcess/AggregateMetricToolTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PipelineAssertionTests.tests` | `InProcess/PipelineAssertionTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `PageRequestResolverTests.tests` | `InProcess/PageRequestResolverTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `LayoutToolsTests.tests` | `InProcess/LayoutToolsTests.fs` | uses `ToolUp.Platform.Server` |
| Platform | `NarrativeCalculationTests.tests` | `InProcess/NarrativeCalculationTests.fs` | uses `ToolUp.Platform.Server` |

**The files two packs compile.** None carries a `[<Tests>]` binding; each pack compiles its own copy of the helper.

| File | Compiled by |
|---|---|
| `Contracts/IBlobStorageContract.fs` | Platform.Infra, Platform |
| `Contracts/IEventStoreContract.fs` | Platform.Infra, Platform |
| `Contracts/IDataObjectStoreContract.fs` | Platform.Infra, Platform, Platform.Scope |
| `Contracts/IDataCatalogContract.fs` | Platform.Infra, Platform |
| `Contracts/IDataSourceContract.fs` | Platform.Infra, Platform |
| `Contracts/IDeclaresPayloadFormatContract.fs` | Platform.Infra, Platform |
| `Contracts/IOAuthCredentialFlowContract.fs` | Platform.Jobs, Platform |
| `Contracts/ISecretStoreContract.fs` | Platform.Config, Platform.Infra, Platform |
| `Contracts/IHealthCheckContract.fs` | Platform.Remoting, Platform |
| `Contracts/IShareTokenStoreContract.fs` | Platform, Platform.Scope |
| `Contracts/InMemoryBlobStorage.fs` | Platform.Audit, Platform.Infra, Platform.Jobs, Platform, Platform.Scope |
| `Contracts/IDatasetStoreContract.fs` | Platform.Infra, Platform |
| `Contracts/IModelRegistryContract.fs` | Platform.Infra, Platform.Jobs, Platform |
| `Support/CacheReset.fs` | Platform.Audit, Platform |
| `Remoting/WireCorpus.fs` | Platform.Remoting, Platform |
| `InProcess/DataObjectStoreTests.fs` | Platform.Infra, Platform, Platform.Scope |
| `InProcess/ShareTokenStoreTests.fs` | Platform, Platform.Scope |
| `InProcess/SsePerConnectionWriterTests.fs` | Platform.Jobs, Platform |
| `InProcess/NeutralityTokens.fs` | Platform.Remoting, Platform |
| `InProcess/BuildTranscriptTests.fs` | Platform.Audit, Platform.Infra, Platform |
| `Contracts/SurfaceDiff.fs` | Platform.PublicApi, Platform |
| `Contracts/PublicApiApproval.fs` | Platform.PublicApi, Platform |

## What a pack is

- **The test sources stay where they are**, under `src/ToolUp.Platform.Tests/`, and each pack's project compiles its
  files from there, as each server part compiles its sources from the umbrella's folder. Thirty other packs and
  `Build.fsproj` source-link files from that folder, the documentation cites the paths, and a test reading its own
  folder through `__SOURCE_DIRECTORY__` still finds it. Which pack a file is in is read from the project files.
- **Each pack's `Program.fs`** registers its lists and calls `PackEntry.run`, which attaches the registration guard
  (over the pack's own assembly, with a floor at about 85 % of the bindings its sweep finds), applies the lane filter
  after it, and under the `affected` lane resolves the pack's share of the lists file. The residue's `Program.fs`
  shrank to the same shape, and the measured slow lists moved with their lists (seven stay in the residue; the
  globalization child-run list is in Remoting).
- **References are the closure the files use**, read off the same symbol facts: Core, the pack's part (which carries
  the earlier parts), and the projects whose symbols its files use. The Remoting pack also references the client
  tier, the build library and the Core-level companions its lists use (the graph, workflow, experiment and fact
  cores).
- **`ToolUp.Platform.Tests.Support`** compiles the lane vocabulary (`TestLane.fs`), its Expecto half
  (`TestLaneFilter.fs`), the registration guard, the corpus anchor and the fire-and-forget harness from where they
  were, and `PackEntry.fs`. It references only Expecto and FSharp.Core, so it adds nothing to a pack's closure. It holds
  no cases, so it is no gate-manifest row; `GateManifest.exclusions` names it. The suite consuming
  `ToolUp.Platform.Testing` (Phase 1100) absorbs it when that lands.
- **A row per pack** in `GateManifest.rows`, with the reads its own sources make (the environment variables are the
  ones its files name) and a budget from its own run. `EXPECTED_PACKS` was re-rendered with each row, never raised by
  hand.

**Removed opens.** Test files carried `open` lines of parts they used nothing of, most of them from 1198's
namespace move. In the residue they compiled, because the residue
references the umbrella; in a pack whose closure lacks the part they do not. 237 such lines were removed
from 183 files, each one an `open` the compiler reported as naming a namespace the pack does not reference,
in a file the symbol facts show uses nothing of it. No other line of any test file changed.

**`GateReconcile` widened.** It recognised a test project by the suffix `.Tests.fsproj`, so a pack named
`ToolUp.Platform.Tests.Infra.fsproj` would have escaped it. `Gate.reconcile` now also recognises a `.Tests.` segment in
the file name; the Support library is the one project that newly matches, and it is a named exclusion.

## The approval gate's own pack

`Contracts/PublicApiApproval.fs`, its pure comparer `Contracts/SurfaceDiff.fs` and `InProcess/PublicApiApprovalTests.fs`
are compiled by `ToolUp.Platform.Tests.PublicApi`, which registers the approval list; the residue also compiles the
first two, without bindings, for its baseline merge-driver cases. The approval pack's project references are every packable
project, one per baseline in `api-baselines/` (191 of them), each with `ReferenceOutputAssembly="false"`: they
are built, not referenced, because the gate renders each assembly from that project's own `bin/`. So building the pack
builds exactly the surface it renders, and nothing else.

```powershell
$env:TOOLUP_APPROVE_API = 'ToolUp.Platform.Core'
dotnet run --project src/ToolUp.Platform.Tests.PublicApi/ToolUp.Platform.Tests.PublicApi.fsproj
Remove-Item Env:TOOLUP_APPROVE_API
```

`MergeBaselines` runs exactly that, where it built the whole solution and then ran the whole Platform pack (every one
of its fifteen thousand cases) under the variable.

Measured on one Windows Arm64 machine, Debug, 2026-11-02, with other sessions building and testing beside it, over tree
`a9d64026`: `TOOLUP_APPROVE_API=ToolUp.Platform.Core` through the approval pack took **100 s** end to end (the
no-op check of its 192 referenced projects and 239 cases). The same regeneration before the partition was a no-op
solution build (144 s, 255 projects, measured on the same machine the same hour) and then the whole Platform pack
under the variable: its last recorded full run, on `main` that morning, was 594 s over 15,244 cases. So about 740 s
became 100 s, and 15,244 cases became 239. The counts are the evidence; the seconds are one busy machine's.

## The graph source

`Select --source graph` lists the packs whose closure meets the diff. A changed path belongs to the projects whose
items name it; failing that, to the projects whose wildcard items cover it; failing that, to the project whose directory
holds it most closely. A server part's source, which sits in the umbrella's folder, is therefore the part's, and a
Platform test file is the pack's that compiles it. A pack runs whole when its closure holds a changed path's owner. Every
other pack runs nothing and is named as skipped with its case count, unless a declared or always-on unit names it. The
manifest keeps 979's shape and gains `"source"` and a `graph` block (selected, skipped, unowned), and `VerifyAll` removes
a skipped pack from the lane that selected it. Only the forcing rules the graph cannot see past still fire
(`msbuild-import`, `solution`, `build-script`, `toolchain`).

**What one part's diff selects.** Not that part's pack alone, and the phase's acceptance sentence ("names every other as
skipped") does not hold as written. Each part references the parts before it, so the pack of every later part, and the
residue over the umbrella, compiles against the changed part, and a sound graph source must run them. It skips the packs
of the parts before it. On the tree this phase leaves:

| A diff to | Family packs selected | Family packs skipped | All packs selected / skipped |
|---|---|---|---|
| the Remoting part (`HttpBodyBinding.fs`) | `Platform`, `Platform.Remoting`, `Platform.Infra`, `Platform.Config`, `Platform.Scope`, `Platform.Jobs`, `Platform.Audit`, `Platform.PublicApi` | none | 38 / 10 |
| the Infra part (`BlobXmlRepository.fs`) | `Platform`, `Platform.Infra`, `Platform.Config`, `Platform.Scope`, `Platform.Jobs`, `Platform.Audit`, `Platform.PublicApi` | `Platform.Remoting` | 37 / 11 |
| the Config part (`LocaleResolver.fs`) | `Platform`, `Platform.Config`, `Platform.Scope`, `Platform.Jobs`, `Platform.Audit`, `Platform.PublicApi` | `Platform.Remoting`, `Platform.Infra` | 36 / 12 |
| the Scope part (`UserDirectoryApiHandler.fs`) | `Platform`, `Platform.Scope`, `Platform.Jobs`, `Platform.Audit`, `Platform.PublicApi` | `Platform.Remoting`, `Platform.Infra`, `Platform.Config` | 35 / 13 |
| the Jobs part (`PresenceApiHandler.fs`) | `Platform`, `Platform.Jobs`, `Platform.Audit`, `Platform.PublicApi` | `Platform.Remoting`, `Platform.Infra`, `Platform.Config`, `Platform.Scope` | 34 / 14 |
| the Audit part (`AuthDenialsDiagnosticsHandler.fs`) | `Platform`, `Platform.Audit`, `Platform.PublicApi` | `Platform.Remoting`, `Platform.Infra`, `Platform.Config`, `Platform.Scope`, `Platform.Jobs` | 33 / 15 |
| the umbrella's composition root (`ServerApp.fs`) | `Platform`, `Platform.PublicApi` | `Platform.Remoting`, `Platform.Infra`, `Platform.Config`, `Platform.Scope`, `Platform.Jobs`, `Platform.Audit` | 32 / 16 |

The Build pack holds both directions: a synthetic graph where part B's diff selects B's pack and the residue and skips
A's pack and an unrelated one, and the real tree, where a Remoting-, a Jobs- and an Audit-part source each select their
own pack and every later one and skip the earlier ones, and the composition root (`ServerApp.fs`) selects the residue
and no part's pack. Red first: with ownership read from folders alone (the item rule disabled), four of the cases failed: the part's
source was the umbrella's, so a part diff selected no part's pack, and a Remoting-part source over the real tree did
not select `Platform.Remoting`. With the item rule they pass.

**When it may be cited: the same rule as the declared source.** It is a fail-fast lane, and a ship cites the full lane
(`docs/platform/testing-conventions.md`).

## The twenty-phase replay

The last twenty phases whose landing touched a server-tier source, read from `main`'s first-parent history (each
phase's diff is the union of its commits'). For each: the paths, what the graph source selects over them on today's
project graph (the forcing rules first, then the packs), what 1121's source selects over the same paths (it is given
no `--declared` file, because a landed phase records none, so its units are the diffed lists), and whether the full lane
failed on a list the graph skipped.

| Phase | Paths (server-tier) | Graph source: forcing rules that fired | Graph source: family packs selected (all packs selected / skipped) | 1121's source: lane, diffed lists | A list the full lane failed on, skipped by the graph |
|---|---|---|---|---|---|
| 1197 | 173 (7) | `build-script`, `msbuild-import`, `solution` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (39 / 9) | full (build-script, msbuild-import, solution, test-project) | none recorded |
| 1198 | 2019 (573) | `build-script`, `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (40 / 8) | full (build-script, composition-root, module-registry, msbuild-import, test-project, test-support, unpinned-test-source) | none recorded |
| 1193 | 35 (13) | `build-script`, `msbuild-import`, `solution` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (build-script, msbuild-import, solution, test-project) | none recorded |
| 875 | 25 (6) | `build-script`, `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (build-script, composition-root, msbuild-import, test-project, test-support) | none recorded |
| 876 | 382 (26) | `build-script`, `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (45 / 3) | full (build-script, composition-root, module-registry, msbuild-import, server-client-config, test-project, test-support, unpinned-test-source) | none recorded |
| 1067 | 85 (30) | `msbuild-import` | residue, Remoting, Infra, Config, Jobs, Audit, PublicApi (39 / 9) | full (composition-root, module-registry, msbuild-import, test-support) | none recorded |
| 1167 | 14 (1) | `msbuild-import` | residue, PublicApi (33 / 15) | full (module-registry, msbuild-import, test-project) | none recorded |
| 1165 | 24 (6) | `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (msbuild-import, test-project, unpinned-test-source) | none recorded |
| 1161 | 29 (3) | `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (msbuild-import, test-project, unpinned-test-source) | none recorded |
| 1157 | 130 (6) | `build-script`, `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (build-script, module-registry, msbuild-import, test-project, test-support, unpinned-test-source) | none recorded |
| 1148 | 208 (110) | `build-script`, `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (build-script, composition-root, module-registry, msbuild-import) | none recorded |
| 1090 | 35 (11) | `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (msbuild-import, test-project) | none recorded |
| 1089 | 38 (7) | `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (msbuild-import, test-project, unpinned-test-source) | none recorded |
| 1060 | 25 (7) | `build-script`, `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (build-script, msbuild-import, test-project) | none recorded |
| 1136 | 12 (1) | `msbuild-import` | residue, Remoting, PublicApi (33 / 15) | full (msbuild-import) | none recorded |
| 1137 | 10 (3) | none (affected) | residue, Infra, Config, Scope, Jobs, Audit, PublicApi (37 / 11) | full (composition-root, test-project) | none recorded |
| 1051 | 32 (12) | none (affected) | residue, Config, Scope, Jobs, Audit, PublicApi (36 / 12) | full (composition-root, module-registry, test-project) | none recorded |
| 1064 | 21 (10) | none (affected) | residue, Infra, Config, Scope, Jobs, Audit, PublicApi (37 / 11) | full (composition-root, unpinned-test-source) | none recorded |
| 1050 | 10 (1) | none (affected) | residue, Remoting, Infra, Scope, PublicApi (35 / 13) | full (composition-root, server-client-config, test-project) | none recorded |
| 1059 | 50 (16) | `build-script`, `msbuild-import` | residue, Remoting, Infra, Config, Scope, Jobs, Audit, PublicApi (44 / 4) | full (build-script, composition-root, msbuild-import, test-project) | none recorded |

**What it shows.**

- **No list the full lane failed on was skipped, because none is recorded.** Every citable gate record on this machine
  (`<common git dir>/fuaran-gate/runs/gate-*.json`, ten phases) exited 0 with no failed case. The admission log
  (`verify-runs.jsonl`) holds one run with a red Platform row, a fast lane on 2026-11-02 at 08:57 that names neither the
  phase nor the list. No other landing log was available for these phases. So the column is "none recorded" rather
  than a claim that nothing failed.
- **The graph source runs the affected lane on four of the twenty, where 1121's source ran the full lane on all twenty.**
  1121's source fires a forcing rule on every one (the composition root, a module registry, a test project or support
  file, an unpinned test source). The graph places those paths itself.
- **Sixteen still run the full lane, and the cause is mostly one file.** Every phase that cuts or rides a version
  appends a draft note to `Directory.Build.props`, an MSBuild import that reaches every project, so `msbuild-import`
  fires. The graph cannot see past an import, and the rule is right to fire. A ledger that lives outside an import
  would leave those phases to the graph. That is a finding for a later phase, not a change made here.
- **Its selections skip what the closure says they may.** The table shows them even where a forcing rule then runs
  the full lane. A Remoting-only change still selects every part's pack, because every part is built over Remoting.
  A change to the composition root or to a companion over the umbrella selects the residue and the approval pack and
  skips the part packs (1167; 1136's selection adds the Remoting pack, through a changed path in that pack's own
  closure). 1137, 1051 and 1064, which ran affected and touched no Remoting source, skipped the Remoting pack and its
  2,458 cases.

## Measured

From `build-census.json`, regenerated on this phase's tree (`a9d64026`, 2026-11-02T18:43:18Z, Microsoft Windows 10.0.26300 Arm64, 12 logical processors),
against the census the cut commit carries (`843a49e0`, 2026-11-02T11:47:59Z). The census is a clean graph build of
the solution; its compile seconds are per project, and on a shared machine the sums are what move, not the shape.

Before, the one pack: 70.6 compile seconds, 408,492 lines, fan-out 82. After, the
family:

| Project | Compile seconds | Lines | Fan-out |
|---|---|---|---|
| `ToolUp.Platform.Tests.Audit` | 5.1 | 9,194 | 9 |
| `ToolUp.Platform.Tests.Config` | 7.3 | 8,792 | 6 |
| `ToolUp.Platform.Tests.Infra` | 14.8 | 17,823 | 7 |
| `ToolUp.Platform.Tests.Jobs` | 7 | 17,006 | 7 |
| `ToolUp.Platform.Tests.PublicApi` | 4.4 | 2,877 | 192 |
| `ToolUp.Platform.Tests.Remoting` | 17.3 | 45,765 | 26 |
| `ToolUp.Platform.Tests.Scope` | 11.1 | 13,956 | 9 |
| `ToolUp.Platform.Tests.Support` | 3.5 | 2,380 | 0 |
| `ToolUp.Platform.Tests` | 47.1 | 304,104 | 83 |

The largest pack is the residue, which still compiles the files the umbrella's lists need. Every other pack compiles
in a fraction of it, and the eight have no reference between them, so a graph build compiles them in parallel once their
parts are built: the largest single compile in the family is 47.1 s, against the
one pack's 70.6 s. The solution's clean wall clock: 301.3 s before, 273.2 s
after (one busy machine; counts are the evidence, seconds are not).

## Tidy record

Reviewed: every key file of the phase (the residue's project file and `Program.fs`, `Support/TestLane.fs`,
`Support/TestLaneFilter.fs`, `Contracts/PublicApiApproval.fs`, `InProcess/PublicApiApprovalTests.fs`, `Build.fs`,
`ToolUp.Forge.sln`, `checks.yml`, `GateReconcileTests.fs`, `AffectedLaneTests.fs`, `CLAUDE.md`, the testing conventions).

Removed:

- **The residue's entry point lost its private copy of the pack runner** (the affected-lane keep, the guard and lane
  wiring, the account of what was kept): one copy now lives in `PackEntry.run`, which all eight packs call, where eight
  copies would otherwise have been written. Its project file lost the phase-history comments that narrated a 2026-05
  migration, and the five sources the Support project now compiles.
- **The dead `open` lines** the partition exposed (above): every one named a part the file uses nothing of.
- **The residue's ledgers shrank to the residue**: each move took its files and lists out of the residue's project file
  and `Program.fs`, so nothing is listed twice.
- **`MergeBaselines` lost its whole-solution build step**, which the approval pack's references make redundant.

Considered and kept:

- **The residue still references every project it referenced before.** Its files load assemblies by name and scan
  sources at run time, so a reference no compile-time use needs can still be one a test needs; pruning it is a change to
  measure, not to infer.
- **Thirty companion packs still source-link `Support/TestRegistrationGuard.fs`** rather than referencing the Support
  project. They work, and moving them is the shared-harness work Phase 1100 owns.
- **`TestLaneFilter.fs` is unchanged**: the per-pack share of a lists file is `TestLane.Lists.forPack`, beside the
  parser of the same file, so the filter needed nothing.
- **The historical migration documents** that name `dotnet run --project src/ToolUp.Platform.Tests/...` with a list
  filter record what was run when those phases shipped. They are left as written; the map above is where a list's pack
  is found now.

## Rollback

Revert the phase's commits. The test sources never moved, so the revert restores the one pack's project and entry point
and drops the eight new projects; the removed `open` lines come back with it, harmlessly.
