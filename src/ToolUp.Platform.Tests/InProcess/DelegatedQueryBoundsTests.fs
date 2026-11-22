module ToolUp.Platform.Tests.InProcess.DelegatedQueryBoundsTests

// Bounds on a delegated-query answer, over the wire a requester sees: a
// ceiling published in the peer surface, an answer above it refused by
// name or continued a page at a time, and a continuation token that is
// useless to any principal, query or instant but the one it was issued for.

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose
open ToolUp.Platform.Audit
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose
open ToolUp.Platform.Tests.InProcess.PeerTestComposition

let private custodianScope = "custodian-north"
let private requesterPeerId = "requester-acme"
let private otherRequesterPeerId = "requester-other"
let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private floor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 10
    PermittedShapes = Set.ofList [ Count; Aggregate; Histogram ]
}

let private aggregatesTemplate =
    DelegatedQueryProfile.template "delegated-query" floor

let private rowsTemplate = DelegatedQueryProfile.withRowRelease aggregatesTemplate

/// Seven rows, each a small JSON object, in the handler's own
/// representation: a JSON array.
let private rows = [ for i in 1..7 -> $"""{{"order":"o-{i}"}}""" ]

let private rowQuery = """{"records":"orders"}"""
let private wideRowQuery = """{"records":"orders","wide":true}"""
let private aggregateQuery = """{"measure":"count","over":"orders"}"""
let private wideAggregateQuery = """{"measure":"histogram","over":"orders"}"""
let private parameters = Map.ofList [ "region", "\"north\"" ]

let private arrayOf (items: string list) = "[" + String.Join(",", items) + "]"

/// The deployment's row count, over its own representation.
let private countRows (result: DelegatedQueryResult) =
    use document = JsonDocument.Parse result.Answer

    match document.RootElement.ValueKind with
    | JsonValueKind.Array -> document.RootElement.GetArrayLength()
    | _ -> 1

/// The deployment's window, over its own representation.
let private window (result: DelegatedQueryResult) (offset: int) (count: int) =
    use document = JsonDocument.Parse result.Answer

    let items =
        document.RootElement.EnumerateArray()
        |> Seq.map _.GetRawText()
        |> Seq.skip (min offset (document.RootElement.GetArrayLength()))
        |> Seq.truncate count
        |> List.ofSeq

    { result with Answer = arrayOf items }

let private handler: DelegatedQueryHandler = {
    Describe = fun _ -> async { return Ok """{"tables":{"orders":["order"]}}""" }
    Evaluate =
        fun _ submission -> async {
            if submission.Query = rowQuery then
                return
                    Ok {
                        Class = DelegatedQueryClass.RowLevel
                        Cohort = 1
                        Answer = arrayOf rows
                    }
            elif submission.Query = wideRowQuery then
                return
                    Ok {
                        Class = DelegatedQueryClass.RowLevel
                        Cohort = 1
                        Answer = arrayOf [ $"""{{"order":"{String('x', 400)}"}}""" ]
                    }
            elif submission.Query = aggregateQuery then
                return
                    Ok {
                        Class = DelegatedQueryClass.Aggregate
                        Cohort = 42
                        Answer = """{"count":42}"""
                    }
            elif submission.Query = wideAggregateQuery then
                return
                    Ok {
                        Class = DelegatedQueryClass.Aggregate
                        Cohort = 42
                        Answer = arrayOf [ for i in 1..40 -> $"""{{"bin":{i},"count":42}}""" ]
                    }
            else
                return Error "the handler does not read that query"
        }
}

let private key = Array.init 32 byte

/// A clock a test moves.
type private Clock(start: DateTimeOffset) =
    let mutable now = start
    member _.Now() = now
    member _.Advance(by: TimeSpan) = now <- now + by

let private start = DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)

let private continuation: DelegatedQueryContinuation = {
    Window = window
    Key = fun () -> async { return key }
    Lifetime = TimeSpan.FromMinutes 15.0
}

/// Two rows or 256 bytes an answer, continued.
let private pagedBounds (clock: Clock) =
    DelegatedQueryBounds.declare 256
    |> DelegatedQueryBounds.withRows 2 countRows
    |> DelegatedQueryBounds.withContinuation continuation
    |> DelegatedQueryBounds.withClock clock.Now

