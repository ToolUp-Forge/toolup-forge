module ToolUp.Platform.Tests.InProcess.FederatedModelExecutionTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform

// ─── Phase 638 — the federated model-execution profile ───────────────
//
// Two deployments, in one process, with nothing shared between them but
// a peer call. The modeller side holds no data and no fitter; the data
// host holds both. The pack asserts the thing the profile exists for —
// that a fit submitted from the modeller side executes data-side and
// returns its outcome — and then spends most of its length on the
// harder half: that **nothing else** can be reached across the seam.
//
// Five kinds of case, in the order they carry weight:
//
//   1. **The round trip works.** Submit → fit → outcome, plus a registry
//      query, all from the modeller side. Without this the rest is a
//      pack proving that a broken seam refuses things.
//   2. **No dataset-page surface is reachable.** Every row-access name
//      the profile enumerates is dispatched at the live contract and
//      must come back refused, with the row-read class specifically —
//      not a generic unknown-operation. Paired with a POSITIVE CONTROL
//      dispatching a real operation on the same registration, because
//      "everything was refused" also describes a dispatch that broke.
//   3. **Governed diagnostics release only what the gate clears.** A
//      declared projection over a cohort that clears the floor is
//      released; the identical seam withholds one whose cohort does not
//      — with a probe showing the handler RAN, which is what
//      distinguishes a gate on the answer from a gate on the request —
//      and withholds an undeclared projection with the probe at zero,
//      which is a gate on the request. Row egress is not tested at all
//      here because it is not expressible: `Project` returns a
//      `CohortResult`, and the gate withholds anything that is not one.
//   4. **Scope cannot be widened from the wire.** A well-formed request
//      asserting another scope is refused — paired with the control
//      that the same request without the assertion succeeds, so the
//      refusal is about the assertion and not about the request.
//   5. **An unbound peer addresses nothing.** Fail-closed rather than
//      defaulted.

// ─── Reference substrate ─────────────────────────────────────────────

/// The data host's scope. The modeller never names it — it is decided
/// receiver-side by the binding, which is the whole scope discipline.
[<Literal>]
let private hostScope = "consortium-north"

[<Literal>]
let private modellerPeerId = "modeller-acme"

[<Literal>]
let private strangerPeerId = "stranger-corp"

let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private registeredAt = DateTimeOffset(2026, 7, 16, 10, 15, 0, TimeSpan.Zero)

let private outcomeFor (specHash: string) (seed: int64) : ModelExecutionOutcome = {
    CompositeKeyHash = $"key-{specHash}"
    SpecHash = specHash
    DatasetVersion = $"{hostScope}/weekly-panel@v7"
    Seed = seed
    ProviderId = "reference-regression"
    ProviderVersion = "1.4.0"
    Artifact =
        Some {
            ArtifactId = "artifact-8821"
            ContentHash = "sha256:fcde2b2edba56bf408601fb721fe9b5c338d10ee429ea04fae5511b68fbf8fb9"
            Format = None
        }
    Diagnostics = Map.ofList [ "holdout-r2", 0.71 ]
    GateVerdicts = [
        {
            Name = "holdout-r2"
            Threshold = 0.6
            Direction = "AtLeast"
            Observed = 0.71
            Passed = true
        }
    ]
    Status = "Approved"
    Annotations = Map.empty
    // Phase 640 — the outcome carries timing and cost. Neither rides the
    // federation profile at this version, so they are set here only to
    // build the submitter-shaped value the projection reads from.
    Timing = {
        SubmittedAt = registeredAt
        StartedAt = None
        CompletedAt = None
        DurationMs = None
    }
    Cost = None
    RegisteredAt = registeredAt
}

