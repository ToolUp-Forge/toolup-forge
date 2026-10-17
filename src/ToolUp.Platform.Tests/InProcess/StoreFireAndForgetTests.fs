module ToolUp.Platform.Tests.InProcess.StoreFireAndForgetTests

// Phase 1025 — a store write started fire-and-forget cannot kill the host.
//
// The conversation and result stores start their `IEventStore` events and
// `ILineageStore` links without awaiting them, and the webhook dispatcher
// starts each event's fan-out (which reads its subscriptions from store IO)
// the same way. Each site is driven once against healthy stores (the
// control) and once against a store that fails in one of three ways:
//
//   * `Plain` — the write fails inside plain async code. Started with a bare
//     `Async.Start`, the exception is rethrown on a thread-pool thread and
//     TERMINATES THE HOST, so this arm can only go red in a child process.
//   * `TaskShaped` — the write fails behind a task boundary
//     (`Async.AwaitTask`).
//   * `Synchronous` — the store call throws before an `Async` exists. The
//     stores call it inside their own async body, so started bare this is
//     the crash class too.
//
// Lineage adds `Refused`: `ILineageStore.Record` returns `Error`, which the
// result stores used to drop without a word.
//
// For every arm: the caller's result equals the control's, no unobserved
// task exception carries the arm's marker, and exactly one Warning names
// the write and the exception type (the dispatcher logs one Error, through
// `Background.start`). Two further cases pin the warning text the move of
// the formatter to `Background` (Phase 1025) must leave byte-identical for
// the Phase 1006 / 1024 request-bound writes.

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

type StoreDownException(marker: string) =
    inherit Exception(marker)

type private FailureMode =
    | Plain
    | TaskShaped
    | Synchronous
    | Refused

/// Every unobserved task exception the process raises, by message. Each arm
/// looks only for its own marker, so a sibling test's unobserved exception
/// can neither fail nor pass this one.
let private unobserved = ConcurrentQueue<string>()

let private unobservedHook =
    lazy
        TaskScheduler.UnobservedTaskException.Add(fun e ->
            for inner in e.Exception.Flatten().InnerExceptions do
                unobserved.Enqueue inner.Message)

let private failWith<'a> (mode: FailureMode) (marker: string) : Async<'a> =
    match mode with
    | Plain -> async {
        do! Async.Sleep 5
        return raise (StoreDownException marker)
      }
    | TaskShaped -> async {
        do!
            Task.Run(
                Action(fun () ->
                    Thread.Sleep 5
                    raise (StoreDownException marker))
            )
            |> Async.AwaitTask

        return Unchecked.defaultof<'a>
      }
    | Synchronous
    | Refused -> raise (StoreDownException marker)

let private failingEventStore (mode: FailureMode) (marker: string) =
    { new IEventStore with
        member _.Write _ = failWith mode marker
        member _.ReadAll _ = async { return [] }
        member _.ReadByType(_, _) = async { return [] }
        member _.ReadBySource(_, _) = async { return [] }
        member _.ListScopes() = async { return [] }

        member _.Erase(_, _, _, _) = async {
            return
                Ok {
                    HandlerName = "events"
                    RecordsAffected = 0
                    Note = None
                }
        }
    }

let private healthyEventStore () =
    InMemoryEventStore.InMemoryEventStore() :> IEventStore

/// A lineage store that records into `links`, or fails per `fails` — the
/// failure decided per link, so one link can fail while the next succeeds.
let private lineageStore (links: ConcurrentQueue<LineageLink>) (fails: LineageLink -> (FailureMode * string) option) =
    { new ILineageStore with
        member _.Record(_, link) =
            match fails link with
            | Some(Refused, marker) -> async { return Error marker }
            | Some(mode, marker) -> failWith mode marker
            | None -> async {
                links.Enqueue link
                return Ok()
              }

        member _.GetAncestors(_, objectId) = async {
            return {
                Root = objectId
                Nodes = []
                Edges = []
            }
        }

        member _.GetDescendants(_, objectId) = async {
            return {
                Root = objectId
                Nodes = []
                Edges = []
            }
        }

        member _.GetPath(_, _, _) = async { return None }

        member _.Erase(_, _, _, _) = async {
            return
                Ok {
                    HandlerName = "lineage"
                    RecordsAffected = 0
                    Note = None
                }
        }
    }