/// A job scheduler that holds what it is given until `RunPending`.
type private HeldScheduler() =
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let pending = ResizeArray<Guid * JobRegistration>()

    member _.RunPending() = async {
        let queued = List.ofSeq pending
        pending.Clear()

        for jobId, registration in queued do
            let context: JobContext = {
                JobId = jobId
                ScopeId = registration.ScopeId
                AccessContext = AccessContexts.forSubject (AuthenticatedUser "_system")
                Attempt = 1
                Trigger = Manual
                Scope = ResolvedScope.anonymous
                TriggerSource = TriggerSource.ScheduledManually(UserId "_system")
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = registration.Payload
                DeadLetterDestination = None
            }

            let! _ = handlers[registration.Handler].Execute context
            ()
    }

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        member _.RegisterHandlerAsync(name, handler) = async {
            handlers[name] <- handler
            return Ok()
        }

        member this.Schedule(scope: ResolvedScope, registration: JobRegistration) =
            (this :> IJobScheduler).Schedule {
                registration with
                    ScopeId = scope.ScopeId
            }

        member _.Schedule(registration) = async {
            let jobId = Guid.NewGuid()
            pending.Add((jobId, registration))
            return Ok(Scheduled jobId)
        }

        member _.TriggerOnce(_, _, _) = async { return Ok() }
        member _.Cancel(_, _) = failwith "not used"
        member _.Disable(_, _) = failwith "not used"
        member _.Enable(_, _) = failwith "not used"
        member _.Get(_, _) = failwith "not used"
        member _.ListJobs _ = failwith "not used"
        member _.GetRecentRuns(_, _, _) = failwith "not used"
        member _.NotifyEventWritten(_, _, _) = failwith "not used"

type private ScopedResultStore() =
    let records = ConcurrentDictionary<string * Guid, PeerJobRecord>()
    member _.All = records |> Seq.map _.Value |> List.ofSeq

    interface IPeerJobResultStore with
        member _.Retention = PeerJobRetentionPolicy.default'

        member _.SaveResult(scopeId, jobId, owner, status) = async {
            records[(scopeId, jobId)] <- { OwnerPeerId = owner; Status = status }
        }

        member _.TryGetResult(scopeId, jobId) = async {
            match records.TryGetValue((scopeId, jobId)) with
            | true, record -> return Some record
            | _ -> return None
        }

type private Custodian = {
    Peer: IPlatformPeer
    Scheduler: HeldScheduler
    Results: ScopedResultStore
    Budget: IPrivacyBudgetLedger
    Audit: ConcurrentQueue<DelegatedQueryAuditPayload>
    Wire: ConcurrentQueue<string>
}

let private budgetScope (peerId: string) : BudgetScope = {
    TemplateId = "delegated-query"
    CounterpartyPeerId = peerId
    Epoch = "perpetual"
}

/// A custodian binding both requesters at `visibility`, gated by
/// `template`, on `leg`, applying `bounds`, with an answer budget of
/// `ceiling`.
let private custodianWith
    (visibility: PeerVisibilityBinding)
    (template: CleanRoomTemplate)
    (leg: DelegatedQueryLeg)
    (bounds: DelegatedQueryBounds)
    (ceiling: decimal)
    =
    let scheduler = HeldScheduler()
    let results = ScopedResultStore()
    let audit = ConcurrentQueue<DelegatedQueryAuditPayload>()

    let budget =
        InMemoryPrivacyBudgetLedger(fun () -> DateTimeOffset.UtcNow) :> IPrivacyBudgetLedger

    let deps: DelegatedQueryDeps = {
        ResolveBinding =
            fun peerId -> async {
                if peerId = requesterPeerId || peerId = otherRequesterPeerId then
                    return
                        Some {
                            PeerId = peerId
                            ScopeId = custodianScope
                            Visibility = visibility
                        }
                else
                    return None
            }
        Handler = handler
        Leg = leg
        ClassCheck = None
        Audit = fun payload -> async { audit.Enqueue payload }
    }

    let gate: DelegatedQueryGate = {
        Broker = CleanRoomBroker.create ()
        Template = template
        Approval = None
        Budget = Some(PrivacyBudgetMeter.create budget (PrivacyBudgetPolicy.create ceiling 1m))
        Audit = CleanRoomGate.noAudit
    }

    let fusion: PeerJobFusion = {
        Scheduler = scheduler
        ResultStore = results
        AuditLog = NoOpAuditLog()
    }

    let peer = DefaultPlatformPeer("custodian") :> IPlatformPeer

    match DelegatedQueryPeerContract.governedWithin bounds gate deps (Some fusion) with
    | Ok(gated, jobHandlers) ->
        peer.RegisterContract gated.Registration

        for name, handler in jobHandlers do
            (scheduler :> IJobScheduler).RegisterHandler(name, handler)
    | Error refusals ->
        failtestf
            "the gate was refused: %s"
            (refusals
             |> List.map DelegatedQueryTemplateRefusal.describe
             |> String.concat "; ")

    {
        Peer = peer
        Scheduler = scheduler
        Results = results
        Budget = budget
        Audit = audit
        Wire = ConcurrentQueue()
    }

