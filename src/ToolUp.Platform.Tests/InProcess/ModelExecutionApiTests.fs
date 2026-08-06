// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelExecutionApiTests

open System
open System.IO
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.Tracing
open ToolUp.ModelProviders.Reference

// ─── Phase 600 — model-execution submitter API conformance ──────────────
//
// Drives the `ModelExecutionApi` handler end-to-end against the reference
// provider + the full blob-backed default stack: submit → (dispatch the
// queued item jobs) → poll outcomes → score → resolve, all through the
// wire surface with no server types on the caller's side of the
// assertions. Plus: scope isolation (GP 4), the typed-refusal mapping
// (per-substrate absence, unknown provider, invalid submission/query,
// not-found), SpecHash opacity at the API tier (the submitter-minted
// hash is stored + queried verbatim, never re-derived), and refusal
// totality (a throwing substrate surfaces as `Unexpected` data, never a
// bare wire exception).

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }
    }

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// The full default substrate stack a submitter-facing deployment
/// composes, temp-dir isolated per fixture.
type private Fixture = {
    Scheduler: IJobScheduler
    Registry: IModelRegistry
    Datasets: IDatasetStore
    Audit: RecordingAuditLog
    Providers: ModelFitProviderRegistry
}

let private freshFixture () : Fixture =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-modelexec-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let blob = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let audit = RecordingAuditLog()

    let schedRoot = Path.Combine(root, "sched")
    Directory.CreateDirectory schedRoot |> ignore
    let schedStorage = LocalFileStorage.LocalFileStorage(schedRoot) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create schedStorage eventStore

    let scheduler =
        JobScheduler.create jobStore eventStore silentChannel ServerConfig.defaults silentLogger (NoOpActivitySink())
        :> IJobScheduler

    let providers = ModelFitProviderRegistry [ ReferenceModelFitProvider.create () ]
    let registry = BlobModelRegistry.create dataObjects (audit :> IAuditLog)
    let datasets = BlobDatasetStore.create dataObjects

    // The item handler registered exactly as a consumer wires it.
    scheduler.RegisterHandler(
        ModelFitBatch.ItemHandlerName,
        ModelFitBatchItemJobHandler.create providers registry (audit :> IAuditLog) silentLogger
    )

    {
        Scheduler = scheduler
        Registry = registry
        Datasets = datasets
        Audit = audit
        Providers = providers
    }

/// An `HttpContext` carrying the fixture's substrate + the caller's
/// resolved `AccessContext` (user-scope subject — the write gate passes,
/// `configScope` resolves the user id as the scope).
let private ctxFor (fixture: Fixture) (userId: string) (withScorer: bool) : HttpContext =
    let services = ServiceCollection()

    services.AddSingleton<IJobScheduler>(fixture.Scheduler) |> ignore
    services.AddSingleton<IModelRegistry>(fixture.Registry) |> ignore
    services.AddSingleton<IDatasetStore>(fixture.Datasets) |> ignore
    services.AddSingleton<IAuditLog>(fixture.Audit :> IAuditLog) |> ignore
    services.AddSingleton<ModelFitProviderRegistry>(fixture.Providers) |> ignore

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser userId))
    |> ignore

    if withScorer then
        let scoreProviders =
            ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]

        services.AddSingleton<IModelScorer>(
            ModelScorer.create scoreProviders fixture.Datasets (fixture.Audit :> IAuditLog) ModelScorePolicy.permissive
        )
        |> ignore

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx

/// A minimal HttpContext with ONLY an AccessContext — every substrate
/// absent (the per-substrate typed-refusal fixture).
let private bareCtx (userId: string) : HttpContext =
    let services = ServiceCollection()

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser userId))
    |> ignore

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx

