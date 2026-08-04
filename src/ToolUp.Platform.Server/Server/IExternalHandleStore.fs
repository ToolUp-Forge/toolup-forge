// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.BlobStorage

// ─── Phase 320 — the external-handle store ───────────────────────────
//
// Three jobs, and the third is the one the whole phase turns on:
//
//   1. **Routing.** A completion callback carries a handle id and
//      nothing else. Something has to say which job run that handle
//      belongs to, and it has to survive a restart — the callback may
//      arrive hours after the process that submitted the work died.
//   2. **Authentication.** The per-handle callback secret's hash lives
//      here, so a callback can be verified without the platform ever
//      holding the cleartext after hand-off.
//   3. **Exactly-once resolution.** `MarkTerminal` is a
//      compare-and-set: the FIRST caller to claim a handle gets `true`,
//      every subsequent caller gets `false`. Both the callback ingress
//      and the Phase 319 reconciliation poll go through it, so
//      whichever arrives first resolves the run and the other is a
//      no-op — including when they arrive concurrently.
//
// **Why `MarkTerminal` is not "read the flag, then write it".** The
// callback and the poll loop are genuinely concurrent — the poll runs on
// the scheduler tick, the callback on a request thread, and on a
// multi-replica deployment they are not even in the same process. A
// read-then-write gate is racy exactly in that window, and losing it
// means two terminal rows and two `JobCompleted` events for one unit of
// work: a job that reports success twice, notifies twice, and (for
// anything downstream that bills or ships on completion) charges twice.
// So the blob-backed implementation demands `IConditionalBlobStorage`
// and refuses to construct without it, on the
// `BlobPeerReplayGuard` precedent — a gate that is racy under load is
// worse than no gate, because it reads as defended.
//
// **What is stored, and what is deliberately not.** The record carries
// the handle (which already carries its own `ScopeId`), the job run id,
// the secret HASH, and the terminal flag. It does not carry the secret,
// the work payload, the result, or anything about what the work was —
// those live where they already lived (the backend, the job run row).
// This store is a routing and gating table, and keeping it that way is
// what makes it safe for the callback path to read before the caller has
// been authenticated.

/// Phase 320 — the durable record behind one outstanding external
/// hand-off. Every field is a primitive or a value record (GP 12 rule 1),
/// so the record round-trips through blob storage and survives a
/// restart.
type ExternalHandleRecord = {
    /// The handle as the backend minted it (Phase 484 restamping
    /// already applied). Carries `ScopeId`, which is the ONLY source of
    /// scope for the callback path — never the request.
    Handle: ExternalHandle
    /// The `JobRun.RunId` this handle resolves. The routing answer a
    /// callback needs.
    JobRunId: Guid
    /// Hex SHA-256 of the callback secret. The cleartext exists only in
    /// the `ExternalCallbackCredential` handed to the backend; it is
    /// never persisted, logged or audited platform-side.
    CallbackSecretHash: string
    /// `true` once some caller has claimed terminal resolution for this
    /// handle. The idempotency gate — see `IExternalHandleStore.MarkTerminal`.
    Terminal: bool
    /// When the hand-off was registered (UTC).
    RegisteredAt: DateTime
    /// When the terminal claim was won, `None` while outstanding.
    TerminalAt: DateTime option
}