let private capturingLogger () =
    let warnings = ConcurrentQueue<string>()
    let errors = ConcurrentQueue<string * exn option>()

    warnings,
    errors,
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Enqueue message
        member _.Error(message, ex) = errors.Enqueue(message, ex)
    }

let private settleOn (attempts: int) (probe: unit -> 'a list) = async {
    let rec go attempt = async {
        match probe () with
        | [] when attempt < attempts ->
            do! Async.Sleep 50
            return! go (attempt + 1)
        | found -> return found
    }

    return! go 0
}

/// A faulted, unobserved task is only reported when it is finalised.
let private leakedFor (marker: string) = async {
    for _ in 1..3 do
        GC.Collect()
        GC.WaitForPendingFinalizers()

    do! Async.Sleep 100
    GC.Collect()
    GC.WaitForPendingFinalizers()
    return unobserved.ToArray() |> Array.filter (fun m -> m.Contains marker)
}

// ─── The store sites ────────────────────────────────────────────────

let private scopeId = "team-1025"

let private conversation (id: string) : Conversation = {
    ConversationId = id
    SchemaVersion = 1
    CreatedAt = DateTime.UtcNow
    CreatedBy = "alice"
    ScopeId = scopeId
    Provider = "TestProvider"
    ModelName = "test-model"
    SystemPromptDigest = "sha256:test"
    SdkVersion = "0.0.0"
}

let private turn (conversationId: string) : ConversationTurn = {
    TurnId = ""
    ConversationId = conversationId
    SchemaVersion = 1
    Role = "user"
    Content = {
        Role = "user"
        Content = "hello"
        ToolCalls = []
        ToolResults = []
        Parts = []
    }
    Timestamp = DateTime.UtcNow
    TokensIn = None
    TokensOut = None
    ContentDigest = ""
}

type private StoreKind =
    | InMemory
    | Persistent

let private conversationStore (kind: StoreKind) (events: IEventStore) (logger: ILogger) : IConversationStore =
    match kind with
    | InMemory -> ConversationStore.InMemoryConversationStore(Some events, logger) :> _
    | Persistent ->
        let objects =
            DataObjectStore.DataObjectStore(InMemoryBlobStorage() :> IBlobStorage) :> IDataObjectStore

        ConversationStore.PersistentConversationStore(objects, Some events, logger) :> _

let private resultStore
    (kind: StoreKind)
    (events: IEventStore)
    (lineage: ILineageStore)
    (logger: ILogger)
    : IResultStore =
    match kind with
    | InMemory -> ResultStore.InMemoryResultStore(Some events, Some lineage, logger) :> _
    | Persistent ->
        let objects =
            DataObjectStore.DataObjectStore(InMemoryBlobStorage() :> IBlobStorage) :> IDataObjectStore

        ResultStore.PersistentResultStore(objects, Some events, Some lineage, logger) :> _

let private expectOk label (result: Async<Result<unit, ConversationError>>) = async {
    match! result with
    | Ok() -> ()
    | Error e -> failtestf "%s: expected Ok, got %A" label e
}

/// One fire-and-forget write, with the call that reaches it. `Act` runs the
/// call against the given stores and renders the caller's result; `Write`
/// is the name the Warning gives the write.
type private Site = {
    Name: string
    Source: string
    Write: string
    /// Whether the failure is injected through the lineage store rather
    /// than the event store.
    ThroughLineage: bool
    Act: IEventStore -> ILineageStore -> ILogger -> Async<string>
}

let private conversationSites (kind: StoreKind) : Site list =
    let source =
        match kind with
        | InMemory -> "InMemoryConversationStore"
        | Persistent -> "PersistentConversationStore"

    let site name write act = {
        Name = sprintf "%s %s" source name
        Source = source
        Write = write
        ThroughLineage = false
        Act =
            fun events _ logger -> async {
                let store = conversationStore kind events logger
                let id = "conv-1025"
                return! act store id
            }
    }

    [
        site "BeginConversation" "ConversationStarted event" (fun store id -> async {
            let! result = store.BeginConversation(scopeId, conversation id)
            return sprintf "%A" result
        })
        site "AppendTurn" "ConversationTurnAppended event" (fun store id -> async {
            do! expectOk "Begin" (store.BeginConversation(scopeId, conversation id))
            let! result = store.AppendTurn(scopeId, id, turn id)
            return sprintf "%A" result
        })
        site "MarkStatus" "ConversationCompleted event" (fun store id -> async {
            do! expectOk "Begin" (store.BeginConversation(scopeId, conversation id))
            let! result = store.MarkStatus(scopeId, id, ConversationStatus.Completed)
            return sprintf "%A" result
        })
        site "Erase" "ConversationErased event" (fun store id -> async {
            do! expectOk "Begin" (store.BeginConversation(scopeId, conversation id))
            let! result = store.Erase(scopeId, "alice", ErasurePolicy.HardDelete, false)
            return sprintf "%A" result
        })
    ]

