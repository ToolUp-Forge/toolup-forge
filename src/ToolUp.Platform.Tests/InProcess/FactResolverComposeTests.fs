// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactResolverComposeTests

open System
open System.IO
open System.Reflection
open Expecto
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 558 — fact-resolver compose wiring ────────────────────────
//
// The concrete `IFactStore`-backed `IFactResolver` (closing the Phase
// 522.B seam) and its compose pickup. Covered here: the clause →
// `FactQuery` mapping over the real store, registry-driven freshness
// (`Grounding.StalenessPolicy` via `Freshness.derive`) + the canonical
// `DisplayFormat` rendering, the supersession pointer on stale heads,
// store-boundary scope filtering (GP 4), the one-knob DI registration
// (`FactsCompose.withFactStore` registers store + gate + resolver
// together), and the composed Stage-1 loop: a store-asserted fact answers
// a fact-clause retrieval as the top source with disclosure filtering
// armed, while a `NoFactStore` deployment retrieves byte-identically.

// ── Shared harness ────────────────────────────────────────────────

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft (metric: string) (period: TemporalExtent) (inputHashes: string list) (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ "acme" ]
    }
    Metric = MetricRef metric
    Value = Scalar value
    Period = period
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = inputHashes
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

let private assertFact (store: IFactStore) (scope: string) (d: FactDraft) : Fact =
    match store.Assert(scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// A real (Phase 519) registry carrying one declared metric.
let private registryWith (staleness: StalenessPolicy) (displayFormat: string) : IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "TestModule"
            Definition = {
                Id = "revenue"
                Name = "Revenue"
                Unit = "GBP"
                Dimensionality = "currency"
                Direction = HigherIsBetter
                DisplayFormat = displayFormat
                Staleness = staleness
                ProducingOperation = None
                CanonicalMethod = None
                RecomputePolicy = None
                RollUp = None
                Context = None
            }
        }
    ] []

let private clauseFor (metric: string) : FactClause = {
    SubjectHierarchy = "brand"
    SubjectPath = [ "acme" ]
    Metric = metric
    PeriodFrom = None
    PeriodTo = None
    AsOf = None
}

let private newFactStore () =
    BlobFactStore.create (InMemoryBlobStorage()) (InMemoryEventStore.InMemoryEventStore())

// ── The resolver over the real store (558.A) ──────────────────────