/// Phase 320 — routing + authentication + exactly-once gating for
/// outstanding external hand-offs.
///
/// **Six portability rules (GP 12).** 1. Identity by value — `Guid`s and
/// value records across the whole surface. 2. Async at every boundary.
/// 3. Nothing to retry as data: the three operations are individually
/// idempotent-or-atomic, so there is no retry policy to express.
/// 4. Stateless between invocations — every call carries its whole key.
/// 5. No cross-handle ordering promised; atomicity is per handle id,
/// which is the only granularity `MarkTerminal` needs. 6. No timing
/// primitive on the surface, so no precision to declare.
type IExternalHandleStore =
    /// Record an accepted hand-off. Called once per handle, by the
    /// scheduler, at the point it learns both the handle and the run id.
    /// Overwrites any record for the same handle id — re-registering a
    /// handle is a hand-off being re-recorded, not a second unit of work.
    abstract Register: handle: ExternalHandle * jobRunId: Guid * callbackSecretHash: string -> Async<unit>

    /// Look a handle up by id alone — the callback path's only input.
    /// `None` for an unknown handle, and for one whose stored record
    /// failed its own scope cross-check.
    abstract Resolve: handleId: Guid -> Async<ExternalHandleRecord option>

    /// **Atomic compare-and-set.** Claims terminal resolution for
    /// `handleId`, returning `true` to the single caller that won and
    /// `false` to every other caller — including one racing it in
    /// another process — and `false` for a handle that does not exist
    /// (nothing to resolve).
    ///
    /// Implementations MUST make this atomic per handle id. An
    /// implementation that reads the flag and then writes it is not a
    /// conforming implementation of this member; see the file header for
    /// what losing the race costs.
    abstract MarkTerminal: handleId: Guid -> Async<bool>

    /// Whether the gate is shared across replicas, declared as data
    /// rather than inferred from the runtime type (GP 12 — mirroring
    /// `IPeerReplayGuard.IsDistributed`). `false` ⇒ each replica gates
    /// independently, so a callback landing on replica A and a poll
    /// running on replica B can both win. `true` ⇒ the claim is
    /// fleet-wide.
    abstract IsDistributed: bool

/// Phase 320 — minting and verifying the per-handle callback secret.
[<RequireQualifiedAccess>]
module ExternalCallbackSecret =
    /// Secret length in bytes before encoding. 32 bytes = 256 bits from
    /// a CSPRNG: the secret is a bearer credential with no rate limit
    /// worth relying on in front of it (the ingress throttle is
    /// friction, not the control), so it is sized to be unguessable
    /// rather than merely inconvenient.
    [<Literal>]
    let SecretBytes = 32

    /// Hex SHA-256 of `secret`. Deliberately a plain hash and not a
    /// password KDF: the input is 256 bits of CSPRNG output, so there is
    /// no dictionary to stretch against and no user-chosen entropy to
    /// protect. What the hash buys is that a compromise of the handle
    /// store does not yield credentials that can be replayed against
    /// the ingress — the same reason a share-token store holds digests.
    let hash (secret: string) : string =
        secret |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString

    /// Mint a fresh secret and its hash. The cleartext is returned
    /// exactly once, to be handed to the backend; only the hash is ever
    /// stored.
    let mint () : string * string =
        let secret = RandomNumberGenerator.GetBytes SecretBytes |> Convert.ToHexString
        secret, hash secret

    /// Constant-time verification of `presented` against a stored hash.
    ///
    /// The comparison is over the two HASHES, not the secrets, so both
    /// operands are fixed-length hex — which is what makes
    /// `FixedTimeEquals` meaningful here. Comparing a caller-supplied
    /// secret against a stored one directly would leak its length
    /// through the length check that has to precede any fixed-time
    /// compare (the `CommutativeCipher` note on the same hazard).
    let verify (storedHash: string) (presented: string) : bool =
        if String.IsNullOrEmpty storedHash || String.IsNullOrEmpty presented then
            false
        else
            let a = Encoding.UTF8.GetBytes(hash presented)
            let b = Encoding.UTF8.GetBytes storedHash

            a.Length = b.Length
            && CryptographicOperations.FixedTimeEquals(ReadOnlySpan a, ReadOnlySpan b)

/// Phase 320 — what happened when the ingress asked for a run to be
/// driven to its terminal state.
///
/// A DU rather than a `bool` because the caller has to respond
/// differently to each case and two of them are NOT errors: an
/// already-resolved handle is the idempotency guarantee working, and a
/// run that has left `AwaitingExternal` is the poll loop having got
/// there first. Collapsing those into "failed" would make a correct
/// duplicate callback look like a fault to whatever is watching the
/// backend's webhook delivery, and a backend that retries on non-2xx
/// would then retry forever.
[<RequireQualifiedAccess>]
type ExternalResolution =
    /// This caller won the terminal claim and the run was driven to its
    /// terminal state (or re-dispatched, for a retriable failure with
    /// attempts remaining — the Phase 319 mapping decides).
    | Resolved of status: string
    /// Some other caller — a duplicate callback, or the reconciliation
    /// poll — already claimed this handle. Nothing was written. The
    /// idempotent case, and a success from the caller's point of view.
    | AlreadyResolved
    /// The handle is known and was claimed by this caller, but no
    /// `AwaitingExternal` run matches it. Hand-edited state, a run
    /// abandoned by the orphan path, or a store that lost the handle on a
    /// round-trip.
    | NoAwaitingRun
    /// The handle's `ScopeId` disagreed with the run's. A GP 4 refusal —
    /// nothing was written.
    | ScopeMismatch of handleScope: string * runScope: string
    /// This deployment has no in-process scheduler / no handle store, so
    /// there is nothing to drive.
    | SinkNotConfigured

