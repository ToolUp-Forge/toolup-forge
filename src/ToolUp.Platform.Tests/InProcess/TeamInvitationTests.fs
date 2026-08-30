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
    ToolUp.Platform.Teams.InMemoryPendingInviteStore(storage, silentLogger) :> IPendingInviteStore

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
                    IssuedAt = DateTime.UtcNow
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
                    IssuedAt = DateTime.UtcNow.AddDays(-8.0)
                }

            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage "expired2@example.com" {
                    TeamId = "t1"
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays(-7.0)
                    InviterUserId = "alice"
                    IssuedAt = DateTime.UtcNow.AddDays(-14.0)
                }

            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage "live@example.com" {
                    TeamId = "t1"
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays(7.0)
                    InviterUserId = "alice"
                    IssuedAt = DateTime.UtcNow
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
                    IssuedAt = DateTime.UtcNow
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
                    IssuedAt = DateTime.UtcNow.AddDays(-1.0)
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
                    IssuedAt = DateTime.UtcNow
                }

            let! consumedFresh = tryConsumePendingForUser (pendingStore storage) ts (audit :> IAuditLog) carol
            Expect.isSome consumedFresh "re-issued (non-expired) entry auto-joins on next sign-in"
        }
    ]

// ─── Phase 547 — pending-invite expiry observability ───────────────────
//
// The store's expiry sweep was silent before Phase 547: an invite that
// lapsed unconsumed left the invitee in neither Members nor Pending
// Invites and emitted no audit row. These tests pin the new behaviour:
// every dropped entry produces exactly one `TeamInviteExpired` under the
// team scope, repeat sweeps don't re-emit, and a store composed without
// an audit log stays byte-for-byte silent (GP 11).

let private expiredEntry teamId inviter issuedAt : PendingInviteByEmail = {
    TeamId = teamId
    Role = Member
    ExpiresAt = DateTime.UtcNow.AddSeconds -60.0
    InviterUserId = inviter
    IssuedAt = issuedAt
}

let private teamInviteExpiredRows (audit: CapturingAuditLog) =
    audit.Recorded
    |> List.choose (fun (scope, e) ->
        match e with
        | TeamInviteExpired p -> Some(scope, p)
        | _ -> None)

[<Tests>]
let pendingInviteExpiryAuditTests =
    testList "PendingInviteExpiryAudit" [
        testCaseAsync "sweepExpired emits one TeamInviteExpired per dropped entry; repeat sweep emits none"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()

            let store =
                ToolUp.Platform.Teams.InMemoryPendingInviteStore(storage, silentLogger, Some(audit :> IAuditLog))
                :> IPendingInviteStore

            // Seed three entries that are still valid at upsert time (so
            // upsert's opportunistic compaction leaves them in place and
            // emits nothing), then let them all lapse together and sweep.
            let soon email : PendingInviteByEmail = {
                TeamId = "teamA"
                Role = Member
                ExpiresAt = DateTime.UtcNow.AddSeconds 2.0
                InviterUserId = "alice@example.com"
                IssuedAt = DateTime.UtcNow
            }

            for email in [ "a@x.com"; "b@x.com"; "c@x.com" ] do
                let! upserted = store.Upsert(email, soon email)
                Expect.isOk upserted "seed upsert succeeds"

            Expect.isEmpty (teamInviteExpiredRows audit) "no expiry rows while entries are still valid"

            // Sleep past the 2s expiry (plus margin for CI scheduler jitter).
            do! Async.Sleep 2600

            let! swept = store.SweepExpired()
            Expect.equal swept (Ok 3) "sweep drops all three lapsed entries"

            let rows = teamInviteExpiredRows audit
            Expect.equal (List.length rows) 3 "exactly one TeamInviteExpired per dropped entry"

            Expect.isTrue
                (rows |> List.forall (fun (scope, _) -> scope = "team-teamA"))
                "each row recorded under the team scope"

            let emails = rows |> List.map (fun (_, p) -> p.InviteeEmail) |> List.sort
            Expect.equal emails [ "a@x.com"; "b@x.com"; "c@x.com" ] "each dropped email named once"

            // Repeat sweep: nothing left, no re-emission.
            let! sweptAgain = store.SweepExpired()
            Expect.equal sweptAgain (Ok 0) "second sweep finds nothing further"
            Expect.equal (List.length (teamInviteExpiredRows audit)) 3 "no re-emission on repeat sweep"
        }

        testCaseAsync "TryConsumeForEmail on a lapsed entry drops it and emits one TeamInviteExpired"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()

            let store =
                ToolUp.Platform.Teams.InMemoryPendingInviteStore(storage, silentLogger, Some(audit :> IAuditLog))
                :> IPendingInviteStore

            let issuedAt = DateTime.UtcNow.AddDays -3.0
            // Seed via the no-audit module function so the seeding is silent
            // and only the consume path's emission is under test.
            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert
                    storage
                    "late@x.com"
                    (expiredEntry "teamB" "alice@example.com" issuedAt)

            let! consumed = store.TryConsumeForEmail "late@x.com"
            Expect.equal consumed (Ok None) "a lapsed entry consumes as absent"

            let rows = teamInviteExpiredRows audit
            Expect.equal (List.length rows) 1 "exactly one expiry row on the consume path"

            let scope, payload = rows.Head
            Expect.equal scope "team-teamB" "recorded under the team scope"
            Expect.equal payload.InviteeEmail "late@x.com" "names the invitee email"
            Expect.equal payload.InviterUserId "alice@example.com" "names the inviter"
            Expect.equal payload.Role Member "carries the role"
            Expect.equal payload.IssuedAt issuedAt "carries the stored issue timestamp"
        }

        testCaseAsync "store composed without an audit log stays silent on expiry (GP 11)"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()

            // 2-arg constructor — no audit log wired. The store must still
            // sweep correctly; it simply records nothing.
            let store =
                ToolUp.Platform.Teams.InMemoryPendingInviteStore(storage, silentLogger) :> IPendingInviteStore

            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert
                    storage
                    "x@x.com"
                    (expiredEntry "teamC" "alice@example.com" (DateTime.UtcNow.AddDays -3.0))

            let! consumed = store.TryConsumeForEmail "x@x.com"
            Expect.equal consumed (Ok None) "lapsed entry still consumes as absent without a log"

            let! swept = store.SweepExpired()
            Expect.equal swept (Ok 0) "nothing left for the sweep after the consume dropped it"

            Expect.isEmpty audit.Recorded "no audit log wired into the store → no emission (GP 11)"
        }
    ]

