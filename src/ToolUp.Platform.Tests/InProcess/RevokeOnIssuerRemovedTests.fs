module ToolUp.Platform.Tests.InProcess.RevokeOnIssuerRemovedTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.NotificationChannel
open ToolUp.ShareTokenStoreDecorators.RevokeOnIssuerRemoved
open ToolUp.Platform.Tests.InProcess.ShareTokenStoreTests

// ─── RevokeOnIssuerRemoved — Phase 66 C.5 integration tests (D.8) ────
//
// End-to-end exercise of the share-token decorator: a real
// `BlobShareTokenStore` (over `LocalFileStorage` in a temp dir, audited
// by a recording `IAuditLog`) wrapped by `RevokeOnIssuerRemovedStore`,
// fed `MembershipChanged` notifications through the default
// `InMemoryNotificationChannel`. Verifies the full chain: subscribe →
// react to `Removed` → enumerate the leaver's claims via `ListByIssuer`
// → revoke each → emit `ShareTokenRevoked` audit with the system actor.
//
// The handler revokes via `Async.Start` (fire-and-forget — the
// notification contract is one-way), and the audit `Record` is itself
// fire-and-forget, so assertions poll with a short timeout rather than
// reading sequentially.

/// Recording `IAuditLog` capturing every `(scopeId, event)` pair.
type private RecordingAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent>()
    member _.Events = recorded |> Seq.toList

    member _.ShareTokenRevocations =
        recorded
        |> Seq.choose (fun (scope, evt) ->
            match evt with
            | AuditEvent.ShareTokenRevoked p -> Some(scope, p)
            | _ -> None)
        |> Seq.toList

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Enqueue(scopeId, audit) }

        member _.GetAuditTrail(scopeId, _dateRange, _eventType) = async {
            return recorded |> Seq.filter (fun (s, _) -> s = scopeId) |> Seq.map snd |> List.ofSeq
        }

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private mkIssue scopeId resourceId issuedBy : ShareTokenIssueRequest = {
    ScopeId = scopeId
    ResourceKind = "forms.publishable"
    ResourceId = resourceId
    AttributedHandle = None
    IssuedBy = issuedBy
    ExpiresAt = None
    UseLimit = None
    RateLimit = None
}

/// Poll `predicate` until it returns true or `timeout` elapses.
let private pollUntil (timeout: TimeSpan) (predicate: unit -> Async<bool>) = async {
    let sw = Stopwatch.StartNew()
    let mutable satisfied = false

    while not satisfied && sw.Elapsed < timeout do
        let! r = predicate ()

        if r then satisfied <- true else do! Async.Sleep 25

    return satisfied
}

let private deliveryTimeout = TimeSpan.FromSeconds 2.0

/// Fresh `(decorator, inner, channel, audit, teamId)` per test so
/// concurrent runs over the same temp-dir root cannot interfere
/// (team ids are GUID-suffixed).
let private setup () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-revoke-issuer-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let secrets = InMemorySecretStore() :> ISecretStore
    let audit = RecordingAuditLog()

    let inner =
        ShareTokenStore.create storage secrets (Some(audit :> IAuditLog)) silentLogger

    let channel = InMemoryNotificationChannel(None) :> INotificationChannel
    let decorator = new RevokeOnIssuerRemovedStore(inner, channel, silentLogger)
    let teamId = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    decorator, inner, channel, audit, teamId

let private publishRemoved (channel: INotificationChannel) teamId userId =
    channel.Publish(
        NotificationKind.PlatformReservedScope,
        MembershipChanged {
            TeamId = teamId
            AffectedUserId = userId
            ChangeKind = MembershipChangeKind.Removed
            PublishedAt = DateTime.UtcNow
        }
    )

