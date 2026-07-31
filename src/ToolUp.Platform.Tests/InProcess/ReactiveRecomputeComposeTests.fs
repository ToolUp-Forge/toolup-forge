// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ReactiveRecomputeComposeTests

open System
open System.Collections.Concurrent
open System.Threading
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 623 — reactive recomputation, ACTIVATED ───────────────────
//
// Phase 561 shipped the substrate and proved it by direct construction;
// its own Outcome recorded that the compose hookup was never wired, so
// nothing in a composed deployment ever ran it. Direct construction is
// exactly what could not catch that, so **every test in this file drives
// the fact tier through `FactsCompose.withFactStore` and resolves its
// collaborators out of the built container** — never by `new`-ing the
// handler, the decorator, or the resolver.
//
// The falsification that matters: delete the compose registration and
// these go red. Each end-to-end case asserts on a value produced by a
// chain whose every link is compose-wired — the `IDataObjectStore`
// decorator, the DI-deferred handler registration, and the DI-resolved
// resolver — so removing any one of them breaks it. `NoFactStore`
// twins pin the other direction: none of it exists when facts are not
// composed (623.E).

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

/// The data object every fact in this file is computed from. The id is
/// what the fact cites in `Evidence.InputHashes` — the same string space
/// as the lineage node id, which is what makes the write-path seed and
/// the read-path probe agree.
[<Literal>]
let private InputObject = "sales.rollup"

let private draftFor (metric: string) (inputId: string) (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ "acme" ]
    }
    Metric = MetricRef metric
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ inputId ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

let private clauseFor (metric: string) : FactClause = {
    SubjectHierarchy = "brand"
    SubjectPath = [ "acme" ]
    Metric = metric
    PeriodFrom = None
    PeriodTo = None
    AsOf = None
}

let private metricDef (id: string) (staleness: StalenessPolicy) (policy: RecomputePolicy option) : MetricDefinition = {
    Id = id
    Name = id
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = HigherIsBetter
    DisplayFormat = ""
    Staleness = staleness
    ProducingOperation = Some "rollup"
    CanonicalMethod = None
    RecomputePolicy = policy
    RollUp = None
}

let private registryWith (defs: MetricDefinition list) : IMetricRegistry =
    MetricRegistry.build (defs |> List.map (fun d -> { Module = "sales"; Definition = d })) []

let private recomputerReturning (f: Fact -> Result<FactDraft option, string>) : IFactRecomputer =
    { new IFactRecomputer with
        member _.Recompute(_scopeId, fact) = async { return f fact }
    }

/// A realistic recompute engine: re-read the input object's LATEST
/// version and produce a draft citing **its** content hash.
///
/// That is not test convenience — it is what the model requires.
/// `Fact.compute` folds the input identities and not the value into the
/// content address, so a `Computed` draft that re-cites the same inputs
/// is byte-identically the same fact and `Assert` is a no-op (law L2: a
/// recompute over unchanged inputs IS the unchanged fact). A recompute
/// produces a new head exactly when it names the new input version — so
/// the engine, the invalidation seed and the freshness probe all key on
/// version identity.
let private recomputerCitingLatest (objects: unit -> IDataObjectStore) (objectId: string) (value: decimal) =
    { new IFactRecomputer with
        member _.Recompute(scopeId, fact) = async {
            let! versions = (objects ()).ListVersions(scopeId, objectId)

            return
                match versions with
                | [] -> Ok None
                | _ ->
                    let latest = versions |> List.maxBy _.Version
                    Ok(Some(draftFor fact.Metric.Value latest.ContentHash value))
        }
    }

