module ToolUp.Platform.Tests.InProcess.MembershipDoctorTests

open System.Collections.Concurrent
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.MembershipDoctor
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 546 — membership-integrity doctor ─────────────────────────
//
// `diagnose` detects and classifies the four drift kinds (email-keyed
// blob key, unresolvable blob key, orphan-team row, dangling
// active-team pointer); `repair` fixes exactly the provably-safe
// subset (row deletion / pointer clear), emits one `MemberRemoved`
// audit event per stripped row + the `MembershipChanged` cache-evict
// envelopes, and leaves email-keyed / unresolvable rows untouched.
// Drift is seeded by direct blob writes — the drifted states cannot be
// produced through the (sanitised) `TeamStore` API, which is the whole
// reason the doctor exists.

// ─── Fixtures ────────────────────────────────────────────────────────

let private upload (storage: IBlobStorage) (blobName: string) (text: string) =
    storage.Upload("_platform", blobName, Encoding.UTF8.GetBytes text)
    |> Async.RunSynchronously
    |> ignore

/// Seed a team record blob directly (id, display name).
let private seedTeam (storage: IBlobStorage) (teamId: string) =
    upload
        storage
        $"teams/{teamId}.json"
        $"{{\"teamId\":\"{teamId}\",\"name\":\"Team {teamId}\",\"createdAt\":\"2026-01-01T00:00:00.0000000Z\",\"archived\":false}}"

/// Seed a user's membership blob directly with rows naming `teamIds`.
let private seedMemberships (storage: IBlobStorage) (userId: string) (teamIds: string list) =
    let rows =
        teamIds
        |> List.map (fun t ->
            $"{{\"teamId\":\"{t}\",\"role\":\"Member\",\"joinedAt\":\"2026-01-01T00:00:00.0000000Z\"}}")
        |> String.concat ","

    upload storage $"memberships/{userId}.json" $"[{rows}]"

/// Seed a user's active-team pointer blob directly.
let private seedPointer (storage: IBlobStorage) (userId: string) (teamId: string) =
    upload storage $"active-team/{userId}.txt" teamId

let private downloadText (storage: IBlobStorage) (blobName: string) =
    match storage.Download("_platform", blobName) |> Async.RunSynchronously with
    | Ok bytes -> Some(Encoding.UTF8.GetString bytes)
    | Error _ -> None

