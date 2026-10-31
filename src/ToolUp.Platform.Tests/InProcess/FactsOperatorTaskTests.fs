// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
module ToolUp.Platform.Tests.InProcess.FactsOperatorTaskTests

// ─── The fact tier's two operator tasks ──────────────────────────────
//
//   1. **Row attribution.** Over a delegated table whose stored image
//      predates attribution, a dry run names every unattributed row and
//      writes nothing; a run records the writer the table's head names; a
//      period slice refused before is then taken, and the rows it carries
//      are minted under the run that wrote them. A second run finds nothing.
//   2. **Job re-issue.** A fact-tier job persisted under a bare shard id is
//      listed by a dry run and re-issued by a run under the scope the
//      deployment's resolver resolves the named principal to — only when
//      that scope is the key the operator named. Every other job is left as
//      it was, and a key the grammar does not render is refused.

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.Tracing
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── 1. Row attribution ──────────────────────────────────────────────

let private products: SubjectDefinition = {
    Id = "products"
    Name = "Products"
    Levels = [ "brand"; "sku" ]
    Calendar = None
}

let private registry: IMetricRegistry =
    MetricRegistry.build [
        {
            MetricRegistration.Module = "sales"
            Definition = {
                Id = "revenue"
                Name = "revenue"
                Unit = "count"
                Dimensionality = "count"
                Direction = HigherIsBetter
                DisplayFormat = ""
                Staleness = UntilSuperseded
                ProducingOperation = None
                CanonicalMethod = None
                RecomputePolicy = None
                RollUp = None
                Context = None
            }
        }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = products
        }
    ]