/// Phase 320 — the seam the completion-callback ingress drives a job run
/// through, implemented by the in-process job scheduler.
///
/// **Why a separate interface rather than a member on `IJobScheduler`.**
/// The same F#-cannot-author-a-default-interface-member constraint that
/// shaped `IIsolatedComputeBackend`: `IJobScheduler` has in-tree and
/// consumer implementations, and a new abstract member breaks every one
/// of them for a capability only the external-compute path uses. The
/// ingress resolves this interface from DI and returns
/// `SinkNotConfigured` when it is absent (GP 13).
///
/// **The `MarkTerminal` gate lives BEHIND this seam, not in front of
/// it.** The implementation claims the handle and drives the run in one
/// operation, so the callback path and the Phase 319 poll path share a
/// single gate rather than each calling one — two gates that must agree
/// is the shape that eventually disagrees.
type IExternalCompletionSink =
    /// Claim `handle` and drive its `AwaitingExternal` run to the
    /// terminal state `outcome` implies, using the same Phase 319
    /// mapping the reconciliation poll uses.
    ///
    /// `outcome` MUST be terminal; a non-terminal outcome returns
    /// `NoAwaitingRun` without writing (the wire contract already
    /// refuses non-terminal statuses upstream — this is the belt).
    abstract ResolveExternal:
        handle: ExternalHandle * jobRunId: Guid * outcome: ExternalOutcome -> Async<ExternalResolution>

/// Phase 320 — process-local `IExternalHandleStore`. Correct and
/// allocation-cheap for a single-instance deployment, and the store the
/// contract pack exercises; **not** distributed
/// (`IsDistributed = false`) and **not** durable — a restart loses every
/// outstanding registration, so a callback arriving after one is
/// rejected as unknown and the run resolves by poll instead.
///
/// The CAS is `ConcurrentDictionary.TryUpdate`, which is the atomic
/// compare-and-set primitive the interface asks for: it succeeds only if
/// the stored value is still the exact one that was read, so two threads
/// racing produce one `true`.
type InMemoryExternalHandleStore() =
    // Documented mutable-state exception to GP 5: a gate IS state, and
    // this is the whole of it. Concurrent by construction.
    let records = ConcurrentDictionary<Guid, ExternalHandleRecord>()

    /// Live record count. Exposed so a test can assert registration
    /// happened rather than inferring it from a downstream effect.
    member _.Count = records.Count

    interface IExternalHandleStore with
        member _.IsDistributed = false

        member _.Register(handle: ExternalHandle, jobRunId: Guid, callbackSecretHash: string) = async {
            let record = {
                Handle = handle
                JobRunId = jobRunId
                CallbackSecretHash = callbackSecretHash
                Terminal = false
                RegisteredAt = DateTime.UtcNow
                TerminalAt = None
            }

            records[handle.HandleId] <- record
        }

        member _.Resolve(handleId: Guid) = async {
            match records.TryGetValue handleId with
            | true, record -> return Some record
            | false, _ -> return None
        }

        member _.MarkTerminal(handleId: Guid) = async {
            // Bounded retry, not a spin: a `TryUpdate` here fails only
            // when a concurrent writer changed the record, and the only
            // change any writer makes is setting `Terminal` — so the
            // re-read either finds it already terminal (return `false`)
            // or finds an unrelated re-registration and tries once more.
            let mutable attempts = 0
            let mutable result = ValueNone

            while result.IsNone && attempts < 8 do
                attempts <- attempts + 1

                match records.TryGetValue handleId with
                | false, _ -> result <- ValueSome false
                | true, current ->
                    if current.Terminal then
                        result <- ValueSome false
                    else
                        let claimed = {
                            current with
                                Terminal = true
                                TerminalAt = Some DateTime.UtcNow
                        }

                        if records.TryUpdate(handleId, claimed, current) then
                            result <- ValueSome true

            return result |> ValueOption.defaultValue false
        }

