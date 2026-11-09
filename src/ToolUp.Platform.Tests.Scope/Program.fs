// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.ScopePack

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs: the lists whose test files reach the Scope part and
/// no later one. Which list is in which pack, and why: docs/migrations/1194-platform-pack-partition.md.
/// `[<Tests>]` alone does NOT get a list into this run, which is why the registration guard runs over
/// it.
let private registeredTests =
    testList "ToolUp.Platform.Tests.Scope" [
        IGrantConsentStoreContract.tests
        ShareTokenSigningKeyProvenanceValidatorTests.tests
        ShareTokenSigningKeyGovernanceTests.tests
        PermissionStoreTests.tests
        PermissionStoreTests.documentTests
        StorageScopeResolverTests.tests
        AuthorizationTests.tests
        GoogleIdentityCompanionTests.tests
        ShareTokenStoreTests.tests
        ShareTokenStoreTests.readPathTests
        ShareTokenAuthMiddlewareTests.tests
        ShareTokenMiddlewareRateLimitTests.tests
        ServiceAccountStoreTests.tests
        ServiceAccountStoreTests.pureTests
        ServiceAccountStoreTests.persistenceTests
        AnonymousSessionMigrationTests.tests
        StoreIdSanitisingTests.tests
        HeaderAuthProviderModeValidatorTests.tests
        MultiInstanceAdminCoherenceValidatorTests.tests
        OidcAudienceBindingValidatorTests.tests
        OidcClaimMappingAdvisoryValidatorTests.tests
        InviteEmailCapabilityValidatorTests.tests
        SecurityHardeningTests.tests
        SecurityHardeningTests.cspMiddlewareTests
        ForwardedHeadersTrustValidatorTests.tests
        PeerBearerAuthTests.tests
        PermissionStoreFailClosedTests.tests
        ActionAuthorizerTests.tests
        HostCapabilityRegistryTests.tests
        ModuleExposureMigrationTests.tests
        AdminMutationApprovalTests.tests
        GrantConsentTests.tests
        MembershipDoctorTests.tests
        SessionRegistryTests.tests
        SessionRegistryTests.derivationTests
        SessionRegistryTests.blobBackedTests
        BuiltInModuleSurfaceTests.tests
        ModuleVisibilityContractTests.resolutionTests
        ModuleVisibilityContractTests.ungovernedTests
        ModuleVisibilityContractTests.scopeWalkTests
        ModuleVisibilityContractTests.foldTests
        ModuleVisibilityContractTests.routeRegistryTests
        AnonymousSessionMigratorTests.tests
        DefaultSubjectResolverTests.tests
        CsrfCarveOutDerivationTests.tests
        SurfaceEnforcementMiddlewareTests.tests
        AnonymousSessionMigrationMiddlewareTests.tests
        PeerAuthPostureTests.ladderTests
        PeerAuthPostureTests.overlapTests
        PeerAuthPostureTests.advisoryTests
        FailClosedDispatchTests.tests
        Phase971PlatformTeamsTests.tests
        IdentityBytesAtRestTests.tests
    ]

/// The `[<Tests>]` bindings this pack deliberately does not run, each
/// with the reason. Phase 722: a deliberate omission is declared data the
/// guard reads, not prose beside the list — a stale entry here fails the
/// guard rather than quietly excusing something.
let private deliberatelyUnregistered = [
    {
        TestRegistrationGuard.Binding = "ToolUp.Platform.Tests.InProcess.BuiltInModuleSurfaceTests.visibilityTests"
        TestRegistrationGuard.Reason =
            "constructs the SDK's built-in CLIENT-side UI modules (FileManagerUI.create, "
            + "DataSubjectRequestAdminUI.create, HealthMonitorUI.create, …), whose bodies touch `Icons` — "
            + "Fable `importDefault` dummy code that throws under .NET. It is a Fable-tier pack, same class "
            + "as the HomeOverviewTests client-tier landing test in the ToolUp.AI.Client.Tests Fable "
            + "harness. See the 2026-07-20 orphaned-pack audit report."
    }
]

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
            Row = "Platform.Scope"
            Assembly = Assembly.GetExecutingAssembly()
            GuardFloor = 18
            Exemptions = deliberatelyUnregistered
            Lanes = laneDeclaration
            Registered = registeredTests
        }
        argv