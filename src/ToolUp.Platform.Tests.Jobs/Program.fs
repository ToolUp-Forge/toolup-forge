// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.JobsPack

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs: the lists whose test files reach the Jobs part and no
/// later one. Which list is in which pack, and why: docs/migrations/1194-platform-pack-partition.md.
/// `[<Tests>]` alone does NOT get a list into this run, which is why the registration guard runs over
/// it.
let private registeredTests =
    testList "ToolUp.Platform.Tests.Jobs" [
        DatasetAssemblyTests.tests
        DatasetRevintageTests.tests
        ProvenanceLabelTests.tests
        ModelPromotionPolicyTests.tests
        ModelRegistrationObserverTests.tests
        IUserSchemaStoreContract.tests
        INotificationPreferenceStoreContract.tests
        JobStoreTests.tests
        CronExpressionTests.tests
        DataIngestorTests.tests
        OAuthSubstrateTests.stateStoreTests
        OAuthSubstrateTests.credentialFlowTests
        OAuthSubstrateTests.pkceCredentialFlowTests
        OAuthSubstrateTests.pkceFlowTests
        OAuthSubstrateTests.refresherScrubTests
        WebhookSecretRotationTests.tests
        WebhookSecretMigrationTests.tests
        WebhookFailureStateLeaseTests.tests
        CronTickElectionTests.tests
        WebhookDispatcherTests.tests
        PresenceChannelTests.tests
        IPresenceTrackerContract.tests
        IEntityLockStoreContract.tests
        ICrdtDocumentStoreContract.tests
        NotificationAddressBookTests.tests
        ExternalContactTests.tests
        SSEHandshakeTests.tests
        WebhookSecretRotationBroadcastTests.tests
        IDistributedLockContract.tests
        UsageLogTests.tests
        SecureByDefaultValidatorTests.tests
        InProcessOAuthTokenRefresherTests.tests
        SseTraceContributorTests.tests
        SsePerConnectionWriterTests.tests
        JobSchedulerInstanceValidatorTests.tests
        NotificationChannelInstanceValidatorTests.tests
        NotificationsExplicitOffTests.tests
        SseAuthModeValidatorTests.tests
        SmokeTestDefaultsTests.tests
        GrantNotificationTests.tests
        JobProgressTests.tests
        StoreFireAndForgetTests.tests
        TeamStoreReadPathTests.tests
        TeamStoreReadPathTests.fixtureTests
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
            Row = "Platform.Jobs"
            Assembly = Assembly.GetExecutingAssembly()
            GuardFloor = 11
            Exemptions = deliberatelyUnregistered
            Lanes = laneDeclaration
            Registered = registeredTests
        }
        argv