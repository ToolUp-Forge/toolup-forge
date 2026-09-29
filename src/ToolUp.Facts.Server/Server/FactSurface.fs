// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── The metric surface (Phase 702) ──────────────────────────────────
//
// Phase 701's population read is one enumeration over the scope's heads:
// correct at any size, and — because `BlobFactStore` is blob-per-fact —
// one network round trip and one JSON deserialisation *per subject*. It
// was measured at 0.200 ms/head, which is 60 seconds for the 300,000
// subjects the population tier exists for, against 1.9 seconds for the
// decidable pipeline over the same cardinality. The gap is not the
// ranking. It is reading the facts at all.
//
// The **metric surface** closes it. Per (scope, metric) it holds a
// derived, columnar snapshot of the current heads — one row per head,
// carrying exactly `PopulationMember` (subject, magnitude, period, `AsOf`,
// method identity) plus the disclosure class, and nothing else. One blob,
// one download, one linear parse. The full `Fact` records are re-read
// only for the top-k the ranking actually returns, which the contract
// bounds at `PopulationQuery.MaxTopK` — so the number of fact reads per
// question stops scaling with the population.
//
// **Derived, never authoritative (GP 5).** The fact log is the truth; the
// surface is a projection of it, in exactly the posture the roadmap
// engine's `state.json` cache holds: slow to rebuild, never wrong, safe to
// delete. `FactSurface.drop` is a cache flush, not data loss.
//
// **"Never wrong" is structural here, not a discipline.** Maintenance
// happens on `Assert`, but a read does not *trust* that it happened. The
// store is append-only and one blob per fact, so the blob *listing* is a
// census of every fact that exists — and a snapshot records the census it
// has folded in. A census that disagrees means something reached the log
// without reaching the surface (a failed update, a second replica, a
// restore) or left it (an erasure), and the read reconciles before
// answering: it folds in the few it is missing, or rebuilds outright. So
// the failure mode of every maintenance path is a slower read, never a
// different answer. Note the census is the *same* `List` call the
// enumeration path already makes first, so the check is free relative to
// the path it replaces. The listing itself is the truth and is not
// bounded: a census kept anywhere else would be derived, and a derived
// census that missed a write would hide a fact — the one failure this
// design exists to rule out.
//
// **What a snapshot records of that census (Phase 891).** Not the ids.
// Until Phase 891 a snapshot carried the id of every fact it had folded
// that was not one of its rows — every superseded head, and every fact of
// every other metric in the scope — so it grew with the scope's history
// rather than with its own population (measured at 100,000 heads: a
// second metric of 100,000 facts added a megabyte of ids to a snapshot
// whose rows it did not change). It now records a `FactCensus`: the
// folded COUNT, a DIGEST (the sum of each id's 64-bit key), and a
// fixed-width invertible Bloom lookup table over the same keys — a count
// and a digest partitioned into cells, so that the DIFFERENCE between the
// snapshot's census and the log's can be decoded back into ids when it is
// small, which is what an incremental fold needs. Its size is fixed by
// `FactSurfaceOptions.MaxIncrementalFold`, never by history. A difference
// too large to decode is a rebuild, exactly as a difference larger than
// `MaxIncrementalFold` always was.
//
//   The one assumption the reconcile rests on: **two different sets of
//   fact ids never share a count and a digest.** A key is the first eight
//   bytes of the SHA-256 of the id, so a collision is a 2^-64 accident
//   rather than a shape any sequence of writes can produce, and a decoded
//   difference is re-checked against the log's count and digest before a
//   fold is trusted. This REPLACES the assumption the id list rested on —
//   that an out-of-band deletion was always paired with a
//   `FactSurface.drop` — because a deletion now changes the digest even
//   when an addition restores the count, and is rebuilt from the log.
//   `FactSurface.drop` stays the operator's cache flush.
//
// **The parsed snapshot is cached in-process (Phase 891)**, keyed by
// scope, metric and the census value the reconcile computes anyway, so a
// population question against an unchanged log parses nothing. A cache
// entry is only served when the log's census EQUALS the census the cached
// snapshot was converged against — a condition checked against the log on
// every read, never against the cache's own memory — so an entry can be
// missing or out of date but never wrong. A hit also probes that the
// snapshot blob still EXISTS (an existence check, not a download), so
// `FactSurface.drop` flushes every replica's cache along with the blob.
// The cache is bounded in entries and in bytes, evicts oldest first, and
// does not exist when the surface is disabled.
//
// **Historical reads bypass it (task 702.D).** A surface holds current
// heads, so it can answer "what is true now" and structurally cannot
// answer "what did we believe on the 3rd" — reconstructing a past head
// needs the superseded facts the surface does not carry. An `AsOf`
// population query therefore goes to enumeration: correct, slow, and rare.
//
// **Small deployments pay nothing (GP 13).** Below
// `FactSurfaceOptions.MinimumHeads` no surface is built or consulted and
// the read is byte-for-byte Phase 701's. `FactSurfaceOptions.disabled`
// restores the pre-702 behaviour exactly, including the blob layout.
//
// **Six portability rules (GP 12)**, audited for the seam below:
//  1. Identity by value — scope / metric / fact ids are strings, rows are
//     records; no live handles cross the seam.
//  2. Async at every boundary — every member returns `Async<_>`.
//  3. Failure as data — `Update` / `Rebuild` return `Result<_, string>`;
//     no `OnFailure` callback, and no exception escapes into `Assert`.
//  4. Stateless between calls — the snapshot's truth is the backing
//     store, read from and written to it exactly as before; the one thing
//     the implementation holds between calls is the Phase 891 parse cache,
//     and it holds nothing a call DEPENDS on. A hit requires the log's
//     census, taken on that very call, to equal the census the cached
//     snapshot was converged against, so a second replica, a restart or a
//     cold cache reads the blob exactly as it did before the cache existed
//     and reaches the same answer. Two replicas over one blob backend
//     converge because the reconcile is driven by the log, not by either
//     replica's memory.
//  5. No cross-shard ordering — a surface is scoped to one `scopeId` and
//     one metric; nothing is promised across either.
//  6. Precision at the lower bound — `AsOf` is carried at the tick
//     precision the fact was stamped with, and never re-derived.