let private inputSchema: DatasetSchema = {
    Columns = [
        {
            Name = "region"
            DType = DatasetDType.Categorical
            Nullable = false
            Role = DatasetColumnRole.PanelUnit
        }
        {
            Name = "spend"
            DType = DatasetDType.Float
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
    ]
}

let private inputRows: DatasetRow list = [
    {
        Cells = [ DatasetValue.Categorical "north"; DatasetValue.Float 100.0 ]
    }
    {
        Cells = [ DatasetValue.Categorical "south"; DatasetValue.Float 80.0 ]
    }
]

/// Seed the caller-scope input vintage.
let private seedInput (fixture: Fixture) (scopeId: string) = async {
    let! created =
        fixture.Datasets.Create(scopeId, "input", inputSchema, inputRows, "seed", Map.empty, StrictlyVersioned)

    match created with
    | Ok v -> return v
    | Error e -> return failtestf "seed failed: %s" (DatasetError.describe e)
}

/// A submission whose SpecHash is deliberately NOT any canonical hash of
/// its payload — the opacity posture exercised at the API tier.
let private submission (seed: int64) : ModelExecutionFitSubmission = {
    DatasetId = "input"
    DatasetVersion = 1
    SpecPayload = """{"opaque":"provider-spec"}"""
    SpecHash = $"submitter-minted-hash-{seed}"
    // Phase 640 — a rule identifier forge stores and never acts on. Named
    // deliberately alongside a hash that is NOT its minting, so a receiver
    // that started validating the pair would fail these tests.
    SpecHashAlgorithm = "canonical-json-sha256-v1"
    ProviderKind = ReferenceModelFitProvider.Kind
    Seed = seed
    Gates = [
        {
            Name = "mean"
            Threshold = 0.0
            Direction = "AtLeast"
        }
    ]
    SubmitterClass = SubmitterClass.Human
}

/// A submission whose single gate cannot pass — the reference provider
/// reports a finite `mean`, and nothing clears a threshold of 1e9. Used to
/// register an artifact that FAILED its declared gates, which is a
/// perfectly ordinary registered outcome (a failure is evidence) and the
/// precondition of the Phase 640 gate policy.
let private gateFailingSubmission (seed: int64) : ModelExecutionFitSubmission = {
    submission seed with
        SpecHash = $"gate-fail-hash-{seed}"
        Gates = [
            {
                Name = "mean"
                Threshold = 1e9
                Direction = "AtLeast"
            }
        ]
}

/// `ctxFor` with the Phase 640 executor policy registered — the opt-in a
/// governed deployment makes. Everything else is identical, so a test pair
/// over the two contexts isolates the policy and nothing else.
let private ctxWithGatePolicy (fixture: Fixture) (userId: string) : HttpContext =
    let services = ServiceCollection()

    services.AddSingleton<IJobScheduler>(fixture.Scheduler) |> ignore
    services.AddSingleton<IModelRegistry>(fixture.Registry) |> ignore
    services.AddSingleton<IDatasetStore>(fixture.Datasets) |> ignore
    services.AddSingleton<IAuditLog>(fixture.Audit :> IAuditLog) |> ignore
    services.AddSingleton<ModelFitProviderRegistry>(fixture.Providers) |> ignore

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser userId))
    |> ignore

    let scoreProviders =
        ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]

    services.AddSingleton<IModelScorer>(
        ModelScorer.create scoreProviders fixture.Datasets (fixture.Audit :> IAuditLog) ModelScorePolicy.permissive
    )
    |> ignore

    services.AddSingleton<ModelExecutionPolicy>(ModelExecutionPolicy.refuseGateFailures)
    |> ignore

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx

/// Await the batch's outcomes landing in the registry —
/// `ModelFitBatch.submit`'s `TriggerOnce` dispatches each item job on a
/// background async (`JobScheduler.TriggerOnce` → `Async.Start`), so the
/// test polls the registry rather than re-executing the handler itself.
let private awaitOutcomes (fixture: Fixture) (scopeId: string) (batchId: string) (expected: int) = async {
    let query = {
        ModelRegistryQuery.any with
            BatchId = Some batchId
    }

    let deadline = DateTime.UtcNow.AddSeconds 20.0
    let mutable count = 0

    while count < expected && DateTime.UtcNow < deadline do
        match! fixture.Registry.QueryPage(scopeId, query, None, 1000) with
        | Ok p -> count <- List.length p.Artifacts
        | Error _ -> ()

        if count < expected then
            do! Async.Sleep 50

    Expect.equal count expected "every dispatched item's outcome landed in the registry"
}

