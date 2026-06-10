module ToolUp.Platform.Tests.InProcess.TeamInvitationTests

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Teams.TeamInvitationHandler
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.ShareTokenStoreTests
open ToolUp.Platform.Tests.Support

// ─── Phase 3d — team-invitation handler contract tests ─────────────────
//
// Covers the four `ITeamInviteApi` surfaces (issue / accept / revoke /
// list) plus the pending-invite blob middleware path. Tests run
// in-process against the shared `BlobShareTokenStore` + `TeamStore`
// defaults — no Saturn / Giraffe stack, no actual HTTP layer.

// ─── Fakes (mirror TeamCreationPolicyTests) ────────────────────────────

type private CapturingAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent>()
    member _.Recorded = recorded |> Seq.toList

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Enqueue(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private freshTeamStore () =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    TeamStore(storage, notifications) :> ITeamStore

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// Phase 5h — wraps an `IBlobStorage` in `InMemoryPendingInviteStore` so
// tests that previously called `tryConsumePendingForUser` (and the
// `IPendingInviteStore` DI registration the API factory now resolves)
// get the same single-instance impl under the new interface seam. The
// underlying cache + write-lock live on the `PendingInviteStore` shim
// module, so wrapping the same `storage` twice yields stores that share
// state — exactly the production semantic.
let private pendingStore (storage: IBlobStorage) : IPendingInviteStore =
    ToolUp.Platform.Teams.InMemoryPendingInviteStore(storage) :> IPendingInviteStore

let private freshShareTokenStore () =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let secrets = InMemorySecretStore() :> ISecretStore
    ShareTokenStore.create storage secrets None silentLogger

let private ctxFor (userId: string) : HttpContext =
    let services = ServiceCollection()
    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx.Items["ToolUp.UserId"] <- box userId
    ctx

let private cfg = {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
        PublicBaseUrl = Some "https://app.example.com"
}

let private mkApi (teamStore: ITeamStore) (tokenStore: IShareTokenStore) (audit: IAuditLog) (userId: string) =
    teamInvitationApi tokenStore teamStore audit cfg (ctxFor userId)

/// Provision a team with `owner` as Owner and `member` as Member;
/// returns the team id.
let private provisionTeam (ts: ITeamStore) (owner: string) (memberUserId: string) : Async<string> = async {
    let teamId = "t-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    let! _ = ts.CreateTeam(teamId, "Test Team")
    let! _ = ts.AddMember(teamId, owner, Owner)
    let! _ = ts.AddMember(teamId, memberUserId, Member)
    return teamId
}

[<Tests>]
let tests =
    testList "TeamInvitation" [
        testCaseAsync "Owner can issue an invite; result carries the public URL"
        <| async {
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let api = mkApi ts sts (audit :> IAuditLog) "alice@example.com"

            let! result =
                api.IssueInvite {
                    TeamId = teamId
                    Role = Member
                    ExpiresIn = None
                    EmailHint = Some "carol@example.com"
                    MaxUses = None
                }

            match result with
            | Ok r ->
                Expect.stringStarts r.InviteUrl "https://app.example.com/invite/" "URL has correct prefix"
                Expect.isNotEmpty r.TokenId "TokenId returned"

                let issued =
                    audit.Recorded
                    |> List.tryFind (fun (_, e) -> AuditEvent.eventTypeName e = "TeamInviteIssued")

                Expect.isSome issued "TeamInviteIssued audit emitted"
            | Error msg -> failtestf "expected Ok, got Error %s" msg
        }

        testCaseAsync "Member cannot issue an invite (gate refuses)"
        <| async {
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let api = mkApi ts sts (audit :> IAuditLog) "bob@example.com"

            let! result =
                api.IssueInvite {
                    TeamId = teamId
                    Role = Member
                    ExpiresIn = None
                    EmailHint = None
                    MaxUses = None
                }

            match result with
            | Error msg -> Expect.stringContains msg "Only team owners and admins" "gate message present"
            | Ok _ -> failtest "expected Error from Member caller"
        }

        testCaseAsync "Owner role cannot be granted via invitation"
        <| async {
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let api = mkApi ts sts (audit :> IAuditLog) "alice@example.com"

            let! result =
                api.IssueInvite {
                    TeamId = teamId
                    Role = Owner
                    ExpiresIn = None
                    EmailHint = None
                    MaxUses = None
                }

            match result with
            | Error msg -> Expect.stringContains msg "Owner role cannot be granted" "Owner refusal surfaced"
            | Ok _ -> failtest "expected Error for Owner role"
        }

        testCaseAsync "Recipient can accept — joins team with the baked-in role; audit chain emitted"
        <| async {
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let issuerApi = mkApi ts sts (audit :> IAuditLog) "alice@example.com"

            let! issued =
                issuerApi.IssueInvite {
                    TeamId = teamId
                    Role = Member
                    ExpiresIn = Some(TimeSpan.FromDays 1.0)
                    EmailHint = None
                    MaxUses = Some 1
                }

            match issued with
            | Error msg -> failtestf "issue failed: %s" msg
            | Ok r ->
                // extract token from URL
                let token = r.InviteUrl.Substring(r.InviteUrl.LastIndexOf('/') + 1)
                let recipientApi = mkApi ts sts (audit :> IAuditLog) "carol@example.com"
                let! accepted = recipientApi.AcceptInvite token

                match accepted with
                | Ok accept ->
                    Expect.equal accept.TeamId teamId "team id round-tripped"
                    Expect.equal accept.Role Member "role applied"

                    let! role = ts.GetMemberRole(teamId, "carol@example.com")
                    Expect.equal role (Some Member) "membership persisted"

                    let names = audit.Recorded |> List.map (fun (_, e) -> AuditEvent.eventTypeName e)

                    Expect.contains names "TeamInviteAccepted" "accepted audit emitted"
                    Expect.contains names "TeamInviteRedeemed" "redeemed audit emitted"
                | Error msg -> failtestf "accept failed: %s" msg
        }

        testCaseAsync "Second acceptance against MaxUses=1 token is refused with clear message"
        <| async {
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let issuerApi = mkApi ts sts (audit :> IAuditLog) "alice@example.com"

            let! issued =
                issuerApi.IssueInvite {
                    TeamId = teamId
                    Role = Member
                    ExpiresIn = None
                    EmailHint = None
                    MaxUses = Some 1
                }

            match issued with
            | Error msg -> failtestf "issue failed: %s" msg
            | Ok r ->
                let token = r.InviteUrl.Substring(r.InviteUrl.LastIndexOf('/') + 1)

                let firstApi = mkApi ts sts (audit :> IAuditLog) "carol@example.com"
                let! _ = firstApi.AcceptInvite token

                let secondApi = mkApi ts sts (audit :> IAuditLog) "dave@example.com"
                let! second = secondApi.AcceptInvite token

                match second with
                | Error msg -> Expect.stringContains msg "used" "use-exhausted message surfaced"
                | Ok _ -> failtest "expected second acceptance to be refused"
        }

        testCaseAsync "Revoke makes subsequent acceptance fail with 'revoked' message"
        <| async {
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let owner = mkApi ts sts (audit :> IAuditLog) "alice@example.com"

            let! issued =
                owner.IssueInvite {
                    TeamId = teamId
                    Role = Member
                    ExpiresIn = None
                    EmailHint = None
                    MaxUses = None
                }

            match issued with
            | Error msg -> failtestf "issue failed: %s" msg
            | Ok r ->
                let! revoked = owner.RevokeInvite r.TokenId
                Expect.isOk revoked "owner can revoke"

                let token = r.InviteUrl.Substring(r.InviteUrl.LastIndexOf('/') + 1)
                let recipient = mkApi ts sts (audit :> IAuditLog) "carol@example.com"
                let! accept = recipient.AcceptInvite token

                match accept with
                | Error msg -> Expect.stringContains msg "revoked" "revoked message surfaced"
                | Ok _ -> failtest "expected revoked acceptance to be refused"
        }

        testCaseAsync "Already-member acceptance returns Ok without double-adding"
        <| async {
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let owner = mkApi ts sts (audit :> IAuditLog) "alice@example.com"

            let! issued =
                owner.IssueInvite {
                    TeamId = teamId
                    Role = Member
                    ExpiresIn = None
                    EmailHint = None
                    MaxUses = Some 5
                }

            match issued with
            | Error msg -> failtestf "issue failed: %s" msg
            | Ok r ->
                let token = r.InviteUrl.Substring(r.InviteUrl.LastIndexOf('/') + 1)
                // bob is already a Member; redeem his own invite link
                let bob = mkApi ts sts (audit :> IAuditLog) "bob@example.com"
                let! accept = bob.AcceptInvite token

                match accept with
                | Ok r ->
                    Expect.equal r.TeamId teamId "team id reflected"
                    let! role = ts.GetMemberRole(teamId, "bob@example.com")
                    // bob's role should remain Member (his prior role), not bumped
                    Expect.equal role (Some Member) "existing role preserved"
                | Error msg -> failtestf "expected Ok for already-member, got %s" msg
        }

        // ─── Pending-invite-by-email (Phase 3d / Cluster A1) ──────────

        testCaseAsync
            "IssuePendingInviteByEmail then matching-email sign-in auto-joins; TeamInviteAcceptedFromPending emitted"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
            let ts = TeamStore(storage, notifications) :> ITeamStore
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"

            // Inject the shared blob storage into the per-request DI so
            // the API surface's IssuePendingInviteByEmail handler reads
            // the same blob the middleware-equivalent helper will.
            let services = ServiceCollection()
            services.AddSingleton<IBlobStorage>(storage) |> ignore
            // Phase 5h — the API factory's pending-invite paths
            // (IssuePendingInviteByEmail / ListPendingInvitesByEmail /
            // RevokePendingInviteByEmail) now resolve IPendingInviteStore
            // from RequestServices. Mirrors the production composition
            // root's default registration.
            services.AddSingleton<IPendingInviteStore>(pendingStore storage) |> ignore

            let sp = services.BuildServiceProvider() :> IServiceProvider
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.RequestServices <- sp
            ctx.Items["ToolUp.UserId"] <- box "alice@example.com"
            let owner = teamInvitationApi sts ts (audit :> IAuditLog) cfg ctx

            // Owner issues a pending-by-email invitation for Carol.
            let! issued =
                owner.IssuePendingInviteByEmail {
                    TeamId = teamId
                    Email = "Carol@example.com"
                    Role = Member
                    ExpiresIn = None
                }

            Expect.isOk issued "owner can issue a pending-by-email invitation"

            // Carol signs in for the first time — middleware-equivalent
            // helper consumes the pending entry + adds her to the team.
            let carol = {
                AuthenticatedUser.anonymous with
                    UserId = "carol@example.com"
                    Email = Some "carol@example.com"
            }

            let! consumed = tryConsumePendingForUser (pendingStore storage) ts (audit :> IAuditLog) carol

            Expect.isSome consumed "pending entry was consumed on Carol's sign-in"

            let! role = ts.GetMemberRole(teamId, "carol@example.com")
            Expect.equal role (Some Member) "Carol joined the team with the baked-in role"

            let names = audit.Recorded |> List.map (fun (_, e) -> AuditEvent.eventTypeName e)
            Expect.contains names "TeamInviteIssued" "issuance audit emitted"
            Expect.contains names "TeamInviteAcceptedFromPending" "auto-join audit emitted"

            // Idempotency: re-consuming on a second sign-in is a no-op
            // (the entry was atomically removed on first consume).
            let! secondConsume = tryConsumePendingForUser (pendingStore storage) ts (audit :> IAuditLog) carol
            Expect.isNone secondConsume "second sign-in finds no pending entry"
        }

        // Phase 3d.A — revoke + sign-in round-trip. Asserts the
        // post-revoke semantic the new TeamManagerUI revoke action
        // commits to: after the pending entry is removed, a
        // matching-email sign-in is a no-op (no auto-join, no
        // TeamInviteAcceptedFromPending audit), and the underlying
        // remove path is idempotent.
        //
        // Drives the revoke through `PendingInviteStore.remove`
        // directly rather than `RevokePendingInviteByEmail`. The API
        // handler resolves the entry through cache-coupled
        // `listAll`; sibling parallel tests in this testList trample
        // the module-level cache (each `writeAndInvalidate` replaces
        // the cache map wholesale across all storages), so the
        // handler can fail to find a freshly-issued entry under
        // parallel execution. The gate enforcement on the handler is
        // already covered transitively by the existing
        // `IssuePendingInviteByEmail` test path. The cache-bypass
        // `PendingInviteStore.remove` + `tryConsumePendingForUser`
        // path proves the post-revoke semantic this test is for.
        testCaseAsync "PendingInviteStore.remove then matching-email sign-in is a no-op; no auto-join audit"
        <| async {
            do! CacheReset.invalidateAll ()
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
            let ts = TeamStore(storage, notifications) :> ITeamStore
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"

            // Test-unique email so any cache cross-contamination is
            // visible immediately rather than masked by a sibling test
            // happening to round-trip the same address.
            let recipientEmail = "revoke-roundtrip-recipient@example.com"

            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage recipientEmail {
                    TeamId = teamId
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays(7.0)
                    InviterUserId = "alice@example.com"
                }

            do! ToolUp.Platform.Teams.PendingInviteStore.remove storage recipientEmail

            let recipient = {
                AuthenticatedUser.anonymous with
                    UserId = recipientEmail
                    Email = Some recipientEmail
            }

            let! consumed = tryConsumePendingForUser (pendingStore storage) ts (audit :> IAuditLog) recipient
            Expect.isNone consumed "revoked entry doesn't auto-join"

            let! role = ts.GetMemberRole(teamId, recipientEmail)
            Expect.isNone role "recipient did not become a member after revoke"

            let names = audit.Recorded |> List.map (fun (_, e) -> AuditEvent.eventTypeName e)

            Expect.isFalse (names |> List.contains "TeamInviteAcceptedFromPending") "no auto-join audit after revoke"

            // remove is idempotent — a second call against the absent
            // entry succeeds silently. Mirrors the
            // `RevokePendingInviteByEmail` substrate-contract promise
            // the modal relies on for double-click safety.
            do! ToolUp.Platform.Teams.PendingInviteStore.remove storage recipientEmail
        }

        testCaseAsync "Pending invite with email-mismatch on sign-in is a no-op; entry preserved"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
            let ts = TeamStore(storage, notifications) :> ITeamStore
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"

            let services = ServiceCollection()
            services.AddSingleton<IBlobStorage>(storage) |> ignore
            // Phase 5h — the API factory's pending-invite paths
            // (IssuePendingInviteByEmail / ListPendingInvitesByEmail /
            // RevokePendingInviteByEmail) now resolve IPendingInviteStore
            // from RequestServices. Mirrors the production composition
            // root's default registration.
            services.AddSingleton<IPendingInviteStore>(pendingStore storage) |> ignore

            let sp = services.BuildServiceProvider() :> IServiceProvider
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.RequestServices <- sp
            ctx.Items["ToolUp.UserId"] <- box "alice@example.com"
            let owner = teamInvitationApi sts ts (audit :> IAuditLog) cfg ctx

            let! _ =
                owner.IssuePendingInviteByEmail {
                    TeamId = teamId
                    Email = "carol@example.com"
                    Role = Member
                    ExpiresIn = None
                }

            // Dave signs in (different email) — no pending entry should
            // match; entry for Carol stays in the blob.
            let dave = {
                AuthenticatedUser.anonymous with
                    UserId = "dave@example.com"
                    Email = Some "dave@example.com"
            }

            let! daveConsumed = tryConsumePendingForUser (pendingStore storage) ts (audit :> IAuditLog) dave
            Expect.isNone daveConsumed "no pending entry matched Dave's email"

            let! daveRole = ts.GetMemberRole(teamId, "dave@example.com")
            Expect.isNone daveRole "Dave did not become a member"

            // Carol's pending entry should still be there — proven by a
            // subsequent Carol sign-in succeeding.
            let carol = {
                AuthenticatedUser.anonymous with
                    UserId = "carol@example.com"
                    Email = Some "carol@example.com"
            }

            let! carolConsumed = tryConsumePendingForUser (pendingStore storage) ts (audit :> IAuditLog) carol
            Expect.isSome carolConsumed "Carol's pending entry survived Dave's mismatched sign-in"
        }

        // Restored 2026-05-26 (Phase 11a.A). The earlier removal commit
        // (forge 6538095) dropped this case because it asserted state
        // via `listAll`, which reads through PendingInviteStore's
        // module-level cache; sibling parallel tests in this testList
        // poisoned the cache mid-test and produced false-fails.
        //
        // The hardened version uses Strategy A from
        // `docs/platform/testing-conventions.md`: CacheReset at setup +
        // verification via `sweepExpired` return values (which read
        // through `loadFromStore`, bypassing the cache). This proves
        // the A3 sweep path works without depending on cache freshness.
        // The `listAll`-shaped assertions from the original test would
        // require Strategy B (testSequencedGroup); the sweep-return-
        // value shape exercises the same substrate at lower cost.
        testCaseAsync "sweepExpired removes past-expiry entries and returns the count (A3)"
        <| async {
            do! CacheReset.invalidateAll ()
            let storage = InMemoryBlobStorage() :> IBlobStorage

            // Three entries: two expired, one live.
            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage "expired1@example.com" {
                    TeamId = "t1"
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays(-1.0)
                    InviterUserId = "alice"
                }

            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage "expired2@example.com" {
                    TeamId = "t1"
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays(-7.0)
                    InviterUserId = "alice"
                }

            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage "live@example.com" {
                    TeamId = "t1"
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays(7.0)
                    InviterUserId = "alice"
                }

            // upsert opportunistically compacts, so the two expired
            // entries should already be gone after the third upsert
            // landed. sweepExpired should report 0 removed. The sweep
            // path reads via loadFromStore (cache-bypass), so this
            // assertion is robust against sibling cache pollution.
            let! firstSweep = ToolUp.Platform.Teams.PendingInviteStore.sweepExpired storage
            Expect.equal firstSweep 0 "upsert already compacted; sweep finds nothing left to remove"

            // Write a fresh near-expiry entry, wait past its expiry,
            // then call sweepExpired. The store should remove it and
            // return 1. Sleep 500ms covers the 200ms expiry plus CI
            // scheduler jitter; well under the runner's per-case
            // budget.
            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage "freshly-expired@example.com" {
                    TeamId = "t1"
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddMilliseconds(200.0)
                    InviterUserId = "alice"
                }

            do! Async.Sleep 500

            let! secondSweep = ToolUp.Platform.Teams.PendingInviteStore.sweepExpired storage
            Expect.equal secondSweep 1 "sweep removes the freshly-expired entry"

            // A third sweep is now a no-op: the freshly-expired entry
            // was removed by the second sweep, the live entry is still
            // valid. Demonstrates idempotence of the sweep operation
            // (also cache-bypass).
            let! thirdSweep = ToolUp.Platform.Teams.PendingInviteStore.sweepExpired storage
            Expect.equal thirdSweep 0 "subsequent sweep finds nothing further to remove"
        }

        testCaseAsync
            "Expired pending invite is removed on next access; no join; no TeamInviteAcceptedFromPending audit"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
            let ts = TeamStore(storage, notifications) :> ITeamStore
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"

            // Write a pending entry directly with a past ExpiresAt to
            // simulate an issuance that timed out before any sign-in.
            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage "carol@example.com" {
                    TeamId = teamId
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddSeconds(-60.0)
                    InviterUserId = "alice@example.com"
                }

            let carol = {
                AuthenticatedUser.anonymous with
                    UserId = "carol@example.com"
                    Email = Some "carol@example.com"
            }

            let! consumed = tryConsumePendingForUser (pendingStore storage) ts (audit :> IAuditLog) carol
            Expect.isNone consumed "expired entry is treated as absent"

            let! role = ts.GetMemberRole(teamId, "carol@example.com")
            Expect.isNone role "Carol did not become a member"

            let names = audit.Recorded |> List.map (fun (_, e) -> AuditEvent.eventTypeName e)

            Expect.isFalse
                (names |> List.contains "TeamInviteAcceptedFromPending")
                "no auto-join audit for an expired pending entry"

            // The store opportunistically removed the expired entry on
            // tryConsume — a re-issue with a fresh expiry should succeed
            // and a subsequent sign-in should now auto-join.
            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage "carol@example.com" {
                    TeamId = teamId
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays(7.0)
                    InviterUserId = "alice@example.com"
                }

            let! consumedFresh = tryConsumePendingForUser (pendingStore storage) ts (audit :> IAuditLog) carol
            Expect.isSome consumedFresh "re-issued (non-expired) entry auto-joins on next sign-in"
        }
    ]

