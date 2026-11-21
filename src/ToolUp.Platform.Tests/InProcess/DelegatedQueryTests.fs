module ToolUp.Platform.Tests.InProcess.DelegatedQueryTests

// The delegated-query profile, end to end over two deployments: a
// custodian that holds the connection to its data and an in-memory
// handler over it, and a requester whose whole vocabulary is the profile's
// envelope builders and answer reader. The requester holds no credential;
// every case below is a call it makes, and every answer is read back off
// the custodian's wire bytes.

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform.PeerAuditEvents
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose
open ToolUp.Platform.Audit
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose
open ToolUp.Platform.Tests.InProcess.PeerTestComposition

/// The custodian's credential. It must appear in no wire object, no audit
/// payload and no gate decision the fixture produces.
let private connectionString =
    "Server=custodian.internal;Database=ledger;User Id=svc;Password=s3cret-917"

let private custodianScope = "custodian-north"
let private requesterPeerId = "requester-acme"
let private strangerPeerId = "stranger-corp"
let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private floor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 10
    PermittedShapes = Set.ofList [ Count; Aggregate; Histogram ]
}

/// The queries the in-memory handler understands, in its own
/// representation. Forge never reads them.
let private aggregateQuery =
    """{"measure":"count","over":"orders","where":{"region":{"param":"region"}}}"""

let private smallCohortQuery =
    """{"measure":"count","over":"orders","where":{"tier":"top"}}"""

let private rowQuery = """{"records":"orders"}"""

let private misdeclaredQuery = """{"records":"orders","declare":"aggregate"}"""

let private rowAnswer = """[{"order":"o-1","customer":"alice@example.com"}]"""
let private parameters = Map.ofList [ "region", "\"north\"" ]

/// A submission whose body ALSO claims to be another principal. The
/// profile's envelope has no principal member, so the claim has nowhere to
/// ride: the handler is told the validated caller.
let private impersonatingQuery =
    """{"measure":"count","over":"orders","asPeer":"stranger-corp"}"""

/// The in-memory handler: holds the connection string, answers by query.
type private LedgerHandler() =
    let callers = ConcurrentQueue<DelegatedQueryCaller>()
    member _.Callers = callers |> List.ofSeq

    member _.Handler: DelegatedQueryHandler = {
        Describe =
            fun caller -> async {
                callers.Enqueue caller
                // The connection string is the handler's to use, never to say.
                ignore connectionString
                return Ok """{"tables":{"orders":["order","region","amount"]}}"""
            }
        Evaluate =
            fun caller submission -> async {
                callers.Enqueue caller

                if submission.Query = aggregateQuery || submission.Query = impersonatingQuery then
                    return
                        Ok {
                            Class = DelegatedQueryClass.Aggregate
                            Cohort = 42
                            Answer = """{"count":42}"""
                        }
                elif submission.Query = smallCohortQuery then
                    return
                        Ok {
                            Class = DelegatedQueryClass.Aggregate
                            Cohort = 3
                            Answer = """{"count":3}"""
                        }
                elif submission.Query = rowQuery then
                    return
                        Ok {
                            Class = DelegatedQueryClass.RowLevel
                            Cohort = 1
                            Answer = rowAnswer
                        }
                elif submission.Query = misdeclaredQuery then
                    return
                        Ok {
                            Class = DelegatedQueryClass.Aggregate
                            Cohort = 42
                            Answer = rowAnswer
                        }
                else
                    return Error "the handler does not read that query"
            }
    }

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

/// A result store keyed by scope AND job, so a test can tell which scope a
/// result parked under.
type private ScopedResultStore() =
    let records = ConcurrentDictionary<string * Guid, PeerJobRecord>()

    interface IPeerJobResultStore with
        member _.Retention = PeerJobRetentionPolicy.keepForever

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
    Ledger: LedgerHandler
    Scheduler: HeldScheduler
    Results: IPeerJobResultStore
    Budget: IPrivacyBudgetLedger
    Audit: ConcurrentQueue<DelegatedQueryAuditPayload>
    Decisions: ConcurrentQueue<PeerCleanRoomDecisionPayload>
    Wire: ConcurrentQueue<string>
}

let private budgetScope: BudgetScope = {
    TemplateId = "delegated-query"
    CounterpartyPeerId = requesterPeerId
    Epoch = "perpetual"
}

