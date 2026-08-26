// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AdminMutationApproval

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore

// ─── Dual control for sensitive admin mutations (Phase 555) ──────────
//
// The two-person rule from change management, applied to the
// `IPermissionStore` write path: a gated write does not apply, it is
// captured as a pending record naming who proposed it and exactly what
// it would do, and a SECOND, DISTINCT administrator approves or rejects.
// Only approval applies it.
//
// **What this catches that Phase 551 does not.** 551 lets a MODULE
// declare a precondition on being granted at all, which is a control
// over WHICH grants are admissible. Dual control is a control over HOW
// MANY PEOPLE agreed, and it binds on modules that declare nothing. The
// fat-finger grant to the wrong `alice` satisfies every module policy in
// the estate: the module got exactly the ceremony it asked for, and one
// person still handed authority to the wrong subject. That is the gap.
//
// **The composition order is fixed and it is load-bearing** (555.D).
// This decorator sits INSIDE the Phase 551 `GrantPolicyPermissionStore`,
// so a write reaches the module's declared grant policy FIRST:
//
//     GrantPolicyPermissionStore        (551 — outermost)
//       └── DualControlPermissionStore  (555 — this file)
//             └── SanitisingPermissionStore
//                   └── PermissionStore
//
// Reverse the two and a write the module would never admit is parked
// awaiting an approval that could not have applied it — a queue entry
// that is guaranteed to fail at the end of a ceremony two people
// performed. The phase says it directly: a write that can never be
// approved is refused, not parked. Refusal-before-capture is what that
// order buys.
//
// **Why revocation is never gated.** A pending record narrows nothing
// while it sits there — the authority it would create does not exist, so
// parking a grant is safe. Parking a REVOCATION is the opposite: it
// leaves authority standing that an administrator has decided must go,
// and it hands a compromised admin a way to keep their own access by
// proposing its removal and never approving it. So the gate is on
// WIDENING only, which is also the rule Phase 551 states for grant
// policy ("a policy constrains the creation of authority, never its
// removal"). Two controls, same asymmetry, same reason.
//
// **Why the proposer must be resolvable.** A two-person rule whose first
// person is "unknown" is not a two-person rule: with an anonymous
// proposer, distinctness is unprovable and any approver clears any
// proposal. So an unattributable gated write is REFUSED
// (`UnattributedProposer`), never parked. The legacy `IPermissionStore`
// signature carries no caller identity — and per GP 4 must not — so the
// decorator is handed a `resolveProposer` seam at composition, wired to
// the live request's `AccessContext` in production and passed explicitly
// in tests.
//
// **What distinctness means today.** Distinct USER ID, trimmed and
// case-insensitive, which is what the phase specifies until Phase 527
// (service accounts) and Phase 528 (session registry) give the estate a
// richer principal. A second SESSION of the same principal therefore
// cannot self-approve — sessions do not enter the comparison at all,
// which is the stronger property and the one 555.C asks for. What is NOT
// yet covered is one human holding two accounts; that is a
// principal-identity question, not a ceremony question, and it belongs
// with 527/528 rather than here.

// ─── Canonical form + fingerprint ────────────────────────────────────

let private jsonOptions = FableConverters.create ()

