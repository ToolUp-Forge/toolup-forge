// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Globalization
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform.BlobStorage

// ─── Durable co-editing store — Phase 756 ────────────────────────────
//
// Phase 535 shipped `ICrdtDocumentStore` deliberately in-memory-only and
// named the durable store as the demand-sized follow-up. This is it: the same seam, the same fifteen
// contract cases, backed by the abstract `IBlobStorage` — so a document
// survives every participant closing their tab AND the process being
// restarted, which is the one durability promise the seam makes and the
// in-memory default could not keep.
//
// **Still no CRDT library, still no npm dependency, still no server-side
// interpretation of a payload.** Every byte this file writes is a
// payload the store received and hands back unchanged. What it adds over
// the in-memory default is exactly the persistence: a blob layout, a
// derivation of the per-document watermark from that layout, and a fold
// that keeps the blob count bounded.
//
// ## Storage layout
//
// Container: always `_platform`, the same reserved container
// `PersistentEventStore` and the blob-backed session / job stores use.
//
//   crdt/{scope}/{docId}/snapshot.json          — the folded prefix
//   crdt/{scope}/{docId}/log/{seq:D19}-{id:N}.json — one loose update
//
// Four properties of that layout are load-bearing:
//
//   1. **The scope is in the path, not in the blob** (GP 4). A stored
//      update records its payload, origin and sequence and NOT its
//      `CrdtDocRef` — the ref is reconstructed from the ref the caller
//      asked about. A corrupt or hand-edited blob therefore cannot
//      claim to belong to another team's document: a cross-tenant read
//      stays an impossible lookup rather than a filtered one.
//   2. **Both key segments are percent-encoded.** `DocId` is opaque and
//      module-owned, so it may contain a `/`. Interpolated raw, the
//      documents `a` and `a/b` would share a `List` prefix and each
//      would read the other's updates — a within-scope leak, and one
//      that only shows up on the day some module picks a path-like id.
//      `Uri.EscapeDataString` makes the segment boundary structural.
//   3. **One blob per update, named by sequence.** Blob stores offer no
//      cross-provider atomic append, so an append is always a fresh
//      unique blob — the same conclusion `PersistentEventStore` reached.
//      The 19-digit zero-padded sequence makes lexicographic order equal
//      numeric order (`Int64.MaxValue` is 19 digits); the GUID suffix
//      means two writers that pick the same sequence overwrite nothing.
//   4. **The watermark is DERIVED, never cached.** It is
//      `max(snapshot.upTo, highest loose sequence)`, recomputed from
//      storage on every operation. That is what makes restart survival
//      structural rather than something the implementation has to
//      remember to do: a freshly constructed store over the same
//      `IBlobStorage` is indistinguishable from the one that wrote the
//      log, so a cursor issued before a restart is still recognised
//      after it (portability rule 4 — nothing is remembered between
//      invocations).
//
// ## Two different things reduce two different costs
//
// The seam has one compaction operation and this implementation has two
// mechanisms, which is worth being explicit about because conflating
// them is how a "compaction" that cannot reduce bytes gets shipped:
//
//   * **The snapshot fold** (`CrdtSnapshotPolicy.SnapshotThreshold`,
//     store-internal, automatic). Rewrites the loose update blobs into
//     one `snapshot.json` holding the SAME updates, then deletes them.
//     Blob count falls; bytes do not; no client's converged state can
//     move, because no payload changed. This bounds read amplification.
//   * **`Compact`** (client-attested, the seam operation). Replaces the
//     prefix a cursor covers with one merged base a participant
//     computed. Bytes fall, because superseded updates are pruned. The
//     store cannot do this itself — it cannot merge opaque payloads —
//     which is why the seam is shaped the way it is.
//
// **The fold is crash-safe by ordering plus a read-side rule.** The
// snapshot is written FIRST and the loose blobs deleted after, so a
// crash between the two leaves an update represented twice. Reads
// therefore drop every loose blob whose sequence is `<= snapshot.upTo`
// — those are, by construction, exactly the ones the snapshot already
// carries. The interrupted state is then merely untidy, never wrong,
// and the next fold cleans it up. The reverse order (delete, then
// write) would lose updates on the same crash, which is why it is not
// used.
//
// A fold failure is swallowed: the update it was riding is already
// durable in its own blob, and the log alone reconstructs the document.
// A `Compact` failure is NOT swallowed — there the write is the
// operation the caller asked for, not an optimisation on top of it.
//
// ## Single-instance, and precisely in what sense (the Phase 9c note)
//
// Phase 535 flagged the in-memory default single-instance on two counts:
// the log lived in one process's memory, and fan-out is per-process.
// This store answers the first and inherits the second, so the honest
// statement is narrower than "durable therefore distributed":
//
//   * **Content survives a restart** — the log is in blob storage, and
//     the watermark is derived from it, so a new process reads exactly
//     what the old one wrote. A client's retained cursor still resolves.
//   * **Sequence assignment serialises on a per-document in-process
//     gate**, so concurrent appends within one process each receive a
//     distinct sequence. Across processes there is no such gate: two
//     writers may both derive watermark N and both write N+1. Nothing is
//     LOST (the GUID suffix keeps the blob names distinct and both
//     payloads are delivered), but a client whose cursor sits exactly at
//     N+1 may be handed only one of them on catch-up. A multi-writer
//     deployment therefore wants either the `IConditionalBlobStorage`
//     ETag seam or a distributed lease around assignment — the Phase 9c
//     distributed-companion shape — and this file is deliberately not
//     that, because half a distributed store is worse than an honest
//     single-instance one.
//   * **Fan-out is per-process**, unchanged from 535: the relay
//     decorator publishes on the local `INotificationChannel`. Peers on
//     other instances recover through `GetDiff` from their retained
//     cursor, which is the same recovery path a dropped local event
//     takes — so the failure mode is latency, not divergence.
//   * **No ordering promise across documents** (GP 12 rule 5).
//     `CrdtDocRef` is the shard key: two documents' sequences are
//     incomparable, and nothing here couples them — separate prefixes,
//     separate gates, separate folds.