/// An `IJobScheduler` that dispatches a triggered job to the handler
/// **registered under its name**, synchronously. It stands in for a real
/// scheduler implementation so the assertion stays deterministic; what it
/// deliberately does NOT do is know any handler the compose root did not
/// register — which is what makes the end-to-end cases falsify the
/// registration rather than assume it.
type private DispatchingScheduler() =
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let scheduled = ConcurrentQueue<JobRegistration>()
    let jobs = ConcurrentDictionary<JobId, JobRegistration>()
    let executed = ConcurrentQueue<JobResult>()

    member _.RegisteredHandlers = handlers.Keys |> List.ofSeq
    member _.Scheduled = scheduled |> List.ofSeq
    member _.Executed = executed |> List.ofSeq

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler
        member _.RegisterHandlerAsync(name, handler) = async { return Ok(handlers[name] <- handler) }

        member _.Schedule(registration: JobRegistration) = async {
            let id = Guid.NewGuid()
            scheduled.Enqueue registration
            jobs[id] <- registration
            return Ok id
        }

        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs _ = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(scopeId, jobId, byUserId) = async {
            match jobs.TryGetValue jobId with
            | false, _ -> return Error "unknown job"
            | true, registration ->
                match handlers.TryGetValue registration.Handler with
                | false, _ ->
                    // The handler was never registered — exactly what a
                    // missing compose registration looks like.
                    return Error(sprintf "no handler registered for %s" registration.Handler)
                | true, handler ->
                    let ctx: JobContext = {
                        JobId = jobId
                        ScopeId = scopeId
                        AccessContext = AccessContext.unrestricted (AuthenticatedUser byUserId)
                        Attempt = 1
                        Trigger = registration.Trigger
                        TriggerSource = ScheduledManually byUserId
                        ScheduledAt = DateTime.UtcNow
                        RunningAt = DateTime.UtcNow
                        Payload = registration.Payload
                        DeadLetterDestination = None
                    }

                    let! result = handler.Execute ctx
                    executed.Enqueue result
                    return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

/// The compose root under test: `FactsCompose.withFactStore` over a
/// substrate-seeded collection, exactly as `ServerApp.run` applies it —
/// then the container is built and its hosted services started, which is
/// what a booting deployment does.
type private Composed = {
    Provider: ServiceProvider
    /// The `IDataObjectStore` the composed deployment hands out — the
    /// decorator when facts are composed, the plain store when not.
    DataObjects: IDataObjectStore
    /// The exact plain-store instance registered before compose ran, for
    /// the reference-equality "nothing was wrapped" assertion.
    PlainDataObjects: IDataObjectStore
}

let private compose
    (knob: FactStoreMode)
    (registry: IMetricRegistry option)
    (recomputer: IFactRecomputer option)
    (scheduler: IJobScheduler option)
    : Composed =
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
    let blob = InMemoryBlobStorage() :> IBlobStorage

    let plainObjects =
        DataObjectStore.DataObjectStore(blob, silentLogger) :> IDataObjectStore

    services.AddSingleton<IBlobStorage>(blob) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<ILogger>(silentLogger) |> ignore
    services.AddSingleton<IDataObjectStore>(plainObjects) |> ignore

    registry
    |> Option.iter (fun r -> services.AddSingleton<IMetricRegistry>(r) |> ignore)

    recomputer
    |> Option.iter (fun r -> services.AddSingleton<IFactRecomputer>(r) |> ignore)

    scheduler
    |> Option.iter (fun s -> services.AddSingleton<IJobScheduler>(s) |> ignore)

    match app.Extensions.ServiceConfig with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    let provider = services.BuildServiceProvider()

    // Boot: every registered hosted service starts, which is where the
    // Phase 623.A deferred declaration resolves + registers.
    for hosted in provider.GetServices<IHostedService>() do
        hosted.StartAsync(CancellationToken.None)
        |> Async.AwaitTask
        |> Async.RunSynchronously

    {
        Provider = provider
        DataObjects = provider.GetRequiredService<IDataObjectStore>()
        PlainDataObjects = plainObjects
    }

let private saveVersion (objects: IDataObjectStore) (scopeId: string) (body: string) : Async<DataObject> = async {
    let! result =
        objects.Save(
            scopeId,
            InputObject,
            Text.Encoding.UTF8.GetBytes body,
            "rollup",
            "tester",
            Map.empty,
            VersioningPolicy.Versioned
        )

    return
        match result with
        | Ok dataObject -> dataObject
        | Error err -> failtestf "data-object save failed: %A" err
}

let private assertFact (store: IFactStore) (scopeId: string) (d: FactDraft) : Async<Fact> = async {
    let! result = store.Assert(scopeId, d)

    return
        match result with
        | Ok fact -> fact
        | Error err -> failtestf "fact assert failed: %s" err
}

/// `DateTime.UtcNow` on Windows advances in ~15ms steps, and the whole
/// upstream-change derivation is "did a version land AFTER this fact was
/// asserted". Separating the two writes by more than one tick is what
/// makes that comparison mean what it says rather than depend on a race.
let private afterAClockTick () = Async.Sleep 40

// ── 623.A — the DI-deferred declaration reaches the scheduler ──────

let declarationTests =
    testList "Phase 623.A — DI-deferred recompute declaration" [

        test "a facts-composed deployment registers + schedules the recompute handler at boot" {
            let scheduler = DispatchingScheduler()

            let composed = compose EnabledFactStore None None (Some(scheduler :> IJobScheduler))

            Expect.contains
                scheduler.RegisteredHandlers
                RecomputeJobHandler.HandlerName
                "the recompute handler is registered with the composed scheduler — the hookup Phase 561 left undone"

            let recomputeJobs =
                scheduler.Scheduled
                |> List.filter (fun r -> r.Handler = RecomputeJobHandler.HandlerName)

            Expect.hasLength recomputeJobs 1 "scheduled exactly once, under the reserved platform scope"
            Expect.equal recomputeJobs.Head.ScopeId "_platform" "the 9b.B default scope"
            Expect.equal recomputeJobs.Head.Trigger Trigger.Manual "fired on demand by invalidation, never on a cadence"

            Expect.isSome
                (composed.Provider.GetService<IFactRecomputer>() |> Option.ofObj)
                "the default IFactRecomputer floor is composed with the store"
        }

        test "a deployment's own IFactRecomputer wins over the composed default (TryAdd)" {
            let mine = recomputerReturning (fun _ -> Ok None)

            let composed = compose EnabledFactStore None (Some mine) None

            Expect.isTrue
                (Object.ReferenceEquals(composed.Provider.GetRequiredService<IFactRecomputer>(), mine))
                "the deployment's engine, not the NoFactRecomputer floor"
        }

        test "the deferred declaration resolves through the SAME path as an eager one (additive to 9b.B)" {
            let scheduler = DispatchingScheduler() :> IJobScheduler

            let eager =
                ScheduledJobDeclaration.create
                    "sales.eager-job"
                    { new IJobHandler with
                        member _.Execute _ = async { return JobResult.Success }
                    }
                    Trigger.Manual

            // The pre-623 surface, untouched.
            ScheduledJobDeclaration.registerWith scheduler silentLogger eager

            // The Phase 623 surface, resolving to the identical shape.
            let deferred =
                DeferredScheduledJobDeclaration.ofHandler "sales.deferred-job" Trigger.Manual (fun _ ->
                    { new IJobHandler with
                        member _.Execute _ = async { return JobResult.Success }
                    })

            ScheduledJobDeclaration.registerWith scheduler silentLogger (deferred.Resolve null)

            let recording = scheduler :?> DispatchingScheduler

            Expect.equal
                (recording.Scheduled
                 |> List.map (fun r -> r.Handler, r.ScopeId, r.Tags.TryFind "source"))
                [
                    "sales.eager-job", "_platform", Some "compose-time"
                    "sales.deferred-job", "_platform", Some "compose-time"
                ]
                "both forms produce the same registration — the deferred variant is a second case, not a retype"
        }

        test "no scheduler composed ⇒ the deferred declaration warns rather than throwing" {
            let warnings = ResizeArray<string>()

            let logger =
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info _ = ()
                    member _.Warn m = warnings.Add m
                    member _.Error(_, _) = ()
                }

            let services = ServiceCollection()
            services.AddSingleton<ILogger>(logger) |> ignore
            let sp = services.BuildServiceProvider()

            let hosted =
                DeferredScheduledJobDeclaration.hostedService
                    "Test feature"
                    [
                        DeferredScheduledJobDeclaration.ofHandler "x" Trigger.Manual (fun _ ->
                            failwith "never resolved")
                    ]
                    sp

            hosted.StartAsync(CancellationToken.None)
            |> Async.AwaitTask
            |> Async.RunSynchronously

            Expect.hasLength warnings 1 "a config mismatch is surfaced, not fatal"
            Expect.stringContains warnings[0] "NoJobScheduler" "the warning names the cause"
        }
    ]

