// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ToleranceTriggerTests

// The tolerance trigger, through the real writer and the real fact store:
//
//   1. A move beyond a tolerance enqueues the run once, naming the subjects that
//      moved; a move within it enqueues nothing and records no refusal.
//   2. Absolute, relative and zero-comparison tolerances mean what the profile says.
//   3. `PreviousDelivery` is the latest earlier period the table holds, `DaysBefore`
//      is exactly that many days back, and a first delivery has nothing to compare.
//   4. A recorded gap at either period is not a move and never reads as zero.
//   5. The same moves are not enqueued twice, whichever facts state them.
//   6. Every refusal is logged with the run key and the table, and reaches no commit.
//   7. An undeclared table or metric refuses at compose.

open System
open System.Collections.Concurrent
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Facts.Presentation
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Metrics
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── Fixtures ─────────────────────────────────────────────────────────

[<Literal>]
let private Readings = "sensor.reading"

[<Literal>]
let private Humidities = "sensor.humidity"

[<Literal>]
let private Bands = "sensor.band"

[<Literal>]
let private Pressures = "sensor.pressure"

[<Literal>]
let private Temperature = "temperature"

[<Literal>]
let private Pressure = "pressure"

[<Literal>]
let private Humidity = "humidity"

[<Literal>]
let private Band = "band"

[<Literal>]
let private Change = "reading-change"