let resolverTests =
    testList "Phase 558 FactStoreFactResolver" [

        testCaseAsync "a current head resolves fresh, rendered under the metric's DisplayFormat"
        <| async {
            let store = newFactStore ()
            let scope = newScope ()
            let fact = assertFact store scope (draft "revenue" q2 [ "h1" ] 21800m)

            let resolver =
                FactStoreFactResolver.create store (Some(registryWith UntilSuperseded "N0"))

            let! resolved = resolver.Resolve(scope, clauseFor "revenue")

            match resolved with
            | [ r ] ->
                Expect.equal r.FactId fact.FactId "the stored fact's content-addressed id"
                Expect.equal r.Rendering "21,800" "rendered under the declared N0 format"
                Expect.equal r.Freshness FactFresh "a current head is fresh under UntilSuperseded"
                Expect.isNone r.SupersededBy "no successor exists"
                Expect.equal r.Metric "revenue" "the registered metric id rides the projection"
            | other -> failtestf "expected exactly one resolved fact, got %A" other
        }

        testCaseAsync "an AsOf reconstruction of a since-corrected head is stale with the supersession pointer"
        <| async {
            let store = newFactStore ()
            let scope = newScope ()
            // Assert, then supersede within the lineage (same key, new input).
            let original = assertFact store scope (draft "revenue" q2 [ "h1" ] 19000m)
            let successor = assertFact store scope (draft "revenue" q2 [ "h2" ] 21800m)
            Expect.equal successor.Supersedes (Some original.FactId) "harness sanity: one lineage"

            let resolver =
                FactStoreFactResolver.create store (Some(registryWith UntilSuperseded "N0"))

            // Reconstruct "what we knew" at the original's transaction time.
            let! resolved =
                resolver.Resolve(
                    scope,
                    {
                        clauseFor "revenue" with
                            AsOf = Some original.AsOf
                    }
                )

            match resolved with
            | [ r ] ->
                Expect.equal r.FactId original.FactId "the head visible at AsOf is the original"

                Expect.equal
                    r.Freshness
                    (FactStale(original.AsOf.ToUniversalTime().ToString "o"))
                    "superseded ⇒ stale under UntilSuperseded, stamped per Freshness.derive"

                Expect.equal r.SupersededBy (Some successor.FactId) "the supersession pointer names the correction"
                Expect.equal r.Rendering "19,000" "the reconstructed value, not the successor's"
            | other -> failtestf "expected the superseded head, got %A" other

            // The same clause with no AsOf resolves the current head, fresh.
            let! current = resolver.Resolve(scope, clauseFor "revenue")

            Expect.equal
                (current |> List.map (fun r -> r.FactId, r.Freshness))
                [ successor.FactId, FactFresh ]
                "the live view is the fresh successor"
        }

        testCaseAsync "FreshFor staleness: a still-current head past its window goes stale at window expiry"
        <| async {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let assertedAt = DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)

            let store =
                BlobFactStore.createWithClock (InMemoryBlobStorage()) events (fun () -> assertedAt)

            let scope = newScope ()
            let fact = assertFact store scope (draft "revenue" q2 [ "h1" ] 100m)

            let registry = Some(registryWith (FreshFor(TimeSpan.FromHours 1.0)) "")

            // Inside the window: fresh.
            let insideWindow =
                FactStoreFactResolver.createWithClock store registry (fun () -> assertedAt.AddMinutes 30.0)

            let! fresh = insideWindow.Resolve(scope, clauseFor "revenue")
            Expect.equal (fresh |> List.map _.Freshness) [ FactFresh ] "fresh inside the window"

            // Past the window: stale since AsOf + window, with no successor.
            let pastWindow =
                FactStoreFactResolver.createWithClock store registry (fun () -> assertedAt.AddHours 2.0)

            let! stale = pastWindow.Resolve(scope, clauseFor "revenue")

            match stale with
            | [ r ] ->
                Expect.equal
                    r.Freshness
                    (FactStale((fact.AsOf.AddHours 1.0).ToUniversalTime().ToString "o"))
                    "stale since the window expiry instant"

                Expect.isNone r.SupersededBy "age-stale, not superseded — no pointer"
            | other -> failtestf "expected one stale head, got %A" other
        }

        testCaseAsync "an unregistered metric defaults to UntilSuperseded freshness + verbatim rendering"
        <| async {
            let store = newFactStore ()
            let scope = newScope ()
            assertFact store scope (draft "margin" q2 [ "h1" ] 12.5m) |> ignore

            // No registry composed at all — the grounding-free deployment.
            let resolver = FactStoreFactResolver.create store None
            let! resolved = resolver.Resolve(scope, clauseFor "margin")

            Expect.equal
                (resolved |> List.map (fun r -> r.Rendering, r.Freshness))
                [ "12.5", FactFresh ]
                "verbatim rendering; current head fresh under the default policy"
        }

        testCaseAsync "the period clause restricts to overlapping facts only"
        <| async {
            let store = newFactStore ()
            let scope = newScope ()

            let q1: TemporalExtent = {
                From = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                To = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                Label = Some "Q1-2026"
            }

            let q1Fact = assertFact store scope (draft "revenue" q1 [ "h1" ] 100m)
            assertFact store scope (draft "revenue" q2 [ "h2" ] 200m) |> ignore

            let resolver = FactStoreFactResolver.create store None

            let! resolved =
                resolver.Resolve(
                    scope,
                    {
                        clauseFor "revenue" with
                            PeriodFrom = Some q1.From
                            PeriodTo = Some q1.To
                    }
                )

            Expect.equal (resolved |> List.map _.FactId) [ q1Fact.FactId ] "only the Q1-overlapping fact resolves"
        }

        testCaseAsync "scope filtering is pinned at the store boundary (GP 4): another scope's facts are unreachable"
        <| async {
            let store = newFactStore ()
            let scopeA = newScope ()
            let scopeB = newScope ()
            assertFact store scopeA (draft "revenue" q2 [ "h1" ] 100m) |> ignore

            let resolver = FactStoreFactResolver.create store None

            let! own = resolver.Resolve(scopeA, clauseFor "revenue")
            Expect.equal (List.length own) 1 "resolvable in its own scope"

            let! foreign = resolver.Resolve(scopeB, clauseFor "revenue")
            Expect.isEmpty foreign "structurally unreachable from another scope"
        }
    ]

// ── One-knob DI registration (558.B) ──────────────────────────────

/// Build the fact-tier DI exactly the way a composed deployment does:
/// `FactsCompose.withFactStore` under the given knob, its service config
/// run over a substrate-seeded collection. Returns the built provider.
let private builtProviderUnder (knob: FactStoreMode) : ServiceProvider =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = knob
                }
        }
        |> FactsCompose.withFactStore

    let services = ServiceCollection()

    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    match app.Extensions.ServiceConfig with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    services.BuildServiceProvider()