// ─── Phase 547.B — expired-invite visibility (ListRecentlyExpiredInvites) ──
//
// The API projection over the 547.A audit rows: a lapsed invite is listed
// (with inviter / role / timestamps), an active one is not, a re-issued
// one leaves the list, an out-of-window lapse is excluded, and the
// Owner/Admin gate holds. This is the server half of the UI's
// active / expired / re-issued state machine.

/// Audit-log fake whose `GetAuditTrail` actually filters — the shared
/// `CapturingAuditLog` returns `[]` unconditionally, which would make
/// every visibility assertion below vacuous.
type private TrailAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent * DateTime>()

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Enqueue(scopeId, audit, DateTime.UtcNow) }

        member _.GetAuditTrail(scopeId, dateRange, eventType) = async {
            return
                recorded
                |> Seq.filter (fun (s, e, at) ->
                    s = scopeId
                    && (match eventType with
                        | Some t -> AuditEvent.eventTypeName e = t
                        | None -> true)
                    && (match dateRange with
                        | Some(fromAt, toAt) -> at >= fromAt && at <= toAt
                        | None -> true))
                |> Seq.sortByDescending (fun (_, _, at) -> at)
                |> Seq.map (fun (_, e, _) -> e)
                |> List.ofSeq
        }

/// Production-shaped per-request context for the expired-invite surface:
/// the handler resolves `IPendingInviteStore` (live-entry exclusion) from
/// `RequestServices`, mirroring the composition root's registration.
let private ctxWithPendingStore (storage: IBlobStorage) (store: IPendingInviteStore) (userId: string) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(storage) |> ignore
    services.AddSingleton<IPendingInviteStore>(store) |> ignore
    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx.Items["ToolUp.UserId"] <- box userId
    ctx

