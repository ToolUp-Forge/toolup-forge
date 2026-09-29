// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── BlobFactStore (Phase 520) ───────────────────────────────────────
//
// The default `IFactStore` — each fact is one immutable JSON blob at
// `<scope>/_facts/{factId}.json`. Append-only and stateless between
// calls (GP 12 rule 4): every read recomputes from the blobs, so the
// store holds nothing in memory across invocations and is
// **distributed-ready** (multiple replicas over one shared blob backend
// converge, because the content-addressed id makes writes idempotent).
//
// Below the point-read index threshold (Phase 890) every read scans the
// blobs under the scope's `_facts/` prefix (O(n) downloads per query /
// assert), which is fine for a small scope. At or above it, a point read,
// a lineage-head lookup and a supersession-chain walk read the facts they
// concern and no others — see "The point-read index" below. A large
// deployment can still swap in an indexed implementation behind the same
// `IFactStore` contract (the six-rule audit is what makes that swap
// safe). Scope isolation is structural (GP 4): the container IS the
// resolved storage scope, so one scope's facts are unreachable from
// another.
//
// **What still enumerates, by definition.** A query that names neither a
// subject nor a metric — `FactQuery.all`, and the full-history listing
// built on it — is the whole-store walk: its answer is every fact, so no
// index can make it read fewer. A query naming only one of the two
// enumerates as well; the population read (a metric across subjects) is
// the metric surface's job (Phase 702), not this index's.
//
// **Fan-out.** Every parallel blob fan-out in this file runs under
// `BlobFanOut.Bound` concurrent requests — a download of a whole scope
// must never open ten thousand requests at once against an object store.
//
// **Audit (GP 6)** rides `IEventStore` under the reserved `_facts` source
// module (the `ILineageStore` pattern) — a durable, scope-isolated,
// queryable record without a core `AuditEvent` edit.

/// What the store decided about one draft, inside the shared write core
/// (Phase 704). `Written` carries the fact for a draft that was stored —
/// the audit and surface-maintenance steps read it — and is `None` for an
/// idempotent skip, which by definition produced no new fact.
type internal DraftDisposition = {
    Outcome: BatchAssertOutcome
    FactId: string
    Written: Fact option
}

