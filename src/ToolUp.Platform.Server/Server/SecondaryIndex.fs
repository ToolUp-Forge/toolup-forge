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
    /// resolved — or, where a store re-checks the key against the record
    /// (the entity store, Phase 973), whose record no longer carries the
    /// key the ref is filed under.
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
// until `Rebuild` is run. If a ref outlives what it indexed — the
// canonical record was deleted, or re-written under another key and
// the old ref's `Remove` was refused — the ref is STALE: `Lookup`
// still lists it, so a caller re-checks every listed value against
// canonical state before answering with it (the entity store re-runs
// the index's extractor over the head, Phase 973) and drops the stale
// one. `Vacuum` reclaims stale refs. Both are recoverable bug
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
    /// Never deletes existing entries (that's `Vacuum`). Safe to run
    /// concurrently with writes. Returns the number of entries written.
    Rebuild: (unit -> Async<seq<'TKey * 'TValue * byte[] option>>) -> Async<int>
    /// Phase 973 — reclaim stale refs under `key`: list them, and delete
    /// every one the supplied `isStale` says canonical state no longer
    /// backs. Returns the number of refs removed; a refused delete is not
    /// counted and stays for the next pass. Idempotent — a second run over
    /// a clean key removes nothing. Safe beside writes: after a delete the
    /// value is asked again, and a ref a concurrent writer re-asserted in
    /// the meantime is written back with the payload it had.
    Vacuum: 'TKey -> ('TValue -> Async<bool>) -> Async<int>
}

module BlobIndex =

    /// Strip a single trailing slash if present. The helper composes
    /// `{indexPrefix}/{keySegment}/{valueSegment}.ref` and tolerates
    /// callers that pass either form.
    let private trimTrailingSlash (s: string) =
        if s.EndsWith "/" then s.Substring(0, s.Length - 1) else s

    /// Path-safe encoder for a key or value segment. Any character
    /// outside the safe set `[A-Za-z0-9_-]` is percent-encoded as its
    /// UTF-8 bytes (`|` → `%7C`, `:` → `%3A`, space → `%20`, etc.).
    /// Callers handing user-controlled or compound-key strings to
    /// `BlobIndex.create` should compose this with their own segment
    /// extractor so the resulting path segment is portable across
    /// every backend — most notably Windows NTFS, which rejects
    /// `< > : " | ? *` in path components.
    ///
    /// Round-trip safe: the same input always produces the same
    /// output, so `Add(key)` and `Lookup(key)` agree on the path.
    /// Single-character ASCII inputs in the safe set are emitted
    /// verbatim, so callers whose keys are already alphanumeric pay
    /// no overhead.
    let pathSafeSegment (s: string) : string =
        let isSafe (c: char) =
            (c >= 'A' && c <= 'Z')
            || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9')
            || c = '_'
            || c = '-'

        let buffer = System.Text.StringBuilder(s.Length)
        let mutable i = 0

        while i < s.Length do
            let c = s[i]

            if isSafe c then
                buffer.Append c |> ignore
                i <- i + 1
            else
                // Encode this code point as its UTF-8 bytes.
                // Handle surrogate pairs together so multi-byte
                // code points round-trip cleanly.
                let span =
                    if
                        System.Char.IsHighSurrogate c
                        && i + 1 < s.Length
                        && System.Char.IsLowSurrogate s[i + 1]
                    then
                        let pair = s.Substring(i, 2)
                        i <- i + 2
                        pair
                    else
                        i <- i + 1
                        string c

                for b in System.Text.Encoding.UTF8.GetBytes span do
                    buffer.Append(sprintf "%%%02X" b) |> ignore

        buffer.ToString()

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
            let! _ = storage.Upload(container, blobName, bytes) // best-effort-write: the drift contract above — canonical state is authoritative; a missed ref shows in IndexConsistencyCheck and Rebuild re-writes it
            return ()
        }

        let remove key value = async {
            let blobName = leafName indexPrefix keyToSegment valueToSegment key value
            let! _ = storage.Delete(container, blobName) // best-effort-write: derived index ref (the drift contract above); a stale ref soft-misses where the caller re-resolves canonical state, and the caller's Vacuum reclaims it (the entity store runs one on every lookup that meets one, Phase 973); the event and job stores run none, and a sampled one shows there as OrphanedIndexEntries
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

            // Phase 863 — `Rebuild` is the repair, so its count is of the
            // entries actually WRITTEN: a refused write is not counted, and a
            // caller comparing the count with what it loaded sees the gap.
            let! written =
                entryList
                |> List.map (fun (key, value, payload) -> async {
                    let blobName = leafName indexPrefix keyToSegment valueToSegment key value
                    let bytes = payload |> Option.defaultValue Array.empty

                    match! storage.Upload(container, blobName, bytes) with
                    | Ok _ -> return 1
                    | Error _ -> return 0
                })
                |> Async.Parallel

            return Array.sum written
        }

        let vacuum key (isStale: 'TValue -> Async<bool>) = async {
            let! listed = lookup key

            let! removed =
                listed
                |> List.map (fun (value, payload) -> async {
                    let! stale = isStale value

                    if not stale then
                        return 0
                    else
                        let blobName = leafName indexPrefix keyToSegment valueToSegment key value

                        match! storage.Delete(container, blobName) with
                        | Error _ -> return 0
                        | Ok() ->
                            // A writer that re-indexed this value under `key`
                            // between the check and the delete wrote the ref
                            // the delete just took: ask again, and put it back.
                            let! stillStale = isStale value

                            if stillStale then
                                return 1
                            else
                                let bytes = payload |> Option.defaultValue Array.empty
                                let! _ = storage.Upload(container, blobName, bytes) // best-effort-write: a re-asserted ref this put-back loses is a missed ref under the drift contract above — Rebuild re-writes it
                                return 0
                })
                |> Async.Parallel

            return Array.sum removed
        }

        {
            Add = add
            Remove = remove
            Lookup = lookup
            Rebuild = rebuild
            Vacuum = vacuum
        }