let private metric (id: string) : MetricDefinition = {
    Id = id
    Name = id
    Unit = "unit"
    Dimensionality = "unit"
    Direction = HigherIsBetter
    DisplayFormat = ""
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private registry: IMetricRegistry =
    MetricRegistry.build
        ([ Temperature; Humidity; Band; Pressure ]
         |> List.map (fun id -> {
             MetricRegistration.Module = "sensors"
             Definition = metric id
         }))
        [
            {
                SubjectRegistration.Module = "sensors"
                Definition = {
                    Id = "sensors"
                    Name = "Sensors"
                    Levels = [ "sensor" ]
                    Calendar = None
                }
            }
        ]

let private table (id: string) (column: string) (shape: FactTableValueShape) : FactTableDefinition = {
    Id = id
    SchemaVersion = 1
    Hierarchy = "sensors"
    Level = "sensor"
    Columns = [ FactTableDefinition.column column shape ]
    PeriodGrain = FactTablePeriodGrain.Day
    ProducingOperation = "sensor-load"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.AppendByRun
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

let private tables: IFactTableRegistry =
    FactTableRegistry.build
        ([
            table Readings Temperature FactTableValueShape.Scalar
            table Humidities Humidity FactTableValueShape.Scalar
            table Bands Band FactTableValueShape.Interval
            {
                table Pressures Pressure FactTableValueShape.Scalar with
                    HistoryMode = FactTableHistoryMode.Replace
            }
         ]
         |> List.map (fun definition -> {
             FactTableRegistration.Module = "sensors"
             Definition = definition
         }))
        [ BindAllFactTables DefaultFactTableWriter.Destination ]

let private trigger (tolerance: Tolerance) : ToleranceTrigger = {
    RunKey = Change
    Table = Readings
    Metric = Temperature
    Subjects = None
    Comparison = ToleranceComparison.PreviousDelivery
    Tolerance = tolerance
}

let private day (n: int) : TemporalExtent = {
    From = DateTime(2026, 9, n, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 9, n + 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some(sprintf "2026-09-%02d" n)
}

let private cell (n: int) (subject: string) (column: string) (value: FactValue) : FactTableRow = {
    Subject = [ subject ]
    Period = day n
    Values = Map.ofList [ column, value ]
}

let private row (n: int) (subject: string) (value: decimal) =
    cell n subject Temperature (Scalar value)

/// A logger that keeps what it is told.
type private ToleranceLogger() =
    let warnings = ConcurrentQueue<string>()
    let infos = ConcurrentQueue<string>()
    member _.Warnings = warnings |> List.ofSeq
    member _.Infos = infos |> List.ofSeq

    interface ILogger with
        member _.Debug _ = ()
        member _.Info message = infos.Enqueue message
        member _.Warn message = warnings.Enqueue message
        member _.Error(_, _) = ()

/// A scheduler that honours an idempotency key and lists what it scheduled, as
/// the platform's does.
type private ToleranceScheduler() =
    let keys = ConcurrentDictionary<string, JobId>()
    let jobs = ConcurrentDictionary<JobId, ResolvedScope * JobRegistration * DateTime>()
    let fired = ConcurrentQueue<ResolvedScope * JobRegistration>()
    let mutable clock = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)

    member _.Fired = fired |> List.ofSeq

    interface IJobScheduler with
        member _.RegisterHandler(_, _) = ()
        member _.RegisterHandlerAsync(_, _) = async { return Ok() }

        member _.Schedule(scope: ResolvedScope, registration: JobRegistration) = async {
            let id = Guid.NewGuid()
            let key = registration.Idempotency |> Option.map _.Key |> Option.get

            if keys.TryAdd(key, id) then
                clock <- clock.AddMinutes 1.0
                jobs[id] <- (scope, registration, clock)
                return Ok(Scheduled id)
            else
                return Ok(Coalesced keys[key])
        }

        member _.Schedule(_: JobRegistration) = async { return failwith "only the resolved overload is used" }
        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }

        member _.ListJobs scopeId = async {
            return [
                for KeyValue(id, (scope, registration, createdAt)) in jobs do
                    if scope.ScopeId = scopeId then
                        {
                            JobId = id
                            ScopeId = scopeId
                            Handler = registration.Handler
                            Payload = registration.Payload
                            Trigger = registration.Trigger
                            Idempotency = registration.Idempotency
                            RetryPolicy = registration.RetryPolicy
                            ShardKey = registration.ShardKey
                            Precision = registration.Precision
                            Status = JobStatus.Active
                            CreatedAt = createdAt
                            CreatedBy = registration.CreatedBy
                            NextRunAt = None
                            LastRunAt = None
                            LastRunStatus = None
                            LastRunError = None
                            ConsecutiveFailures = 0
                            Tags = registration.Tags
                            LastRunWarning = None
                        }
            ]
        }

        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(_, jobId, _) = async {
            let scope, registration, _ = jobs[jobId]
            fired.Enqueue((scope, registration))
            return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

/// A store whose reads fail.
let private faulting (inner: IFactStore) : IFactStore =
    { new IFactStore with
        member _.Assert(scope, draft) = inner.Assert(scope, draft)
        member _.AssertBatch(scope, drafts) = inner.AssertBatch(scope, drafts)
        member _.Get(scope, factId) = inner.Get(scope, factId)

        member _.Query(_, _) =
            async.Return(Error(StorageFault("query", "the store is down")))

        member _.QueryWithCompetition(scope, query) =
            inner.QueryWithCompetition(scope, query)

        member _.QuerySupersessionChain(scope, factId) =
            inner.QuerySupersessionChain(scope, factId)

        member _.QueryPopulation(scope, query) = inner.QueryPopulation(scope, query)
    }

/// The real writer, decorated, over one scheduler.
type private World(triggers: ToleranceTrigger list, ?withScheduler: bool, ?storeFaults: bool) =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let facts = BlobFactStore.create blobs events
    let logger = ToleranceLogger()
    let scheduler = ToleranceScheduler()

    let writer =
        ToleranceTrigger.decorate
            triggers
            (fun () -> Some tables)
            (fun () ->
                Some(
                    if defaultArg storeFaults false then
                        faulting facts
                    else
                        facts
                ))
            (fun () ->
                if defaultArg withScheduler true then
                    Some(scheduler :> IJobScheduler)
                else
                    None)
            (fun () -> None)
            logger
            (DefaultFactTableWriter.create facts blobs events tables (Some registry))

    member _.Writer = writer
    member _.Warnings = logger.Warnings
    member _.Infos = logger.Infos
    member _.Fired = scheduler.Fired

    member _.Publish(scope: ResolvedScope, tableId: string, n: int, input: char, rows: FactTableRow list) = async {
        let options =
            FactTableRunOptions.none
            |> FactTableRunOptions.withInputs [ "sha256:" + String(input, 64) ]
            |> FactTableRunOptions.forSlice (day n)

        let! opened = writer.OpenRun(scope, tableId, options = options)

        let run =
            match opened with
            | Ok run -> run
            | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

        match! writer.WriteRows(scope, run.RunId, rows) with
        | Ok _ -> ()
        | Error e -> failtestf "write: %s" (FactTableWriteError.describe e)

        match! writer.Commit(scope, run.RunId) with
        | Ok _ -> ()
        | Error e -> failtestf "commit: %s" (FactTableWriteError.describe e)
    }

let private scopeId = "team-sensors"

let private jobContext (scope: ResolvedScope) (payload: string) (trigger: Trigger) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = scope.ScopeId
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "worker")
    Attempt = 1
    Trigger = trigger
    Scope = scope
    TriggerSource = ScheduledManually(UserId "worker")
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = payload
    DeadLetterDestination = None
}

