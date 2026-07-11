// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.MembershipDoctor

open System.Text
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement

// ─── Phase 546 — membership-integrity doctor ─────────────────────────
//
// Deployment history accumulates stale membership rows the SDK never
// detects: email-string-keyed rows from the pre-invite-flow add path,
// rows keyed by a JWT `sub` after the deployment switched identity
// resolution to `oid` (the same person now resolves to a different
// userId), and rows naming a team purged out from under them. They
// render as ghost members in the Team Manager while the real person
// appears missing. Phase 131 documented that membership rows are
// admin-asserted, not identity-proof — drift is expected and needs a
// detector + opt-in repairer, in the same report-then-fix spirit as
// the config preflight validators.
//
// `diagnose` is pure classification over injected reads (GP 5) —
// unit-testable without storage. `repair` applies only the provably
// safe subset (delete rows naming a nonexistent team, clear dangling
// active-team pointers) and emits a `MemberRemoved` audit event per
// row it touches (GP 6). Email-keyed and unresolvable rows are
// report-only: the right fix — re-add under the resolved id — needs
// operator knowledge the doctor does not have (GP 11).
//
// The `toolup memberships doctor` CLI verb mirrors `diagnose` over the
// local-file blob layout in pure BCL (the CLI carries no SDK
// reference); `ToolUp.Cli.Tests` pins the two to the same
// classification via a fixture-tree parity test — the same anti-drift
// mechanism the Phase 166 stamp round-trip uses.

/// What is wrong with a membership row / pointer.
type MembershipFindingKind =
    /// The membership blob is keyed by an email address (`userId`
    /// contains `@`) — the pre-invite-flow add path wrote it before
    /// identity resolution existed.
    | EmailKeyedRow
    /// The membership blob's `userId` fails the Phase 131 identity
    /// sanitiser — it can never be produced by the current resolution
    /// path (e.g. a raw provider-prefixed JWT `sub`).
    | UnresolvableRow
    /// A membership row names a team with no team record — the team
    /// was purged out from under the row.
    | OrphanTeamRow
    /// The user's active-team pointer names a team the user holds no
    /// (valid) membership row for.
    | DanglingActiveTeam

/// What the doctor proposes to do about a finding.
type MembershipRepair =
    /// Safe: delete the membership row (the team it names no longer
    /// exists, so the row can grant nothing legitimate).
    | DeleteMembershipRow
    /// Safe: clear the active-team pointer (the user re-selects a team
    /// on their next visit; `ActiveTeamPolicy` re-points members with
    /// exactly one team automatically).
    | ClearActiveTeamPointer
    /// Report-only: the row must be re-added under the resolved id by
    /// an operator who knows who the row was meant to name.
    | ReAddUnderResolvedId

/// One finding — data, not prose, so the CLI, an admin surface, and
/// tests all consume the same output (546.B).
type MembershipDiagnosis = {
    Kind: MembershipFindingKind
    /// The id the membership / pointer blob is keyed by. May itself be
    /// the drifted value (an email, a raw JWT `sub`) — that is the
    /// point of the report; render it to operators, never echo it back
    /// to unauthenticated callers.
    UserId: string
    /// The team the finding is about. `None` when the finding is the
    /// blob key itself and the blob holds no rows.
    TeamId: string option
    /// Human-readable why. Categorised — never carries attacker-
    /// controlled bytes beyond the ids already in the record.
    Evidence: string
    ProposedRepair: MembershipRepair
}

/// Injected reads `diagnose` classifies over. Build from any store via
/// `MembershipDoctorStorage.reads`, or hand-roll for tests.
type MembershipReads = {
    /// User ids keyed by a `memberships/{userId}.json` blob.
    ListMembershipUserIds: unit -> Async<string list>
    /// The user's stored membership rows (empty when the blob is
    /// missing).
    LoadMemberships: string -> Async<StoredMembership list>
    /// Team ids that have a team record.
    ListTeamIds: unit -> Async<Set<string>>
    /// User ids that have an active-team pointer blob (a pointer can
    /// outlive its membership blob).
    ListActiveTeamUserIds: unit -> Async<string list>
    /// The user's active-team pointer, if set.
    GetActiveTeam: string -> Async<string option>
}

/// Injected writes `repair` applies the safe subset through. Build from
/// a store via `MembershipDoctorStorage.writes`, or hand-roll for tests.
type MembershipWrites = {
    /// Persist the user's membership rows (post-strip).
    SaveMemberships: string -> StoredMembership list -> Async<unit>
    /// Clear the user's active-team pointer. Receives the dangling team
    /// id so implementations can publish cache-eviction envelopes.
    ClearActiveTeam: string -> string -> Async<unit>
    /// Record a `MemberRemoved` audit event for a stripped row —
    /// `teamId`, then the affected user id (GP 6).
    EmitMemberRemoved: string -> string -> Async<unit>
}

