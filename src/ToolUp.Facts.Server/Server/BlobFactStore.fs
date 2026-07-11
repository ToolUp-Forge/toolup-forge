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

/// Blob-backed default `IFactStore`. Construct via `BlobFactStore.create`.
type BlobFactStore(storage: IBlobStorage, events: IEventStore, clock: unit -> DateTime) =

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
            let! all = loadAll scopeId
            let t = query.AsOf |> Option.defaultValue (clock().ToUniversalTime())

            // Clause filters first (subject / metric / method / period).
            let filtered =
                all
                |> List.filter (fun f ->
                    (query.Subject |> Option.forall (fun s -> s = f.Subject))
                    && (query.Metric |> Option.forall (fun m -> m = f.Metric))
                    && (query.Method
                        |> Option.forall (fun m -> Fact.methodIdentity m = Fact.methodIdentity f.Method))
                    && (query.PeriodOverlaps |> Option.forall (fun p -> periodsOverlap p f.Period)))

            // Then bitemporal visibility (law L4), unless the caller asked
            // for the full history.
            let visible =
                if query.IncludeSuperseded then
                    filtered |> List.filter (fun f -> f.AsOf <= t)
                else
                    visibleAt t filtered

            return
                visible
                |> List.sortBy (fun f -> f.Subject.Hierarchy, f.Metric.Value, f.Period.From)
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
    let create (storage: IBlobStorage) (events: IEventStore) : IFactStore =
        BlobFactStore(storage, events, (fun () -> DateTime.UtcNow)) :> IFactStore

    /// `create` with an explicit clock (test seam / deterministic
    /// transaction time for `AsOf` reconstruction).
    let createWithClock (storage: IBlobStorage) (events: IEventStore) (clock: unit -> DateTime) : IFactStore =
        BlobFactStore(storage, events, clock) :> IFactStore