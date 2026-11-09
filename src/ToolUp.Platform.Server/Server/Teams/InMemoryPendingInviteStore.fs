// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Scope.Teams

open System
open System.Runtime.CompilerServices
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

// ─── Email-keyed pre-invite document ─────────────────────────────────
//
// Persists a flat map of `email -> PendingInviteByEmail` as one document,
// `_platform/pending-invites.json` (path constant declared in
// `TeamInviteTypes.PendingInvitesBlobPath`), through the document store.
// Two access patterns:
//
//   * Admin-write path. `IssuePendingInviteByEmail` adds or replaces the
//     entry for the supplied email in one guarded update.
//     Owner/Admin only; gated in the API handler.
//
//   * Middleware-read path. `ScopeResolutionMiddleware` calls
//     `TryConsumeForEmail` on every authenticated sign-in resolve. It
//     returns the pending entry (if any) AND removes it in the same
//     update — the caller is expected to follow up with
//     `ITeamStore.AddMember`.
//
// **One document, one policy.** Both SDK stores are this file's codec and
// plans over `BlobDocumentStore`: absence is established before the read,
// a read failure is a refusal (`StorageFailed`), never an empty map, and
// undecodable bytes are QUARANTINED — copied aside, the canonical document
// left untouched, every operation refused until an operator repairs or
// removes it. The heal-to-empty the stores used to perform (Phase 5h /
// Phase 116: rename the corrupt blob aside, then write `{}` over it, so the
// store "self-healed" by forgetting every pending invite) is RETIRED: an
// empty map written over bytes nobody could read is a silent loss of every
// invite in them, and quarantine already keeps the deployment safe. The
// stores differ only in what the backend gives them: over a backend with
// conditional writes (`BlobPendingInviteStore`) replicas resolve by
// precondition; over one without (`InMemoryPendingInviteStore`) writers in
// one process take turns and that is the whole guarantee. Same document,
// same bytes, so a deployment that grows from one replica to several keeps
// its pending entries, in both directions.
//
// Nothing is cached between calls: a read serves the document as stored,
// so a peer's consume is never served twice.

