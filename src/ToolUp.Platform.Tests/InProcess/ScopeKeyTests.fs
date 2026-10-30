module ToolUp.Platform.Tests.InProcess.ScopeKeyTests

// The scope grammar, pinned byte for byte.
//
// Before `ScopeKey` existed the grammar relating a scope to its storage container
// (`team-{id}`, `user-{id}`, `session-{id}`, the reserved `_platform` shard,
// a claim's own scope id) was minted by string interpolation at five sites
// and parsed back by prefix tests at six. `ScopeKey` is that grammar as a
// closed type. The fixture below is what the tree produced BEFORE the type
// existed — the strings already at rest in every deployment's containers
// and audit streams — so a rendering that moves one byte goes red here
// rather than orphaning data (GP 11).

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.StorageScopeResolver

/// One row per scope shape the five mint sites produced: the key, the
/// container it was stored under, and the bare id it was keyed on.
let private fixture: (string * ScopeKey * string * string) list = [
    "team (middleware, resolver, configScope)", ScopeKey.Team "acme", "team-acme", "acme"
    "team, guid id", ScopeKey.Team "3f2a9c1e7b6d4e0f", "team-3f2a9c1e7b6d4e0f", "3f2a9c1e7b6d4e0f"
    "team id that itself contains the prefix", ScopeKey.Team "team-x", "team-team-x", "team-x"
    "user (middleware, resolvers, configScope, file management)", ScopeKey.User "alice", "user-alice", "alice"
    "user, email-shaped id", ScopeKey.User "alice@example.com", "user-alice@example.com", "alice@example.com"
    ("session (middleware, anonymous resolver)",
     ScopeKey.Session "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
     "session-0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
     "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0")
    "the reserved platform shard", ScopeKey.Platform PlatformShard.Platform, "_platform", "_platform"
    "a claim whose scope is a team container", ScopeKey.Claim "team-acme", "team-acme", "team-acme"
    "a claim whose scope is opaque", ScopeKey.Claim "survey-2026", "survey-2026", "survey-2026"
]

let private claimFor (scopeId: string) : ShareTokenClaim = {
    TokenId = "tok-1"
    ScopeId = scopeId
    ResourceKind = "survey"
    ResourceId = "s-1"
    AttributedHandle = None
    IssuedBy = "issuer"
    IssuedAt = DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero)
    ExpiresAt = DateTimeOffset(2026, 11, 29, 0, 0, 0, TimeSpan.Zero)
    UseLimit = None
    UsedCount = 0
    Revoked = false
    RateLimit = None
}

let private alice: AuthenticatedUser = {
    UserId = "alice"
    DisplayName = "Alice"
    Email = Some "alice@example.com"
    TenantId = None
    Roles = []
    DirectoryRoles = []
    Admission = PrincipalAdmission.Member
}

let private configWith (surfaces: SurfaceProfile list) = {
    ServerConfig.defaults with
        Surfaces = surfaces
}

let private renderingTests =
    testList "the renderings reproduce every byte at rest" [
        for name, key, container, scopeId in fixture do
            testCase name
            <| fun _ ->
                Expect.equal (ScopeKey.container key) container "container"
                Expect.equal (ScopeKey.scopeId key) scopeId "bare id"

                Expect.equal
                    (StorageScope.ofKey true key)
                    {
                        ScopeId = scopeId
                        Container = container
                        Persist = true
                    }
                    "the StorageScope projection renders both fields"

        testCase "ScopeKey.platform is the _platform shard"
        <| fun _ ->
            Expect.equal ScopeKey.platform (ScopeKey.Platform PlatformShard.Platform) "the named value"
            Expect.equal (ScopeKey.container ScopeKey.platform) ScopeKey.PlatformContainer "the literal agrees"

        testCase "the platform secret scope that compiles ahead of ScopeKey is the same shard"
        <| fun _ ->
            Expect.equal
                ComponentRequirements.PlatformScope
                ScopeKey.PlatformContainer
                "ComponentRequirements.PlatformScope is a named exception of the literal gate only because of compile order"
    ]

let private parseTests =
    testList "tryParseContainer — the one legacy parser" [
        for name, key, container, _ in fixture do
            testCase $"round-trips: {name}"
            <| fun _ ->
                match ScopeKey.tryParseContainer container with
                | Some parsed ->
                    // A claim's container is whatever scope it names, so it
                    // parses to THAT scope; the bytes still round-trip.
                    Expect.equal (ScopeKey.container parsed) container "container ∘ parse is the identity"

                    match key with
                    | ScopeKey.Claim _ -> ()
                    | _ -> Expect.equal parsed key "parse ∘ container is the identity"
                | None ->
                    match key with
                    | ScopeKey.Claim _ -> () // an opaque claim scope is no grammar container
                    | _ -> failtestf "%s did not parse" container

        testCase "an opaque claim scope parses to nothing"
        <| fun _ -> Expect.isNone (ScopeKey.tryParseContainer "survey-2026") "not a grammar container"

        testCase "refuses an empty or padded id, as the identity smart constructors do"
        <| fun _ ->
            for malformed in
                [
                    "team-"
                    "user-"
                    "session-"
                    "team- acme"
                    "team-acme "
                    "user-\talice"
                    "session-abc\n"
                ] do
                Expect.isNone (ScopeKey.tryParseContainer malformed) $"refuses %A{malformed}"

        testCase "refuses what is not the grammar"
        <| fun _ ->
            for other in [ ""; "acme"; "TEAM-acme"; "Team-acme"; "_platformx"; "_facts"; "teams-acme" ] do
                Expect.isNone (ScopeKey.tryParseContainer other) $"refuses %A{other}"

            Expect.isNone (ScopeKey.tryParseContainer null) "refuses null"
    ]