/// One current head, projected into the surface. `Member` is the decidable
/// projection every population step reads; `Disclosure` is carried because
/// a fact's classification travels with it from birth (plan D14) and a
/// projection that dropped it could not be the basis of a disclosure-gated
/// read later — nothing in the ranking consults it.
type internal FactSurfaceRow = {
    Member: PopulationMember
    /// `Disclosure.toString` of the head's classification.
    Disclosure: string
}

/// The value a census reduces to for equality: how many fact ids, and the
/// sum of their keys. Two censuses with equal values hold the same ids —
/// the one assumption the reconcile rests on (see the header).
[<Struct>]
type internal FactCensusValue = { Count: int; Digest: uint64 }

/// A fixed-size census of a set of fact ids (Phase 891): the count, the
/// digest, and an invertible Bloom lookup table of `3 * Width` cells over
/// the same 64-bit keys. Adding an id touches one cell in each of three
/// sub-tables; subtracting one table from another cell by cell leaves a
/// table of the symmetric difference, which peels back into keys whenever
/// the difference is small against the width.
///
/// The arrays are never mutated once a census is built — every operation
/// below copies first — so a census is a value like the snapshot around it.
type internal FactCensus = {
    Count: int
    Digest: uint64
    /// Cells per sub-table.
    Width: int
    /// Per cell: the number of ids added.
    Counts: int[]
    /// Per cell: the XOR of the keys added.
    Keys: uint64[]
    /// Per cell: the XOR of each added key's check value.
    Checks: uint32[]
}