/// The custodian deployment: one binding per requester at `visibility`,
/// gated by `template`, on `leg`.
let private custodianWith
    (visibility: PeerVisibilityBinding)
    (template: CleanRoomTemplate)
    (leg: DelegatedQueryLeg)
    (classCheck: (DelegatedQueryCaller -> DelegatedQueryResult -> Result<unit, string>) option)
    =
    let ledger = LedgerHandler()
    let scheduler = HeldScheduler()
    let results = ScopedResultStore() :> IPeerJobResultStore
    let audit = ConcurrentQueue<DelegatedQueryAuditPayload>()
    let decisions = ConcurrentQueue<PeerCleanRoomDecisionPayload>()

    let budget =
        InMemoryPrivacyBudgetLedger(fun () -> DateTimeOffset.UtcNow) :> IPrivacyBudgetLedger

    let deps: DelegatedQueryDeps = {
        ResolveBinding =
            fun peerId -> async {
                if peerId = requesterPeerId then
                    return
                        Some {
                            PeerId = peerId
                            ScopeId = custodianScope
                            Visibility = visibility
                        }
                else
                    return None
            }
        Handler = ledger.Handler
        Leg = leg
        ClassCheck = classCheck
        Audit = fun payload -> async { audit.Enqueue payload }
    }

    let gate: DelegatedQueryGate = {
        Broker = CleanRoomBroker.create ()
        Template = template
        Approval = None
        Budget = Some(PrivacyBudgetMeter.create budget (PrivacyBudgetPolicy.create 1000m 1m))
        Audit = fun payload -> async { decisions.Enqueue payload }
    }

    let fusion: PeerJobFusion = {
        Scheduler = scheduler
        ResultStore = results
        AuditLog = NoOpAuditLog()
    }

    let peer = DefaultPlatformPeer("custodian") :> IPlatformPeer

    match DelegatedQueryPeerContract.governed gate deps (Some fusion) with
    | Ok host ->
        peer.RegisterContract host.Gated.Registration

        for name, handler in host.JobHandlers do
            (scheduler :> IJobScheduler).RegisterHandler(name, handler)
    | Error refusals ->
        failtestf
            "the gate was refused: %s"
            (refusals
             |> List.map DelegatedQueryTemplateRefusal.describe
             |> String.concat "; ")

    {
        Peer = peer
        Ledger = ledger
        Scheduler = scheduler
        Results = results
        Budget = budget
        Audit = audit
        Decisions = decisions
        Wire = ConcurrentQueue()
    }

let private aggregatesTemplate =
    DelegatedQueryProfile.template "delegated-query" floor

let private custodian () =
    custodianWith PeerVisibilityBinding.default' aggregatesTemplate DelegatedQueryLeg.Immediate None

/// The context the custodian's transport derives from the VALIDATED
/// credential of `peerId`.
let private contextFor (peerId: string) : PeerCallContext = {
    Peer = {
        PeerId = peerId
        DisplayName = peerId
    }
    User = Anonymous
    ContractVersion = v1
    Route = [ peerId ]
    RootRequestId = "root-917"
    ParentRequestId = None
    HopsRemaining = 4
}

/// The requester's side of one call: send raw arguments, keep the wire
/// bytes, read the answer.
let private sendRaw (custodian: Custodian) (peerId: string) (operation: string) (argsJson: string) = async {
    let! outcome = custodian.Peer.Handle(DelegatedQueryProfile.ContractId, contextFor peerId, operation, argsJson)

    match outcome with
    | Ok json ->
        custodian.Wire.Enqueue json

        match DelegatedQueryPeerContract.readAnswer json with
        | Ok answer -> return Ok answer
        | Error reason -> return failtestf "the custodian answered an unreadable document (%s): %s" reason json
    | Error e -> return Error e
}

let private send (custodian: Custodian) (peerId: string) (request: DelegatedQueryRequest) =
    sendRaw custodian peerId request.Operation (DelegatedQueryPeerContract.arguments request)

let private submit (query: string) =
    DelegatedQueryPeerContract.submissionRequest {
        Query = query
        Parameters = parameters
    }

let private expectRefusal (outcome: Result<DelegatedQueryAnswer, PeerError>) (expected: string) (why: string) =
    match outcome with
    | Ok(DelegatedQueryAnswer.Refused refusal) -> Expect.equal (DelegatedQueryRefusal.className refusal) expected why
    | other -> failtestf "%s — expected a %s refusal, got %A" why expected other