/// One unit of work: a job attempt, which completes when `body` returns.
let private inUnit (jobScope: ResolvedScope) (body: unit -> Async<unit>) = async {
    let handler =
        { new IJobHandler with
            member _.Execute _ = async {
                do! body ()
                return Success
            }
        }

    let! result = (DeliveryCompleted.aroundJob "load" handler).Execute(jobContext jobScope "" Trigger.Manual)
    Expect.equal result Success "the attempt succeeded"
}

/// A delivery of one period of readings, as one unit of work.
let private deliver (world: World) (n: int) (input: char) (rows: (string * decimal) list) =
    let scope = TestScope.resolved scopeId

    inUnit scope (fun () ->
        world.Publish(scope, Readings, n, input, rows |> List.map (fun (subject, value) -> row n subject value)))

let private entries (registration: JobRegistration) =
    (GroundedNarrativeTrigger.tryParsePayload registration.Payload).Value.InvalidatedInputs

let private moved (registration: JobRegistration) =
    entries registration
    |> List.filter _.StartsWith(GroundedNarrativeTrigger.MovePrefix, StringComparison.Ordinal)

let private namesSubject (subject: string) (entry: string) =
    entry.StartsWith(sprintf "move:%s:%s:%s:" Readings Temperature subject, StringComparison.Ordinal)

// ─── The firing rule ──────────────────────────────────────────────────

let private firingTests =
    testList "the firing rule" [

        testCaseAsync "a move beyond an absolute tolerance enqueues once, naming the subject that moved"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 5m) ]
            do! deliver world 1 'a' [ "north", 10m; "south", 10m ]
            do! deliver world 2 'b' [ "north", 16m; "south", 11m ]

            match world.Fired with
            | [ scope, registration ] ->
                Expect.equal scope.ScopeId scopeId "under the unit's resolved scope"
                Expect.isFalse scope.IsAnonymous "resolved, not anonymous"
                let named = moved registration
                Expect.hasLength named 1 "one subject moved"
                Expect.isTrue (namesSubject "north" named.Head) (sprintf "north is named: %s" named.Head)

                Expect.exists
                    (entries registration)
                    (fun e -> e = "period:" + (day 2).From.ToString "o")
                    "at the newest period"
            | fired -> failtestf "expected one firing, got %d" fired.Length

            Expect.isEmpty world.Warnings "no refusal"
        }

        testCaseAsync "a move within tolerance enqueues nothing and is not recorded as a refusal"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 5m) ]
            do! deliver world 1 'a' [ "north", 10m ]
            do! deliver world 2 'b' [ "north", 14m ]
            Expect.isEmpty world.Fired "within tolerance"
            Expect.isEmpty world.Warnings "not a refusal"
        }

        test "a move exactly at the tolerance is not beyond it, and either direction counts" {
            Expect.isFalse (ToleranceTrigger.beyond (Tolerance.Absolute 5m) 10m 15m) "at the limit"
            Expect.isTrue (ToleranceTrigger.beyond (Tolerance.Absolute 5m) 10m 15.01m) "past it"
            Expect.isTrue (ToleranceTrigger.beyond (Tolerance.Absolute 5m) 10m 4m) "downwards"
            Expect.isFalse (ToleranceTrigger.beyond (Tolerance.Absolute 0m) 10m 10m) "zero, unmoved"
            Expect.isTrue (ToleranceTrigger.beyond (Tolerance.Absolute 0m) 10m 10.01m) "zero admits any move"
        }

        test "a relative tolerance is over the comparison's magnitude" {
            let tenPercent = Tolerance.Relative 0.1m
            Expect.isFalse (ToleranceTrigger.beyond tenPercent 100m 110m) "at the limit"
            Expect.isTrue (ToleranceTrigger.beyond tenPercent 100m 111m) "past it"
            Expect.isTrue (ToleranceTrigger.beyond tenPercent -100m -111m) "a negative comparison's magnitude"
            Expect.isFalse (ToleranceTrigger.beyond tenPercent -100m -105m) "within it, negative"
        }

        test "a relative tolerance over a zero comparison: any move is beyond it, zero to zero is not" {
            let huge = Tolerance.Relative 1000m
            Expect.isTrue (ToleranceTrigger.beyond huge 0m 0.01m) "any move from zero"
            Expect.isTrue (ToleranceTrigger.beyond huge 0m -5m) "downwards too"
            Expect.isFalse (ToleranceTrigger.beyond huge 0m 0m) "no move"
        }

        testCaseAsync "a relative tolerance fires through the writer, and a zero comparison does not raise"
        <| async {
            let world = World [ trigger (Tolerance.Relative 0.1m) ]
            do! deliver world 1 'a' [ "north", 100m; "south", 0m; "east", 100m ]
            do! deliver world 2 'b' [ "north", 105m; "south", 3m; "east", 120m ]

            match world.Fired with
            | [ _, registration ] ->
                let named = moved registration
                Expect.hasLength named 2 "east and south moved"
                Expect.exists named (namesSubject "east") "ten per cent passed"
                Expect.exists named (namesSubject "south") "a move from zero"
            | fired -> failtestf "expected one firing, got %d" fired.Length

            Expect.isEmpty world.Warnings "nothing raised"
        }

        testCaseAsync "selected subjects: only they are asked about"
        <| async {
            let world =
                World [
                    {
                        trigger (Tolerance.Absolute 1m) with
                            Subjects = Some [ "south" ]
                    }
                ]

            do! deliver world 1 'a' [ "north", 10m; "south", 10m ]
            do! deliver world 2 'b' [ "north", 50m; "south", 10m ]
            Expect.isEmpty world.Fired "the subject that moved is not selected"
            do! deliver world 3 'c' [ "north", 50m; "south", 20m ]

            match world.Fired with
            | [ _, registration ] ->
                let named = moved registration
                Expect.hasLength named 1 "one selected subject"
                Expect.isTrue (namesSubject "south" named.Head) "south"
            | fired -> failtestf "expected one firing, got %d" fired.Length
        }
    ]