let private okv (label: string) =
    function
    | Ok v -> v
    | Error e -> failtestf "%s refused: %s" label (ModelExecutionRefusal.describe e)

let tests =
    testList "ModelExecutionApi" [
        testCaseAsync "end-to-end: resolve → submit → dispatch → poll outcomes → score → resolve output"
        <| async {
            let fixture = freshFixture ()
            let api = ModelExecutionApiHandler.modelExecutionApi (ctxFor fixture "op-1" true)
            let! _ = seedInput fixture "op-1"

            // Resolve the vintage to pin.
            let! resolved = api.ResolveLatestDatasetVersion "input"
            let vintage = okv "resolve" resolved
            Expect.equal vintage.Version 1 "latest input vintage resolved"
            Expect.equal vintage.RowCount 2L "row count visible to the submitter"

            // Submit a small batch.
            let batch = {
                BatchId = "wave-1"
                Items = [ submission 1L; submission 2L; submission 3L ]
            }

            let! receipt = api.SubmitFitBatch batch
            let r = okv "submit" receipt
            Expect.equal r.ItemCount 3 "receipt names the item count"
            Expect.equal (List.length r.Jobs) 3 "three job refs to poll"
            Expect.isEmpty r.EnqueueFailures "no enqueue failures"

            // Dispatch the queued items (the test stand-in for the hosted loop).
            do! awaitOutcomes fixture "op-1" "wave-1" 3

            // Poll the whole wave in one call.
            let query: ModelExecutionOutcomeQuery = {
                SpecHashes = []
                DatasetVersions = []
                Statuses = []
                BatchId = Some "wave-1"
            }

            let! outcomes = api.QueryOutcomes(query, None, 10)
            let page = okv "query" outcomes
            Expect.equal (List.length page.Outcomes) 3 "the whole wave polls in one call"
            Expect.isNone page.NextCursor "single page"

            let distinctKeys = page.Outcomes |> List.map _.CompositeKeyHash |> List.distinct

            Expect.equal (List.length distinctKeys) 3 "distinct composite keys per item"

            // SpecHash opacity at the API tier: the submitter-minted hash is
            // stored + returned verbatim (never re-derived from the payload).
            Expect.equal
                (page.Outcomes |> List.map _.SpecHash |> List.sort)
                [
                    "submitter-minted-hash-1"
                    "submitter-minted-hash-2"
                    "submitter-minted-hash-3"
                ]
                "submitter-minted spec hashes round-trip verbatim"

            // Single outcome fetch by key.
            let first = page.Outcomes.Head
            let! fetched = api.GetOutcome first.CompositeKeyHash
            Expect.equal (okv "get" fetched) first "GetOutcome returns the same wire outcome"

            Expect.isTrue
                (first.GateVerdicts |> List.forall (fun v -> v.Direction = "AtLeast"))
                "gate verdicts carry stable direction strings"

            // Score the artifact against the input vintage.
            let scoreRequest = {
                ArtifactKeyHash = first.CompositeKeyHash
                InputDatasetId = "input"
                InputVersion = 1
                OutputDatasetId = "predictions"
            }

            let! scored = api.RequestScore scoreRequest
            let output = okv "score" scored
            Expect.equal output.DatasetId "predictions" "predictions land under the requested id"
            Expect.equal output.RowCount 2L "one prediction per input row"

            // The output vintage resolves like any other.
            let! reResolved = api.ResolveDatasetVersion("predictions", output.Version)
            Expect.equal (okv "re-resolve" reResolved) output "scored output resolves through the API"
        }

        testCaseAsync "scope isolation — another principal sees nothing of the wave (GP 4)"
        <| async {
            let fixture = freshFixture ()
            let apiA = ModelExecutionApiHandler.modelExecutionApi (ctxFor fixture "op-a" false)
            let apiB = ModelExecutionApiHandler.modelExecutionApi (ctxFor fixture "op-b" false)
            let! _ = seedInput fixture "op-a"

            let! receipt =
                apiA.SubmitFitBatch {
                    BatchId = "wave-iso"
                    Items = [ submission 1L ]
                }

            let r = okv "submit" receipt
            do! awaitOutcomes fixture "op-a" "wave-iso" 1

            let query: ModelExecutionOutcomeQuery = {
                SpecHashes = []
                DatasetVersions = []
                Statuses = []
                BatchId = Some "wave-iso"
            }

            let! mine = apiA.QueryOutcomes(query, None, 10)
            Expect.equal (List.length (okv "own query" mine).Outcomes) 1 "the owner sees the outcome"

            let! theirs = apiB.QueryOutcomes(query, None, 10)
            Expect.isEmpty (okv "foreign query" theirs).Outcomes "another principal's scope is empty"

            let ownKey = (okv "own query" mine).Outcomes.Head.CompositeKeyHash

            match! apiB.GetOutcome ownKey with
            | Error(ModelExecutionRefusal.NotFound _) -> ()
            | other -> failtestf "expected NotFound across scopes; got %A" other

            match! apiB.ResolveLatestDatasetVersion "input" with
            | Error(ModelExecutionRefusal.NotFound _) -> ()
            | other -> failtestf "expected NotFound for a foreign dataset; got %A" other

            ignore r
        }

        testCaseAsync "typed refusals — absent substrates, unknown provider, invalid submissions and queries"
        <| async {
            // Every substrate absent → SubstrateDisabled naming the surface.
            let bare = ModelExecutionApiHandler.modelExecutionApi (bareCtx "op-1")

            match! bare.SubmitFit(submission 1L) with
            | Error(ModelExecutionRefusal.SubstrateDisabled "job scheduler") -> ()
            | other -> failtestf "expected SubstrateDisabled 'job scheduler'; got %A" other

            match! bare.GetOutcome "any" with
            | Error(ModelExecutionRefusal.SubstrateDisabled "model registry") -> ()
            | other -> failtestf "expected SubstrateDisabled 'model registry'; got %A" other

            match! bare.ResolveLatestDatasetVersion "input" with
            | Error(ModelExecutionRefusal.SubstrateDisabled "datasets") -> ()
            | other -> failtestf "expected SubstrateDisabled 'datasets'; got %A" other

            let fixture = freshFixture ()
            // No scorer registered in this ctx.
            let api = ModelExecutionApiHandler.modelExecutionApi (ctxFor fixture "op-1" false)

            match!
                api.RequestScore {
                    ArtifactKeyHash = "k"
                    InputDatasetId = "input"
                    InputVersion = 1
                    OutputDatasetId = "out"
                }
            with
            | Error(ModelExecutionRefusal.SubstrateDisabled "model scorer") -> ()
            | other -> failtestf "expected SubstrateDisabled 'model scorer'; got %A" other

            // Unknown provider kind — denied before any enqueue.
            match!
                api.SubmitFit {
                    submission 1L with
                        ProviderKind = "no-such-provider"
                }
            with
            | Error(ModelExecutionRefusal.UnknownProvider "no-such-provider") -> ()
            | other -> failtestf "expected UnknownProvider; got %A" other

            // Empty batch → InvalidSubmission.
            match! api.SubmitFitBatch { BatchId = "b"; Items = [] } with
            | Error(ModelExecutionRefusal.InvalidSubmission _) -> ()
            | other -> failtestf "expected InvalidSubmission for an empty batch; got %A" other

            // Malformed gate direction → InvalidSubmission.
            match!
                api.SubmitFit {
                    submission 1L with
                        Gates = [
                            {
                                Name = "mean"
                                Threshold = 0.0
                                Direction = "Sideways"
                            }
                        ]
                }
            with
            | Error(ModelExecutionRefusal.InvalidSubmission _) -> ()
            | other -> failtestf "expected InvalidSubmission for a bad gate direction; got %A" other

            // Unknown status string → InvalidQuery; bad limit → InvalidQuery.
            let anyQuery: ModelExecutionOutcomeQuery = {
                SpecHashes = []
                DatasetVersions = []
                Statuses = [ "NotAStatus" ]
                BatchId = None
            }

            match! api.QueryOutcomes(anyQuery, None, 10) with
            | Error(ModelExecutionRefusal.InvalidQuery _) -> ()
            | other -> failtestf "expected InvalidQuery for a bad status; got %A" other

            match! api.QueryOutcomes({ anyQuery with Statuses = [] }, None, 0) with
            | Error(ModelExecutionRefusal.InvalidQuery _) -> ()
            | other -> failtestf "expected InvalidQuery for limit 0; got %A" other

            // Unknown outcome key → NotFound.
            match! api.GetOutcome "no-such-key" with
            | Error(ModelExecutionRefusal.NotFound _) -> ()
            | other -> failtestf "expected NotFound; got %A" other
        }

        testCaseAsync "refusal totality — a throwing substrate surfaces as Unexpected data, never a wire exception"
        <| async {
            let throwingRegistry =
                { new IModelRegistry with
                    member _.Register(_, _, _, _, _) = failwith "boom"
                    member _.Get(_, _) = failwith "boom"
                    member _.QueryBySpecHash(_, _) = failwith "boom"
                    member _.QueryByDatasetVersion(_, _) = failwith "boom"
                    member _.QueryByStatus(_, _) = failwith "boom"
                    member _.QueryPage(_, _, _, _) = failwith "boom"
                    member _.TransitionStatus(_, _, _, _, _) = failwith "boom"
                }

            let services = ServiceCollection()
            services.AddSingleton<IModelRegistry>(throwingRegistry) |> ignore

            services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser "op-1"))
            |> ignore

            let ctx = DefaultHttpContext() :> HttpContext
            ctx.RequestServices <- services.BuildServiceProvider()
            let api = ModelExecutionApiHandler.modelExecutionApi ctx

            match! api.GetOutcome "k" with
            | Error(ModelExecutionRefusal.Unexpected m) -> Expect.stringContains m "boom" "the failure travels as data"
            | other -> failtestf "expected Unexpected; got %A" other

            let query: ModelExecutionOutcomeQuery = {
                SpecHashes = []
                DatasetVersions = []
                Statuses = []
                BatchId = None
            }

            match! api.QueryOutcomes(query, None, 10) with
            | Error(ModelExecutionRefusal.Unexpected _) -> ()
            | other -> failtestf "expected Unexpected; got %A" other
        }

        testCaseAsync "anonymous callers are refused with ScopeUnavailable"
        <| async {
            let services = ServiceCollection()

            services.AddSingleton<AccessContext>(AccessContext.unrestricted (AnonymousSession "anon-1"))
            |> ignore

            let ctx = DefaultHttpContext() :> HttpContext
            ctx.RequestServices <- services.BuildServiceProvider()
            let api = ModelExecutionApiHandler.modelExecutionApi ctx

            match! api.SubmitFit(submission 1L) with
            | Error ModelExecutionRefusal.ScopeUnavailable -> ()
            | other -> failtestf "expected ScopeUnavailable; got %A" other

            match! api.GetOutcome "k" with
            | Error ModelExecutionRefusal.ScopeUnavailable -> ()
            | other -> failtestf "expected ScopeUnavailable; got %A" other
        }
        // ── Phase 640 — the closed carry gaps, at the API tier ────────

        testCaseAsync "the receipt's job handle is opaque, and the minting rule is stored unvalidated"
        <| async {
            let fixture = freshFixture ()
            let api = ModelExecutionApiHandler.modelExecutionApi (ctxFor fixture "op-640" false)
            let! _ = seedInput fixture "op-640"

            let! receipt = api.SubmitFit(submission 11L)
            let r = okv "submit" receipt
            let handle = (List.exactlyOne r.Jobs).JobId

            Expect.isFalse (String.IsNullOrWhiteSpace handle) "the handle is present"

            do! awaitOutcomes fixture "op-640" "single/submitter-minted-hash-11/11" 1

            let query: ModelExecutionOutcomeQuery = {
                SpecHashes = [ "submitter-minted-hash-11" ]
                DatasetVersions = []
                Statuses = []
                BatchId = None
            }

            let! page = api.QueryOutcomes(query, None, 10)
            let outcome = (okv "query" page).Outcomes |> List.exactlyOne

            // The submission named a minting rule whose minting its hash is
            // NOT. Forge stored the pair without comparing them — a receiver
            // that had started validating would have refused this submission
            // outright rather than registering an outcome for it.
            Expect.equal outcome.SpecHash "submitter-minted-hash-11" "the hash is stored exactly as handed"

            // The artifact is expressible as absent now; this one is
            // present, and carries no format because forge declares none for
            // bytes it never opens.
            match outcome.Artifact with
            | Some artifact ->
                Expect.isFalse (String.IsNullOrWhiteSpace artifact.ArtifactId) "a retained artifact names itself"
                Expect.isNone artifact.Format "forge declares no format for an opaque artifact"
            | None -> failtest "the reference provider retains an artifact"

            Expect.equal outcome.Timing.SubmittedAt outcome.RegisteredAt "timing carries what the registry retains"
            Expect.isNone outcome.Timing.DurationMs "an unretained duration is absent, never fabricated"
            Expect.isNone outcome.Cost "this deployment does not account for cost"
        }

        testCaseAsync "a gate-failed artifact scores by default and is refused with GateFailed under the policy"
        <| async {
            // The pair is the point. Registering a gate-failed outcome is
            // unchanged behaviour, and so is scoring it — the policy is what
            // moves, and only for a deployment that asked for it.
            let fixture = freshFixture ()
            let! _ = seedInput fixture "op-641"

            let permissive =
                ModelExecutionApiHandler.modelExecutionApi (ctxFor fixture "op-641" true)

            let! receipt = permissive.SubmitFit(gateFailingSubmission 21L)
            let r = okv "submit" receipt
            Expect.isEmpty r.EnqueueFailures "a gate that will fail is not an enqueue refusal"

            do! awaitOutcomes fixture "op-641" "single/gate-fail-hash-21/21" 1

            let query: ModelExecutionOutcomeQuery = {
                SpecHashes = [ "gate-fail-hash-21" ]
                DatasetVersions = []
                Statuses = []
                BatchId = None
            }

            let! page = permissive.QueryOutcomes(query, None, 10)
            let outcome = (okv "query" page).Outcomes |> List.exactlyOne

            Expect.isTrue
                (outcome.GateVerdicts |> List.exists (fun v -> not v.Passed))
                "the fit registered despite failing its gate — a failed gate is evidence, not an error"

            let scoreRequest = {
                ArtifactKeyHash = outcome.CompositeKeyHash
                InputDatasetId = "input"
                InputVersion = 1
                OutputDatasetId = "predictions-permissive"
            }

            match! permissive.RequestScore scoreRequest with
            | Ok _ -> ()
            | Error e -> failtestf "an unconfigured deployment must score exactly as before: %A" e

            // Same artifact, same request, one registered policy.
            let governed =
                ModelExecutionApiHandler.modelExecutionApi (ctxWithGatePolicy fixture "op-641")

            match!
                governed.RequestScore {
                    scoreRequest with
                        OutputDatasetId = "predictions-governed"
                }
            with
            | Error(ModelExecutionRefusal.GateFailed verdicts) ->
                Expect.isNonEmpty verdicts "the refusal carries the verdicts, so no re-query is needed"

                Expect.isTrue
                    (verdicts |> List.exists (fun v -> v.Name = "mean" && not v.Passed))
                    "the failing gate is named in the refusal"
            | other -> failtestf "expected GateFailed under the policy; got %A" other
        }
    ]