[<Tests>]
let expiredInviteVisibilityTests =
    testList "ExpiredInviteVisibility" [
        testCaseAsync "lapsed invite is listed; active is not; re-issue clears it (active/expired/re-issued)"
        <| async {
            do! CacheReset.invalidateAll ()
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = TrailAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"

            let store =
                ToolUp.Platform.Teams.InMemoryPendingInviteStore(storage, silentLogger, Some(audit :> IAuditLog))
                :> IPendingInviteStore

            // A live entry — must stay out of the expired list.
            let! seeded =
                store.Upsert(
                    "active@x.com",
                    {
                        TeamId = teamId
                        Role = Member
                        ExpiresAt = DateTime.UtcNow.AddDays 5.0
                        InviterUserId = "alice@example.com"
                        IssuedAt = DateTime.UtcNow
                    }
                )

            Expect.isOk seeded "live entry seeded"

            // A lapsed entry — seeded silently, then dropped by the consume
            // path, which emits the TeamInviteExpired row the API reads.
            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert
                    storage
                    "late@x.com"
                    (expiredEntry teamId "alice@example.com" (DateTime.UtcNow.AddDays -3.0))

            let! consumed = store.TryConsumeForEmail "late@x.com"
            Expect.equal consumed (Ok None) "lapsed entry consumed as absent"

            let ctx = ctxWithPendingStore storage store "alice@example.com"
            let api = teamInvitationApi sts ts (audit :> IAuditLog) cfg ctx

            let! listed = api.ListRecentlyExpiredInvites teamId

            match listed with
            | Error e -> failtestf "expected Ok, got Error %s" e
            | Ok rows ->
                Expect.equal (List.length rows) 1 "exactly the lapsed invite is listed"
                let row = rows.Head
                Expect.equal row.InviteeEmail "late@x.com" "names the lapsed email"
                Expect.equal row.InviterUserId "alice@example.com" "names the inviter"
                Expect.equal row.Role Member "carries the role"

            // Re-issue — the email holds a live pending entry again, so the
            // expired projection excludes it (the UI's "re-issued" state).
            let! reissued =
                api.IssuePendingInviteByEmail {
                    TeamId = teamId
                    Email = "late@x.com"
                    Role = Member
                    ExpiresIn = None
                }

            Expect.isOk reissued "re-issue succeeds"

            let! listedAfter = api.ListRecentlyExpiredInvites teamId

            match listedAfter with
            | Error e -> failtestf "expected Ok after re-issue, got Error %s" e
            | Ok rows -> Expect.isEmpty rows "a re-issued email leaves the expired list"
        }

        testCaseAsync "a lapse older than the 30-day window is not listed"
        <| async {
            do! CacheReset.invalidateAll ()
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = TrailAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"

            let store =
                ToolUp.Platform.Teams.InMemoryPendingInviteStore(storage, silentLogger, Some(audit :> IAuditLog))
                :> IPendingInviteStore

            // Recorded NOW (inside the GetAuditTrail date pre-filter) with an
            // out-of-window ExpiredAt — pins the payload-side window filter
            // specifically, the case of a sweep noticing an old lapse late.
            do!
                (audit :> IAuditLog)
                    .Record(
                        $"team-{teamId}",
                        TeamInviteExpired {
                            TeamId = teamId
                            InviteeEmail = "old@x.com"
                            InviterUserId = "alice@example.com"
                            Role = Member
                            IssuedAt = DateTime.UtcNow.AddDays -45.0
                            ExpiredAt = DateTime.UtcNow.AddDays -40.0
                        }
                    )

            let ctx = ctxWithPendingStore storage store "alice@example.com"
            let api = teamInvitationApi sts ts (audit :> IAuditLog) cfg ctx

            let! listed = api.ListRecentlyExpiredInvites teamId
            Expect.equal listed (Ok []) "out-of-window lapse excluded"
        }

        testCaseAsync "a Member caller is refused (Owner/Admin gate)"
        <| async {
            do! CacheReset.invalidateAll ()
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = TrailAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let api = mkApi ts sts (audit :> IAuditLog) "bob@example.com"

            let! listed = api.ListRecentlyExpiredInvites teamId
            Expect.isError listed "Owner/Admin gate enforced on the expired-invite listing"
        }
    ]

// ─── Phase 547.C — opt-in inviter notification on invite expiry ────────
//
// Composed through the real `ComposeTeamRuntime` registration so the test
// pins the wiring, not a hand-built notifier: opted in, a sweep publishes
// one `TransactionalEmail` to the inviter under the team scope; on the
// default config the identical sweep publishes nothing (GP 13) while the
// audit row still lands (547.A is independent of 547.C).

type private CapturingChannel() =
    let published = ConcurrentQueue<string * Notification>()
    member _.Published = published |> Seq.toList

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue(scopeId, notification) }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe _ = async { return () }