// ─── The comparison ───────────────────────────────────────────────────

let private comparisonTests =
    let weekBack = {
        trigger (Tolerance.Absolute 5m) with
            Comparison = ToleranceComparison.DaysBefore 7
    }

    testList "the comparison" [

        testCaseAsync "a first delivery has nothing to compare: nothing fires, and the log says so"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 0m) ]
            do! deliver world 1 'a' [ "north", 10m ]
            Expect.isEmpty world.Fired "no comparison, no fire"
            Expect.isEmpty world.Warnings "not a refusal"

            Expect.exists
                world.Infos
                (fun i -> i.Contains Change && i.Contains Readings && i.Contains "nothing to compare")
                "logged"
        }

        testCaseAsync "the previous delivery is the latest earlier period the table holds, across a gap in days"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 5m) ]
            do! deliver world 1 'a' [ "north", 10m ]
            do! deliver world 5 'b' [ "north", 30m ]
            do! deliver world 9 'c' [ "north", 31m ]
            Expect.hasLength world.Fired 1 "day 5 against day 1 moved; day 9 against day 5 did not"
        }

        testCaseAsync "a subject the previous delivery did not state did not move"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 5m) ]
            do! deliver world 1 'a' [ "north", 10m ]
            do! deliver world 2 'b' [ "north", 10m; "south", 99m ]
            Expect.isEmpty world.Fired "south has no comparison, and the walk does not go further back"
        }

        testCaseAsync "days back compares that day, not the previous delivery"
        <| async {
            let world = World [ weekBack ]
            do! deliver world 1 'a' [ "north", 10m ]
            do! deliver world 7 'b' [ "north", 90m ]
            do! deliver world 8 'c' [ "north", 11m ]
            Expect.isEmpty world.Fired "day 8 against day 1 is within tolerance, though day 7 was far off"
            do! deliver world 9 'd' [ "north", 11m ]
            Expect.isEmpty world.Fired "day 2 holds nothing to compare"
            Expect.exists world.Infos (fun i -> i.Contains "nothing to compare") "and the log says so"
            do! deliver world 14 'e' [ "north", 11m ]
            Expect.hasLength world.Fired 1 "day 14 against day 7 moved"
        }
    ]

