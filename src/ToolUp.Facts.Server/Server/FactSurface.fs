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
// census of every fact that exists — and a snapshot records how many fact
// ids it has folded in. A count that disagrees means something reached the
// log without reaching the surface (a failed update, a second replica, a
// restore), and the read reconciles before answering: it folds in the few
// it is missing, or rebuilds outright. So the failure mode of every
// maintenance path is a slower read, never a different answer. Note the
// census is the *same* `List` call the enumeration path already makes
// first, so the check is free relative to the path it replaces.
//
//   The one shape this cannot see: a fact blob *deleted* out of band
//   while another is added, leaving the count intact. Nothing in the store
//   deletes facts (append-only), but `IBlobStorage.Erase` under a GDPR
//   erasure could — so an erasure that touches `_facts/` must be followed
//   by `FactSurface.drop`. Stated here because it is the single assumption
//   the reconcile rests on.
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
//  4. Stateless between calls — the snapshot is read from and written to
//     the backing store on every call; the implementation holds nothing.
//     Two replicas over one blob backend converge because the reconcile is
//     driven by the log, not by either replica's memory.
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
    /// Fact ids this snapshot has folded in that are **not** rows: heads
    /// that have since been superseded, and facts belonging to other
    /// metrics in the same scope. Kept so the census reconcile can tell
    /// "already seen, not a head of mine" from "never seen" — the two
    /// answers the count check depends on.
    Absorbed: string list
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
    /// is cheaper than many point reads.
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

    [<Literal>]
    let Version = 1

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

    /// Render a snapshot. One header line, then one line per row, then one
    /// line per absorbed id — a shape a reader can bound before parsing.
    let encode (snapshot: FactSurfaceSnapshot) : byte[] =
        let rowCount = List.length snapshot.Rows
        let absorbedCount = List.length snapshot.Absorbed
        let sb = StringBuilder(128 + rowCount * 160 + absorbedCount * 68)

        sb
            .Append(Magic)
            .Append('\t')
            .Append(Version)
            .Append('\t')
            .Append(escape snapshot.Metric)
            .Append('\t')
            .Append(rowCount)
            .Append('\t')
            .Append(absorbedCount)
            .Append('\t')
            .Append(if snapshot.Stale then '1' else '0')
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

        for id in snapshot.Absorbed do
            sb.Append(id).Append('\n') |> ignore

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
            // pairs — nine fields on a row line, six on the header.
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

            if nextLine () <> 6 || text 0 <> Magic || int32Field 1 <> Version then
                None
            else
                let metric = textUnescaped 2
                let rowCount = int32Field 3
                let absorbedCount = int32Field 4
                let stale = text 5 = "1"

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

                let absorbed = ResizeArray<string> absorbedCount

                for _ in 1..absorbedCount do
                    if ok then
                        if nextLine () <> 1 then
                            ok <- false
                        else
                            absorbed.Add(text 0)

                if not ok then
                    None
                else
                    Some {
                        Metric = metric
                        Stale = stale
                        Rows = List.ofSeq rows
                        Absorbed = List.ofSeq absorbed
                    }
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

    /// Absorb `fact` into `snapshot`, which projects `metric`.
    ///
    /// Exactly one fact id joins the folded set per call, whichever branch
    /// runs — that is the invariant the census reconcile depends on, and
    /// the reason the superseded head is *moved* to `Absorbed` rather than
    /// dropped: it is still a blob in the log, so it must still be counted.
    ///
    /// A fact of another metric is absorbed without becoming a row (a
    /// scope's facts share one blob prefix, so a surface must be able to
    /// account for its neighbours' facts). A fact that supersedes a head
    /// replaces that head's row; a fact under a different method for the
    /// same subject supersedes nothing and simply adds a second row —
    /// competition is surfaced, never merged (D19).
    ///
    /// **Batch callers fold in `AsOf` order.** Supersession strictly
    /// increases `AsOf` within a lineage (law L3), so ascending `AsOf` is a
    /// topological order over the edges; folding a successor before its
    /// predecessor would leave the predecessor as a row nothing ever
    /// retires.
    let applyFact (metric: string) (fact: Fact) (snapshot: FactSurfaceSnapshot) : FactSurfaceSnapshot =
        let retained =
            match fact.Supersedes with
            | None -> snapshot.Rows
            | Some sid -> snapshot.Rows |> List.filter (fun r -> r.Member.FactId <> sid)

        let retiredIds =
            match fact.Supersedes with
            | Some sid when List.length retained <> List.length snapshot.Rows -> [ sid ]
            | _ -> []

        let belongs = fact.Metric.Value = metric

        {
            snapshot with
                Rows = if belongs then rowOf fact :: retained else retained
                Absorbed =
                    if belongs then
                        retiredIds @ snapshot.Absorbed
                    else
                        (fact.FactId :: retiredIds) @ snapshot.Absorbed
        }

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
    /// is one. A fact of another metric is absorbed (its id is recorded)
    /// without becoming a row; a fact that supersedes a head replaces that
    /// head's row; a competing method adds a second row under the same
    /// subject. `Ok` with no snapshot present is a no-op, not a failure —
    /// a surface that has not been built yet has nothing to maintain.
    abstract Update: scopeId: string * metric: string * fact: Fact -> Async<Result<unit, string>>

    /// Persist a snapshot the caller has already folded — the read-time
    /// reconcile's write, which absorbs a batch and pays one round trip.
    abstract Put: scopeId: string * metric: string * snapshot: FactSurfaceSnapshot -> Async<Result<unit, string>>

    /// Replace the snapshot wholesale from the log: `heads` are the
    /// metric's current heads, `absorbedIds` every other fact id in scope.
    abstract Rebuild:
        scopeId: string * metric: string * heads: Fact list * absorbedIds: string list ->
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
/// the facts it projects.
type internal BlobFactSurface(storage: IBlobStorage) =

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

        member _.Update(scopeId: string, metric: string, fact: Fact) : Async<Result<unit, string>> = async {
            let! existing = get scopeId metric

            match existing with
            | None -> return Ok()
            | Some snapshot -> return! put scopeId metric (FactSurfaceFold.applyFact metric fact snapshot)
        }

        member _.Put(scopeId: string, metric: string, snapshot: FactSurfaceSnapshot) : Async<Result<unit, string>> =
            put scopeId metric snapshot

        member _.Rebuild
            (scopeId: string, metric: string, heads: Fact list, absorbedIds: string list)
            : Async<Result<FactSurfaceSnapshot, string>> =
            async {
                let snapshot = {
                    Metric = metric
                    Stale = false
                    Rows = heads |> List.map FactSurfaceFold.rowOf
                    Absorbed = absorbedIds
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
                    Absorbed = []
                }

            return ()
        }

/// Shared helpers over a snapshot: the census reconcile, and the decidable
/// pipeline run over rows instead of facts.
module internal FactSurfaceRead =

    /// Every fact id a snapshot has folded in — rows plus absorbed. The
    /// store is append-only and one blob per fact, so this set is always a
    /// SUBSET of the scope's fact ids; that is what makes a bare count
    /// comparison sound, and why it is stated here rather than assumed.
    let foldedCount (snapshot: FactSurfaceSnapshot) : int =
        List.length snapshot.Rows + List.length snapshot.Absorbed

    /// The fact ids a snapshot has never seen, given the scope's census.
    let unseen (snapshot: FactSurfaceSnapshot) (storeIds: string list) : string list =
        let known = HashSet<string>(StringComparer.Ordinal)

        for row in snapshot.Rows do
            known.Add row.Member.FactId |> ignore

        for id in snapshot.Absorbed do
            known.Add id |> ignore

        storeIds |> List.filter (known.Contains >> not)

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