let private expectWithheld (outcome: Result<DelegatedQueryAnswer, PeerError>) (why: string) =
    match outcome with
    | Error(PeerCleanRoomWithheld id) -> Expect.equal id "delegated-query" why
    | other -> failtestf "%s — expected the gate to withhold, got %A" why other

let private roundTripTests =
    testList "a requester asks; the custodian answers" [
        testCaseAsync "Describe answers the schema and the classes the grant admits"
        <| async {
            let custodian = custodian ()
            let! described = send custodian requesterPeerId (DelegatedQueryPeerContract.describeRequest ())

            match described with
            | Ok(DelegatedQueryAnswer.Described description) ->
                Expect.stringContains description.Schema "orders" "the handler's per-peer schema rides through"

                Expect.equal
                    description.Classes
                    [ DelegatedQueryClass.Aggregate ]
                    "AggregatesOnly admits aggregates only"

                Expect.equal description.Leg DelegatedQueryLeg.Immediate "the leg is published"
            | other -> failtestf "Describe must answer a description, got %A" other
        }

        testCaseAsync "an aggregate that clears the floor is answered with the handler's bytes unchanged"
        <| async {
            let custodian = custodian ()
            let! answered = send custodian requesterPeerId (submit aggregateQuery)

            match answered with
            | Ok(DelegatedQueryAnswer.Answered result) ->
                Expect.equal result.Class DelegatedQueryClass.Aggregate "the declared class rides through"
                Expect.equal result.Answer """{"count":42}""" "the opaque answer is not re-encoded"
            | other -> failtestf "the aggregate must be answered, got %A" other
        }

        testCaseAsync "an aggregate below the minimum cohort is withheld by the gate, not the handler"
        <| async {
            let custodian = custodian ()
            let! answer = send custodian requesterPeerId (submit smallCohortQuery)
            expectWithheld answer "a cohort of 3 under a floor of 10 never crosses"

            Expect.isTrue
                (custodian.Decisions |> Seq.exists (fun d -> not d.Released))
                "the withhold is recorded receiver-side"
        }

        testCaseAsync "a handler's refusal reaches the requester typed"
        <| async {
            let custodian = custodian ()
            let! refused = send custodian requesterPeerId (submit "{}")
            expectRefusal refused "delegated-query-handler-refused" "the handler declined the query"
        }
    ]

let private authorityTests =
    testList "the declared class against the authority level" [
        testCaseAsync "a row-level answer at AggregatesOnly is refused before any byte of it is written"
        <| async {
            let custodian = custodian ()
            let! refused = send custodian requesterPeerId (submit rowQuery)
            expectRefusal refused "delegated-query-authority-level-exceeded" "rows need Full"

            Expect.isFalse
                (custodian.Wire |> Seq.exists (fun w -> w.Contains "alice@example.com"))
                "no byte of the row-level answer reached the wire"
        }

        testCaseAsync "a narrowing beneath a Full ceiling refuses as a narrowing"
        <| async {
            let visibility =
                PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.Full
                |> PeerVisibilityBinding.withNarrowing
                    (TeamNarrowing "north-analysts")
                    PeerDataVisibilityLevel.AggregatesOnly

            let custodian =
                custodianWith
                    visibility
                    (DelegatedQueryProfile.withRowRelease aggregatesTemplate)
                    DelegatedQueryLeg.Immediate
                    None

            let! refused = send custodian requesterPeerId (submit rowQuery)
            expectRefusal refused "delegated-query-authority-narrowed" "the team narrowing is named"
        }

        testCaseAsync "at Full, rows are withheld unless the template releases them"
        <| async {
            let full = PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.Full

            let unreleased =
                custodianWith full aggregatesTemplate DelegatedQueryLeg.Immediate None

            let! withheld = send unreleased requesterPeerId (submit rowQuery)
            expectWithheld withheld "a template that releases no rows releases no rows, whatever the grant"

            let released =
                custodianWith
                    full
                    (DelegatedQueryProfile.withRowRelease aggregatesTemplate)
                    DelegatedQueryLeg.Immediate
                    None

            let! answered = send released requesterPeerId (submit rowQuery)

            match answered with
            | Ok(DelegatedQueryAnswer.Answered result) -> Expect.equal result.Answer rowAnswer "the rows cross"
            | other -> failtestf "a released row-level answer at Full must be answered, got %A" other

            let! described = send released requesterPeerId (DelegatedQueryPeerContract.describeRequest ())

            match described with
            | Ok(DelegatedQueryAnswer.Described d) ->
                Expect.equal
                    d.Classes
                    [ DelegatedQueryClass.Aggregate; DelegatedQueryClass.RowLevel ]
                    "Describe names rows exactly when both the grant and the template admit them"
            | other -> failtestf "Describe must answer, got %A" other
        }

        testCaseAsync "a composed class check refuses an answer that declares aggregate and carries rows"
        <| async {
            let check (_: DelegatedQueryCaller) (result: DelegatedQueryResult) =
                if result.Class = DelegatedQueryClass.Aggregate && result.Answer.StartsWith "[" then
                    Error "an array of records is not an aggregate"
                else
                    Ok()

            let custodian =
                custodianWith PeerVisibilityBinding.default' aggregatesTemplate DelegatedQueryLeg.Immediate (Some check)

            let! refused = send custodian requesterPeerId (submit misdeclaredQuery)
            expectRefusal refused "delegated-query-class-misdeclared" "the deployment's check decides"
        }
    ]