/// Blob layout for the durable co-editing store. Internal: the paths are
/// an implementation detail of `BlobCrdtDocumentStore`, not a contract
/// any caller may depend on — the seam promises a cursor, not a filename.
module internal BlobCrdtLayout =

    /// The reserved platform container, shared with the event / session /
    /// job stores. Never a scope-derived container: the scope lives in
    /// the blob path, which is what keeps a `List` per-document.
    [<Literal>]
    let PlatformContainer = "_platform"

    [<Literal>]
    let private SnapshotLeaf = "snapshot.json"

    /// Percent-encode one key segment so an opaque `DocId` containing a
    /// path separator cannot escape its own prefix. Never reversed — the
    /// ref always comes from the caller, never from a blob name.
    let private segment (value: string) =
        Uri.EscapeDataString(if isNull value then "" else value)

    /// `crdt/{scope}/{docId}/` — everything for one document, and
    /// nothing belonging to any other.
    let docPrefix (ref: CrdtDocRef) =
        $"crdt/{segment ref.Scope}/{segment ref.DocId}/"

    let logPrefix (ref: CrdtDocRef) = docPrefix ref + "log/"

    let snapshotName (ref: CrdtDocRef) = docPrefix ref + SnapshotLeaf

    /// `…/log/0000000000000000012-{guid:N}.json`. Zero-padded to 19 so
    /// the lexicographic order a blob store lists in is the numeric
    /// order catch-up needs.
    let logName (ref: CrdtDocRef) (sequence: int64) (id: Guid) =
        $"{logPrefix ref}{sequence:D19}-{id:N}.json"

    /// The sequence a loose log blob's name encodes, or `None` for a
    /// name that is not one of ours. Tolerant by design: a foreign blob
    /// under the prefix is skipped rather than failing the read.
    let sequenceOfLogName (name: string) : int64 option =
        if isNull name then
            None
        else
            let leaf =
                let cut = name.LastIndexOfAny [| '/'; '\\' |]
                if cut < 0 then name else name.Substring(cut + 1)

            let digits =
                match leaf.IndexOf '-' with
                | -1 -> leaf
                | i -> leaf.Substring(0, i)

            match Int64.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, sequence when sequence > 0L -> Some sequence
            | _ -> None