let private full = PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.Full

let private pagedCustodian (clock: Clock) =
    custodianWith full rowsTemplate DelegatedQueryLeg.Immediate (pagedBounds clock) 1000m

let private contextFor (peerId: string) : PeerCallContext = {
    Peer = {
        PeerId = peerId
        DisplayName = peerId
    }
    User = Anonymous
    ContractVersion = v1
    Route = [ peerId ]
    RootRequestId = "root-922"
    ParentRequestId = None
    HopsRemaining = 4
}

let private send (custodian: Custodian) (peerId: string) (request: DelegatedQueryRequest) = async {
    let! outcome =
        custodian.Peer.Handle(
            DelegatedQueryProfile.ContractId,
            contextFor peerId,
            request.Operation,
            DelegatedQueryPeerContract.arguments request
        )

    match outcome with
    | Ok json ->
        custodian.Wire.Enqueue json

        match DelegatedQueryPeerContract.readAnswer json with
        | Ok answer -> return Ok answer
        | Error reason -> return failtestf "the custodian answered an unreadable document (%s): %s" reason json
    | Error e -> return Error e
}

let private submission (query: string) : DelegatedQuerySubmission = {
    Query = query
    Parameters = parameters
}

let private submit (query: string) =
    DelegatedQueryPeerContract.submissionRequest (submission query)

let private continueWith (query: string) (token: string) =
    DelegatedQueryPeerContract.continuationRequest (submission query) token

let private expectRefusal (outcome: Result<DelegatedQueryAnswer, PeerError>) (expected: string) (why: string) =
    match outcome with
    | Ok(DelegatedQueryAnswer.Refused refusal) -> Expect.equal (DelegatedQueryRefusal.className refusal) expected why
    | other -> failtestf "%s — expected a %s refusal, got %A" why expected other

let private firstPage (custodian: Custodian) = async {
    match! send custodian requesterPeerId (submit rowQuery) with
    | Ok(DelegatedQueryAnswer.Continued page) -> return page
    | other -> return failtestf "an answer above the ceiling must be continued, got %A" other
}

/// The rows of every page, read until the last.
let private readAll (custodian: Custodian) = async {
    let pages = ResizeArray<DelegatedQueryResult>()
    let! first = firstPage custodian
    pages.Add first.Result
    let mutable token = Some first.Continuation

    while token.IsSome do
        match! send custodian requesterPeerId (continueWith rowQuery token.Value) with
        | Ok(DelegatedQueryAnswer.Continued page) ->
            pages.Add page.Result
            token <- Some page.Continuation
        | Ok(DelegatedQueryAnswer.Answered result) ->
            pages.Add result
            token <- None
        | other -> failtestf "a continuation must answer a page, got %A" other

    return List.ofSeq pages
}

let private rowsOf (results: DelegatedQueryResult list) =
    results
    |> List.collect (fun r ->
        use document = JsonDocument.Parse r.Answer
        document.RootElement.EnumerateArray() |> Seq.map _.GetRawText() |> List.ofSeq)

let private enabled = {
    ServerConfig.defaults with
        MultiInstance = {
            ServerConfig.defaults.MultiInstance with
                PeerSubstrate = FeatureMode.Enabled
        }
}