// ── 623.B — upstream-aware freshness + OnQuery recompute at read ───

let readPathTests =
    testList "Phase 623.B — upstream-aware freshness at the read path" [

        testCaseAsync
            "an UntilUpstreamChange metric goes stale through the composed resolver when its input gains a version"
        <| async {
            // RecomputePolicy left undeclared (⇒ Manual): nothing
            // recomputes, so this isolates the freshness derivation.
            let registry = registryWith [ metricDef "revenue" UntilUpstreamChange None ]

            let composed = compose EnabledFactStore (Some registry) None None
            let store = composed.Provider.GetRequiredService<IFactStore>()
            let resolver = composed.Provider.GetRequiredService<IFactResolver>()
            let scope = newScope ()

            let! _v1 = saveVersion composed.DataObjects scope "the original rollup"
            let! _fact = assertFact store scope (draftFor "revenue" InputObject 100m)

            let! beforeChange = resolver.Resolve(scope, clauseFor "revenue")

            Expect.equal
                (beforeChange |> List.map _.Freshness)
                [ FactFresh ]
                "fresh while the input it cites is the latest word"

            do! afterAClockTick ()
            let! _v2 = saveVersion composed.DataObjects scope "the corrected rollup"

            let! afterChange = resolver.Resolve(scope, clauseFor "revenue")

            match afterChange with
            | [ resolved ] ->
                match resolved.Freshness with
                | FactStale _ -> ()
                | other ->
                    failtestf
                        "an UntilUpstreamChange metric must go stale once its input changed — got %A (this is the Stage-0 degradation Phase 623.B removed)"
                        other

                Expect.isNone
                    resolved.SupersededBy
                    "stale by upstream change, not by supersession — no successor exists"

                Expect.equal resolved.Rendering "100" "the stale value still reads as itself; only the stamp changed"
            | other -> failtestf "expected exactly one resolved fact, got %A" other
        }

        testCaseAsync "UntilSuperseded is untouched by the upstream signal (GP 11 — the pre-623 answer)"
        <| async {
            let registry = registryWith [ metricDef "revenue" UntilSuperseded None ]

            let composed = compose EnabledFactStore (Some registry) None None
            let store = composed.Provider.GetRequiredService<IFactStore>()
            let resolver = composed.Provider.GetRequiredService<IFactResolver>()
            let scope = newScope ()

            let! _v1 = saveVersion composed.DataObjects scope "v1"
            let! _fact = assertFact store scope (draftFor "revenue" InputObject 100m)
            do! afterAClockTick ()
            let! _v2 = saveVersion composed.DataObjects scope "v2"

            let! resolved = resolver.Resolve(scope, clauseFor "revenue")

            Expect.equal
                (resolved |> List.map _.Freshness)
                [ FactFresh ]
                "a metric that did not declare UntilUpstreamChange resolves exactly as it did before Phase 623"
        }

        testCaseAsync "an OnQuery metric is recomputed AT READ through the composed resolver"
        <| async {
            let registry =
                registryWith [ metricDef "revenue" UntilUpstreamChange (Some OnQuery) ]

            let objects = ref Unchecked.defaultof<IDataObjectStore>

            let composed =
                compose
                    EnabledFactStore
                    (Some registry)
                    (Some(recomputerCitingLatest (fun () -> objects.Value) InputObject 120m))
                    None

            objects.Value <- composed.DataObjects

            let store = composed.Provider.GetRequiredService<IFactStore>()
            let resolver = composed.Provider.GetRequiredService<IFactResolver>()
            let scope = newScope ()

            let! v1 = saveVersion composed.DataObjects scope "v1"
            let! stale = assertFact store scope (draftFor "revenue" v1.ContentHash 100m)
            do! afterAClockTick ()
            let! _v2 = saveVersion composed.DataObjects scope "v2"

            let! resolved = resolver.Resolve(scope, clauseFor "revenue")

            match resolved with
            | [ fresh ] ->
                Expect.equal fresh.Rendering "120" "the read returned the RECOMPUTED value, not the stale one"
                Expect.notEqual fresh.FactId stale.FactId "a new content-addressed head"
                Expect.equal fresh.Freshness FactFresh "the recomputed head is current and its inputs are now caught up"
            | other -> failtestf "expected the recomputed head, got %A" other

            // The recompute went through the ordinary Assert path, so the
            // supersession edge is derived, not written by the read.
            let! heads = store.Query(scope, FactQuery.all)
            Expect.hasLength heads 1 "one current head"
            Expect.equal heads.Head.Supersedes (Some stale.FactId) "the recomputed head supersedes the stale one"
        }

        testCaseAsync "no recomputer composed ⇒ the OnQuery arm leaves the stale fact standing"
        <| async {
            let registry =
                registryWith [ metricDef "revenue" UntilUpstreamChange (Some OnQuery) ]

            // Only the NoFactRecomputer floor the compose root registers.
            let composed = compose EnabledFactStore (Some registry) None None
            let store = composed.Provider.GetRequiredService<IFactStore>()
            let resolver = composed.Provider.GetRequiredService<IFactResolver>()
            let scope = newScope ()

            let! _v1 = saveVersion composed.DataObjects scope "v1"
            let! original = assertFact store scope (draftFor "revenue" InputObject 100m)
            do! afterAClockTick ()
            let! _v2 = saveVersion composed.DataObjects scope "v2"

            let! resolved = resolver.Resolve(scope, clauseFor "revenue")

            match resolved with
            | [ r ] ->
                Expect.equal r.FactId original.FactId "the existing fact stands"
                Expect.equal r.Rendering "100" "nothing was recomputed — no engine wired"

                match r.Freshness with
                | FactStale _ -> ()
                | other -> failtestf "and it is honestly stale, got %A" other
            | other -> failtestf "expected one resolved fact, got %A" other
        }
    ]