/// Building, comparing and differencing censuses.
module internal FactCensus =

    /// The narrowest sketch: the width a stale marker carries.
    [<Literal>]
    let MinimumWidth = 8

    /// The width that decodes a difference of `capacity` ids with high
    /// probability — one and a half cells per id across the three
    /// sub-tables, the usual margin over the peeling threshold. A failed
    /// decode is a rebuild, never a wrong answer, so the margin buys
    /// speed and nothing else.
    let widthFor (capacity: int) : int =
        max MinimumWidth ((max 0 capacity * 3 / 2 + 2) / 3)

    let private fmix64 (value: uint64) : uint64 =
        let mutable h = value
        h <- h ^^^ (h >>> 33)
        h <- h * 0xff51afd7ed558ccdUL
        h <- h ^^^ (h >>> 33)
        h <- h * 0xc4ceb9fe1a85ec53UL
        h ^^^ (h >>> 33)

    /// The 64-bit key of one fact id: the first eight bytes of its SHA-256.
    /// Cryptographic rather than fast on purpose — the digest's no-collision
    /// assumption is then an accident's probability, not an adversary's
    /// opportunity. Measured cost is in `docs/platform/facts.md`.
    type internal KeyHasher() =
        let mutable buffer: byte[] = Array.zeroCreate 128
        let hash: byte[] = Array.zeroCreate 32

        member _.Key(id: string) : uint64 =
            let needed = Encoding.UTF8.GetMaxByteCount id.Length

            if needed > buffer.Length then
                buffer <- Array.zeroCreate needed

            let written = Encoding.UTF8.GetBytes(id, 0, id.Length, buffer, 0)
            SHA256.HashData(ReadOnlySpan(buffer, 0, written), Span(hash)) |> ignore
            Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(hash, 0, 8))

    let private checkOf (key: uint64) : uint32 =
        uint32 (fmix64 (key ^^^ 0x9E3779B97F4A7C15UL) >>> 32)

    let private cellOf (width: int) (key: uint64) (table: int) : int =
        let spread = fmix64 (key + uint64 (table + 1) * 0xBF58476D1CE4E5B9UL)
        int (spread % uint64 width) + table * width

    /// An empty census of the given width.
    let empty (width: int) : FactCensus =
        let w = max MinimumWidth width

        {
            Count = 0
            Digest = 0UL
            Width = w
            Counts = Array.zeroCreate (3 * w)
            Keys = Array.zeroCreate (3 * w)
            Checks = Array.zeroCreate (3 * w)
        }

    let private addKey (counts: int[]) (keys: uint64[]) (checks: uint32[]) (width: int) (sign: int) (key: uint64) =
        let check = checkOf key

        for table in 0..2 do
            let i = cellOf width key table
            counts[i] <- counts[i] + sign
            keys[i] <- keys[i] ^^^ key
            checks[i] <- checks[i] ^^^ check

    /// `census` with `ids` added. Copies the table once, however many ids
    /// there are, so a batch fold pays for one table and not one per fact.
    let addAll (ids: string seq) (census: FactCensus) : FactCensus =
        let counts = Array.copy census.Counts
        let keys = Array.copy census.Keys
        let checks = Array.copy census.Checks
        let hasher = KeyHasher()
        let mutable count = census.Count
        let mutable digest = census.Digest

        for id in ids do
            let key = hasher.Key id
            addKey counts keys checks census.Width 1 key
            count <- count + 1
            digest <- digest + key

        {
            census with
                Count = count
                Digest = digest
                Counts = counts
                Keys = keys
                Checks = checks
        }

    /// The census of exactly `ids`, at `width`.
    let ofIds (width: int) (ids: string seq) : FactCensus = addAll ids (empty width)

    /// The value a census reduces to for equality.
    let valueOf (census: FactCensus) : FactCensusValue = {
        Count = census.Count
        Digest = census.Digest
    }

    /// The census value of the log's fact ids — what every surface read
    /// computes from the `List` it takes anyway. One key per id, no table.
    let valueOfIds (ids: string seq) : FactCensusValue =
        let hasher = KeyHasher()
        let mutable count = 0
        let mutable digest = 0UL

        for id in ids do
            count <- count + 1
            digest <- digest + hasher.Key id

        { Count = count; Digest = digest }

    /// Why a difference could not be used for an incremental fold. Every
    /// case is a rebuild; they are told apart only so the reason is legible.
    type internal Divergence =
        /// The difference is too large for this census's width to decode.
        | Undecodable
        /// The snapshot folded ids the log no longer holds — an out-of-band
        /// deletion (an erasure).
        | Departed of count: int
        /// Two ids in the log share a key; the digest cannot tell them
        /// apart, so no decoded answer about them is trusted.
        | KeyCollision

    /// The ids in `logIds` that `census` has not folded, decoded from the
    /// difference of the two tables. `Error` when that is not possible or
    /// not the whole story (see `Divergence`); the caller rebuilds.
    let unseen (census: FactCensus) (logIds: string seq) : Result<string list, Divergence> =
        let width = census.Width
        let counts = Array.zeroCreate (3 * width)
        let keys = Array.zeroCreate (3 * width)
        let checks = Array.zeroCreate (3 * width)
        let byKey = Dictionary<uint64, string>()
        let hasher = KeyHasher()
        let mutable collided = false

        for id in logIds do
            let key = hasher.Key id

            if byKey.TryAdd(key, id) then
                addKey counts keys checks width 1 key
            else
                collided <- true

        if collided then
            Error KeyCollision
        else
            // The log's table minus the snapshot's: +1 cells are ids only
            // the log holds, -1 cells ids only the snapshot folded.
            for i in 0 .. counts.Length - 1 do
                counts[i] <- counts[i] - census.Counts[i]
                keys[i] <- keys[i] ^^^ census.Keys[i]
                checks[i] <- checks[i] ^^^ census.Checks[i]

            let pending = Stack<int>(seq { 0 .. counts.Length - 1 })
            let added = ResizeArray<uint64>()
            let mutable departed = 0
            // A true peel removes one id for good, so a decodable table
            // needs at most one peel per cell. The budget only matters for
            // a table corrupted into a shape that could cycle.
            let mutable budget = 2 * counts.Length

            while pending.Count > 0 && budget > 0 do
                let i = pending.Pop()
                let sign = counts[i]

                if (sign = 1 || sign = -1) && checks[i] = checkOf keys[i] then
                    budget <- budget - 1
                    let key = keys[i]

                    if sign = 1 then added.Add key else departed <- departed + 1

                    addKey counts keys checks width (-sign) key

                    for table in 0..2 do
                        pending.Push(cellOf width key table)

            let clean =
                Array.forall ((=) 0) counts
                && Array.forall ((=) 0UL) keys
                && Array.forall ((=) 0u) checks

            if not clean then
                Error Undecodable
            elif departed > 0 then
                Error(Departed departed)
            else
                let ids = ResizeArray<string>(added.Count)
                let mutable resolved = true

                for key in added do
                    match byKey.TryGetValue key with
                    | true, id -> ids.Add id
                    | _ -> resolved <- false

                if resolved then Ok(List.ofSeq ids) else Error Undecodable

/// A derived snapshot of one (scope, metric)'s current heads.
type internal FactSurfaceSnapshot = {
    /// The metric id this surface projects. Carried in the payload as well
    /// as in the blob name so a snapshot read back under a colliding
    /// sanitised name is detected rather than trusted.
    Metric: string
    /// Set by a maintenance path that could not complete and could not
    /// delete the snapshot either — the explicit staleness marker. A stale
    /// snapshot is never read as an answer; it forces a rebuild.
    Stale: bool
    /// The current heads for `Metric`.
    Rows: FactSurfaceRow list
    /// Every fact id this snapshot has folded in — its rows, the heads
    /// they superseded, and every fact of every other metric in the scope
    /// — as a fixed-size census rather than a list (Phase 891). The
    /// reconcile compares it with the log's census to tell "converged"
    /// from "behind", and decodes the difference to learn which facts to
    /// fold.
    Census: FactCensus
}

/// How a store maintains and consults its metric surfaces.
type FactSurfaceOptions = {
    /// Whether surfaces are maintained and consulted at all. `false`
    /// reproduces the Phase 701 read path byte-for-byte and writes no
    /// surface blob.
    Enabled: bool
    /// The current-head count at or above which a population read builds
    /// and consults a surface. Below it the read enumerates, so a small
    /// deployment never pays for an index it cannot benefit from (GP 13),
    /// and its blob layout is unchanged.
    MinimumHeads: int
    /// The largest number of unseen facts a read will fold into an
    /// existing snapshot before giving up and rebuilding from the log.
    /// Folding costs one fact read each; past this many, one enumeration
    /// is cheaper than many point reads. It also sizes the census table a
    /// snapshot carries (Phase 891) — wide enough to decode a difference
    /// of this many ids — so it bounds the snapshot's census, not history.
    MaxIncrementalFold: int
}

