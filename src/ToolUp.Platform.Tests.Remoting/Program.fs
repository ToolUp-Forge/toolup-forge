// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.RemotingPack

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.Graph
open ToolUp.Platform.Tests.Remoting
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs: the lists whose test files reach the Remoting part
/// and no later one, with the lists that reach no server part at all (the earliest pack, whose closure
/// is the smallest). Which list is in which pack, and why: docs/migrations/1194-platform-pack-
/// partition.md. `[<Tests>]` alone does NOT get a list into this run, which is why the registration
/// guard runs over it.
let private registeredTests =
    testList "ToolUp.Platform.Tests.Remoting" [
        ColumnMatcherTests.tests
        ColumnMappingTests.tests
        DerivedColumnEvalTests.tests
        ExperimentSubstrateTests.tests
        WorkflowEngineTests.tests
        ClientBrandLiftTests.tests
        CellProvenanceTests.tests
        AgChartExportTests.tests
        ValidationTests.tests
        IdempotencyStoreEvictionTests.tests
        InMemoryJobDispatcherTests.tests
        AuditBodyDisposalTests.tests
        PostDispatchBodyReadTests.tests
        StreamingTests.tests
        StreamingDispatchTests.tests
        StreamingPipelineTests.tests
        JobHandleTests.tests
        MarkdownRendererTests.tests
        HtmlRendererTests.tests
        NarrativeElementTests.tests
        HealthyHealthCheckTests.tests
        DegradedHealthCheckTests.tests
        UnhealthyHealthCheckTests.tests
        OkConfigValidatorTests.tests
        WarningConfigValidatorTests.tests
        ErrorConfigValidatorTests.tests
        EffectJoinTests.tests
        OidcClassifyTokenTests.tests
        OidcRefreshPlanTests.tests
        OidcDiagnoseTests.tests
        OidcTracerTests.tests
        OidcStateMachineTests.tests
        OidcPresetsTests.tests
        OidcCoherenceValidatorTests.tests
        OidcSecondaryFlowTests.tests
        InMemoryGraphStoreTests.tests
        Neo4jConformanceTests.pureTests
        Neo4jConformanceTests.liveTests
        AgeConformanceTests.pureTests
        AgeConformanceTests.liveTests
        DatadogReadbackClientTests.tests
        RateLimitConfigHelpersTests.tests
        I18nCoverageTests.tests
        MultimodalAIProviderTests.tests
        SyntheticClientToolAuthorizerTests.tests
        HostCapabilityNegotiationTests.tests
        HostRenderTelemetryTests.tests
        HostContentSanitizerTests.tests
        HostI18nResolverTests.tests
        HostRenderBudgetTests.tests
        NoActiveTeamLandingTests.tests
        ScopeDenialTests.tests
        HostingSeamComponentIdTests.tests
        HostedTreeLayoutTests.tests
        UnreducedViewPreviewTests.tests
        FactRenderingTests.tests
        FactRenderingTests.invariantGlobalizationTests
        BrandKitTests.tests
        HostThemeTokensTests.tests
        BrandKitLayoutTests.tests
        GoogleDirectoryTests.tests
        WithRequestHeadersPassthroughTests.tests
        ConsentProviderTests.tests
        ConsentGatedScriptTests.tests
        AdAnalyticsSinkTests.tests
        AdAnalyticsSinkTests.noOpTests
        ModuleGroupingValidatorTests.tests
        SidebarVisibilityContractTests.tests
        SidebarVisibilityContractTests.orderingTests
        SidebarVisibilityContractTests.groupSetTests
        SidebarVisibilityContractTests.navRoleTests
        SidebarVisibilityContractTests.accessibleNameTests
        SidebarVisibilityContractTests.placementTests
        SidebarVisibilityContractTests.keyboardNavigationTests
        SidebarVisibilityContractTests.chromeRowTests
        RouteGuardContractTests.routeGuardTests
        RouteGuardContractTests.sharedPredicateTests
        CommandPaletteContractTests.paletteVisibilityTests
        CommandPaletteContractTests.expansionTests
        CommandPaletteContractTests.fuzzyTests
        CommandPaletteContractTests.paletteStateTests
        MessageCatalogContractTests.tests
        MessageCatalogContractTests.teamDefaultTests
        MessageCatalogContractTests.consumerOverrideTests
        MessageCatalogContractTests.localeSwitchTests
        LocalizationTests.tests
        SidebarHidingContractTests.hideRestoreTests
        SidebarHidingContractTests.pinHideRuleTests
        SidebarHidingContractTests.legacyBlobTests
        SidebarHidingContractTests.paletteParityTests
        AdminLandingContractTests.compositionTests
        AdminLandingContractTests.roleFilterTests
        AdminLandingContractTests.clickThroughTests
        AdminLandingContractTests.emptyStateTests
        CsrfPrefetchAnonymousGateTests.tests
        SubjectKindClientFlowTests.tests
        SubjectWildcardAnalyzerTests.tests
        GeneratedInvocationChainTests.tests
        IAIProviderContract.tests
        SvgPropTests.tests
        DataPropTests.tests
        AriaPropTests.tests
        ClientToolkitThemingTests.tests
        RemoteAndMemoTests.tests
        DomAttrCustomAuditTests.tests
        StjBackwardCompatTests.tests
        RemotingAnalyzerRecognitionTests.tests
        RemotingDecodeRefusalTests.tests
        SchemaVersionTests.tests
        MsgPackHostileInputTests.tests
        OpenCoreVocabularyNeutralityTests.tests
        ClientHostCapabilitiesContract.tests
        Conformance.ModelExecutionSpecConformance.tests
        MsgPackRoundTripTests.tests
        StjRoundTripTests.tests
        JsonTextTests.tests
        SseGrammarTests.tests
        JsonWideValueTests.tests
        ReadPolicyAttributeTests.tests
        RemotingBatchTests.tests
        GeneratorFidelityTests.tests
        ClientProxyGenerationTests.tests
        PlatformDecodersGenerationTests.tests
        PlatformJsonEncodersGenerationTests.tests
        CodecsTests.tests
        ApiRecordCensusTests.tests
        ApiContractTests.tests
        ProofOracleTests.tests
        DisclosureProofOracleTests.tests
        ModelInputProofOracleTests.tests
        ElmishProofOracleTests.tests
        ElmishLoopProofOracleTests.tests
        FactPresentationTests.tests
        AppCompositionConformanceTests.tests
    ]

let private deliberatelyUnregistered: TestRegistrationGuard.Exemption list = []

/// The pack's measured slow lists. The rule, and how to re-derive it, are
/// beside the residue pack's declaration in src/ToolUp.Platform.Tests/Program.fs.
let private laneDeclaration: TestLaneFilter.LaneDeclaration = {
    Slow = [
        // ~10 s / 2 cases — one case starts a child run of this pack.
        FactRenderingTests.invariantGlobalizationTests
    ]
    // No list here is audited to the `pure` standard (no filesystem, process,
    // socket or environment variable), so that lane runs the guard alone.
    Pure = []
}

/// The guard's floor is a LOWER BOUND on the `[<Tests>]` bindings its sweep
/// finds (about 85 % of them), so adding a list never needs an edit here.
[<EntryPoint>]
let main argv =
    PackEntry.run
        {
            Row = "Platform.Remoting"
            Assembly = Assembly.GetExecutingAssembly()
            GuardFloor = 45
            Exemptions = deliberatelyUnregistered
            Lanes = laneDeclaration
            Registered = registeredTests
        }
        argv