let private saveResult (store: IResultStore) = async {
    let! result = store.SaveResult(scopeId, "mmm", "fit", [| 1uy; 2uy |], "alice", [ "input-a" ])

    return
        match result with
        | Ok o -> sprintf "Ok %s v%d %s by %s" o.ObjectId o.Version o.DataType o.CreatedBy
        | Error e -> sprintf "Error %A" e
}

let private resultSites (kind: StoreKind) : Site list =
    let source =
        match kind with
        | InMemory -> "InMemoryResultStore"
        | Persistent -> "PersistentResultStore"

    let act events lineage logger =
        saveResult (resultStore kind events lineage logger)

    [
        {
            Name = sprintf "%s SaveResult event" source
            Source = source
            Write = "AnalysisCompleted event"
            ThroughLineage = false
            Act = act
        }
        {
            Name = sprintf "%s SaveResult lineage" source
            Source = source
            Write = "lineage link (scope team-1025, input-a -> "
            ThroughLineage = true
            Act = act
        }
    ]

let private storeSites =
    conversationSites InMemory
    @ conversationSites Persistent
    @ resultSites InMemory
    @ resultSites Persistent

let private failureWarnings (site: Site) (warnings: ConcurrentQueue<string>) =
    warnings.ToArray()
    |> List.ofArray
    |> List.filter (fun w -> w.Contains(sprintf "the %s" site.Write) && w.Contains " write failed")

let private storeArm (site: Site) (mode: FailureMode) =
    testCaseAsync (sprintf "%s: a %A failure is caught, logged once, and changes nothing" site.Name mode)
    <| async {
        unobservedHook.Force()
        let marker = sprintf "store-down-%s" (Guid.NewGuid().ToString "N")
        let _, _, controlLogger = capturingLogger ()

        let! control = site.Act (healthyEventStore ()) (lineageStore (ConcurrentQueue()) (fun _ -> None)) controlLogger

        let events, lineage =
            if site.ThroughLineage then
                healthyEventStore (), lineageStore (ConcurrentQueue()) (fun _ -> Some(mode, marker))
            else
                failingEventStore mode marker, lineageStore (ConcurrentQueue()) (fun _ -> None)

        let warnings, _, logger = capturingLogger ()
        let! result = site.Act events lineage logger

        Expect.equal result control "the caller's result is the healthy stores'"

        let! logged = settleOn 60 (fun () -> failureWarnings site warnings)
        let! leaked = leakedFor marker
        Expect.isEmpty leaked "no unobserved task exception carries this write's failure"

        match logged with
        | [ warning ] ->
            let named =
                if mode = Refused then
                    "InvalidOperationException"
                else
                    "StoreDownException"

            Expect.stringContains warning named "the Warning names the exception type"
            Expect.stringContains warning site.Source "the Warning names its source"
            Expect.stringContains warning "is unaffected" "the Warning says the result is unaffected"
        | other -> failtestf "expected exactly one Warning naming the %s write, got %A" site.Write other
    }

// ─── The webhook dispatcher's fan-out ───────────────────────────────

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

type private NoSecrets() =
    interface ISecretStore with
        member _.GetSecret(_, _) = async { return None }
        member _.SetSecret(_, _, _) = async { return Ok() }
        member _.DeleteSecret(_, _) = async { return Ok() }
        member _.ListKeys _ = async { return [] }

