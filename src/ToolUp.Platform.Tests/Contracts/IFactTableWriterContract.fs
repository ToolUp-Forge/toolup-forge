module ToolUp.Platform.Tests.Contracts.IFactTableWriterContract

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Facts

// ─── IFactTableWriter contract pack (Phase 887) ──────────────────────
//
// Parametrised tests for any `IFactTableWriter`. A binding supplies a
// factory that, given a clock, the declared tables and the metric
// registry, hands back a fresh fixture: the writer, the `IFactStore` its
// facts are readable through, and two isolated scopes. The default writer
// over `BlobFactStore` is bound below; a table-store companion binds the
// same pack a second time.
//
// Coverage: open → write → commit; the all-or-nothing commit (one bad row
// writes nothing and the refusal names the row); every row defect the
// declaration implies; the watermark sequence; the change summary against
// the previous run (new / changed / unchanged / removed / movers); removed
// rows superseded by absences; `AppendByRun` vs `Replace`; the commit
// conflict; abandon + the Required/Optional outcome; overdue runs and
// staleness through the cadence; undeclared tables; scope isolation.
//
// After the pack: the default writer's audit (one run record per run), its
// binding refusal, the provenance case bound to both provenances (a computed
// run and an imported one, Phase 932), a 10,000-subject run answered by the population read,
// and the compose-time fact-table preflight.

/// What a binding hands the pack.
type FactTableWriterFixture = {
    Writer: IFactTableWriter
    Facts: IFactStore
    ScopeA: string
    ScopeB: string
}

/// A binding's factory: clock → declared tables → metric registry → fixture.
type FactTableWriterFactory =
    (unit -> DateTime) -> FactTableDefinition list -> IMetricRegistry -> FactTableWriterFixture

let private t0 = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)

let private metric id : MetricDefinition = {
    Id = id
    Name = id
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
            Definition = metric "revenue"
        }
        {
            MetricRegistration.Module = "sales"
            Definition = metric "segment"
        }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = products
        }
    ]

/// A two-column table at the `sku` level (depth 2).
let private skuTable
    (id: string)
    (history: FactTableHistoryMode)
    (requirement: FactTableRequirement)
    : FactTableDefinition =
    {
        Id = id
        SchemaVersion = 1
        Hierarchy = "products"
        Level = "sku"
        Columns = [
            FactTableDefinition.column "revenue" FactTableValueShape.Scalar
            {
                (FactTableDefinition.column "segment" FactTableValueShape.Categorical) with
                    Disclosure = Some FactTableDisclosure.Internal
            }
        ]
        PeriodGrain = FactTablePeriodGrain.Month
        ProducingOperation = "sales-rollup"
        RefreshCadence = TimeSpan.FromDays 1.0
        HistoryMode = history
        Disclosure = FactTableDisclosure.Surfaceable
        Requirement = requirement
    }

let private september: TemporalExtent = {
    From = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-09"
}

let private row (brand: string) (sku: string) (revenue: decimal) (segment: string) : FactTableRow = {
    Subject = [ brand; sku ]
    Period = september
    Values = Map.ofList [ "revenue", Scalar revenue; "segment", Categorical segment ]
}

