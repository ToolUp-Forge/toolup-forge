module ToolUp.Platform.Tests.InProcess.LineageDurabilityTests

// Phase 1027 — a lineage link survives a lineage-store outage.
//
// With durable lineage on, a result save's links ride the lineage outbox
// (`ResultStore.LineageOutbox`, the Phase 599 write-ahead shape): staged in
// one intent blob, recorded inline, and retried by `RelayOnce` until each is
// recorded, deduplicated by link id, or the intent becomes a dead letter.
// With it off, a link the store fails to record is logged and dropped,
// exactly as Phase 1025 left it.
//
// The lineage store here counts every `Record` per link id, so "recorded
// exactly once" is a count, not an absence of errors. A "restart" is a fresh
// `LineageOutbox` over the same blob and object stores: the blob substrate
// is the only state that survives a real process death. The relay runs with
// a zero settle window and zero backoff so each pass is one attempt.

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Transport
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

type LineageStoreDownException(message: string) =
    inherit Exception(message)

/// A lineage store that fails while `failing ()` says so (a plain-async
/// throw, the crash class Phase 1025 contained) and otherwise records,
/// counting every successful `Record` per link id.
type private CountingLineageStore(failing: unit -> bool) =
    let recorded = ConcurrentQueue<LineageLink>()
    let mutable calls = 0

    member _.Calls = calls
    member _.Recorded = List.ofSeq recorded

    member _.TimesRecorded(linkId: Guid) =
        recorded |> Seq.filter (fun l -> l.LinkId = linkId) |> Seq.length

    interface ILineageStore with
        member _.Record(_scopeId, link) = async {
            Threading.Interlocked.Increment &calls |> ignore

            if failing () then
                raise (LineageStoreDownException "lineage store unavailable (503)")

            recorded.Enqueue link
            return Ok()
        }

        member _.GetAncestors(_scopeId, objectId) = async {
            if failing () then
                raise (LineageStoreDownException "lineage store unavailable (503)")

            let edges = recorded |> Seq.filter (fun l -> l.ToObjectId = objectId) |> List.ofSeq

            return {
                Root = objectId
                Nodes = []
                Edges = edges
            }
        }

        member _.GetDescendants(_scopeId, objectId) = async {
            return {
                Root = objectId
                Nodes = []
                Edges = []
            }
        }

        member _.GetPath(_scopeId, _fromId, _toId) = async { return None }

        member _.Erase(_scopeId, _subjectUserId, _policy, _dryRun) = async {
            return
                Ok {
                    HandlerName = "lineage"
                    RecordsAffected = 0
                    Note = None
                }
        }

let private capturingLogger () =
    let warnings = ConcurrentQueue<string>()
    let errors = ConcurrentQueue<string>()

    warnings,
    errors,
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Enqueue message
        member _.Error(message, _) = errors.Enqueue message
    }

let private scopeId = "team-1027"
let private json = FableConverters.create ()

let private policy (maxAttempts: int) : RetryPolicy = {
    MaxAttempts = maxAttempts
    InitialBackoff = TimeSpan.Zero
    MaxBackoff = TimeSpan.Zero
    Timeout = None
}

/// The substrate that survives a restart: blob storage and the object store.
type private Substrate = {
    Blobs: IBlobStorage
    Objects: IDataObjectStore
}

let private substrate () =
    let blobs = InMemoryBlobStorage() :> IBlobStorage

    {
        Blobs = blobs
        Objects = DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore
    }

let private outbox (s: Substrate) (store: ILineageStore) (logger: ILogger) (maxAttempts: int) (abandon: TimeSpan) =
    ResultStore.LineageOutbox(s.Blobs, store, s.Objects, logger, policy maxAttempts, TimeSpan.Zero, abandon)

type private StoreKind =
    | InMemory
    | Persistent

let private resultStore (kind: StoreKind) (s: Substrate) (store: ILineageStore) logger (o: ResultStore.LineageOutbox) =
    match kind with
    | InMemory -> ResultStore.InMemoryResultStore(None, Some store, logger, Some o) :> IResultStore
    | Persistent -> ResultStore.PersistentResultStore(s.Objects, None, Some store, logger, Some o) :> IResultStore