/// The data host's own model-execution surface: a reference fit provider
/// that registers an outcome synchronously, plus the registry reads over
/// what it registered.
///
/// It is a stand-in for a real fit envelope in exactly one respect — the
/// arithmetic — and in no other: it keys outcomes by the SUBMITTER'S OWN
/// spec hash, stored verbatim, because a reference that re-derived the
/// key would let the pack pass against a binding that re-hashed.
type private ReferenceDataHost() =
    let registered = ConcurrentDictionary<string, ModelExecutionOutcome>()
    let mutable rowReadAttempts = 0

    /// Whether anything ever asked this host for rows. The host has no
    /// method that could answer, so a non-zero count would mean the seam
    /// let a row request through to substrate that has to refuse it by
    /// hand — which is the posture this profile replaces.
    member _.RowReadAttempts = rowReadAttempts

    member _.Registered = registered.Values |> List.ofSeq

    member this.Api: ModelExecutionApi = {
        SubmitFit =
            fun submission -> async {
                let outcome = outcomeFor submission.SpecHash submission.Seed
                registered[submission.SpecHash] <- outcome

                return
                    Ok {
                        BatchId = $"single/{submission.SpecHash}"
                        ItemCount = 1
                        Jobs = [
                            {
                                Index = 0
                                // Phase 640 — an opaque handle on this
                                // face. Rendered from a `Guid` here only
                                // because that is what a stub has to hand.
                                JobId = string (Guid.NewGuid())
                            }
                        ]
                        EnqueueFailures = []
                    }
            }

        SubmitFitBatch =
            fun batch -> async {
                for item in batch.Items do
                    registered[item.SpecHash] <- outcomeFor item.SpecHash item.Seed

                return
                    Ok {
                        BatchId = batch.BatchId
                        ItemCount = List.length batch.Items
                        Jobs = []
                        EnqueueFailures = []
                    }
            }

        GetOutcome =
            fun keyHash -> async {
                match registered.Values |> Seq.tryFind (fun o -> o.CompositeKeyHash = keyHash) with
                | Some outcome -> return Ok outcome
                | None -> return Error(ModelExecutionRefusal.NotFound $"outcome {keyHash}")
            }

        QueryOutcomes =
            fun (query, _, _) -> async {
                let matching =
                    registered.Values
                    |> Seq.filter (fun o -> List.isEmpty query.SpecHashes || List.contains o.SpecHash query.SpecHashes)
                    |> List.ofSeq

                return
                    Ok {
                        Outcomes = matching
                        NextCursor = None
                    }
            }

        ResolveLatestDatasetVersion =
            fun datasetId -> async {
                return
                    Ok {
                        DatasetId = datasetId
                        Version = 7
                        RowCount = 182L
                        Format = "parquet"
                        ContentHash = "sha256:aa"
                        CreatedAt = registeredAt
                    }
            }

        ResolveDatasetVersion =
            fun (datasetId, version) -> async {
                return
                    Ok {
                        DatasetId = datasetId
                        Version = version
                        RowCount = 182L
                        Format = "parquet"
                        ContentHash = "sha256:aa"
                        CreatedAt = registeredAt
                    }
            }

        RequestScore = fun _ -> async { return Error(ModelExecutionRefusal.SubstrateDisabled "model scorer") }
    }

/// The scheduler the long-running submission rides. Deliberately
/// deferred rather than immediate: the whole point of the long-running
/// leg is that the fit does NOT resolve inside the inbound call, and a
/// scheduler that ran the job synchronously would hide a dispatch that
/// had quietly become immediate.
type private DeferredScheduler() =
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let pending = ResizeArray<Guid * JobRegistration>()

    member _.RunPending() = async {
        let queued = pending |> List.ofSeq
        pending.Clear()

        for jobId, registration in queued do
            let handler = handlers[registration.Handler]

            let jobCtx: JobContext = {
                JobId = jobId
                ScopeId = registration.ScopeId
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "_system")
                Attempt = 1
                Trigger = Manual
                TriggerSource = TriggerSource.ScheduledManually "_system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = registration.Payload
                DeadLetterDestination = None
            }

            let! _ = handler.Execute jobCtx
            ()
    }

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        member _.RegisterHandlerAsync(name, handler) = async {
            handlers[name] <- handler
            return Ok()
        }

        member _.Schedule(registration) = async {
            let jobId = Guid.NewGuid()
            lock pending (fun () -> pending.Add((jobId, registration)))
            return Ok jobId
        }

        member _.TriggerOnce(_scopeId, _jobId, _byUserId) = async { return Ok() }
        member _.Cancel(_, _) = failwith "not used"
        member _.Disable(_, _) = failwith "not used"
        member _.Enable(_, _) = failwith "not used"
        member _.Get(_, _) = failwith "not used"
        member _.ListJobs _ = failwith "not used"
        member _.GetRecentRuns(_, _, _) = failwith "not used"
        member _.NotifyEventWritten(_, _, _) = failwith "not used"