let private ok label (r: Async<Result<'T, FactTableWriteError>>) : 'T =
    match Async.RunSynchronously r with
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %s" label (FactTableWriteError.describe e)

let private refused label (r: Async<Result<'T, FactTableWriteError>>) : FactTableWriteError =
    match Async.RunSynchronously r with
    | Ok _ -> failtestf "%s: expected a refusal, got Ok" label
    | Error e -> e

/// Open, stage the rows (in the given batches) and commit.
let private runOf (writer: IFactTableWriter) (scope: string) (tableId: string) (batches: FactTableRow list list) =
    let run = ok "open" (writer.OpenRun(scope, tableId))

    for batch in batches do
        ok "write" (writer.WriteRows(scope, run.RunId, batch)) |> ignore

    run, writer.Commit(scope, run.RunId) |> Async.RunSynchronously

let private committed label (_, result: Result<FactTableCommit, FactTableWriteError>) : FactTableCommit =
    match result with
    | Ok commit -> commit
    | Error e -> failtestf "%s: expected a commit, got %s" label (FactTableWriteError.describe e)

let private tableFacts (facts: IFactStore) (scope: string) (metricId: string) : Fact list =
    facts.Query(
        scope,
        {
            FactQuery.all with
                Metric = Some(MetricRef metricId)
        }
    )
    |> Async.RunSynchronously

let private valueAt (facts: IFactStore) (scope: string) (metricId: string) (path: string list) : FactValue option =
    tableFacts facts scope metricId
    |> List.tryFind (fun f -> f.Subject.Path = path)
    |> Option.map _.Value

/// The contract pack.
let tests (name: string) (factory: FactTableWriterFactory) =
    let fixtureWith (clock: unit -> DateTime) (tables: FactTableDefinition list) = factory clock tables registry

    let fixture (tables: FactTableDefinition list) = fixtureWith (fun () -> t0) tables

    testList $"IFactTableWriter contract — {name}" [

        test "a committed run is the table's current content: watermark, facts, status" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let f = fixture [ table ]

            let commit =
                runOf f.Writer f.ScopeA table.Id [
                    [ row "acme" "a1" 10m "core"; row "acme" "a2" 20m "core" ]
                    [ row "zeta" "z1" 5m "edge" ]
                ]
                |> committed "first run"

            Expect.equal commit.Watermark.Sequence 1L "the first commit is sequence 1"
            Expect.equal commit.Watermark.TableId table.Id "the watermark names the table"
            Expect.equal commit.RowCount 3 "every staged row across both batches is committed"
            Expect.equal commit.FactsWritten 6 "one fact per cell"
            Expect.equal commit.Change.New 3 "every row is new against no previous run"
            Expect.equal (commit.Change.Changed + commit.Change.Unchanged + commit.Change.Removed) 0 "nothing else"
            Expect.isNonEmpty commit.Watermark.ContentDigest "the content digest is minted"

            let revenue = tableFacts f.Facts f.ScopeA "revenue"
            Expect.hasLength revenue 3 "the revenue column is readable as facts"

            for fact in revenue do
                Expect.equal fact.Method (Computed("sales-rollup", "v1", table.Id)) "the table's own lineage"
                Expect.equal fact.Disclosure Disclosure.Surfaceable "the table default disclosure"

            for fact in tableFacts f.Facts f.ScopeA "segment" do
                Expect.equal fact.Disclosure Disclosure.Internal "the column's own disclosure wins"

            let status = ok "status" (f.Writer.Status(f.ScopeA, table.Id))
            Expect.equal status.LastCommit (Some commit) "status reports the last commit"
            Expect.equal status.Freshness (FactTableFreshness.Fresh commit.Watermark.CommittedAt) "fresh"
            Expect.equal status.LatestRunOutcome (Some FactTableRunOutcome.Succeeded) "the latest run succeeded"
        }

        test "one invalid row rejects the whole run, writes nothing, and names the row" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let f = fixture [ table ]

            let bad = {
                (row "acme" "a2" 20m "core") with
                    Subject = [ "acme" ]
            }

            let run, result =
                runOf f.Writer f.ScopeA table.Id [ [ row "acme" "a1" 10m "core"; bad; row "zeta" "z1" 5m "edge" ] ]

            match result with
            | Error(FactTableRowsRejected(runId, defects)) ->
                Expect.equal runId run.RunId "the refusal names the run"
                Expect.equal (defects |> List.map _.Position) [ 1 ] "exactly the bad row, by position"
                Expect.stringContains defects.Head.Subject "acme" "the refusal names the row's subject"
                Expect.stringContains defects.Head.Problem "depth 2" "the refusal says what is wrong"
            | other -> failtestf "expected FactTableRowsRejected, got %A" other

            Expect.isEmpty (tableFacts f.Facts f.ScopeA "revenue") "nothing was written"

            let runs = ok "runs" (f.Writer.Runs(f.ScopeA, table.Id))

            match runs |> List.map _.Status with
            | [ FactTableRunStatus.Rejected reason ] ->
                Expect.stringContains reason "row 1" "the record keeps the reason"
            | other -> failtestf "expected one rejected run, got %A" other

            match refused "write after reject" (f.Writer.WriteRows(f.ScopeA, run.RunId, [ row "a" "b" 1m "c" ])) with
            | FactTableRunClosed _ -> ()
            | other -> failtestf "expected FactTableRunClosed, got %A" other

            let status = ok "status" (f.Writer.Status(f.ScopeA, table.Id))
            Expect.equal status.Freshness FactTableFreshness.NeverRefreshed "a rejected run refreshes nothing"

            match status.LatestRunOutcome with
            | Some(FactTableRunOutcome.Failed _) -> ()
            | other -> failtestf "a rejected run of a Required table is a failed run, got %A" other
        }

        test "every row defect the declaration implies is reported" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Optional

            let f = fixture [ table ]

            let rows = [
                row "acme" "a1" 10m "core" // 0: valid
                {
                    (row "acme" "a2" 1m "core") with
                        Values = Map.ofList [ "revenue", Scalar 1m ]
                } // 1: missing column
                {
                    (row "acme" "a3" 1m "core") with
                        Values = Map.ofList [ "revenue", Categorical "x"; "segment", Categorical "core" ]
                } // 2: wrong shape
                {
                    (row "acme" "a4" 1m "core") with
                        Values = (row "acme" "a4" 1m "core").Values |> Map.add "margin" (Scalar 1m)
                } // 3: undeclared column
                row "acme" "a1" 11m "core" // 4: duplicate key of row 0
                {
                    (row "acme" "a5" 1m "core") with
                        Period = { september with To = september.From }
                } // 5: not half-open
                {
                    (row "acme" "" 1m "core") with
                        Subject = [ "acme"; "" ]
                } // 6: empty member
                {
                    (row "acme" "a7" 1m "core") with
                        Values = Map.ofList [ "revenue", Absent "not computed"; "segment", Absent "not computed" ]
                } // 7: absences are valid in any column
            ]

            match runOf f.Writer f.ScopeA table.Id [ rows ] |> snd with
            | Error(FactTableRowsRejected(_, defects)) ->
                Expect.equal
                    (defects |> List.map _.Position |> List.distinct)
                    [ 1; 2; 3; 4; 5; 6 ]
                    "rows 1–6 and only them"

                let text = defects |> List.map _.Problem |> String.concat " | "
                Expect.stringContains text "no value for column 'segment'" "missing column"
                Expect.stringContains text "declared Scalar" "shape mismatch"
                Expect.stringContains text "'margin' is not a column" "undeclared column"
                Expect.stringContains text "subject-and-period key of row 0" "duplicate key names the first row"
                Expect.stringContains text "half-open" "bad period"
                Expect.stringContains text "empty member" "empty member id"
            | other -> failtestf "expected FactTableRowsRejected, got %A" other
        }

        test "a second run is summarised against the first; a removed row is superseded by an absence" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let f = fixture [ table ]

            runOf f.Writer f.ScopeA table.Id [
                [
                    row "acme" "a1" 10m "core"
                    row "acme" "a2" 20m "core"
                    row "acme" "a3" 30m "core"
                ]
            ]
            |> committed "first"
            |> ignore

            let second =
                runOf f.Writer f.ScopeA table.Id [
                    [
                        row "acme" "a1" 10m "core"
                        row "acme" "a2" 50m "core"
                        row "acme" "a4" 1m "new"
                    ]
                ]
                |> committed "second"

            Expect.equal second.Watermark.Sequence 2L "the sequence advances"
            Expect.equal second.Change.New 1 "a4 is new"
            Expect.equal second.Change.Changed 1 "a2 changed"
            Expect.equal second.Change.Unchanged 1 "a1 unchanged"
            Expect.equal second.Change.Removed 1 "a3 was removed"

            match second.Change.LargestMovers with
            | [ mover ] ->
                Expect.equal
                    (mover.Metric, mover.Previous, mover.Current, mover.Delta)
                    ("revenue", 20m, 50m, 30m)
                    "the mover"

                Expect.stringContains mover.Subject "acme>a2" "the mover names its subject"
            | other -> failtestf "expected one mover, got %A" other

            // Replace: an unchanged cell is an idempotent skip.
            Expect.equal second.FactsWritten (1 + 2 + 2) "a2's revenue, a4's two cells, a3's two absences"

            match valueAt f.Facts f.ScopeA "revenue" [ "acme"; "a3" ] with
            | Some(Absent reason) -> Expect.stringContains reason "removed by fact-table run" "the absence says why"
            | other -> failtestf "a removed row's current value is an absence, got %A" other

            Expect.equal
                (valueAt f.Facts f.ScopeA "revenue" [ "acme"; "a2" ])
                (Some(Scalar 50m))
                "the new value is current"
        }

        test "AppendByRun attributes every cell to its run; an unchanged re-run still writes" {
            let table =
                skuTable "sku-history" FactTableHistoryMode.AppendByRun FactTableRequirement.Optional

            let f = fixture [ table ]
            let rows = [ row "acme" "a1" 10m "core"; row "acme" "a2" 20m "core" ]

            let first = runOf f.Writer f.ScopeA table.Id [ rows ] |> committed "first"
            let second = runOf f.Writer f.ScopeA table.Id [ rows ] |> committed "second"

            Expect.equal first.FactsWritten 4 "every cell"
            Expect.equal second.FactsWritten 4 "every cell again — each run is a complete snapshot"
            Expect.equal second.Change.Unchanged 2 "yet the summary says nothing moved"
            Expect.equal first.Watermark.ContentDigest second.Watermark.ContentDigest "same rows, same digest"

            for fact in tableFacts f.Facts f.ScopeA "revenue" do
                Expect.equal
                    fact.Evidence.ResultRef
                    (Some(FactTableWatermark.render second.Watermark))
                    "current facts name the run that wrote them"
        }

        test "a run opened before another commit is refused rather than summarised against the wrong base" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Optional

            let f = fixture [ table ]
            let early = ok "open early" (f.Writer.OpenRun(f.ScopeA, table.Id))

            ok "stage early" (f.Writer.WriteRows(f.ScopeA, early.RunId, [ row "acme" "a1" 1m "core" ]))
            |> ignore

            runOf f.Writer f.ScopeA table.Id [ [ row "acme" "a1" 2m "core" ] ]
            |> committed "the other run"
            |> ignore

            match refused "stale commit" (f.Writer.Commit(f.ScopeA, early.RunId)) with
            | FactTableCommitConflict(_, openedAgainst, current) ->
                Expect.equal (openedAgainst, current) (0L, 1L) "the conflict names both sequences"
            | other -> failtestf "expected FactTableCommitConflict, got %A" other

            Expect.equal
                (valueAt f.Facts f.ScopeA "revenue" [ "acme"; "a1" ])
                (Some(Scalar 2m))
                "the stale run wrote nothing"
        }

        test "an abandoned run fails a Required table and is discarded for an Optional one" {
            let required =
                skuTable "required-table" FactTableHistoryMode.Replace FactTableRequirement.Required

            let optional =
                skuTable "optional-table" FactTableHistoryMode.Replace FactTableRequirement.Optional

            let f = fixture [ required; optional ]

            for table in [ required; optional ] do
                let run = ok "open" (f.Writer.OpenRun(f.ScopeA, table.Id))

                ok "abandon" (f.Writer.Abandon(f.ScopeA, run.RunId, "upstream data missing"))
                |> ignore

            match (ok "required status" (f.Writer.Status(f.ScopeA, required.Id))).LatestRunOutcome with
            | Some(FactTableRunOutcome.Failed reason) ->
                Expect.stringContains reason "upstream data missing" "reason kept"
            | other -> failtestf "expected Failed, got %A" other

            match (ok "optional status" (f.Writer.Status(f.ScopeA, optional.Id))).LatestRunOutcome with
            | Some(FactTableRunOutcome.Discarded _) -> ()
            | other -> failtestf "expected Discarded, got %A" other
        }

        test "an open run past the cadence reads as failed, and a missed refresh reads as stale" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let now = ref t0
            let f = fixtureWith (fun () -> now.Value) [ table ]

            let commit =
                runOf f.Writer f.ScopeA table.Id [ [ row "acme" "a1" 1m "core" ] ]
                |> committed "first"

            ok "open a second" (f.Writer.OpenRun(f.ScopeA, table.Id)) |> ignore

            now.Value <- t0.AddHours 12.0
            let halfway = ok "halfway" (f.Writer.Status(f.ScopeA, table.Id))
            Expect.equal halfway.LatestRunOutcome (Some FactTableRunOutcome.InProgress) "within the cadence"
            Expect.equal halfway.Freshness (FactTableFreshness.Fresh commit.Watermark.CommittedAt) "still fresh"

            now.Value <- t0.AddDays 3.0
            let late = ok "late" (f.Writer.Status(f.ScopeA, table.Id))

            match late.LatestRunOutcome with
            | Some(FactTableRunOutcome.Failed reason) -> Expect.stringContains reason "refresh cadence" "overdue"
            | other -> failtestf "expected Failed, got %A" other

            match late.Freshness with
            | FactTableFreshness.Stale(at, overdueBy) ->
                Expect.equal at commit.Watermark.CommittedAt "stale since the last commit"
                Expect.equal overdueBy (TimeSpan.FromDays 2.0) "overdue by age minus cadence"
            | other -> failtestf "expected Stale, got %A" other
        }

        test "an undeclared table is refused by name" {
            let f =
                fixture [
                    skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Optional
                ]

            match refused "open" (f.Writer.OpenRun(f.ScopeA, "ghost")) with
            | FactTableUndeclared "ghost" -> ()
            | other -> failtestf "expected FactTableUndeclared, got %A" other

            match refused "commit unknown" (f.Writer.Commit(f.ScopeA, "no-such-run")) with
            | FactTableRunUnknown "no-such-run" -> ()
            | other -> failtestf "expected FactTableRunUnknown, got %A" other
        }

        test "runs, tables and facts are isolated per scope" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Optional

            let f = fixture [ table ]

            runOf f.Writer f.ScopeA table.Id [ [ row "acme" "a1" 1m "core" ] ]
            |> committed "scope A"
            |> ignore

            Expect.isEmpty (tableFacts f.Facts f.ScopeB "revenue") "no facts leak"
            Expect.isEmpty (ok "runs B" (f.Writer.Runs(f.ScopeB, table.Id))) "no runs leak"

            let status = ok "status B" (f.Writer.Status(f.ScopeB, table.Id))
            Expect.equal status.Freshness FactTableFreshness.NeverRefreshed "scope B never refreshed"

            let firstB =
                runOf f.Writer f.ScopeB table.Id [ [ row "acme" "a1" 1m "core" ] ]
                |> committed "scope B"

            Expect.equal firstB.Watermark.Sequence 1L "each scope keeps its own sequence"
        }
    ]