/// Deterministic textual rendering of a captured mutation.
///
/// Hand-built rather than taken from the JSON serialiser, because the
/// fingerprint is a security-relevant identity and STJ property ordering
/// is a serialiser implementation detail: an options change or a
/// converter upgrade must not silently re-fingerprint every queued
/// proposal. `Map.toList` is key-ordered and list order is preserved, so
/// this is stable across processes and machines.
let canonicalForm (mutation: AdminMutation) : string =
    let perms (ps: ModulePermission list) =
        ps |> List.map string |> List.sort |> String.concat ","

    let modules (m: Map<string, ModulePermission list>) =
        m
        |> Map.toList
        |> List.map (fun (name, ps) -> $"{name}=[{perms ps}]")
        |> String.concat ";"

    match mutation with
    | AdminMutation.SetTeamPermissions p ->
        let members =
            p.Members
            |> Map.toList
            |> List.map (fun (userId, byModule) -> $"{userId}{{{modules byModule}}}")
            |> String.concat ";"

        let exposure =
            p.Exposure
            |> Map.toList
            |> List.map (fun (name, state) -> $"{name}={ModuleExposure.toToken state}")
            |> String.concat ";"

        let grants =
            p.Grants
            |> Map.toList
            |> List.map (fun (userId, byModule) ->
                let inner =
                    byModule
                    |> Map.toList
                    |> List.map (fun (name, r) ->
                        let consented = r.ConsentedBy |> Option.defaultValue ""

                        $"{name}={GrantState.toToken r.State}/{GrantPolicy.toToken r.SatisfiedPolicy}/{r.Justification}/{consented}")
                    |> String.concat ","

                $"{userId}{{{inner}}}")
            |> String.concat ";"

        $"set-team-permissions|defaults:{modules p.Defaults}|members:{members}|exposure:{exposure}|grants:{grants}"
    | AdminMutation.SetMemberPermissions(userId, moduleName, permissions) ->
        $"set-member-permissions|user:{userId}|module:{moduleName}|perms:[{perms permissions}]"
    | AdminMutation.SetTeamDefaults defaults -> $"set-team-defaults|defaults:{modules defaults}"
    | AdminMutation.SetModuleExposure(moduleName, state) ->
        $"set-module-exposure|module:{moduleName}|state:{ModuleExposure.toToken state}"

/// SHA-256 over the canonical form, lowercase hex. Binds an approval to
/// the exact bytes proposed.
let fingerprint (mutation: AdminMutation) : string =
    use sha = SHA256.Create()

    canonicalForm mutation
    |> Encoding.UTF8.GetBytes
    |> sha.ComputeHash
    |> Array.map (fun b -> b.ToString "x2")
    |> String.concat ""

/// Two principals are the same administrator when their ids match after
/// trimming, case-insensitively. Deliberately permissive on the
/// SAMENESS side: a comparison that treated `"Alice"` and `"alice"` as
/// two people would let a self-approval through on a capitalisation, and
/// this predicate exists to prevent exactly that.
let isSameAdministrator (a: string) (b: string) =
    let norm (s: string) = if isNull (box s) then "" else s.Trim()

    String.Equals(norm a, norm b, StringComparison.OrdinalIgnoreCase)

// ─── Pending-approval store ──────────────────────────────────────────

/// Durable queue of pending admin mutations, partitioned by team.
///
/// Satisfies the six portability rules: identity by value (`requestId` /
/// `teamId` strings, never a handle), async at every boundary, no
/// callbacks, no state between calls, and no ordering promise across
/// teams — a team id is the shard key, and nothing in the ceremony reads
/// two teams' queues together.
type IAdminMutationApprovalStore =
    /// Persist a pending record. Overwrites a record with the same id,
    /// which cannot collide in practice (the id is a fresh GUID) and is
    /// the idempotent shape if it ever did.
    abstract Propose: pending: PendingAdminMutation -> Async<Result<unit, string>>

    /// Read one pending record. `Ok None` means no such record — which is
    /// also what an already-decided request looks like, because a decided
    /// record is removed. `Error` means the store could not be read, and
    /// callers must NOT treat that as absence.
    abstract TryGet: teamId: string * requestId: string -> Async<Result<PendingAdminMutation option, string>>

    /// Every pending record for a team, newest proposal first. Unreadable
    /// records are skipped rather than failing the whole listing — one
    /// corrupt blob must not hide the rest of the queue.
    abstract List: teamId: string -> Async<Result<PendingAdminMutation list, string>>

    /// Discard a record. Idempotent — removing an absent record is `Ok`.
    abstract Remove: teamId: string * requestId: string -> Async<Result<unit, string>>

[<Literal>]
let private PlatformContainer = "_platform"

let private pendingPrefix (teamId: string) = $"admin-mutations/{teamId}/"

let private pendingBlobName (teamId: string) (requestId: string) =
    $"{pendingPrefix teamId}{requestId}.json"