let private surfaceGate: DelegatedQueryGate = {
    Broker = CleanRoomBroker.create ()
    Template = aggregatesTemplate
    Approval = None
    Budget = None
    Audit = CleanRoomGate.noAudit
}

let private surfaceDeps: DelegatedQueryDeps = {
    ResolveBinding = fun _ -> async { return None }
    Handler = handler
    Leg = DelegatedQueryLeg.Immediate
    ClassCheck = None
    Audit = DelegatedQueryPeerContract.noAudit
}

let private servedWith (builder: PeerJobFusion option -> PeerContractHost) =
    let surface =
        PeerComposition.create ()
        |> PeerComposition.app (ServerApp.withConfig enabled)
        |> PeerComposition.options (PeerOptions.withContract builder)
        |> PeerSurface.describe

    surface,
    surface.Serves.Contracts
    |> List.find (fun c -> c.ContractId = DelegatedQueryProfile.ContractId)

let private descriptorTests =
    testList "the bound in the descriptor" [
        testCase "a contract composed with no bounds publishes the conservative default and says so"
        <| fun () ->
            match DelegatedQueryPeerContract.builder surfaceGate surfaceDeps with
            | Error refusals -> failtestf "refused: %A" refusals
            | Ok builder ->
                let _, served = servedWith builder

                Expect.equal
                    served.Bounds
                    (Some {
                        MaxRows = None
                        MaxBytes = DelegatedQueryBounds.DefaultMaxBytes
                        Continuation = false
                        Basis = PeerAnswerBoundsBasis.Default
                    })
                    "the default ceiling is published with its basis, so a counterparty can tell it from a declared one"

        testCase "declared bounds are published as declared, in the stamped surface"
        <| fun () ->
            let clock = Clock start

            match DelegatedQueryPeerContract.builderWithin (pagedBounds clock) surfaceGate surfaceDeps with
            | Error refusals -> failtestf "refused: %A" refusals
            | Ok builder ->
                let surface, served = servedWith builder

                Expect.equal
                    served.Bounds
                    (Some {
                        MaxRows = Some 2
                        MaxBytes = 256
                        Continuation = true
                        Basis = PeerAnswerBoundsBasis.Declared
                    })
                    "the declared ceiling and the continuation offer"

                Expect.stringContains
                    (PeerSurface.exportJson surface)
                    "\"Bounds\":{\"MaxRows\":2,\"MaxBytes\":256,\"Continuation\":true,\"Basis\":\"Declared\"}"
                    "the bound is in the document a counterparty pins"

                Expect.equal
                    (DelegatedQueryBounds.published surface)
                    served.Bounds
                    "a requester reads the published bound before it calls"

        testCase "every other contract publishes no bound, as before"
        <| fun () ->
            let surface =
                PeerComposition.create ()
                |> PeerComposition.app (ServerApp.withConfig enabled)
                |> PeerComposition.options (PeerOptions.withPeerAuditTransparency)
                |> PeerSurface.describe

            Expect.isNonEmpty surface.Serves.Contracts "the reference composition serves a contract"
            Expect.all surface.Serves.Contracts (fun c -> c.Bounds.IsNone) "no bound is invented for a contract"
            Expect.isNone (DelegatedQueryBounds.published surface) "no delegated query is served"

        testCase "a label published before bounds existed reads as the default for the profile"
        <| fun () ->
            let clock = Clock start

            match DelegatedQueryPeerContract.builderWithin (pagedBounds clock) surfaceGate surfaceDeps with
            | Error refusals -> failtestf "refused: %A" refusals
            | Ok builder ->
                let surface, _ = servedWith builder

                let older = {
                    surface with
                        Serves = {
                            surface.Serves with
                                Contracts = surface.Serves.Contracts |> List.map (fun c -> { c with Bounds = None })
                        }
                }

                Expect.equal
                    (DelegatedQueryBounds.published older)
                    (Some(DelegatedQueryBounds.ceiling DelegatedQueryBounds.conservative))
                    "silence is the narrowest offer, not an unbounded one"

        testCase "bounds that cannot be applied are refused at composition, by name"
        <| fun () ->
            let unpaged = {
                DelegatedQueryBounds.declare 256 with
                    Continuation = Some continuation
            }

            match DelegatedQueryPeerContract.builderWithin unpaged surfaceGate surfaceDeps with
            | Ok _ -> failtest "continuation without a row count must be refused"
            | Error refusals ->
                Expect.exists
                    refusals
                    (function
                    | DelegatedQueryTemplateRefusal.BoundsUnusable _ -> true
                    | _ -> false)
                    "the refusal names the bounds"

            match DelegatedQueryPeerContract.builderWithin (DelegatedQueryBounds.declare 0) surfaceGate surfaceDeps with
            | Ok _ -> failtest "a byte ceiling of zero must be refused"
            | Error _ -> ()
    ]