let private admissionTests =
    testList "admission is the receiver's" [
        testCaseAsync "an unbound peer addresses nothing"
        <| async {
            let custodian = custodian ()
            let! refused = send custodian strangerPeerId (submit aggregateQuery)
            expectRefusal refused "delegated-query-peer-unbound" "no binding, no scope"
        }

        testCaseAsync "an asserted scope other than the binding's is refused, never routed on"
        <| async {
            let custodian = custodian ()

            let request = {
                submit aggregateQuery with
                    AssertedScope = Some "custodian-south"
            }

            let! refused = send custodian requesterPeerId request
            expectRefusal refused "delegated-query-scope-widening" "the assertion is checked"

            let agreeing = {
                submit aggregateQuery with
                    AssertedScope = Some custodianScope
            }

            let! answered = send custodian requesterPeerId agreeing

            Expect.isTrue
                (match answered with
                 | Ok(DelegatedQueryAnswer.Answered _) -> true
                 | _ -> false)
                "an assertion that agrees is a self-check, and passes"
        }

        testCaseAsync "a later profile version, an unknown operation and a malformed envelope are refused by class"
        <| async {
            let custodian = custodian ()

            let! later =
                send custodian requesterPeerId {
                    submit aggregateQuery with
                        ProfileVersion = 2
                }

            expectRefusal later "delegated-query-profile-version-unsupported" "never read partially"

            // A method off the template's surface never reaches the contract:
            // the gate withholds it before anything runs.
            let! offSurface = send custodian requesterPeerId (DelegatedQueryPeerContract.request "ReadPage" "{}")
            expectWithheld offSurface "only the three invocations are on the surface"

            // An envelope naming another operation than the method it was sent
            // to is the contract's to refuse.
            let mismatched = DelegatedQueryPeerContract.request "ReadPage" "{}"

            let! unknown =
                sendRaw
                    custodian
                    requesterPeerId
                    DelegatedQueryProfile.SubmitOperation
                    (DelegatedQueryPeerContract.arguments mismatched)

            expectRefusal unknown "delegated-query-unknown-operation" "the envelope and the method must agree"

            let! malformed = sendRaw custodian requesterPeerId "Submit" (JsonRpc.serialize [ "{\"Operation\":" ])
            expectRefusal malformed "delegated-query-request-unreadable" "not an envelope"
        }

        testCaseAsync "a body that claims another principal is evaluated as the validated caller"
        <| async {
            let custodian = custodian ()

            // The envelope and the query both name another peer. Neither
            // has a member the profile reads as a principal.
            let request = submit impersonatingQuery

            let claimed =
                (DelegatedQueryPeerContract.arguments request)
                    .Replace("\"ProfileVersion\"", "\"Peer\":\"stranger-corp\",\"ProfileVersion\"")

            let! answered = sendRaw custodian requesterPeerId "Submit" claimed

            Expect.isTrue
                (match answered with
                 | Ok(DelegatedQueryAnswer.Answered _) -> true
                 | _ -> false)
                "the call is answered"

            let callers = custodian.Ledger.Callers
            Expect.isNonEmpty callers "the handler ran"

            for caller in callers do
                Expect.equal caller.PeerId requesterPeerId "the handler is told the validated peer"
                Expect.equal caller.ScopeId custodianScope "and the binding's scope"
        }
    ]

