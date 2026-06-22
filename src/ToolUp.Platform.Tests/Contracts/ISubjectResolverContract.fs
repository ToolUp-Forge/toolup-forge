module ToolUp.Platform.Tests.Contracts.ISubjectResolverContract

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.TeamManagement

// ─── ISubjectResolver contract pack — Phase 66 Stream D.1 ────────────
//
// Conformance bar for any `ISubjectResolver` implementing the four-step
// resolution algorithm (design §3.1, restated verbatim in
// `ISubjectResolver.fs`). The algorithm — not just the default impl —
// is the interface contract: the resolved `Subject` is the load-bearing
// pivot every downstream consumer derives storage scope, persistence,
// permissions, and audit attribution from, so a second implementation
// (a sidecar grain, an Akka actor per GP 12) must reproduce the same
// path decisions byte-for-byte.
//
// Callers bind via a factory parametrised over the deployment's
// `Surfaces` and an optional `ITeamStore` (the only substrate the
// algorithm consults — step 2a active-team + step-2a-tail membership
// re-check). The team-store shapes the algorithm reacts to are built
// here as inline stubs so the pack is self-contained and impl-agnostic:
// the binding supplies only the resolver, and every cache / notification
// dependency stays the impl's private concern. `DefaultSubjectResolver`
// binds to this pack from its InProcess test file.
//
// Covers all five D.1 paths: claim-bearer (step 1, dominant), anonymous
// (step 3, supported + unsupported), user-no-team (step 2b), user-with-
// team (step 2a happy + NotTeamMember + step-2b/2c fall-through), and
// the UnsupportedSubject failures.

/// Given the deployment's surfaces and an optional team store, produce
/// a fresh resolver. Invoked once per test so stateful implementations
/// (active-team caches) start from a clean slate.
type ResolverFactory = SurfaceProfile list -> ITeamStore option -> ISubjectResolver

// ─── Fixtures ────────────────────────────────────────────────────────

let private aUser: AuthenticatedUser = {
    UserId = "user-1"
    DisplayName = "User One"
    Email = Some "user-1@example.com"
    TenantId = None
    Roles = []
}

let private aClaim: ShareTokenClaim = {
    TokenId = "token-1"
    ScopeId = "scope-1"
    ResourceKind = "forms.publishable"
    ResourceId = "form-1"
    AttributedHandle = None
    IssuedBy = "issuer-1"
    IssuedAt = System.DateTimeOffset.UnixEpoch
    ExpiresAt = System.DateTimeOffset.UnixEpoch.AddDays 1.0
    UseLimit = None
    UsedCount = 0
    Revoked = false
    RateLimit = None
}

let private emptyRequest: SubjectResolutionRequest = {
    User = None
    SessionId = None
    Claim = None
    Headers = Map.empty
}

/// Inline `ITeamStore` whose active-team pointer and membership-role
/// answers are fixed — the two reads the §3.1 algorithm performs in
/// step 2. Every mutating method is a no-op `Error` since resolution
/// never writes. Constant answers make the impl's caching irrelevant
/// to the assertion (a warmed cache returns the same value).
let private teamStoreStub (activeTeam: string option) (role: TeamRole option) : ITeamStore =
    { new ITeamStore with
        member _.CreateTeam(_, _) = async { return Error "unused" }
        member _.DeleteTeam _ = async { return Error "unused" }
        member _.GetTeam _ = async { return None }
        member _.ListTeams() = async { return [] }
        member _.AddMember(_, _, _) = async { return Error "unused" }
        member _.RemoveMember(_, _) = async { return Error "unused" }
        member _.ChangeMemberRole(_, _, _) = async { return Error "unused" }
        member _.GetTeamsForUser _ = async { return [] }
        member _.GetTeamMembers _ = async { return [] }
        member _.GetMemberRole(_, _) = async { return role }
        member _.GetActiveTeam _ = async { return activeTeam }
        member _.SetActiveTeam(_, _) = async { return Error "unused" }
        member _.SetArchived(_, _) = async { return Error "unused" }
        member _.PurgeTeam _ = async { return Error "unused" }
    }

