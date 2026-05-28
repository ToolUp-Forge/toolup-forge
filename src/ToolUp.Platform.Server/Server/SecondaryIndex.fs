module ToolUp.Platform.SecondaryIndex

open ToolUp.Platform.BlobStorage

/// Snapshot of one secondary index's consistency for the
/// `/dev/inspect` page (Phase 9f Step 5). Each indexed store
/// produces one entry per index it maintains; drift > 0 in any
/// dimension flags a recoverable bug class without alerting on
/// every transient miss.
type IndexConsistencyEntry = {
    /// Logical store name — e.g. "events", "jobs".
    StoreName: string
    /// Index name as it appears on disk — e.g. "_by-type",
    /// "_idempotency".
    IndexName: string
    /// How many entries were sampled.
    SampleSize: int
    /// Sampled entries that resolved consistently in both directions.
    ConsistentEntries: int
    /// Sampled index refs whose canonical record could not be
    /// resolved.
    OrphanedIndexEntries: int
    /// Sampled canonical records whose index ref was missing.
    UnindexedCanonicals: int
}

// ─── BlobIndex<'TKey, 'TValue> ───────────────────────────────────
//
// Shared helper for maintaining a secondary index over `IBlobStorage`.
// On-disk layout:
//
//     {indexPrefix}/{key-segment}/{value-id}.ref
//
// The `.ref` blob is either empty (existence-only) or carries a tiny
// denormalised payload — e.g. the canonical blob name for a
// `PersistentEventStore` event, or `(jobId, createdAt)` JSON for a
// `BlobJobStore` idempotency entry.
//
// Why this layout
// ───────────────
// - **Per-key listing is cheap.** `Lookup(key)` becomes a single
//   `IBlobStorage.List` call against `{indexPrefix}/{key-segment}/`,
//   returning the small set of value-ids registered under that key.
//   No download per non-match.
// - **Concurrent writers race only on the leaf blob.** `Upload` is
//   replace-on-write and `Delete` is idempotent, so two writers
//   writing the same `(key, valueId)` pair produce identical
//   content. No CAS needed.
// - **GP4 (team isolation) is preserved by the caller.** The
//   `indexPrefix` is always scope-prefixed (e.g.
//   `events/{scopeId}/_by-type`). The helper never widens it.
//
// Drift contract
// ──────────────
// Canonical state is authoritative. If a canonical write succeeds
// and the subsequent index `Add` fails, `Lookup` will miss the entry
// until `Rebuild` is run. If a canonical record is deleted while an
// index ref still points at it, the caller's resolver returns `None`
// and the entry is silently dropped. Both are recoverable bug
// classes; surfaces in `IndexConsistencyCheck` (Phase 9f Step 5)
// without alerting on every transient miss.
//
// Six-rule portability (Phase 9c)
// ───────────────────────────────
// - Identity by value (`'TKey` and `'TValue` are user-supplied value
//   types — no live handles).
// - Async at every boundary (every method returns `Async<_>`).
// - No retry / supervision (delegated to the caller's policy).
// - Stateless between calls (no instance state beyond the
//   `IBlobStorage` reference + the closure-captured config).
// - No cross-shard ordering (`Lookup` result order is unspecified).
// - Precision N/A (no scheduling).

/// Operations on a blob-backed secondary index. `'TKey` identifies a
/// key segment under `{indexPrefix}/`; `'TValue` identifies a leaf
/// `.ref` blob within that segment. Callers map both to/from path
/// strings via the `keyToSegment` / `valueToSegment` / `valueParser`
/// arguments to `BlobIndex.create`.
type BlobIndex<'TKey, 'TValue> = {
    /// Write `{indexPrefix}/{keyToSegment key}/{valueToSegment value}.ref`.
    /// `payload = None` writes an empty ref blob. `payload = Some bytes`
    /// stores a small denormalised payload that `Lookup` returns alongside
    /// the value. Idempotent — repeated `Add` of the same `(key, value)`
    /// produces identical content.
    Add: 'TKey -> 'TValue -> byte[] option -> Async<unit>
    /// Delete the leaf `.ref` blob. Idempotent — removing a missing
    /// entry succeeds silently.
    Remove: 'TKey -> 'TValue -> Async<unit>
    /// List every `(value, payload)` pair registered under `key`. Result
    /// order is unspecified. A failure to download a single payload is
    /// treated as `(value, None)` rather than a hard error — drift soft-
    /// miss.
    Lookup: 'TKey -> Async<('TValue * byte[] option) list>
    /// Idempotently re-write index entries from the supplied loader.
    /// Never deletes existing entries (that's a separate vacuum
    /// operation). Safe to run concurrently with writes. Returns the
    /// number of entries written.
    Rebuild: (unit -> Async<seq<'TKey * 'TValue * byte[] option>>) -> Async<int>
}