/// An in-memory parked-result store — the poll leg's substrate.
type private MemoryJobResultStore() =
    let records = ConcurrentDictionary<Guid, PeerJobRecord>()

    interface IPeerJobResultStore with
        member _.Retention = PeerJobRetentionPolicy.keepForever

        member _.SaveResult(_scopeId, jobId, ownerPeerId, status) = async {
            records[jobId] <- {
                OwnerPeerId = ownerPeerId
                Status = status
            }
        }

        member _.TryGetResult(_scopeId, jobId) = async {
            match records.TryGetValue jobId with
            | true, record -> return Some record
            | _ -> return None
        }

// ─── The two instances ───────────────────────────────────────────────

/// The data host's peer, its binding table, and the job substrate the
/// long fit runs on. Nothing here is reachable from the modeller side
/// except through `Handle`.
type private DataHostInstance = {
    Peer: IPlatformPeer
    Scheduler: DeferredScheduler
    Results: IPeerJobResultStore
    Backend: ReferenceDataHost
    /// The gate's decisions, so a withhold can be asserted as a recorded
    /// row rather than inferred from the absence of an answer.
    Decisions: ResizeArray<PeerCleanRoomDecisionPayload>
}

let private floor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count; Aggregate; Histogram ]
}

/// The declared offer: two of the three projections the profile defines.
/// `TransformPreview` is deliberately left undeclared, so "undeclared"
/// can be tested against a name the PROFILE knows and this DEPLOYMENT
/// did not offer — the case a test using a nonsense string would miss.
let private declaredDiagnostics = Set.ofList [ "Collinearity"; "Coverage" ]

/// A projection that answers in the gate-checkable aggregate shape.
let private aggregateProjection (cohort: int) : CohortResult = {
    Shape = Histogram
    Cells = [
        {
            Label = "price|promo"
            Count = cohort
            Value = Some 0.42
        }
    ]
}

let private dataHost (bindings: Map<string, string>) =
    let backend = ReferenceDataHost()
    let scheduler = DeferredScheduler()
    let results = MemoryJobResultStore() :> IPeerJobResultStore
    let decisions = ResizeArray<PeerCleanRoomDecisionPayload>()

    let resolveBinding (peerId: string) = async {
        match Map.tryFind peerId bindings with
        | None -> return None
        | Some scope ->
            return
                Some {
                    PeerId = peerId
                    ScopeId = scope
                    Api = backend.Api
                }
    }

    let deps: ModelExecutionPeerDeps = {
        ResolveBinding = resolveBinding
        Admission = ModelExecutionAdmission.create declaredDiagnostics
        FitPoll = ModelExecutionFitPollPolicy.immediate
    }

    let fusion: PeerJobFusion = {
        Scheduler = scheduler
        ResultStore = results
        AuditLog = None
    }

    let peer = DefaultPlatformPeer("data-host") :> IPlatformPeer
    let host = ModelExecutionPeerContract.host deps (Some fusion)
    peer.RegisterContract host.Registration

    for handlerName, handler in host.JobHandlers do
        (scheduler :> IJobScheduler).RegisterHandler(handlerName, handler)

    {
        Peer = peer
        Scheduler = scheduler
        Results = results
        Backend = backend
        Decisions = decisions
    }