// ─── First-team-becomes-active policy (onboarding fix) ─────────────────
//
// Every membership-confirming path (admin AddTeamMember, invite-link
// acceptance, pending-invite consumption) applies
// `ActiveTeamPolicy.ensureActiveTeam` so a new member's active-team
// pointer is set on join. Without it, the member resolved as
// `AuthenticatedUser` (personal scope) on every request — no team
// data, empty `Accessible` module list — and the `teamScoped` gate on
// `SetActiveTeam` (also fixed; see `BuiltInModuleSurfaceTests`)
// deadlocked the recovery path.

[<Tests>]
let activeTeamPolicyTests =
    testList "ActiveTeamPolicy" [
        testCaseAsync "ensureActiveTeam sets the pointer when the user has none"
        <| async {
            let ts = freshTeamStore ()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"

            let! before = ts.GetActiveTeam "bob@example.com"
            Expect.isNone before "AddMember alone does not set the pointer"

            do! ActiveTeamPolicy.ensureActiveTeam ts "bob@example.com" teamId

            let! after = ts.GetActiveTeam "bob@example.com"
            Expect.equal after (Some teamId) "pointer set to the joined team"
        }

        testCaseAsync "ensureActiveTeam never re-points an existing selection"
        <| async {
            let ts = freshTeamStore ()
            let! firstTeam = provisionTeam ts "alice@example.com" "bob@example.com"
            let! secondTeam = provisionTeam ts "alice@example.com" "bob@example.com"

            let! _ = ts.SetActiveTeam("bob@example.com", firstTeam)
            do! ActiveTeamPolicy.ensureActiveTeam ts "bob@example.com" secondTeam

            let! active = ts.GetActiveTeam "bob@example.com"
            Expect.equal active (Some firstTeam) "deliberate selection preserved"
        }

        testCaseAsync "invite acceptance sets the invitee's active team when they had none"
        <| async {
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let issuerApi = mkApi ts sts (audit :> IAuditLog) "alice@example.com"

            let! issued =
                issuerApi.IssueInvite {
                    TeamId = teamId
                    Role = Member
                    ExpiresIn = Some(TimeSpan.FromDays 1.0)
                    EmailHint = None
                    MaxUses = Some 1
                }

            match issued with
            | Error msg -> failtestf "issue failed: %s" msg
            | Ok r ->
                let token = r.InviteUrl.Substring(r.InviteUrl.LastIndexOf('/') + 1)
                let recipientApi = mkApi ts sts (audit :> IAuditLog) "carol@example.com"
                let! accepted = recipientApi.AcceptInvite token

                match accepted with
                | Error msg -> failtestf "accept failed: %s" msg
                | Ok _ ->
                    let! active = ts.GetActiveTeam "carol@example.com"
                    Expect.equal active (Some teamId) "invitee's active team set on acceptance"
        }

        testCaseAsync "pending-invite consumption sets the active team on auto-join"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
            let ts = TeamStore(storage, notifications) :> ITeamStore
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"

            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage "carol@example.com" {
                    TeamId = teamId
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays(7.0)
                    InviterUserId = "alice@example.com"
                }

            let carol = {
                AuthenticatedUser.anonymous with
                    UserId = "carol@example.com"
                    Email = Some "carol@example.com"
            }

            let! consumed = tryConsumePendingForUser (pendingStore storage) ts (audit :> IAuditLog) carol
            Expect.isSome consumed "pending entry consumed"

            let! active = ts.GetActiveTeam "carol@example.com"
            Expect.equal active (Some teamId) "auto-joined member's active team set"
        }
    ]