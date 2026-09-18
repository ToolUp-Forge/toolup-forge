// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Teams

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 5h — ETag-based multi-instance pending-invite store ───────
//
// The distributed default for `IPendingInviteStore`: every mutation is
// read-with-ETag → transform → `UploadWithETag IfMatch`, so two replicas
// that both load the map and both write it cannot silently lose one
// another's update — the second writer's precondition fails, it
// re-reads, replays its own mutation over the peer's result, and
// retries. That is the whole difference from `InMemoryPendingInviteStore`,
// whose `SemaphoreSlim` serialises writers inside ONE process and
// nothing across processes.
//
// **Blob layout — identical to the single-instance store, on purpose.**
// Same container (`_platform`), same blob (`pending-invites.json`), same
// flat `email -> PendingInviteByEmail` map, same codec
// (`PendingInviteStore.encodeMap` / `decodeMap`). A deployment that grows
// from one replica to several — or a test that swaps the store under a
// populated blob — keeps every pending entry, in both directions. The
// cost is that the whole map is one blob and one ETag, so writes to
// unrelated emails contend with each other; the map is typically dozens
// of entries and the retry loop absorbs that.
//
// **No process-local cache (5h.t3).** The single-instance store's
// 30-second read cache is a throughput optimisation that is only correct
// when this process is the only writer; here a peer may consume an entry
// at any moment, and a cached read would serve it a second time (the
// double-auto-join the shard names). Every operation, `ListAll`
// included, reads the blob. Multi-instance correctness over
// single-instance throughput; a deployment whose read load makes that
// bite is what the optional Redis cache decorator (separate sub-phase)
// is for.
//
// **Conflict budget.** A lost precondition is retried up to
// `BlobPendingInviteStoreOptions.MaxRetries` times (default 3) with
// exponential backoff from `InitialBackoff` (default 100 ms: 100 / 200 /
// 400 ms). After that the operation surfaces
// `PendingInviteStoreError.Conflict` and the CALLER decides — the store
// never retries unboundedly against a hot blob. An infrastructure
// failure (`ConditionalWriteFailure`) is NOT retried here: it is
// `StorageFailed`, because looping against a failing backend is not
// contention handling.
//
// **Absent blob.** Read as the empty map, and the first write carries
// `IfAbsent` rather than `IfMatch` — so two replicas creating the blob
// at once also resolve by precondition, first writer wins, the second
// re-reads and replays. Note `IConditionalBlobStorage.DownloadWithETag`
// reports absence and infrastructure failure as the same `Error string`;
// this store treats both as "empty", exactly as the single-instance
// store's `Download` path does, and the `IfAbsent` guard is what makes
// that safe for WRITES — a misread can never overwrite a blob that
// exists. A read-only `ListAll` against a failing backend does report
// an empty list; that is the same answer the InMemory store gives.
//
// **Corrupt blob — fail closed (Phase 116 posture, carried over).** A
// present-but-undecodable blob is quarantined to a timestamped sibling
// (`pending-invites.json.corrupt-<utc>`), the canonical blob is healed
// to an empty map with `IfMatch <corrupt etag>` (so a peer that already
// repaired it is not clobbered), an `Error` is logged naming the
// quarantine path, and the triggering operation returns `StorageFailed`
// WITHOUT writing the caller's mutation. Proceeding from `Map.empty`
// here would persist empty-plus-one and erase every other pending
// invite.
//
// **Audit stays at the caller boundary (5h.t4).** `TeamInvitationHandler`
// emits `TeamInviteIssued` / `TeamInviteRevoked` / consume-side rows; this
// store is silent about the mutations it performs, exactly like
// `IShareTokenStore`'s backends. The ONE emission it owns is the
// `TeamInviteExpired` row for entries a sweep drops — a fact only the
// store observes — through the same `PendingInviteExpiryHook` the
// single-instance store runs (Phase 547 / 547.C), after the write that
// dropped them has succeeded.
//
// **Six portability rules** (mirroring `IPendingInviteStore.fs`):
//
//   1. Identity by value. Email + team id are strings; the map value is
//      the wire record. The ETag is an opaque per-provider token carried
//      from `DownloadWithETag` straight back into `IfMatch`, never
//      parsed, never persisted, never compared across providers.
//   2. Async at every boundary. Every member returns `Async<Result<_>>`;
//      the backoff is `Async.Sleep`, not a thread block.
//   3. Retry / supervision as data. The budget is an options record;
//      exhaustion is `PendingInviteStoreError.Conflict`, an infrastructure
//      failure is `StorageFailed`. No callbacks, no exceptions across the
//      seam.
//   4. Stateless between invocations. NOTHING is held between calls —
//      no cache, no lock, no last-seen ETag. Every operation's
//      correctness derives from the blob and its ETag at the moment of
//      the call, so any replica can serve any call.
//   5. No cross-shard ordering promises. Entries are independent; the
//      single blob is a layout choice, not an ordering guarantee.
//   6. Precision N/A — validity is timestamp-bounded, not tick-driven.