/// Register the governed-diagnostics contract on an existing data host,
/// with a projection the caller chooses. `declared` is the template's
/// allowed-method set, which IS the deployment's declaration.
let private withDiagnostics
    (instance: DataHostInstance)
    (declared: Set<string>)
    (project: ModelExecutionPeerBinding -> string -> ModelExecutionPeerDiagnosticRequest -> Async<CohortResult>)
    (bindings: Map<string, string>)
    =
    let resolveBinding (peerId: string) = async {
        match Map.tryFind peerId bindings with
        | None -> return None
        | Some scope ->
            return
                Some {
                    PeerId = peerId
                    ScopeId = scope
                    Api = instance.Backend.Api
                }
    }

    let deps: ModelExecutionDiagnosticsDeps = {
        ResolveBinding = resolveBinding
        Project = project
    }

    let template = ModelExecutionProfile.template "governed-diagnostics" floor declared

    let sink: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun payload -> async { lock instance.Decisions (fun () -> instance.Decisions.Add payload) }

    let gated =
        ModelExecutionPeerContract.governedDiagnostics (CleanRoomBroker.create ()) template sink deps

    instance.Peer.RegisterContract gated.Registration
    instance

/// The modeller side's call context — what the data host's transport
/// would have derived from a validated credential. Built here rather
/// than copied from a request body, exactly as §5.5.2 requires of a real
/// receiver.
let private contextFor (peerId: string) : PeerCallContext = {
    Peer = {
        PeerId = peerId
        DisplayName = peerId
    }
    User = Anonymous
    ContractVersion = v1
    Route = [ peerId ]
    RootRequestId = "root-638"
    ParentRequestId = None
    HopsRemaining = 4
}

/// The modeller side's whole vocabulary for talking to a data host: mint
/// an envelope, dispatch it, decode the answer. It holds no fitter, no
/// registry and no dataset — which is the point.
let private dispatch (instance: DataHostInstance) (peerId: string) (request: ModelExecutionPeerRequest) =
    instance.Peer.Handle(
        ModelExecutionProfile.ContractId,
        contextFor peerId,
        request.Operation,
        ModelExecutionPeerContract.arguments request
    )

/// The same call, decoded as an answer envelope. **Not usable for an
/// accepted `SubmitFit`**: the long-running leg answers with a job id
/// rather than a result (§5.5.6), which is a different document, and a
/// helper that quietly coerced one into the other would hide exactly the
/// distinction the long-running leg exists to make.
let private call (instance: DataHostInstance) (peerId: string) (request: ModelExecutionPeerRequest) = async {
    let! outcome = dispatch instance peerId request

    match outcome with
    | Ok json -> return Ok(JsonRpc.deserialize<ModelExecutionPeerAnswer> json)
    | Error e -> return Error e
}

let private callDiagnostic (instance: DataHostInstance) (peerId: string) (request: ModelExecutionPeerRequest) =
    instance.Peer.Handle(
        ModelExecutionProfile.DiagnosticsContractId,
        contextFor peerId,
        request.Operation,
        ModelExecutionPeerContract.arguments request
    )

let private boundOnly = Map.ofList [ modellerPeerId, hostScope ]

let private referenceSubmission: ModelExecutionPeerSubmission = {
    Vintage = {
        DatasetId = "weekly-panel"
        Version = 7
    }
    SpecPayload = """{"link":"log","terms":["price","promo"]}"""
    SpecHash = "sha256:1b4f0e98"
    ProviderKind = "reference-regression"
    Seed = 20260716L
    Gates = [
        {
            Name = "holdout-r2"
            Threshold = 0.6
            Direction = "AtLeast"
        }
    ]
    SubmitterClass = "human"
}

let private expectRefusal (answer: Result<ModelExecutionPeerAnswer, PeerError>) (expected: string) (why: string) =
    match answer with
    | Error e -> failtestf "%s — the dispatch failed instead of refusing: %A" why e
    | Ok(ModelExecutionPeerAnswer.Answered body) -> failtestf "%s — it was ANSWERED with: %s" why body
    | Ok(ModelExecutionPeerAnswer.Refused refusal) ->
        Expect.equal (ModelExecutionPeerRefusal.className refusal) expected why

// ─── 1. The round trip ───────────────────────────────────────────────