/// Blob-backed `IAdminMutationApprovalStore`. One JSON document per
/// pending record under `_platform/admin-mutations/{teamId}/{id}.json`.
///
/// **Distributed-ready.** No state between calls, no in-memory index, no
/// singleton cache: every read goes to storage. The queue is small by
/// construction (a pending record is a human-scale act with a TTL), so
/// the cost of not caching is a blob read per approval, and the benefit
/// is that two app instances cannot disagree about whether a proposal is
/// still open.
type BlobAdminMutationApprovalStore(storage: IBlobStorage, ?logger: ILogger) =
    let logError (msg: string) =
        match logger with
        | Some(l: ILogger) -> l.Error(msg, None)
        | None -> ()

    let deserialise (bytes: byte[]) =
        try
            Some(JsonSerializer.Deserialize<PendingAdminMutation>(Encoding.UTF8.GetString bytes, jsonOptions))
        with _ ->
            None

    interface IAdminMutationApprovalStore with
        member _.Propose pending = async {
            try
                let bytes = JsonSerializer.Serialize(pending, jsonOptions) |> Encoding.UTF8.GetBytes

                let! result = storage.Upload(PlatformContainer, pendingBlobName pending.TeamId pending.RequestId, bytes)

                return
                    match result with
                    | Ok _ -> Ok()
                    | Error e -> Error e
            with ex ->
                logError
                    $"AdminMutationApproval: could not persist pending mutation '{pending.RequestId}': {ex.Message}"

                return Error ex.Message
        }

        member _.TryGet(teamId, requestId) = async {
            let blobName = pendingBlobName teamId requestId
            let! exists = storage.Exists(PlatformContainer, blobName)

            if not exists then
                return Ok None
            else
                let! result = storage.Download(PlatformContainer, blobName)

                match result with
                | Error e ->
                    // Present but unreadable. Reporting this as absence
                    // would let a storage blip turn "awaiting approval"
                    // into "no such proposal", which the caller renders as
                    // an ordinary refusal — the failure mode dual control
                    // exists to remove.
                    logError
                        $"AdminMutationApproval: pending mutation '{requestId}' for team '{teamId}' exists but could not be read ({e})."

                    return Error e
                | Ok bytes ->
                    match deserialise bytes with
                    | Some pending -> return Ok(Some pending)
                    | None ->
                        logError
                            $"AdminMutationApproval: pending mutation '{requestId}' for team '{teamId}' is present but unparseable."

                        return Error "pending record is present but unparseable"
        }

        member _.List teamId = async {
            try
                let! names = storage.List(PlatformContainer, pendingPrefix teamId)

                let! records =
                    names
                    |> List.map (fun name -> async {
                        let! result = storage.Download(PlatformContainer, name)

                        return
                            match result with
                            | Ok bytes -> deserialise bytes
                            | Error _ -> None
                    })
                    |> Async.Sequential

                return
                    records
                    |> Array.toList
                    |> List.choose id
                    |> List.sortByDescending _.ProposedAtUtc
                    |> Ok
            with ex ->
                return Error ex.Message
        }

        member _.Remove(teamId, requestId) = async {
            let! result = storage.Delete(PlatformContainer, pendingBlobName teamId requestId)
            return result
        }

// ─── Gating decision (555.A / 555.B) ─────────────────────────────────

/// Exposure states ordered by how much they expose. A write that raises
/// this rank widens what the team may reach and is therefore gated;
/// lowering it narrows and is not.
let private exposureRank =
    function
    | ModuleExposure.Unavailable -> 0
    | ModuleExposure.Hidden -> 1
    | ModuleExposure.Available -> 2