// ─── Recorded gaps ────────────────────────────────────────────────────

let private gapTests =
    let scope () = TestScope.resolved scopeId

    let publishGap (world: World) (n: int) (input: char) (subject: string) =
        let scope = scope ()

        inUnit scope (fun () ->
            world.Publish(scope, Readings, n, input, [ cell n subject Temperature (Absent "not measured") ]))

    testList "a recorded gap" [

        testCaseAsync "a gap in the newest period is not a move and never a zero"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 1m) ]
            do! deliver world 1 'a' [ "north", 10m ]
            do! publishGap world 2 'b' "north"
            Expect.isEmpty world.Fired "a coerced zero would have moved by ten"
            Expect.isEmpty world.Warnings "not a refusal"
        }

        testCaseAsync "a gap in the compared period is not a move and never a zero"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 1m) ]
            do! publishGap world 1 'a' "north"
            do! deliver world 2 'b' [ "north", 10m ]
            Expect.isEmpty world.Fired "a coerced zero would have moved by ten"
            Expect.hasLength world.Infos 1 "only the first delivery had nothing to compare"
        }
    ]

// ─── Once ─────────────────────────────────────────────────────────────

let private onceTests =
    testList "once" [

        testCaseAsync "a re-delivery of the same figures enqueues nothing"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 5m) ]
            do! deliver world 1 'a' [ "north", 10m ]
            do! deliver world 2 'b' [ "north", 20m ]
            do! deliver world 2 'c' [ "north", 20m ]
            Expect.hasLength world.Fired 1 "the same figures say nothing new"
            Expect.isEmpty world.Warnings "not a refusal"
        }

        testCaseAsync "a commit that moves its table and leaves the moves as they were enqueues nothing"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 5m) ]
            do! deliver world 1 'a' [ "north", 10m ]
            do! deliver world 2 'b' [ "north", 10m ]
            do! deliver world 3 'c' [ "north", 20m ]
            Expect.hasLength world.Fired 1 "the move"
            do! deliver world 1 'd' [ "north", 11m ]
            Expect.hasLength world.Fired 1 "a restatement of an earlier period changes neither figure compared"
        }

        testCaseAsync "a correction of the newest figure enqueues again, and so does one returning to the first"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 5m) ]
            do! deliver world 1 'a' [ "north", 10m ]
            do! deliver world 2 'b' [ "north", 20m ]
            do! deliver world 2 'c' [ "north", 30m ]
            Expect.hasLength world.Fired 2 "a new figure is news"
            do! deliver world 2 'd' [ "north", 20m ]
            Expect.hasLength world.Fired 3 "the report on file says thirty"
        }

        testCaseAsync "a correction returning to a figure on a table whose facts are the same ones enqueues too"
        <| async {
            let world =
                World [
                    {
                        trigger (Tolerance.Absolute 5m) with
                            Table = Pressures
                            Metric = Pressure
                    }
                ]

            let scope = TestScope.resolved scopeId

            let publish (n: int) (input: char) (value: decimal) =
                inUnit scope (fun () ->
                    world.Publish(scope, Pressures, n, input, [ cell n "north" Pressure (Scalar value) ]))

            do! publish 1 'a' 10m
            do! publish 2 'b' 20m
            do! publish 2 'c' 30m
            do! publish 2 'd' 20m
            Expect.hasLength world.Fired 3 "it does not coalesce onto the first figure's job"
        }

        testCaseAsync "a commit that changed nothing is not asked, even with moves no job has named"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 5m) ]
            let scope = TestScope.resolved scopeId
            do! world.Publish(scope, Readings, 1, 'a', [ row 1 "north" 10m ])
            do! world.Publish(scope, Readings, 2, 'b', [ row 2 "north" 20m ])
            Expect.isEmpty world.Fired "off any unit, nothing could carry them"
            do! deliver world 2 'c' [ "north", 20m ]
            Expect.isEmpty world.Fired "an unchanged re-delivery brought no news"
        }

        testCaseAsync "two triggers of one run, over two tables of one unit, enqueue it once naming both"
        <| async {
            let humid = {
                trigger (Tolerance.Absolute 1m) with
                    Table = Humidities
                    Metric = Humidity
            }

            let world = World [ trigger (Tolerance.Absolute 1m); humid ]
            let scope = TestScope.resolved scopeId

            let both (n: int) (input: char) (value: decimal) =
                inUnit scope (fun () -> async {
                    do! world.Publish(scope, Readings, n, input, [ row n "north" value ])
                    do! world.Publish(scope, Humidities, n, input, [ cell n "north" Humidity (Scalar value) ])
                    Expect.isEmpty world.Fired "nothing fires until the unit completes"
                })

            do! both 1 'a' 10m
            do! both 2 'b' 20m

            match world.Fired with
            | [ _, registration ] ->
                let named = moved registration
                Expect.hasLength named 2 "both metrics"
                Expect.exists named (fun e -> e.Contains Temperature) "temperature"
                Expect.exists named (fun e -> e.Contains Humidity) "humidity"
            | fired -> failtestf "expected one firing, got %d" fired.Length
        }

        testCaseAsync "two runs on one table each enqueue, and a table no trigger names is read no further"
        <| async {
            let other = {
                trigger (Tolerance.Absolute 1m) with
                    RunKey = "other-run"
            }

            let world = World [ trigger (Tolerance.Absolute 1m); other ]
            let scope = TestScope.resolved scopeId
            do! deliver world 1 'a' [ "north", 10m ]
            do! deliver world 2 'b' [ "north", 20m ]

            let runs =
                world.Fired
                |> List.map (fun (_, r) -> (GroundedNarrativeTrigger.tryParsePayload r.Payload).Value.RunKey)
                |> List.sort

            Expect.equal runs [ "other-run"; Change ] "one job each"

            do!
                inUnit scope (fun () ->
                    world.Publish(scope, Humidities, 1, 'a', [ cell 1 "north" Humidity (Scalar 1m) ]))

            Expect.hasLength world.Fired 2 "not watched"
            Expect.hasLength world.Infos 2 "only the two first-delivery lines"
        }

        testCaseAsync "a job's recorded trigger names the tolerance only when its change names a move"
        <| async {
            let scope = TestScope.resolved scopeId
            let narratives = GroundedNarrativeRegistry()

            narratives.Register(
                GroundedNarrativeDefinition.create Change "Change" "sensors" "narrator" "Change" "Say what moved." []
            )
            |> ignore

            let recorded = ConcurrentQueue<string>()

            let run =
                { new IGroundedNarrativeRun with
                    member _.Run request = async {
                        recorded.Enqueue request.Trigger
                        return GroundedNarrativeFailed "not run in this test"
                    }
                }

            let world =
                BlobFactStore.create (InMemoryBlobStorage()) (InMemoryEventStore.InMemoryEventStore())

            let handler =
                GroundedNarrativeTrigger.GroundedNarrativeJobHandler(world, None, narratives, run) :> IJobHandler

            let execute (change: Set<string>) (job: Trigger) =
                handler.Execute(jobContext scope (GroundedNarrativeTrigger.payloadFor Change change) job)

            let! _ = execute (Set.singleton "move:t:m:s:a->b") Trigger.Manual
            let! _ = execute (Set.singleton "delivery:x") Trigger.Manual
            let! _ = execute (Set.singleton "move:t:m:s:a->b") (Trigger.CronTrigger "0 7 * * 1")

            Expect.equal
                (List.ofSeq recorded)
                [
                    GroundedNarrativeTrigger.ToleranceTriggerName
                    GroundedNarrativeTrigger.TriggerName
                    "cadence"
                ]
                "named for what enqueued the job"
        }
    ]