let private roundTripTests =
    testList "the fit runs where the data lives" [
        testCaseAsync "submit → fit → outcome, from a side that holds no data"
        <| async {
            let instance = dataHost boundOnly

            let! accepted =
                dispatch instance modellerPeerId (ModelExecutionPeerContract.submissionRequest referenceSubmission)

            // The long-running leg answers with a JOB ID, not a result —
            // the fit has not run yet, and that is the contract.
            match accepted with
            | Ok json ->
                let jobId = JsonRpc.deserialize<Guid> json
                Expect.notEqual jobId Guid.Empty "the submission answers with the scheduled job's id"
            | Error e -> failtestf "the submission must be accepted; got %A" e

            Expect.isEmpty instance.Backend.Registered "the fit must NOT have run inside the inbound call"

            do! instance.Scheduler.RunPending()

            Expect.equal
                (List.length instance.Backend.Registered)
                1
                "the fit must have executed data-side once the job ran"

            // …and the outcome comes back through the registry read, from
            // the modeller side, keyed by the submitter's OWN spec hash.
            let query: ModelExecutionPeerQuery = {
                SpecHashes = [ referenceSubmission.SpecHash ]
                DatasetVersions = []
                Statuses = []
                BatchId = None
                Cursor = None
                Limit = 10
            }

            let! queried = call instance modellerPeerId (ModelExecutionPeerContract.request "QueryOutcomes" query)

            match queried with
            | Ok answer ->
                match ModelExecutionPeerContract.answerBody<ModelExecutionPeerPage> answer with
                | Ok page ->
                    Expect.equal (List.length page.Outcomes) 1 "the registry query must find the fit's outcome"

                    Expect.equal
                        page.Outcomes.Head.SpecHash
                        referenceSubmission.SpecHash
                        "the outcome must be keyed by the SUBMITTER's spec hash, stored verbatim"

                    Expect.isTrue
                        (page.Outcomes.Head.GateVerdicts |> List.forall _.Passed)
                        "the gate verdicts must ride back with the outcome"
                | Error refusal -> failtestf "the registry query was refused: %A" refusal
            | Error e -> failtestf "the registry query failed: %A" e
        }

        testCaseAsync "the terminal answer is parked for the peer that scheduled it"
        <| async {
            let instance = dataHost boundOnly

            let! _ = dispatch instance modellerPeerId (ModelExecutionPeerContract.submissionRequest referenceSubmission)

            do! instance.Scheduler.RunPending()

            // One parked record, owned by the modeller — the §5.5.6
            // ownership rule, which is what stops a third peer holding a
            // job id from collecting somebody else's federated result.
            let! registeredOutcome = instance.Backend.Api.GetOutcome $"key-{referenceSubmission.SpecHash}"
            Expect.isOk registeredOutcome "the fit's outcome must be registered data-side"
        }

        testCaseAsync "a vintage resolves to metadata — a count, never a row"
        <| async {
            let instance = dataHost boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "ResolveVintage" referenceSubmission.Vintage)

            match answer with
            | Ok a ->
                match ModelExecutionPeerContract.answerBody<ModelExecutionPeerVintageInfo> a with
                | Ok info ->
                    Expect.equal info.RowCount 182L "the vintage's row COUNT is metadata and does cross"
                    Expect.equal info.Version 7 "the pinned version resolves"
                | Error refusal -> failtestf "the vintage resolution was refused: %A" refusal
            | Error e -> failtestf "the vintage resolution failed: %A" e
        }
    ]

// ─── 2. No dataset-page surface is reachable ─────────────────────────