/// Standard surface policies.
module FactSurfaceOptions =

    /// No surface: the Phase 701 enumeration, byte-for-byte, with no
    /// surface blob written and no probe on the assert path.
    let disabled: FactSurfaceOptions = {
        Enabled = false
        MinimumHeads = 0
        MaxIncrementalFold = 0
    }

    /// The default policy. Enabled, with a threshold generous enough that
    /// an ordinary deployment's blob layout and read path are unchanged:
    /// at Phase 701's measured 0.200 ms/head, 512 heads is an enumeration
    /// of about a tenth of a second, which is already interactive.
    ///
    /// **On GP 11.** A new feature defaults to prior behaviour, and this
    /// one defaults to prior *answers* — the two paths are held byte-equal
    /// by the shared decidable pipeline and by the whole population
    /// contract running against both — while changing prior *mechanism*
    /// above the threshold, where the prior mechanism does not work. A
    /// deployment that wants the letter as well as the spirit composes
    /// `disabled`.
    let defaults: FactSurfaceOptions = {
        Enabled = true
        MinimumHeads = 512
        MaxIncrementalFold = 4096
    }

    /// Enabled at every size — no fallback threshold. The shape the
    /// contract pack binds so the surface path is exercised by the same
    /// cases the enumerating path is, rather than only at a scale a test
    /// suite cannot reach.
    let always: FactSurfaceOptions = { defaults with MinimumHeads = 0 }

/// The surface's on-disk footprint, and the cache-flush operation over
/// it. A deployment never needs either — the store maintains and rebuilds
/// its own surfaces — but an operator draining, backing up, or erasing a
/// scope does.
module FactSurface =

    /// Blob-name prefix every surface in a scope lives under. Deliberately
    /// a sibling of `_facts/` rather than a child: the fact enumeration
    /// lists `_facts/` and must never see a derived artefact in its census.
    [<Literal>]
    let Prefix = "_factsurface/"

    let private safeSegment (metric: string) : string =
        let sb = StringBuilder(metric.Length)

        for c in metric do
            if Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = '.' then
                sb.Append c |> ignore
            else
                sb.Append '_' |> ignore

        let sanitised = sb.ToString()

        if sanitised.Length > 48 then
            sanitised.Substring(0, 48)
        else
            sanitised

    /// The blob a metric's surface occupies within a scope. A readable
    /// sanitised stem for the operator, plus a hash of the *unsanitised*
    /// id so two metrics that sanitise alike do not collide.
    let blobName (metric: string) : string =
        let digest =
            SHA256.HashData(Encoding.UTF8.GetBytes metric)
            |> Array.take 6
            |> Array.map (fun b -> b.ToString("x2", CultureInfo.InvariantCulture))
            |> String.concat ""

        sprintf "%s%s-%s.tsv" Prefix (safeSegment metric) digest

    /// Flush every surface in a scope. The next population read rebuilds
    /// whatever it needs from the fact log, so this is a cache flush and
    /// never data loss — the operation to run after an out-of-band write
    /// or an erasure that touched `_facts/`.
    let drop (storage: IBlobStorage) (scopeId: string) : Async<unit> = async {
        let! names = storage.List(scopeId, Prefix)

        for name in names do
            let! _ = storage.Delete(scopeId, name)
            ()
    }

// ─── Wire format ─────────────────────────────────────────────────────
//
// A flat, line-oriented table rather than JSON, and that is the whole
// point of the phase: the cost Phase 701 measured *is* per-fact JSON
// deserialisation, so a projection that reads back through the same
// serialiser would inherit the problem it exists to solve. The format is
// versioned in its header and the decoder refuses anything it does not
// recognise — a refusal reads as "no surface" and rebuilds, so a format
// change is self-healing across a rolling upgrade rather than breaking.