// ─── Binding: the default writer over BlobFactStore ──────────────────

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

type private DefaultWorld = {
    Fixture: FactTableWriterFixture
    Events: IEventStore
    Storage: IBlobStorage
}

let private defaultWorld
    (clock: unit -> DateTime)
    (tables: FactTableDefinition list)
    (metrics: IMetricRegistry)
    (bindings: FactTableBinding list)
    : DefaultWorld =
    let storage = InMemoryBlobStorage.InMemoryBlobStorage()
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let facts = BlobFactStore.createWithRegistry storage events (Some metrics)

    let tableRegistry =
        FactTableRegistry.build
            (tables
             |> List.map (fun t -> {
                 FactTableRegistration.Module = "sales"
                 Definition = t
             }))
            bindings

    {
        Fixture = {
            Writer = DefaultFactTableWriter.createWithClock facts storage events tableRegistry (Some metrics) clock
            Facts = facts
            ScopeA = newScope ()
            ScopeB = newScope ()
        }
        Events = events
        Storage = storage
    }

let private bindAll = [ BindAllFactTables DefaultFactTableWriter.Destination ]

/// The default writer as a contract-pack factory, binding every declared table.
/// The pack itself is bound from the test runner, by its qualified name.
let defaultWriterFactory: FactTableWriterFactory =
    fun clock tables metrics -> (defaultWorld clock tables metrics bindAll).Fixture