module BlobIndex =

    /// Strip a single trailing slash if present. The helper composes
    /// `{indexPrefix}/{keySegment}/{valueSegment}.ref` and tolerates
    /// callers that pass either form.
    let private trimTrailingSlash (s: string) =
        if s.EndsWith "/" then s.Substring(0, s.Length - 1) else s

    /// Compose the leaf blob name for a `(key, value)` pair.
    let private leafName indexPrefix keyToSegment valueToSegment key value =
        let prefix = trimTrailingSlash indexPrefix
        $"{prefix}/{keyToSegment key}/{valueToSegment value}.ref"

    /// Compose the listing prefix for `Lookup(key)`.
    let private keyPrefix indexPrefix keyToSegment key =
        let prefix = trimTrailingSlash indexPrefix
        $"{prefix}/{keyToSegment key}/"

    /// Extract the value-id segment from a leaf blob name. Path
    /// separators may be `/` (cloud stores) or `\` (LocalFileStorage on
    /// Windows); both are tolerated.
    let private extractValueSegment (blobName: string) : string option =
        let parts = blobName.Split([| '/'; '\\' |])

        if parts.Length = 0 then
            None
        else
            let last = parts[parts.Length - 1]

            if last.EndsWith ".ref" then
                Some(last.Substring(0, last.Length - 4))
            else
                None

    /// Construct a `BlobIndex` operating over the given `IBlobStorage`,
    /// `container`, and `indexPrefix` (path prefix scoped by the
    /// caller, e.g. `events/{scopeId}/_by-type`).
    ///
    /// `keyToSegment` and `valueToSegment` map keys / values to
    /// path-safe segments. Callers handling user-controlled keys
    /// should sanitise (e.g. sha256-hex). `valueParser` is the
    /// inverse of `valueToSegment`; it returns `None` for unparsable
    /// segments (which are dropped from the `Lookup` result).
    let create<'TKey, 'TValue when 'TKey: equality and 'TValue: equality>
        (storage: IBlobStorage)
        (container: string)
        (indexPrefix: string)
        (keyToSegment: 'TKey -> string)
        (valueToSegment: 'TValue -> string)
        (valueParser: string -> 'TValue option)
        : BlobIndex<'TKey, 'TValue> =

        let add key value payload = async {
            let blobName = leafName indexPrefix keyToSegment valueToSegment key value
            let bytes = payload |> Option.defaultValue Array.empty
            let! _ = storage.Upload(container, blobName, bytes)
            return ()
        }

        let remove key value = async {
            let blobName = leafName indexPrefix keyToSegment valueToSegment key value
            let! _ = storage.Delete(container, blobName)
            return ()
        }

        let lookup key = async {
            let prefix = keyPrefix indexPrefix keyToSegment key
            let! names = storage.List(container, prefix)

            let parsed =
                names
                |> List.choose (fun name ->
                    match extractValueSegment name with
                    | Some seg ->
                        match valueParser seg with
                        | Some v -> Some(name, v)
                        | None -> None
                    | None -> None)

            let! results =
                parsed
                |> List.map (fun (blobName, value) -> async {
                    let! result = storage.Download(container, blobName)

                    let payload =
                        match result with
                        | Ok bytes when bytes.Length > 0 -> Some bytes
                        | _ -> None

                    return (value, payload)
                })
                |> Async.Parallel

            return results |> Array.toList
        }

        let rebuild (loader: unit -> Async<seq<'TKey * 'TValue * byte[] option>>) = async {
            let! entries = loader ()
            let entryList = entries |> List.ofSeq

            let! _ =
                entryList
                |> List.map (fun (key, value, payload) -> add key value payload)
                |> Async.Parallel

            return entryList.Length
        }

        {
            Add = add
            Remove = remove
            Lookup = lookup
            Rebuild = rebuild
        }