module internal FactSurfaceCodec =

    [<Literal>]
    let Magic = "toolup.factsurface"

    /// Version 2 (Phase 891) replaced the absorbed-id lines with the fixed
    /// census. A version-1 snapshot is refused like any other unreadable
    /// one — it reads as no surface and the next read rebuilds it.
    [<Literal>]
    let Version = 2

    /// Bytes per census cell on the wire: a 32-bit count, a 64-bit key sum
    /// and a 32-bit check sum, little-endian.
    [<Literal>]
    let CellBytes = 16

    /// The encoded size of a snapshot, estimated without encoding it: the
    /// weight the parse cache charges an entry. The row term is the one
    /// `encode` sizes its buffer by; the census term is exact.
    let estimatedBytes (snapshot: FactSurfaceSnapshot) : int64 =
        128L
        + int64 (List.length snapshot.Rows) * 160L
        + int64 (snapshot.Census.Counts.Length * CellBytes / 3 * 4)

    /// Escape the characters the table uses structurally. `>` joins subject
    /// path segments; the empty segment gets its own escape so a one-empty-
    /// segment path is distinguishable from an empty path.
    let private escape (s: string) : string =
        if String.IsNullOrEmpty s then
            s
        else
            let mutable needs = false

            for c in s do
                if c = '\\' || c = '\t' || c = '\n' || c = '\r' || c = '>' then
                    needs <- true

            if not needs then
                s
            else
                let sb = StringBuilder(s.Length + 8)

                for c in s do
                    match c with
                    | '\\' -> sb.Append "\\\\" |> ignore
                    | '\t' -> sb.Append "\\t" |> ignore
                    | '\n' -> sb.Append "\\n" |> ignore
                    | '\r' -> sb.Append "\\r" |> ignore
                    | '>' -> sb.Append "\\g" |> ignore
                    | other -> sb.Append other |> ignore

                sb.ToString()

    let private unescape (s: string) : string =
        if s.IndexOf('\\') < 0 then
            s
        else
            let sb = StringBuilder(s.Length)
            let mutable i = 0

            while i < s.Length do
                if s[i] = '\\' && i + 1 < s.Length then
                    match s[i + 1] with
                    | 't' -> sb.Append '\t' |> ignore
                    | 'n' -> sb.Append '\n' |> ignore
                    | 'r' -> sb.Append '\r' |> ignore
                    | 'g' -> sb.Append '>' |> ignore
                    | 'e' -> ()
                    | other -> sb.Append other |> ignore

                    i <- i + 2
                else
                    sb.Append s[i] |> ignore
                    i <- i + 1

            sb.ToString()

    let private encodePath (path: string list) : string =
        path
        |> List.map (fun seg -> if seg = "" then "\\e" else escape seg)
        |> String.concat ">"

    let private decodePath (s: string) : string list =
        if s = "" then
            []
        else
            s.Split '>' |> Array.map unescape |> Array.toList

    let private kindChar (kind: DateTimeKind) =
        match kind with
        | DateTimeKind.Utc -> 'U'
        | DateTimeKind.Local -> 'L'
        | _ -> 'N'

    let private kindOf (c: char) =
        match c with
        | 'U' -> DateTimeKind.Utc
        | 'L' -> DateTimeKind.Local
        | _ -> DateTimeKind.Unspecified

    let private appendInstant (sb: StringBuilder) (d: DateTime) =
        sb.Append(d.Ticks.ToString CultureInfo.InvariantCulture).Append(kindChar d.Kind)
        |> ignore

    let private encodeCells (census: FactCensus) : string =
        let bytes = Array.zeroCreate<byte> (census.Counts.Length * CellBytes)

        for i in 0 .. census.Counts.Length - 1 do
            let at = i * CellBytes
            Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(Span(bytes, at, 4), census.Counts[i])
            Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(Span(bytes, at + 4, 8), census.Keys[i])
            Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, at + 12, 4), census.Checks[i])

        Convert.ToBase64String bytes

    let private decodeCells (count: int) (digest: uint64) (width: int) (text: string) : FactCensus option =
        let bytes = Convert.FromBase64String text

        if width < 1 || bytes.Length <> 3 * width * CellBytes then
            None
        else
            let cells = 3 * width
            let counts = Array.zeroCreate cells
            let keys = Array.zeroCreate cells
            let checks = Array.zeroCreate cells

            for i in 0 .. cells - 1 do
                let at = i * CellBytes
                counts[i] <- Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(bytes, at, 4))
                keys[i] <- Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, at + 4, 8))
                checks[i] <- Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(bytes, at + 12, 4))

            Some {
                Count = count
                Digest = digest
                Width = width
                Counts = counts
                Keys = keys
                Checks = checks
            }

    /// Render a snapshot. One header line, then one line per row, then one
    /// line holding the census table — a shape a reader can bound before
    /// parsing. Every census field is fixed-width, so a snapshot's size
    /// depends on its own rows and on nothing else in the scope.
    let encode (snapshot: FactSurfaceSnapshot) : byte[] =
        let rowCount = List.length snapshot.Rows
        let census = snapshot.Census

        let sb =
            StringBuilder(128 + rowCount * 160 + census.Counts.Length * CellBytes / 3 * 4 + 4)

        sb
            .Append(Magic)
            .Append('\t')
            .Append(Version)
            .Append('\t')
            .Append(escape snapshot.Metric)
            .Append('\t')
            .Append(rowCount)
            .Append('\t')
            .Append(if snapshot.Stale then '1' else '0')
            .Append('\t')
            .Append(census.Count.ToString("x8", CultureInfo.InvariantCulture))
            .Append('\t')
            .Append(census.Digest.ToString("x16", CultureInfo.InvariantCulture))
            .Append('\t')
            .Append(census.Width.ToString("x8", CultureInfo.InvariantCulture))
            .Append('\n')
        |> ignore

        for row in snapshot.Rows do
            let m = row.Member

            sb.Append(m.FactId).Append('\t').Append(escape m.Subject.Hierarchy).Append('\t')
            |> ignore

            sb.Append(encodePath m.Subject.Path).Append('\t') |> ignore

            match m.Magnitude with
            | Some d -> sb.Append(d.ToString CultureInfo.InvariantCulture) |> ignore
            | None -> ()

            sb.Append('\t') |> ignore
            appendInstant sb m.PeriodFrom
            sb.Append('\t') |> ignore
            appendInstant sb m.PeriodTo
            sb.Append('\t') |> ignore
            appendInstant sb m.AsOf

            sb.Append('\t').Append(escape m.MethodIdentity).Append('\t').Append(escape row.Disclosure).Append('\n')
            |> ignore

        sb.Append(encodeCells census).Append('\n') |> ignore

        Encoding.UTF8.GetBytes(sb.ToString())

    /// Parse a snapshot, or `None` for anything this build cannot read.
    ///
    /// Deliberately index-scanned rather than `Split`-ed: a 300,000-row
    /// surface is tens of megabytes, and splitting it into lines and then
    /// into fields allocates several strings per row before a single one is
    /// needed. Only the five genuinely-textual fields are materialised; the
    /// numbers and instants are parsed straight off the source span.
    let decode (bytes: byte[]) : FactSurfaceSnapshot option =
        try
            let s = Encoding.UTF8.GetString bytes
            let len = s.Length
            let mutable pos = 0

            // Field boundaries of the current line, as (start, length)
            // pairs — nine fields on a row line, eight on the header.
            let bounds = Array.zeroCreate<struct (int * int)> 16

            /// Split the next line into `bounds`; returns the field count,
            /// or -1 at end of input.
            let nextLine () =
                if pos >= len then
                    -1
                else
                    let newline = s.IndexOf('\n', pos)
                    let stop = if newline < 0 then len else newline
                    let mutable fieldStart = pos
                    let mutable count = 0
                    let mutable i = pos

                    while i <= stop do
                        if i = stop || s[i] = '\t' then
                            if count < bounds.Length then
                                bounds[count] <- struct (fieldStart, i - fieldStart)

                            count <- count + 1
                            fieldStart <- i + 1

                        i <- i + 1

                    pos <- if newline < 0 then len else newline + 1
                    count

            let text (index: int) =
                let struct (start, length) = bounds[index]
                s.Substring(start, length)

            let textUnescaped (index: int) = unescape (text index)

            let int32Field (index: int) =
                let struct (start, length) = bounds[index]
                Int32.Parse(s.AsSpan(start, length), NumberStyles.Integer, CultureInfo.InvariantCulture)

            let instant (index: int) =
                let struct (start, length) = bounds[index]

                let ticks =
                    Int64.Parse(s.AsSpan(start, length - 1), NumberStyles.Integer, CultureInfo.InvariantCulture)

                DateTime(ticks, kindOf s[start + length - 1])

            let magnitude (index: int) =
                let struct (start, length) = bounds[index]

                if length = 0 then
                    None
                else
                    Some(Decimal.Parse(s.AsSpan(start, length), NumberStyles.Number, CultureInfo.InvariantCulture))

            let hexField (index: int) =
                let struct (start, length) = bounds[index]
                UInt64.Parse(s.AsSpan(start, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)

            if nextLine () <> 8 || text 0 <> Magic || int32Field 1 <> Version then
                None
            else
                let metric = textUnescaped 2
                let rowCount = int32Field 3
                let stale = text 4 = "1"
                let censusCount = int (hexField 5)
                let censusDigest = hexField 6
                let censusWidth = int (hexField 7)

                let rows = ResizeArray<FactSurfaceRow> rowCount
                let mutable ok = true

                for _ in 1..rowCount do
                    if ok then
                        if nextLine () <> 9 then
                            ok <- false
                        else
                            rows.Add {
                                Member = {
                                    FactId = text 0
                                    Subject = {
                                        Hierarchy = textUnescaped 1
                                        Path = decodePath (text 2)
                                    }
                                    Magnitude = magnitude 3
                                    PeriodFrom = instant 4
                                    PeriodTo = instant 5
                                    AsOf = instant 6
                                    MethodIdentity = textUnescaped 7
                                }
                                Disclosure = textUnescaped 8
                            }

                let census =
                    if ok && nextLine () = 1 then
                        decodeCells censusCount censusDigest censusWidth (text 0)
                    else
                        None

                match census with
                | Some census when ok ->
                    Some {
                        Metric = metric
                        Stale = stale
                        Rows = List.ofSeq rows
                        Census = census
                    }
                | _ -> None
        with _ ->
            None

/// The pure fold of one fact into a snapshot. Shared by the assert-time
/// maintenance path (one fact, persisted immediately) and the read-time
/// reconcile (a batch, persisted once) so the two can never disagree
/// about what absorbing a fact means.
module internal FactSurfaceFold =

    let rowOf (f: Fact) : FactSurfaceRow = {
        Member = PopulationMember.ofFact f
        Disclosure = Disclosure.toString f.Disclosure
    }

    /// Absorb `facts`, in order, into `snapshot`, which projects `metric`,
    /// and report how many rows the fold visited (Phase 891).
    ///
    /// Exactly one fact id joins the census per fact, whichever branch
    /// runs — that is the invariant the census reconcile depends on. A
    /// superseded head leaves the rows but not the census: it is still a
    /// blob in the log, so it must still be counted.
    ///
    /// A fact of another metric is absorbed without becoming a row (a
    /// scope's facts share one blob prefix, so a surface must be able to
    /// account for its neighbours' facts). A fact that supersedes a head
    /// replaces that head's row; a fact under a different method for the
    /// same subject supersedes nothing and simply adds a second row —
    /// competition is surfaced, never merged (D19).
    ///
    /// **Linear in the batch.** A supersession is a keyed removal — the
    /// retired id is recorded, and the snapshot's own rows are walked ONCE
    /// at the end — where folding fact by fact walked every row for every
    /// supersession: measured at 8.2 ms per superseding fact over 100,000
    /// rows before Phase 891, so a refresh restating 100,000 subjects was
    /// some fourteen minutes of list filtering. The result is exactly the
    /// fact-by-fact fold's, row order included, which the test pack holds.
    ///
    /// **Batch callers fold in `AsOf` order.** Supersession strictly
    /// increases `AsOf` within a lineage (law L3), so ascending `AsOf` is a
    /// topological order over the edges; folding a successor before its
    /// predecessor would leave the predecessor as a row nothing ever
    /// retires.
    let applyFacts (metric: string) (facts: Fact list) (snapshot: FactSurfaceSnapshot) : FactSurfaceSnapshot * int =
        // Ids retired from the snapshot's ORIGINAL rows. A removal reaches
        // an original row whenever it happens, because that row existed
        // before the whole batch.
        let retired = HashSet<string>(StringComparer.Ordinal)
        // Rows the batch adds, in the order it adds them, and which are
        // still live. A removal reaches only the added rows that exist at
        // that moment — the fact-by-fact fold's semantics, kept exactly.
        let added = ResizeArray<FactSurfaceRow>()
        let live = ResizeArray<bool>()
        let addedAt = Dictionary<string, ResizeArray<int>>(StringComparer.Ordinal)
        let mutable visits = 0

        for fact in facts do
            match fact.Supersedes with
            | Some sid ->
                retired.Add sid |> ignore

                match addedAt.TryGetValue sid with
                | true, positions ->
                    for p in positions do
                        live[p] <- false
                        visits <- visits + 1

                    positions.Clear()
                | _ -> ()
            | None -> ()

            if fact.Metric.Value = metric then
                let row = rowOf fact

                match addedAt.TryGetValue row.Member.FactId with
                | true, positions -> positions.Add added.Count
                | _ -> addedAt[row.Member.FactId] <- ResizeArray [ added.Count ]

                added.Add row
                live.Add true

        let original =
            if retired.Count = 0 then
                snapshot.Rows
            else
                visits <- visits + List.length snapshot.Rows

                snapshot.Rows |> List.filter (fun r -> not (retired.Contains r.Member.FactId))

        // `applyFact` prepends, so the latest addition leads.
        let mutable rows = original

        for i in 0 .. added.Count - 1 do
            visits <- visits + 1

            if live[i] then
                rows <- added[i] :: rows

        {
            snapshot with
                Rows = rows
                Census = FactCensus.addAll (facts |> Seq.map _.FactId) snapshot.Census
        },
        visits

    /// Absorb one `fact` into `snapshot` — `applyFacts` over a batch of
    /// one, so the assert-time path and the batch paths share one
    /// definition of what absorbing a fact means.
    let applyFact (metric: string) (fact: Fact) (snapshot: FactSurfaceSnapshot) : FactSurfaceSnapshot =
        applyFacts metric [ fact ] snapshot |> fst

// ─── The seam ────────────────────────────────────────────────────────

/// Maintenance and retrieval of one scope's metric surfaces. Internal by
/// construction: the surface is an implementation choice of a store, not a
/// contract a consumer composes against. `IFactStore` is the contract, and
/// a store that indexes differently — or not at all — is still a
/// conforming implementation.
type internal IFactSurface =
    /// The snapshot for `(scopeId, metric)`, or `None` when there is none,
    /// it is unreadable, or it belongs to a different metric.
    abstract Get: scopeId: string * metric: string -> Async<FactSurfaceSnapshot option>

    /// Fold one newly-asserted fact into the existing snapshot, if there
    /// is one, and return what was written. A fact of another metric joins
    /// the census without becoming a row; a fact that supersedes a head
    /// replaces that head's row; a competing method adds a second row under
    /// the same subject. `Ok None` — no snapshot present — is a no-op, not a
    /// failure: a surface that has not been built yet has nothing to
    /// maintain.
    abstract Update: scopeId: string * metric: string * fact: Fact -> Async<Result<FactSurfaceSnapshot option, string>>

    /// Persist a snapshot the caller has already folded — the read-time
    /// reconcile's write, which absorbs a batch and pays one round trip.
    abstract Put: scopeId: string * metric: string * snapshot: FactSurfaceSnapshot -> Async<Result<unit, string>>

    /// Replace the snapshot wholesale from the log: `heads` are the
    /// metric's current heads, `foldedIds` EVERY fact id the rebuild read —
    /// the heads included — which becomes the snapshot's census.
    abstract Rebuild:
        scopeId: string * metric: string * heads: Fact list * foldedIds: string list ->
            Async<Result<FactSurfaceSnapshot, string>>

    /// Flush the snapshot. Best effort, and safe by definition — the next
    /// read rebuilds.
    abstract Drop: scopeId: string * metric: string -> Async<unit>

    /// Mark the snapshot stale in place. The fallback for a maintenance
    /// path that failed and could not `Drop` either: a stale snapshot is
    /// never read as an answer, so the marker and the deletion mean the
    /// same thing to a reader and the failure has two independent ways to
    /// be recorded rather than one.
    abstract MarkStale: scopeId: string * metric: string -> Async<unit>

/// The blob-backed surface: one snapshot blob per (scope, metric) beside
/// the facts it projects. `censusWidth` sizes the census of every snapshot
/// this instance BUILDS; a snapshot read back keeps the width it was built
/// with, so replicas configured differently still read each other's.
type internal BlobFactSurface(storage: IBlobStorage, censusWidth: int) =

    let put (scopeId: string) (metric: string) (snapshot: FactSurfaceSnapshot) : Async<Result<unit, string>> = async {
        let! r = storage.Upload(scopeId, FactSurface.blobName metric, FactSurfaceCodec.encode snapshot)

        return
            match r with
            | Ok _ -> Ok()
            | Error e -> Error e
    }

    let get (scopeId: string) (metric: string) : Async<FactSurfaceSnapshot option> = async {
        let! r = storage.Download(scopeId, FactSurface.blobName metric)

        return
            match r with
            | Error _ -> None
            | Ok bytes ->
                match FactSurfaceCodec.decode bytes with
                // A snapshot naming a different metric is a sanitised-name
                // collision the hash was supposed to prevent. Refusing
                // beats answering from the wrong population.
                | Some snapshot when snapshot.Metric = metric -> Some snapshot
                | _ -> None
    }

    interface IFactSurface with

        member _.Get(scopeId: string, metric: string) : Async<FactSurfaceSnapshot option> = get scopeId metric

        member _.Update
            (scopeId: string, metric: string, fact: Fact)
            : Async<Result<FactSurfaceSnapshot option, string>> =
            async {
                let! existing = get scopeId metric

                match existing with
                | None -> return Ok None
                | Some snapshot ->
                    let folded = FactSurfaceFold.applyFact metric fact snapshot
                    let! r = put scopeId metric folded

                    return
                        match r with
                        | Ok() -> Ok(Some folded)
                        | Error e -> Error e
            }

        member _.Put(scopeId: string, metric: string, snapshot: FactSurfaceSnapshot) : Async<Result<unit, string>> =
            put scopeId metric snapshot

        member _.Rebuild
            (scopeId: string, metric: string, heads: Fact list, foldedIds: string list)
            : Async<Result<FactSurfaceSnapshot, string>> =
            async {
                let snapshot = {
                    Metric = metric
                    Stale = false
                    Rows = heads |> List.map FactSurfaceFold.rowOf
                    Census = FactCensus.ofIds censusWidth foldedIds
                }

                let! r = put scopeId metric snapshot

                return
                    match r with
                    | Ok() -> Ok snapshot
                    | Error e -> Error e
            }

        member _.Drop(scopeId: string, metric: string) : Async<unit> = async {
            let! _ = storage.Delete(scopeId, FactSurface.blobName metric)
            return ()
        }

        member _.MarkStale(scopeId: string, metric: string) : Async<unit> = async {
            // Deliberately does NOT read the existing snapshot first: this
            // runs after something already failed, and a zero-row stale
            // marker forces the same rebuild a mangled one would. Small,
            // and independent of whatever went wrong.
            let! _ =
                put scopeId metric {
                    Metric = metric
                    Stale = true
                    Rows = []
                    Census = FactCensus.empty FactCensus.MinimumWidth
                }

            return ()
        }

/// Shared helpers over a snapshot: the decidable pipeline run over rows
/// instead of facts. (The census reconcile's arithmetic is `FactCensus`.)
module internal FactSurfaceRead =

    /// The rows a query's subject / period clauses admit, as members.
    let matching (query: PopulationQuery) (snapshot: FactSurfaceSnapshot) : PopulationMember list =
        snapshot.Rows
        |> List.choose (fun row ->
            let m = row.Member

            let admits =
                PopulationQuery.matchesSubject query m.Subject
                && (query.PeriodOverlaps
                    |> Option.forall (fun p -> p.From < m.PeriodTo && m.PeriodFrom < p.To))

            if admits then Some m else None)

// ─── The parse cache (Phase 891) ─────────────────────────────────────
//
// A population question used to download and parse the whole snapshot —
// tens of megabytes at the population tier's cardinality — every time,
// and concurrent questions were concurrent downloads and parses. The
// cache keeps the PARSED snapshot per (scope, metric), tagged with the
// census value it was converged against, and serves it only to a read
// whose own census — taken from the log on that read — is equal. So it is
// an optimisation over the stateless seam, never a second source of
// truth: an entry that is out of date simply does not match, and the read
// goes to the blob exactly as it would with no cache at all.

/// A bounded, oldest-first cache of converged snapshots, one entry per
/// (scope, metric). Bounded in entries and in estimated encoded bytes; an
/// entry heavier than the whole byte budget is never admitted. Safe for
/// concurrent use.
type internal FactSurfaceCache(maxEntries: int, maxBytes: int64) =

    let gate = obj ()

    let entries =
        Dictionary<
            struct (string * string),
            struct (FactCensusValue * FactSurfaceSnapshot * int64 * LinkedListNode<struct (string * string)>)
         >()

    // Insertion order, oldest at the head: the eviction order.
    let order = LinkedList<struct (string * string)>()
    let mutable bytes = 0L

    let remove (key: struct (string * string)) =
        match entries.TryGetValue key with
        | true, struct (_, _, weight, node) ->
            entries.Remove key |> ignore
            order.Remove node
            bytes <- bytes - weight
        | _ -> ()

    /// The default bounds: sixteen (scope, metric) pairs and 64 MiB of
    /// encoded snapshot — room for one population at the tier's stated
    /// 300,000 subjects, or many smaller ones.
    static member DefaultMaxEntries = 16

    /// See `DefaultMaxEntries`.
    static member DefaultMaxBytes = 64L * 1024L * 1024L

    /// The cache every surface-enabled `BlobFactStore` holds.
    static member CreateDefault() =
        FactSurfaceCache(FactSurfaceCache.DefaultMaxEntries, FactSurfaceCache.DefaultMaxBytes)

    /// The cached snapshot for `(scopeId, metric)` if, and only if, it was
    /// converged against exactly `census`. An entry that does not match is
    /// dropped: it can only be out of date, and it is holding memory.
    member _.TryGet(scopeId: string, metric: string, census: FactCensusValue) : FactSurfaceSnapshot option =
        lock gate (fun () ->
            let key = struct (scopeId, metric)

            match entries.TryGetValue key with
            | true, struct (cachedCensus, snapshot, _, _) when cachedCensus = census -> Some snapshot
            | true, _ ->
                remove key
                None
            | _ -> None)

    /// Remember `snapshot` for `(scopeId, metric)`, keyed by its own
    /// census, replacing any earlier entry for the pair. A stale snapshot
    /// is never cached — it is not an answer — and clears the pair.
    member _.Store(scopeId: string, metric: string, snapshot: FactSurfaceSnapshot) : unit =
        lock gate (fun () ->
            let key = struct (scopeId, metric)
            remove key
            let weight = FactSurfaceCodec.estimatedBytes snapshot

            if not snapshot.Stale && maxEntries > 0 && weight <= maxBytes then
                let node = order.AddLast key
                entries[key] <- struct (FactCensus.valueOf snapshot.Census, snapshot, weight, node)
                bytes <- bytes + weight

                while entries.Count > maxEntries || bytes > maxBytes do
                    remove order.First.Value)

    /// Forget `(scopeId, metric)` — the read path's response to a snapshot
    /// blob that has been dropped.
    member _.Evict(scopeId: string, metric: string) : unit =
        lock gate (fun () -> remove (struct (scopeId, metric)))

    /// Entries currently held.
    member _.Count = lock gate (fun () -> entries.Count)

    /// Estimated encoded bytes currently held.
    member _.Bytes = lock gate (fun () -> bytes)