/// A registry whose `ListSubscriptions` — the store IO an event's fan-out
/// starts with — fails per `mode`; every other call reaches `inner`.
let private failingRegistry (inner: IWebhookRegistry) (mode: FailureMode) (marker: string) =
    { new IWebhookRegistry with
        member _.CreateSubscription subscription = inner.CreateSubscription subscription
        member _.ListSubscriptions _ = failWith mode marker

        member _.GetSubscription(scope, subscriptionId) =
            inner.GetSubscription(scope, subscriptionId)

        member _.UpdateStatus(scope, subscriptionId, status) =
            inner.UpdateStatus(scope, subscriptionId, status)

        member _.RotateSecret(scope, subscriptionId, currentRef, previousRef, graceExpiresAt) =
            inner.RotateSecret(scope, subscriptionId, currentRef, previousRef, graceExpiresAt)

        member _.SetConsecutiveFailures(scope, subscriptionId, count) =
            inner.SetConsecutiveFailures(scope, subscriptionId, count)

        member _.DeleteSubscription(scope, subscriptionId) =
            inner.DeleteSubscription(scope, subscriptionId)

        member _.ListAllActive() = inner.ListAllActive()
    }

let private eventFor (i: int) : ModuleEvent = {
    Id = Guid.NewGuid()
    OccurredAt = DateTime.UtcNow
    ScopeId = scopeId
    SourceModule = "tests.module"
    EventType = sprintf "ThingHappened%d" i
    Payload = """{"n":1}"""
}

let private webhookArm (mode: FailureMode) =
    testCaseAsync (
        sprintf
            "WebhookDispatcher fan-out: a %A ListSubscriptions failure is caught, logged once per event, and the dispatcher survives"
            mode
    )
    <| async {
        unobservedHook.Force()
        let marker = sprintf "store-down-%s" (Guid.NewGuid().ToString "N")
        let storage = InMemoryBlobStorage() :> IBlobStorage
        let registry = failingRegistry (WebhookRegistry.createRegistry storage) mode marker
        let _, errors, logger = capturingLogger ()

        let dispatcher =
            WebhookDispatcher.create
                registry
                (WebhookRegistry.createDeliveryLog storage)
                (healthyEventStore ())
                (new HttpClient())
                WebhookRetryPolicy.defaults
                logger
                (NoOpActivitySink() :> IActivitySink)
                (NoSecrets() :> ISecretStore)
                (fun () ->
                    { new IRateLimiter with
                        member _.Wait _ = async { return Proceed }
                    })
                { AllowedHosts = [] }

        let hosted = dispatcher :> IHostedService
        do! hosted.StartAsync CancellationToken.None |> Async.AwaitTask

        // Two events: the second is fanned out only if the first one's
        // failure left the dispatcher (and the host) standing.
        let first, second = eventFor 1, eventFor 2

        let unhandledFor (event: ModuleEvent) () =
            errors.ToArray()
            |> List.ofArray
            |> List.filter (fun (message, _) ->
                message.Contains "event=dispatch_unhandled"
                && message.Contains(sprintf "eventId=%O" event.Id))

        (dispatcher :> WebhookDispatcher.IWebhookDispatcher).Dispatch first
        let! firstLogged = settleOn 60 (unhandledFor first)
        (dispatcher :> WebhookDispatcher.IWebhookDispatcher).Dispatch second
        let! secondLogged = settleOn 60 (unhandledFor second)

        use stopWindow = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
        do! hosted.StopAsync stopWindow.Token |> Async.AwaitTask

        let! leaked = leakedFor marker
        Expect.isEmpty leaked "no unobserved task exception carries the fan-out's failure"

        for event, logged in [ first, firstLogged; second, secondLogged ] do
            match logged with
            | [ _, Some ex ] ->
                Expect.equal
                    ((ProviderExceptions.unwrap ex).GetType())
                    typeof<StoreDownException>
                    "the Error carries the store's exception"
            | other -> failtestf "expected exactly one Error for event %O, got %A" event.Id other
    }

// ─── The warning text the move must not change ──────────────────────