/// Retry budget for `BlobPendingInviteStore`'s read-modify-write loop.
/// A lost `IfMatch` precondition (a peer wrote between this replica's
/// read and its write) is retried up to `MaxRetries` further times, each
/// preceded by an exponentially growing pause that starts at
/// `InitialBackoff`. `BlobPendingInviteStoreOptions.defaults` is the
/// shard's 3 × (100 / 200 / 400 ms).
type BlobPendingInviteStoreOptions = {
    /// Retries after the first attempt before the operation surfaces
    /// `PendingInviteStoreError.Conflict`. `0` means a single attempt and
    /// no retry. Default 3.
    MaxRetries: int
    /// Pause before the first retry; each subsequent retry doubles it.
    /// Default 100 ms (so 100 / 200 / 400 ms across the default budget).
    /// `TimeSpan.Zero` is legal and makes the loop spin without pausing —
    /// useful only in tests that force conflicts deterministically.
    InitialBackoff: TimeSpan
}

/// Companion functions for `BlobPendingInviteStoreOptions`.
module BlobPendingInviteStoreOptions =

    /// The shard's default budget: 3 retries at 100 / 200 / 400 ms.
    let defaults: BlobPendingInviteStoreOptions = {
        MaxRetries = 3
        InitialBackoff = TimeSpan.FromMilliseconds 100.0
    }

