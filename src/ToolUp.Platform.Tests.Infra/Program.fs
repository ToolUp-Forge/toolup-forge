// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InfraPack

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs: the lists whose test files reach the Infra part and
/// no later one. Which list is in which pack, and why: docs/migrations/1194-platform-pack-partition.md.
/// `[<Tests>]` alone does NOT get a list into this run, which is why the registration guard runs over
/// it.
let private registeredTests =
    testList "ToolUp.Platform.Tests.Infra" [
        LocalFileStorageTests.tests
        InMemoryEventStoreTests.tests
        PersistentEventStoreTests.tests
        DataObjectStoreTests.tests
        DatasetStoreTests.tests
        VirtualDatasetTests.tests
        IModelRegistryContract.tests
        IExternalHandleStoreContract.tests
        IExternalHandleStoreContract.selfTests
        SidecarStoreTests.tests
        IColumnMappingStoreContract.tests
        DataCatalogTests.tests
        LineageStoreTests.tests
        InMemoryDataSourceTests.tests
        InMemoryDataSourceTests.payloadFormatTests
        DataSourceFidelityContract.selfTests
        OAuth1aSubstrateTests.tests
        OAuth1aSubstrateTests.flowTests
        TransientFaultPolicyTests.tests
        TimeSeriesStoreTests.tests
        TelemetrySinkTests.tests
        HealthCheckAggregatorTests.tests
        HealthStateTrackerTests.tests
        EntityOutboxTests.tests
        RelationshipTests.tests
        PrometheusMetricsSinkTests.tests
        JsonConsoleLoggerTests.tests
        BlobProviderProfileTests.tests
        DegradedCapabilityRegistryTests.tests
        InMemoryRateLimitStoreTests.tests
        InMemoryRateLimitStoreTests.contractTests
        BuildTranscriptTests.transcriptDeterminismTests
        BuildTranscriptTests.transcriptSensitivityTests
        BuildTranscriptTests.provenanceTests
        BuildTranscriptTests.deployRecordCanonicalFormTests
        BuildTranscriptTests.deployRecordVerificationTests
        BuildTranscriptTests.closureCanonicalFormTests
        BuildTranscriptTests.closureAttestationTests
        BuildTranscriptTests.closureCaptureTests
        BuildTranscriptTests.closureBindingTests
        WorkProvenanceSourceTests.tests
        SbomProjectionTests.sbomSchemaConformanceTests
        SbomProjectionTests.sbomUnattestedPresenceTests
        SbomProjectionTests.sbomDeterminismTests
        SbomProjectionTests.sbomHonestyTests
        SbomProjectionTests.sbomScopeStatementTests
        SbomProjectionTests.sbomEndpointTests
        PrerenderDeterminismTests.tests
        MemoizedComputeDispatcherTests.memoizationTests
        MemoizedComputeDispatcherTests.coalescingTests
        MemoizedComputeDispatcherTests.scopeIsolationTests
        MemoizedComputeDispatcherTests.ttlTests
        MemoizedComputeDispatcherTests.optInTests
        MemoizedComputeDispatcherTests.budgetCompositionTests
        MemoizedComputeDispatcherTests.durabilityTests
        MemoizedComputeDispatcherTests.evictionTests
        BudgetSeamTests.tests
        SignedWorkerOutcomeTests.tests
        SqliteLogStoreTests.tests
        BlobDocumentStoreTests.tests
        BlobDocumentStoreTests.raisingReadTests
        BlobDocumentStoreTests.byteFixtureTests
    ]

let private deliberatelyUnregistered: TestRegistrationGuard.Exemption list = []

/// The pack's measured slow lists. The rule, and how to re-derive it, are
/// beside the residue pack's declaration in src/ToolUp.Platform.Tests/Program.fs.
let private laneDeclaration: TestLaneFilter.LaneDeclaration = {
    Slow = []
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
            Row = "Platform.Infra"
            Assembly = Assembly.GetExecutingAssembly()
            GuardFloor = 5
            Exemptions = deliberatelyUnregistered
            Lanes = laneDeclaration
            Registered = registeredTests
        }
        argv