let registrationTests =
    testList "Phase 558 one-knob registration" [

        test "EnabledFactStore registers the resolver alongside the store + gate — one knob, never three" {
            let sp = builtProviderUnder EnabledFactStore

            Expect.isFalse (isNull (box (sp.GetService<IFactStore>()))) "IFactStore resolves"
            Expect.isFalse (isNull (box (sp.GetService<IFactDisclosureGate>()))) "IFactDisclosureGate resolves"
            Expect.isFalse (isNull (box (sp.GetService<IFactResolver>()))) "IFactResolver resolves"
        }

        test "NoFactStore registers none of the fact tier" {
            let sp = builtProviderUnder NoFactStore

            Expect.isTrue (isNull (box (sp.GetService<IFactResolver>()))) "no resolver"
            Expect.isTrue (isNull (box (sp.GetService<IFactDisclosureGate>()))) "no gate"
        }
    ]

// ── The composed Stage-1 loop (558.C/D) ───────────────────────────

let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

type private ConstantEmbedder() =
    interface IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }

        member _.GenerateEmbeddings texts =
            batchedFallback (fun _ -> async { return unitVec }) texts

        member _.Dimensions = 8
        member _.ProviderId = "test"
        member _.ModelId = "constant-v1"

let private newVectorStore () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-fact-resolver-compose-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(storage, logger = silentLogger, flushIntervalMs = 60000)

    store :> IVectorStore, store :> IDisposable

/// Mirror of `RAGCompose`'s Phase 558 pickup: resolve the fact tier from
/// the built provider when present, and hand the *optional* results to the
/// pipeline constructor — absent registrations ⇒ the args are omitted.
let private pipelineOver (vectorStore: IVectorStore) (sp: ServiceProvider) : IRetrievalPipeline =
    let factResolverOpt =
        match sp.GetService(typeof<IFactResolver>) with
        | :? IFactResolver as r -> Some r
        | _ -> None

    let disclosureGateOpt =
        match sp.GetService(typeof<IFactDisclosureGate>) with
        | :? IFactDisclosureGate as g -> Some g
        | _ -> None

    new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
        vectorStore,
        ConstantEmbedder(),
        ?factResolver = factResolverOpt,
        ?disclosureGate = disclosureGateOpt
    )
    :> IRetrievalPipeline

/// A storage scope as the scope-resolution middleware would produce it.
let private storageScope (scopeId: string) : StorageScope = {
    ScopeId = scopeId
    Container = "container-" + scopeId
    Persist = true
}

/// Phase 820 — the request carries the scope the platform minted for it;
/// the pipeline reads the fact tier in that scope and derives none.
let private clausedRequest = {
    RetrievalRequest.create "what was revenue?" [ User "u" ] 10 Interleaved with
        FactClause = Some(clauseFor "revenue")
        FactScope = ScopeResolution.ofStorageScope (storageScope "u")
}

let private queryFactsArgs =
    """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue"}"""