// ─── Refusals ─────────────────────────────────────────────────────────

let private refusalTests =
    let refusesNaming (world: World) (reason: string) =
        Expect.exists
            world.Warnings
            (fun w -> w.Contains Change && w.Contains Readings && w.Contains reason)
            (sprintf "logged, with the run and the table: %s; %A" reason world.Warnings)

    testList "refusals" [

        testCaseAsync "no scheduler composed is logged and the commit succeeds"
        <| async {
            let world = World([ trigger (Tolerance.Absolute 1m) ], withScheduler = false)
            do! deliver world 1 'a' [ "north", 10m ]
            do! deliver world 2 'b' [ "north", 20m ]
            refusesNaming world "no IJobScheduler"
        }

        testCaseAsync "a fault reading the facts is logged, never read as nothing having moved"
        <| async {
            let world = World([ trigger (Tolerance.Absolute 1m) ], storeFaults = true)
            do! deliver world 1 'a' [ "north", 10m ]
            Expect.isEmpty world.Fired "nothing to enqueue over"
            refusesNaming world "could not be read"
        }

        testCaseAsync "a commit off the request path and any delivery is logged and succeeds"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 1m) ]
            do! world.Publish(TestScope.resolved scopeId, Readings, 1, 'a', [ row 1 "north" 10m ])
            Expect.isEmpty world.Fired "nothing can carry the run"
            refusesNaming world "off the request path"
        }

        testCaseAsync "a unit that resolved another scope, or none, enqueues nothing and says so"
        <| async {
            let world = World [ trigger (Tolerance.Absolute 1m) ]
            let commitScope = TestScope.resolved scopeId

            do!
                inUnit (TestScope.resolved "team-other") (fun () ->
                    world.Publish(commitScope, Readings, 1, 'a', [ row 1 "north" 10m ]))

            refusesNaming world "the delivery resolved 'team-other'"

            do!
                inUnit ResolvedScope.anonymous (fun () ->
                    world.Publish(commitScope, Readings, 2, 'b', [ row 2 "north" 20m ]))

            refusesNaming world "resolved no scope"
            Expect.isEmpty world.Fired "nothing fired"
        }
    ]

