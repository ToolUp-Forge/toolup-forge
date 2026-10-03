module ToolUp.Platform.Tests.Contracts.IFactTableOrphanSweepContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Facts

// ─── IFactTableOrphanSweep contract pack (Phase 977) ─────────────────
//
// Parametrised tests for any writer implementing `IFactTableOrphanSweep`,
// driven only through the seam a host uses (`FactTableOrphanSweep.sweep`
// over the composed `IFactTableWriter`) and through the blob store the
// writer keeps its runs in — never through a writer's private layout.
//
// What the seam PROMISES, whatever the implementation:
//
//   - **It reclaims what it is entitled to.** An ended run whose staged rows
//     and provenance survived a refused delete is swept: the blobs go, and
//     the report counts them.
//   - **It never touches what is not a leftover.** A run that is still open,
//     a run with no record yet (one being opened — its provenance is kept
//     before its record), and a committed run's provenance when the writer
//     keeps it (the binding declares whether it does) all survive.
//   - **It is idempotent.** A second sweep over the same state reports
//     nothing and deletes nothing.
//   - **Refusals are reported, not swallowed.** A sweep the store refuses
//     names every blob it could not delete, leaves them at rest, and the
//     next sweep reclaims them.
//
// Bound to every production implementation: the default writer, the
// delegate writer, and the fact-browse notifying decorator (over the
// default writer).

/// A blob store whose deletes can be refused on demand — the post-terminal
/// deletes a writer makes, refused as a real store can refuse them.
type RefusableBlobStorage(inner: IBlobStorage) =
    /// While true, every `Delete` is refused.
    member val Refusing = false with get, set

    interface IBlobStorage with
        member _.CanComposeFrom = inner.CanComposeFrom

        member _.ComposeFrom(container, targetBlobName, sourceBlobNames) =
            inner.ComposeFrom(container, targetBlobName, sourceBlobNames)

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member this.Delete(container, blobName) =
            if this.Refusing then
                async { return Error(sprintf "refused: %s" blobName) }
            else
                inner.Delete(container, blobName)

        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// What a binding hands the pack.
type FactTableOrphanSweepFixture = {
    /// The writer, as a host composes it.
    Writer: IFactTableWriter
    /// The store the writer keeps its runs in.
    Storage: RefusableBlobStorage
    /// A fresh scope.
    Scope: string
    /// The declared table the pack writes ("sku-sales").
    TableId: string
    /// Whether the writer keeps a committed run's provenance past its end.
    KeepsCommittedProvenance: bool
    /// Plant a provenance blob for a run id the way `OpenRun` keeps one
    /// before writing the run's record — a run with no record yet.
    PlantUnrecordedProvenance: string -> string -> unit
}

/// A binding's factory.
type FactTableOrphanSweepFactory = unit -> FactTableOrphanSweepFixture

// ─── Shared declarations ─────────────────────────────────────────────

let private t0 = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)

let private revenue: MetricDefinition = {
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
            Definition = revenue
        }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = products
        }
    ]

