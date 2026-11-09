// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Scope.Teams

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

// ─── The multi-instance pending-invite store ─────────────────────────
//
// The distributed default for `IPendingInviteStore`: the pending-invites
// document (`InMemoryPendingInviteStore.fs` holds its layout, codec and
// plans) over `BlobDocumentStore` on a backend WITH conditional writes, so
// two replicas that both read the map and both write it cannot silently
// lose one another's update — the second writer's precondition fails, it
// re-reads, replays its own plan over the peer's result, and retries.
//
// **Blob layout — identical to the single-instance store, on purpose.**
// Same document, same codec, so a deployment that grows from one replica
// to several — or a test that swaps the store under a populated blob —
// keeps every pending entry, in both directions. The cost is that the
// whole map is one document and one ETag, so writes to unrelated emails
// contend with each other; the map is typically dozens of entries and the
// retry loop absorbs that.
//
// **No process-local cache.** A peer may consume an entry at any moment,
// and a cached read would serve it a second time (the double-auto-join).
// Every operation, `ListAll` included, reads the document.
//
// **Conflict budget.** A lost precondition is retried up to
// `BlobPendingInviteStoreOptions.MaxRetries` times (default 3) with
// exponential backoff from `InitialBackoff` (default 100 ms: 100 / 200 /
// 400 ms). After that the operation surfaces
// `PendingInviteStoreError.Conflict` and the CALLER decides. An
// infrastructure failure is NOT retried: it is `StorageFailed`.
//
// **Absent, unreadable and corrupt.** The document store decides all
// three: absence is established before the read (the first write then
// carries `IfAbsent`, so two replicas creating it resolve by
// precondition), a read failure is `StorageFailed` — never the empty map,
// for a write or for `ListAll` — and undecodable bytes are quarantined and
// left in place, every operation refused until an operator repairs or
// removes them. The heal-to-empty this store performed (Phase 5h) is
// RETIRED: see `InMemoryPendingInviteStore.fs`.
//
// **Audit stays at the caller boundary.** `TeamInvitationHandler` emits
// `TeamInviteIssued` / `TeamInviteRevoked` / consume-side rows; the ONE
// emission the store owns is the `TeamInviteExpired` row for entries a
// write drops — a fact only the store observes — through
// `PendingInviteExpiryHook`, after the write that dropped them has landed.
//
// **Six portability rules** (mirroring `IPendingInviteStore.fs`):
//
//   1. Identity by value. Email + team id are strings; the ETag is an
//      opaque per-provider token, never parsed or persisted.
//   2. Async at every boundary; the backoff is `Async.Sleep`.
//   3. Retry / supervision as data. The budget is an options record;
//      exhaustion is `Conflict`, an infrastructure failure `StorageFailed`.
//      No exceptions across the seam.
//   4. Stateless between invocations: no cache, no last-seen ETag.
//   5. No cross-shard ordering promises.
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