let private runEvents (events: IEventStore) (scope: string) : ModuleEvent list =
    events.ReadBySource(scope, FactEvents.SourceModule) |> Async.RunSynchronously

/// The default writer's own obligations: audit, binding, scale.
let defaultWriterObligationTests =
    testList "DefaultFactTableWriter — audit, binding, scale" [

        test "a run writes one run record to the audit trail, never one per row" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let world = defaultWorld (fun () -> t0) [ table ] registry bindAll
            let f = world.Fixture

            let commit =
                runOf f.Writer f.ScopeA table.Id [
                    [ for i in 1..50 -> row "acme" (sprintf "s%d" i) (decimal i) "core" ]
                ]
                |> committed "run"

            let events = runEvents world.Events f.ScopeA

            let runRecords =
                events |> List.filter (fun e -> e.EventType = FactTableEvents.RunCommittedType)

            Expect.hasLength runRecords 1 "one run record"

            Expect.stringContains
                runRecords.Head.Payload
                commit.Watermark.ContentDigest
                "the record carries the watermark"

            Expect.isEmpty
                (events |> List.filter (fun e -> e.EventType = FactEvents.AssertedType))
                "no per-fact assertion rows"

            Expect.hasLength
                (events |> List.filter (fun e -> e.EventType = FactEvents.BatchAssertedType))
                1
                "the fact store's own write receipt is one summarised row, which the run record cites by digest"

            Expect.stringContains runRecords.Head.Payload commit.BatchDigest "the run record cites the batch digest"
        }

        test "a rejected or abandoned run is audited once too" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let world = defaultWorld (fun () -> t0) [ table ] registry bindAll
            let f = world.Fixture

            runOf f.Writer f.ScopeA table.Id [
                [
                    {
                        (row "a" "b" 1m "c") with
                            Subject = [ "a" ]
                    }
                ]
            ]
            |> ignore

            let open' = ok "open" (f.Writer.OpenRun(f.ScopeA, table.Id))
            ok "abandon" (f.Writer.Abandon(f.ScopeA, open'.RunId, "cancelled")) |> ignore

            let types = runEvents world.Events f.ScopeA |> List.map _.EventType |> List.sort

            Expect.equal
                types
                [ FactTableEvents.RunAbandonedType; FactTableEvents.RunRejectedType ]
                "one record per ended run, and no fact rows"
        }

        test "a table bound elsewhere is refused by the default writer, naming the binding" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let world =
                defaultWorld (fun () -> t0) [ table ] registry [ BindFactTable(table.Id, "warehouse") ]

            match refused "open" (world.Fixture.Writer.OpenRun(world.Fixture.ScopeA, table.Id)) with
            | FactTableNotBoundHere(id, Some "warehouse", writer) ->
                Expect.equal (id, writer) (table.Id, DefaultFactTableWriter.Destination) "names table and writer"
            | other -> failtestf "expected FactTableNotBoundHere, got %A" other
        }

        // ── The provenance case, bound to both provenances (Phase 932) ──

        test "provenance: an unstaged run and a ComputedRun-staged run write the table's own lineage" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let world = defaultWorld (fun () -> t0) [ table ] registry bindAll
            let f = world.Fixture

            runOf f.Writer f.ScopeA table.Id [ [ row "acme" "a1" 10m "core" ] ]
            |> committed "unstaged"
            |> ignore

            let run = ok "open" (f.Writer.OpenRun(f.ScopeB, table.Id))
            ok "stage" (FactTableRunProvenance.stage world.Storage f.ScopeB run.RunId ComputedRun)

            ok "write" (f.Writer.WriteRows(f.ScopeB, run.RunId, [ row "acme" "a1" 10m "core" ]))
            |> ignore

            ok "commit" (f.Writer.Commit(f.ScopeB, run.RunId)) |> ignore

            for scope in [ f.ScopeA; f.ScopeB ] do
                let facts = tableFacts f.Facts scope "revenue" @ tableFacts f.Facts scope "segment"
                Expect.hasLength facts 2 "one fact per cell"

                for fact in facts do
                    Expect.equal fact.Method (Computed("sales-rollup", "v1", table.Id)) "the table's own lineage"

                Expect.isEmpty
                    (world.Storage.List(scope, "_fact-tables/provenance/") |> Async.RunSynchronously)
                    "a computed run leaves no provenance behind (GP 11)"
        }

        test "provenance: an ImportedRun writes each origin's rows Imported, floored, and names a withdrawal" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let world = defaultWorld (fun () -> t0) [ table ] registry bindAll
            let f = world.Fixture
            let restricted = Disclosure.Restricted "pricing"

            let cell (brand: string) (sku: string) (metricId: string) (d: Disclosure) : FactTableImportedCell = {
                Subject = [ brand; sku ]
                Metric = metricId
                From = september.From
                To = september.To
                Disclosure = d
            }

            let origin (brand: string) (withdrawal: string option) (cells: FactTableImportedCell list) = {
                RootMember = brand
                CertificateRef = "cert:" + brand
                TriggerRef = "import:" + brand
                Withdrawal = withdrawal
                Cells = cells
            }

            let acme =
                origin "acme" None [
                    cell "acme" "a1" "revenue" restricted
                    cell "acme" "a2" "revenue" Disclosure.Surfaceable
                ]

            let importedRun (origins: FactTableImportOrigin list) (rows: FactTableRow list) =
                let run = ok "open" (f.Writer.OpenRun(f.ScopeA, table.Id))
                ok "stage" (FactTableRunProvenance.stage world.Storage f.ScopeA run.RunId (ImportedRun origins))
                ok "write" (f.Writer.WriteRows(f.ScopeA, run.RunId, rows)) |> ignore
                ok "commit" (f.Writer.Commit(f.ScopeA, run.RunId))

            importedRun [
                acme
                origin "beta" None [ cell "beta" "b1" "revenue" Disclosure.Surfaceable ]
            ] [
                row "acme" "a1" 10m "core"
                row "acme" "a2" 20m "core"
                row "zeta" "z1" 5m "edge"
                row "beta" "b1" 7m "edge"
            ]
            |> ignore

            importedRun [ acme; origin "beta" (Some "withdrawn: beta left") [] ] [
                row "acme" "a1" 10m "core"
                row "acme" "a2" 20m "core"
                row "zeta" "z1" 5m "edge"
            ]
            |> ignore

            let factAt (metricId: string) (path: string list) =
                match
                    tableFacts f.Facts f.ScopeA metricId
                    |> List.filter (fun x -> x.Subject.Path = path)
                with
                | [ fact ] -> fact
                | other -> failtestf "expected one current %s fact at %A, got %d" metricId path other.Length

            let a1 = factAt "revenue" [ "acme"; "a1" ]
            Expect.equal a1.Method (Imported "cert:acme") "an origin's row is Imported, naming its certificate"
            Expect.equal a1.Evidence.TriggerRef (Some "import:acme") "and its evidence names the origin"

            Expect.equal
                a1.Disclosure
                (Disclosure.floor restricted Disclosure.Surfaceable)
                "no wider than the origin published it"

            Expect.equal
                (factAt "revenue" [ "acme"; "a2" ]).Disclosure
                Disclosure.Surfaceable
                "and no narrower than the floor"

            Expect.equal
                (factAt "segment" [ "acme"; "a1" ]).Disclosure
                Disclosure.Internal
                "a cell the origin did not place is narrowed to the bottom"

            Expect.equal
                (factAt "revenue" [ "zeta"; "z1" ]).Method
                (Computed("sales-rollup", "v1", table.Id))
                "a row under no origin keeps the table's own lineage"

            let b1 = factAt "revenue" [ "beta"; "b1" ]
            Expect.equal b1.Value (Absent "withdrawn: beta left") "a withdrawn origin's removal names the withdrawal"
            Expect.equal b1.Method (Imported "cert:beta") "in the origin's own lineage"

            Expect.isEmpty
                (world.Storage.List(f.ScopeA, "_fact-tables/provenance/")
                 |> Async.RunSynchronously)
                "the staged provenance is discarded with the run's staging"
        }

        test "provenance: only an open run takes one, and an abandoned run drops it" {
            let table =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let world = defaultWorld (fun () -> t0) [ table ] registry bindAll
            let f = world.Fixture
            let imported = ImportedRun []

            match refused "unknown" (FactTableRunProvenance.stage world.Storage f.ScopeA "no-such-run" imported) with
            | FactTableRunUnknown "no-such-run" -> ()
            | other -> failtestf "expected FactTableRunUnknown, got %A" other

            let run, _ = runOf f.Writer f.ScopeA table.Id [ [ row "acme" "a1" 10m "core" ] ]

            match refused "committed" (FactTableRunProvenance.stage world.Storage f.ScopeA run.RunId imported) with
            | FactTableRunClosed(id, "committed") -> Expect.equal id run.RunId "names the run"
            | other -> failtestf "expected FactTableRunClosed, got %A" other

            let pending = ok "open" (f.Writer.OpenRun(f.ScopeA, table.Id))
            ok "stage" (FactTableRunProvenance.stage world.Storage f.ScopeA pending.RunId imported)

            Expect.isNonEmpty
                (world.Storage.List(f.ScopeA, "_fact-tables/provenance/")
                 |> Async.RunSynchronously)
                "staged beside the run (the probe below is not vacuous)"

            ok "abandon" (f.Writer.Abandon(f.ScopeA, pending.RunId, "changed my mind"))
            |> ignore

            Expect.isEmpty
                (world.Storage.List(f.ScopeA, "_fact-tables/provenance/")
                 |> Async.RunSynchronously)
                "an abandoned run's provenance goes with it"
        }

        test "a 10,000-subject run commits once and the population read answers over it" {
            let table = {
                (skuTable "sku-population" FactTableHistoryMode.Replace FactTableRequirement.Required) with
                    Columns = [ FactTableDefinition.column "revenue" FactTableValueShape.Scalar ]
            }

            let world = defaultWorld (fun () -> t0) [ table ] registry bindAll
            let f = world.Fixture

            let rows = [
                for i in 1..10_000 ->
                    {
                        Subject = [ sprintf "b%d" (i % 100); sprintf "s%d" i ]
                        Period = september
                        Values = Map.ofList [ "revenue", Scalar(decimal i) ]
                    }
            ]

            let commit =
                runOf f.Writer f.ScopeA table.Id (rows |> List.chunkBySize 2_500)
                |> committed "population run"

            Expect.equal commit.RowCount 10_000 "every row committed"
            Expect.equal commit.FactsWritten 10_000 "one fact per subject"

            let population =
                f.Facts.QueryPopulation(
                    f.ScopeA,
                    {
                        (PopulationQuery.create (MetricRef "revenue") "products") with
                            Level = Some 2
                            Ordering = Descending
                            TopK = 3
                    }
                )
                |> Async.RunSynchronously

            match population with
            | Ok result ->
                Expect.equal result.Stats.SubjectCount 10_000 "the population is the table"

                Expect.equal
                    (result.Ranked |> List.map _.Value)
                    [ Scalar 10_000m; Scalar 9_999m; Scalar 9_998m ]
                    "ranked"
            | Error e -> failtestf "population read refused: %s" e

            let runRecords =
                runEvents world.Events f.ScopeA
                |> List.filter (fun e -> e.EventType = FactTableEvents.RunCommittedType)

            Expect.hasLength runRecords 1 "one audit record for the run"
        }
    ]