let private save (store: IResultStore) (inputs: string list) = async {
    match! store.SaveResult(scopeId, "mmm", "fit", Encoding.UTF8.GetBytes "{}", "alice", inputs) with
    | Ok o -> return o
    | Error e -> return failtestf "SaveResult failed: %A" e
}

/// The staged intents, decoded.
let private staged (s: Substrate) = async {
    let! names = s.Blobs.List("_platform", "lineage-outbox/")
    let intents = ResizeArray<ResultStore.LineageIntent>()

    for name in names |> List.filter (fun n -> n.EndsWith ".json") do
        match! s.Blobs.Download("_platform", name) with
        | Ok bytes ->
            intents.Add(JsonSerializer.Deserialize<ResultStore.LineageIntent>(Encoding.UTF8.GetString bytes, json))
        | Error _ -> ()

    return List.ofSeq intents
}

/// Wait for the inline (fire-and-forget) first attempt to have been made:
/// every staged intent carries at least one attempt, or none is staged.
let private afterInlineAttempt (s: Substrate) (store: CountingLineageStore) (links: int) = async {
    let rec go n = async {
        let! intents = staged s

        let settled =
            store.Calls >= links && intents |> List.forall (fun i -> i.Attempts >= 1)

        if settled || n > 200 then
            return ()
        else
            do! Async.Sleep 25
            return! go (n + 1)
    }

    do! go 0
}

let private outageThenRecovery (kind: StoreKind) =
    testCaseAsync $"{kind}: a store failing for N attempts then recovering ends with every link recorded exactly once"
    <| async {
        let s = substrate ()
        // Three failed Record calls: both links inline, then one on the
        // first relay pass. The second pass must skip the link the first
        // pass recorded, or it is doubled.
        let mutable failuresLeft = 3

        let store =
            CountingLineageStore(fun () ->
                if failuresLeft > 0 then
                    failuresLeft <- failuresLeft - 1
                    true
                else
                    false)

        let warnings, _, logger = capturingLogger ()
        let o = outbox s store logger 10 (TimeSpan.FromMinutes 10.0)
        let results = resultStore kind s store logger o

        let! saved = save results [ "input-a"; "input-b" ]
        do! afterInlineAttempt s store 2

        let! afterInline = staged s
        Expect.equal afterInline.Length 1 "the failed links are staged, as one intent"
        Expect.equal afterInline.Head.Attempts 1 "the inline attempt is counted"

        let mutable passes = 0
        let mutable pending = 1

        while pending > 0 && passes < 10 do
            let! _ = o.RelayOnce 100
            let! p = o.PendingCount()
            pending <- p
            passes <- passes + 1

        Expect.equal pending 0 "nothing is left staged once the store recovers"

        let links = afterInline.Head.Links
        Expect.equal links.Length 2 "both links were staged"

        for link in links do
            Expect.equal link.ToObjectId saved.ObjectId "the link points into the saved result"
            Expect.equal (store.TimesRecorded link.LinkId) 1 $"link {link.FromObjectId} is recorded exactly once"

        Expect.equal store.Recorded.Length 2 "no other link was recorded"
        let! dead = o.DeadLetterCount()
        Expect.equal dead 0 "a recovered outage leaves no dead letter"

        Expect.isTrue
            (warnings
             |> Seq.exists (fun w -> w.Contains "lineage link (scope team-1027, input-a ->"))
            "Phase 1025's reconstructable warning is kept for the inline failure"
    }