/// JSON encoding of one stored update and of a folded snapshot.
///
/// Deliberately hand-rolled over `JsonDocument` rather than reflected
/// off the record, mirroring `PersistentEventStore`: the payload is
/// arbitrary bytes (base64 here — a CRDT update is not text and must
/// survive a round trip byte-for-byte), and the `CrdtDocRef` is
/// deliberately ABSENT so a blob cannot assert a scope.
module internal BlobCrdtCodec =

    let private base64Of (payload: byte[]) =
        if isNull (box payload) then
            ""
        else
            Convert.ToBase64String payload

    let private updateDto (update: CrdtUpdate) = {|
        payload = base64Of update.Payload
        originSession = update.OriginSession
        sequence = update.Sequence
        appendedAt = update.AppendedAt.ToUniversalTime().ToString("o")
    |}

    let serializeUpdate (update: CrdtUpdate) : byte[] =
        JsonSerializer.Serialize(updateDto update) |> Encoding.UTF8.GetBytes

    let serializeSnapshot (upTo: int64) (updates: CrdtUpdate list) : byte[] =
        let dto = {|
            upTo = upTo
            updates = updates |> List.map updateDto
        |}

        JsonSerializer.Serialize dto |> Encoding.UTF8.GetBytes

    let private readUpdate (ref: CrdtDocRef) (element: JsonElement) : CrdtUpdate = {
        Ref = ref
        Payload =
            match element.GetProperty("payload").GetString() with
            | null
            | "" -> Array.empty
            | encoded -> Convert.FromBase64String encoded
        OriginSession = element.GetProperty("originSession").GetString()
        Sequence = element.GetProperty("sequence").GetInt64()
        AppendedAt =
            DateTime.Parse(
                element.GetProperty("appendedAt").GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            )
    }

    /// Parse one loose update blob. `None` on anything unreadable —
    /// a partially-written or foreign blob is skipped, never fatal.
    let tryParseUpdate (ref: CrdtDocRef) (bytes: byte[]) : CrdtUpdate option =
        try
            use doc = JsonDocument.Parse(Encoding.UTF8.GetString bytes)
            Some(readUpdate ref doc.RootElement)
        with _ ->
            None

    /// Parse the snapshot blob into `(upTo, updates)`. `None` on
    /// anything unreadable, which degrades the document to "the loose
    /// log is all there is" rather than failing the read.
    let tryParseSnapshot (ref: CrdtDocRef) (bytes: byte[]) : (int64 * CrdtUpdate list) option =
        try
            use doc = JsonDocument.Parse(Encoding.UTF8.GetString bytes)
            let root = doc.RootElement
            let upTo = root.GetProperty("upTo").GetInt64()

            let updates =
                root.GetProperty("updates").EnumerateArray()
                |> Seq.map (readUpdate ref)
                |> List.ofSeq

            Some(upTo, updates)
        with _ ->
            None

/// Storage-failure surfacing. A module-level function because the seam
/// returns `Async<CrdtUpdate>` / `Async<CrdtSnapshot>` with no error
/// channel, so an infrastructure failure can only be raised — the same
/// posture `Compact`'s `invalidArg` refusals already take.
module internal BlobCrdtErrors =
    let storage (operation: string) (ref: CrdtDocRef) (message: string) : 'T =
        failwith $"BlobCrdtDocumentStore.%s{operation} failed for document '%s{CrdtDocRef.toKey ref}': %s{message}"

/// One document as storage currently holds it: the updates a joiner
/// needs (snapshot then loose tail, in sequence order), the derived
/// watermark, and how many loose blobs are outstanding against the
/// fold threshold.
type internal BlobCrdtDocumentState = {
    Updates: CrdtUpdate list
    Watermark: int64
    LooseCount: int
}