let composedLoopTests =
    testList "Phase 558 composed Stage-1 loop" [

        testCaseAsync
            "EnabledFactStore + RAG: a store-asserted fact answers the clause as the top source, freshness-stamped, with the gate armed"
        <| async {
            let vectorStore, dispose = newVectorStore ()

            try
                do!
                    vectorStore.Upsert (User "u") "c1" unitVec {
                        Content = "revenue commentary prose"
                        Metadata = Map.empty
                    }

                let sp = builtProviderUnder EnabledFactStore
                let factStore = sp.GetRequiredService<IFactStore>()

                // The pipeline hands the resolver the request's resolved
                // fact scope — `"u"` here — so the store is seeded under it.
                let surfaceable = assertFact factStore "u" (draft "revenue" q2 [ "h1" ] 21800m)

                // A competing Internal fact (different method ⇒ both heads
                // current) that the paired gate must keep out of egress.
                let internalFact =
                    assertFact factStore "u" {
                        draft "revenue" q2 [ "h2" ] 19999m with
                            Method = Computed("intermediate", "1", "p1")
                            Disclosure = Internal
                    }

                let pipeline = pipelineOver vectorStore sp

                let! results = pipeline.Retrieve clausedRequest (AccessContext.unrestricted (AuthenticatedUser "u"))

                match results with
                | top :: rest ->
                    Expect.equal
                        (top.Metadata.TryFind ChunkMetadata.FactIdKey)
                        (Some surfaceable.FactId)
                        "the store-asserted fact is the top source"

                    Expect.equal top.Content "21800" "the real store's value, rendered (verbatim — no registry)"

                    Expect.equal
                        (top.Metadata.TryFind ChunkMetadata.FactFreshnessKey)
                        (Some "Fresh")
                        "freshness-stamped per the staleness policy"

                    Expect.equal (rest |> List.map _.ChunkId) [ "c1" ] "the similarity chunk follows the fact"
                | [] -> failtest "expected a fact + chunk"

                // The Internal fact is absent — not annotated (Phase 525).
                let surfacedIds =
                    results |> List.choose (fun m -> m.Metadata.TryFind ChunkMetadata.FactIdKey)

                Expect.isFalse
                    (surfacedIds |> List.contains internalFact.FactId)
                    "the Internal fact never crosses the retrieval egress door"

                Expect.isFalse
                    (results |> List.exists (fun m -> m.Content.Contains "19999"))
                    "the denied value is nowhere in the result set"
            finally
                dispose.Dispose()
        }

        testCaseAsync "NoFactStore ⇒ retrieval is byte-identical to a fact-less pipeline (GP 11 / GP 13)"
        <| async {
            let vectorStore, dispose = newVectorStore ()

            try
                do!
                    vectorStore.Upsert (User "u") "c1" unitVec {
                        Content = "a chunk"
                        Metadata = Map.empty
                    }

                let ctx = AccessContext.unrestricted (AuthenticatedUser "u")

                // The NoFactStore composition: withFactStore is a no-op, so
                // the pickup resolves nothing and the ctor args are omitted.
                let composed = pipelineOver vectorStore (builtProviderUnder NoFactStore)

                // A pipeline that never had fact wiring at all.
                let plain =
                    new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(vectorStore, ConstantEmbedder())
                    :> IRetrievalPipeline

                let! composedResults = composed.Retrieve clausedRequest ctx
                let! plainResults = plain.Retrieve clausedRequest ctx

                Expect.equal composedResults plainResults "byte-identical results, clause and all"
                Expect.equal (composedResults |> List.map _.ChunkId) [ "c1" ] "chunk-only retrieval"
            finally
                dispose.Dispose()
        }
    ]

// ── One scope per request across RAG and Facts (Phase 820) ────────
//
// The divergence the phase retires: an access context whose team pointer
// and user id would have derived one fact shard (team-else-user) while the
// platform's scope resolution resolved the request to another. Before 820
// the retrieval turn read the derived shard and `query_facts` read the
// resolved one — two shards for one request. Now both read the scope
// `ScopeResolution.forRequest` returns for the request, so they agree.

let divergenceTests =
    testList "Phase 820 one scope per request" [

        testCaseAsync
            "a request whose team pointer and user id derive a different shard reads ONE shard through retrieval and query_facts"
        <| async {
            let vectorStore, dispose = newVectorStore ()

            try
                let sp = builtProviderUnder EnabledFactStore
                let factStore = sp.GetRequiredService<IFactStore>()

                // The resolver minted the user's own scope; the access
                // context also carries an active-team pointer, so the
                // retired team-else-user rule would have read the team's
                // shard instead.
                let resolvedId = "alice"
                let teamPointer = "team-pointer"

                let ctx = {
                    AccessContext.unrestricted (AuthenticatedUser resolvedId) with
                        TeamId = Some teamPointer
                }

                let inResolved =
                    assertFact factStore resolvedId (draft "revenue" q2 [ "h-resolved" ] 21800m)

                let inDerived =
                    assertFact factStore teamPointer (draft "revenue" q2 [ "h-derived" ] 99999m)

                // The request as the middleware leaves it: the minted scope
                // recorded on the HttpContext, read back through the one
                // function both doors use.
                let http = DefaultHttpContext()
                http.RequestServices <- sp
                ScopeResolution.remember http (storageScope resolvedId) |> ignore
                http.Items["ToolUp.UserId"] <- box resolvedId

                // The retrieval turn.
                let request = {
                    RetrievalRequest.create "what was revenue?" [ User resolvedId ] 10 Interleaved with
                        FactClause = Some(clauseFor "revenue")
                        FactScope = ScopeResolution.forRequest http
                }

                let! results = (pipelineOver vectorStore sp).Retrieve request ctx

                let retrievedFactIds =
                    results |> List.choose (fun m -> m.Metadata.TryFind ChunkMetadata.FactIdKey)

                Expect.equal retrievedFactIds [ inResolved.FactId ] "the retrieval turn reads the resolved shard only"

                // The fact door, on the same request.
                let! toolJson = FactQueryTool.execute (http :> HttpContext) queryFactsArgs

                let toolFactIds =
                    let root = (JsonDocument.Parse toolJson).RootElement

                    root.GetProperty("facts").EnumerateArray()
                    |> Seq.map (fun f -> f.GetProperty("factId").GetString())
                    |> List.ofSeq

                Expect.equal toolFactIds [ inResolved.FactId ] "query_facts reads the resolved shard only"
                Expect.equal retrievedFactIds toolFactIds "both doors read ONE shard for the request"

                Expect.isFalse
                    (retrievedFactIds @ toolFactIds |> List.contains inDerived.FactId)
                    "the shard the retired derivation named is read by neither door"
            finally
                dispose.Dispose()
        }

        testCaseAsync "a retrieval built without a request reads the anonymous scope, never a derived one"
        <| async {
            let vectorStore, dispose = newVectorStore ()

            try
                let sp = builtProviderUnder EnabledFactStore
                let factStore = sp.GetRequiredService<IFactStore>()
                let userFact = assertFact factStore "u" (draft "revenue" q2 [ "h-user" ] 21800m)

                let request = {
                    RetrievalRequest.create "what was revenue?" [ User "u" ] 10 Interleaved with
                        FactClause = Some(clauseFor "revenue")
                }

                Expect.isTrue request.FactScope.IsAnonymous "RetrievalRequest.create defaults to the anonymous scope"

                let! results =
                    (pipelineOver vectorStore sp).Retrieve request (AccessContext.unrestricted (AuthenticatedUser "u"))

                Expect.isFalse
                    (results
                     |> List.exists (fun m -> m.Metadata.TryFind ChunkMetadata.FactIdKey = Some userFact.FactId))
                    "the user's shard is not inferred from the access context"
            finally
                dispose.Dispose()
        }
    ]