let private mintTests =
    testList "the mint sites render through ScopeKey" [
        testCase "Middleware.StorageScopeDerivation — every subject kind"
        <| fun _ ->
            let config = configWith Surfaces.anonymousAndTeam

            let cases = [
                AnonymousSession "s-1", ScopeKey.Session "s-1"
                Subject.AuthenticatedUser "alice", ScopeKey.User "alice"
                TeamMember("alice", "acme"), ScopeKey.Team "acme"
                Subject.ClaimBearer(claimFor "team-acme"), ScopeKey.Claim "team-acme"
            ]

            for subject, key in cases do
                Expect.equal
                    (Middleware.StorageScopeDerivation.tryKeyFromSubject subject)
                    (Some key)
                    $"%A{subject}: the key"

                let derived = Middleware.StorageScopeDerivation.tryFromSubject None config subject

                Expect.equal (derived |> Option.map _.Container) (Some(ScopeKey.container key)) "container"
                Expect.equal (derived |> Option.map _.ScopeId) (Some(ScopeKey.scopeId key)) "bare id"

            Expect.isNone
                (Middleware.StorageScopeDerivation.tryKeyFromSubject (Subject.PublicationReader "rhea"))
                "a publication reader holds no key"

        testCase "AccessContext.configScope and its inverse"
        <| fun _ ->
            let team = AccessContext.unrestricted (TeamMember("alice", "acme"))
            let user = AccessContext.unrestricted (Subject.AuthenticatedUser "alice")

            let claim = AccessContext.unrestricted (Subject.ClaimBearer(claimFor "survey-2026"))

            Expect.equal (AccessContext.configScope team) (Some(StorageScope.ofKey true (ScopeKey.Team "acme"))) "team"

            Expect.equal (AccessContext.configScope user) (Some(StorageScope.ofKey true (ScopeKey.User "alice"))) "user"

            Expect.equal
                (AccessContext.configScope claim)
                (Some(StorageScope.ofKey true (ScopeKey.Claim "survey-2026")))
                "claim"

            Expect.equal
                (AccessContext.forConfigScope "bob" (StorageScope.ofKey true (ScopeKey.Team "acme"))
                 |> Option.map _.Subject)
                (Some(TeamMember("bob", "acme")))
                "a team scope round-trips"

            Expect.equal
                (AccessContext.forConfigScope "bob" (StorageScope.ofKey true (ScopeKey.User "alice"))
                 |> Option.map _.Subject)
                (Some(Subject.AuthenticatedUser "alice"))
                "a user scope round-trips"

            Expect.isNone
                (AccessContext.forConfigScope "bob" (StorageScope.ofKey true (ScopeKey.Claim "survey-2026")))
                "a claim scope has no config context"

            Expect.isNone
                (AccessContext.forConfigScope "bob" {
                    ScopeId = "other"
                    Container = "team-acme"
                    Persist = true
                })
                "fields naming two principals name neither"

        testCaseAsync "the storage-scope resolvers"
        <| async {
            let request: ScopeResolutionRequest = {
                User = Some alice
                SessionId = Some "s-1"
                Headers = Map.empty
            }

            let! anonymous = (AnonymousScopeResolver() :> IStorageScopeResolver).Resolve request
            let! ephemeral = (AuthenticatedEphemeralScopeResolver() :> IStorageScopeResolver).Resolve request
            let! individual = (AuthenticatedScopeResolver() :> IStorageScopeResolver).Resolve request

            Expect.equal anonymous (Ok(StorageScope.ofKey false (ScopeKey.Session "s-1"))) "anonymous"
            Expect.equal ephemeral (Ok(StorageScope.ofKey false (ScopeKey.User "alice"))) "ephemeral"
            Expect.equal individual (Ok(StorageScope.ofKey true (ScopeKey.User "alice"))) "individual"
        }
    ]