let private skuSales: FactTableDefinition = {
    Id = "sku-sales"
    SchemaVersion = 1
    Hierarchy = "products"
    Level = "sku"
    Columns = [ FactTableDefinition.column "revenue" FactTableValueShape.Scalar ]
    PeriodGrain = FactTablePeriodGrain.Month
    ProducingOperation = "sales-rollup"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.AppendByRun
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

let private september: TemporalExtent = {
    From = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-09"
}

let private rows (n: int) : FactTableRow list = [
    for i in 1..n ->
        {
            Subject = [ "brand-01"; sprintf "sku-%04d" i ]
            Period = september
            Values = Map.ofList [ "revenue", Scalar(decimal i) ]
        }
]

let private origin: FactTableImportOrigin = {
    RootMember = "brand-01"
    CertificateRef = "cert:brand-01"
    TriggerRef = "import:brand-01"
    Withdrawal = None
    Cells = []
}

let private ok label (r: Async<Result<'T, FactTableWriteError>>) : 'T =
    match Async.RunSynchronously r with
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %s" label (FactTableWriteError.describe e)

// ─── The pack ────────────────────────────────────────────────────────

/// Every blob at rest in the fixture's scope.
let private atRest (f: FactTableOrphanSweepFixture) : Set<string> =
    (f.Storage :> IBlobStorage).List(f.Scope, "")
    |> Async.RunSynchronously
    |> Set.ofList

/// Open an imported run (so it keeps a provenance) and stage `batches`.
let private openStaged (f: FactTableOrphanSweepFixture) (batches: int list) : FactTableRunRecord =
    let run = ok "open" (f.Writer.OpenRun(f.Scope, f.TableId, ImportedRun [ origin ]))

    for n in batches do
        ok "write" (f.Writer.WriteRows(f.Scope, run.RunId, rows n)) |> ignore

    run

let private sweep (f: FactTableOrphanSweepFixture) : FactTableOrphanSweepReport =
    ok "sweep" (FactTableOrphanSweep.sweep f.Writer f.Scope)

/// An ended run whose two staged batches and provenance survived refused
/// deletes; returns the run and the leftovers it left (the blobs that
/// would have gone, read off the store).
let private refusedLeftovers (f: FactTableOrphanSweepFixture) : FactTableRunRecord * Set<string> =
    let before = atRest f
    let run = openStaged f [ 3; 3 ]
    let staged = atRest f
    f.Storage.Refusing <- true
    ok "abandon" (f.Writer.Abandon(f.Scope, run.RunId, "ended")) |> ignore
    f.Storage.Refusing <- false
    // What the run added while open, less its record, is what an ended run
    // must not keep. The record is the one blob still named by the run that
    // the run ledger reads; every other blob the run added is a leftover.
    let added = Set.difference staged before

    let record =
        added
        |> Set.filter (fun name ->
            match (f.Storage :> IBlobStorage).Download(f.Scope, name) |> Async.RunSynchronously with
            | Ok bytes ->
                let text = Text.Encoding.UTF8.GetString bytes
                text.Contains "\"StagedRows\"" && text.Contains run.RunId
            | Error _ -> false)

    run, Set.difference added record

/// The pack, bound once per implementation.
let tests (name: string) (factory: FactTableOrphanSweepFactory) =
    testList (sprintf "IFactTableOrphanSweep contract - %s" name) [

        test "an ended run's leftovers are reclaimed, and the report counts them" {
            let f = factory ()
            let _, leftovers = refusedLeftovers f

            Expect.hasLength (Set.toList leftovers) 3 "two staged batches and one provenance survived the refusal"
            Expect.isTrue (Set.isSubset leftovers (atRest f)) "and are at rest"

            let report = sweep f
            Expect.equal report.RunsSwept 1 "one ended run swept"
            Expect.equal report.StagedBlobsDeleted 2 "its two staged batches"
            Expect.equal report.ProvenanceBlobsDeleted 1 "and its provenance"
            Expect.isEmpty report.Refused "nothing refused"
            Expect.isEmpty report.Unreadable "nothing unreadable"
            Expect.isEmpty (Set.intersect leftovers (atRest f)) "none of them is left at rest"
        }

        test "a second sweep finds nothing (idempotent)" {
            let f = factory ()
            refusedLeftovers f |> ignore
            sweep f |> ignore
            let after = atRest f

            Expect.equal (sweep f) FactTableOrphanSweepReport.empty "the second sweep reports nothing"
            Expect.equal (atRest f) after "and deletes nothing"
        }

        test "an open run is never touched" {
            let f = factory ()
            let live = openStaged f [ 3 ]
            let liveBlobs = atRest f
            refusedLeftovers f |> ignore

            let report = sweep f
            Expect.equal report.RunsSwept 1 "only the ended run is swept"
            Expect.isTrue (Set.isSubset liveBlobs (atRest f)) "every blob of the open run is still at rest"

            let commit = ok "commit" (f.Writer.Commit(f.Scope, live.RunId))
            Expect.equal commit.RowCount 3 "and the open run still commits every row it staged"
        }

        test "a run with no record is never touched" {
            let f = factory ()
            let runId = Guid.NewGuid().ToString("N")
            f.PlantUnrecordedProvenance f.Scope runId
            let planted = atRest f
            Expect.isNonEmpty (Set.toList planted) "the planted provenance is at rest"

            let report = sweep f
            Expect.equal report.RunsSwept 0 "no run swept"
            Expect.equal report.ProvenanceBlobsDeleted 0 "no provenance deleted"
            Expect.equal (atRest f) planted "the provenance of a run being opened survives"
        }

        test "a committed run's provenance is kept or reclaimed exactly as the writer declares" {
            let f = factory ()
            let run = openStaged f [ 3 ]
            f.Storage.Refusing <- true
            ok "commit" (f.Writer.Commit(f.Scope, run.RunId)) |> ignore
            f.Storage.Refusing <- false

            let report = sweep f
            Expect.equal report.StagedBlobsDeleted 1 "the committed run's staged batch is reclaimed"

            if f.KeepsCommittedProvenance then
                Expect.equal report.ProvenanceBlobsDeleted 0 "a kept provenance is never touched"
                let again = sweep f
                Expect.equal again FactTableOrphanSweepReport.empty "nor by any later sweep"
            else
                Expect.equal report.ProvenanceBlobsDeleted 1 "a provenance the writer does not keep is reclaimed"
        }

        test "refusals are reported, not swallowed, and the next sweep reclaims them" {
            let f = factory ()
            let _, leftovers = refusedLeftovers f

            f.Storage.Refusing <- true
            let refused = sweep f
            f.Storage.Refusing <- false

            Expect.equal (refused.StagedBlobsDeleted + refused.ProvenanceBlobsDeleted) 0 "nothing deleted"
            Expect.hasLength refused.Refused 3 "every blob it could not delete is named"

            for name in leftovers do
                Expect.isTrue
                    (refused.Refused |> List.exists (fun r -> r.Contains name))
                    (sprintf "the refusal names %s" name)

            Expect.isTrue (Set.isSubset leftovers (atRest f)) "and they are still at rest"

            let retried = sweep f
            Expect.equal (retried.StagedBlobsDeleted + retried.ProvenanceBlobsDeleted) 3 "the next sweep reclaims them"
            Expect.isEmpty (Set.intersect leftovers (atRest f)) "none is left"
        }
    ]

// ─── Bindings ────────────────────────────────────────────────────────

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private tableRegistry (destination: string) =
    FactTableRegistry.build [
        {
            FactTableRegistration.Module = "sales"
            Definition = skuSales
        }
    ] [ BindAllFactTables destination ]

let private plant (storage: IBlobStorage) (root: string) (scope: string) (runId: string) =
    storage.Upload(scope, sprintf "%sprovenance/%s.json" root runId, Text.Encoding.UTF8.GetBytes "{}")
    |> Async.RunSynchronously
    |> Result.defaultWith (fun e -> failtestf "plant: %s" e)
    |> ignore

/// The default writer over a `BlobFactStore`.
let defaultWriterFactory: FactTableOrphanSweepFactory =
    fun () ->
        let storage = RefusableBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
        let blob = storage :> IBlobStorage
        let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
        let facts = BlobFactStore.createWithRegistry blob events (Some registry)

        {
            Writer =
                DefaultFactTableWriter.createWithClock
                    facts
                    blob
                    events
                    (tableRegistry DefaultFactTableWriter.Destination)
                    (Some registry)
                    (fun () -> t0)
            Storage = storage
            Scope = newScope ()
            TableId = skuSales.Id
            KeepsCommittedProvenance = false
            PlantUnrecordedProvenance = plant blob "_fact-tables/"
        }

/// The delegate writer (Phase 889) holding the table.
let delegateWriterFactory: FactTableOrphanSweepFactory =
    fun () ->
        let storage = RefusableBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
        let blob = storage :> IBlobStorage
        let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
        let inner = BlobFactStore.createWithRegistry blob events (Some registry)
        let tables = tableRegistry DelegateFact.Destination

        let delegates =
            match DelegateFacts.resolve tables (Some registry) [ skuSales.Id ] with
            | Ok ds -> ds
            | Error e -> failtestf "resolve: %s" e

        let store =
            DelegatedFactStore.create inner blob delegates (Some registry) (fun () -> t0) :> IFactStore

        {
            Writer =
                DelegatedFactStore.writer
                    None
                    blob
                    events
                    tables
                    (Some registry)
                    delegates
                    (fun () -> Some store)
                    (fun () -> t0)
            Storage = storage
            Scope = newScope ()
            TableId = skuSales.Id
            KeepsCommittedProvenance = true
            PlantUnrecordedProvenance = plant blob "_delegate-tables/"
        }

/// The fact-browse notifying decorator over the default writer.
let notifyingWriterFactory: FactTableOrphanSweepFactory =
    fun () ->
        let fixture = defaultWriterFactory ()

        {
            fixture with
                Writer =
                    FactBrowseHandler.notifyingWriter
                        (ToolUp.Platform.NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel)
                        fixture.Writer
        }