let private ceilingTests =
    testList "an answer above the ceiling" [
        testCaseAsync "is refused by name with the ceiling and the size, and never reaches the wire"
        <| async {
            let bounds =
                DelegatedQueryBounds.declare 256 |> DelegatedQueryBounds.withRows 50 countRows

            let custodian =
                custodianWith PeerVisibilityBinding.default' aggregatesTemplate DelegatedQueryLeg.Immediate bounds 1000m

            let! answered = send custodian requesterPeerId (submit wideAggregateQuery)

            match answered with
            | Ok(DelegatedQueryAnswer.Refused(DelegatedQueryRefusal.AnswerBoundExceeded(bound, ceiling, size))) ->
                Expect.equal bound "bytes" "the bound exceeded"
                Expect.equal ceiling 256 "the ceiling"
                Expect.isGreaterThan size 256 "the size it measured"
            | other -> failtestf "expected a bound refusal, got %A" other

            for document in custodian.Wire do
                Expect.isFalse (document.Contains "\"bin\"") "no byte of the answer is written"
        }

        testCaseAsync "a row ceiling is checked before the byte ceiling"
        <| async {
            let bounds =
                DelegatedQueryBounds.declare 16 |> DelegatedQueryBounds.withRows 3 countRows

            let custodian =
                custodianWith full rowsTemplate DelegatedQueryLeg.Immediate bounds 1000m

            let! answered = send custodian requesterPeerId (submit rowQuery)
            expectRefusal answered "delegated-query-answer-bound-exceeded" "rows over the ceiling, not continued"

            match answered with
            | Ok(DelegatedQueryAnswer.Refused(DelegatedQueryRefusal.AnswerBoundExceeded(bound, ceiling, size))) ->
                Expect.equal (bound, ceiling, size) ("rows", 3, 7) "named by rows, with the ceiling and the count"
            | _ -> ()
        }

        testCaseAsync "an aggregate answer is never continued, whatever the bounds offer"
        <| async {
            let clock = Clock start

            let custodian =
                custodianWith full rowsTemplate DelegatedQueryLeg.Immediate (pagedBounds clock) 1000m

            let! answered = send custodian requesterPeerId (submit wideAggregateQuery)
            expectRefusal answered "delegated-query-answer-bound-exceeded" "an aggregate that does not fit is refused"
        }

        testCaseAsync "under AggregatesOnly no continuation is offered"
        <| async {
            let clock = Clock start

            let custodian =
                custodianWith
                    PeerVisibilityBinding.default'
                    rowsTemplate
                    DelegatedQueryLeg.Immediate
                    (pagedBounds clock)
                    1000m

            let! answered = send custodian requesterPeerId (submit wideAggregateQuery)
            expectRefusal answered "delegated-query-answer-bound-exceeded" "a narrower query, not a page"

            let! rowsAnswer = send custodian requesterPeerId (submit rowQuery)
            expectRefusal rowsAnswer "delegated-query-authority-level-exceeded" "rows never reach AggregatesOnly"
        }

        testCaseAsync "an answer within the ceiling is answered whole, unchanged"
        <| async {
            let clock = Clock start
            let custodian = pagedCustodian clock
            let! answered = send custodian requesterPeerId (submit aggregateQuery)

            match answered with
            | Ok(DelegatedQueryAnswer.Answered result) -> Expect.equal result.Answer """{"count":42}""" "whole"
            | other -> failtestf "expected the answer, got %A" other
        }

        testCaseAsync "a single row wider than the byte ceiling is refused, not paged forever"
        <| async {
            let clock = Clock start
            let custodian = pagedCustodian clock
            let! answered = send custodian requesterPeerId (submit wideRowQuery)

            match answered with
            | Ok(DelegatedQueryAnswer.Refused(DelegatedQueryRefusal.AnswerBoundExceeded(bound, _, _))) ->
                Expect.equal bound "bytes" "no page fits"
            | other -> failtestf "expected a bound refusal, got %A" other
        }
    ]