/// Inline `ITeamStore` whose active-team read raises — exercises the
/// step-2a "storage outage must surface distinctly, never degrade to a
/// silent no-active-team" contract.
let private throwingTeamStore: ITeamStore =
    { new ITeamStore with
        member _.CreateTeam(_, _) = async { return Error "unused" }
        member _.DeleteTeam _ = async { return Error "unused" }
        member _.GetTeam _ = async { return None }
        member _.ListTeams() = async { return [] }
        member _.AddMember(_, _, _) = async { return Error "unused" }
        member _.RemoveMember(_, _) = async { return Error "unused" }
        member _.ChangeMemberRole(_, _, _) = async { return Error "unused" }
        member _.GetTeamsForUser _ = async { return [] }
        member _.GetTeamMembers _ = async { return [] }
        member _.GetMemberRole(_, _) = async { return None }
        member _.GetActiveTeam _ = async { return raise (exn "blob store unreachable") }
        member _.SetActiveTeam(_, _) = async { return Error "unused" }
        member _.SetArchived(_, _) = async { return Error "unused" }
        member _.PurgeTeam _ = async { return Error "unused" }
    }

let private run (resolver: ISubjectResolver) (request: SubjectResolutionRequest) =
    resolver.Resolve request |> Async.RunSynchronously

// ─── The pack ────────────────────────────────────────────────────────