// ─── Composition ──────────────────────────────────────────────────────

let private factApp () : ServerApp =
    {
        ServerApp.empty with
            Config = {
                ServerConfig.defaults with
                    Surfaces = [ SurfaceProfile.individual ]
                    Stores = {
                        ServerConfig.defaults.Stores with
                            FactStore = FeatureMode.Enabled
                    }
            }
    }
    |> FactsCompose.withFactStore

let private composed (app: ServerApp) (services: IServiceCollection) : IServiceCollection =
    match ServerApp.serviceConfig app with
    | Some configure -> configure services
    | None -> services

let private compositionTests =
    let validate (triggers: ToleranceTrigger list) (services: IServiceCollection) =
        let app =
            factApp ()
            |> FactsCompose.withFactTableWriter
            |> FactsCompose.withToleranceTriggers triggers

        composed app services
        |> Seq.choose (fun d ->
            match d.ImplementationInstance with
            | :? ConfigValidation.IConfigValidator as v when v.Name = "fact-tolerance-triggers" -> Some v
            | _ -> None)
        |> Seq.exactlyOne
        |> fun v -> v.Validate() |> Async.RunSynchronously

    let declared () =
        ServiceCollection().AddSingleton<IFactTableRegistry>(tables) :> IServiceCollection

    let refusedNaming (fragment: string) (result: ConfigValidation.ValidationResult) =
        match result with
        | ConfigValidation.ValidationResult.Error message ->
            Expect.stringContains message "FactsCompose.withToleranceTriggers" "names the builder"
            Expect.stringContains message fragment "names what is wrong"
        | other -> failtestf "expected a refusal naming %s, got %A" fragment other

    testList "composition" [

        testCaseAsync "one line composes it: the resolved writer fires, and the sweep still passes through"
        <| async {
            let scheduler = ToleranceScheduler()
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let services =
                ServiceCollection()
                    .AddSingleton<IBlobStorage>(blobs)
                    .AddSingleton<IEventStore>(events)
                    .AddSingleton<IFactStore>(BlobFactStore.create blobs events)
                    .AddSingleton<ILogger>(ToleranceLogger())
                    .AddSingleton<IJobScheduler>(scheduler)
                    .AddSingleton<IFactTableRegistry>(tables)
                    .AddSingleton<IMetricRegistry>(registry)
                    .AddSingleton<IHttpContextAccessor>(HttpContextAccessor())

            let app =
                factApp ()
                |> FactsCompose.withFactTableWriter
                |> FactsCompose.withToleranceTriggers [ trigger (Tolerance.Absolute 1m) ]

            use provider = (composed app services).BuildServiceProvider()
            let writer = provider.GetRequiredService<IFactTableWriter>()
            let scope = TestScope.resolved scopeId

            let publish (n: int) (input: char) (value: decimal) =
                inUnit scope (fun () -> async {
                    let options =
                        FactTableRunOptions.none
                        |> FactTableRunOptions.withInputs [ "sha256:" + String(input, 64) ]
                        |> FactTableRunOptions.forSlice (day n)

                    let! opened = writer.OpenRun(scope, Readings, options = options)

                    match opened with
                    | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)
                    | Ok run ->
                        let! _ = writer.WriteRows(scope, run.RunId, [ row n "north" value ])
                        let! _ = writer.Commit(scope, run.RunId)
                        return ()
                })

            do! publish 1 'a' 10m
            do! publish 2 'b' 20m
            Expect.hasLength scheduler.Fired 1 "the composed writer fired"
            Expect.isTrue (writer :? IFactTableOrphanSweep) "the sweep passes through"
        }

        test "composed before withFactTableWriter, the composition is refused naming the order" {
            let app =
                factApp ()
                |> FactsCompose.withToleranceTriggers [ trigger (Tolerance.Absolute 1m) ]

            Expect.throwsC (fun () -> composed app (ServiceCollection()) |> ignore) (fun ex ->
                Expect.stringContains ex.Message "FactsCompose.withToleranceTriggers" "refused, naming the builder")
        }

        test "with the fact store disabled the app is unchanged" {
            let app =
                ServerApp.empty
                |> FactsCompose.withToleranceTriggers [ trigger (Tolerance.Absolute 1m) ]

            Expect.isEmpty app.Contributions "nothing composed"
        }

        test "declared table and metric pass" {
            match validate [ trigger (Tolerance.Absolute 1m) ] (declared ()) with
            | ConfigValidation.ValidationResult.Ok -> ()
            | other -> failtestf "expected Ok, got %A" other
        }

        test "an undeclared table refuses at compose" {
            validate
                [
                    {
                        trigger (Tolerance.Absolute 1m) with
                            Table = "sensor.calibration"
                    }
                ]
                (declared ())
            |> refusedNaming "sensor.calibration"
        }

        test "no table declared at all refuses at compose" {
            validate [ trigger (Tolerance.Absolute 1m) ] (ServiceCollection())
            |> refusedNaming Readings
        }

        test "a metric the table does not carry refuses at compose" {
            validate
                [
                    {
                        trigger (Tolerance.Absolute 1m) with
                            Metric = Humidity
                    }
                ]
                (declared ())
            |> refusedNaming Humidity
        }

        test "a column with no figure to compare refuses at compose" {
            validate
                [
                    {
                        trigger (Tolerance.Absolute 1m) with
                            Table = Bands
                            Metric = Band
                    }
                ]
                (declared ())
            |> refusedNaming "as a scalar"
        }

        test "a trigger written wrongly refuses at the call, each for its reason" {
            let base' = trigger (Tolerance.Absolute 1m)

            for wrong, fragment in
                [
                    { base' with RunKey = " " }, "names no run"
                    { base' with Table = "" }, "names no table"
                    { base' with Metric = "" }, "names no metric"
                    { base' with Subjects = Some [] }, "selects no subject"
                    { base' with Subjects = Some [ " " ] }, "a blank subject"
                    {
                        base' with
                            Subjects = Some [ "a"; "a" ]
                    },
                    "a subject twice"
                    {
                        base' with
                            Comparison = ToleranceComparison.DaysBefore 0
                    },
                    "from 1 to"
                    {
                        base' with
                            Comparison = ToleranceComparison.DaysBefore 3661
                    },
                    "from 1 to"
                    {
                        base' with
                            Tolerance = Tolerance.Absolute -1m
                    },
                    "negative"
                    {
                        base' with
                            Tolerance = Tolerance.Relative -0.1m
                    },
                    "negative"
                ] do
                Expect.throwsC
                    (fun () -> factApp () |> FactsCompose.withToleranceTriggers [ wrong ] |> ignore)
                    (fun ex -> Expect.stringContains ex.Message fragment "names the reason")
        }

        test "the comparison keeps a presented comparison's vocabulary" {
            let names (t: Type) =
                FSharpType.GetUnionCases t |> Array.map _.Name |> List.ofArray

            Expect.equal
                (names typeof<ToleranceComparison>)
                (names typeof<PresentationCompareTo>)
                "the same cases: a seam below the presentation tier cannot name its type"

            Expect.equal ToleranceTrigger.MaxDaysBefore PresentationCompareTo.MaxDaysBefore "the same bound"
        }
    ]

[<Tests>]
let tests =
    testList "ToleranceTrigger" [
        firingTests
        comparisonTests
        gapTests
        onceTests
        refusalTests
        compositionTests
    ]