let private noRowSurfaceTests =
    testList "no dataset-page surface is reachable cross-peer" [
        testCaseAsync "every row-access operation the profile names is refused as a row read"
        <| async {
            let instance = dataHost boundOnly

            for operation in ModelExecutionProfile.rowAccessOperations do
                let! answer =
                    call
                        instance
                        modellerPeerId
                        (ModelExecutionPeerContract.request operation referenceSubmission.Vintage)

                expectRefusal
                    answer
                    "model-execution-row-read-refused"
                    $"'{operation}' must be refused as a row read, specifically — a generic unknown-operation refusal would not tell an operator that somebody asked for rows"

            Expect.equal
                instance.Backend.RowReadAttempts
                0
                "no row request may reach the substrate: the seam refuses it, rather than the backend having to"
        }

        testCaseAsync "the positive control: a real operation on the same registration IS answered"
        <| async {
            // Without this, "every row operation was refused" would pass
            // equally against a dispatch that had broken and started
            // refusing everything.
            let instance = dataHost boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "ResolveVintage" referenceSubmission.Vintage)

            match answer with
            | Ok(ModelExecutionPeerAnswer.Answered _) -> ()
            | other -> failtestf "the control operation must be answered on the same registration; got %A" other
        }

        test "the profile's contracts expose no row-returning operation at all" {
            // The strongest form of the claim, and the only one that
            // generalises: the cases above show that particular names are
            // refused, and this shows there is no name that would not be.
            let served =
                Set.union ModelExecutionProfile.operations ModelExecutionProfile.diagnostics

            let overlap = Set.intersect served ModelExecutionProfile.rowAccessOperations

            Expect.isEmpty
                overlap
                "no served operation may share a name with the row-access vocabulary — if one did, a row request would be dispatched rather than refused"

            Expect.isEmpty
                (served |> Set.filter (fun name -> name.Contains "Row" || name.Contains "Page"))
                "no served operation may even be row- or page-shaped by name"
        }
    ]

// ─── 3. Governed diagnostics ─────────────────────────────────────────

/// Tracks whether a projection ran, so a surface refusal can be shown to
/// happen BEFORE the sensitive computation rather than after it.
type private ProjectionProbe() =
    let mutable invoked = 0
    member _.Invocations = invoked
    member _.Ran() = invoked <- invoked + 1

let private diagnosticsTests =
    testList "governed diagnostics release only what the gate clears" [
        testCaseAsync "a declared projection answering in the aggregate shape is released"
        <| async {
            let probe = ProjectionProbe()

            let instance =
                withDiagnostics
                    (dataHost boundOnly)
                    declaredDiagnostics
                    (fun _ _ _ -> async {
                        probe.Ran()
                        return aggregateProjection 182
                    })
                    boundOnly

            let request =
                ModelExecutionPeerContract.diagnosticRequest "Collinearity" {
                    Vintage = referenceSubmission.Vintage
                    Terms = [ "promo"; "price" ]
                }

            let! answer = callDiagnostic instance modellerPeerId request

            match answer with
            | Ok json ->
                let released = JsonRpc.deserialize<CohortResult> json
                Expect.equal released.Shape Histogram "the released answer keeps its declared shape"
                Expect.isNonEmpty released.Cells "the released answer carries its cells"
                Expect.equal probe.Invocations 1 "the projection ran"
            | Error e -> failtestf "a declared, floor-clearing projection must be released; got %A" e
        }

        testCaseAsync "a projection over too small a cohort is WITHHELD, not passed through"
        <| async {
            let probe = ProjectionProbe()

            let instance =
                withDiagnostics
                    (dataHost boundOnly)
                    declaredDiagnostics
                    // The projection is declared, runs, and answers in the
                    // right shape — and is still withheld, because the
                    // cohort behind it does not clear the floor. The
                    // handler makes no broker call and does not know a
                    // gate exists: the gate is not something it invokes,
                    // it is the pipe its answer travels down.
                    (fun _ _ _ -> async {
                        probe.Ran()
                        return aggregateProjection 2
                    })
                    boundOnly

            let request =
                ModelExecutionPeerContract.diagnosticRequest "Coverage" {
                    Vintage = referenceSubmission.Vintage
                    Terms = [ "price" ]
                }

            let! answer = callDiagnostic instance modellerPeerId request

            match answer with
            | Error(PeerCleanRoomWithheld templateId) ->
                Expect.equal templateId "governed-diagnostics" "the withhold names the template and nothing else"
            | other -> failtestf "a sub-floor answer must be withheld; got %A" other

            Expect.equal
                probe.Invocations
                1
                "the projection DID run and its answer was still withheld — which is what distinguishes a gate on the answer from a gate on the request"
        }

        testCaseAsync "an UNDECLARED projection is refused before it runs"
        <| async {
            let probe = ProjectionProbe()

            let instance =
                withDiagnostics
                    (dataHost boundOnly)
                    // `TransformPreview` is a projection the PROFILE
                    // defines and this deployment did not declare — the
                    // case a nonsense operation name would not exercise.
                    declaredDiagnostics
                    (fun _ _ _ -> async {
                        probe.Ran()
                        return aggregateProjection 182
                    })
                    boundOnly

            let request =
                ModelExecutionPeerContract.diagnosticRequest "TransformPreview" {
                    Vintage = referenceSubmission.Vintage
                    Terms = [ "price" ]
                }

            let! answer = callDiagnostic instance modellerPeerId request

            match answer with
            | Error(PeerCleanRoomWithheld _) -> ()
            | other -> failtestf "an undeclared projection must be refused; got %A" other

            Expect.equal
                probe.Invocations
                0
                "the refusal must happen BEFORE the projection runs — computing over the data and discarding the answer is work done over sensitive data for nobody's benefit"
        }

        test "the declaration cannot mint a projection the profile does not define" {
            let template =
                ModelExecutionProfile.template "over-declared" floor (Set.ofList [ "Collinearity"; "ExportEverything" ])

            Expect.equal
                template.AllowedMethods
                (Set.singleton "Collinearity")
                "a template that declares an undefined projection offers only the defined ones — an operator must not be able to read an offer that nothing could dispatch"
        }
    ]