// ─── The compose-time preflight (FactTablePreflight) ─────────────────

let private salesModule (tables: FactTableDefinition list) =
    ServerModule.create "sales"
    |> ServerModule.declareMetrics [ metric "revenue"; metric "segment" ]
    |> ServerModule.declareSubjects [ products ]
    |> ServerModule.declareFactTables tables

let private codes (app: ServerApp) =
    FactTablePreflight.defects (ServerApp.factTableComposition app)
    |> List.map (fun d -> d.RuleCode, d.Severity)

let private factStoreApp () = {
    ServerApp.empty with
        Config = {
            ServerConfig.defaults with
                FactStore = EnabledFactStore
        }
}

let private validate (app: ServerApp) : ConfigValidation.ValidationResult =
    let validator =
        FactTablePreflight.FactTableDeclarationValidator(ServerApp.factTableComposition app)
        :> ConfigValidation.IConfigValidator

    validator.Validate() |> Async.RunSynchronously

let preflightTests =
    testList "Fact-table declarations — compose-time preflight (Phase 887)" [

        test "declareFactTables composes left to right and fans into the app with its module" {
            let a = skuTable "t-a" FactTableHistoryMode.Replace FactTableRequirement.Optional
            let b = skuTable "t-b" FactTableHistoryMode.Replace FactTableRequirement.Optional

            let m =
                ServerModule.create "sales"
                |> ServerModule.declareFactTables [ a ]
                |> ServerModule.declareFactTables [ b ]

            Expect.equal (m.FactTables |> List.map _.Id) [ "t-a"; "t-b" ] "declaration order kept"

            let app = ServerApp.empty |> ServerApp.addModules [ m ]

            Expect.equal
                (app.RegisteredFactTables |> List.map (fun r -> r.Module, r.Definition.Id))
                [ "sales", "t-a"; "sales", "t-b" ]
                "each registration names its module"
        }

        test "a composition with no table declaration registers nothing and carries no table state" {
            let app = ServerApp.empty |> ServerApp.addModules [ ServerModule.create "plain" ]

            Expect.isEmpty app.RegisteredFactTables "no tables"
            Expect.isEmpty app.FactTableBindings "no bindings"
            Expect.isEmpty (ServerModule.create "plain").FactTables "a module declares none by default"

            let services = ServiceCollection() :> IServiceCollection
            let before = services.Count

            FactTablePreflight.serviceRegistration (ServerApp.factTableComposition app) services
            |> ignore

            Expect.equal services.Count before "no validator is registered"
        }

        test "a well-formed, bound composition has no defects and validates Ok" {
            let app =
                factStoreApp ()
                |> ServerApp.addModules [
                    salesModule [
                        skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required
                    ]
                ]
                |> FactsCompose.withFactTableWriter

            Expect.isEmpty (codes app) "no defects"
            Expect.equal (validate app) ConfigValidation.Ok "the validator passes"

            let services = ServiceCollection() :> IServiceCollection

            FactTablePreflight.serviceRegistration (ServerApp.factTableComposition app) services
            |> ignore

            Expect.equal services.Count 1 "the structural validator is registered for a declaring composition"
        }

        test "a Required table bound to no store refuses to start, naming the table" {
            let app =
                ServerApp.empty
                |> ServerApp.addModules [
                    salesModule [
                        skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required
                    ]
                ]

            Expect.equal (codes app) [ FactTablePreflight.UnboundRequiredRule, DefectError ] "the construction hole"

            match validate app with
            | ConfigValidation.Error message ->
                Expect.stringContains message "sku-sales" "names the table"
                Expect.stringContains message "sales" "names the declaring module"
            | other -> failtestf "expected Error, got %A" other

            // withFactTableWriter without the fact store binds nothing, so the
            // hole stays — it is not papered over.
            let noStore = app |> FactsCompose.withFactTableWriter
            Expect.equal (codes noStore) [ FactTablePreflight.UnboundRequiredRule, DefectError ] "still unbound"

            // An Optional table with no store is not a hole.
            let optional =
                ServerApp.empty
                |> ServerApp.addModules [
                    salesModule [
                        skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Optional
                    ]
                ]

            Expect.isEmpty (codes optional) "optional output may go unbound"
        }

        test "unregistered metrics, unknown levels, malformed and duplicate tables are errors naming them" {
            let good =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Optional

            let badColumn = {
                good with
                    Id = "bad-column"
                    Columns = [ FactTableDefinition.column "margin" FactTableValueShape.Scalar ]
            }

            let badLevel = {
                good with
                    Id = "bad-level"
                    Level = "store"
            }

            let badHierarchy = {
                good with
                    Id = "bad-hierarchy"
                    Hierarchy = "geo"
            }

            let malformed = {
                good with
                    Id = "malformed"
                    ProducingOperation = ""
                    RefreshCadence = TimeSpan.Zero
                    Columns = [
                        FactTableDefinition.column "revenue" FactTableValueShape.Scalar
                        FactTableDefinition.column "revenue" FactTableValueShape.Scalar
                    ]
            }

            let other = ServerModule.create "finance" |> ServerModule.declareFactTables [ good ]

            let app =
                ServerApp.empty
                |> ServerApp.addModules [ salesModule [ good; badColumn; badLevel; badHierarchy; malformed ]; other ]

            let defects = FactTablePreflight.defects (ServerApp.factTableComposition app)

            let byCode code =
                defects |> List.filter (fun d -> d.RuleCode = code) |> List.map _.Message

            Expect.stringContains
                (byCode FactTablePreflight.UnregisteredMetricRule |> String.concat "\n")
                "'margin'"
                "metric"

            Expect.hasLength (byCode FactTablePreflight.UnknownSubjectLevelRule) 2 "the level and the hierarchy"

            Expect.stringContains
                (byCode FactTablePreflight.UnknownSubjectLevelRule |> String.concat "\n")
                "'brand', 'sku'"
                "the level refusal enumerates the hierarchy's levels"

            let malformedText =
                byCode FactTablePreflight.MalformedTableRule |> String.concat "\n"

            Expect.stringContains malformedText "producing operation is empty" "operation"
            Expect.stringContains malformedText "cadence is not positive" "cadence"
            Expect.stringContains malformedText "metric 'revenue' is declared in 2 columns" "duplicate column"

            match byCode FactTablePreflight.DuplicateTableRule with
            | [ message ] -> Expect.stringContains message "'finance', 'sales'" "both modules named"
            | other -> failtestf "expected one duplicate defect, got %A" other

            // The fixture's tables also share metrics at one level, which is the
            // (warning-only) two-homes rule; every OTHER defect is an error.
            Expect.isTrue
                (defects
                 |> List.filter (fun d -> d.RuleCode <> FactTablePreflight.MetricTwoHomesRule)
                 |> List.forall (fun d -> d.Severity = DefectError))
                "every one of these is an error"
        }

        test "a metric with two homes at one level is a warning naming both" {
            let first =
                skuTable "sku-a" FactTableHistoryMode.Replace FactTableRequirement.Optional

            let second =
                skuTable "sku-b" FactTableHistoryMode.Replace FactTableRequirement.Optional

            let twoTables =
                ServerApp.empty |> ServerApp.addModules [ salesModule [ first; second ] ]

            match FactTablePreflight.defects (ServerApp.factTableComposition twoTables) with
            | defects when
                defects
                |> List.forall (fun d -> d.RuleCode = FactTablePreflight.MetricTwoHomesRule)
                ->
                let text = defects |> List.map _.Message |> String.concat "\n"
                Expect.stringContains text "sku-a" "first table"
                Expect.stringContains text "sku-b" "second table"
                Expect.isTrue (defects |> List.forall (fun d -> d.Severity = DefectWarning)) "a warning, not an error"
            | other -> failtestf "expected only two-homes warnings, got %A" other

            let recomputed =
                ServerModule.create "sales"
                |> ServerModule.declareMetrics [
                    {
                        (metric "revenue") with
                            RecomputePolicy = Some Eager
                    }
                    metric "segment"
                ]
                |> ServerModule.declareSubjects [ products ]
                |> ServerModule.declareFactTables [ first ]

            let app = ServerApp.empty |> ServerApp.addModules [ recomputed ]

            match FactTablePreflight.defects (ServerApp.factTableComposition app) with
            | [ d ] ->
                Expect.equal d.RuleCode FactTablePreflight.MetricTwoHomesRule "the two-homes rule"
                Expect.stringContains d.Message "sku-a" "names the table"
                Expect.stringContains d.Message "Eager" "names the fact-by-fact home"
            | other -> failtestf "expected one warning, got %A" other

            match validate app with
            | ConfigValidation.Warning _ -> ()
            | other -> failtestf "a warning does not block the start, got %A" other
        }

        test "the rule manifest names every code the preflight can emit, all structural" {
            let codes = FactTablePreflight.ruleManifest |> List.map _.Code

            Expect.equal
                (Set.ofList codes)
                (Set.ofList [
                    FactTablePreflight.DuplicateTableRule
                    FactTablePreflight.MalformedTableRule
                    FactTablePreflight.UnregisteredMetricRule
                    FactTablePreflight.UnknownSubjectLevelRule
                    FactTablePreflight.UnboundRequiredRule
                    FactTablePreflight.MetricTwoHomesRule
                ])
                "the published codes"

            Expect.isTrue
                (FactTablePreflight.classifiedRuleManifest
                 |> List.forall (fun r -> r.Class = StructuralRule))
                "every rule is structural"
        }

        test "an explicit binding wins over a bind-all, and the registry resolves it" {
            let t =
                skuTable "sku-sales" FactTableHistoryMode.Replace FactTableRequirement.Required

            let bindings = [ BindAllFactTables "fact-store"; BindFactTable("sku-sales", "warehouse") ]

            Expect.equal (FactTableBinding.destinationOf bindings "sku-sales") (Some "warehouse") "explicit wins"

            Expect.equal
                (FactTableBinding.destinationOf bindings "other")
                (Some "fact-store")
                "bind-all covers the rest"

            Expect.equal (FactTableBinding.destinationOf [] "sku-sales") None "unbound"

            let registration: FactTableRegistration = { Module = "sales"; Definition = t }
            let reg = FactTableRegistry.build [ registration ] bindings

            Expect.equal (reg.DestinationOf "sku-sales") (Some "warehouse") "registry resolves the binding"
            Expect.equal (reg.DestinationOf "ghost") None "an undeclared table has no destination"
            Expect.equal (reg.DeclaringModule "sku-sales") (Some "sales") "the declaring module"
        }
    ]