/// Every module a write ADDS or WIDENS authority on, given the document
/// it is replacing. Removals and unchanged rows are absent by
/// construction: a revoked entry is not present in the new document, and
/// an untouched one compares equal.
///
/// This is what makes a repair of an existing document cheap — an
/// operator rewriting a permission blob without changing any grant
/// proposes nothing and the write applies straight through.
let widenedModules (previous: TeamPermissions) (mutation: AdminMutation) : string list =
    match mutation with
    | AdminMutation.SetTeamPermissions p ->
        let memberWidened = [
            for KeyValue(userId, byModule) in p.Members do
                let prior = previous.Members |> Map.tryFind userId |> Option.defaultValue Map.empty

                for KeyValue(moduleName, perms) in byModule do
                    if not (List.isEmpty perms) && Map.tryFind moduleName prior <> Some perms then
                        moduleName
        ]

        let defaultsWidened = [
            for KeyValue(moduleName, perms) in p.Defaults do
                if
                    not (List.isEmpty perms)
                    && Map.tryFind moduleName previous.Defaults <> Some perms
                then
                    moduleName
        ]

        let exposureWidened = [
            for KeyValue(moduleName, state) in p.Exposure do
                let prior =
                    previous.Exposure
                    |> Map.tryFind moduleName
                    |> Option.defaultValue ModuleExposure.Available

                if exposureRank state > exposureRank prior then
                    moduleName
        ]

        // A whole-document write can also widen exposure by DROPPING a
        // restriction: absence reads as `Available`, so a module the
        // previous document held as Hidden and the new one omits has been
        // re-exposed.
        let exposureCleared = [
            for KeyValue(moduleName, prior) in previous.Exposure do
                if
                    not (p.Exposure.ContainsKey moduleName)
                    && exposureRank ModuleExposure.Available > exposureRank prior
                then
                    moduleName
        ]

        memberWidened @ defaultsWidened @ exposureWidened @ exposureCleared
        |> List.distinct
    | AdminMutation.SetMemberPermissions(userId, moduleName, permissions) ->
        let prior = previous.Members |> Map.tryFind userId |> Option.defaultValue Map.empty

        if List.isEmpty permissions || Map.tryFind moduleName prior = Some permissions then
            []
        else
            [ moduleName ]
    | AdminMutation.SetTeamDefaults defaults -> [
        for KeyValue(moduleName, perms) in defaults do
            if
                not (List.isEmpty perms)
                && Map.tryFind moduleName previous.Defaults <> Some perms
            then
                moduleName
      ]
    | AdminMutation.SetModuleExposure(moduleName, state) ->
        let prior =
            previous.Exposure
            |> Map.tryFind moduleName
            |> Option.defaultValue ModuleExposure.Available

        if exposureRank state > exposureRank prior then
            [ moduleName ]
        else
            []

/// Is this write gated under `scope`? `isPolicyBearing` answers "does
/// this module declare a Phase 551 `GrantPolicy` stricter than
/// `AdminDiscretion`" — passed as a predicate rather than as the
/// registry itself so this file takes no dependency on
/// `GrantPolicyGuard`, and so a test can drive both scope arms without
/// composing a module set.
let isGated
    (scope: AdminMutationScope)
    (isPolicyBearing: string -> bool)
    (previous: TeamPermissions)
    (mutation: AdminMutation)
    : bool =
    match widenedModules previous mutation with
    | [] -> false
    | widened ->
        match scope with
        | AdminMutationScope.AllPermissionWrites -> true
        | AdminMutationScope.PolicyBearingModulesOnly -> widened |> List.exists isPolicyBearing

// ─── The ceremony (555.B / 555.C / 555.D) ────────────────────────────

/// Best-effort audit emission. Swallowed on failure, deliberately and
/// for the Phase 551 reason: the control is the refusal, not the row, and
/// a downed audit pipeline must never turn a denial into an admission.
/// `schedule` is `Async.Start` in production and a synchronous runner in
/// tests, so a test observes decision and row from one call and the two
/// cannot drift apart with only one of them covered.
let private emit (auditLog: IAuditLog option) (schedule: Async<unit> -> unit) (teamId: string) (event: AuditEvent) =
    match auditLog with
    | None -> ()
    | Some log ->
        schedule (
            async {
                try
                    do! log.Record($"team-{teamId}", event)
                with _ ->
                    ()
            }
        )