/// What a `repair` run did and what it deliberately left alone.
type MembershipRepairOutcome = {
    /// Findings the safe subset fixed this run.
    Repaired: MembershipDiagnosis list
    /// Findings left for the operator (email-keyed / unresolvable rows).
    ReportOnly: MembershipDiagnosis list
}

/// Classify the blob-key id itself. Email-keyed takes precedence over
/// the sanitiser (an email also fails the charset check, but the
/// email-keyed classification carries the actionable provenance).
let private classifyUserIdKey (userId: string) : (MembershipFindingKind * string) option =
    if userId.Contains '@' then
        Some(EmailKeyedRow, "blob is keyed by an email address (pre-invite-flow add path)")
    else
        match IdentitySanitiser.sanitiseScopeId userId with
        | Error reason -> Some(UnresolvableRow, sprintf "blob key fails identity sanitisation: %s" reason)
        | Ok _ -> None

/// Walk every membership blob + active-team pointer and classify the
/// drift. Pure over the injected reads; deterministic order (sorted by
/// user id). A clean store yields `[]`.
let diagnose (reads: MembershipReads) : Async<MembershipDiagnosis list> = async {
    let! teamIds = reads.ListTeamIds()
    let! membershipUsers = reads.ListMembershipUserIds()
    let! pointerUsers = reads.ListActiveTeamUserIds()

    let allUsers = membershipUsers @ pointerUsers |> List.distinct |> List.sort

    let! perUser =
        allUsers
        |> List.map (fun userId -> async {
            let! rows = reads.LoadMemberships userId

            match classifyUserIdKey userId with
            | Some(kind, evidence) ->
                // A bad blob key taints everything under it — every row
                // is report-only (the pointer blob shares the key and is
                // left alone too: repair must not touch an email-keyed
                // user at all).
                return
                    match rows with
                    | [] -> [
                        {
                            Kind = kind
                            UserId = userId
                            TeamId = None
                            Evidence = evidence
                            ProposedRepair = ReAddUnderResolvedId
                        }
                      ]
                    | rows ->
                        rows
                        |> List.map (fun r -> {
                            Kind = kind
                            UserId = userId
                            TeamId = Some r.TeamId
                            Evidence = evidence
                            ProposedRepair = ReAddUnderResolvedId
                        })
            | None ->
                let orphanFindings =
                    rows
                    |> List.filter (fun r -> not (teamIds.Contains r.TeamId))
                    |> List.map (fun r -> {
                        Kind = OrphanTeamRow
                        UserId = userId
                        TeamId = Some r.TeamId
                        Evidence = "membership row names a team with no team record"
                        ProposedRepair = DeleteMembershipRow
                    })

                let! pointer = reads.GetActiveTeam userId

                let pointerFindings =
                    match pointer with
                    | None -> []
                    | Some teamId when rows |> List.exists (fun r -> r.TeamId = teamId && teamIds.Contains teamId) -> []
                    | Some teamId ->
                        let evidence =
                            if rows |> List.exists (fun r -> r.TeamId = teamId) then
                                "active-team pointer names a team whose membership row is itself orphaned"
                            elif List.isEmpty rows then
                                "active-team pointer exists but the user has no membership rows"
                            else
                                "active-team pointer names a team the user is not a member of"

                        [
                            {
                                Kind = DanglingActiveTeam
                                UserId = userId
                                TeamId = Some teamId
                                Evidence = evidence
                                ProposedRepair = ClearActiveTeamPointer
                            }
                        ]

                return orphanFindings @ pointerFindings
        })
        |> Async.Sequential

    return perUser |> List.concat
}

/// Apply the safe repair subset: strip orphan-team rows (one save per
/// user, one `MemberRemoved` audit event per stripped row) and clear
/// dangling active-team pointers. Email-keyed / unresolvable rows are
/// never touched — they come back in `ReportOnly`. Re-derives the
/// diagnosis from a fresh read so it never acts on a stale report.
let repair (reads: MembershipReads) (writes: MembershipWrites) : Async<MembershipRepairOutcome> = async {
    let! diagnoses = diagnose reads

    let safe, reportOnly =
        diagnoses
        |> List.partition (fun d ->
            match d.ProposedRepair with
            | DeleteMembershipRow
            | ClearActiveTeamPointer -> true
            | ReAddUnderResolvedId -> false)

    let rowDeletions =
        safe
        |> List.filter (fun d -> d.ProposedRepair = DeleteMembershipRow)
        |> List.groupBy _.UserId

    for (userId, findings) in rowDeletions do
        let orphanTeams = findings |> List.choose _.TeamId |> Set.ofList
        let! rows = reads.LoadMemberships userId
        let kept = rows |> List.filter (fun r -> not (orphanTeams.Contains r.TeamId))
        do! writes.SaveMemberships userId kept

        for teamId in orphanTeams do
            do! writes.EmitMemberRemoved teamId userId

    for d in safe |> List.filter (fun d -> d.ProposedRepair = ClearActiveTeamPointer) do
        match d.TeamId with
        | Some teamId -> do! writes.ClearActiveTeam d.UserId teamId
        | None -> ()

    return {
        Repaired = safe
        ReportOnly = reportOnly
    }
}