let private permanentOutage (kind: StoreKind) =
    testCaseAsync $"{kind}: a permanently failing store produces dead letters carrying the full link"
    <| async {
        let s = substrate ()
        let store = CountingLineageStore(fun () -> true)
        let _, errors, logger = capturingLogger ()
        let o = outbox s store logger 3 (TimeSpan.FromMinutes 10.0)
        let results = resultStore kind s store logger o

        let! saved = save results [ "input-a"; "input-b" ]
        do! afterInlineAttempt s store 2
        let! stagedLinks = staged s
        let links = stagedLinks.Head.Links

        for _ in 1..5 do
            let! _ = o.RelayOnce 100
            ()

        let! pending = o.PendingCount()
        Expect.equal pending 0 "nothing is left staged after the retry budget"
        let! count = o.DeadLetterCount()
        Expect.equal count 1 "the intent is one dead letter"

        let! letters = o.DeadLetters()
        let letter = List.exactlyOne letters
        Expect.equal letter.Intent.Attempts 3 "the dead letter records every attempt"
        Expect.stringContains letter.LastError "503" "the dead letter records the last error"
        Expect.equal letter.Intent.Links links "the dead letter carries every link, whole"

        for link in letter.Intent.Links do
            Expect.equal link.ToObjectId saved.ObjectId "each dead link points into the saved result"
            Expect.equal link.ModuleName "mmm" "each dead link keeps its module"

            Expect.isTrue
                (errors
                 |> Seq.exists (fun e -> e.Contains "event=dead_lettered" && e.Contains(string link.LinkId)))
                $"an Error names dead link {link.LinkId} with its reconstructable text"

        Expect.isEmpty store.Recorded "no link was recorded"
    }

let private restartMidOutage =
    testCaseAsync "a restart mid-outage loses no staged link"
    <| async {
        let s = substrate ()
        let down = CountingLineageStore(fun () -> true)
        let _, _, logger = capturingLogger ()
        let before = outbox s down logger 10 (TimeSpan.FromMinutes 10.0)
        let results = resultStore Persistent s down logger before

        let! _ = save results [ "input-a"; "input-b" ]
        do! afterInlineAttempt s down 2
        let! _ = before.RelayOnce 100

        // The process dies. A new one starts against a recovered store,
        // with nothing but the blob and object stores in common.
        let up = CountingLineageStore(fun () -> false)
        let after = outbox s up logger 10 (TimeSpan.FromMinutes 10.0)
        let! relayed = after.RelayOnce 100

        Expect.equal relayed 1 "the restarted relay records the staged intent"
        Expect.equal up.Recorded.Length 2 "both links survive the restart"
        let! pending = after.PendingCount()
        Expect.equal pending 0 "nothing is left staged"
    }

let private crashBeforeFirstAttempt =
    testCaseAsync "a crash between the save and its first attempt loses no link (the witness admits it)"
    <| async {
        let s = substrate ()
        let _, _, logger = capturingLogger ()
        let up = CountingLineageStore(fun () -> false)
        let o = outbox s up logger 10 (TimeSpan.FromMinutes 10.0)
        let objectId = ResultObjectId.make "mmm" "fit"

        let links: LineageLink list = [
            {
                LinkId = Guid.NewGuid()
                FromObjectId = "input-a"
                ToObjectId = objectId
                ModuleName = "mmm"
                LinkType = Derived
                Timestamp = DateTime.UtcNow
            }
        ]

        // Staged and saved, then the process died before recording.
        let! stagedIntent = o.Stage(scopeId, objectId, links, 1)
        Expect.isSome stagedIntent "staging succeeded"

        let! _ =
            s.Objects.Save(scopeId, objectId, Encoding.UTF8.GetBytes "{}", "fit", "alice", Map.empty, StrictlyVersioned)

        let! relayed = o.RelayOnce 100
        Expect.equal relayed 1 "the relay records a witnessed intent"
        Expect.equal (up.TimesRecorded links.Head.LinkId) 1 "the link is recorded once"
    }

let private neverCommittedIsAbandoned =
    testCaseAsync "an intent whose save never landed is abandoned, its links never recorded"
    <| async {
        let s = substrate ()
        let warnings, _, logger = capturingLogger ()
        let up = CountingLineageStore(fun () -> false)
        let o = outbox s up logger 10 TimeSpan.Zero
        let objectId = ResultObjectId.make "mmm" "fit"

        let link = {
            LinkId = Guid.NewGuid()
            FromObjectId = "input-a"
            ToObjectId = objectId
            ModuleName = "mmm"
            LinkType = Derived
            Timestamp = DateTime.UtcNow
        }

        let! _ = o.Stage(scopeId, objectId, [ link ], 1)
        do! Async.Sleep 20
        let! relayed = o.RelayOnce 100

        Expect.equal relayed 0 "nothing is recorded"
        Expect.isEmpty up.Recorded "no ghost link for a result that never landed"
        let! pending = o.PendingCount()
        Expect.equal pending 0 "the abandoned intent is gone"
        Expect.isTrue (warnings |> Seq.exists (fun w -> w.Contains "event=intent_abandoned")) "the abandon is logged"
    }