let private continuationTests =
    testList "continuation" [
        testCaseAsync "an answer leaves a page at a time and arrives whole"
        <| async {
            let clock = Clock start
            let custodian = pagedCustodian clock
            let! pages = readAll custodian

            Expect.equal (rowsOf pages) rows "every row, once, in order"
            Expect.equal pages.Length 4 "seven rows at two a page"

            for page in pages do
                Expect.isLessThanOrEqual (countRows page) 2 "each page within the row ceiling"
                Expect.isLessThanOrEqual (Encoding.UTF8.GetByteCount page.Answer) 256 "and the byte ceiling"
        }

        testCaseAsync "a page is halved until its bytes fit"
        <| async {
            let clock = Clock start

            let narrow =
                DelegatedQueryBounds.declare 40
                |> DelegatedQueryBounds.withRows 4 countRows
                |> DelegatedQueryBounds.withContinuation continuation
                |> DelegatedQueryBounds.withClock clock.Now

            let custodian =
                custodianWith full rowsTemplate DelegatedQueryLeg.Immediate narrow 1000m

            let! page = firstPage custodian
            Expect.equal (countRows page.Result) 2 "four rows do not fit forty bytes; two do"
        }

        testCaseAsync "each page is charged and recorded as a call of its own"
        <| async {
            let clock = Clock start
            let custodian = pagedCustodian clock
            let! pages = readAll custodian
            let! remaining = custodian.Budget.RemainingBudget(budgetScope requesterPeerId, 1000m)

            Expect.equal remaining.EpsilonCommitted (decimal pages.Length) "one charge per page"

            let outcomes =
                custodian.Audit
                |> Seq.filter (fun a -> a.Operation = DelegatedQueryProfile.SubmitOperation)
                |> Seq.map _.Outcome
                |> List.ofSeq

            Expect.equal outcomes [ "continued"; "continued"; "continued"; "answered" ] "one record per page"
        }

        testCaseAsync "paging cannot read past the budget"
        <| async {
            let clock = Clock start

            let custodian =
                custodianWith full rowsTemplate DelegatedQueryLeg.Immediate (pagedBounds clock) 2m

            let! first = firstPage custodian

            let! second = send custodian requesterPeerId (continueWith rowQuery first.Continuation)

            let token =
                match second with
                | Ok(DelegatedQueryAnswer.Continued page) -> page.Continuation
                | other -> failtestf "the second page is within the budget, got %A" other

            let! third = send custodian requesterPeerId (continueWith rowQuery token)

            match third with
            | Error(PeerCleanRoomWithheld _) -> ()
            | other -> failtestf "the third page is past the budget and must be withheld, got %A" other
        }

        testCaseAsync "a token presented by another principal is refused"
        <| async {
            let clock = Clock start
            let custodian = pagedCustodian clock
            let! first = firstPage custodian
            let! stolen = send custodian otherRequesterPeerId (continueWith rowQuery first.Continuation)
            expectRefusal stolen "delegated-query-continuation-foreign" "the token names its principal"
        }

        testCaseAsync "a token presented for another query is refused"
        <| async {
            let clock = Clock start
            let custodian = pagedCustodian clock
            let! first = firstPage custodian

            let otherParameters =
                DelegatedQueryPeerContract.continuationRequest
                    {
                        Query = rowQuery
                        Parameters = Map.ofList [ "region", "\"south\"" ]
                    }
                    first.Continuation

            let! mismatched = send custodian requesterPeerId otherParameters
            expectRefusal mismatched "delegated-query-continuation-mismatched" "the token names its parameters"

            let! otherQuery = send custodian requesterPeerId (continueWith wideRowQuery first.Continuation)
            expectRefusal otherQuery "delegated-query-continuation-mismatched" "and its query"
        }

        testCaseAsync "a token past its lifetime is refused"
        <| async {
            let clock = Clock start
            let custodian = pagedCustodian clock
            let! first = firstPage custodian
            clock.Advance(TimeSpan.FromMinutes 16.0)
            let! late = send custodian requesterPeerId (continueWith rowQuery first.Continuation)
            expectRefusal late "delegated-query-continuation-expired" "the token names its expiry"
        }

        testCaseAsync "a token the custodian did not sign is refused before its claims are read"
        <| async {
            let clock = Clock start
            let custodian = pagedCustodian clock
            let! first = firstPage custodian
            let parts = first.Continuation.Split '.'

            let tampered =
                String.Join(".", [| parts[0]; parts[1]; String('A', parts[2].Length) |])

            let! forged = send custodian requesterPeerId (continueWith rowQuery tampered)
            expectRefusal forged "delegated-query-continuation-invalid" "a forged signature"

            let! junk = send custodian requesterPeerId (continueWith rowQuery "not-a-token")
            expectRefusal junk "delegated-query-continuation-invalid" "a document that is not a token"
        }

        testCaseAsync "a token is refused where the custodian no longer offers continuation"
        <| async {
            let clock = Clock start
            let! first = firstPage (pagedCustodian clock)

            let unpaged =
                custodianWith
                    full
                    rowsTemplate
                    DelegatedQueryLeg.Immediate
                    (DelegatedQueryBounds.declare 256 |> DelegatedQueryBounds.withRows 2 countRows)
                    1000m

            let! refused = send unpaged requesterPeerId (continueWith rowQuery first.Continuation)
            expectRefusal refused "delegated-query-continuation-invalid" "no continuation offered"
        }
    ]