let private queuedTests =
    testList "the queued leg" [
        testCaseAsync "Submit answers a handle; Resolve collects the result through the gate"
        <| async {
            let custodian =
                custodianWith PeerVisibilityBinding.default' aggregatesTemplate DelegatedQueryLeg.Queued None

            let! scheduled = send custodian requesterPeerId (submit aggregateQuery)

            let handle =
                match scheduled with
                | Ok(DelegatedQueryAnswer.Scheduled handle) -> handle
                | other -> failtestf "a queued Submit answers a handle, got %A" other

            let! early = send custodian requesterPeerId (DelegatedQueryPeerContract.resolveRequest handle)
            Expect.equal early (Ok DelegatedQueryAnswer.Pending) "not run yet"

            do! custodian.Scheduler.RunPending()

            let! stolen = send custodian strangerPeerId (DelegatedQueryPeerContract.resolveRequest handle)
            expectRefusal stolen "delegated-query-peer-unbound" "an unbound peer addresses nothing"

            let! resolved = send custodian requesterPeerId (DelegatedQueryPeerContract.resolveRequest handle)

            match resolved with
            | Ok(DelegatedQueryAnswer.Answered result) -> Expect.equal result.Answer """{"count":42}""" "collected"
            | other -> failtestf "the parked result must be collected, got %A" other

            let jobId = Guid.Parse handle
            let! onPollScope = custodian.Results.TryGetResult(PeerJob.Scope, jobId)

            Expect.isNone
                onPollScope
                "nothing parks where the transport's poll route reads, so only Resolve, which is gated, collects it"
        }

        testCaseAsync "a parked row-level answer is re-judged against the grant as it stands"
        <| async {
            let custodian =
                custodianWith PeerVisibilityBinding.default' aggregatesTemplate DelegatedQueryLeg.Queued None

            let! scheduled = send custodian requesterPeerId (submit rowQuery)

            let handle =
                match scheduled with
                | Ok(DelegatedQueryAnswer.Scheduled handle) -> handle
                | other -> failtestf "a queued Submit answers a handle, got %A" other

            do! custodian.Scheduler.RunPending()
            let! resolved = send custodian requesterPeerId (DelegatedQueryPeerContract.resolveRequest handle)
            expectRefusal resolved "delegated-query-authority-level-exceeded" "rows need Full at resolve too"
        }
    ]

let private substrateTests =
    testList "the substrate's budget and audit" [
        testCaseAsync "an answer is charged; a description returns its reservation"
        <| async {
            let custodian = custodian ()
            let! _ = send custodian requesterPeerId (DelegatedQueryPeerContract.describeRequest ())
            let! afterDescribe = custodian.Budget.RemainingBudget(budgetScope, 1000m)
            Expect.equal afterDescribe.EpsilonCommitted 0m "a description carries no protected data"

            let! _ = send custodian requesterPeerId (submit aggregateQuery)
            let! afterAnswer = custodian.Budget.RemainingBudget(budgetScope, 1000m)
            Expect.equal afterAnswer.EpsilonCommitted 1m "an answer is charged to the counterparty's budget"
        }

        testCaseAsync "the audit record carries the query's hash and the declared class, never the query"
        <| async {
            let custodian = custodian ()
            let! _ = send custodian requesterPeerId (submit aggregateQuery)

            let record =
                custodian.Audit
                |> Seq.find (fun a -> a.Operation = DelegatedQueryProfile.SubmitOperation)

            Expect.equal record.QueryHash (Some(DelegatedQueryProfile.hash aggregateQuery)) "the query's hash"
            Expect.equal record.AnswerClass (Some "Aggregate") "the declared class"
            Expect.equal record.CallerPeerId requesterPeerId "the validated caller"
            let serialised = JsonRpc.serialize record
            Expect.isFalse (serialised.Contains "orders") "the query itself is not recorded"
            Expect.isFalse (serialised.Contains "count") "nor the answer"
        }

        testCaseAsync "the connection string appears in no wire object, audit payload or decision"
        <| async {
            let full = PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.Full

            for custodian in
                [
                    custodian ()
                    custodianWith
                        full
                        (DelegatedQueryProfile.withRowRelease aggregatesTemplate)
                        DelegatedQueryLeg.Queued
                        None
                ] do
                let! _ = send custodian requesterPeerId (DelegatedQueryPeerContract.describeRequest ())

                for query in [ aggregateQuery; smallCohortQuery; rowQuery; "{}" ] do
                    let! answer = send custodian requesterPeerId (submit query)

                    match answer with
                    | Ok(DelegatedQueryAnswer.Scheduled handle) ->
                        do! custodian.Scheduler.RunPending()
                        let! _ = send custodian requesterPeerId (DelegatedQueryPeerContract.resolveRequest handle)
                        ()
                    | _ -> ()

                let produced =
                    List.ofSeq custodian.Wire
                    @ (custodian.Audit |> Seq.map JsonRpc.serialize |> List.ofSeq)
                    @ (custodian.Decisions |> Seq.map JsonRpc.serialize |> List.ofSeq)

                Expect.isGreaterThan produced.Length 5 "the fixture produced documents to read"

                for document in produced do
                    Expect.isFalse (document.Contains "s3cret-917") "the credential never leaves the custodian"
                    Expect.isFalse (document.Contains "custodian.internal") "nor its host"
        }
    ]