let private partlyDeliveredIsDeduplicated =
    testCaseAsync "a retry never doubles a link the store already holds (dedup by link id)"
    <| async {
        let s = substrate ()
        let _, _, logger = capturingLogger ()
        let up = CountingLineageStore(fun () -> false)
        let o = outbox s up logger 10 (TimeSpan.FromMinutes 10.0)
        let objectId = ResultObjectId.make "mmm" "fit"

        let mk input = {
            LinkId = Guid.NewGuid()
            FromObjectId = input
            ToObjectId = objectId
            ModuleName = "mmm"
            LinkType = Derived
            Timestamp = DateTime.UtcNow
        }

        let a, b = mk "input-a", mk "input-b"
        let! _ = o.Stage(scopeId, objectId, [ a; b ], 0)
        // Recorded, then the process died before the intent was cleared.
        let! _ = (up :> ILineageStore).Record(scopeId, a)
        let! relayed = o.RelayOnce 100

        Expect.equal relayed 1 "the intent completes"
        Expect.equal (up.TimesRecorded a.LinkId) 1 "the already-recorded link is not recorded again"
        Expect.equal (up.TimesRecorded b.LinkId) 1 "the missing link is recorded"
    }

let private switchOff =
    testList "with durable lineage off, behaviour is Phase 1025's" [
        testCaseAsync "a failed link is logged with its reconstructable text and dropped, never retried"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let objects = DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore
            let store = CountingLineageStore(fun () -> true)
            let warnings, _, logger = capturingLogger ()

            let results =
                ResultStore.PersistentResultStore(objects, None, Some store, logger) :> IResultStore

            let! _ = save results [ "input-a" ]

            let rec waitWarned n = async {
                if warnings.IsEmpty && n < 200 then
                    do! Async.Sleep 25
                    return! waitWarned (n + 1)
            }

            do! waitWarned 0
            do! Async.Sleep 100

            Expect.equal store.Calls 1 "one attempt, no retry"

            Expect.isTrue
                (warnings
                 |> Seq.exists (fun w -> w.Contains "lineage link (scope team-1027, input-a ->"))
                "the Phase 1025 warning names the link"

            let! stagedNames = blobs.List("_platform", "lineage-outbox")
            Expect.isEmpty stagedNames "nothing is staged"
        }

        test "the default is off, and withDurableLineage is the switch" {
            Expect.equal ServerConfig.defaults.LineageDurability NoDurableLineage "default off"

            let config = (ServerApp.empty |> ServerApp.withDurableLineage true).Config
            Expect.equal config.LineageDurability EnabledDurableLineage "the builder enables it"

            let config = (ServerApp.empty |> ServerApp.withDurableLineage false).Config
            Expect.equal config.LineageDurability NoDurableLineage "and disables it"
        }

        test "compose builds a lineage outbox only when it is enabled with the lineage and result stores" {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let objects = DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let warnings, _, logger = capturingLogger ()

            let build config =
                let _, _, _, o =
                    ComposeStores.buildOptionalStoreValuesLogged logger config events objects (Some blobs)

                o

            let full = {
                ServerConfig.defaults with
                    Lineage = EnabledLineageStore
                    ResultStore = PersistentResultStore
            }

            Expect.isNone (build full) "off by default: no outbox"

            Expect.isSome
                (build {
                    full with
                        LineageDurability = EnabledDurableLineage
                })
                "on: an outbox"

            Expect.isNone
                (build {
                    full with
                        LineageDurability = EnabledDurableLineage
                        Lineage = NoLineageStore
                })
                "on without the lineage store: none"

            Expect.isTrue
                (warnings |> Seq.exists (fun w -> w.Contains "durable lineage NOT enabled"))
                "and the misconfiguration is logged"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 1027 - a lineage link survives a lineage-store outage" [
        outageThenRecovery InMemory
        outageThenRecovery Persistent
        permanentOutage InMemory
        permanentOutage Persistent
        restartMidOutage
        crashBeforeFirstAttempt
        neverCommittedIsAbandoned
        partlyDeliveredIsDeduplicated
        switchOff
    ]