let tests =
    testList "RevokeOnIssuerRemoved — integration" [

        testCaseAsync "MembershipChanged.Removed revokes the leaver's claims and leaves others intact"
        <| async {
            let decorator, inner, channel, audit, teamId = setup ()
            use _ = decorator

            let! _ = inner.Issue(mkIssue teamId "form-1" "alice")
            let! _ = inner.Issue(mkIssue teamId "form-2" "alice")
            let! _ = inner.Issue(mkIssue teamId "form-3" "bob")

            do! publishRemoved channel teamId "alice"

            let! revoked =
                pollUntil deliveryTimeout (fun () -> async {
                    let! aliceClaims = inner.ListByIssuer(teamId, "alice")
                    return not aliceClaims.IsEmpty && (aliceClaims |> List.forall _.Revoked)
                })

            Expect.isTrue revoked "alice's claims all revoked within timeout"

            let! bobClaims = inner.ListByIssuer(teamId, "bob")
            Expect.equal bobClaims.Length 1 "bob still has his claim"
            Expect.isFalse (bobClaims |> List.exists _.Revoked) "bob's claim is untouched"
        }

        testCaseAsync "Revocation emits ShareTokenRevoked audit attributed to the system actor"
        <| async {
            let decorator, inner, channel, audit, teamId = setup ()
            use _ = decorator

            let! _ = inner.Issue(mkIssue teamId "form-a" "carol")
            let! _ = inner.Issue(mkIssue teamId "form-b" "carol")

            do! publishRemoved channel teamId "carol"

            let! audited = pollUntil deliveryTimeout (fun () -> async { return audit.ShareTokenRevocations.Length = 2 })

            Expect.isTrue audited "two ShareTokenRevoked audit events recorded"

            let revocations = audit.ShareTokenRevocations

            Expect.all
                revocations
                (fun (scope, p) -> scope = teamId && p.UserId = RevocationActor)
                "every revocation audited under the team scope with the system actor"

            let resources = revocations |> List.map (fun (_, p) -> p.ResourceId) |> List.sort
            Expect.equal resources [ "form-a"; "form-b" ] "both of carol's resources audited"
        }

        testCaseAsync "Re-delivery of the same Removed event is idempotent — no double revocation, no extra audit"
        <| async {
            let decorator, inner, channel, audit, teamId = setup ()
            use _ = decorator

            let! _ = inner.Issue(mkIssue teamId "form-x" "dave")

            do! publishRemoved channel teamId "dave"

            let! firstPass =
                pollUntil deliveryTimeout (fun () -> async { return audit.ShareTokenRevocations.Length = 1 })

            Expect.isTrue firstPass "first delivery revoked + audited once"

            // Re-deliver the identical event. The handler re-enumerates,
            // finds the claim already revoked, filters it out, and issues
            // no Revoke — so no second audit event is emitted.
            do! publishRemoved channel teamId "dave"
            do! Async.Sleep 300

            Expect.equal audit.ShareTokenRevocations.Length 1 "no second audit event on redelivery"

            let! claims = inner.ListByIssuer(teamId, "dave")
            Expect.equal claims.Length 1 "still exactly one claim"
            Expect.isTrue (claims |> List.forall _.Revoked) "claim remains revoked"
        }

        testCaseAsync "Non-Removed membership changes are ignored"
        <| async {
            let decorator, inner, channel, audit, teamId = setup ()
            use _ = decorator

            let! _ = inner.Issue(mkIssue teamId "form-add" "erin")

            // Added / RoleChanged / ActiveTeamSet must not trigger revocation.
            for kind in
                [
                    MembershipChangeKind.Added
                    MembershipChangeKind.RoleChanged
                    MembershipChangeKind.ActiveTeamSet
                ] do
                do!
                    channel.Publish(
                        NotificationKind.PlatformReservedScope,
                        MembershipChanged {
                            TeamId = teamId
                            AffectedUserId = "erin"
                            ChangeKind = kind
                            PublishedAt = DateTime.UtcNow
                        }
                    )

            do! Async.Sleep 300

            let! claims = inner.ListByIssuer(teamId, "erin")
            Expect.equal claims.Length 1 "claim still present"
            Expect.isFalse (claims |> List.exists _.Revoked) "no revocation from non-Removed events"
            Expect.equal audit.ShareTokenRevocations.Length 0 "no audit events from non-Removed events"
        }

        testCaseAsync "Dispose unsubscribes — events after teardown do not revoke"
        <| async {
            let decorator, inner, channel, audit, teamId = setup ()

            let! _ = inner.Issue(mkIssue teamId "form-dispose" "frank")

            (decorator :> IDisposable).Dispose()

            do! publishRemoved channel teamId "frank"
            do! Async.Sleep 300

            let! claims = inner.ListByIssuer(teamId, "frank")
            Expect.isFalse (claims |> List.exists _.Revoked) "no revocation after Dispose unsubscribed the handler"
            Expect.equal audit.ShareTokenRevocations.Length 0 "no audit events after Dispose"
        }
    ]