let private compositionTests =
    testList "composition" [
        testCase "a template switching noise on is refused by name"
        <| fun () ->
            let noised = {
                aggregatesTemplate with
                    Release = {
                        aggregatesTemplate.Release with
                            Noise = Some(NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m))
                    }
            }

            Expect.equal
                (DelegatedQueryPeerContract.check noised)
                [ DelegatedQueryTemplateRefusal.NoiseDeclared "delegated-query" ]
                "an opaque answer cannot carry noise"

        testCase "a release declaration the profile does not read is refused by name"
        <| fun () ->
            let odd = {
                aggregatesTemplate with
                    Release = {
                        aggregatesTemplate.Release with
                            Declarations = Map.ofList [ "Describe", "row-level" ]
                    }
            }

            Expect.equal
                (DelegatedQueryPeerContract.check odd)
                [
                    DelegatedQueryTemplateRefusal.UnreadableDeclaration("delegated-query", "Describe", "row-level")
                ]
                "rows are released from Submit and Resolve only"

        testCase "the peer surface declares the profile exactly when a deployment composes it"
        <| fun () ->
            let enabled = {
                ServerConfig.defaults with
                    MultiInstance = {
                        ServerConfig.defaults.MultiInstance with
                            PeerSubstrate = FeatureMode.Enabled
                    }
            }

            let gate: DelegatedQueryGate = {
                Broker = CleanRoomBroker.create ()
                Template = aggregatesTemplate
                Approval = None
                Budget = None
                Audit = CleanRoomGate.noAudit
            }

            let deps: DelegatedQueryDeps = {
                ResolveBinding = fun _ -> async { return None }
                Handler = LedgerHandler().Handler
                Leg = DelegatedQueryLeg.Immediate
                ClassCheck = None
                Audit = DelegatedQueryPeerContract.noAudit
            }

            let served (composition: PeerComposition) =
                (PeerSurface.describe composition).Serves.Contracts |> List.map _.ContractId

            let bare =
                PeerComposition.create () |> PeerComposition.app (ServerApp.withConfig enabled)

            Expect.isFalse
                (served bare |> List.contains DelegatedQueryProfile.ContractId)
                "an enabled substrate without the contract serves no delegated query"

            Expect.isEmpty (served (PeerComposition.create ())) "NoPeerSubstrate publishes the empty surface, unchanged"

            match DelegatedQueryPeerContract.builder gate deps with
            | Error refusals -> failtestf "refused: %A" refusals
            | Ok builder ->
                let composed = bare |> PeerComposition.options (PeerOptions.withContract builder)

                Expect.contains
                    (served composed)
                    DelegatedQueryProfile.ContractId
                    "the descriptor a counterparty pins names the profile's contract"
    ]

let tests =
    testList "Phase 917 — the delegated-query profile" [
        roundTripTests
        authorityTests
        admissionTests
        queuedTests
        substrateTests
        compositionTests
    ]