/// Build `MembershipReads` / `MembershipWrites` over the `_platform`
/// blob layout `TeamStore` owns (`memberships/{userId}.json`,
/// `teams/{teamId}.json`, `active-team/{userId}.txt`). The doctor is
/// the diagnostics twin of `TeamStore`, so it reuses the store's
/// internal blob-name helpers + JSON codec rather than mirroring them.
module MembershipDoctorStorage =

    let private idFromBlobName (prefix: string) (suffix: string) (name: string) : string option =
        // `IBlobStorage.List` names are forward-slash separated on the
        // in-memory / cloud backends, but `LocalFileStorage` returns
        // OS-relative paths — backslashed on Windows. Normalise before
        // matching so the doctor reads every backend identically.
        let name = name.Replace('\\', '/')

        if
            name.StartsWith prefix
            && name.EndsWith suffix
            && name.Length > prefix.Length + suffix.Length
        then
            Some(name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length))
        else
            None

    /// Reads over the `_platform` membership layout of `storage`.
    let reads (storage: IBlobStorage) : MembershipReads = {
        ListMembershipUserIds =
            fun () -> async {
                let! names = storage.List(platformContainer, "memberships/")
                return names |> List.choose (idFromBlobName "memberships/" ".json")
            }
        LoadMemberships =
            fun userId -> async {
                let! result = storage.Download(platformContainer, membershipBlobName userId)

                return
                    match result with
                    | Ok bytes -> Json.deserializeMemberships bytes
                    | Error _ -> []
            }
        ListTeamIds =
            fun () -> async {
                let! names = storage.List(platformContainer, "teams/")
                return names |> List.choose (idFromBlobName "teams/" ".json") |> Set.ofList
            }
        ListActiveTeamUserIds =
            fun () -> async {
                let! names = storage.List(platformContainer, "active-team/")
                return names |> List.choose (idFromBlobName "active-team/" ".txt")
            }
        GetActiveTeam =
            fun userId -> async {
                let! result = storage.Download(platformContainer, activeTeamBlobName userId)

                return
                    match result with
                    | Ok bytes ->
                        let teamId = Encoding.UTF8.GetString(bytes).Trim()
                        if teamId = "" then None else Some teamId
                    | Error _ -> None
            }
    }

    /// Writes over the same layout. `auditLog` records one
    /// `MemberRemoved` per stripped row, actor-attributed (GP 6);
    /// `notifications` publishes the same `MembershipChanged` envelopes
    /// `TeamStore` publishes on its own membership writes, so resolver
    /// caches evict structurally rather than by TTL (Phase 5d) — the
    /// doctor writes blobs directly and must not skip publication.
    let writes
        (storage: IBlobStorage)
        (auditLog: IAuditLog option)
        (notifications: INotificationChannel option)
        (actorUserId: string)
        : MembershipWrites =
        let publish (teamId: string) (affectedUserId: string) (kind: MembershipChangeKind) = async {
            match notifications with
            | Some channel ->
                let payload: MembershipChangedPayload = {
                    TeamId = teamId
                    AffectedUserId = affectedUserId
                    ChangeKind = kind
                    PublishedAt = System.DateTime.UtcNow
                }

                do! channel.Publish(NotificationKind.PlatformReservedScope, MembershipChanged payload)
            | None -> ()
        }

        {
            SaveMemberships =
                fun userId rows -> async {
                    let! _ =
                        storage.Upload(platformContainer, membershipBlobName userId, Json.serializeMemberships rows)

                    return ()
                }
            ClearActiveTeam =
                fun userId teamId -> async {
                    let! _ = storage.Delete(platformContainer, activeTeamBlobName userId)
                    do! publish teamId userId MembershipChangeKind.ActiveTeamSet
                }
            EmitMemberRemoved =
                fun teamId affectedUserId -> async {
                    match auditLog with
                    | Some log ->
                        do!
                            log.Record(
                                teamId,
                                MemberRemoved {
                                    UserId = actorUserId
                                    TeamId = teamId
                                    AffectedUserId = affectedUserId
                                }
                            )
                    | None -> ()

                    do! publish teamId affectedUserId MembershipChangeKind.Removed
                }
        }