/// Multi-instance implementation of `IPendingInviteStore`: the
/// pending-invites document over a backend that implements
/// `IConditionalBlobStorage`, ETag-guarded with a bounded retry and no
/// process-local state. Same document and codec as
/// `InMemoryPendingInviteStore`, so the two are interchangeable under a
/// populated blob. See the file header for the concurrency, layout,
/// corrupt-document and audit postures.
///
/// The constructors REFUSE (`ArgumentException`) a backend that does not
/// implement `IConditionalBlobStorage`: it cannot be correct across
/// replicas. `BlobPendingInviteStore.TryCreate` is the probing form —
/// `None` for such a backend — which is what `compose` uses when it
/// auto-selects this store for `ServerConfig.MultiInstance.ReplicaCount > 1`.
type BlobPendingInviteStore
    /// Full constructor: the storage, the logger, the optional audit log
    /// and inviter notifier the expiry hook runs, and the retry budget.
    (
        storage: IBlobStorage,
        logger: ILogger,
        auditLog: IAuditLog,
        expiryNotifier: ((string * PendingInviteByEmail) list -> Async<unit>) option,
        options: BlobPendingInviteStoreOptions
    ) =

    do
        match box storage with
        | :? IConditionalBlobStorage -> ()
        | _ ->
            invalidArg
                (nameof storage)
                "BlobPendingInviteStore needs a backend implementing IConditionalBlobStorage; use BlobPendingInviteStore.TryCreate to probe, or InMemoryPendingInviteStore for a single instance"

    let document =
        PendingInviteStore.Document(
            BlobDocumentStore<Map<string, PendingInviteByEmail>>(
                storage,
                PendingInviteStore.codec,
                logger,
                {
                    MaxRetries = options.MaxRetries
                    InitialBackoff = options.InitialBackoff
                }
            ),
            PendingInviteExpiryHook.build logger auditLog expiryNotifier
        )

    /// One operation's plan, applied as one guarded update; an unexpected
    /// exception (a throwing storage double, a codec defect) surfaces as
    /// `StorageFailed` rather than escaping the seam (portability rule 3).
    let apply (op: string) (plan: PendingInviteStore.Plan<'r>) : Async<Result<'r, PendingInviteStoreError>> = async {
        try
            let! outcome = document.Apply plan

            return
                outcome
                |> Result.mapError (fun error ->
                    match error with
                    | BlobMapStoreError.Contended(_, attempts) ->
                        logger.Warn(
                            sprintf
                                "[BlobPendingInviteStore] %s lost the ETag precondition %d time(s) in a row — surfacing Conflict to the caller."
                                op
                                attempts
                        )
                    | _ -> ()

                    PendingInviteStore.toStoreError op error)
        with ex ->
            return Error(PendingInviteStoreError.StorageFailed(sprintf "%s: %s" op ex.Message))
    }

    /// Full-argument constructor with the default retry budget.
    new
        (
            storage: IBlobStorage,
            logger: ILogger,
            auditLog: IAuditLog,
            expiryNotifier: ((string * PendingInviteByEmail) list -> Async<unit>) option
        ) =
        BlobPendingInviteStore(storage, logger, auditLog, expiryNotifier, BlobPendingInviteStoreOptions.defaults)

    /// Audit-log-only constructor (no inviter notifier), default budget.
    new(storage: IBlobStorage, logger: ILogger, auditLog: IAuditLog) =
        BlobPendingInviteStore(storage, logger, auditLog, None, BlobPendingInviteStoreOptions.defaults)

    /// Minimal constructor — no audit log, no notifier, default budget.
    /// The expiry sweep is then silent (GP 11).
    new(storage: IBlobStorage, logger: ILogger) =
        BlobPendingInviteStore(storage, logger, NoOpAuditLog(), None, BlobPendingInviteStoreOptions.defaults)

    /// Build the store when `blobs` implements `IConditionalBlobStorage`,
    /// `None` otherwise — the probing form, for a compose path that falls
    /// back to `InMemoryPendingInviteStore` (and lets
    /// `PendingInviteStoreInstanceValidator` say so) rather than failing
    /// the boot or picking a store that cannot be correct.
    static member TryCreate
        (
            blobs: IBlobStorage,
            logger: ILogger,
            auditLog: IAuditLog,
            expiryNotifier: ((string * PendingInviteByEmail) list -> Async<unit>) option
        ) : IPendingInviteStore option =
        match box blobs with
        | :? IConditionalBlobStorage ->
            Some(BlobPendingInviteStore(blobs, logger, auditLog, expiryNotifier) :> IPendingInviteStore)
        | _ -> None

    /// Probing form with the default hooks (no audit log, no notifier).
    static member TryCreate(blobs: IBlobStorage, logger: ILogger) : IPendingInviteStore option =
        BlobPendingInviteStore.TryCreate(blobs, logger, NoOpAuditLog(), None)

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