let private table: FactTableDefinition = {
    Id = "sku-sales"
    SchemaVersion = 1
    Hierarchy = "products"
    Level = "sku"
    Columns = [ FactTableDefinition.column "revenue" FactTableValueShape.Scalar ]
    PeriodGrain = FactTablePeriodGrain.Month
    ProducingOperation = "sales-rollup"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.Replace
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

let private month (m: int) : TemporalExtent = {
    From = DateTime(2026, m, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, m + 1, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some(sprintf "2026-%02d" m)
}

let private row (sku: string) (m: int) (revenue: decimal) : FactTableRow = {
    Subject = [ "acme"; sku ]
    Period = month m
    Values = Map.ofList [ "revenue", Scalar revenue ]
}

type private DelegatedWorld = {
    Writer: IFactTableWriter
    Store: IFactStore
    Storage: IBlobStorage
    Scope: ResolvedScope
}

let private delegatedWorld () : DelegatedWorld =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let ticks = ref 0

    let clock () =
        ticks.Value <- ticks.Value + 1
        DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(float ticks.Value)

    let inner =
        BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

    let tables =
        FactTableRegistry.build [
            {
                FactTableRegistration.Module = "sales"
                Definition = table
            }
        ] [ BindFactTable(table.Id, DelegateFact.Destination) ]

    let delegates =
        match DelegateFacts.resolve tables (Some registry) [ table.Id ] with
        | Ok ds -> ds
        | Error e -> failtestf "resolve: %s" e

    let store =
        DelegatedFactStore.create inner storage delegates (Some registry) clock :> IFactStore

    {
        Writer =
            DelegatedFactStore.writer None storage events tables (Some registry) delegates (fun () -> Some store) clock
        Store = store
        Storage = storage
        Scope = TestScope.resolved ("team-" + Guid.NewGuid().ToString "N")
    }

let private run (w: DelegatedWorld) (options: FactTableRunOptions) (rows: FactTableRow list) =
    match w.Writer.OpenRun(w.Scope, table.Id, options = options) |> Async.RunSynchronously with
    | Error e -> Error e
    | Ok opened ->
        match w.Writer.WriteRows(w.Scope, opened.RunId, rows) |> Async.RunSynchronously with
        | Error e -> Error e
        | Ok _ -> w.Writer.Commit(w.Scope, opened.RunId) |> Async.RunSynchronously

let private cell (w: DelegatedWorld) (sku: string) (m: int) : Fact =
    w.Store.Query(
        w.Scope,
        FactQuery.forSubjectMetric
            {
                Hierarchy = "products"
                Path = [ "acme"; sku ]
            }
            (MetricRef "revenue")
    )
    |> TestScope.ok
    |> Async.RunSynchronously
    |> List.filter (fun f -> f.Period = month m)
    |> List.exactlyOne

/// Rewrite every stored image as an earlier release left it: no row names its
/// writer.
let private forgetAttribution (w: DelegatedWorld) =
    let scope = w.Scope.ScopeId

    for name in w.Storage.List(scope, "_delegate-tables/rows/") |> Async.RunSynchronously do
        let image =
            match w.Storage.Download(scope, name) |> Async.RunSynchronously with
            | Ok bytes -> Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString bytes).AsArray()
            | Error e -> failtestf "download: %s" e

        for node in image do
            node.AsObject().Remove "WrittenBy" |> ignore

        w.Storage.Upload(scope, name, Encoding.UTF8.GetBytes(image.ToJsonString()))
        |> Async.RunSynchronously
        |> ignore

let private attribute (w: DelegatedWorld) (dryRun: bool) =
    match
        DelegateRowAttribution.attribute w.Storage dryRun [ w.Scope.ScopeId ]
        |> Async.RunSynchronously
    with
    | Ok report -> report
    | Error e -> failtestf "attribution: %s" e

let private valueOf' (f: Fact) =
    match f.Value with
    | Scalar d -> d
    | other -> failtestf "expected a scalar, got %A" other

let private sliced (m: int) =
    FactTableRunOptions.none |> FactTableRunOptions.forSlice (month m)

let private attributionTests =
    testList "delegate row attribution" [

        test "a dry run names every unattributed row and writes nothing; a run records the head's writer" {
            let w = delegatedWorld ()

            match
                run
                    w
                    (FactTableRunOptions.none
                     |> FactTableRunOptions.withInputs [ "sha256:" + String('a', 64) ])
                    [ row "a1" 9 10m; row "a1" 10 20m ]
            with
            | Ok _ -> ()
            | Error e -> failtestf "first run: %s" (FactTableWriteError.describe e)

            forgetAttribution w

            let planned = attribute w true
            Expect.equal (planned.Images |> List.map _.Unattributed) [ 2 ] "both rows of the one image"
            Expect.isTrue planned.DryRun "said to be a dry run"
            Expect.stringContains (DelegateRowAttribution.render planned) "would attribute 2 row(s)" "the report"

            match run w (sliced 9) [ row "a1" 9 11m ] with
            | Error(FactTableRunOptionsRefused(_, reason)) ->
                Expect.stringContains reason "stored before rows were attributed" "a dry run wrote nothing"
            | other -> failtestf "expected the slice refused before the attribution, got %A" other

            let recorded = attribute w false
            Expect.equal (recorded.Images |> List.map _.Unattributed) [ 2 ] "the rows it attributed"

            let october = cell w "a1" 10

            match run w (sliced 9) [ row "a1" 9 11m ] with
            | Ok _ -> ()
            | Error e -> failtestf "the slice, once attributed: %s" (FactTableWriteError.describe e)

            Expect.equal (valueOf' (cell w "a1" 9)) 11m "the slice's row"

            Expect.equal
                (cell w "a1" 10).FactId
                october.FactId
                "the carried row is minted under the run the head named as its writer"

            let again = attribute w false
            Expect.isTrue (again.Images |> List.forall (fun i -> i.Unattributed = 0)) "a second run finds nothing"
            Expect.hasLength again.Images 2 "and examines both images"
        }
    ]

// ─── 2. Job re-issue ─────────────────────────────────────────────────

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
        member _.Unsubscribe _ = async { return () }
    }

/// The in-process scheduler with a carrier bound, as the platform composes it.
let private scheduler () : IJobScheduler * ScopeCarrier =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let scheduler =
        JobScheduler.create
            (JobStore.create blobs events)
            events
            silentChannel
            ServerConfig.defaults
            silentLogger
            (NoOpActivitySink() :> IActivitySink)
        :> IJobScheduler

    let carrier = ScopeCarrier.ephemeral ()
    (scheduler :?> IScopeCarrierBinding).BindScopeCarrier carrier

    let idle =
        { new IJobHandler with
            member _.Execute _ = async { return JobResult.Success }
        }

    for handler in
        [
            RecomputeJobHandler.HandlerName
            CoherenceJobHandler.HandlerName
            "reports.weekly"
        ] do
        scheduler.RegisterHandler(handler, idle)

    scheduler, carrier

/// A job scheduled the way every job was before its caller carried a scope:
/// the string overload, so no token.
let private unscoped (scheduler: IJobScheduler) (shard: string) (handler: string) : JobId =
    let registration: JobRegistration = {
        ScopeId = shard
        Handler = handler
        Payload = "{}"
        Trigger = CronTrigger "0 6 * * 1"
        Idempotency = None
        RetryPolicy = JobRetryPolicy.defaults
        ShardKey = None
        Precision = JobPrecision.Minute
        CreatedBy = "before-0.38"
        Tags = Map.empty
    }

    match scheduler.Schedule registration |> Async.RunSynchronously with
    | Ok(Scheduled jobId | Coalesced jobId) -> jobId
    | Error e -> failtestf "schedule failed: %A" e

/// The platform's declared-principal resolver over a deployment resolver that
/// resolves every principal to `key`.
let private resolvingTo (key: ScopeKey) =
    DeclaredPrincipalResolver(fun _ -> async.Return(Ok(StorageScope.ofKey true key)))

let private runsAnonymous (scheduler: IJobScheduler) (carrier: ScopeCarrier) (shard: string) (jobId: JobId) =
    match scheduler.Get(shard, jobId) |> Async.RunSynchronously with
    | Some job -> (CarriedJobScope.ofDefinition carrier job).IsAnonymous
    | None -> failtestf "job %O is gone" jobId

let private reissueTests =
    testList "fact-job re-issue" [

        test
            "a dry run lists the refused fact jobs by name and writes nothing; a run re-issues them under the named key" {
            let scheduler, carrier = scheduler ()
            let shard = "a" + Guid.NewGuid().ToString "N"
            let key = ScopeKey.Team shard
            let recompute = unscoped scheduler shard RecomputeJobHandler.HandlerName
            let coherence = unscoped scheduler shard CoherenceJobHandler.HandlerName
            let other = unscoped scheduler shard "reports.weekly"

            let resolver = resolvingTo key

            let planned =
                FactJobReissue.reissue scheduler carrier resolver key None true
                |> Async.RunSynchronously

            Expect.equal
                (planned.Jobs |> List.map (fun (j, _) -> j.JobId) |> Set.ofList)
                (Set.ofList [ recompute; coherence ])
                "the two fact jobs, and not the other"

            Expect.equal (FactJobReissue.exitCode planned) 0 "a dry run that listed is a success"
            Expect.stringContains (FactJobReissue.render planned) (string recompute) "named in the report"
            Expect.isTrue (runsAnonymous scheduler carrier shard recompute) "a dry run wrote nothing"

            let report =
                FactJobReissue.reissue scheduler carrier resolver key (Some "operator") false
                |> Async.RunSynchronously

            Expect.equal
                (report.Jobs |> List.map snd |> List.distinct)
                [ FactJobReissueOutcome.Reissued ]
                "every refused job re-issued"

            Expect.equal (FactJobReissue.exitCode report) 0 "exit 0"

            for jobId in [ recompute; coherence ] do
                match scheduler.Get(shard, jobId) |> Async.RunSynchronously with
                | Some job ->
                    let scope = CarriedJobScope.ofDefinition carrier job
                    Expect.equal scope.Key (Some key) "the job now runs under the named key"
                | None -> failtest "job gone"

            Expect.isTrue (runsAnonymous scheduler carrier shard other) "a job that is not the fact tier's is untouched"

            let again =
                FactJobReissue.reissue scheduler carrier resolver key (Some "operator") true
                |> Async.RunSynchronously

            Expect.isEmpty again.Jobs "nothing is refused any more"
        }

        test "a principal that resolves to another kind than the operator named re-issues nothing" {
            let scheduler, carrier = scheduler ()
            let shard = "a" + Guid.NewGuid().ToString "N"
            let job = unscoped scheduler shard RecomputeJobHandler.HandlerName

            // The operator says the shard is a user's; the deployment resolves
            // the principal to the team of that id.
            let report =
                FactJobReissue.reissue
                    scheduler
                    carrier
                    (resolvingTo (ScopeKey.Team shard))
                    (ScopeKey.User shard)
                    (Some "operator")
                    false
                |> Async.RunSynchronously

            Expect.isSome report.Refusal "refused"
            Expect.stringContains report.Refusal.Value "nothing was re-issued" "says so"
            Expect.equal (FactJobReissue.exitCode report) 1 "exit 1"
            Expect.isTrue (runsAnonymous scheduler carrier shard job) "the job is as it was"

            let unnamed =
                FactJobReissue.reissue
                    scheduler
                    carrier
                    (resolvingTo (ScopeKey.Team shard))
                    (ScopeKey.Team shard)
                    None
                    false
                |> Async.RunSynchronously

            Expect.isSome unnamed.Refusal "a run without a principal re-issues nothing"
            Expect.isTrue (runsAnonymous scheduler carrier shard job) "still as it was"
        }

        test "the kind is the operator's to say: a key the grammar does not render is refused" {
            Expect.equal (FactJobReissue.parseKey "team:a1") (Ok(ScopeKey.Team "a1")) "team"
            Expect.equal (FactJobReissue.parseKey "user:u-7") (Ok(ScopeKey.User "u-7")) "user"
            Expect.equal (FactJobReissue.parseKey "session:s1") (Ok(ScopeKey.Session "s1")) "session"

            for text in [ "a1"; "org:a1"; "team:"; ":a1"; "team: a1" ] do
                Expect.isError (FactJobReissue.parseKey text) (sprintf "'%s' is refused" text)
        }
    ]

let tests = testList "Fact tier operator tasks" [ attributionTests; reissueTests ]