let tests (name: string) (factory: ResolverFactory) =
    testList $"{name} — ISubjectResolver contract" [

        // ── Step 1 — share-token claim dominates ──────────────────────

        testList "claim bearer (step 1)" [
            test "Claim present → ClaimBearer, regardless of Surfaces" {
                let resolver = factory Surfaces.individual None

                match
                    run resolver {
                        emptyRequest with
                            Claim = Some aClaim
                    }
                with
                | Ok(Subject.ClaimBearer c) -> Expect.equal c.TokenId "token-1" "the validated claim passes through"
                | other -> failtestf "expected ClaimBearer, got %A" other
            }

            test "Claim dominates even when an authenticated user is also present" {
                let resolver = factory Surfaces.individual None

                let request = {
                    emptyRequest with
                        User = Some aUser
                        Claim = Some aClaim
                }

                match run resolver request with
                | Ok(Subject.ClaimBearer _) -> ()
                | other -> failtestf "expected claim to dominate the user, got %A" other
            }

            test "Claim resolves even when ClaimBearer is not in Surfaces (step 1 is unconditional)" {
                // Step 1 never consults supportsClaimBearer — claim
                // presence short-circuits before any surface check.
                let resolver = factory Surfaces.anonymous None

                match
                    run resolver {
                        emptyRequest with
                            Claim = Some aClaim
                    }
                with
                | Ok(Subject.ClaimBearer _) -> ()
                | other -> failtestf "expected ClaimBearer despite anonymous-only Surfaces, got %A" other
            }
        ]

        // ── Step 3 — anonymous session ────────────────────────────────

        testList "anonymous (step 3)" [
            test "No user + Anonymous in Surfaces + SessionId → AnonymousSession with that id" {
                let resolver = factory Surfaces.anonymous None

                match
                    run resolver {
                        emptyRequest with
                            SessionId = Some "sess-1"
                    }
                with
                | Ok(Subject.AnonymousSession sid) -> Expect.equal sid "sess-1" "supplied session id is used verbatim"
                | other -> failtestf "expected AnonymousSession, got %A" other
            }

            test "No user + no SessionId → AnonymousSession with a freshly-generated non-empty id" {
                let resolver = factory Surfaces.anonymous None

                match run resolver emptyRequest with
                | Ok(Subject.AnonymousSession sid) -> Expect.isNotEmpty sid "a fresh session id is generated"
                | other -> failtestf "expected AnonymousSession, got %A" other
            }

            test "Anonymous AuthenticatedUser value is treated as the step-3 branch, not step 2" {
                // A `Some anonymous` user (credentials presented but
                // failed validation) must fall to the anonymous branch.
                let resolver = factory Surfaces.anonymous None

                let request = {
                    emptyRequest with
                        User = Some AuthenticatedUser.anonymous
                        SessionId = Some "sess-2"
                }

                match run resolver request with
                | Ok(Subject.AnonymousSession sid) -> Expect.equal sid "sess-2" "anonymous user resolves as a session"
                | other -> failtestf "expected AnonymousSession, got %A" other
            }

            test "No user + Anonymous NOT in Surfaces → UnsupportedSubject AnonymousKind" {
                let resolver = factory Surfaces.individual None

                match run resolver emptyRequest with
                | Error(SubjectResolutionError.UnsupportedSubject AnonymousKind) -> ()
                | other -> failtestf "expected UnsupportedSubject AnonymousKind, got %A" other
            }
        ]

        // ── Step 2 — authenticated subject ────────────────────────────

        testList "authenticated user — no team surface (step 2b / 2c)" [
            test "User + AuthenticatedUser in Surfaces (no Team) → AuthenticatedUser" {
                let resolver = factory Surfaces.individual None

                match run resolver { emptyRequest with User = Some aUser } with
                | Ok(Subject.AuthenticatedUser uid) -> Expect.equal uid "user-1" "resolves to the individual shape"
                | other -> failtestf "expected AuthenticatedUser, got %A" other
            }

            test "User + only Anonymous in Surfaces → UnsupportedSubject UserKind (step 2c)" {
                let resolver = factory Surfaces.anonymous None

                match run resolver { emptyRequest with User = Some aUser } with
                | Error(SubjectResolutionError.UnsupportedSubject UserKind) -> ()
                | other -> failtestf "expected UnsupportedSubject UserKind, got %A" other
            }
        ]

        testList "authenticated user — team surface (step 2a)" [
            test "Team surface + active team + still a member → TeamMember" {
                let store = teamStoreStub (Some "acme") (Some Owner)
                let resolver = factory Surfaces.team (Some store)

                match run resolver { emptyRequest with User = Some aUser } with
                | Ok(Subject.TeamMember(uid, tid)) ->
                    Expect.equal uid "user-1" "carries the user id"
                    Expect.equal tid "acme" "carries the active team id"
                | other -> failtestf "expected TeamMember, got %A" other
            }

            test
                "Team surface + no active team + AuthenticatedUser also in Surfaces → AuthenticatedUser (step 2b fall-through)" {
                let store = teamStoreStub None None

                let resolver =
                    factory [ SurfaceProfile.team; SurfaceProfile.individual ] (Some store)

                match run resolver { emptyRequest with User = Some aUser } with
                | Ok(Subject.AuthenticatedUser uid) ->
                    Expect.equal uid "user-1" "no active team falls through to individual"
                | other -> failtestf "expected AuthenticatedUser fall-through, got %A" other
            }

            test
                "Team surface + no active team + no AuthenticatedUser in Surfaces → UnsupportedSubject UserKind (step 2c)" {
                let store = teamStoreStub None None
                let resolver = factory Surfaces.team (Some store)

                match run resolver { emptyRequest with User = Some aUser } with
                | Error(SubjectResolutionError.UnsupportedSubject UserKind) -> ()
                | other -> failtestf "expected UnsupportedSubject UserKind, got %A" other
            }

            test "Team surface + active team set but membership revoked → NotTeamMember" {
                // Active-team pointer present, but the per-request
                // membership probe returns no role (removed between the
                // pointer write and this request). Must deny, naming
                // the team — never grant on a stale pointer.
                let store = teamStoreStub (Some "acme") None
                let resolver = factory Surfaces.team (Some store)

                match run resolver { emptyRequest with User = Some aUser } with
                | Error(SubjectResolutionError.NotTeamMember tid) ->
                    Expect.equal tid "acme" "names the team that denied"
                | other -> failtestf "expected NotTeamMember, got %A" other
            }

            test "Team surface + active-team read fails → SubjectResolutionFailed (never a silent no-team)" {
                let resolver = factory Surfaces.team (Some throwingTeamStore)

                match run resolver { emptyRequest with User = Some aUser } with
                | Error(SubjectResolutionError.SubjectResolutionFailed msg) ->
                    Expect.stringContains msg "unreachable" "carries the underlying store failure"
                | other -> failtestf "expected SubjectResolutionFailed, got %A" other
            }
        ]
    ]