/// Durable `ICrdtDocumentStore` over the composed `IBlobStorage` (Phase
/// 756). Selected by `ServerConfig.CrdtDocuments = PersistentCrdtDocuments
/// policy`; `compose` wraps it in `NotifyingCrdtDocumentStore` exactly as
/// it wraps the in-memory default, so fan-out is inherited rather than
/// re-implemented.
///
/// **Cursor encoding is this implementation's own business.**
/// `StateVector` is opaque by contract, so these bytes — `bcrdt1:` plus
/// the decimal watermark — are not part of the seam. The prefix is there
/// so a cursor issued by a DIFFERENT implementation (the in-memory
/// store's bare decimal, say) is recognised as foreign and degrades to
/// whole-document catch-up rather than being misread as a watermark of
/// this store's own. That is cursor law 3 taken seriously: an
/// unrecognised vector is never an error, and never a silent wrong
/// answer either.
///
/// `now` is injectable for deterministic tests. All five operations
/// serialise on a per-document gate — reads included, so a read can
/// never observe a fold half-applied.
type BlobCrdtDocumentStore(blobStorage: IBlobStorage, policy: CrdtSnapshotPolicy, ?now: unit -> DateTime) =
    let clock = defaultArg now (fun () -> DateTime.UtcNow)

    /// One gate per document. The ref is the shard key, so two documents
    /// never contend and no gate is ever held across a `Compact` of
    /// another document (GP 12 rule 5).
    let gates = ConcurrentDictionary<CrdtDocRef, SemaphoreSlim>()

    let withDocument (ref: CrdtDocRef) (work: unit -> Async<'T>) = async {
        let gate = gates.GetOrAdd(ref, (fun _ -> new SemaphoreSlim(1, 1)))
        do! gate.WaitAsync() |> Async.AwaitTask

        try
            return! work ()
        finally
            gate.Release() |> ignore
    }

    let CursorPrefix = "bcrdt1:"

    /// Watermark -> opaque cursor. Zero (an untouched document) encodes
    /// as `StateVector.empty`, so law 1 holds with no special case at the
    /// read site.
    let encode (watermark: int64) : StateVector =
        if watermark <= 0L then
            StateVector.empty
        else
            {
                Bytes = Encoding.UTF8.GetBytes(CursorPrefix + string watermark)
            }

    /// Opaque cursor -> watermark. `None` for anything this store did
    /// not issue, which every caller below turns into "send everything".
    let decode (vector: StateVector) : int64 option =
        if StateVector.isEmpty vector then
            Some 0L
        else
            try
                let text = Encoding.UTF8.GetString vector.Bytes

                if text.StartsWith(CursorPrefix, StringComparison.Ordinal) then
                    match
                        Int64.TryParse(
                            text.Substring CursorPrefix.Length,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture
                        )
                    with
                    | true, watermark -> Some watermark
                    | _ -> None
                else
                    // A different implementation's cursor. Not an error.
                    None
            with _ ->
                // Non-UTF-8 bytes — a foreign cursor, not an error.
                None

    /// Read one document from storage. Caller holds the gate.
    ///
    /// The dedup rule is the crash-safety half of the fold: a loose blob
    /// whose sequence is `<= upTo` is already inside the snapshot, so it
    /// is dropped whether the fold's delete pass completed or not.
    let readDocument (ref: CrdtDocRef) = async {
        let! snapshotBytes = blobStorage.Download(BlobCrdtLayout.PlatformContainer, BlobCrdtLayout.snapshotName ref)

        let snapshot =
            match snapshotBytes with
            | Ok bytes -> BlobCrdtCodec.tryParseSnapshot ref bytes
            | Error _ -> None

        let upTo, folded =
            match snapshot with
            | Some(upTo, updates) -> upTo, updates
            | None -> 0L, []

        let! names = blobStorage.List(BlobCrdtLayout.PlatformContainer, BlobCrdtLayout.logPrefix ref)

        let sequenced =
            names
            |> List.choose (fun name -> BlobCrdtLayout.sequenceOfLogName name |> Option.map (fun s -> s, name))

        let loose = sequenced |> List.filter (fun (sequence, _) -> sequence > upTo)

        let! downloaded =
            loose
            |> List.map (fun (sequence, name) -> async {
                let! content = blobStorage.Download(BlobCrdtLayout.PlatformContainer, name)

                return
                    match content with
                    | Ok bytes ->
                        BlobCrdtCodec.tryParseUpdate ref bytes
                        |> Option.map (fun u -> sequence, name, u)
                    | Error _ -> None
            })
            |> Async.Parallel

        let tail = downloaded |> Array.choose id |> List.ofArray

        // Sorted by (sequence, blob name): the name breaks a tie that a
        // cross-process race could produce, so every reader orders an
        // identical log identically.
        let ordered =
            tail
            |> List.sortBy (fun (sequence, name, _) -> sequence, name)
            |> List.map (fun (_, _, update) -> update)

        let highestStored =
            sequenced |> List.fold (fun acc (sequence, _) -> max acc sequence) 0L

        return {
            Updates = folded @ ordered
            Watermark = max upTo highestStored
            LooseCount = List.length ordered
        }
    }

    /// Fold `updates` into the snapshot blob and prune every loose blob
    /// the snapshot now carries. Caller holds the gate. Raises on a
    /// failed snapshot write; the delete pass is best-effort, because a
    /// surviving duplicate is dropped by `readDocument`'s dedup rule.
    let writeSnapshot (operation: string) (ref: CrdtDocRef) (updates: CrdtUpdate list) = async {
        let upTo = updates |> List.fold (fun acc u -> max acc u.Sequence) 0L

        let! written =
            blobStorage.Upload(
                BlobCrdtLayout.PlatformContainer,
                BlobCrdtLayout.snapshotName ref,
                BlobCrdtCodec.serializeSnapshot upTo updates
            )

        match written with
        | Error message -> return BlobCrdtErrors.storage operation ref message
        | Ok _ ->
            let! names = blobStorage.List(BlobCrdtLayout.PlatformContainer, BlobCrdtLayout.logPrefix ref)

            let superseded =
                names
                |> List.filter (fun name ->
                    match BlobCrdtLayout.sequenceOfLogName name with
                    | Some sequence -> sequence <= upTo
                    | None -> false)

            do!
                superseded
                |> List.map (fun name -> blobStorage.Delete(BlobCrdtLayout.PlatformContainer, name) |> Async.Ignore)
                |> Async.Parallel
                |> Async.Ignore

            return ()
    }

    interface ICrdtDocumentStore with
        member _.Append(ref, payload, originSession) =
            withDocument ref (fun () -> async {
                let! document = readDocument ref

                let update = {
                    Ref = ref
                    Payload = payload
                    OriginSession = originSession
                    Sequence = document.Watermark + 1L
                    AppendedAt = clock ()
                }

                let! written =
                    blobStorage.Upload(
                        BlobCrdtLayout.PlatformContainer,
                        BlobCrdtLayout.logName ref update.Sequence (Guid.NewGuid()),
                        BlobCrdtCodec.serializeUpdate update
                    )

                match written with
                | Error message -> return BlobCrdtErrors.storage "Append" ref message
                | Ok _ ->
                    // The update is durable from here on, so the fold is
                    // an optimisation riding this call and its failure is
                    // swallowed: the loose log alone still reconstructs
                    // the document, and the next append retries the fold.
                    if
                        policy.SnapshotThreshold > 0
                        && document.LooseCount + 1 >= policy.SnapshotThreshold
                    then
                        try
                            do! writeSnapshot "Append" ref (document.Updates @ [ update ])
                        with _ ->
                            ()

                    return update
            })

        member _.GetStateVector(ref) =
            withDocument ref (fun () -> async {
                let! document = readDocument ref
                return encode document.Watermark
            })

        member _.GetDiff(ref, since) =
            withDocument ref (fun () -> async {
                let! document = readDocument ref

                return
                    match decode since with
                    | Some watermark -> document.Updates |> List.filter (fun u -> u.Sequence > watermark)
                    | None ->
                        // Unrecognised cursor — send the whole document.
                        // Re-applying a held update is free; losing one
                        // is not.
                        document.Updates
            })

        member _.Snapshot(ref) =
            withDocument ref (fun () -> async {
                let! document = readDocument ref

                return {
                    Ref = ref
                    Updates = document.Updates
                    Vector = encode document.Watermark
                }
            })

        member _.Compact(ref, merged, covers) =
            withDocument ref (fun () -> async {
                let covered =
                    match decode covers with
                    | Some w when w > 0L -> w
                    | Some _ ->
                        invalidArg
                            "covers"
                            "Compact requires a state vector this store issued; StateVector.empty covers nothing and would append a second copy of the document rather than compact it."
                    | None ->
                        invalidArg
                            "covers"
                            "Compact requires a state vector this store issued; the supplied vector is not one this store can interpret."

                let! document = readDocument ref
                let tail = document.Updates |> List.filter (fun u -> u.Sequence > covered)

                // The merged base takes the slot of the last covered
                // update, so it sorts ahead of the surviving tail and a
                // client already past `covered` is not re-sent it.
                let baseUpdate = {
                    Ref = ref
                    Payload = merged
                    OriginSession = CrdtDocument.CompactionOrigin
                    Sequence = covered
                    AppendedAt = clock ()
                }

                let updates = baseUpdate :: tail

                // Not swallowed, unlike the fold above: here the write IS
                // the operation the caller asked for.
                do! writeSnapshot "Compact" ref updates

                return {
                    Ref = ref
                    Updates = updates
                    Vector = encode document.Watermark
                }
            })