/// Phase 320 — the durable, distributed-ready `IExternalHandleStore`
/// (`IsDistributed = true`). Two blobs per handle in the reserved
/// `_platform` container:
///
///   * `external-compute/handles/{scopeId}/{handleId}.json` — the
///     record, **scope-partitioned** (GP 4), so a scope's outstanding
///     hand-offs live under its own prefix and a scope-scoped
///     enumeration cannot see another scope's.
///   * `external-compute/handle-index/{handleId}` — the scope id, as
///     bytes. A pointer, so `Resolve` is one read rather than a listing
///     of every scope's partition.
///
/// **Why an index blob rather than a flat unpartitioned record.** The
/// callback carries a handle id and no scope, so `Resolve` has to get
/// from id to partition somehow, and the two candidates are a `List`
/// over the whole prefix or a pointer. The listing is O(outstanding
/// hand-offs) per callback and grows with the deployment; the pointer is
/// O(1) forever. It holds a scope id and nothing else — a routing key,
/// not tenant data — and the record it points at is re-checked against
/// the partition it was actually read from, so a wrong pointer produces
/// `None` rather than a cross-scope read (`Resolve`'s cross-check
/// below).
///
/// **Conditional writes are a hard requirement, checked at
/// construction.** `MarkTerminal` is the exactly-once gate, and a
/// download-modify-upload fallback races precisely where the callback
/// and the poll loop meet — the one place this store exists to be
/// atomic. Refused loudly at compose time rather than at the first
/// callback, on the `BlobPeerReplayGuard` / `BlobPrivacyBudgetLedger`
/// precedent.
type BlobExternalHandleStore(blobs: IBlobStorage, now: unit -> DateTime) =
    let container = "_platform"
    let recordPrefix = "external-compute/handles/"
    let indexPrefix = "external-compute/handle-index/"

    let cas =
        match box blobs with
        | :? IConditionalBlobStorage as c -> c
        | _ ->
            invalidArg
                "blobs"
                "BlobExternalHandleStore requires an IBlobStorage that also implements IConditionalBlobStorage (conditional writes). The MarkTerminal compare-and-set is the entire exactly-once guarantee — a download-modify-upload fallback races exactly where a completion callback and the reconciliation poll meet, and losing that race resolves one unit of work twice. Use InMemoryExternalHandleStore for a single-instance deployment, or a backend with conditional-write support."

    let json = FableConverters.create ()

    let recordName (scopeId: string) (handleId: Guid) =
        $"%s{recordPrefix}%s{scopeId}/%O{handleId}.json"

    let indexName (handleId: Guid) = $"%s{indexPrefix}%O{handleId}"

    /// Decode a record, or `None` for anything that does not
    /// round-trip. A record that cannot be read is treated exactly as an
    /// absent one: the callback is refused and the poll loop resolves
    /// the run, which is strictly better than throwing out of an
    /// unauthenticated ingress path.
    let tryDecode (bytes: byte[]) : ExternalHandleRecord option =
        try
            Some(JsonSerializer.Deserialize<ExternalHandleRecord>(Encoding.UTF8.GetString bytes, json))
        with _ ->
            None

    let encode (record: ExternalHandleRecord) =
        JsonSerializer.Serialize(record, json) |> Encoding.UTF8.GetBytes

    new(blobs: IBlobStorage) = BlobExternalHandleStore(blobs, (fun () -> DateTime.UtcNow))

    /// Build the store when `blobs` supports conditional writes, `None`
    /// otherwise — the probing form, for a compose path that falls back
    /// to `InMemoryExternalHandleStore` rather than failing the boot.
    static member TryCreate(blobs: IBlobStorage) : IExternalHandleStore option =
        match box blobs with
        | :? IConditionalBlobStorage -> Some(BlobExternalHandleStore blobs :> IExternalHandleStore)
        | _ -> None

    interface IExternalHandleStore with
        member _.IsDistributed = true

        member _.Register(handle: ExternalHandle, jobRunId: Guid, callbackSecretHash: string) = async {
            let record = {
                Handle = handle
                JobRunId = jobRunId
                CallbackSecretHash = callbackSecretHash
                Terminal = false
                RegisteredAt = now ()
                TerminalAt = None
            }

            // Record first, pointer second. A pointer with no record
            // resolves to `None` (a callback refused, poll resolves the
            // run); a record with no pointer is unreachable by callback
            // but still poll-resolvable. Both degrade to "poll it",
            // which is why this order needs no transaction.
            //
            // **A failed write RAISES rather than being discarded.** The
            // caller (the scheduler's `HandedOff` arm) already catches,
            // logs "no completion callback can route to this run; it will
            // resolve by poll", and carries on — so raising costs nothing
            // and buys the diagnosis. Swallowing the `Result` would hand
            // the backend a credential the platform cannot verify and
            // report nothing: the same degradation with the explanation
            // removed.
            let! stored = blobs.Upload(container, recordName handle.ScopeId handle.HandleId, encode record)

            match stored with
            | Error e ->
                return
                    failwith
                        $"BlobExternalHandleStore could not persist the handle record for %O{handle.HandleId} in scope %s{handle.ScopeId}: %s{e}"
            | Ok _ ->
                let! indexed = blobs.Upload(container, indexName handle.HandleId, Encoding.UTF8.GetBytes handle.ScopeId)

                match indexed with
                | Error e ->
                    return
                        failwith
                            $"BlobExternalHandleStore persisted the handle record for %O{handle.HandleId} but could not write its id index: %s{e}"
                | Ok _ -> return ()
        }

        member _.Resolve(handleId: Guid) = async {
            let! pointer = blobs.Download(container, indexName handleId)

            match pointer with
            | Error _ -> return None
            | Ok scopeBytes ->
                let scopeId = Encoding.UTF8.GetString scopeBytes
                let! stored = blobs.Download(container, recordName scopeId handleId)

                match stored |> Result.toOption |> Option.bind tryDecode with
                | None -> return None
                | Some record ->
                    // Cross-check: the record's own `ScopeId` must equal
                    // the partition it was read from, and its handle id
                    // must be the one asked for. A pointer that named
                    // the wrong partition, or a record copied between
                    // partitions, is refused rather than followed —
                    // which is what keeps GP 4 a structural property of
                    // this path rather than a property of the pointer
                    // blob being correct.
                    if record.Handle.ScopeId = scopeId && record.Handle.HandleId = handleId then
                        return Some record
                    else
                        return None
        }

        member this.MarkTerminal(handleId: Guid) = async {
            let store = this :> IExternalHandleStore

            // Read-check-CAS, retried on a genuine concurrent write.
            // `ETagMismatch` means somebody else wrote the record
            // between the read and the write; the re-read then either
            // finds it terminal (they won — return `false`) or finds it
            // still open (their write was a re-registration) and tries
            // again. Bounded, so a pathological backend cannot spin a
            // request thread indefinitely.
            let mutable attempts = 0
            let mutable result = ValueNone

            while result.IsNone && attempts < 8 do
                attempts <- attempts + 1

                let! resolved = store.Resolve handleId

                match resolved with
                | None -> result <- ValueSome false
                | Some record when record.Terminal -> result <- ValueSome false
                | Some record ->
                    let name = recordName record.Handle.ScopeId handleId
                    let! current = cas.DownloadWithETag(container, name)

                    match current with
                    | Error _ -> result <- ValueSome false
                    | Ok(bytes, etag) ->
                        match tryDecode bytes with
                        | None -> result <- ValueSome false
                        | Some latest when latest.Terminal -> result <- ValueSome false
                        | Some latest ->
                            let claimed = {
                                latest with
                                    Terminal = true
                                    TerminalAt = Some(now ())
                            }

                            let! written = cas.UploadWithETag(container, name, encode claimed, IfMatch etag)

                            match written with
                            | Ok _ -> result <- ValueSome true
                            | Error(ETagMismatch _) -> () // lost the race; re-read and re-decide
                            | Error(ConditionalWriteFailure _) -> result <- ValueSome false

            return result |> ValueOption.defaultValue false
        }