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
// The default scans blobs under the scope's `_facts/` prefix (O(n) per
// query / assert). That is fine for the default single-backend store; a
// large deployment swaps in an indexed implementation behind the same
// `IFactStore` contract (the six-rule audit is what makes that swap
// safe). Scope isolation is structural (GP 4): the container IS the
// resolved storage scope, so one scope's facts are unreachable from
// another.
//
// **Audit (GP 6)** rides `IEventStore` under the reserved `_facts` source
// module (the `ILineageStore` pattern) — a durable, scope-isolated,
// queryable record without a core `AuditEvent` edit.

/// Blob-backed default `IFactStore`. Construct via `BlobFactStore.create`
/// (or `createWithRegistry` to enable Phase 566 canonical-method
/// selection — `registry = None` preserves the registry-less behaviour
/// byte-for-byte, GP 11; or `createWithSurface` to choose the Phase 702
/// metric-surface policy explicitly).
type BlobFactStore
    (
        storage: IBlobStorage,
        events: IEventStore,
        registry: Grounding.IMetricRegistry option,
        clock: unit -> DateTime,
        surfaceOptions: FactSurfaceOptions
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
    // policy costs one unused object per store and nothing per call.
    let surface = BlobFactSurface(storage) :> IFactSurface

    let serialise (f: Fact) : byte[] =
        JsonSerializer.Serialize(f, jsonOptions) |> Encoding.UTF8.GetBytes

    let deserialise (bytes: byte[]) : Fact =
        JsonSerializer.Deserialize<Fact>(Encoding.UTF8.GetString bytes, jsonOptions)

    // Every fact in scope, materialised. Stateless (recomputed per call).
    let loadAll (scopeId: string) : Async<Fact list> = async {
        let! names = storage.List(scopeId, factsPrefix)

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
            |> Async.Parallel

        return facts |> Array.choose id |> Array.toList
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
    let runQuery (scopeId: string) (query: FactQuery) : Async<Fact list * Fact list> = async {
        let! all = loadAll scopeId
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
        let headIds = HashSet<string>(heads |> List.map _.FactId, StringComparer.Ordinal)

        let absorbed = all |> List.map _.FactId |> List.filter (headIds.Contains >> not)

        let! r = surface.Rebuild(scopeId, metric.Value, heads, absorbed)

        return
            match r with
            | Ok snapshot -> Some snapshot
            | Error _ -> None
    }

    /// Bring a snapshot up to date against the scope's fact census, or
    /// rebuild it. `None` means "could not produce a trustworthy snapshot"
    /// — the caller enumerates, which is always available and always
    /// right.
    let reconcileSurface
        (scopeId: string)
        (metric: MetricRef)
        (names: string list)
        (existing: FactSurfaceSnapshot option)
        : Async<FactSurfaceSnapshot option> =
        async {
            let storeCount = List.length names

            match existing with
            | Some snapshot when not snapshot.Stale ->
                let folded = FactSurfaceRead.foldedCount snapshot

                if folded = storeCount then
                    // The folded set is a subset of the census by
                    // construction, so equal cardinality is equality — and
                    // the converged path never has to materialise the ids.
                    return Some snapshot
                elif folded > storeCount then
                    // Facts left the log — nothing in the store does that,
                    // so this is an out-of-band deletion (an erasure).
                    // Rebuild rather than reason about which rows survived.
                    return! rebuildSurface scopeId metric
                else
                    let missing = FactSurfaceRead.unseen snapshot (names |> List.map factIdOfBlob)

                    if List.length missing > surfaceOptions.MaxIncrementalFold then
                        return! rebuildSurface scopeId metric
                    else
                        let! fetched = missing |> List.map (load scopeId) |> Async.Parallel
                        let facts = fetched |> Array.choose id |> Array.toList

                        if List.length facts <> List.length missing then
                            return! rebuildSurface scopeId metric
                        else
                            // Ascending `AsOf` is a topological order over
                            // the supersession edges (law L3).
                            let folded =
                                facts
                                |> List.sortBy _.AsOf
                                |> List.fold (fun acc f -> FactSurfaceFold.applyFact metric.Value f acc) snapshot

                            let! _ = surface.Put(scopeId, metric.Value, folded)
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
                    |> Async.Parallel

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
                let! existing = surface.Get(scopeId, query.Metric.Value)
                let! reconciled = reconcileSurface scopeId query.Metric names existing

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
                | Ok() -> return ()
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

    /// The pre-702 four-argument shape, on the default surface policy.
    /// An explicit secondary constructor rather than an optional parameter
    /// on the primary: an optional argument folds into one widened
    /// constructor and the four-argument token disappears, which is a
    /// break for every existing caller.
    new(storage: IBlobStorage, events: IEventStore, registry: Grounding.IMetricRegistry option, clock: unit -> DateTime) =
        BlobFactStore(storage, events, registry, clock, FactSurfaceOptions.defaults)

    /// Registry-less construction — the pre-566 shape, byte-for-byte.
    new(storage: IBlobStorage, events: IEventStore, clock: unit -> DateTime) =
        BlobFactStore(storage, events, None, clock)

    interface IFactStore with

        member _.Assert(scopeId: string, draft: FactDraft) : Async<Result<Fact, string>> = async {
            try
                let inputHashes = Fact.effectiveInputHashes draft.Method draft.Evidence draft.Value

                let factId =
                    Fact.compute draft.Subject draft.Metric draft.Period draft.Method inputHashes

                // Idempotent (law L2): an identical tuple already stored is
                // a no-op — return it unchanged, no new write, no audit
                // (nothing changed state).
                let! existing = load scopeId factId

                match existing with
                | Some fact -> return Ok fact
                | None ->
                    // New fact. Derive the supersession edge: the current
                    // head of this lineage (the latest-AsOf fact sharing
                    // the lineage key) is superseded by this assertion.
                    let! all = loadAll scopeId
                    let key = Fact.lineageKey draft.Subject draft.Metric draft.Period draft.Method

                    let currentHead =
                        all
                        |> List.filter (fun f -> lineageKeyOf f = key)
                        |> List.sortByDescending _.AsOf
                        |> List.tryHead

                    // Transaction time — strictly greater than the head's,
                    // so supersession chains are acyclic by construction
                    // (law L3), even if the clock has coarse resolution.
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

                    let! writeResult = storage.Upload(scopeId, blobName factId, serialise fact)

                    match writeResult with
                    | Error e -> return Error(sprintf "fact store write failed: %s" e)
                    | Ok _ ->
                        // Audit (GP 6): a FactAsserted event, and — when it
                        // superseded a predecessor — the supersession edge.
                        let assertedPayload: FactAssertedEvent = {
                            FactId = factId
                            Subject = subjectString draft.Subject
                            Metric = draft.Metric.Value
                            Method = Fact.methodIdentity draft.Method
                            Disclosure = disclosureString draft.Disclosure
                            AsOf = asOf
                        }

                        do!
                            writeEvent
                                scopeId
                                asOf
                                FactEvents.AssertedType
                                (JsonSerializer.Serialize(assertedPayload, jsonOptions))

                        match currentHead with
                        | Some head ->
                            let supersededPayload: FactSupersededEvent = {
                                NewFactId = factId
                                SupersededFactId = head.FactId
                                Subject = subjectString draft.Subject
                                Metric = draft.Metric.Value
                                AsOf = asOf
                            }

                            do!
                                writeEvent
                                    scopeId
                                    asOf
                                    FactEvents.SupersededType
                                    (JsonSerializer.Serialize(supersededPayload, jsonOptions))
                        | None -> ()

                        // Phase 702 — fold the new head into the derived
                        // read model, in the same logical operation. The
                        // fact is already durable; this cannot fail the
                        // assert (see `maintainSurface`).
                        do! maintainSurface scopeId fact

                        return Ok fact
            with ex ->
                return Error(sprintf "fact store assert failed: %s" ex.Message)
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
                let! all = loadAll scopeId
                let key = lineageKeyOf f

                return all |> List.filter (fun g -> lineageKeyOf g = key) |> List.sortBy _.AsOf
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