/// Capture a gated mutation as a pending record. **Nothing is applied.**
///
/// Refuses an unattributable proposer rather than parking the write —
/// see the file header.
let propose
    (store: IAdminMutationApprovalStore)
    (settings: DualControlSettings)
    (auditLog: IAuditLog option)
    (schedule: Async<unit> -> unit)
    (now: DateTimeOffset)
    (teamId: string)
    (proposerId: string)
    (mutation: AdminMutation)
    : Async<Result<AdminMutationQueued, AdminMutationRefusal>> =
    async {
        if String.IsNullOrWhiteSpace proposerId then
            return Error AdminMutationRefusal.UnattributedProposer
        else
            let print = fingerprint mutation

            let pending = {
                RequestId = Guid.NewGuid().ToString "N"
                TeamId = teamId
                Mutation = mutation
                Fingerprint = print
                Summary = AdminMutation.summary mutation
                ProposedBy = proposerId.Trim()
                ProposedAtUtc = now
                ExpiresAtUtc = now.AddMinutes(float (DualControlSettings.ttlMinutes settings))
            }

            match! store.Propose pending with
            | Error e -> return Error(AdminMutationRefusal.ApprovalStoreUnavailable e)
            | Ok() ->
                emit
                    auditLog
                    schedule
                    teamId
                    (AdminMutationProposed {
                        RequestId = pending.RequestId
                        TeamId = teamId
                        ProposerId = pending.ProposedBy
                        MutationKind = AdminMutationKind.toToken (AdminMutation.kind mutation)
                        Fingerprint = print
                        Summary = pending.Summary
                        ExpiresAtUtc = pending.ExpiresAtUtc
                    })

                return
                    Ok {
                        RequestId = pending.RequestId
                        Fingerprint = print
                        ExpiresAtUtc = pending.ExpiresAtUtc
                    }
    }

/// Fetch a pending record and check every precondition a DECISION needs,
/// emitting the refusal row on failure. Shared by `approve` and `reject`
/// so the two cannot disagree about what a decidable record is.
let private loadDecidable
    (store: IAdminMutationApprovalStore)
    (auditLog: IAuditLog option)
    (schedule: Async<unit> -> unit)
    (now: DateTimeOffset)
    (teamId: string)
    (requestId: string)
    (actorId: string)
    (requireDistinctActor: bool)
    : Async<Result<PendingAdminMutation, AdminMutationRefusal>> =
    async {
        let refuse (pending: PendingAdminMutation option) (refusal: AdminMutationRefusal) =
            emit
                auditLog
                schedule
                teamId
                (AdminMutationApprovalRefused {
                    RequestId = requestId
                    TeamId = teamId
                    ProposerId = pending |> Option.map _.ProposedBy |> Option.defaultValue ""
                    AttemptedApproverId = actorId
                    MutationKind =
                        pending
                        |> Option.map (fun p -> AdminMutationKind.toToken (AdminMutation.kind p.Mutation))
                        |> Option.defaultValue ""
                    RefusalCode = AdminMutationRefusal.code refusal
                })

            Error refusal

        match! store.TryGet(teamId, requestId) with
        | Error e -> return refuse None (AdminMutationRefusal.ApprovalStoreUnavailable e)
        | Ok None -> return refuse None (AdminMutationRefusal.UnknownRequest requestId)
        | Ok(Some pending) ->
            // Order matters: self-approval is checked BEFORE expiry, so a
            // proposer attempting to approve their own lapsed proposal is
            // recorded as the self-approval attempt it is rather than
            // disappearing into an expiry row. The security signal wins
            // the tie.
            if requireDistinctActor && isSameAdministrator pending.ProposedBy actorId then
                return refuse (Some pending) (AdminMutationRefusal.SelfApprovalRefused(requestId, actorId))
            elif PendingAdminMutation.isExpired now pending then
                // Discard as we go: lazy expiry means the record is
                // removed at the moment someone touches it, so a
                // `DualControl` deployment needs no sweeper hosted
                // service (GP 13).
                let! _ = store.Remove(teamId, requestId)

                emit
                    auditLog
                    schedule
                    teamId
                    (AdminMutationExpired {
                        RequestId = requestId
                        TeamId = teamId
                        ProposerId = pending.ProposedBy
                        MutationKind = AdminMutationKind.toToken (AdminMutation.kind pending.Mutation)
                        Fingerprint = pending.Fingerprint
                        ExpiredAtUtc = pending.ExpiresAtUtc
                    })

                return refuse (Some pending) (AdminMutationRefusal.Expired(requestId, pending.ExpiresAtUtc))
            elif fingerprint pending.Mutation <> pending.Fingerprint then
                return refuse (Some pending) (AdminMutationRefusal.FingerprintMismatch requestId)
            else
                return Ok pending
    }