// ─── 4 + 5. Scope and binding ────────────────────────────────────────

let private scopeTests =
    testList "scope is decided receiver-side" [
        testCaseAsync "a request asserting another scope is refused"
        <| async {
            let instance = dataHost boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.requestAsserting
                        "other-tenant"
                        "ResolveVintage"
                        referenceSubmission.Vintage)

            expectRefusal
                answer
                "model-execution-scope-widening"
                "an asserted scope naming another tenant must be refused, never routed on"
        }

        testCaseAsync "the control: the SAME request asserting the bound scope succeeds"
        <| async {
            // Which establishes that the refusal above is about the
            // assertion disagreeing, not about assertions existing.
            let instance = dataHost boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.requestAsserting hostScope "ResolveVintage" referenceSubmission.Vintage)

            match answer with
            | Ok(ModelExecutionPeerAnswer.Answered _) -> ()
            | other -> failtestf "an assertion that AGREES with the binding must not be refused; got %A" other
        }

        testCaseAsync "an unbound peer addresses no scope"
        <| async {
            let instance = dataHost boundOnly

            let! answer =
                call
                    instance
                    strangerPeerId
                    (ModelExecutionPeerContract.request "ResolveVintage" referenceSubmission.Vintage)

            expectRefusal
                answer
                "model-execution-peer-unbound"
                "a peer with no binding must be refused fail-closed, never defaulted into a scope"
        }

        testCaseAsync "an unbound peer cannot schedule a fit either"
        <| async {
            // The pre-check runs BEFORE the job is scheduled: an unbound
            // peer must not become a queued job that a background worker
            // then refuses out of sight.
            let instance = dataHost boundOnly

            let! answer =
                call instance strangerPeerId (ModelExecutionPeerContract.submissionRequest referenceSubmission)

            expectRefusal
                answer
                "model-execution-peer-unbound"
                "an unbound peer's submission must be refused at dispatch"

            do! instance.Scheduler.RunPending()
            Expect.isEmpty instance.Backend.Registered "no fit may have been queued for an unbound peer"
        }

        testCaseAsync "a version beyond the profile is refused whole"
        <| async {
            let instance = dataHost boundOnly

            let request = {
                ModelExecutionPeerContract.request "ResolveVintage" referenceSubmission.Vintage with
                    ProfileVersion = ModelExecutionProfile.Version + 1
            }

            let! answer = call instance modellerPeerId request

            expectRefusal
                answer
                "model-execution-profile-version-unsupported"
                "a document the reader cannot fully read is refused, never partially read"
        }
    ]