let private textPins =
    testList "the write-failure warning text" [
        testCase "the request-bound warning (Phase 1006 / 1024) is byte-identical"
        <| fun _ ->
            let warnings, _, logger = capturingLogger ()
            let ctx = DefaultHttpContext()
            ctx.Request.Method <- "GET"
            ctx.Request.Path <- PathString "/p/report"
            ctx.Request.QueryString <- QueryString "?secret=1"

            let warn =
                SurfaceEnforcement.writeFailureWarner "ScopeResolutionMiddleware" (Some logger) ctx

            warn "scope-downgrade audit" (AggregateException(StoreDownException "sink down"))

            Expect.equal
                (List.ofSeq warnings)
                [
                    "ScopeResolutionMiddleware: the scope-downgrade audit write failed on GET /p/report (StoreDownException: sink down); the response is unaffected."
                ]
                "source, write, method and path without the query, the unwrapped cause, the response unaffected"

        testCase "the store warning names the write and the caller's result"
        <| fun _ ->
            let warnings, _, logger = capturingLogger ()

            let warn =
                Background.writeFailureLogger "InMemoryResultStore" (Some logger) "" "the save's result"

            warn "AnalysisCompleted event (scope s, module m, object o, version 1)" (StoreDownException "disk full")

            Expect.equal
                (List.ofSeq warnings)
                [
                    "InMemoryResultStore: the AnalysisCompleted event (scope s, module m, object o, version 1) write failed (StoreDownException: disk full); the save's result is unaffected."
                ]
                "the one formatter, with no request in hand"

        testCase "a logger that throws is ignored"
        <| fun _ ->
            let throwing =
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info _ = ()
                    member _.Warn _ = failwith "logger down"
                    member _.Error(_, _) = ()
                }

            Background.writeFailureLogger "S" (Some throwing) "" "x" "w" (StoreDownException "m")
    ]

// ─── Lineage specifics ──────────────────────────────────────────────

let private lineageSpecifics =
    testList "lineage" [
        for kind in [ InMemory; Persistent ] do
            testCaseAsync (
                sprintf "%A: one link failing does not lose the others, and its warning reconstructs it" kind
            )
            <| async {
                unobservedHook.Force()
                let marker = sprintf "store-down-%s" (Guid.NewGuid().ToString "N")
                let recorded = ConcurrentQueue<LineageLink>()

                let lineage =
                    lineageStore recorded (fun link ->
                        if link.FromObjectId = "input-a" then
                            Some(Plain, marker)
                        else
                            None)

                let warnings, _, logger = capturingLogger ()
                let store = resultStore kind (healthyEventStore ()) lineage logger

                let! result = store.SaveResult(scopeId, "mmm", "fit", [| 1uy |], "alice", [ "input-a"; "input-b" ])

                let toId =
                    match result with
                    | Ok o -> o.ObjectId
                    | Error e -> failtestf "SaveResult: %A" e

                let! survivors =
                    settleOn 60 (fun () -> recorded.ToArray() |> List.ofArray |> List.map (fun l -> l.FromObjectId))

                Expect.equal survivors [ "input-b" ] "the second link is still recorded"

                let! logged =
                    settleOn 60 (fun () ->
                        warnings.ToArray()
                        |> List.ofArray
                        |> List.filter (fun w -> w.Contains "lineage link"))

                match logged with
                | [ warning ] ->
                    for part in [ scopeId; sprintf "input-a -> %s" toId; "module mmm"; "type Derived"; "link " ] do
                        Expect.stringContains warning part "the warning carries what the lost link was made of"
                | other -> failtestf "expected exactly one lineage Warning, got %A" other
            }
    ]

// ─── Composition ────────────────────────────────────────────────────

let private publicConstructors =
    testCaseAsync "a store built through its public constructor contains the failure too"
    <| async {
        let marker = sprintf "store-down-%s" (Guid.NewGuid().ToString "N")

        let store =
            ConversationStore.InMemoryConversationStore(failingEventStore Plain marker) :> IConversationStore

        let! result = store.BeginConversation(scopeId, conversation "conv-public")
        Expect.equal result (Ok()) "the caller's result is unaffected"
        // The failure lands after the call; give it time to have killed the
        // host if it were going to.
        do! Async.Sleep 200
    }

let tests =
    testList "Phase 1025 — a store write started fire-and-forget cannot kill the host" [
        for site in storeSites do
            for mode in [ Plain; TaskShaped; Synchronous ] do
                storeArm site mode

            if site.ThroughLineage then
                storeArm site Refused

        for mode in [ Plain; TaskShaped; Synchronous ] do
            webhookArm mode

        textPins
        lineageSpecifics
        publicConstructors
    ]