/// Multi-instance implementation of `IPendingInviteStore` over the Phase
/// 600 `IConditionalBlobStorage` seam: ETag-guarded read-modify-write
/// with a bounded retry, no process-local state. Same blob and codec as
/// `InMemoryPendingInviteStore`, so the two are interchangeable under a
/// populated blob. See the file header for the concurrency, layout,
/// corrupt-blob and audit postures.
///
/// Construct it over a storage that implements the conditional seam —
/// the `IConditionalBlobStorage` constructors — or probe an `IBlobStorage`
/// with `BlobPendingInviteStore.TryCreate`, which is what `compose` does
/// when it auto-selects this store for `ServerConfig.ReplicaCount > 1`.
/// A backend that cannot do conditional writes cannot be correct here,
/// so the probe returns `None` rather than degrading to a racy
/// download-modify-upload.
type BlobPendingInviteStore
    /// Full constructor: the conditional storage, the logger, the
    /// optional audit log and inviter notifier the expiry hook runs, and
    /// the retry budget. The shorter constructors delegate here with
    /// `BlobPendingInviteStoreOptions.defaults`.
    (
        storage: IConditionalBlobStorage,
        logger: ILogger,
        auditLog: IAuditLog option,
        expiryNotifier: ((string * PendingInviteByEmail) list -> Async<unit>) option,
        options: BlobPendingInviteStoreOptions
    ) =

    let container = PendingInviteStore.platformContainer
    let blobName = PendingInviteStore.blobName

    let maxRetries = max 0 options.MaxRetries

    let backoffFor (retry: int) : TimeSpan =
        // retry = 1 → InitialBackoff, 2 → ×2, 3 → ×4, …
        TimeSpan.FromTicks(options.InitialBackoff.Ticks * (1L <<< (retry - 1)))

    /// Phase 547 / 547.C — the expiry side-effect hook, shared with the
    /// single-instance store. Runs AFTER the write that dropped the
    /// entries has succeeded, and outside any retry, so an audit row is
    /// emitted at most once per durable drop.
    let onExpired: (string * PendingInviteByEmail) list -> Async<unit> =
        PendingInviteExpiryHook.build logger auditLog expiryNotifier

    /// The current map with the ETag to CAS against, or the empty map and
    /// `None` when the blob is absent (see the header on the absent /
    /// unreadable collapse). A present-but-corrupt blob is quarantined
    /// and healed here and reported as `StorageFailed`, so no caller ever
    /// proceeds from a map derived from a failed decode.
    let read (op: string) : Async<Result<Map<string, PendingInviteByEmail> * string option, PendingInviteStoreError>> = async {
        match! storage.DownloadWithETag(container, blobName) with
        | Error _ -> return Ok(Map.empty, None)
        | Ok(bytes, etag) ->
            match PendingInviteStore.decodeMap bytes with
            | Ok map -> return Ok(map, Some etag)
            | Error reason ->
                let quarantine = PendingInviteStore.quarantineBlobName ()

                // Copy aside first (create-only: the name is timestamped to
                // the millisecond, and a collision is a peer quarantining the
                // same bytes, which is fine to lose). Then heal the canonical
                // blob to empty ONLY if it is still the corrupt version we
                // read — a peer that already healed or repaired it wins.
                let! _ = storage.UploadWithETag(container, quarantine, bytes, IfAbsent)

                let! _ =
                    storage.UploadWithETag(container, blobName, PendingInviteStore.encodeMap Map.empty, IfMatch etag)

                logger.Error(
                    sprintf
                        "[BlobPendingInviteStore] %s aborted: pending-invites blob was corrupt and has been quarantined to %s (%s). No invites were overwritten; the store self-heals to empty on the next read."
                        op
                        quarantine
                        reason,
                    None
                )

                return
                    Error(
                        PendingInviteStoreError.StorageFailed(
                            sprintf "pending-invites blob was corrupt (quarantined to %s)" quarantine
                        )
                    )
    }

    /// The read-modify-write loop every mutation goes through. `plan`
    /// is pure over the map it is handed and answers three things: the
    /// map to persist (`None` = nothing to write, the operation is
    /// complete after the read), the operation's result, and the
    /// expired entries the write drops (emitted through `onExpired` only
    /// once the write has landed). A lost precondition re-reads and
    /// re-plans from scratch — the plan is REPLAYED over the peer's
    /// result, never merged — up to the retry budget.
    let mutate
        (op: string)
        (plan:
            Map<string, PendingInviteByEmail>
                -> Map<string, PendingInviteByEmail> option * 'r * (string * PendingInviteByEmail) list)
        : Async<Result<'r, PendingInviteStoreError>> =
        let rec attempt (retry: int) = async {
            match! read op with
            | Error e -> return Error e
            | Ok(current, etag) ->
                match plan current with
                | None, result, _ -> return Ok result
                | Some updated, result, expired ->
                    let condition =
                        match etag with
                        | Some tag -> IfMatch tag
                        | None -> IfAbsent

                    match!
                        storage.UploadWithETag(container, blobName, PendingInviteStore.encodeMap updated, condition)
                    with
                    | Ok _ ->
                        do! onExpired expired
                        return Ok result
                    | Error(ConditionalWriteFailure message) ->
                        return
                            Error(
                                PendingInviteStoreError.StorageFailed(
                                    sprintf "%s: conditional write failed: %s" op message
                                )
                            )
                    | Error(ETagMismatch _) ->
                        if retry >= maxRetries then
                            logger.Warn(
                                sprintf
                                    "[BlobPendingInviteStore] %s lost the ETag precondition %d time(s) in a row — surfacing Conflict to the caller."
                                    op
                                    (retry + 1)
                            )

                            return Error PendingInviteStoreError.Conflict
                        else
                            let pause = backoffFor (retry + 1)

                            if pause > TimeSpan.Zero then
                                do! Async.Sleep pause

                            return! attempt (retry + 1)
        }

        attempt 0

    /// Wrap a member body so an unexpected exception (a throwing storage
    /// double, a codec defect) surfaces as `StorageFailed` rather than
    /// escaping the seam (portability rule 3).
    let guard (op: string) (body: Async<Result<'r, PendingInviteStoreError>>) = async {
        try
            return! body
        with ex ->
            return Error(PendingInviteStoreError.StorageFailed(sprintf "%s: %s" op ex.Message))
    }

    /// Full-argument constructor with the default retry budget.
    new
        (
            storage: IConditionalBlobStorage,
            logger: ILogger,
            auditLog: IAuditLog option,
            expiryNotifier: ((string * PendingInviteByEmail) list -> Async<unit>) option
        ) =
        BlobPendingInviteStore(storage, logger, auditLog, expiryNotifier, BlobPendingInviteStoreOptions.defaults)

    /// Audit-log-only constructor (no inviter notifier), default budget.
    new(storage: IConditionalBlobStorage, logger: ILogger, auditLog: IAuditLog option) =
        BlobPendingInviteStore(storage, logger, auditLog, None, BlobPendingInviteStoreOptions.defaults)

    /// Minimal constructor — no audit log, no notifier, default budget.
    /// The expiry sweep is then silent, as the single-instance store's
    /// 2-arg form is (GP 11).
    new(storage: IConditionalBlobStorage, logger: ILogger) =
        BlobPendingInviteStore(storage, logger, None, None, BlobPendingInviteStoreOptions.defaults)

    /// Build the store when `blobs` also implements `IConditionalBlobStorage`,
    /// `None` otherwise — the probing form, for a compose path that falls
    /// back to `InMemoryPendingInviteStore` (and lets
    /// `PendingInviteStoreInstanceValidator` say so) rather than failing
    /// the boot or, worse, picking a store that cannot be correct.
    static member TryCreate
        (
            blobs: IBlobStorage,
            logger: ILogger,
            auditLog: IAuditLog option,
            expiryNotifier: ((string * PendingInviteByEmail) list -> Async<unit>) option
        ) : IPendingInviteStore option =
        match box blobs with
        | :? IConditionalBlobStorage as cas ->
            Some(BlobPendingInviteStore(cas, logger, auditLog, expiryNotifier) :> IPendingInviteStore)
        | _ -> None

    /// Probing form with the default hooks (no audit log, no notifier).
    static member TryCreate(blobs: IBlobStorage, logger: ILogger) : IPendingInviteStore option =
        BlobPendingInviteStore.TryCreate(blobs, logger, None, None)

    interface IPendingInviteStore with
        member _.Upsert(email, pending) =
            guard "Upsert"
            <| mutate "Upsert" (fun current ->
                // Opportunistic compaction on write, as the single-instance
                // store does — keeps storage growth bounded without a
                // scheduled sweep, and a re-issue to an email whose prior
                // entry lapsed emits that lapse alongside the fresh issue.
                let compacted, expired = PendingInviteStore.partitionExpired DateTime.UtcNow current
                let updated = compacted |> Map.add (email.ToLowerInvariant()) pending
                Some updated, (), expired)

        member _.Remove(email) =
            guard "Remove"
            <| async {
                // The plan's own result is a `Result` (NotFound is decided
                // from the read, with nothing written), so flatten it over
                // the loop's substrate errors.
                let! outcome =
                    mutate "Remove" (fun current ->
                        let key = email.ToLowerInvariant()

                        if Map.containsKey key current then
                            Some(Map.remove key current), Ok(), []
                        else
                            None, Error PendingInviteStoreError.NotFound, [])

                return Result.bind id outcome
            }

        member _.TryConsumeForEmail(email) =
            guard "TryConsumeForEmail"
            <| mutate "TryConsumeForEmail" (fun current ->
                let key = email.ToLowerInvariant()

                match Map.tryFind key current with
                | None -> None, None, []
                | Some entry when entry.ExpiresAt < DateTime.UtcNow ->
                    // Lapsed: drop it AND surface the expiry — the invitee
                    // lands in neither Members nor Pending Invites, so
                    // without the row nobody is told.
                    Some(Map.remove key current), None, [ key, entry ]
                | Some entry -> Some(Map.remove key current), Some entry, [])

        member _.ListAll() =
            guard "ListAll"
            <| async {
                match! read "ListAll" with
                | Ok(current, _) -> return Ok(Map.toList current)
                | Error e -> return Error e
            }

        member _.SweepExpired() =
            guard "SweepExpired"
            <| mutate "SweepExpired" (fun current ->
                let compacted, expired = PendingInviteStore.partitionExpired DateTime.UtcNow current

                if List.isEmpty expired then
                    None, 0, []
                else
                    Some compacted, List.length expired, expired)