/// A second, distinct administrator approves a pending mutation, and the
/// captured mutation is applied via `apply`.
///
/// `apply` is the seam onto the underlying store — the decorator hands it
/// the store it wraps, so an approved mutation lands through exactly the
/// path the proposal would have taken and never re-enters the gate.
///
/// The record is removed BEFORE `apply` runs. That ordering is
/// deliberate: it makes a concurrent double-approval unable to apply the
/// mutation twice, at the cost of leaving nothing to retry if `apply`
/// then fails. Retrying an admin mutation nobody re-approved is the worse
/// of the two failures, and the `Applied = false` audit row makes the
/// case visible.
let approve
    (store: IAdminMutationApprovalStore)
    (apply: AdminMutation -> Async<Result<unit, string>>)
    (auditLog: IAuditLog option)
    (schedule: Async<unit> -> unit)
    (now: DateTimeOffset)
    (teamId: string)
    (requestId: string)
    (approverId: string)
    : Async<Result<AdminMutationDecision, AdminMutationRefusal>> =
    async {
        if String.IsNullOrWhiteSpace approverId then
            return Error AdminMutationRefusal.UnattributedProposer
        else
            match! loadDecidable store auditLog schedule now teamId requestId approverId true with
            | Error refusal -> return Error refusal
            | Ok pending ->
                let! _ = store.Remove(teamId, requestId)
                let! applied = apply pending.Mutation

                emit
                    auditLog
                    schedule
                    teamId
                    (AdminMutationApproved {
                        RequestId = requestId
                        TeamId = teamId
                        ProposerId = pending.ProposedBy
                        ApproverId = approverId.Trim()
                        MutationKind = AdminMutationKind.toToken (AdminMutation.kind pending.Mutation)
                        Fingerprint = pending.Fingerprint
                        Applied = Result.isOk applied
                    })

                match applied with
                | Ok() -> return Ok(AdminMutationDecision.Applied requestId)
                | Error e -> return Error(AdminMutationRefusal.ApprovalStoreUnavailable e)
    }

/// Turn a pending mutation down. Nothing is applied and the record is
/// discarded.
///
/// The proposer MAY withdraw their own proposal — `requireDistinctActor`
/// is false here — because withdrawing destroys authority rather than
/// creating it, which is the same asymmetry that leaves revocation
/// ungated. Forcing a second person to agree that a mistake was a
/// mistake buys nothing and leaves the queue holding a grant the
/// proposer has already disowned.
let reject
    (store: IAdminMutationApprovalStore)
    (auditLog: IAuditLog option)
    (schedule: Async<unit> -> unit)
    (now: DateTimeOffset)
    (teamId: string)
    (requestId: string)
    (actorId: string)
    (reason: string)
    : Async<Result<AdminMutationDecision, AdminMutationRefusal>> =
    async {
        match! loadDecidable store auditLog schedule now teamId requestId actorId false with
        | Error refusal -> return Error refusal
        | Ok pending ->
            let! _ = store.Remove(teamId, requestId)

            emit
                auditLog
                schedule
                teamId
                (AdminMutationRejected {
                    RequestId = requestId
                    TeamId = teamId
                    ProposerId = pending.ProposedBy
                    ApproverId = (if isNull (box actorId) then "" else actorId.Trim())
                    MutationKind = AdminMutationKind.toToken (AdminMutation.kind pending.Mutation)
                    Fingerprint = pending.Fingerprint
                    Reason = (if isNull (box reason) then "" else reason)
                })

            return Ok(AdminMutationDecision.Rejected requestId)
    }

