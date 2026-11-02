// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.AuditPack

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs: the lists whose test files reach the Audit part and
/// no later one. Which list is in which pack, and why: docs/migrations/1194-platform-pack-partition.md.
/// `[<Tests>]` alone does NOT get a list into this run, which is why the registration guard runs over
/// it.
let private registeredTests =
    testList "ToolUp.Platform.Tests.Audit" [
        SignedExportTests.tests
        MinimumViableShapeTests.tests
        InMemoryAuditSinkTests.tests
        AuditReplicatorTests.tests
        Ed25519ArtifactSubstrateTests.tests
        InMemoryPendingInviteStoreTests.tests
        BlobPendingInviteStoreTests.tests
        BlobPendingInviteStoreTests.validatorGateTests
        AuditLogModeValidatorTests.tests
        EventStoreChainValidatorTests.tests
        AuditLogHealthCheckTests.tests
        AuditWriteFailureMetricTests.tests
        AuditFailurePolicyTests.tests
        PermissionAuditChainTests.tests
        AuthAuditHookTests.tests
        HostActionAuditTests.tests
        NotificationSilentlySkippedTests.tests
        ConversationExportAuditTests.tests
        ConversationExporterTests.tests
        AuditStreamKeyTests.tests
        InProcessRateLimiterTests.tests
        ConsentStateStoreTests.tests
        ConsentStateStoreTests.entityBackedTests
        ConsentStateStoreTests.restartPersistenceTests
        AdAnalyticsObservabilityTests.tests
        AuditViewApiHandlerTests.roleGateTests
        AuditViewApiHandlerTests.scopeIsolationTests
        AuditViewApiHandlerTests.filterTests
        AuditViewApiHandlerTests.pagingTests
        AuditViewApiHandlerTests.exportTests
        AuditViewApiHandlerTests.substratePairingTests
        EvidenceEnumerationCompletenessTests.derivationTests
        EvidenceEnumerationCompletenessTests.missingPositionTests
        EvidenceEnumerationCompletenessTests.boundedTests
        EvidenceEnumerationCompletenessTests.shorterRenderTests
        EvidenceEnumerationCompletenessTests.bundleStatementTests
        IEgressGateContract.tests
        IEvidencePackGeneratorContract.tests
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
            Row = "Platform.Audit"
            Assembly = Assembly.GetExecutingAssembly()
            GuardFloor = 8
            Exemptions = deliberatelyUnregistered
            Lanes = laneDeclaration
            Registered = registeredTests
        }
        argv