// ── The source says so (Phase 820, widening the Phase 797 guard) ──
//
// The 797 source guard pins the three fact tools; the retrieval pipeline
// is a fourth reader of the fact tier and is pinned here until the guard
// becomes a discovery over every door. The spellings are assembled at
// runtime so this file does not match itself.

/// The spellings of a fact scope derived from the access context.
let private derivationSpellings = [
    "Option.defaultValue ctx." + "UserId", "a team-else-user fallback onto the access context's user id"
    "let factScope" + "Id =", "a locally-derived fact scope id"
    "ctx.TeamId |> " + "Option.defaultValue", "a fact scope read off the access context's team pointer"
]

/// Pure classifier: the derivations a source carries, as (needle, why).
let private derivations (source: string) : (string * string) list =
    derivationSpellings |> List.filter (fun (needle, _) -> source.Contains needle)

let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private pipelineSourceGuardTests =
    testList "Phase 820 the retrieval pipeline derives no fact scope" [

        test "the classifier fires on the pre-820 derivation (go-red)" {
            let planted =
                "                    let factScope"
                + "Id = ctx.TeamId |> "
                + "Option.defaultValue ctx."
                + "UserId"

            Expect.equal (List.length (derivations planted)) 3 "every spelling of the derivation is caught"
        }

        test "the classifier is quiet on the post-820 read" {
            Expect.isEmpty
                (derivations "                    let factScope = request.FactScope")
                "the request's scope is not an offence"
        }

        test "RetrievalPipeline.fs reads the request's resolved scope and carries no derivation" {
            let path =
                Path.Combine(repoRoot (), "src", "ToolUp.RAG.Server", "Server", "RetrievalPipeline.fs")

            Expect.isTrue (File.Exists path) "RetrievalPipeline.fs exists"
            let source = File.ReadAllText path

            Expect.stringContains source "request.FactScope" "the fact stage takes the request's resolved scope"

            Expect.stringContains
                source
                "gate.Check(factScope, ctx.UserId, FactRetrieval"
                "the egress door is asked through the typed overload"

            match derivations source with
            | [] -> ()
            | found ->
                failtestf
                    "RetrievalPipeline.fs derives a fact scope:\n%s"
                    (found |> List.map (fun (_, why) -> "  - " + why) |> String.concat "\n")
        }

        test "the prompt path carries the request's minted scope onto the retrieval request" {
            let path =
                Path.Combine(repoRoot (), "src", "ToolUp.RAG.Server", "Server", "RAGPromptBuilder.fs")

            Expect.isTrue (File.Exists path) "RAGPromptBuilder.fs exists"

            Expect.stringContains
                (File.ReadAllText path)
                "FactScope = ctx.Scope"
                "the clause-bearing request reads in PromptContext.Scope, the scope the handler minted"
        }
    ]

let tests =
    testList "Phase 558 fact-resolver compose wiring" [
        resolverTests
        registrationTests
        composedLoopTests
        divergenceTests
        pipelineSourceGuardTests
    ]