/// The concurrency bound every parallel blob fan-out in `BlobFactStore`
/// runs under (Phase 890). Sixteen is well inside the per-client
/// connection budget of every shipped storage backend, and far above the
/// point where an in-memory or local backend stops getting faster.
module internal BlobFanOut =

    [<Literal>]
    let Bound = 16

    /// `Async.Parallel` under `Bound`. Result order is input order, exactly
    /// as the unbounded form's is.
    let run (computations: Async<'T> seq) : Async<'T array> = Async.Parallel(computations, Bound)

// ─── The point-read index (Phase 890) ────────────────────────────────
//
// One empty `.ref` leaf per fact, in the Phase 9f blob-index layout
// (`{prefix}/{key}/{value}.ref`), nested one level deeper so that ONE leaf
// set carries both indexes the point reads need:
//
//     _factindex/{subject-metric}/{lineage}/{asOf}_{factId}.ref
//
//   - **by subject and metric** — every lineage, and so every fact, of one
//     (subject, metric) is under `_factindex/{subject-metric}/`;
//   - **by lineage** — every fact of one lineage (subject, metric, period,
//     method identity) is under `…/{subject-metric}/{lineage}/`.
//
// One leaf per fact rather than one per index is deliberate: two leaves
// written by two uploads can land half-written, and a lineage entry whose
// subject-and-metric entry is missing would answer a point read with a
// fact short. One upload is either there or not.
//
// **The census decides whether the index may answer.** Every consulting
// read lists `_facts/` (the census the enumeration takes anyway) and the
// leaf set, and uses the index only when every fact in the census has a
// leaf. Anything else — a failed leaf write, a fact written behind the
// store's back, a scope that has just crossed the threshold, an index an
// operator deleted — reads as "incomplete", and that read enumerates
// (the truth, always) and writes the missing leaves from the facts it
// just read. So a failed index write costs one enumeration and never a
// different answer, and the next read is indexed again. A leaf whose fact
// is gone (an erasure) is ignored, because only census members are read.
//
// **Leaves narrow; the pipeline decides.** A leaf admits a SUPERSET of
// what the enumeration would keep, and the facts it admits then run
// through the exact filters and the exact lineage key the enumeration
// uses, in census order — so the two paths agree by construction, not by
// a second reading of the rules. Where the leaf name can be exact it is:
// a `Utc` or `Unspecified` period endpoint round-trips through the store's
// serialiser with its ticks intact and is compared exactly. A `Local`
// endpoint is stored as its UTC instant and compared within
// `LocalTolerance`, which bounds every UTC offset there is — a `Local`
// value's read-back ticks depend on the reading machine's zone.
//
// The index is DERIVED (GP 5): the fact blobs are the truth, the leaves
// hold no fact data, and deleting every leaf loses nothing.

/// How a `BlobFactStore` maintains and consults its point-read index
/// (Phase 890) — the by-lineage and by-subject-and-metric index that lets
/// a point read, a lineage-head lookup and a supersession-chain walk read
/// the facts they concern and no others.
type FactIndexOptions = {
    /// Whether the index is maintained and consulted at all. `false`
    /// reproduces the pre-890 read and write paths byte-for-byte and
    /// writes no index blob.
    Enabled: bool
    /// The scope's fact count at or above which the index is written and
    /// consulted. Below it every read enumerates, exactly as before, and
    /// no index blob is written — so a small scope's blob layout is
    /// unchanged (GP 13).
    MinimumFacts: int
}

/// Standard point-read index policies.
module FactIndexOptions =

    /// No index: the pre-890 enumeration, byte-for-byte, with no index
    /// blob written and no index listing on any path.
    let disabled: FactIndexOptions = { Enabled = false; MinimumFacts = 0 }

    /// The default policy. Enabled at 512 facts — the size Phase 702 chose
    /// for its surface, below which a whole-scope enumeration is still a
    /// fraction of a second and the index would be pure overhead.
    ///
    /// **On GP 11.** The index changes how a large scope's point reads
    /// execute, never what they return: the contract pack runs against
    /// both paths, and every indexed read is the enumeration's pipeline
    /// applied to a narrower read. A deployment that wants the pre-890
    /// mechanism as well as its answers composes `disabled`.
    let defaults: FactIndexOptions = { Enabled = true; MinimumFacts = 512 }

    /// Enabled at every size — no threshold. The shape the contract pack
    /// binds, so the indexed path is exercised by every contract case
    /// rather than only at a scale a test suite cannot reach.
    let always: FactIndexOptions = { defaults with MinimumFacts = 0 }

/// The point-read index's on-disk footprint (Phase 890).
module FactIndex =

    /// Blob-name prefix every index leaf in a scope lives under. A sibling
    /// of `_facts/`, never a child: the fact census lists `_facts/` and
    /// must never count a derived artefact.
    [<Literal>]
    let Prefix = "_factindex/"

/// One parsed index leaf.
type internal FactIndexLeaf = {
    SubjectMetric: string
    FromKind: char
    FromTicks: int64
    ToKind: char
    ToTicks: int64
    MethodHash: string
    AsOfTicks: int64
    FactId: string
}

/// The leaf layout: how a fact names its leaf, how a leaf name parses
/// back, and the superset tests a read narrows by.
module internal FactIndexLayout =

    /// Bounds any difference between a `Local` endpoint's stored UTC
    /// instant and the ticks it reads back as on another machine (every
    /// UTC offset is within 14 hours).
    let LocalTolerance = TimeSpan.FromDays(2.0).Ticks

    let private hex (s: string) =
        Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes s)
        |> Convert.ToHexStringLower

    // A fixed period and method, so the store's own lineage key can be
    // reused as the (subject, metric) key: it canonicalises the subject
    // exactly as the lineage the enumeration compares does, which a
    // second rendering of the subject here could drift from.
    let private anyPeriod: TemporalExtent = {
        From = DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        To = DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        Label = None
    }

    /// The (subject, metric) key segment.
    let subjectMetricKey (subject: SubjectRef) (metric: MetricRef) : string =
        hex (Fact.lineageKey subject metric anyPeriod (HumanAsserted ""))

    /// The method-identity half of the lineage segment.
    let methodHash (m: MethodRef) : string =
        (hex (Fact.methodIdentity m)).Substring(0, 32)

    let private endpoint (d: DateTime) : char * int64 =
        match d.Kind with
        | DateTimeKind.Utc -> 'U', d.Ticks
        | DateTimeKind.Unspecified -> 'N', d.Ticks
        | _ -> 'L', d.ToUniversalTime().Ticks

    /// The leaf blob name for one fact.
    let leafName (f: Fact) : string =
        let fk, ft = endpoint f.Period.From
        let tk, tt = endpoint f.Period.To

        sprintf
            "%s%s/%c%019d%c%019d_%s/%019d_%s.ref"
            FactIndex.Prefix
            (subjectMetricKey f.Subject f.Metric)
            fk
            ft
            tk
            tt
            (methodHash f.Method)
            f.AsOf.Ticks
            (SecondaryIndex.BlobIndex.pathSafeSegment f.FactId)

    /// Parse a leaf blob name; `None` for anything that is not one (a
    /// stray blob under the prefix is ignored, never trusted).
    let tryParse (name: string) : FactIndexLeaf option =
        let parts = name.Replace('\\', '/').Split('/')

        if parts.Length < 4 || parts[parts.Length - 4] + "/" <> FactIndex.Prefix then
            None
        else
            let sm = parts[parts.Length - 3]
            let lineage = parts[parts.Length - 2]
            let leaf = parts[parts.Length - 1]

            let ticks (s: string) =
                match
                    Int64.TryParse(s, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
                with
                | true, v -> Some v
                | _ -> None

            let kindOk (c: char) = c = 'U' || c = 'N' || c = 'L'

            if
                lineage.Length <= 41
                || lineage[40] <> '_'
                || not (kindOk lineage[0])
                || not (kindOk lineage[20])
                || not (leaf.EndsWith(".ref", StringComparison.Ordinal))
                || leaf.Length < 25
                || leaf[19] <> '_'
            then
                None
            else
                match
                    ticks (lineage.Substring(1, 19)), ticks (lineage.Substring(21, 19)), ticks (leaf.Substring(0, 19))
                with
                | Some fromTicks, Some toTicks, Some asOf ->
                    Some {
                        SubjectMetric = sm
                        FromKind = lineage[0]
                        FromTicks = fromTicks
                        ToKind = lineage[20]
                        ToTicks = toTicks
                        MethodHash = lineage.Substring 41
                        AsOfTicks = asOf
                        FactId = Uri.UnescapeDataString(leaf.Substring(20, leaf.Length - 24))
                    }
                | _ -> None

    // The ticks an endpoint READS BACK as, as an interval: exact for `U`
    // and `N`, the stored instant widened by the tolerance for `L`.
    let private readBack (kind: char) (ticks: int64) : int64 * int64 =
        if kind = 'L' then
            ticks - LocalTolerance, ticks + LocalTolerance
        else
            ticks, ticks

    /// Could this leaf's fact overlap `period`? The enumeration's test is
    /// `period.From < f.Period.To && f.Period.From < period.To` on the
    /// read-back values; this is that test against their widest range.
    let mayOverlap (period: TemporalExtent) (leaf: FactIndexLeaf) : bool =
        let fromLo, _ = readBack leaf.FromKind leaf.FromTicks
        let _, toHi = readBack leaf.ToKind leaf.ToTicks
        period.From.Ticks < toHi && fromLo < period.To.Ticks

    // Does one endpoint canonicalise — as the lineage key does, through
    // `ToUniversalTime` on THIS machine — to the target's instant?
    let private sameInstant (kind: char) (ticks: int64) (target: DateTime) : bool =
        let targetUtc = target.ToUniversalTime().Ticks

        match kind with
        | 'U' -> ticks = targetUtc
        | 'N' -> DateTime(ticks, DateTimeKind.Unspecified).ToUniversalTime().Ticks = targetUtc
        | _ -> abs (ticks - targetUtc) <= LocalTolerance

    /// Could this leaf's fact share a lineage with a fact over
    /// (`subjectMetric`, `period`, `method`)? A superset test: the caller
    /// decides with the lineage key itself once the fact is read.
    let mayShareLineage (subjectMetric: string) (period: TemporalExtent) (methodHash: string) (leaf: FactIndexLeaf) =
        leaf.SubjectMetric = subjectMetric
        && leaf.MethodHash = methodHash
        && sameInstant leaf.FromKind leaf.FromTicks period.From
        && sameInstant leaf.ToKind leaf.ToTicks period.To

/// What a consulting read found when it compared the index with the census.
type internal FactIndexState =
    /// The index is off, or the scope is below the threshold: enumerate,
    /// and write no index.
    | NotConsulted
    /// Some census member has no leaf: enumerate, then write the missing
    /// leaves. Carries the fact ids that do have one.
    | Incomplete of indexed: HashSet<string>
    /// Every census member has a leaf. Carries the leaves and each census
    /// member's position and blob name, so a narrowed read keeps census
    /// order — the order the enumeration's stable sorts break ties by.
    | Complete of leaves: FactIndexLeaf list * census: Dictionary<string, int * string>

/// Blob-backed default `IFactStore`. Construct via `BlobFactStore.create`
/// (or `createWithRegistry` to enable Phase 566 canonical-method
/// selection — `registry = None` preserves the registry-less behaviour
/// byte-for-byte, GP 11; or `createWithSurface` to choose the Phase 702
/// metric-surface policy explicitly; or `createWithIndex` to choose the
/// Phase 890 point-read index policy as well).
type BlobFactStore
    (
        storage: IBlobStorage,
        events: IEventStore,
        registry: Grounding.IMetricRegistry option,
        clock: unit -> DateTime,
        surfaceOptions: FactSurfaceOptions,
        indexOptions: FactIndexOptions
    ) =

    static let jsonOptions = FableConverters.create ()

    // One blob per fact under the scope's `_facts/` prefix.
    let factsPrefix = "_facts/"
    let blobName (factId: string) = sprintf "%s%s.json" factsPrefix factId

    // The blob name IS the fact id (Phase 702's census rests on this):
    // `_facts/{factId}.json`, so listing the prefix enumerates every fact
    // that exists without downloading one.
    let factIdOfBlob (name: string) =
        let stem =
            if name.StartsWith(factsPrefix, StringComparison.Ordinal) then
                name.Substring factsPrefix.Length
            else
                name

        if stem.EndsWith(".json", StringComparison.Ordinal) then
            stem.Substring(0, stem.Length - 5)
        else
            stem

    // Phase 702 — the derived current-heads read model. Constructed
    // eagerly and consulted only when the policy says so, so a disabled
    // policy costs one unused object per store and nothing per call. Its
    // census is sized to decode the largest difference the policy will
    // fold incrementally (Phase 891).
    let surface =
        BlobFactSurface(storage, FactCensus.widthFor surfaceOptions.MaxIncrementalFold) :> IFactSurface

    // Phase 891 — the parse cache over the surface. It does not exist when
    // the surface is disabled, so a disabled policy holds nothing.
    let surfaceCache =
        if surfaceOptions.Enabled then
            Some(FactSurfaceCache.CreateDefault())
        else
            None

    let cacheSurface (scopeId: string) (metric: string) (snapshot: FactSurfaceSnapshot) =
        match surfaceCache with
        | Some cache -> cache.Store(scopeId, metric, snapshot)
        | None -> ()

    let serialise (f: Fact) : byte[] =
        JsonSerializer.Serialize(f, jsonOptions) |> Encoding.UTF8.GetBytes

    let deserialise (bytes: byte[]) : Fact =
        JsonSerializer.Deserialize<Fact>(Encoding.UTF8.GetString bytes, jsonOptions)

    // The facts behind the given blob names, in the given order. A blob
    // that will not read or parse is skipped — the enumeration's rule, so
    // every path that reads through here agrees with it.
    let loadNames (scopeId: string) (names: string list) : Async<Fact list> = async {
        let! facts =
            names
            |> List.map (fun name -> async {
                let! r = storage.Download(scopeId, name)

                return
                    match r with
                    | Ok bytes ->
                        try
                            Some(deserialise bytes)
                        with _ ->
                            None
                    | Error _ -> None
            })
            |> BlobFanOut.run

        return facts |> Array.choose id |> Array.toList
    }

    // Every fact in scope, materialised. Stateless (recomputed per call).
    let loadAll (scopeId: string) : Async<Fact list> = async {
        let! names = storage.List(scopeId, factsPrefix)
        return! loadNames scopeId names
    }

    let load (scopeId: string) (factId: string) : Async<Fact option> = async {
        let! r = storage.Download(scopeId, blobName factId)

        return
            match r with
            | Ok bytes ->
                try
                    Some(deserialise bytes)
                with _ ->
                    None
            | Error _ -> None
    }

    let lineageKeyOf (f: Fact) : string =
        Fact.lineageKey f.Subject f.Metric f.Period f.Method

    let periodsOverlap (a: TemporalExtent) (b: TemporalExtent) : bool = a.From < b.To && b.From < a.To

    // ─── The point-read index (Phase 890) ─────────────────────────────
    //
    // The layout, the census rule and why the two agree with the
    // enumeration are in the note above `FactIndexOptions`.

    /// Write one fact's leaf. `false` on any failure — the census check on
    /// the next consulting read is what notices, and repairs, a leaf that
    /// is missing, so a failure here is never propagated.
    ///
    /// The leaf describes the fact AS IT READS BACK — the form every read,
    /// and a rebuild, compares against — so the write path and a rebuild
    /// name the same leaf even where the serialiser normalises a
    /// `DateTimeKind` on the way through.
    let writeLeaf (scopeId: string) (f: Fact) : Async<bool> = async {
        try
            let readBack = deserialise (serialise f)
            let! r = storage.Upload(scopeId, FactIndexLayout.leafName readBack, Array.empty)
            return Result.isOk r
        with _ ->
            return false
    }

    let writeLeaves (scopeId: string) (facts: Fact list) : Async<int> = async {
        let! written = facts |> List.map (writeLeaf scopeId) |> BlobFanOut.run
        return written |> Array.filter id |> Array.length
    }

    /// Compare the index with the census `names`. Lists the leaf set only
    /// when the policy consults the index at this census size.
    let readIndex (scopeId: string) (names: string list) : Async<FactIndexState> = async {
        if not indexOptions.Enabled || List.length names < indexOptions.MinimumFacts then
            return NotConsulted
        else
            let! leafNames = storage.List(scopeId, FactIndex.Prefix)
            let leaves = leafNames |> List.choose FactIndexLayout.tryParse
            let indexed = HashSet<string>(leaves |> Seq.map _.FactId, StringComparer.Ordinal)
            let census = Dictionary<string, int * string>(StringComparer.Ordinal)
            names |> List.iteri (fun i name -> census[factIdOfBlob name] <- (i, name))

            // Two census names for one id cannot both be narrowed to, so a
            // scope holding them always enumerates.
            if census.Count = List.length names && census.Keys |> Seq.forall indexed.Contains then
                return Complete(leaves, census)
            else
                return Incomplete indexed
    }

    /// Write the leaves the enumeration just proved missing. The facts are
    /// already in hand, so a repair costs writes and no reads.
    let repairIndex (scopeId: string) (facts: Fact list) (indexed: HashSet<string>) : Async<unit> =
        facts
        |> List.filter (fun f -> not (indexed.Contains f.FactId))
        |> writeLeaves scopeId
        |> Async.Ignore

    /// The census members the given leaves admit, as blob names in census
    /// order — the order the enumeration reads in, and so the order its
    /// stable sorts break ties by.
    let admittedNames (census: Dictionary<string, int * string>) (admitted: FactIndexLeaf seq) : string list =
        admitted
        |> Seq.choose (fun l ->
            match census.TryGetValue l.FactId with
            | true, entry -> Some entry
            | _ -> None)
        |> Seq.distinct
        |> Seq.sortBy fst
        |> Seq.map snd
        |> List.ofSeq

    /// The leaves that may belong to the lineage of a fact over
    /// (`subject`, `metric`, `period`, `method`).
    let lineageLeaves
        (leaves: FactIndexLeaf list)
        (subject: SubjectRef)
        (metric: MetricRef)
        (period: TemporalExtent)
        (method: MethodRef)
        : FactIndexLeaf list =
        let subjectMetric = FactIndexLayout.subjectMetricKey subject metric
        let methodHash = FactIndexLayout.methodHash method

        leaves
        |> List.filter (FactIndexLayout.mayShareLineage subjectMetric period methodHash)

    /// The enumeration's head rule over one lineage's facts in census
    /// order: the FIRST fact seen among equal `AsOf` values wins.
    let headOf (facts: Fact list) : Fact option =
        facts
        |> List.fold
            (fun (acc: Fact option) f ->
                match acc with
                | Some existing when existing.AsOf >= f.AsOf -> acc
                | _ -> Some f)
            None

    /// The facts a query's clauses can concern. Narrowed through the index
    /// when the query names a subject AND a metric and the index is
    /// complete; every fact otherwise — the enumeration, repairing the
    /// index on the way when it was found incomplete. The query pipeline
    /// filters whatever this returns, so a wider read is never a wrong one.
    let queryCandidates (scopeId: string) (query: FactQuery) : Async<Fact list> = async {
        match query.Subject, query.Metric with
        | Some subject, Some metric when indexOptions.Enabled ->
            let! names = storage.List(scopeId, factsPrefix)
            let! state = readIndex scopeId names

            match state with
            | Complete(leaves, census) ->
                let key = FactIndexLayout.subjectMetricKey subject metric

                return!
                    leaves
                    |> Seq.filter (fun l ->
                        l.SubjectMetric = key
                        && (query.PeriodOverlaps |> Option.forall (fun p -> FactIndexLayout.mayOverlap p l)))
                    |> admittedNames census
                    |> loadNames scopeId
            | Incomplete indexed ->
                let! all = loadNames scopeId names
                do! repairIndex scopeId all indexed
                return all
            | NotConsulted -> return! loadNames scopeId names
        | _ -> return! loadAll scopeId
    }

    // Readable projections for the audit payloads (shared renderers, so
    // the audit / provenance / evidence surfaces never drift).
    let subjectString = SubjectRef.toString
    let disclosureString = Disclosure.toString

    // Law L4 visibility: facts visible at `t` are those asserted by `t`
    // that no by-`t` successor supersedes.
    //
    // The supersession edges are collected into a set FIRST rather than
    // re-scanned per candidate. Same answer in the same order — a fact is
    // hidden exactly when some by-`t` fact names it — at O(n) instead of
    // O(n²). Phase 701 wrote the nested scan and measured the read at 500
    // heads, where the quadratic term is invisible; at the 100,000 this
    // tier is for it is the whole cost, and it made the *enumeration*
    // baseline this phase is measured against unrunnable rather than
    // merely slow. Worth stating because it is the trap in every
    // "extrapolate the per-item cost" measurement: the per-item cost was
    // not constant.
    let visibleAt (t: DateTime) (all: Fact list) : Fact list =
        let byT = all |> List.filter (fun f -> f.AsOf <= t)
        let superseded = HashSet<string>(StringComparer.Ordinal)

        for f in byT do
            match f.Supersedes with
            | Some sid -> superseded.Add sid |> ignore
            | None -> ()

        byT |> List.filter (fun f -> not (superseded.Contains f.FactId))

    // ─── Canonical-method selection (Phase 566 — D19 closure) ─────────

    // The competition key: two current heads *compete* when they share
    // (subject, metric, period) but were produced by different methods
    // (D19). The period `Label` is cosmetic and excluded, mirroring the
    // lineage key's canonical period.
    let competitionKey (f: Fact) =
        f.Subject, f.Metric, f.Period.From, f.Period.To

    // Resolve a method-less query's competing heads to the metric's
    // registry-declared canonical method, where one is declared. Per
    // competing group: no registry / no declaration → every head (the
    // pre-566 behaviour, GP 11); a declaration with at least one matching
    // head → only the matching head(s); a declaration no head matches →
    // every head (an empty canonical lineage must surface the competitors,
    // never hide the metric entirely — GP 9, and the competition indicator
    // still discloses the contest).
    let selectCanonical (heads: Fact list) : Fact list =
        match registry with
        | None -> heads
        | Some reg ->
            PopulationSelection.canonicalHeads
                competitionKey
                (fun f -> Fact.methodIdentity f.Method)
                (fun f -> reg.TryGetMetric f.Metric.Value |> Option.bind _.CanonicalMethod)
                heads

    // The shared query pipeline: clause filters → L4 visibility → (for a
    // method-less current-heads query) canonical selection. Returns the
    // current heads at `t` (the competition base every returned fact's
    // indicator derives from) alongside the sorted listing.
    //
    // Phase 890 — `all` is every fact, or, for a subject-and-metric query
    // over a complete index, the census-ordered facts of that (subject,
    // metric)'s lineages. Every step below filters to that set anyway, so
    // the two inputs produce the same answer.
    let runQuery (scopeId: string) (query: FactQuery) : Async<Fact list * Fact list> = async {
        let! all = queryCandidates scopeId query
        let t = query.AsOf |> Option.defaultValue (clock().ToUniversalTime())

        // Clause filters minus `Method` first (subject / metric / period)
        // — the *competition scope*. A fact's competitors are the other
        // current heads sharing its (subject, metric, period) regardless
        // of which method the caller named, so the indicator base is
        // derived before the method clause narrows the listing.
        let scoped =
            all
            |> List.filter (fun f ->
                (query.Subject |> Option.forall (fun s -> s = f.Subject))
                && (query.Metric |> Option.forall (fun m -> m = f.Metric))
                && (query.PeriodOverlaps |> Option.forall (fun p -> periodsOverlap p f.Period)))

        // Bitemporal visibility (law L4) — the current heads at `t`.
        // Supersession edges never cross a lineage (a superseder shares
        // its predecessor's method identity by construction), so applying
        // the method clause after visibility is equivalent to before it.
        let heads = visibleAt t scoped

        let byMethod (facts: Fact list) =
            match query.Method with
            | None -> facts
            | Some m ->
                facts
                |> List.filter (fun f -> Fact.methodIdentity m = Fact.methodIdentity f.Method)

        // The listing: full history when asked; otherwise the current
        // heads, resolved to the canonical method for a method-less query
        // (an explicit Method clause is already the caller's selection).
        let listing =
            if query.IncludeSuperseded then
                byMethod scoped |> List.filter (fun f -> f.AsOf <= t)
            elif query.Method.IsSome then
                byMethod heads
            else
                selectCanonical heads

        return
            heads,
            listing
            |> List.sortBy (fun f -> f.Subject.Hierarchy, f.Metric.Value, f.Period.From)
    }

    // The derived competition indicator for one returned fact: the method
    // identities of the *other* current heads sharing its (subject,
    // metric, period). A superseded fact in an `IncludeSuperseded`
    // listing never lists its own lineage's head (same method identity).
    let competingMethods (heads: Fact list) (f: Fact) : string list =
        let key = competitionKey f
        let ownMethod = Fact.methodIdentity f.Method

        heads
        |> List.filter (fun g -> competitionKey g = key)
        |> List.map (fun g -> Fact.methodIdentity g.Method)
        |> List.filter (fun identity -> identity <> ownMethod)
        |> List.distinct

    // ─── Population read (Phase 701) ──────────────────────────────────
    //
    // The reference implementation of the cross-subject read: enumerate
    // the scope's heads, filter to the query's subject set, rank, and
    // summarise. **Correct at any size, efficient at small** — which is
    // the deliberate division of labour: this is one O(n) pass over the
    // same blobs `Query` already walks, and an indexed current-heads read
    // model is the scale path behind the same contract. A deployment that
    // never asks a population question pays nothing for it (GP 13).
    //
    // Every decidable step is shared with `PopulationQueryTypes` rather
    // than re-implemented here — the subject predicate, the threshold,
    // the ordering resolution, the ranking and the statistics fold — so
    // an indexed implementation over the same heads is byte-for-byte
    // equivalent by construction rather than by a second reading of the
    // spec.
    // The metric's declared staleness policy — an undeclared metric reads
    // as `UntilSuperseded`, the shared default across every fact surface.
    let stalenessOf (metricDef: Grounding.MetricDefinition option) =
        metricDef
        |> Option.map _.Staleness
        |> Option.defaultValue Grounding.UntilSuperseded

    let enumeratePopulation
        (scopeId: string)
        (query: PopulationQuery)
        (direction: RankDirection)
        (metricDef: Grounding.MetricDefinition option)
        : Async<PopulationResult> =
        async {
            let! all = loadAll scopeId
            let t = query.AsOf |> Option.defaultValue (clock().ToUniversalTime())

            let scoped =
                all
                |> List.filter (fun f ->
                    f.Metric = query.Metric
                    && PopulationQuery.matchesSubject query f.Subject
                    && (query.PeriodOverlaps |> Option.forall (fun p -> periodsOverlap p f.Period)))

            // Law L4: the heads current at `t`. There is no
            // `IncludeSuperseded` on the population shape — a ranking
            // that mixed a value with the value that replaced it would
            // rank one subject twice and mean nothing.
            let heads = visibleAt t scoped

            // D19: competing methods are never merged, so a population
            // admitting every method would rank one subject once per
            // method. `CanonicalMethodOnly` is the default and reuses the
            // exact selection `Query` applies to a method-less read.
            let selected =
                match query.Methods with
                | AllCompetingMethods -> heads
                | OneMethod m ->
                    heads
                    |> List.filter (fun f -> Fact.methodIdentity m = Fact.methodIdentity f.Method)
                | CanonicalMethodOnly -> selectCanonical heads

            // The threshold narrows the POPULATION, not just the ranking,
            // so the statistics describe what the query matched.
            let population =
                match query.Threshold with
                | None -> selected
                | Some threshold -> selected |> List.filter (fun f -> ValueThreshold.satisfies threshold f.Value)

            // Freshness is derived per the metric's declared staleness
            // policy at the query instant — so an `AsOf` replay reports the
            // freshness that held THEN, not now. Every member is a current
            // head at `t` by construction of `visibleAt`.
            let policy = stalenessOf metricDef

            let stats =
                PopulationStats.ofPopulation (fun f -> Freshness.derive policy f true t) population

            let k = PopulationQuery.effectiveTopK query
            let ranked = PopulationRanking.rank direction population

            return {
                Ranked = ranked |> List.truncate k
                Direction = direction
                EffectiveTopK = k
                Truncated = List.length ranked > k
                Stats = stats
            }
        }

    // ─── The metric surface (Phase 702) ───────────────────────────────
    //
    // The same read, executed against the derived current-heads snapshot
    // instead of the log. Every decidable step below is the SAME function
    // the enumeration above calls, applied to `PopulationMember` values
    // rather than facts — the subject predicate literally is
    // `PopulationQuery.matchesSubject`, the selection
    // `PopulationSelection.canonicalHeads`, the ranking the comparator
    // inside `PopulationRanking.rankBy`, the summary
    // `PopulationStats.ofMembersWithFreshness`. So the two paths do not
    // agree because they were checked against each other; they agree
    // because there is one implementation of each decision.
    //
    // What differs is only what is READ: a snapshot and the top-k facts,
    // instead of every fact.

    // A head is a fact no other fact supersedes — the surface's
    // population, unconstrained by any visibility instant. `visibleAt`
    // narrows that same set to an `AsOf`, which needs the superseded
    // facts a heads-only surface does not carry; hence task 702.D.
    let currentHeadsFor (metric: MetricRef) (all: Fact list) : Fact list =
        let superseded = HashSet<string>(StringComparer.Ordinal)

        for f in all do
            match f.Supersedes with
            | Some sid -> superseded.Add sid |> ignore
            | None -> ()

        all
        |> List.filter (fun f -> f.Metric = metric && not (superseded.Contains f.FactId))

    let rebuildSurface (scopeId: string) (metric: MetricRef) : Async<FactSurfaceSnapshot option> = async {
        let! all = loadAll scopeId
        let heads = currentHeadsFor metric all

        // The census is every fact the rebuild actually READ — never the
        // listing's names. A blob that would not read this time is left
        // out, so the next read sees it as unseen and tries it again,
        // rather than recording as folded a fact whose row is missing.
        let! r = surface.Rebuild(scopeId, metric.Value, heads, all |> List.map _.FactId)

        return
            match r with
            | Ok snapshot ->
                cacheSurface scopeId metric.Value snapshot
                Some snapshot
            | Error _ -> None
    }

    /// Bring a snapshot up to date against the scope's fact census, or
    /// rebuild it. `None` means "could not produce a trustworthy snapshot"
    /// — the caller enumerates, which is always available and always
    /// right.
    let reconcileSurface
        (scopeId: string)
        (metric: MetricRef)
        (logIds: string list)
        (logCensus: FactCensusValue)
        (existing: FactSurfaceSnapshot option)
        : Async<FactSurfaceSnapshot option> =
        async {
            match existing with
            | Some snapshot when not snapshot.Stale ->
                let folded = FactCensus.valueOf snapshot.Census

                if folded = logCensus then
                    // The same count and the same digest: the same ids (the
                    // one assumption, stated in FactSurface.fs) — and the
                    // converged path never materialises the difference.
                    return Some snapshot
                elif logCensus.Count - folded.Count > surfaceOptions.MaxIncrementalFold then
                    // More unseen facts than the policy folds one by one:
                    // one enumeration is cheaper, whatever the decode says.
                    return! rebuildSurface scopeId metric
                else
                    match FactCensus.unseen snapshot.Census logIds with
                    | Error _ ->
                        // Undecodable, a departed fact (an erasure), or a
                        // key collision: rebuild from the log rather than
                        // reason about which rows survived.
                        return! rebuildSurface scopeId metric
                    | Ok missing ->
                        let! fetched = missing |> List.map (load scopeId) |> BlobFanOut.run
                        let facts = fetched |> Array.choose id |> Array.toList

                        if List.length facts <> List.length missing then
                            return! rebuildSurface scopeId metric
                        else
                            // Ascending `AsOf` is a topological order over
                            // the supersession edges (law L3).
                            let folded, _ =
                                FactSurfaceFold.applyFacts metric.Value (facts |> List.sortBy _.AsOf) snapshot

                            // Belt and braces: a decoded difference is only
                            // trusted when the fold lands exactly on the
                            // log's census.
                            if FactCensus.valueOf folded.Census <> logCensus then
                                return! rebuildSurface scopeId metric
                            else
                                let! put = surface.Put(scopeId, metric.Value, folded)

                                match put with
                                | Ok() -> cacheSurface scopeId metric.Value folded
                                | Error _ -> ()

                                return Some folded
            | _ -> return! rebuildSurface scopeId metric
        }

    /// Run the population question over a reconciled snapshot. `None` means
    /// the projection declined — the caller enumerates, which is always
    /// available and always right.
    let answerFromSnapshot
        (scopeId: string)
        (query: PopulationQuery)
        (direction: RankDirection)
        (metricDef: Grounding.MetricDefinition option)
        (snapshot: FactSurfaceSnapshot)
        : Async<PopulationResult option> =
        async {
            let t = clock().ToUniversalTime()

            // A head stamped in the FUTURE relative to this read's instant
            // has not happened yet under law L4, so the enumeration hides
            // it — and, where it superseded something, shows that
            // predecessor instead. A heads-only projection cannot produce
            // the predecessor, so it declines the whole question rather
            // than answer it differently. Ordinary transaction times never
            // trip this; clock skew across replicas and an out-of-band
            // write do, and those are exactly the cases where a silently
            // different answer would be worst.
            if snapshot.Rows |> List.exists (fun r -> r.Member.AsOf > t) then
                return None
            else
                let admitted = FactSurfaceRead.matching query snapshot

                let selected =
                    match query.Methods with
                    | AllCompetingMethods -> admitted
                    | OneMethod m ->
                        let identity = Fact.methodIdentity m
                        admitted |> List.filter (fun x -> x.MethodIdentity = identity)
                    | CanonicalMethodOnly ->
                        match registry with
                        | None -> admitted
                        | Some _ ->
                            let selector = metricDef |> Option.bind _.CanonicalMethod

                            PopulationSelection.canonicalHeads
                                PopulationMember.competitionKey
                                _.MethodIdentity
                                (fun _ -> selector)
                                admitted

                let population =
                    match query.Threshold with
                    | None -> selected
                    | Some threshold ->
                        selected
                        |> List.filter (fun x -> ValueThreshold.satisfiesMagnitude threshold x.Magnitude)

                let policy = stalenessOf metricDef

                let stats =
                    PopulationStats.ofMembers (fun x -> Freshness.deriveAt policy x.AsOf true t) population

                let k = PopulationQuery.effectiveTopK query
                let ranked = PopulationRanking.rankMembers direction population

                // The only fact reads a population question costs: the page
                // it actually returns, bounded by the contract's `MaxTopK`
                // rather than by the population's size.
                let! page =
                    ranked
                    |> List.truncate k
                    |> List.map (fun x -> load scopeId x.FactId)
                    |> BlobFanOut.run

                let resolved = page |> Array.choose id

                if resolved.Length <> min k (List.length ranked) then
                    // A ranked head could not be re-read. Returning a short
                    // ranking would be a different answer, not a slower one
                    // — so decline and let the caller enumerate.
                    return None
                else
                    return
                        Some {
                            Ranked = Array.toList resolved
                            Direction = direction
                            EffectiveTopK = k
                            Truncated = List.length ranked > k
                            Stats = stats
                        }
        }

    let surfacePopulation
        (scopeId: string)
        (query: PopulationQuery)
        (direction: RankDirection)
        (metricDef: Grounding.MetricDefinition option)
        : Async<PopulationResult option> =
        async {
            // The census. This is the same `List` call `loadAll` makes
            // first, so consulting the surface costs nothing the
            // enumeration would not have paid anyway — the saving is the
            // per-fact download and deserialisation that follows it.
            let! names = storage.List(scopeId, factsPrefix)

            if List.length names < surfaceOptions.MinimumHeads then
                // GP 13 — below the threshold a surface cannot pay for
                // itself, so none is built and the blob layout is
                // unchanged.
                return None
            else
                // The census value: one key per listed id, which is what
                // both the reconcile and the parse cache compare against.
                let logIds = names |> List.map factIdOfBlob
                let logCensus = FactCensus.valueOfIds logIds

                let! cached = async {
                    match surfaceCache with
                    | None -> return None
                    | Some cache ->
                        match cache.TryGet(scopeId, query.Metric.Value, logCensus) with
                        | None -> return None
                        | Some snapshot ->
                            // A hit still asks whether the snapshot blob
                            // exists — one probe, never a download — so
                            // `FactSurface.drop` flushes every replica's
                            // cache as well as the blob, and remains the
                            // operator's lever it was before the cache.
                            let! present = storage.Exists(scopeId, FactSurface.blobName query.Metric.Value)

                            if present then
                                return Some snapshot
                            else
                                cache.Evict(scopeId, query.Metric.Value)
                                return None
                }

                match cached with
                | Some snapshot -> return! answerFromSnapshot scopeId query direction metricDef snapshot
                | None ->
                    let! existing = surface.Get(scopeId, query.Metric.Value)

                    // A snapshot already converged is remembered here; the
                    // reconcile remembers the ones it folds or rebuilds.
                    match existing with
                    | Some snapshot when not snapshot.Stale && FactCensus.valueOf snapshot.Census = logCensus ->
                        cacheSurface scopeId query.Metric.Value snapshot
                    | _ -> ()

                    let! reconciled = reconcileSurface scopeId query.Metric logIds logCensus existing

                    match reconciled with
                    | None -> return None
                    | Some snapshot -> return! answerFromSnapshot scopeId query direction metricDef snapshot
        }

    let runPopulation (scopeId: string) (query: PopulationQuery) : Async<Result<PopulationResult, string>> = async {
        let metricDef = registry |> Option.bind (fun r -> r.TryGetMetric query.Metric.Value)

        // Resolve the ordering FIRST: a refusal (GP 9 — an unresolvable
        // direction is never guessed) costs no store read, and the caller
        // gets the same answer whether or not the population exists.
        match PopulationOrdering.resolve query.Metric.Value query.Ordering (metricDef |> Option.map _.Direction) with
        | Error refusal -> return Error refusal
        | Ok direction ->
            // Task 702.D — a historical read bypasses the surface. The
            // surface holds current heads; reconstructing what was current
            // at `t` needs the facts a later assertion superseded, which is
            // precisely what a heads-only projection has dropped.
            // Correct-but-slow for the rare replay question, by design.
            let usesSurface = surfaceOptions.Enabled && query.AsOf.IsNone

            let! viaSurface =
                if usesSurface then
                    surfacePopulation scopeId query direction metricDef
                else
                    async.Return None

            match viaSurface with
            | Some result -> return Ok result
            | None ->
                let! enumerated = enumeratePopulation scopeId query direction metricDef
                return Ok enumerated
    }

    // Assert-time maintenance (task 702.B). Best effort by construction:
    // the fact is already durable when this runs, the surface is derived,
    // and the read path reconciles against the log regardless — so every
    // failure here costs a slower read and never a different answer. The
    // ladder is: fold the fact in; if that fails, flush the snapshot; if
    // the flush fails too, mark it stale. Nothing here can fail an
    // `Assert`.
    let maintainSurface (scopeId: string) (fact: Fact) : Async<unit> = async {
        if not surfaceOptions.Enabled then
            return ()
        else
            try
                let! updated = surface.Update(scopeId, fact.Metric.Value, fact)

                match updated with
                | Ok None -> return ()
                | Ok(Some written) ->
                    // Converged against the log as this replica last read
                    // it plus this fact: a read whose census matches may
                    // answer from it without a download.
                    cacheSurface scopeId fact.Metric.Value written
                    return ()
                | Error _ ->
                    do! surface.Drop(scopeId, fact.Metric.Value)
                    let! still = surface.Get(scopeId, fact.Metric.Value)

                    if still.IsSome then
                        do! surface.MarkStale(scopeId, fact.Metric.Value)
            with _ ->
                try
                    do! surface.MarkStale(scopeId, fact.Metric.Value)
                with _ ->
                    ()
    }

    // Assert-time maintenance for a BATCH (task 704.B). The same seam,
    // the same fold, and the same best-effort ladder as `maintainSurface`
    // above — one round trip per metric the batch touched, instead of one
    // per fact.
    //
    // Two details are load-bearing, and both are properties of
    // `FactSurfaceFold.applyFact` rather than of this function:
    //
    //  1. **Ascending `AsOf` order.** Supersession strictly increases
    //     `AsOf` within a lineage (law L3), so ascending `AsOf` is a
    //     topological order over the batch's edges; folding a successor
    //     before its predecessor would leave a row nothing ever retires.
    //     `applyFact` states this requirement of its batch callers, and
    //     this is the caller it means.
    //  2. **The WHOLE batch is folded into EVERY touched metric's
    //     snapshot**, not just that metric's own facts. A scope's facts
    //     share one blob prefix, so a snapshot's census accounts for its
    //     neighbours' facts too — `applyFact` absorbs an out-of-metric
    //     fact without making it a row, which is exactly what keeps
    //     `FactSurfaceRead.foldedCount` comparable to the scope's blob
    //     count. Absorbing them here is precisely what the read-time
    //     reconcile would otherwise do, through the same function; doing
    //     it now leaves the snapshot converged rather than one incremental
    //     fold behind.
    //
    // Nothing here can fail an `AssertBatch`: the facts are already
    // durable, the surface is derived, and the read path reconciles
    // against the log regardless — so every failure costs a slower read
    // and never a different answer.
    let maintainSurfaceBatch (scopeId: string) (facts: Fact list) : Async<unit> = async {
        if not surfaceOptions.Enabled || List.isEmpty facts then
            return ()
        else
            let ordered = facts |> List.sortBy _.AsOf
            let metrics = facts |> List.map _.Metric |> List.distinct

            for metric in metrics do
                try
                    let! existing = surface.Get(scopeId, metric.Value)

                    match existing with
                    // No snapshot yet is a no-op, not a failure — a
                    // surface that has not been built has nothing to
                    // maintain, and the read path builds it on demand.
                    | None -> ()
                    | Some snapshot ->
                        // Linear in the batch (Phase 891): one walk of the
                        // snapshot's rows, however many heads the batch
                        // supersedes.
                        let folded, _ = FactSurfaceFold.applyFacts metric.Value ordered snapshot

                        let! put = surface.Put(scopeId, metric.Value, folded)

                        match put with
                        | Ok() -> cacheSurface scopeId metric.Value folded
                        | Error _ ->
                            do! surface.Drop(scopeId, metric.Value)
                            let! still = surface.Get(scopeId, metric.Value)

                            if still.IsSome then
                                do! surface.MarkStale(scopeId, metric.Value)
                with _ ->
                    try
                        do! surface.MarkStale(scopeId, metric.Value)
                    with _ ->
                        ()
    }

    // GP 6 audit — one ModuleEvent under the reserved `_facts` source
    // module per state change (assert / supersession).
    let writeEvent (scopeId: string) (occurredAt: DateTime) (eventType: string) (payload: string) : Async<unit> =
        events.Write {
            Id = Guid.NewGuid()
            OccurredAt = occurredAt
            ScopeId = scopeId
            SourceModule = FactEvents.SourceModule
            EventType = eventType
            Payload = payload
        }

    // ─── The write core (Phase 704) ───────────────────────────────────
    //
    // ONE implementation of what asserting drafts means — the scalar
    // `Assert` is a batch of one, not a second copy of the derivation.
    // That is the point of the phase as much as the amortisation is: the
    // content address, the idempotency test, the lineage-head lookup, the
    // `AsOf` rule and the write all have exactly one definition, so
    // "scalar and batch agree" is a property of the code rather than a
    // pair of tests.
    //
    // What the core does NOT do is audit or maintain the surface. Those
    // are the two things that legitimately differ between the two
    // callers — per-fact rows versus one summarised row; one fold versus
    // one fold per metric — so each caller does its own, over the
    // dispositions the core hands back.
    let writeDrafts (scopeId: string) (drafts: FactDraft list) : Async<Result<DraftDisposition list, string>> = async {
        // The census. The blob name IS the fact id (the same property
        // Phase 702's surface rests on), so presence is decided by a
        // single `List` call — no download per draft, at any batch size.
        // This is what makes re-running an unchanged population cost one
        // round trip rather than one per subject.
        let! names = storage.List(scopeId, factsPrefix)
        let stored = HashSet<string>(names |> List.map factIdOfBlob, StringComparer.Ordinal)

        let addressed =
            drafts
            |> List.map (fun d ->
                let inputHashes = Fact.effectiveInputHashes d.Method d.Evidence d.Value
                d, Fact.compute d.Subject d.Metric d.Period d.Method inputHashes)

        if addressed |> List.forall (fun (_, factId) -> stored.Contains factId) then
            // Every draft is already stored, so no lineage head is needed
            // and the log is never read. An empty batch takes this branch
            // too, vacuously.
            return
                Ok(
                    addressed
                    |> List.map (fun (_, factId) -> {
                        Outcome = BatchIdempotent
                        FactId = factId
                        Written = None
                    })
                )
        else
            let! index = readIndex scopeId names
            let heads = Dictionary<string, Fact>(StringComparer.Ordinal)

            match index with
            | Complete(leaves, census) ->
                // Phase 890 — the heads of the lineages this batch touches,
                // and no others, through the index. A leaf names its fact's
                // `AsOf`, so each lineage's head is DECIDED from the leaves
                // and only the winner is read, to confirm it: one read per
                // touched lineage that has a head, none for a new lineage.
                // A winner that does not confirm (unreadable, another
                // lineage behind a tolerant leaf, a leaf that disagrees
                // with its fact) sends that lineage to the full rule over
                // every candidate that reads.
                let touched =
                    addressed
                    |> List.filter (fun (_, factId) -> not (stored.Contains factId))
                    |> List.map (fun (d, _) -> Fact.lineageKey d.Subject d.Metric d.Period d.Method, d)
                    |> List.distinctBy fst

                let candidates =
                    touched
                    |> List.map (fun (key, d) ->
                        let lineage = lineageLeaves leaves d.Subject d.Metric d.Period d.Method
                        let names = lineage |> admittedNames census

                        // The winner by the head rule over the leaves' own
                        // `AsOf`, in census order: first among equals.
                        let ticksOf =
                            lineage
                            |> List.map (fun l -> l.FactId, l.AsOfTicks)
                            |> List.distinctBy fst
                            |> dict

                        let winner =
                            names
                            |> List.fold
                                (fun acc name ->
                                    let ticks = ticksOf[factIdOfBlob name]

                                    match acc with
                                    | Some(_, best) when best >= ticks -> acc
                                    | _ -> Some(name, ticks))
                                None

                        key, names, winner)

                let! confirmed =
                    candidates
                    |> List.map (fun (key, _, winner) -> async {
                        match winner with
                        | None -> return key, None
                        | Some(name, ticks) ->
                            let! read = loadNames scopeId [ name ]

                            match read with
                            | [ f ] when lineageKeyOf f = key && f.AsOf.Ticks = ticks -> return key, Some(Ok f)
                            | _ -> return key, Some(Error())
                    })
                    |> BlobFanOut.run

                for (key, names, _), (_, outcome) in Seq.zip candidates confirmed do
                    match outcome with
                    | None -> ()
                    | Some(Ok head) -> heads[key] <- head
                    | Some(Error()) ->
                        let! lineage = loadNames scopeId names

                        match lineage |> List.filter (fun f -> lineageKeyOf f = key) |> headOf with
                        | Some head -> heads[key] <- head
                        | None -> ()
            | NotConsulted
            | Incomplete _ ->
                let! all = loadAll scopeId

                // The current head of every lineage in scope, indexed once.
                // The scalar path re-derives this per assert by scanning the
                // whole log; a batch of 10⁵ would scan it 10⁵ times. Keeping
                // the FIRST fact seen among equal `AsOf` values matches the
                // scalar path's stable `sortByDescending _.AsOf |> tryHead`.
                for f in all do
                    let key = lineageKeyOf f

                    match heads.TryGetValue key with
                    | true, existing when existing.AsOf >= f.AsOf -> ()
                    | _ -> heads[key] <- f

                match index with
                | Incomplete indexed -> do! repairIndex scopeId all indexed
                | _ -> ()

            // Derivation first, in submission order and entirely in
            // memory: each draft's head comes from the log OR from an
            // earlier draft of this same batch, which is what makes a
            // batch carrying two versions of one lineage settle exactly as
            // two sequential `Assert`s would. Nothing is written until the
            // whole batch is derived, so no derivation ever sees a
            // half-written log.
            let seen = HashSet<string>(stored, StringComparer.Ordinal)
            let dispositions = ResizeArray<DraftDisposition>()

            for draft, factId in addressed do
                if seen.Contains factId then
                    dispositions.Add {
                        Outcome = BatchIdempotent
                        FactId = factId
                        Written = None
                    }
                else
                    let key = Fact.lineageKey draft.Subject draft.Metric draft.Period draft.Method

                    let currentHead =
                        match heads.TryGetValue key with
                        | true, head -> Some head
                        | _ -> None

                    // Transaction time — strictly greater than the head's,
                    // so supersession chains stay acyclic (law L3) even
                    // when the clock is coarse or frozen, which is exactly
                    // the case a batch makes ordinary rather than rare.
                    let now = clock().ToUniversalTime()

                    let asOf =
                        match currentHead with
                        | Some head when head.AsOf >= now -> head.AsOf.AddTicks 1L
                        | _ -> now

                    let fact = {
                        FactId = factId
                        Subject = draft.Subject
                        Metric = draft.Metric
                        Value = draft.Value
                        Period = draft.Period
                        AsOf = asOf
                        Method = draft.Method
                        Evidence = draft.Evidence
                        Confidence = draft.Confidence
                        Supersedes = currentHead |> Option.map _.FactId
                        Disclosure = draft.Disclosure
                    }

                    seen.Add factId |> ignore
                    heads[key] <- fact

                    dispositions.Add {
                        Outcome =
                            if currentHead.IsSome then
                                BatchSuperseding
                            else
                                BatchAsserted
                        FactId = factId
                        Written = Some fact
                    }

            // The writes. Independent by construction — one blob per
            // content address, and the `seen` set guarantees a batch never
            // writes the same address twice — so they parallelise without
            // ordering risk.
            let! writeResults =
                dispositions
                |> Seq.choose _.Written
                |> Seq.map (fun f -> async {
                    let! r = storage.Upload(scopeId, blobName f.FactId, serialise f)
                    return f, r
                })
                |> BlobFanOut.run

            // Phase 890 — the index leaves, with the fact writes and after
            // them: a leaf is written only for a fact that is durable, so no
            // leaf ever names a fact this call failed to store. A leaf that
            // fails is noticed by the next consulting read's census check,
            // which enumerates and rewrites it. Below the threshold, none.
            match index with
            | NotConsulted -> ()
            | Incomplete _
            | Complete _ ->
                do!
                    writeResults
                    |> Array.choose (fun (f, r) ->
                        match r with
                        | Ok _ -> Some f
                        | Error _ -> None)
                    |> List.ofArray
                    |> writeLeaves scopeId
                    |> Async.Ignore

            let failures =
                writeResults
                |> Array.choose (fun (f, r) ->
                    match r with
                    | Error e -> Some(f.FactId, e)
                    | Ok _ -> None)

            match failures with
            | [||] -> return Ok(List.ofSeq dispositions)
            | [| (_, e) |] ->
                // The scalar path's message, verbatim — a batch of one
                // must fail the way `Assert` has always failed.
                return Error(sprintf "fact store write failed: %s" e)
            | many ->
                let named =
                    many
                    |> Array.truncate 10
                    |> Array.map (fun (factId, e) -> sprintf "[%s] %s" factId e)
                    |> String.concat "; "

                let more =
                    if many.Length > 10 then
                        sprintf " (and %d more)" (many.Length - 10)
                    else
                        ""

                return
                    Error(
                        sprintf
                            "fact store write failed for %d of %d facts: %s%s"
                            many.Length
                            writeResults.Length
                            named
                            more
                    )
    }

    /// The caller-facing refusal for a batch whose drafts are not all
    /// well-formed. Names offenders by POSITION — a malformed draft's
    /// content address is meaningless, and position is what the producer
    /// indexes its own input by — plus the subject and metric it claimed,
    /// rendered through the shared `SubjectRef.toString` so the refusal
    /// reads the way every other fact surface reads.
    let renderOffenders (total: int) (offenders: (int * FactDraft * string list) list) : string =
        let named =
            offenders
            |> List.truncate 10
            |> List.map (fun (index, draft, defects) ->
                sprintf
                    "#%d %s / %s: %s"
                    index
                    (subjectString draft.Subject)
                    draft.Metric.Value
                    (String.concat ", " defects))
            |> String.concat "; "

        let more =
            let hidden = List.length offenders - 10

            if hidden > 0 then sprintf " (and %d more)" hidden else ""

        sprintf
            "fact store batch rejected: %d of %d drafts are malformed and none were committed — %s%s"
            (List.length offenders)
            total
            named
            more

    /// The pre-702 four-argument shape, on the default surface policy.
    /// An explicit secondary constructor rather than an optional parameter
    /// on the primary: an optional argument folds into one widened
    /// constructor and the four-argument token disappears, which is a
    /// break for every existing caller.
    new(storage: IBlobStorage, events: IEventStore, registry: Grounding.IMetricRegistry option, clock: unit -> DateTime) =
        BlobFactStore(storage, events, registry, clock, FactSurfaceOptions.defaults)

    /// The pre-890 five-argument shape, on the default point-read index
    /// policy — a secondary constructor for the same reason as the one
    /// above.
    new
        (
            storage: IBlobStorage,
            events: IEventStore,
            registry: Grounding.IMetricRegistry option,
            clock: unit -> DateTime,
            surfaceOptions: FactSurfaceOptions
        ) =
        BlobFactStore(storage, events, registry, clock, surfaceOptions, FactIndexOptions.defaults)

    /// Rebuild the point-read index (Phase 890) from the scope's facts —
    /// the Phase 9f `Rebuild` shape. Idempotent, safe to run concurrently
    /// with writes, and it deletes nothing: a leaf whose fact is gone is
    /// ignored by every read, because reads consult only census members.
    /// Writes regardless of the size threshold — an operator asking for a
    /// rebuild gets one. Returns the number of facts indexed.
    member _.RebuildIndex(scopeId: string) : Async<int> = async {
        let! all = loadAll scopeId
        return! writeLeaves scopeId all
    }

    /// Sample the scope's facts and index leaves and check each side
    /// resolves to the other — the Phase 9f consistency check. A sampled
    /// fact with no leaf describing it is `UnindexedCanonicals`; a sampled
    /// leaf naming a fact the census does not hold is
    /// `OrphanedIndexEntries`. Neither ever changes an answer (the census
    /// check sends an incomplete index to the enumeration and an orphan is
    /// never read); drift is repaired by `RebuildIndex`. A scope the policy
    /// does not index (disabled, or below the threshold) reports an empty
    /// sample rather than a scope's worth of expected misses.
    member _.IndexConsistencyCheck
        (scopeId: string, sampleSize: int)
        : Async<SecondaryIndex.IndexConsistencyEntry list> =
        async {
            let! names = storage.List(scopeId, factsPrefix)

            let entry sample consistent orphans unindexed : SecondaryIndex.IndexConsistencyEntry = {
                StoreName = "facts"
                IndexName = FactIndex.Prefix.TrimEnd '/'
                SampleSize = sample
                ConsistentEntries = consistent
                OrphanedIndexEntries = orphans
                UnindexedCanonicals = unindexed
            }

            if not indexOptions.Enabled || List.length names < indexOptions.MinimumFacts then
                return [ entry 0 0 0 0 ]
            else
                let! leafNames = storage.List(scopeId, FactIndex.Prefix)
                let leaves = leafNames |> List.choose FactIndexLayout.tryParse
                let census = HashSet<string>(names |> List.map factIdOfBlob, StringComparer.Ordinal)
                let! sampled = names |> List.truncate (max sampleSize 0) |> loadNames scopeId

                let described (f: Fact) =
                    let subjectMetric = FactIndexLayout.subjectMetricKey f.Subject f.Metric
                    let methodHash = FactIndexLayout.methodHash f.Method

                    leaves
                    |> List.exists (fun l ->
                        l.FactId = f.FactId
                        && l.AsOfTicks = f.AsOf.Ticks
                        && FactIndexLayout.mayShareLineage subjectMetric f.Period methodHash l)

                let consistent = sampled |> List.filter described |> List.length

                let orphans =
                    leaves
                    |> List.truncate (max sampleSize 0)
                    |> List.filter (fun l -> not (census.Contains l.FactId))
                    |> List.length

                return [ entry sampled.Length consistent orphans (sampled.Length - consistent) ]
        }

    /// The metric surface's parse cache (Phase 891), or `None` when the
    /// surface policy is disabled. Internal: exposed to the test pack so
    /// "the cache is off when the surface is" is asserted, not assumed.
    member internal _.SurfaceCache: FactSurfaceCache option = surfaceCache

    /// Registry-less construction — the pre-566 shape, byte-for-byte.
    new(storage: IBlobStorage, events: IEventStore, clock: unit -> DateTime) =
        BlobFactStore(storage, events, None, clock)

    interface IFactStore with

        // ── Phase 797 — the request-path form. Each typed member is the
        // string member over the resolved scope's shard key; the store
        // keys on nothing else, so the two forms cannot diverge. ──

        member this.Assert(scope: ResolvedScope, draft: FactDraft) : Async<Result<Fact, string>> =
            (this :> IFactStore).Assert(scope.ScopeId, draft)

        member this.AssertBatch
            (scope: ResolvedScope, drafts: FactDraft list)
            : Async<Result<BatchAssertReceipt, string>> =
            (this :> IFactStore).AssertBatch(scope.ScopeId, drafts)

        member this.Get(scope: ResolvedScope, factId: string) : Async<Fact option> =
            (this :> IFactStore).Get(scope.ScopeId, factId)

        member this.Query(scope: ResolvedScope, query: FactQuery) : Async<Fact list> =
            (this :> IFactStore).Query(scope.ScopeId, query)

        member this.QueryWithCompetition(scope: ResolvedScope, query: FactQuery) : Async<FactWithCompetition list> =
            (this :> IFactStore).QueryWithCompetition(scope.ScopeId, query)

        member this.QuerySupersessionChain(scope: ResolvedScope, factId: string) : Async<Fact list> =
            (this :> IFactStore).QuerySupersessionChain(scope.ScopeId, factId)

        member this.QueryPopulation
            (scope: ResolvedScope, query: PopulationQuery)
            : Async<Result<PopulationResult, string>> =
            (this :> IFactStore).QueryPopulation(scope.ScopeId, query)

        member _.Assert(scopeId: string, draft: FactDraft) : Async<Result<Fact, string>> = async {
            try
                // Phase 704 — a batch of one. The content address, the
                // idempotency test, the lineage-head lookup, the `AsOf`
                // rule and the write all live in `writeDrafts`; what stays
                // here is the part that is genuinely scalar — the per-fact
                // audit shape, which the batch path deliberately does not
                // emit.
                let! written = writeDrafts scopeId [ draft ]

                match written with
                | Error e -> return Error e
                | Ok [ { Written = Some fact } ] ->
                    // Audit (GP 6): a FactAsserted event, and — when it
                    // superseded a predecessor — the supersession edge.
                    let assertedPayload: FactAssertedEvent = {
                        FactId = fact.FactId
                        Subject = subjectString fact.Subject
                        Metric = fact.Metric.Value
                        Method = Fact.methodIdentity fact.Method
                        Disclosure = disclosureString fact.Disclosure
                        AsOf = fact.AsOf
                    }

                    do!
                        writeEvent
                            scopeId
                            fact.AsOf
                            FactEvents.AssertedType
                            (JsonSerializer.Serialize(assertedPayload, jsonOptions))

                    match fact.Supersedes with
                    | Some supersededId ->
                        let supersededPayload: FactSupersededEvent = {
                            NewFactId = fact.FactId
                            SupersededFactId = supersededId
                            Subject = subjectString fact.Subject
                            Metric = fact.Metric.Value
                            AsOf = fact.AsOf
                        }

                        do!
                            writeEvent
                                scopeId
                                fact.AsOf
                                FactEvents.SupersededType
                                (JsonSerializer.Serialize(supersededPayload, jsonOptions))
                    | None -> ()

                    // Phase 702 — fold the new head into the derived
                    // read model, in the same logical operation. The
                    // fact is already durable; this cannot fail the
                    // assert (see `maintainSurface`).
                    do! maintainSurface scopeId fact

                    return Ok fact
                | Ok [ { FactId = factId; Written = None } ] ->
                    // Idempotent (law L2): an identical tuple already
                    // stored is a no-op — return it unchanged, no new
                    // write, no audit (nothing changed state).
                    let! existing = load scopeId factId

                    match existing with
                    | Some fact -> return Ok fact
                    | None ->
                        // The census named this id and the blob will not
                        // read back. Reporting the corrupt store beats
                        // silently overwriting it with a fact this call
                        // happens to be able to reconstruct.
                        return Error(sprintf "fact store read failed: %s is in the census but unreadable" factId)
                | Ok other ->
                    return Error(sprintf "fact store assert failed: %d dispositions for one draft" other.Length)
            with ex ->
                return Error(sprintf "fact store assert failed: %s" ex.Message)
        }

        member _.AssertBatch(scopeId: string, drafts: FactDraft list) : Async<Result<BatchAssertReceipt, string>> = async {
            try
                // Pre-flight over the WHOLE batch, before the first write:
                // the batch is the atom the producer retries, so a batch
                // that cannot be asserted must decide that before it has
                // written anything (task 704.B).
                let offenders =
                    drafts
                    |> List.mapi (fun index draft -> index, draft, FactDraft.defects draft)
                    |> List.filter (fun (_, _, defects) -> not (List.isEmpty defects))

                if not (List.isEmpty offenders) then
                    return Error(renderOffenders (List.length drafts) offenders)
                elif List.isEmpty drafts then
                    return Ok BatchAssertReceipt.empty
                else
                    let! written = writeDrafts scopeId drafts

                    match written with
                    | Error e -> return Error e
                    | Ok dispositions ->
                        let receipt =
                            dispositions
                            |> List.map (fun d -> d.Outcome, d.FactId)
                            |> BatchAssertReceipt.ofDispositions

                        let facts = dispositions |> List.choose _.Written

                        // The batch's transaction time: the latest `AsOf`
                        // it stamped, so the audit row sorts after every
                        // fact it reports. An all-idempotent batch stamped
                        // none, so it is timed by the clock.
                        let asOf =
                            match facts with
                            | [] -> clock().ToUniversalTime()
                            | _ -> facts |> List.map _.AsOf |> List.max

                        // Task 704.C — ONE summarised audit event per
                        // batch, carrying the receipt. This fires even for
                        // an all-idempotent batch, where the scalar path
                        // writes nothing: "the population re-ran and
                        // nothing moved" is a different claim from "no
                        // fact changed", and it is the one a producer
                        // needs to be able to prove.
                        let payload: FactBatchAssertedEvent = { Receipt = receipt; AsOf = asOf }

                        do!
                            writeEvent
                                scopeId
                                asOf
                                FactEvents.BatchAssertedType
                                (JsonSerializer.Serialize(payload, jsonOptions))

                        // Phase 702 — one fold per touched metric, through
                        // the same seam the scalar path uses.
                        do! maintainSurfaceBatch scopeId facts

                        return Ok receipt
            with ex ->
                return Error(sprintf "fact store batch assert failed: %s" ex.Message)
        }

        member _.Get(scopeId: string, factId: string) : Async<Fact option> = load scopeId factId

        member _.Query(scopeId: string, query: FactQuery) : Async<Fact list> = async {
            let! _, listing = runQuery scopeId query
            return listing
        }

        member _.QueryWithCompetition(scopeId: string, query: FactQuery) : Async<FactWithCompetition list> = async {
            let! heads, listing = runQuery scopeId query

            return
                listing
                |> List.map (fun f -> {
                    Fact = f
                    CompetingMethods = competingMethods heads f
                })
        }

        member _.QueryPopulation(scopeId: string, query: PopulationQuery) : Async<Result<PopulationResult, string>> =
            runPopulation scopeId query

        member _.QuerySupersessionChain(scopeId: string, factId: string) : Async<Fact list> = async {
            let! target = load scopeId factId

            match target with
            | None -> return []
            | Some f ->
                let key = lineageKeyOf f

                // Phase 890 — the lineage's own facts through the index when
                // it is complete; the enumeration (repairing an incomplete
                // index) otherwise. Either way the lineage key itself makes
                // the final cut, over facts in census order.
                let! facts =
                    if not indexOptions.Enabled then
                        loadAll scopeId
                    else
                        async {
                            let! names = storage.List(scopeId, factsPrefix)
                            let! index = readIndex scopeId names

                            match index with
                            | Complete(leaves, census) ->
                                return!
                                    lineageLeaves leaves f.Subject f.Metric f.Period f.Method
                                    |> admittedNames census
                                    |> loadNames scopeId
                            | Incomplete indexed ->
                                let! all = loadNames scopeId names
                                do! repairIndex scopeId all indexed
                                return all
                            | NotConsulted -> return! loadNames scopeId names
                        }

                return facts |> List.filter (fun g -> lineageKeyOf g = key) |> List.sortBy _.AsOf
        }

/// Construction for `BlobFactStore`.
module BlobFactStore =

    /// Create a `BlobFactStore` over the given blob backend, emitting
    /// audit events into `events` (the `IEventStore` under the reserved
    /// `_facts` source module). Transaction time is `DateTime.UtcNow`.
    /// Registry-less: method-less queries surface every competing head
    /// (use `createWithRegistry` for Phase 566 canonical-method selection).
    let create (storage: IBlobStorage) (events: IEventStore) : IFactStore =
        BlobFactStore(storage, events, None, (fun () -> DateTime.UtcNow)) :> IFactStore

    /// `create` with an explicit clock (test seam / deterministic
    /// transaction time for `AsOf` reconstruction).
    let createWithClock (storage: IBlobStorage) (events: IEventStore) (clock: unit -> DateTime) : IFactStore =
        BlobFactStore(storage, events, None, clock) :> IFactStore

    /// `create` with the metric registry (Phase 566): a metric whose
    /// registration declares a `CanonicalMethod` resolves method-less
    /// queries to the canonical lineage's head among the competitors.
    /// `registry = None` — and any metric with no declaration — preserves
    /// the pre-566 behaviour byte-for-byte (GP 11).
    let createWithRegistry
        (storage: IBlobStorage)
        (events: IEventStore)
        (registry: Grounding.IMetricRegistry option)
        : IFactStore =
        BlobFactStore(storage, events, registry, (fun () -> DateTime.UtcNow)) :> IFactStore

    /// `createWithRegistry` with an explicit clock (test seam /
    /// deterministic transaction time for `AsOf` reconstruction).
    let createWithRegistryAndClock
        (storage: IBlobStorage)
        (events: IEventStore)
        (registry: Grounding.IMetricRegistry option)
        (clock: unit -> DateTime)
        : IFactStore =
        BlobFactStore(storage, events, registry, clock) :> IFactStore

    /// `createWithRegistryAndClock` with an explicit Phase 702 metric-
    /// surface policy. The population read's answers do not depend on it —
    /// the surface and the enumeration are held byte-equal by the shared
    /// decidable pipeline — so this chooses *how* the read executes, not
    /// what it returns: `FactSurfaceOptions.disabled` for the pre-702
    /// enumeration exactly, `always` to index at every size,
    /// `defaults` (already in force via the other factories) to index
    /// above the size at which enumeration stops being interactive.
    let createWithSurface
        (storage: IBlobStorage)
        (events: IEventStore)
        (registry: Grounding.IMetricRegistry option)
        (clock: unit -> DateTime)
        (surface: FactSurfaceOptions)
        : IFactStore =
        BlobFactStore(storage, events, registry, clock, surface) :> IFactStore

    /// `createWithSurface` with an explicit Phase 890 point-read index
    /// policy as well. Like the surface policy, it chooses how reads
    /// execute and never what they return: `FactIndexOptions.disabled` for
    /// the pre-890 enumeration exactly, `always` to index at every size,
    /// `defaults` (already in force via the other factories) to index from
    /// the size at which a whole-scope read stops being cheap.
    let createWithIndex
        (storage: IBlobStorage)
        (events: IEventStore)
        (registry: Grounding.IMetricRegistry option)
        (clock: unit -> DateTime)
        (surface: FactSurfaceOptions)
        (index: FactIndexOptions)
        : IFactStore =
        BlobFactStore(storage, events, registry, clock, surface, index) :> IFactStore