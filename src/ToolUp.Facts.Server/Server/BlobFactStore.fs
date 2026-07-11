// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
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
/// byte-for-byte, GP 11).
type BlobFactStore
    (storage: IBlobStorage, events: IEventStore, registry: Grounding.IMetricRegistry option, clock: unit -> DateTime) =

    static let jsonOptions = FableConverters.create ()

    // One blob per fact under the scope's `_facts/` prefix.
    let factsPrefix = "_facts/"
    let blobName (factId: string) = sprintf "%s%s.json" factsPrefix factId

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
    let visibleAt (t: DateTime) (all: Fact list) : Fact list =
        let byT = all |> List.filter (fun f -> f.AsOf <= t)

        byT
        |> List.filter (fun f -> not (byT |> List.exists (fun g -> g.Supersedes = Some f.FactId)))

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
            heads
            |> List.groupBy competitionKey
            |> List.collect (fun (_, group) ->
                match group with
                | []
                | [ _ ] -> group
                | contested ->
                    let canonical =
                        reg.TryGetMetric (List.head contested).Metric.Value
                        |> Option.bind _.CanonicalMethod

                    match canonical with
                    | None -> contested
                    | Some selector ->
                        match
                            contested
                            |> List.filter (fun f ->
                                Grounding.CanonicalMethod.matches selector (Fact.methodIdentity f.Method))
                        with
                        | [] -> contested
                        | matching -> matching)

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