/// The module-function surface kept from the pre-Phase-5h shape, over the
/// same document as the two stores. New code resolves `IPendingInviteStore`
/// from DI; these functions raise `DocumentUnavailableException` where the
/// interface returns `StorageFailed`.
module PendingInviteStore =

    let private jsonOptions = PlatformJson.options

    /// Container + blob name of the pending-invites map, shared by both
    /// stores so they are migration-compatible in both directions.
    let internal platformContainer = ScopeKey.PlatformContainer
    let internal blobName = "pending-invites.json"

    let internal encodeMap (map: Map<string, PendingInviteByEmail>) : byte[] =
        JsonSerializer.Serialize(map, jsonOptions) |> Encoding.UTF8.GetBytes

    /// Decode the persisted document. An empty one is the legitimate
    /// zero-state (`Ok Map.empty`); a present-but-unparseable one is
    /// `Error reason`, which the document store quarantines (GP 9).
    let internal decodeMap (bytes: byte[]) : Result<Map<string, PendingInviteByEmail>, string> =
        if isNull bytes || bytes.Length = 0 then
            Ok Map.empty
        else
            let json = Encoding.UTF8.GetString bytes

            try
                // `Option.ofObj` requires `'T : null`, which F# Map does not
                // satisfy. Box then null-check: a deserialiser can hand back
                // null on malformed input.
                match
                    JsonSerializer.Deserialize<Map<string, PendingInviteByEmail>>(json, jsonOptions)
                    |> box
                with
                | null -> Error "deserialiser returned null for a non-empty pending-invites blob"
                | o -> Ok(o :?> Map<string, PendingInviteByEmail>)
            with ex ->
                Error(sprintf "pending-invites blob failed to deserialise: %s" ex.Message)

    let internal codec: BlobCodec<Map<string, PendingInviteByEmail>> = {
        Encode = encodeMap
        Decode = decodeMap
    }

    let private path = DocPath.ofString blobName

    /// Split the map into the entries to keep (`ExpiresAt` at or after
    /// `now`) and the expired entries to drop (`ExpiresAt` strictly before
    /// `now`). Pure over the input — callers decide whether to persist the
    /// kept map and whether to emit a `TeamInviteExpired` audit row per
    /// dropped entry (Phase 547).
    let internal partitionExpired
        (now: DateTime)
        (map: Map<string, PendingInviteByEmail>)
        : Map<string, PendingInviteByEmail> * (string * PendingInviteByEmail) list =
        let kept = map |> Map.filter (fun _ entry -> entry.ExpiresAt >= now)

        let expired =
            map |> Map.toList |> List.filter (fun (_, entry) -> entry.ExpiresAt < now)

        kept, expired

    /// What one operation decided about the map it was handed: the map to
    /// persist (`None` = nothing to write), the operation's result, and the
    /// expired entries the write drops.
    type internal Plan<'r> =
        Map<string, PendingInviteByEmail>
            -> Map<string, PendingInviteByEmail> option * 'r * (string * PendingInviteByEmail) list

    /// Add or replace the entry for `email`, compacting expired entries in
    /// the same write (storage growth stays bounded without a scheduled
    /// sweep; a re-issue to an email whose prior entry lapsed drops that
    /// lapse alongside the fresh issue).
    let internal upsertPlan (email: string) (pending: PendingInviteByEmail) : Plan<unit> =
        fun current ->
            let compacted, expired = partitionExpired DateTime.UtcNow current
            Some(compacted |> Map.add (email.ToLowerInvariant()) pending), (), expired

    /// Remove the entry for `email`; `false` (nothing written) when absent.
    let internal removePlan (email: string) : Plan<bool> =
        fun current ->
            let key = email.ToLowerInvariant()

            if Map.containsKey key current then
                Some(Map.remove key current), true, []
            else
                None, false, []

    /// Read-and-remove the entry for `email` if present and live. A lapsed
    /// entry is dropped AND surfaced as expired: the invitee lands in
    /// neither Members nor Pending Invites, so without the row nobody is
    /// told.
    let internal consumePlan (email: string) : Plan<PendingInviteByEmail option> =
        fun current ->
            let key = email.ToLowerInvariant()

            match Map.tryFind key current with
            | None -> None, None, []
            | Some entry when entry.ExpiresAt < DateTime.UtcNow -> Some(Map.remove key current), None, [ key, entry ]
            | Some entry -> Some(Map.remove key current), Some entry, []

    /// Drop every expired entry; nothing is written when none has lapsed.
    let internal sweepPlan: Plan<int> =
        fun current ->
            let compacted, expired = partitionExpired DateTime.UtcNow current

            if List.isEmpty expired then
                None, 0, []
            else
                Some compacted, List.length expired, expired

    /// The pending-invites document over one backend: every operation is
    /// one guarded update of it, and `onExpired` runs only once the write
    /// that dropped the entries has landed (outside any retry, so an audit
    /// row is emitted at most once per durable drop).
    type internal Document
        (
            docs: BlobDocumentStore<Map<string, PendingInviteByEmail>>,
            onExpired: (string * PendingInviteByEmail) list -> Async<unit>
        ) =

        member _.Apply(plan: Plan<'r>) : Async<Result<'r, BlobMapStoreError>> = async {
            let! outcome =
                docs.Update(
                    ScopeKey.platform,
                    path,
                    fun current ->
                        match plan (defaultArg current Map.empty) with
                        | None, result, _ -> BlobUpdate.Keep(result, [])
                        | Some updated, result, expired -> BlobUpdate.Write(updated, (result, expired))
                )

            match outcome with
            | Ok(result, expired) ->
                do! onExpired expired
                return Ok result
            | Error e -> return Error e
        }

        member _.ReadAll() : Async<Result<(string * PendingInviteByEmail) list, BlobMapStoreError>> = async {
            let! read = docs.Read(ScopeKey.platform, path)
            return read |> Result.map (fun map -> defaultArg map Map.empty |> Map.toList)
        }

    /// The interface's error for a document-store verdict: a precondition
    /// lost on every attempt is `Conflict` (the caller decides), every
    /// other verdict is `StorageFailed` naming the document.
    let internal toStoreError (op: string) (error: BlobMapStoreError) : PendingInviteStoreError =
        match error with
        | BlobMapStoreError.Contended _ -> PendingInviteStoreError.Conflict
        | other -> PendingInviteStoreError.StorageFailed(sprintf "%s: %s" op (BlobMapStoreError.describe other))

    let private noExpiryHook: (string * PendingInviteByEmail) list -> Async<unit> =
        fun _ -> async { return () }

    // One document store per backend, so the module functions' writers in
    // this process take turns on the same stripes. Keyed by the backend
    // (weakly), never a lock or a read cache.
    let private documents = ConditionalWeakTable<IBlobStorage, Document>()

    let private documentFor (storage: IBlobStorage) =
        documents.GetValue(
            storage,
            fun storage ->
                Document(
                    BlobDocumentStore<Map<string, PendingInviteByEmail>>(
                        storage,
                        codec,
                        ToolUp.Platform.Config.ConsoleLogger.ConsoleLogger() :> ILogger
                    ),
                    noExpiryHook
                )
        )

    let private run (storage: IBlobStorage) (plan: Plan<'r>) : Async<'r> = async {
        let! outcome = (documentFor storage).Apply plan
        return outcome |> DocumentUnavailable.orRaise
    }

    /// Drop every expired entry and return how many were dropped. Useful as
    /// a scheduled background sweep (via `IJobScheduler`).
    let sweepExpired (storage: IBlobStorage) : Async<int> = run storage sweepPlan

    /// Add or replace the pending entry for `email`. The caller is
    /// responsible for the Owner/Admin gate on `pending.TeamId`.
    let upsert (storage: IBlobStorage) (email: string) (pending: PendingInviteByEmail) : Async<unit> =
        run storage (upsertPlan email pending)

    /// Remove the pending entry for `email` if present. Idempotent.
    let remove (storage: IBlobStorage) (email: string) : Async<unit> = async {
        let! _ = run storage (removePlan email)
        return ()
    }

    /// Every pending entry as `(email, entry)` pairs; the caller filters by
    /// team where appropriate.
    let listAll (storage: IBlobStorage) : Async<(string * PendingInviteByEmail) list> = async {
        let! read = (documentFor storage).ReadAll()
        return read |> DocumentUnavailable.orRaise
    }

    /// Read-and-remove the entry for `email` if present and not expired.
    let tryConsumeForEmail (storage: IBlobStorage) (email: string) : Async<PendingInviteByEmail option> =
        run storage (consumePlan email)

/// Phase 5h — the expiry side-effect hook shared by every SDK-shipped
/// `IPendingInviteStore` implementation (`InMemoryPendingInviteStore`,
/// `BlobPendingInviteStore`). Builds the `onExpired` function a store
/// runs AFTER a sweep / upsert / consume has durably dropped expired
/// entries: one `TeamInviteExpired` audit row per entry (Phase 547) and
/// then, when composed, the inviter notification (Phase 547.C). Lives
/// here rather than in either store because the posture — best-effort,
/// a throwing sink degrades to a `Warn`, never fails the store operation
/// — is a property of the seam, not of one backend.
module internal PendingInviteExpiryHook =

    /// Per-expiry audit hook. Emits one `TeamInviteExpired` under the
    /// entry's team audit stream (`AuditStreamKey.ofTeam`) for every entry a sweep drops.
    /// Best-effort: a throwing `IAuditLog.Record` (misconfigured sink,
    /// unreachable `IEventStore`) must never fail the sweep — the entry
    /// is already durably gone — so each emission is guarded and a
    /// failure degrades to a `Warn`. No audit log composed → no-op (GP 11
    /// / GP 13).
    let private auditExpired
        (logger: ILogger)
        (auditLog: IAuditLog option)
        : (string * PendingInviteByEmail) list -> Async<unit> =
        match auditLog with
        | None -> fun _ -> async { return () }
        | Some log ->
            fun expired -> async {
                for email, entry in expired do
                    try
                        do!
                            log.Record(
                                AuditScope.team (TeamId entry.TeamId),
                                AuditSubject.system "PendingInviteStore",
                                TeamInviteExpired {
                                    TeamId = entry.TeamId
                                    InviteeEmail = email
                                    InviterUserId = entry.InviterUserId
                                    Role = entry.Role
                                    IssuedAt = entry.IssuedAt
                                    ExpiredAt = entry.ExpiresAt
                                }
                            )
                            |> AuditRow.orRefuse
                    with ex ->
                        logger.Warn(
                            sprintf
                                "[PendingInviteStore] TeamInviteExpired audit emission failed for team %s: %s"
                                entry.TeamId
                                ex.Message
                        )
            }

    /// The full hook: audit emission first, then — only when a notifier
    /// was composed — the Phase 547.C inviter notification, run AFTER
    /// audit emission so the durable trail row exists whatever the
    /// notification transport does. Same best-effort posture: a throwing
    /// notifier degrades to a `Warn`, never fails the sweep. `None`
    /// notifier → the hook is byte-for-byte the pre-547.C audit-only
    /// shape (GP 11 / GP 13).
    let build
        (logger: ILogger)
        (auditLog: IAuditLog option)
        (expiryNotifier: ((string * PendingInviteByEmail) list -> Async<unit>) option)
        : (string * PendingInviteByEmail) list -> Async<unit> =
        let auditExpired = auditExpired logger auditLog

        match expiryNotifier with
        | None -> auditExpired
        | Some notify ->
            fun expired -> async {
                do! auditExpired expired

                if not (List.isEmpty expired) then
                    try
                        do! notify expired
                    with ex ->
                        logger.Warn(sprintf "[PendingInviteStore] invite-expiry notification failed: %s" ex.Message)
            }

/// The pending-invites document over a backend WITHOUT conditional writes:
/// writers in this process take turns on the document, which is the whole
/// guarantee, so it serves a single-instance deployment
/// (`BlobPendingInviteStore` is the multi-instance form over the same
/// document). Nothing is cached between calls. Every failure surfaces as a
/// `PendingInviteStoreError`; an undecodable document is quarantined by the
/// document store, logged there at `Error`, and every operation is
/// `StorageFailed` until it is repaired or removed. `Remove` returns
/// `Error NotFound` when no entry was present.
type InMemoryPendingInviteStore
    (
        storage: IBlobStorage,
        logger: ILogger,
        auditLog: IAuditLog option,
        expiryNotifier: ((string * PendingInviteByEmail) list -> Async<unit>) option
    ) =

    let document =
        PendingInviteStore.Document(
            BlobDocumentStore<Map<string, PendingInviteByEmail>>(storage, PendingInviteStore.codec, logger),
            PendingInviteExpiryHook.build logger auditLog expiryNotifier
        )

    let apply (op: string) (plan: PendingInviteStore.Plan<'r>) : Async<Result<'r, PendingInviteStoreError>> = async {
        try
            let! outcome = document.Apply plan
            return outcome |> Result.mapError (PendingInviteStore.toStoreError op)
        with ex ->
            return Error(PendingInviteStoreError.StorageFailed(sprintf "%s: %s" op ex.Message))
    }

    /// Phase 547 3-arg constructor — audit log but no expiry notifier.
    new(storage: IBlobStorage, logger: ILogger, auditLog: IAuditLog option) =
        InMemoryPendingInviteStore(storage, logger, auditLog, None)

    /// 2-arg constructor — no audit log, so the expiry sweep stays silent
    /// (GP 11). `ComposeTeamRuntime` uses the 4-arg form to wire the
    /// resolved `IAuditLog` and, when the deployment opts in, the Phase
    /// 547.C expiry notifier.
    new(storage: IBlobStorage, logger: ILogger) = InMemoryPendingInviteStore(storage, logger, None, None)

    interface IPendingInviteStore with
        member _.Upsert(email, pending) =
            apply "Upsert" (PendingInviteStore.upsertPlan email pending)

        member _.Remove(email) = async {
            match! apply "Remove" (PendingInviteStore.removePlan email) with
            | Ok true -> return Ok()
            | Ok false -> return Error PendingInviteStoreError.NotFound
            | Error e -> return Error e
        }

        member _.TryConsumeForEmail(email) =
            apply "TryConsumeForEmail" (PendingInviteStore.consumePlan email)

        member _.ListAll() = async {
            try
                let! read = document.ReadAll()
                return read |> Result.mapError (PendingInviteStore.toStoreError "ListAll")
            with ex ->
                return Error(PendingInviteStoreError.StorageFailed(sprintf "ListAll: %s" ex.Message))
        }

        member _.SweepExpired() =
            apply "SweepExpired" PendingInviteStore.sweepPlan