/// Run one lapsed-entry sweep through a store composed by
/// `ComposeTeamRuntime.registerTeamPermissionStores` under `config`,
/// returning what the channel saw and what the audit log recorded.
let private sweepUnderConfig (config: ServerConfig) : Async<(string * Notification) list * (string * AuditEvent) list> = async {
    do! CacheReset.invalidateAll ()
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let channel = CapturingChannel()
    let audit = CapturingAuditLog()
    let services = ServiceCollection()

    ComposeTeamRuntime.registerTeamPermissionStores
        services
        config
        storage
        (channel :> INotificationChannel)
        silentLogger
        (audit :> IAuditLog)
        None
    |> ignore

    use sp = services.BuildServiceProvider()

    let store = sp.GetService(typeof<IPendingInviteStore>) :?> IPendingInviteStore

    do!
        ToolUp.Platform.Teams.PendingInviteStore.upsert
            storage
            "lapsed@x.com"
            (expiredEntry "teamN" "alice@example.com" (DateTime.UtcNow.AddDays -3.0))

    let! swept = store.SweepExpired()
    Expect.equal swept (Ok 1) "sweep drops the lapsed entry"

    return channel.Published, audit.Recorded
}

[<Tests>]
let inviteExpiryNotificationTests =
    testList "InviteExpiryNotification" [
        testCaseAsync "opted in: sweep publishes one TransactionalEmail to the inviter under the team scope"
        <| async {
            let! published, _ =
                sweepUnderConfig {
                    cfg with
                        NotifyInviterOnInviteExpiry = true
                }

            match published with
            | [ scope, TransactionalEmail envelope ] ->
                Expect.equal scope "team-teamN" "published under the team scope"
                Expect.equal envelope.RecipientUserIds [ "alice@example.com" ] "addressed to the inviter"

                match envelope.Content with
                | InlineEmail(subject, body, _) ->
                    Expect.stringContains subject "expired" "subject names the expiry"
                    Expect.stringContains body "lapsed@x.com" "body names the invitee"
                | TemplatedEmail _ -> failtest "expected an inline email body"
            | other -> failtestf "expected exactly one TransactionalEmail publish, got %A" other
        }

        testCaseAsync "default config: the identical sweep publishes nothing (GP 13); the audit row still lands"
        <| async {
            let! published, recorded = sweepUnderConfig cfg

            Expect.isEmpty published "no notification without the opt-in"

            let expiredRows =
                recorded
                |> List.filter (fun (_, e) -> AuditEvent.eventTypeName e = "TeamInviteExpired")

            Expect.equal (List.length expiredRows) 1 "the 547.A audit emission is independent of the 547.C opt-in"
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
                    IssuedAt = DateTime.UtcNow
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
// ─── Phase 548 — on-demand pending-invite consumption ──────────────────
//
// `ITeamInviteApi.CheckMyInvites` runs the same consumption core as the
// middleware's session-window trigger, on demand, for the authenticated
// caller only. The caller is read from
// `HttpContext.Items["ToolUp.User"]` — the `AuthenticatedUser` the
// scope-resolution middleware stamps from the validated principal — so
// these tests stamp it the way the middleware does rather than passing
// an id through the API surface (there is no argument to pass: the
// method takes `unit`, which is the point).

let private ctxForAuthenticatedUser (store: IPendingInviteStore) (user: AuthenticatedUser) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IPendingInviteStore>(store) |> ignore
    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx.Items["ToolUp.User"] <- box user
    ctx.Items["ToolUp.UserId"] <- box user.UserId
    ctx

let private signedInAs (email: string) : AuthenticatedUser = {
    AuthenticatedUser.anonymous with
        UserId = email
        Email = Some email
}

[<Tests>]
let checkMyInvitesTests =
    testList "CheckMyInvites" [
        testCaseAsync "consumes a pending invite on demand and echoes the joined team"
        <| async {
            do! CacheReset.invalidateAll ()
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let email = "check-ondemand-happy@example.com"

            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage email {
                    TeamId = teamId
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays 7.0
                    InviterUserId = "alice@example.com"
                    IssuedAt = DateTime.UtcNow
                }

            let ctx = ctxForAuthenticatedUser (pendingStore storage) (signedInAs email)
            let api = teamInvitationApi sts ts (audit :> IAuditLog) cfg ctx

            match! api.CheckMyInvites() with
            | Error e -> failtestf "expected Ok, got Error %s" e
            | Ok None -> failtest "expected the pending invite to be consumed"
            | Ok(Some team) -> Expect.equal team.TeamId teamId "the joined team is echoed back"

            let! role = ts.GetMemberRole(teamId, email)
            Expect.equal role (Some Member) "the caller joined with the baked-in role"

            let names = audit.Recorded |> List.map (fun (_, e) -> AuditEvent.eventTypeName e)
            Expect.contains names "TeamInviteAcceptedFromPending" "the consumption core's join audit still fires"
        }

        testCaseAsync "nothing pending returns Ok None"
        <| async {
            do! CacheReset.invalidateAll ()
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! _ = provisionTeam ts "alice@example.com" "bob@example.com"

            let ctx =
                ctxForAuthenticatedUser (pendingStore storage) (signedInAs "check-nothing-pending@example.com")

            let api = teamInvitationApi sts ts (audit :> IAuditLog) cfg ctx

            let! result = api.CheckMyInvites()
            Expect.equal result (Ok None) "no pending entry is Ok None, not an error"
        }

        testCaseAsync "an expired pending invite returns Ok None and emits the expiry audit row"
        <| async {
            do! CacheReset.invalidateAll ()
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let email = "check-ondemand-expired@example.com"

            // Seeded through the raw store shim so the lapsed entry is
            // present on disk — the `IPendingInviteStore` consume path
            // is what drops it (and emits `TeamInviteExpired`, Phase
            // 547.A), which is exactly what this call must exercise.
            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert
                    storage
                    email
                    (expiredEntry teamId "alice@example.com" (DateTime.UtcNow.AddDays -3.0))

            let store =
                ToolUp.Platform.Teams.InMemoryPendingInviteStore(storage, silentLogger, Some(audit :> IAuditLog))
                :> IPendingInviteStore

            let ctx = ctxForAuthenticatedUser store (signedInAs email)
            let api = teamInvitationApi sts ts (audit :> IAuditLog) cfg ctx

            let! result = api.CheckMyInvites()
            Expect.equal result (Ok None) "an expired invite reads as nothing pending"

            let! role = ts.GetMemberRole(teamId, email)
            Expect.isNone role "an expired invite never joins the caller"

            let names = audit.Recorded |> List.map (fun (_, e) -> AuditEvent.eventTypeName e)
            Expect.contains names "TeamInviteExpired" "the lapse is visible in the audit trail (Phase 547.A)"
        }

        testCaseAsync "an anonymous caller is refused"
        <| async {
            do! CacheReset.invalidateAll ()
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let email = "check-ondemand-anonymous@example.com"

            // A live pending entry the caller would consume IF the
            // anonymous gate were missing — so the assertion below
            // cannot pass vacuously on an empty store.
            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage email {
                    TeamId = teamId
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays 7.0
                    InviterUserId = "alice@example.com"
                    IssuedAt = DateTime.UtcNow
                }

            // No `ToolUp.User` / `ToolUp.UserId` stamp at all — the
            // shape an unauthenticated request presents.
            let services = ServiceCollection()
            services.AddSingleton<IPendingInviteStore>(pendingStore storage) |> ignore
            let sp = services.BuildServiceProvider() :> IServiceProvider
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.RequestServices <- sp

            let api = teamInvitationApi sts ts (audit :> IAuditLog) cfg ctx

            let! result = api.CheckMyInvites()
            Expect.isError result "an anonymous caller is refused"

            let! role = ts.GetMemberRole(teamId, email)
            Expect.isNone role "the refused call consumed nothing"
        }

        testCaseAsync "a second call is idempotent — Ok None, membership unchanged"
        <| async {
            do! CacheReset.invalidateAll ()
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let ts = freshTeamStore ()
            let sts = freshShareTokenStore ()
            let audit = CapturingAuditLog()
            let! teamId = provisionTeam ts "alice@example.com" "bob@example.com"
            let email = "check-ondemand-idempotent@example.com"

            do!
                ToolUp.Platform.Teams.PendingInviteStore.upsert storage email {
                    TeamId = teamId
                    Role = Member
                    ExpiresAt = DateTime.UtcNow.AddDays 7.0
                    InviterUserId = "alice@example.com"
                    IssuedAt = DateTime.UtcNow
                }

            let ctx = ctxForAuthenticatedUser (pendingStore storage) (signedInAs email)
            let api = teamInvitationApi sts ts (audit :> IAuditLog) cfg ctx

            let! first = api.CheckMyInvites()
            Expect.isOk first "the first call consumes the invite"

            let! second = api.CheckMyInvites()
            Expect.equal second (Ok None) "the second call finds nothing pending"

            let! role = ts.GetMemberRole(teamId, email)
            Expect.equal role (Some Member) "membership survives the repeat call"

            let joins =
                audit.Recorded
                |> List.filter (fun (_, e) -> AuditEvent.eventTypeName e = "TeamInviteAcceptedFromPending")

            Expect.equal (List.length joins) 1 "exactly one join audit — the repeat call did not re-join"
        }
    ]