let private queuedTests =
    testList "the queued leg" [
        testCaseAsync "the parked result honours the ceiling, and Resolve pages it"
        <| async {
            let clock = Clock start

            let custodian =
                custodianWith full rowsTemplate DelegatedQueryLeg.Queued (pagedBounds clock) 1000m

            let resolveNext (request: DelegatedQueryRequest) = async {
                let! scheduled = send custodian requesterPeerId request

                let handle =
                    match scheduled with
                    | Ok(DelegatedQueryAnswer.Scheduled handle) -> handle
                    | other -> failtestf "a queued Submit answers a handle, got %A" other

                do! custodian.Scheduler.RunPending()
                return! send custodian requesterPeerId (DelegatedQueryPeerContract.resolveRequest handle)
            }

            let! first = resolveNext (submit rowQuery)

            let page =
                match first with
                | Ok(DelegatedQueryAnswer.Continued page) -> page
                | other -> failtestf "the parked answer is a page, got %A" other

            Expect.equal (countRows page.Result) 2 "the first page"

            for record in custodian.Results.All do
                match record.Status with
                | PeerJobStatus.Completed json ->
                    Expect.isFalse (json.Contains "o-7") "the whole answer is never parked"
                | other -> failtestf "a parked result is completed, got %A" other

            let! second = resolveNext (continueWith rowQuery page.Continuation)

            match second with
            | Ok(DelegatedQueryAnswer.Continued next) ->
                Expect.equal (rowsOf [ next.Result ]) (rows |> List.skip 2 |> List.take 2) "the next page"
            | other -> failtestf "the continuation is a page, got %A" other
        }

        testCaseAsync "a foreign token is refused when the Submit arrives, before anything is scheduled"
        <| async {
            let clock = Clock start

            let custodian =
                custodianWith full rowsTemplate DelegatedQueryLeg.Queued (pagedBounds clock) 1000m

            let! scheduled = send custodian requesterPeerId (submit rowQuery)

            let handle =
                match scheduled with
                | Ok(DelegatedQueryAnswer.Scheduled handle) -> handle
                | other -> failtestf "a queued Submit answers a handle, got %A" other

            do! custodian.Scheduler.RunPending()
            let! first = send custodian requesterPeerId (DelegatedQueryPeerContract.resolveRequest handle)

            let token =
                match first with
                | Ok(DelegatedQueryAnswer.Continued page) -> page.Continuation
                | other -> failtestf "the parked answer is a page, got %A" other

            let! stolen = send custodian otherRequesterPeerId (continueWith rowQuery token)
            expectRefusal stolen "delegated-query-continuation-foreign" "refused at Submit"
        }
    ]

let tests =
    testList "Phase 922 — bounds on a delegated-query answer" [
        descriptorTests
        ceilingTests
        continuationTests
        queuedTests
    ]