/// The still-open proposals for a team, with lapsed records discarded
/// (and audited) as they are encountered. The read an approval queue is
/// built from, and the only sweep the design has — see the lazy-expiry
/// note in `loadDecidable`.
let listPending
    (store: IAdminMutationApprovalStore)
    (auditLog: IAuditLog option)
    (schedule: Async<unit> -> unit)
    (now: DateTimeOffset)
    (teamId: string)
    : Async<Result<PendingAdminMutation list, string>> =
    async {
        match! store.List teamId with
        | Error e -> return Error e
        | Ok all ->
            let expired, live = all |> List.partition (PendingAdminMutation.isExpired now)

            for pending in expired do
                let! _ = store.Remove(teamId, pending.RequestId)

                emit
                    auditLog
                    schedule
                    teamId
                    (AdminMutationExpired {
                        RequestId = pending.RequestId
                        TeamId = teamId
                        ProposerId = pending.ProposedBy
                        MutationKind = AdminMutationKind.toToken (AdminMutation.kind pending.Mutation)
                        Fingerprint = pending.Fingerprint
                        ExpiredAtUtc = pending.ExpiresAtUtc
                    })

            return Ok live
    }

// ─── The `IPermissionStore` decorator ────────────────────────────────

/// `IPermissionStore` decorator implementing the two-person rule.
/// Composed ONLY when `ServerConfig.AdminMutationPolicy` is
/// `DualControl`, so a `SingleAdmin` deployment resolves the undecorated
/// store and is byte-for-byte unchanged (GP 11 / GP 13).
///
/// Reads pass straight through. A write is evaluated against `isGated`;
/// an ungated write (a revocation, a no-op repair, or — under
/// `PolicyBearingModulesOnly` — a module declaring no Phase 551 policy)
/// is applied immediately and unchanged. A gated write applies NOTHING
/// and returns a `DUAL-CONTROL-PENDING-APPROVAL` error naming the request
/// id, because `Result<unit, string>` is the only channel the legacy
/// interface has. A caller wanting the typed outcome uses
/// `AdminMutationGate.write`, which returns
/// `AdminMutationWriteOutcome` — the same split Phase 551 uses between
/// its decorator and `PermissionGrants.grantModuleAccess`.
type DualControlPermissionStore
    (
        inner: IPermissionStore,
        settings: DualControlSettings,
        approvals: IAdminMutationApprovalStore,
        isPolicyBearing: string -> bool,
        resolveProposer: unit -> string option,
        now: unit -> DateTimeOffset,
        ?auditLog: IAuditLog
    ) =

    // Production scheduling for the best-effort audit emissions. A test
    // constructs the ceremony functions directly with a synchronous
    // scheduler; the decorator is the production path and never blocks a
    // refusal on a log write.
    let schedule (work: Async<unit>) = Async.Start work

    /// Apply a captured mutation to the WRAPPED store. The approval path
    /// re-enters here rather than through this decorator, so an approved
    /// mutation cannot be gated into a second ceremony.
    let applyToInner (teamId: string) (mutation: AdminMutation) : Async<Result<unit, string>> =
        match mutation with
        | AdminMutation.SetTeamPermissions p -> inner.SetTeamPermissions(teamId, p)
        | AdminMutation.SetMemberPermissions(userId, moduleName, permissions) ->
            inner.SetMemberPermissions(teamId, userId, moduleName, permissions)
        | AdminMutation.SetTeamDefaults defaults -> inner.SetTeamDefaults(teamId, defaults)
        | AdminMutation.SetModuleExposure(moduleName, state) -> inner.SetModuleExposure(teamId, moduleName, state)

    /// The queued-not-applied message the legacy `Result<unit, string>`
    /// channel carries. Prefixed with a stable greppable code and naming
    /// the request id, because a caller that cannot see the typed outcome
    /// still has to tell an operator what to approve.
    let queuedMessage (queued: AdminMutationQueued) =
        $"DUAL-CONTROL-PENDING-APPROVAL: the write did not apply. It is queued as request '{queued.RequestId}' and requires approval by a second, distinct administrator before {queued.ExpiresAtUtc:o}."

    /// The whole write path: gate, then either apply verbatim or park.
    ///
    /// An UNGATED write returns the inner store's result **verbatim**,
    /// error string included. That is not laziness — a deployment that
    /// enables dual control and then sees different error text on writes
    /// the gate does not touch has had its behaviour changed where the
    /// phase promised it would not be.
    let gate (teamId: string) (mutation: AdminMutation) : Async<Result<unit, string>> = async {
        let! previous = inner.GetTeamPermissions teamId

        if not (isGated settings.Scope isPolicyBearing previous mutation) then
            return! applyToInner teamId mutation
        else
            match resolveProposer () with
            | None
            | Some "" -> return Error(AdminMutationRefusal.describe AdminMutationRefusal.UnattributedProposer)
            | Some proposerId ->
                match! propose approvals settings auditLog schedule (now ()) teamId proposerId mutation with
                | Error refusal -> return Error(AdminMutationRefusal.describe refusal)
                | Ok queued -> return Error(queuedMessage queued)
    }

    /// The store this decorator wraps. Exposed so the composition root
    /// and an admin surface can apply an APPROVED mutation without going
    /// back through the gate.
    member _.Inner = inner

    member _.Settings = settings
    member _.Approvals = approvals

    // ─── Typed entry points (555.C — the API-first approval surface) ──

    /// Evaluate a write and report what happened as a VALUE rather than
    /// as an error string: `AppliedImmediately` when the write was not
    /// gated, `QueuedForApproval` when it was parked. The entry point a
    /// policy-aware admin surface calls, so it can render "queued for
    /// approval, request X" instead of parsing prose.
    member _.Write(teamId: string, proposerId: string, mutation: AdminMutation) = async {
        let! previous = inner.GetTeamPermissions teamId

        if not (isGated settings.Scope isPolicyBearing previous mutation) then
            match! applyToInner teamId mutation with
            | Ok() -> return Ok AdminMutationWriteOutcome.AppliedImmediately
            | Error e -> return Error(AdminMutationRefusal.ApprovalStoreUnavailable e)
        else
            match! propose approvals settings auditLog schedule (now ()) teamId proposerId mutation with
            | Error refusal -> return Error refusal
            | Ok queued -> return Ok(AdminMutationWriteOutcome.QueuedForApproval queued)
    }

    /// A second, distinct administrator approves a pending mutation. Self
    /// approval is refused structurally — `approverId` equal to the
    /// recorded proposer never reaches the apply path.
    member _.Approve(teamId: string, requestId: string, approverId: string) =
        approve approvals (applyToInner teamId) auditLog schedule (now ()) teamId requestId approverId

    /// Turn a pending mutation down. Nothing is applied.
    member _.Reject(teamId: string, requestId: string, actorId: string, reason: string) =
        reject approvals auditLog schedule (now ()) teamId requestId actorId reason

    /// The still-open proposals for a team, lapsed records swept as they
    /// are read. The approval queue an admin surface renders.
    member _.ListPending(teamId: string) =
        listPending approvals auditLog schedule (now ()) teamId

    interface IPermissionStore with
        // Reads are untouched: dual control governs the creation of
        // authority, and a read creates none.
        member _.GetTeamPermissions teamId = inner.GetTeamPermissions teamId

        member _.GetEffectivePermissions(userId, teamId) =
            inner.GetEffectivePermissions(userId, teamId)

        member _.GetModuleExposure teamId = inner.GetModuleExposure teamId

        member _.SetTeamPermissions(teamId, permissions) =
            gate teamId (AdminMutation.SetTeamPermissions permissions)

        member _.SetMemberPermissions(teamId, userId, moduleName, permissions) =
            gate teamId (AdminMutation.SetMemberPermissions(userId, moduleName, permissions))

        member _.SetTeamDefaults(teamId, defaults) =
            gate teamId (AdminMutation.SetTeamDefaults defaults)

        member _.SetModuleExposure(teamId, moduleName, state) =
            gate teamId (AdminMutation.SetModuleExposure(moduleName, state))