// ── 623.C / 623.D — end to end through the compose root ────────────

let endToEndTests =
    testList "Phase 623.C/D — reactive recomputation end to end" [

        testCaseAsync
            "an Eager metric is recomputed when a data-object version lands on the COMPOSED store (the whole point of the phase)"
        <| async {
            let registry = registryWith [ metricDef "revenue" UntilUpstreamChange (Some Eager) ]

            let scheduler = DispatchingScheduler()
            let objects = ref Unchecked.defaultof<IDataObjectStore>

            let composed =
                compose
                    EnabledFactStore
                    (Some registry)
                    (Some(recomputerCitingLatest (fun () -> objects.Value) InputObject 120m))
                    (Some(scheduler :> IJobScheduler))

            objects.Value <- composed.DataObjects

            let store = composed.Provider.GetRequiredService<IFactStore>()
            let scope = newScope ()

            let! v1 = saveVersion composed.DataObjects scope "v1"
            let! stale = assertFact store scope (draftFor "revenue" v1.ContentHash 100m)

            // Nothing has changed yet — the fact base is quiet.
            let! before = store.Query(scope, FactQuery.all)
            Expect.equal (before |> List.map _.Value) [ Scalar 100m ] "the original value stands before the change"

            // The one line the whole phase exists for: a data-object
            // version lands on the store the deployment composed, and the
            // fact tier reacts without anyone telling it to.
            do! afterAClockTick ()
            let! _v2 = saveVersion composed.DataObjects scope "v2"

            let recomputeJobs =
                scheduler.Scheduled
                |> List.filter (fun r ->
                    r.Handler = RecomputeJobHandler.HandlerName
                    && r.Tags.TryFind "origin" = Some "fact-invalidation")

            Expect.hasLength
                recomputeJobs
                1
                "the arriving version enqueued exactly one recompute for the invalidated fact"

            Expect.equal
                scheduler.Executed
                [ JobResult.Success ]
                "and it was dispatched to the handler the compose root registered"

            let! heads = store.Query(scope, FactQuery.all)
            Expect.hasLength heads 1 "one current head after the recompute"
            Expect.equal heads.Head.Value (Scalar 120m) "the head is the recomputed value"

            Expect.equal
                heads.Head.Supersedes
                (Some stale.FactId)
                "supersession stays derived through the ordinary Assert"
        }

        testCaseAsync "an unrelated object landing invalidates nothing (the walk is not a blanket sweep)"
        <| async {
            let registry = registryWith [ metricDef "revenue" UntilUpstreamChange (Some Eager) ]

            let recomputer =
                recomputerReturning (fun _ -> Ok(Some(draftFor "revenue" InputObject 120m)))

            let scheduler = DispatchingScheduler()

            let composed =
                compose EnabledFactStore (Some registry) (Some recomputer) (Some(scheduler :> IJobScheduler))

            let store = composed.Provider.GetRequiredService<IFactStore>()
            let scope = newScope ()

            let! _fact = assertFact store scope (draftFor "revenue" InputObject 100m)

            let! _ =
                composed.DataObjects.Save(
                    scope,
                    "marketing.spend",
                    Text.Encoding.UTF8.GetBytes "unrelated",
                    "rollup",
                    "tester",
                    Map.empty,
                    VersioningPolicy.Versioned
                )

            Expect.isEmpty
                (scheduler.Scheduled
                 |> List.filter (fun r -> r.Tags.TryFind "origin" = Some "fact-invalidation"))
                "no fact cited the object that landed"

            let! heads = store.Query(scope, FactQuery.all)
            Expect.equal (heads |> List.map _.Value) [ Scalar 100m ] "the fact base is untouched"
        }

        testCaseAsync "declaring no reactive policy costs nothing at save time (the declaration IS the opt-in)"
        <| async {
            // The fact tier is fully composed; the vocabulary simply asks
            // for no recomputation. The decorator must short-circuit.
            let registry = registryWith [ metricDef "revenue" UntilSuperseded None ]

            let recomputer =
                recomputerReturning (fun _ -> failtest "the recomputer must never be reached")

            let scheduler = DispatchingScheduler()

            let composed =
                compose EnabledFactStore (Some registry) (Some recomputer) (Some(scheduler :> IJobScheduler))

            let store = composed.Provider.GetRequiredService<IFactStore>()
            let scope = newScope ()

            let! _v1 = saveVersion composed.DataObjects scope "v1"
            let! original = assertFact store scope (draftFor "revenue" InputObject 100m)
            do! afterAClockTick ()
            let! _v2 = saveVersion composed.DataObjects scope "v2"

            Expect.isEmpty
                (scheduler.Scheduled
                 |> List.filter (fun r -> r.Tags.TryFind "origin" = Some "fact-invalidation"))
                "nothing scheduled — the gate short-circuits before any store read"

            let! heads = store.Query(scope, FactQuery.all)
            Expect.equal (heads |> List.map _.FactId) [ original.FactId ] "the fact base is byte-identical"
        }

        testCaseAsync "a reaction that throws never fails the write it followed"
        <| async {
            let scope = newScope ()
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let inner = DataObjectStore.DataObjectStore(blob, silentLogger) :> IDataObjectStore

            let exploding =
                ReactiveDataChange.decorate
                    inner
                    (fun () -> true)
                    (fun _ _ -> async { return failwith "fact tier is down" })
                    silentLogger

            let! result =
                exploding.Save(
                    scope,
                    InputObject,
                    Text.Encoding.UTF8.GetBytes "v1",
                    "rollup",
                    "tester",
                    Map.empty,
                    VersioningPolicy.Versioned
                )

            match result with
            | Ok dataObject -> Expect.equal dataObject.Version 1 "the write committed and reported itself normally"
            | Error err -> failtestf "the write must survive a failing reaction, got %A" err

            let! versions = inner.ListVersions(scope, InputObject)
            Expect.hasLength versions 1 "and the version is really there"
        }
    ]