/// Audit collector recording (scopeId, event) pairs.
type private RecordingAuditLog() =
    let events = ConcurrentBag<string * AuditEvent>()
    member _.Events = events |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, event) = async { events.Add(scopeId, event) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private diagnoseNow (storage: IBlobStorage) =
    diagnose (MembershipDoctorStorage.reads storage) |> Async.RunSynchronously

let private repairWith (storage: IBlobStorage) (audit: IAuditLog option) (channel: INotificationChannel option) =
    repair (MembershipDoctorStorage.reads storage) (MembershipDoctorStorage.writes storage audit channel "doctor-actor")
    |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "MembershipDoctor" [
        testCase "clean store yields an empty report"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            seedTeam storage "team-a"
            seedMemberships storage "user-1" [ "team-a" ]
            seedPointer storage "user-1" "team-a"

            Expect.isEmpty (diagnoseNow storage) "no drift, no findings"

        testCase "email-keyed blob is classified per row and proposed report-only"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            seedTeam storage "team-a"
            seedMemberships storage "ghost@example.com" [ "team-a" ]

            match diagnoseNow storage with
            | [ d ] ->
                Expect.equal d.Kind EmailKeyedRow "kind"
                Expect.equal d.UserId "ghost@example.com" "userId"
                Expect.equal d.TeamId (Some "team-a") "teamId"
                Expect.equal d.ProposedRepair ReAddUnderResolvedId "report-only"
            | other -> failtestf "expected exactly one finding, got %A" other

        testCase "unresolvable blob key (raw provider-prefixed sub) is classified report-only"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            seedTeam storage "team-a"
            seedMemberships storage "auth0|abc123" [ "team-a" ]

            match diagnoseNow storage with
            | [ d ] ->
                Expect.equal d.Kind UnresolvableRow "kind"
                Expect.equal d.ProposedRepair ReAddUnderResolvedId "report-only"
            | other -> failtestf "expected exactly one finding, got %A" other

        testCase "orphan-team row and dangling pointer are detected and classified"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            seedTeam storage "team-a"
            seedTeam storage "team-b"
            // Row naming a purged team + a pointer at a team the user
            // holds no row for.
            seedMemberships storage "user-1" [ "team-a"; "team-purged" ]
            seedPointer storage "user-1" "team-b"

            let findings = diagnoseNow storage
            let kinds = findings |> List.map (fun d -> d.Kind, d.TeamId)

            Expect.equal
                (Set.ofList kinds)
                (Set.ofList [ OrphanTeamRow, Some "team-purged"; DanglingActiveTeam, Some "team-b" ])
                "both drift kinds classified against the right team"

        testCase "pointer-only user (no membership blob) is a dangling pointer"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            seedTeam storage "team-a"
            seedPointer storage "user-gone" "team-a"

            match diagnoseNow storage with
            | [ d ] ->
                Expect.equal d.Kind DanglingActiveTeam "kind"
                Expect.equal d.UserId "user-gone" "userId"
                Expect.equal d.ProposedRepair ClearActiveTeamPointer "safe repair"
            | other -> failtestf "expected exactly one finding, got %A" other

        testCase "repair strips orphan rows, clears dangling pointers, and re-diagnoses clean"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            seedTeam storage "team-a"
            // user-1: one valid row, one orphan row, pointer at the
            // orphan team (the pointer's own row is orphaned).
            seedMemberships storage "user-1" [ "team-a"; "team-purged" ]
            seedPointer storage "user-1" "team-purged"

            let outcome = repairWith storage None None

            Expect.equal outcome.Repaired.Length 2 "row strip + pointer clear"
            Expect.isEmpty outcome.ReportOnly "nothing left for the operator"
            Expect.isEmpty (diagnoseNow storage) "repaired store re-diagnoses clean"

            Expect.equal
                (downloadText storage "memberships/user-1.json"
                 |> Option.map (fun t -> t.Contains "team-a", t.Contains "team-purged"))
                (Some(true, false))
                "the valid row survives, the orphan row is gone"

            Expect.isNone (downloadText storage "active-team/user-1.txt") "pointer blob deleted"

        testCase "repair never touches email-keyed or unresolvable rows"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            seedTeam storage "team-a"
            seedMemberships storage "ghost@example.com" [ "team-purged" ]
            seedPointer storage "ghost@example.com" "team-purged"
            seedMemberships storage "auth0|abc123" [ "team-a" ]
            let emailBlobBefore = downloadText storage "memberships/ghost@example.com.json"

            let outcome = repairWith storage None None

            Expect.isEmpty outcome.Repaired "nothing provably safe to fix"
            Expect.equal outcome.ReportOnly.Length 2 "both bad-key blobs reported"

            Expect.equal
                (downloadText storage "memberships/ghost@example.com.json")
                emailBlobBefore
                "email-keyed blob survives --repair byte-for-byte"

            Expect.isSome (downloadText storage "active-team/ghost@example.com.txt") "bad-key pointer left alone"

            Expect.equal (diagnoseNow storage |> List.length) 2 "report-only findings persist after repair"

        testCase "repair emits one actor-attributed MemberRemoved per stripped row, team-scoped"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            seedTeam storage "team-a"
            seedMemberships storage "user-1" [ "team-a"; "purged-x"; "purged-y" ]
            seedMemberships storage "user-2" [ "purged-x" ]
            let audit = RecordingAuditLog()

            let _ = repairWith storage (Some(audit :> IAuditLog)) None

            let removed =
                audit.Events
                |> List.choose (fun (scope, e) ->
                    match e with
                    | MemberRemoved p -> Some(scope, p.TeamId, p.AffectedUserId, p.UserId)
                    | _ -> None)

            Expect.equal
                (Set.ofList removed)
                (Set.ofList [
                    "purged-x", "purged-x", "user-1", "doctor-actor"
                    "purged-y", "purged-y", "user-1", "doctor-actor"
                    "purged-x", "purged-x", "user-2", "doctor-actor"
                ])
                "one MemberRemoved per stripped row, recorded against the team scope"

        testCase "repair publishes MembershipChanged so resolver caches evict"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            seedTeam storage "team-a"
            seedMemberships storage "user-1" [ "team-purged" ]
            seedPointer storage "user-1" "team-purged"
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel

            let published = ConcurrentBag<MembershipChangeKind * string * string>()

            channel.Subscribe(
                NotificationKind.PlatformReservedScope,
                fun envelope ->
                    match envelope.Notification with
                    | MembershipChanged p -> published.Add(p.ChangeKind, p.TeamId, p.AffectedUserId)
                    | _ -> ()
            )
            |> Async.RunSynchronously
            |> ignore

            let _ = repairWith storage None (Some channel)

            Expect.equal
                (published |> Set.ofSeq)
                (Set.ofList [
                    MembershipChangeKind.Removed, "team-purged", "user-1"
                    MembershipChangeKind.ActiveTeamSet, "team-purged", "user-1"
                ])
                "row strip publishes Removed; pointer clear publishes ActiveTeamSet"
    ]