let private persistenceTests =
    testList "ScopeKey.persistence — Phase 246's rule, written once" [
        let rows = [
            ("a session under an undeclared Anonymous fails closed",
             Surfaces.individual,
             ScopeKey.Session "s",
             { Persist = false; Defaulted = true })
            "an ephemeral anonymous surface",
            Surfaces.anonymous,
            ScopeKey.Session "s",
            { Persist = false; Defaulted = false }
            ("a persistent anonymous surface",
             [ SurfaceProfile.anonymousPersistent ],
             ScopeKey.Session "s",
             { Persist = true; Defaulted = false })
            "a trial user", Surfaces.trial, ScopeKey.User "u", { Persist = false; Defaulted = false }
            "an individual user", Surfaces.individual, ScopeKey.User "u", { Persist = true; Defaulted = false }
            ("a user under team-only Surfaces fails closed",
             Surfaces.team,
             ScopeKey.User "u",
             { Persist = false; Defaulted = true })
            "a team", Surfaces.team, ScopeKey.Team "t", { Persist = true; Defaulted = false }
            ("a team under individual Surfaces fails closed",
             Surfaces.individual,
             ScopeKey.Team "t",
             { Persist = false; Defaulted = true })
            "a claim is never matched", Surfaces.anonymous, ScopeKey.Claim "c", { Persist = true; Defaulted = false }
            "the platform shard always persists",
            Surfaces.anonymous,
            ScopeKey.platform,
            { Persist = true; Defaulted = false }
        ]

        for name, surfaces, key, expected in rows do
            testCase name
            <| fun _ ->
                Expect.equal (ScopeKey.persistence surfaces key) expected "persistence"
                Expect.equal (ScopeKey.persists surfaces key) expected.Persist "persists"
    ]

let private auditTests =
    testList "AuditScope.streamKey — the stream keys at rest" [
        testCase "a scoped key renders its BARE id, never its container"
        <| fun _ ->
            for _, key, _, scopeId in fixture do
                Expect.equal (AuditScope.streamKey (AuditScope.Scoped key)) scopeId $"%A{key}"

        testCase "the platform trail is _platform"
        <| fun _ -> Expect.equal (AuditScope.streamKey AuditScope.PlatformScope) "_platform" "platform trail"

        testCase "AuditSubject.fromScopeId reads through the one parser"
        <| fun _ ->
            let rows = [
                "_platform", UserAudit AuditSubject.PlatformSentinelUserId
                "", UserAudit AuditSubject.PlatformSentinelUserId
                "session-s1", AnonymousAudit "s1"
                "user-alice", UserAudit "alice"
                "team-acme", TeamAudit(AuditSubject.DispatcherSentinelUserId, "acme")
                "acme", UserAudit "acme"
                "team-", UserAudit "team-"
            ]

            for scopeId, expected in rows do
                Expect.equal (AuditSubject.fromScopeId scopeId) expected $"%A{scopeId}"
    ]

/// The key a scope renders from, read off the scope itself: present for
/// every grammar case, absent for every shape no key renders — the claim,
/// the anonymous scope, the arbitrary containers the fact-tier fixtures and
/// carried tokens use, and a scope whose id disagrees with its container.
let private keyTests =
    testList "StorageScope.tryKey and ResolvedScope.Key — the key a scope renders from" [
        let mint = StorageScopeResolver.ScopeResolution.ofStorageScope

        for name, key, container, scopeId in fixture do
            testCase name
            <| fun _ ->
                let scope = {
                    ScopeId = scopeId
                    Container = container
                    Persist = true
                }

                let expected =
                    match key with
                    | ScopeKey.Claim _ -> None
                    | _ -> Some key

                Expect.equal (StorageScope.tryKey scope) expected "tryKey"
                Expect.equal (StorageScope.tryKey (StorageScope.ofKey false key)) expected "tryKey round-trips ofKey"
                Expect.equal (mint scope).Key expected "ResolvedScope.Key"
                Expect.equal (mint scope).Storage (Some scope) "Storage is the minted record, byte for byte"

                Expect.equal
                    (StorageScope.tryTeamId scope)
                    (match key with
                     | ScopeKey.Team teamId -> Some teamId
                     | _ -> None)
                    "tryTeamId"

        let absent = [
            "an arbitrary fixture container", "t1", "container-t1"
            "a claim whose scope id is its team-shaped container", "team-acme", "team-acme"
            "a scope id that disagrees with its container", "other", "team-acme"
            "an empty team id", "", "team-"
            "a padded user id", " alice", "user- alice"
            "an unknown prefix", "acme", "org-acme"
        ]

        for name, scopeId, container in absent do
            testCase $"absent: {name}"
            <| fun _ ->
                let scope = {
                    ScopeId = scopeId
                    Container = container
                    Persist = true
                }

                Expect.isNone (StorageScope.tryKey scope) "tryKey"
                Expect.isNone (StorageScope.tryTeamId scope) "tryTeamId"
                Expect.isNone (mint scope).Key "ResolvedScope.Key"
                Expect.equal (mint scope).Storage (Some scope) "Storage is unchanged"

        testCase "the anonymous scope has no key"
        <| fun _ -> Expect.isNone ResolvedScope.anonymous.Key "anonymous"
    ]

let tests =
    testList "Phase 1060 — ScopeKey" [
        renderingTests
        parseTests
        mintTests
        persistenceTests
        auditTests
        keyTests
    ]