// ── 623.E — zero cost when facts are not composed ─────────────────

let zeroCostTests =
    testList "Phase 623.E — a facts-free deployment is unchanged" [

        test "NoFactStore leaves the ServerApp structurally identical" {
            let app = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = NoFactStore
                    }
            }

            // Reference equality, not structural: the `NoFactStore` arm
            // must return the very object it was handed, so no field is
            // rebuilt and nothing is allocated (GP 13).
            Expect.isTrue
                (Object.ReferenceEquals(FactsCompose.withFactStore app, app))
                "withFactStore is the identity on a deployment that did not ask for facts"

            Expect.isNone (FactsCompose.withFactStore app).Extensions.ServiceConfig "no service config was contributed"
        }

        test "NoFactStore registers no hosted service, no recomputer, and does not wrap the data-object store" {
            let scheduler = DispatchingScheduler()

            let composed = compose NoFactStore None None (Some(scheduler :> IJobScheduler))

            Expect.isEmpty
                (composed.Provider.GetServices<IHostedService>() |> List.ofSeq)
                "no hosted service — nothing to start, nothing to tick"

            Expect.isEmpty scheduler.RegisteredHandlers "no handler registered with the scheduler"
            Expect.isEmpty scheduler.Scheduled "no job scheduled"

            Expect.isTrue
                (isNull (box (composed.Provider.GetService<IFactRecomputer>())))
                "no IFactRecomputer registered"

            Expect.isTrue (isNull (box (composed.Provider.GetService<IFactStore>()))) "no fact store"

            // The strongest form of "no allocation": the deployment gets
            // back the very instance it registered, not a wrapper around it.
            Expect.isTrue
                (Object.ReferenceEquals(composed.DataObjects, composed.PlainDataObjects))
                "the composed IDataObjectStore is the SAME object — no decorator was allocated"
        }

        test "EnabledFactStore is the twin that proves the previous assertion means something" {
            let composed = compose EnabledFactStore None None None

            Expect.isFalse
                (Object.ReferenceEquals(composed.DataObjects, composed.PlainDataObjects))
                "a facts-composed deployment DOES get the reactive decorator"

            Expect.equal
                (composed.DataObjects.GetType().Name)
                "ReactiveDataObjectStore"
                "and it is the Phase 623.C decorator specifically"
        }

        testCaseAsync "the decorator is transparent — every delegated member behaves as the plain store"
        <| async {
            let composed = compose EnabledFactStore None None None
            let scope = newScope ()

            let! saved = saveVersion composed.DataObjects scope "v1"
            let! fetched = composed.DataObjects.Get(scope, InputObject)

            match fetched with
            | Ok(dataObject, bytes) ->
                Expect.equal dataObject.Version saved.Version "same version"
                Expect.equal (Text.Encoding.UTF8.GetString bytes) "v1" "same bytes"
            | Error err -> failtestf "Get through the decorator failed: %A" err

            let! versions = composed.DataObjects.ListVersions(scope, InputObject)
            Expect.hasLength versions 1 "ListVersions delegates"

            let! objects = composed.DataObjects.ListObjects scope
            Expect.equal (objects |> List.map _.ObjectId) [ InputObject ] "ListObjects delegates"
        }
    ]

let tests =
    testList "Phase 623 reactive recomputation activation" [
        declarationTests
        readPathTests
        endToEndTests
        zeroCostTests
    ]