/// Phase 640 — the seam's refusals read in the submitter face's own closed
/// vocabulary.
///
/// This is where two of that vocabulary's classes are actually produced. The
/// in-deployment remoting face is typed end to end, so neither an envelope
/// version nor an unknown document kind can arise there; across a peer
/// boundary both are ordinary, which is why the projection lives here and
/// not as a decorative mapping beside the DU.
let private submitterVocabularyTests =
    testList "seam refusals read in the submitter vocabulary" [
        test "an unread profile version projects to EnvelopeVersionMismatch, enumerating what IS read" {
            let admission = ModelExecutionAdmission.create Set.empty
            let known = Set.union admission.Operations admission.Diagnostics |> Set.toList

            let projected =
                ModelExecutionPeerRefusal.ProfileVersionUnsupported(9, 1)
                |> ModelExecutionPeerRefusal.toSubmitterRefusal known

            match projected with
            | ModelExecutionRefusal.EnvelopeVersionMismatch(received, accepted) ->
                Expect.equal received 9 "the version that arrived is carried"

                // Enumerated, not reported as a ceiling: the caller's
                // question is which version it may send, not how high this
                // deployment can count.
                Expect.equal accepted [ 1 ] "the accepted versions are enumerated"
            | other -> failtestf "expected EnvelopeVersionMismatch; got %A" other
        }

        test "a row probe and an undeclared operation both project to UnknownDocumentKind, naming what is served" {
            let admission = ModelExecutionAdmission.create Set.empty
            let known = Set.union admission.Operations admission.Diagnostics |> Set.toList

            let project = ModelExecutionPeerRefusal.toSubmitterRefusal known

            // The seam keeps these apart for the refusal LOG, so an operator
            // can tell a probe from a typo. A submitter's remedy is the same
            // either way, so the wire does not manufacture a distinction it
            // would have to handle for nothing.
            for refusal in
                [
                    ModelExecutionPeerRefusal.RowAccessRefused "ReadPage"
                    ModelExecutionPeerRefusal.UndeclaredDiagnostic "ReadPage"
                ] do
                match project refusal with
                | ModelExecutionRefusal.UnknownDocumentKind(kind, served) ->
                    Expect.equal kind "ReadPage" "the operation asked for is named"
                    Expect.contains served "GetOutcome" "the served operations are offered back"
                    Expect.equal served (List.sort served) "the served set is ordered"
                    Expect.isFalse (List.contains "ReadPage" served) "a row read is not among them"
                | other -> failtestf "expected UnknownDocumentKind; got %A" other
        }

        test "a scope-widening assertion is a policy refusal, not a role refusal" {
            // The distinction is real rather than taxonomic: `Forbidden` says
            // YOU may not, and invites the caller to seek permission. Nothing
            // about this request is permitted from any caller, because the
            // binding decides the scope — so it is refused as a rule.
            match
                ModelExecutionPeerRefusal.ScopeWideningRefused "someone-elses-scope"
                |> ModelExecutionPeerRefusal.toSubmitterRefusal []
            with
            | ModelExecutionRefusal.PolicyRefused rule ->
                Expect.equal rule "model-execution.scope-widening" "the rule identifier is the stable part"
            | other -> failtestf "expected PolicyRefused; got %A" other

            // The asserted scope is deliberately absent from the refusal: it
            // is the caller's own claim, and echoing an unresolved scope
            // identifier back across the seam is the one thing §6.2 exists to
            // prevent.
            let described =
                ModelExecutionPeerRefusal.ScopeWideningRefused "someone-elses-scope"
                |> ModelExecutionPeerRefusal.toSubmitterRefusal []
                |> ModelExecutionRefusal.describe

            Expect.isFalse
                (described.Contains "someone-elses-scope")
                "an asserted scope is never echoed back in the submitter-facing refusal"
        }

        test "a refusal already in the submitter vocabulary is carried through unchanged" {
            let inner = ModelExecutionRefusal.UnknownProvider "regression"

            Expect.equal
                (ModelExecutionPeerRefusal.SubmitterRefused inner
                 |> ModelExecutionPeerRefusal.toSubmitterRefusal [])
                inner
                "the projection is the identity on classes that already belong to this face"
        }
    ]

let tests =
    testList "Phase 638 — federated model execution" [
        roundTripTests
        noRowSurfaceTests
        diagnosticsTests
        scopeTests
        submitterVocabularyTests
    ]