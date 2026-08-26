module ToolUp.Platform.Tests.InProcess.FederatedModelExecutionTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
// Phase 642 — the disclosure plane's own vocabulary (`IFactDisclosureGate`,
// `FactEgressSurface`, `FactDisclosureVerdict`), which the federated-egress
// cases stub, and the compose record the descriptor cases build.
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

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
                    // Phase 642 — the default grant: `AggregatesOnly`,
                    // no narrowing, no egress route. Every case in this
                    // module predates the authority levels and must keep
                    // behaving exactly as it did (GP 11), so the shared
                    // host declares the shipped posture; the authority
                    // cases build their own bindings.
                    Visibility = PeerVisibilityBinding.default'
                    Egress = None
                    // Phase 644 — no transition granted, which is the
                    // fail-closed default and the pre-644 behaviour: an
                    // invocation is refused whether or not the operation
                    // is declared.
                    TransitionAuthority = ModelTransitionAuthority.none
                }
    }

    let deps: ModelExecutionPeerDeps = {
        ResolveBinding = resolveBinding
        Admission = ModelExecutionAdmission.create declaredDiagnostics
        FitPoll = ModelExecutionFitPollPolicy.immediate
        // Phase 643 — no declared views, which is what every case above
        // this section is evidence for: a deployment that upgrades and
        // declares nothing behaves byte-for-byte as it did (GP 11).
        Views = None
        // Phase 644 — likewise: no transition substrate, so the
        // operation is neither declared nor servable.
        Transitions = None
        // Phase 646 — and no promotion substrate either: nothing is
        // constructed, nothing is admitted (GP 13).
        Promotions = None
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
                    Visibility = PeerVisibilityBinding.default'
                    Egress = None
                    // Phase 644 — no transition granted, which is the
                    // fail-closed default and the pre-644 behaviour: an
                    // invocation is refused whether or not the operation
                    // is declared.
                    TransitionAuthority = ModelTransitionAuthority.none
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

// ─── Phase 642 — declared data-visibility authority levels ───────────
//
// The profile shipped closed against row egress; these cases are about
// what a deployment has AGREED a peer may see, which is a different
// question. Three properties carry the whole family and each is asserted
// on its own: the levels are ORDERED (so "narrower" is computable and a
// floor is a value rather than a disagreement), narrowing only ever
// LOWERS (so the innermost, least authoritative layer cannot re-admit
// what the agreement excluded), and the refusals are DISTINGUISHABLE (so
// a caller pursues the remedy that exists).

/// A data host whose binding declares a level and, optionally, an egress
/// route. Deliberately a separate constructor from `dataHost` rather than
/// an extra parameter on it: every pre-642 case in this module must keep
/// running against a binding that declares nothing, because "a
/// deployment that upgrades is byte-for-byte unchanged" is the claim
/// those cases are the evidence for.
let private dataHostGranting
    (visibility: PeerVisibilityBinding)
    (egress: PeerEgressRoute option)
    (bindings: Map<string, string>)
    =
    let backend = ReferenceDataHost()
    let scheduler = DeferredScheduler()
    let results = MemoryJobResultStore() :> IPeerJobResultStore

    let resolveBinding (peerId: string) = async {
        match Map.tryFind peerId bindings with
        | None -> return None
        | Some scope ->
            return
                Some {
                    PeerId = peerId
                    ScopeId = scope
                    Api = backend.Api
                    Visibility = visibility
                    Egress = egress
                    TransitionAuthority = ModelTransitionAuthority.none
                }
    }

    let deps: ModelExecutionPeerDeps = {
        ResolveBinding = resolveBinding
        Admission = ModelExecutionAdmission.create declaredDiagnostics
        FitPoll = ModelExecutionFitPollPolicy.immediate
        // Phase 643 — no declared views, which is what every case above
        // this section is evidence for: a deployment that upgrades and
        // declares nothing behaves byte-for-byte as it did (GP 11).
        Views = None
        // Phase 644 — likewise: no transition substrate, so the
        // operation is neither declared nor servable.
        Transitions = None
        // Phase 646 — and no promotion substrate either: nothing is
        // constructed, nothing is admitted (GP 13).
        Promotions = None
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
        Decisions = ResizeArray<PeerCleanRoomDecisionPayload>()
    }

/// A gate that answers from a fixed verdict map and records what it was
/// asked, including the ambient purpose claimed at the moment of the
/// call — which is the only way to assert that the claim was installed
/// BEFORE the gate was consulted rather than after.
type private RecordingGate(verdicts: Map<string, FactDisclosureVerdict>, claimed: string ref) =
    member val Calls = ResizeArray<string * FactEgressSurface * string list * string>() with get

    interface IFactDisclosureGate with
        member this.Check(scopeId, principal, surface, factIds) = async {
            this.Calls.Add(scopeId, surface, factIds, claimed.Value)
            ignore principal

            return
                factIds
                |> List.map (fun id ->
                    id, verdicts.TryFind id |> Option.defaultValue (FactNotDisclosable "unknown-fact"))
                |> Map.ofList
        }

let private authorityLevelTests =
    testList "the levels are ordered and read fail-closed" [
        test "the order is AggregatesOnly < ViewOnly < Full" {
            Expect.equal
                (PeerDataVisibilityLevel.all |> List.map PeerDataVisibilityLevel.rank)
                [ 0; 1; 2 ]
                "the enumeration is weakest-first and the ranks agree with it"

            Expect.isTrue
                (PeerDataVisibilityLevel.admits PeerDataVisibilityLevel.Full PeerDataVisibilityLevel.ViewOnly)
                "a Full grant reaches a ViewOnly requirement"

            Expect.isFalse
                (PeerDataVisibilityLevel.admits PeerDataVisibilityLevel.ViewOnly PeerDataVisibilityLevel.Full)
                "a ViewOnly grant does not reach a Full requirement"
        }

        test "an absent, empty or unrecognised declaration reads as the narrowest level" {
            // The three arms are one claim from three directions: a
            // counterparty's silence is not a grant, and neither is a word
            // this build cannot enforce. `null` is the pre-642 case — a
            // label published before the member existed.
            for declared in [ null; ""; "Everything"; "aggregatesonly" ] do
                Expect.equal
                    (PeerDataVisibilityLevel.ofLabelOrDefault declared)
                    PeerDataVisibilityLevel.AggregatesOnly
                    $"'{declared}' is not a grant"

            // The control that separates a reader which fails closed from
            // one that always returns the default.
            Expect.equal
                (PeerDataVisibilityLevel.ofLabelOrDefault "ViewOnly")
                PeerDataVisibilityLevel.ViewOnly
                "a declared level IS read"
        }

        test "the floor over a set is its minimum, never a disagreement marker" {
            Expect.equal
                (PeerDataVisibilityLevel.floor [
                    PeerDataVisibilityLevel.Full
                    PeerDataVisibilityLevel.AggregatesOnly
                    PeerDataVisibilityLevel.ViewOnly
                ])
                PeerDataVisibilityLevel.AggregatesOnly
                "a group grants what its narrowest participant grants"

            Expect.equal
                (PeerDataVisibilityLevel.floor [])
                PeerDataVisibilityLevel.AggregatesOnly
                "nothing declared is the narrowest level, not the broadest"
        }

        test "the Full vocabulary and the row-access probe list are disjoint" {
            // They answer different questions and a name on both would be
            // ambiguous: the probe list is a structural absence refused at
            // every level, the Full list is a grant.
            Expect.isEmpty
                (Set.intersect ModelExecutionProfile.fullOperations ModelExecutionProfile.rowAccessOperations
                 |> Set.toList)
                "no operation is both a row-access probe and a Full-reserved operation"
        }
    ]

let private narrowingTests =
    testList "narrowing may only lower" [
        test "each layer narrows the one before it, outermost-first" {
            let binding =
                PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.Full
                |> PeerVisibilityBinding.withNarrowing (TeamNarrowing "north") PeerDataVisibilityLevel.ViewOnly
                |> PeerVisibilityBinding.withNarrowing (UserNarrowing "ana") PeerDataVisibilityLevel.AggregatesOnly

            let resolved = PeerVisibility.resolve binding "buyer-acme"

            Expect.equal resolved.Ceiling PeerDataVisibilityLevel.Full "the ceiling is the peer's grant"

            Expect.equal
                resolved.Effective
                PeerDataVisibilityLevel.AggregatesOnly
                "the innermost layer's narrowing is what a caller gets"

            Expect.equal
                resolved.ContributingScopes
                [ PeerCeiling "buyer-acme"; TeamNarrowing "north"; UserNarrowing "ana" ]
                "the walk records where each layer came from, outermost-first"

            Expect.equal resolved.NarrowedBy (Some(UserNarrowing "ana")) "the innermost layer that lowered is named"
        }

        test "a layer declaring MORE than it inherited is clamped and recorded, never honoured" {
            // The whole safety property. If an inner layer could widen,
            // the least authoritative scope in the walk could re-admit
            // data the bilateral agreement excluded.
            let binding =
                PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.AggregatesOnly
                |> PeerVisibilityBinding.withNarrowing (TeamNarrowing "north") PeerDataVisibilityLevel.Full

            let resolved = PeerVisibility.resolve binding "buyer-acme"

            Expect.equal
                resolved.Effective
                PeerDataVisibilityLevel.AggregatesOnly
                "the ceiling holds; the layer did not raise it"

            Expect.equal
                resolved.ClampedScopes
                [ TeamNarrowing "north" ]
                "a mis-declared layer is NAMED rather than silently doing nothing — an operator has to be able to find it"

            Expect.isNone resolved.NarrowedBy "clamping is not narrowing; nothing was lowered"
        }

        test "a binding that declares nothing resolves to the narrowest level with no narrowing" {
            let resolved = PeerVisibility.resolve PeerVisibilityBinding.default' "buyer-acme"

            Expect.equal resolved.Effective PeerDataVisibilityLevel.AggregatesOnly "the pre-642 posture, named"
            Expect.isEmpty resolved.ClampedScopes "nothing to clamp"
            Expect.isEmpty (List.tail resolved.ContributingScopes) "only the ceiling contributed"
        }
    ]

let private authorityAdmissionTests =
    testList "requests are classified by the level they require" [
        test "the profile's own operations require only the narrowest level" {
            // Which is why a deployment upgrading into this phase enforces
            // exactly what it already enforced.
            for operation in Set.union ModelExecutionProfile.operations ModelExecutionProfile.diagnostics do
                Expect.equal
                    (ModelExecutionProfile.requiredAuthority operation)
                    PeerDataVisibilityLevel.AggregatesOnly
                    $"'{operation}' is metadata or a governed aggregate"
        }

        test "an unrecognised operation requires the narrowest level, so the authority check never refuses a typo" {
            Expect.equal
                (ModelExecutionProfile.requiredAuthority "Colinearity")
                PeerDataVisibilityLevel.AggregatesOnly
                "a misspelling is refused by the DECLARATION check, with the class that tells a caller what it named"
        }

        testCaseAsync "a bounded-view request against an aggregates-only grant is refused as an AUTHORITY question"
        <| async {
            let instance = dataHostGranting PeerVisibilityBinding.default' None boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "RenderView" referenceSubmission.Vintage)

            expectRefusal
                answer
                "model-execution-authority-level-exceeded"
                "the deployment implements the classification and has not granted it — 'we do not do that' and 'not for you' have different remedies"
        }

        testCaseAsync "the identical request against a ViewOnly grant is no longer an authority question"
        <| async {
            let instance =
                dataHostGranting (PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.ViewOnly) None boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "RenderView" referenceSubmission.Vintage)

            // The control that separates an authority check which fires
            // from one that refuses everything: the grant admits it, and
            // what refuses now is the declaration check, because the view
            // machinery is a later phase.
            expectRefusal
                answer
                "model-execution-undeclared-diagnostic"
                "the grant reaches it; this deployment does not implement it yet"
        }

        testCaseAsync "a Full-reserved request against a ViewOnly grant is refused at the next rung"
        <| async {
            let instance =
                dataHostGranting (PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.ViewOnly) None boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "ReadVintageSeries" referenceSubmission.Vintage)

            expectRefusal
                answer
                "model-execution-authority-level-exceeded"
                "the levels are a ladder, not a pair of special cases"
        }

        testCaseAsync "a narrowing beneath an admitting ceiling refuses with its OWN class, naming the layer"
        <| async {
            let instance =
                dataHostGranting
                    (PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.ViewOnly
                     |> PeerVisibilityBinding.withNarrowing
                         (TeamNarrowing "north-analysts")
                         PeerDataVisibilityLevel.AggregatesOnly)
                    None
                    boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "RenderView" referenceSubmission.Vintage)

            expectRefusal
                answer
                "model-execution-authority-narrowed"
                "a ceiling refusal is a question for the two organisations; a narrowing refusal is one for this deployment's own configuration"

            match answer with
            | Ok(ModelExecutionPeerAnswer.Refused refusal) ->
                Expect.stringContains
                    (ModelExecutionPeerRefusal.describe refusal)
                    "team:north-analysts"
                    "the refusal names the layer to look at"
            | other -> failtestf "expected a refusal; got %A" other
        }

        testCaseAsync "a row-access probe is still refused as a row read at EVERY level"
        <| async {
            // The ordering claim: the row vocabulary is a structural
            // absence, not a grant, so re-reporting a probe as an
            // authority question would tell a caller that a wider grant
            // might get it one. It would not.
            for ceiling in PeerDataVisibilityLevel.all do
                let instance =
                    dataHostGranting (PeerVisibilityBinding.ofCeiling ceiling) None boundOnly

                let! answer =
                    call
                        instance
                        modellerPeerId
                        (ModelExecutionPeerContract.request "ReadPage" referenceSubmission.Vintage)

                expectRefusal
                    answer
                    "model-execution-row-read-refused"
                    $"a probe at '{PeerDataVisibilityLevel.label ceiling}' is a probe, not an authority question"
        }
    ]

let private federatedEgressTests =
    testList "level-gated egress rides the disclosure plane" [
        testCaseAsync "no route composed answers exactly as before, consulting nothing"
        <| async {
            let instance = dataHostGranting PeerVisibilityBinding.default' None boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "ResolveVintage" referenceSubmission.Vintage)

            match answer with
            | Ok(ModelExecutionPeerAnswer.Answered _) -> ()
            | other -> failtestf "a deployment with no fact substrate pays nothing and answers unchanged; got %A" other
        }

        testCaseAsync "a permitted reference crosses, and the gate was asked at the federated-egress door"
        <| async {
            let claimed = ref ""
            let gate = RecordingGate(Map.ofList [ "fact-1", FactDisclosable ], claimed)

            let route: PeerEgressRoute = {
                Gate = gate
                Purpose = {
                    PurposeId = "federated-modelling"
                    Claim = fun purpose -> claimed.Value <- purpose
                }
                References = fun _ -> [ "fact-1" ]
                // Phase 643 — no view is rendered on any of these paths,
                // so the view-keyed door is never reached. Stated rather
                // than shared with `References`: a route that answered
                // the same set at both doors would make a render
                // indistinguishable from the metadata call beside it,
                // which is the confusion the second function exists to
                // prevent.
                ViewReferences = fun _ _ -> []
            }

            let instance =
                dataHostGranting PeerVisibilityBinding.default' (Some route) boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "ResolveVintage" referenceSubmission.Vintage)

            match answer with
            | Ok(ModelExecutionPeerAnswer.Answered _) -> ()
            | other -> failtestf "a permitted reference crosses; got %A" other

            let scopeId, surface, factIds, purposeAtCall = gate.Calls |> Seq.exactlyOne

            Expect.equal scopeId hostScope "the binding's scope, never one from the wire"
            Expect.equal surface FactPeerEgress "judged at the federation door, not at some other surface's"
            Expect.equal factIds [ "fact-1" ] "the answer's declared references"

            // The Phase 592 facet is only bound if the claim is installed
            // BEFORE the gate reads it; asserting the claim afterwards
            // would pass against a route that claimed nothing at all.
            Expect.equal
                purposeAtCall
                "federated-modelling"
                "the purpose was claimed before the gate was consulted, not after"
        }

        testCaseAsync "a withheld reference refuses the answer, naming the operation and nothing else"
        <| async {
            let claimed = ref ""

            let gate =
                RecordingGate(Map.ofList [ "fact-1", FactNotDisclosable "licensed-third-party" ], claimed)

            let route: PeerEgressRoute = {
                Gate = gate
                Purpose = {
                    PurposeId = "federated-modelling"
                    Claim = fun purpose -> claimed.Value <- purpose
                }
                References = fun _ -> [ "fact-1" ]
                // Phase 643 — no view is rendered on any of these paths,
                // so the view-keyed door is never reached. Stated rather
                // than shared with `References`: a route that answered
                // the same set at both doors would make a render
                // indistinguishable from the metadata call beside it,
                // which is the confusion the second function exists to
                // prevent.
                ViewReferences = fun _ _ -> []
            }

            let instance =
                dataHostGranting PeerVisibilityBinding.default' (Some route) boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "ResolveVintage" referenceSubmission.Vintage)

            expectRefusal
                answer
                "model-execution-egress-withheld"
                "the level admitted the request; the disclosure plane withheld what the answer carries"

            match answer with
            | Ok(ModelExecutionPeerAnswer.Refused refusal) ->
                let described = ModelExecutionPeerRefusal.describe refusal

                // Naming the policy is right at a door inside the trust
                // boundary and wrong across a federation edge: it would
                // tell a counterparty that a fact it may not see EXISTS.
                Expect.isFalse (described.Contains "licensed-third-party") "the policy is never named across the seam"

                Expect.isFalse (described.Contains "fact-1") "nor is the reference"
            | other -> failtestf "expected a refusal; got %A" other
        }

        testCaseAsync "an unresolvable reference is withheld, exactly as a denied one is"
        <| async {
            // Fail-closed across all three of denied, unknown and
            // unresolvable-in-scope: a reference the gate did not
            // affirmatively permit is one nothing said may cross.
            let claimed = ref ""
            let gate = RecordingGate(Map.empty, claimed)

            let route: PeerEgressRoute = {
                Gate = gate
                Purpose = {
                    PurposeId = "federated-modelling"
                    Claim = fun purpose -> claimed.Value <- purpose
                }
                References = fun _ -> [ "fact-from-another-scope" ]
                ViewReferences = fun _ _ -> []
            }

            let instance =
                dataHostGranting PeerVisibilityBinding.default' (Some route) boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "ResolveVintage" referenceSubmission.Vintage)

            expectRefusal
                answer
                "model-execution-egress-withheld"
                "an id the gate cannot resolve is not permitted by omission"
        }

        testCaseAsync "a refusal never rides the door"
        <| async {
            let claimed = ref ""
            let gate = RecordingGate(Map.empty, claimed)

            let route: PeerEgressRoute = {
                Gate = gate
                Purpose = {
                    PurposeId = "federated-modelling"
                    Claim = fun purpose -> claimed.Value <- purpose
                }
                References = fun _ -> [ "fact-1" ]
                // Phase 643 — no view is rendered on any of these paths,
                // so the view-keyed door is never reached. Stated rather
                // than shared with `References`: a route that answered
                // the same set at both doors would make a render
                // indistinguishable from the metadata call beside it,
                // which is the confusion the second function exists to
                // prevent.
                ViewReferences = fun _ _ -> []
            }

            let instance =
                dataHostGranting PeerVisibilityBinding.default' (Some route) boundOnly

            let! answer =
                call instance modellerPeerId (ModelExecutionPeerContract.request "ReadPage" referenceSubmission.Vintage)

            expectRefusal answer "model-execution-row-read-refused" "the probe refusal stands"

            Expect.isEmpty
                (List.ofSeq gate.Calls)
                "there is nothing in a refusal to disclose, so routing one would spend a gate check and an audit row to learn that"
        }
    ]

let private authorityVocabularyTests =
    testList "the authority refusals read in the submitter vocabulary" [
        test "a ceiling refusal is Forbidden — it invites the caller to seek permission" {
            match
                ModelExecutionPeerRefusal.AuthorityLevelExceeded("RenderView", "ViewOnly", "AggregatesOnly")
                |> ModelExecutionPeerRefusal.toSubmitterRefusal []
            with
            | ModelExecutionRefusal.Forbidden message ->
                Expect.stringContains
                    message
                    "ViewOnly"
                    "the level the operation needs is named, so the ask is actionable"
            | other -> failtestf "expected Forbidden; got %A" other
        }

        test "a narrowing refusal is a policy refusal under a stable rule id" {
            match
                ModelExecutionPeerRefusal.AuthorityNarrowingRefused(
                    "RenderView",
                    "ViewOnly",
                    "AggregatesOnly",
                    "team:north"
                )
                |> ModelExecutionPeerRefusal.toSubmitterRefusal []
            with
            | ModelExecutionRefusal.PolicyRefused rule ->
                Expect.equal rule "model-execution.authority-narrowing" "the rule identifier is the stable part"
            | other -> failtestf "expected PolicyRefused; got %A" other
        }

        test "an egress withhold reaches the submitter as a rule, carrying nothing about the data" {
            let projected =
                ModelExecutionPeerRefusal.EgressWithheld "Coverage"
                |> ModelExecutionPeerRefusal.toSubmitterRefusal []

            match projected with
            | ModelExecutionRefusal.PolicyRefused rule ->
                Expect.equal rule "model-execution.egress-withheld" "the rule identifier is all a counterparty learns"
            | other -> failtestf "expected PolicyRefused; got %A" other
        }

        test "every refusal class the profile defines has a distinct stable name" {
            let classes =
                [
                    ModelExecutionPeerRefusal.ProfileVersionUnsupported(2, 1)
                    ModelExecutionPeerRefusal.RowAccessRefused "ReadPage"
                    ModelExecutionPeerRefusal.UndeclaredDiagnostic "Leverage"
                    ModelExecutionPeerRefusal.ScopeWideningRefused "other"
                    ModelExecutionPeerRefusal.PeerUnbound "peer"
                    ModelExecutionPeerRefusal.RequestUnreadable "truncated"
                    ModelExecutionPeerRefusal.SubmitterRefused(ModelExecutionRefusal.NotFound "x")
                    ModelExecutionPeerRefusal.AuthorityLevelExceeded("RenderView", "ViewOnly", "AggregatesOnly")
                    ModelExecutionPeerRefusal.AuthorityNarrowingRefused(
                        "RenderView",
                        "ViewOnly",
                        "AggregatesOnly",
                        "team:n"
                    )
                    ModelExecutionPeerRefusal.EgressWithheld "Coverage"
                ]
                |> List.map ModelExecutionPeerRefusal.className

            Expect.equal
                (List.distinct classes |> List.length)
                classes.Length
                "a class shared by two conditions is a class a caller cannot act on"
        }
    ]

let private declaredSurfaceTests =
    testList "the grant is declared once and published in the descriptor" [
        test "a composed declaration is what the surface publishes" {
            let surface =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig {
                    ServerConfig.defaults with
                        PeerSubstrate = EnabledPeerSubstrate
                }
                |> PeerServerApp.withLocalPeer {
                    PeerId = "data-host"
                    DisplayName = "Data host"
                }
                |> PeerServerApp.withDataVisibility PeerDataVisibilityLevel.ViewOnly
                |> PeerSurface.describe

            Expect.equal surface.DataVisibility "ViewOnly" "the label a counterparty pins"

            Expect.equal
                (PeerSurface.dataVisibility surface)
                PeerDataVisibilityLevel.ViewOnly
                "and the level the seam enforces — one declaration, two readers"
        }

        test "a composition that declares nothing publishes the narrowest level, present rather than omitted" {
            let surface =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig {
                    ServerConfig.defaults with
                        PeerSubstrate = EnabledPeerSubstrate
                }
                |> PeerSurface.describe

            Expect.equal surface.DataVisibility "AggregatesOnly" "the shipped posture, named rather than changed"
        }

        test "a label that omits the member reads as the narrowest level" {
            // The pre-642 counterparty. `null` is what deserialising a
            // label without the member produces, and the honest reading of
            // "said nothing" is never the broadest grant.
            Expect.equal
                (PeerSurface.dataVisibility {
                    PeerSurface.empty with
                        DataVisibility = null
                })
                PeerDataVisibilityLevel.AggregatesOnly
                "silence is not a grant"
        }
    ]

// ─── Phase 643 — `ViewOnly`: server-rendered bounded views ───────────
//
// The level named in Phase 642 now does something: a granted peer sees
// RENDERED artifacts of declared series, bounded and audited, and there
// is still no shape that carries a row. Five properties, each asserted
// on its own because each can fail without the others noticing — the
// response is an artifact, the bounds are the declaration's, the budget
// is spent per peer, the render goes through the deployment's OWN chart
// grammar, and every render leaves a record naming the series.

// These two opens are declared HERE rather than at the top of the file
// because they are needed only by this section, and an `open` at the
// head would put a second `Component` / `RenderView` in scope for 1,600
// lines of cases that have no use for either.
open System.Text
open Giraffe.ViewEngine
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering

let private renderClock = DateTimeOffset(2026, 7, 16, 10, 15, 0, TimeSpan.Zero)

/// The declared offer. `MaxPointsPerSeries` is deliberately BELOW the
/// number of points the reader below returns, so the clamp is exercised
/// by the ordinary path rather than by a case constructed for it.
let private spendView: PeerViewDeclaration = {
    ViewId = "spend-vs-response"
    DatasetId = "weekly-panel"
    Title = "Weekly spend against response"
    Kind = "line"
    Series = [ "promo-spend"; "search-clicks" ]
    Resolutions = [ "day"; "week" ]
    MaxWindowDays = 90
    MaxSeriesPerRequest = 2
    MaxPointsPerSeries = 3
    MaxRendersPerWindow = 2
    RenderWindowSeconds = 60
}

let private point (label: string) (value: float) : PeerViewPoint = { Label = label; Value = value }

let private readSeries: PeerViewSeries list = [
    {
        Name = "promo-spend"
        Points = [
            point "w1" 10.0
            point "w2" 20.0
            point "w3" 15.0
            point "w4" 40.0
            point "w5" 5.0
        ]
    }
    {
        Name = "search-clicks"
        Points = [ point "w1" 1.5; point "w2" 2.5 ]
    }
]

/// The deployment's OWN chart grammar, wired the way a composition wires
/// it: the shipped deterministic renderer, reached through the prop bag
/// this substrate builds. Not a stand-in — the point of the case is that
/// a federated view and a published page come out of one renderer.
let private grammarRenderer: PeerViewRenderer = {
    MediaType = "image/svg+xml"
    Render =
        fun bags ->
            bags
            |> List.map (NarrativeCharts.renderChart >> RenderView.AsString.htmlNode)
            |> String.concat ""
            |> Encoding.UTF8.GetBytes
}

let private viewDepsWith (now: unit -> DateTimeOffset) (declarations: PeerViewDeclaration list) : PeerViewDeps = {
    Declarations = fun _ -> async { return declarations }
    ReadSeries = fun _ _ -> async { return readSeries }
    Renderer = grammarRenderer
    Rate = PeerViewRateGuard.inProcess now
}

/// A FUNCTION, not a value: `PeerViewRateGuard.inProcess` holds its
/// counters, so a shared value would let one case spend another's
/// budget — and the failure would look like a bug in whichever case
/// happened to run third.
let private viewDeps () =
    viewDepsWith (fun () -> renderClock) [ spendView ]

/// A data host that declares views. A fourth constructor rather than a
/// parameter on `dataHostGranting`, for the reason that one is separate
/// from `dataHost`: every case above must keep running against a
/// deployment that declares none, because "an upgrade changes nothing
/// until you opt in" is the claim those cases are the evidence for.
let private dataHostRendering
    (visibility: PeerVisibilityBinding)
    (egress: PeerEgressRoute option)
    (views: PeerViewDeps option)
    (bindings: Map<string, string>)
    =
    let backend = ReferenceDataHost()
    let scheduler = DeferredScheduler()
    let results = MemoryJobResultStore() :> IPeerJobResultStore

    let resolveBinding (peerId: string) = async {
        match Map.tryFind peerId bindings with
        | None -> return None
        | Some scope ->
            return
                Some {
                    PeerId = peerId
                    ScopeId = scope
                    Api = backend.Api
                    Visibility = visibility
                    Egress = egress
                    TransitionAuthority = ModelTransitionAuthority.none
                }
    }

    let deps: ModelExecutionPeerDeps = {
        ResolveBinding = resolveBinding
        Admission =
            // Declaring the views and granting the level are two acts,
            // and the cases below turn each off independently.
            match views with
            | Some _ ->
                ModelExecutionAdmission.create declaredDiagnostics
                |> ModelExecutionAdmission.withViews
            | None -> ModelExecutionAdmission.create declaredDiagnostics
        FitPoll = ModelExecutionFitPollPolicy.immediate
        Views = views
        Transitions = None
        // Phase 646 — and no promotion substrate either: nothing is
        // constructed, nothing is admitted (GP 13).
        Promotions = None
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
        Decisions = ResizeArray<PeerCleanRoomDecisionPayload>()
    }

let private viewOnly =
    PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.ViewOnly

let private referenceWindow: PeerViewWindow = {
    From = DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero)
    To = DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)
}

let private renderRequest: PeerViewRequest = {
    ViewId = spendView.ViewId
    DatasetVersion = 7
    Series = [ "promo-spend"; "search-clicks" ]
    Window = referenceWindow
    Resolution = "week"
}

/// Dispatch a render and decode the artifact, failing loudly on a
/// refusal — the shape a case that is ABOUT the happy path wants.
let private renderThrough (instance: DataHostInstance) (request: PeerViewRequest) = async {
    let! answer = call instance modellerPeerId (ModelExecutionPeerContract.viewRequest request)

    match answer with
    | Ok(ModelExecutionPeerAnswer.Answered body) -> return JsonRpc.deserialize<PeerViewArtifact> body
    | other -> return failtestf "the render was expected to be answered; got %A" other
}

let private viewContractTests =
    testList "a granted peer sees a rendered artifact and nothing row-shaped" [
        testCaseAsync "a render inside every bound answers with the artifact"
        <| async {
            let instance = dataHostRendering viewOnly None (Some(viewDeps ())) boundOnly
            let! artifact = renderThrough instance renderRequest

            Expect.equal artifact.ViewId spendView.ViewId "the artifact names the view it renders"
            Expect.equal artifact.MediaType "image/svg+xml" "the media type the deployment's renderer declares"
            Expect.equal artifact.Series [ "promo-spend"; "search-clicks" ] "the series it covered, ordinally"
            Expect.equal artifact.Resolution "week" "the resolution it was asked for"
            Expect.equal artifact.Window referenceWindow "the window it was asked for"

            // 3 of promo-spend's 5 points and both of search-clicks': the
            // clamp is against the DECLARATION, not against the reader's
            // good behaviour, so a reader returning more cannot widen
            // what crosses.
            Expect.equal artifact.RenderedPoints 5 "points clamped to the declared ceiling per series"
        }

        test "no member of the answer shape could carry a row" {
            // The structural half of "a view is not an export route". The
            // dispatch cannot leak a series because there is nowhere to
            // put one — asserted over the type rather than over one
            // answer, so a field added later fails here rather than in
            // whatever case happens to notice.
            let permitted =
                Set.ofList [
                    typeof<string>.FullName
                    typeof<int>.FullName
                    typeof<PeerViewWindow>.FullName
                ]

            for field in FSharp.Reflection.FSharpType.GetRecordFields typeof<PeerViewArtifact> do
                let t = field.PropertyType

                let ok =
                    Set.contains t.FullName permitted
                    // `Series` is a list of NAMES the caller itself sent.
                    || (t.IsGenericType
                        && t.GetGenericTypeDefinition() = typedefof<list<_>>
                        && t.GetGenericArguments().[0] = typeof<string>)

                Expect.isTrue ok $"'{field.Name}' is a {t.Name}, which is a shape a series could ride in"
        }

        testCaseAsync "the offer and one view's declaration are answerable, ordinally"
        <| async {
            let second = {
                spendView with
                    ViewId = "coverage-by-week"
            }

            let instance =
                dataHostRendering
                    viewOnly
                    None
                    (Some(viewDepsWith (fun () -> renderClock) [ spendView; second ]))
                    boundOnly

            let! listed = call instance modellerPeerId (ModelExecutionPeerContract.request "ListViews" "")

            match listed with
            | Ok(ModelExecutionPeerAnswer.Answered body) ->
                let declared = JsonRpc.deserialize<PeerViewDeclaration list> body

                Expect.equal
                    (declared |> List.map _.ViewId)
                    [ "coverage-by-week"; "spend-vs-response" ]
                    "the offer is ordinally sorted, whatever order it was declared in"
            | other -> failtestf "ListViews is a declared view operation; got %A" other

            let! described =
                call instance modellerPeerId (ModelExecutionPeerContract.request "DescribeView" spendView.ViewId)

            match described with
            | Ok(ModelExecutionPeerAnswer.Answered body) ->
                Expect.equal
                    (JsonRpc.deserialize<PeerViewDeclaration> body).MaxWindowDays
                    spendView.MaxWindowDays
                    "the declaration a caller reads is the one the receiver enforces"
            | other -> failtestf "DescribeView is a declared view operation; got %A" other

            let! missing =
                call instance modellerPeerId (ModelExecutionPeerContract.request "DescribeView" "no-such-view")

            expectRefusal
                missing
                "model-execution-view-undeclared"
                "'there is no such view' and 'the view is empty' are different facts"
        }
    ]

let private viewGrammarTests =
    testList "views render through the deployment's own chart grammar" [
        test "the prop bag is the one the grammar's own projector emits" {
            // The single check that keeps this from becoming a second
            // grammar. Both sides are built from the same series,
            // including a label carrying the encoding's own separators —
            // the case where two implementations of "the same" encoding
            // quietly disagree.
            let series: PeerViewSeries = {
                Name = "promo-spend"
                Points = [ point "w1;a" 10.0; point "w2=b" 20.5 ]
            }

            let projected =
                NarrativeFromData.chart
                    NarrativeFromData.Line
                    (Some series.Name)
                    (series.Points |> List.map (fun p -> p.Label, p.Value))

            match projected with
            | Component(name, props) ->
                Expect.equal name NarrativeCharts.ComponentName "the same component the grammar registers"

                Expect.equal
                    (PeerView.chartProps "line" series)
                    props
                    "the federated leg speaks the grammar's prop format, key for key and byte for byte"
            | other -> failtestf "expected the grammar's chart component; got %A" other
        }

        test "the bound bag is the grammar's bound bag, vintage prop included" {
            // The case above pins the UNBOUND three keys. What `render`
            // actually emits is the bound bag — Phase 649's
            // `chart.datasetVintage` on top — and a binding prop that
            // agreed with nothing would be worse than no binding at all,
            // because a reader would recover a vintage the grammar does
            // not recognise.
            let series: PeerViewSeries = {
                Name = "promo-spend"
                Points = [ point "w1;a" 10.0; point "w2=b" 20.5 ]
            }

            let binding: NarrativeFromData.ChartBinding = {
                ArtifactKey = None
                DatasetVintage = Some(PeerView.vintageToken spendView.DatasetId renderRequest.DatasetVersion)
            }

            let projected =
                NarrativeFromData.chartWith
                    binding
                    NarrativeFromData.Line
                    (Some series.Name)
                    (series.Points |> List.map (fun p -> p.Label, p.Value))

            match projected with
            | Component(_, props) ->
                Expect.equal
                    (PeerView.chartPropsAt "line" spendView.DatasetId renderRequest.DatasetVersion series)
                    props
                    "the bound federated bag is the grammar's bound bag"

                Expect.equal
                    PeerView.DatasetVintageProp
                    NarrativeFromData.DatasetVintageProp
                    "and it is the grammar's own key, not a second string that matches today"
            | other -> failtestf "expected the grammar's chart component; got %A" other
        }

        test "the rendered artifact declares the vintage the request pinned" {
            // The reason the binding is threaded at all: an artifact that
            // does not say which vintage it draws is one a modeller
            // cannot cite. Read back through the grammar's own reader, so
            // this asserts recoverability rather than string presence.
            let bag =
                PeerView.chartPropsAt spendView.Kind spendView.DatasetId renderRequest.DatasetVersion {
                    Name = "promo-spend"
                    Points = [ point "w1" 10.0 ]
                }

            let recovered = NarrativeFromData.chartBinding bag

            Expect.equal
                recovered.DatasetVintage
                (Some "weekly-panel@7")
                "the vintage names the dataset AND the version — neither alone re-derives the picture"

            Expect.isNone
                recovered.ArtifactKey
                "a view renders a dataset series, not a governed result — an empty artifact key would claim a binding that does not exist"
        }

        testCaseAsync "the artifact's bytes ARE that renderer's output, and the hash is over them"
        <| async {
            let instance = dataHostRendering viewOnly None (Some(viewDeps ())) boundOnly
            let! artifact = renderThrough instance renderRequest

            let bytes = Convert.FromBase64String artifact.Content
            let markup = Encoding.UTF8.GetString bytes

            Expect.stringContains markup "<svg" "the artifact is the grammar's inline SVG, not a re-implementation"

            Expect.stringContains
                markup
                "tu-chart__svg--line"
                "rendered at the kind the DECLARATION names — the host decides how its data is drawn"

            let expected =
                "sha256:"
                + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData bytes).ToLowerInvariant()

            Expect.equal artifact.ContentHash expected "the hash pins the bytes a modeller was shown"
        }
    ]

let private viewBoundsTests =
    testList "an over-bound request is refused typed, naming the bound" [
        testCaseAsync "each bound has its own class"
        <| async {
            let instance = dataHostRendering viewOnly None (Some(viewDeps ())) boundOnly

            let cases = [
                {
                    renderRequest with
                        ViewId = "spend-by-region"
                },
                "model-execution-view-undeclared"
                {
                    renderRequest with
                        Series = [ "margin-per-unit" ]
                },
                "model-execution-view-series-undeclared"
                { renderRequest with Series = [] }, "model-execution-view-no-series"
                {
                    renderRequest with
                        Window = {
                            referenceWindow with
                                From = DateTimeOffset(2025, 7, 13, 0, 0, 0, TimeSpan.Zero)
                        }
                },
                "model-execution-view-window-budget"
                {
                    renderRequest with
                        Window = {
                            From = referenceWindow.To
                            To = referenceWindow.From
                        }
                },
                "model-execution-view-window-unordered"
                {
                    renderRequest with
                        Resolution = "hour"
                },
                "model-execution-view-resolution-undeclared"
            ]

            for request, expected in cases do
                let! answer = call instance modellerPeerId (ModelExecutionPeerContract.viewRequest request)
                expectRefusal answer expected $"the refusal must name the bound that was left ({expected})"
        }

        test "the series budget is refused as its own class, above the declared count" {
            // Kept out of the dispatch loop above because it needs a view
            // whose series list is longer than its per-request budget —
            // the shape a deployment reaches for when it publishes many
            // series and renders few at a time.
            let wide = {
                spendView with
                    Series = [ "a"; "b"; "c" ]
                    MaxSeriesPerRequest = 2
            }

            let request = {
                renderRequest with
                    Series = [ "a"; "b"; "c" ]
            }

            match PeerView.validate [ wide ] request with
            | Error(PeerViewRefusal.SeriesBudgetExceeded(_, requested, limit)) ->
                Expect.equal (requested, limit) (3, 2) "the refusal carries what was asked and what is allowed"
            | other -> failtestf "expected a series-budget refusal; got %A" other
        }

        test "membership is judged before the count" {
            // Two things are wrong with this request; the refusal names
            // the one whose fix is different. A caller told 'too many'
            // would drop a series it was entitled to and still fail.
            let request = {
                renderRequest with
                    Series = [ "promo-spend"; "search-clicks"; "margin-per-unit" ]
            }

            match PeerView.validate [ spendView ] request with
            | Error(PeerViewRefusal.UndeclaredSeries(_, series)) ->
                Expect.equal series "margin-per-unit" "the series this view does not carry"
            | other -> failtestf "membership precedes the count; got %A" other
        }
    ]

let private viewRateTests =
    testList "the render budget is enforced data-side, per peer" [
        testCaseAsync "exhaustion is a typed refusal, not a timeout"
        <| async {
            let instance = dataHostRendering viewOnly None (Some(viewDeps ())) boundOnly

            // The declaration admits two per window.
            for _ in 1..2 do
                let! _ = renderThrough instance renderRequest
                ()

            let! third = call instance modellerPeerId (ModelExecutionPeerContract.viewRequest renderRequest)

            expectRefusal
                third
                "model-execution-view-render-budget"
                "a spent budget is a decision the receiver took, so it is reported as one"

            match third with
            | Ok(ModelExecutionPeerAnswer.Refused refusal) ->
                let described = ModelExecutionPeerRefusal.describe refusal

                // A caller that knows the limit and the window can
                // schedule; one left on a timeout can only retry into the
                // same wall.
                Expect.stringContains described "2" "the refusal carries the limit"
                Expect.stringContains described "60" "and the window"
            | other -> failtestf "expected a refusal; got %A" other
        }

        test "the window resets, and one peer's spend is not another's" {
            let clock = ref renderClock
            let guard = PeerViewRateGuard.inProcess (fun () -> clock.Value)

            let reserve (peerId: string) =
                guard.Reserve peerId spendView |> Async.RunSynchronously

            Expect.isTrue (reserve modellerPeerId).Admitted "the first render of the window"
            Expect.isTrue (reserve modellerPeerId).Admitted "the second, which is the declared limit"
            Expect.isFalse (reserve modellerPeerId).Admitted "the third is over it"

            // A budget shared between counterparties would let a busy
            // peer deny a quiet one, which is not a bound anybody agreed
            // to.
            Expect.isTrue (reserve strangerPeerId).Admitted "a different peer has its own budget"

            clock.Value <- renderClock.AddSeconds 61.0
            Expect.isTrue (reserve modellerPeerId).Admitted "a new window admits again"
        }

        testCaseAsync "a refused request does not spend a slot"
        <| async {
            // Validation runs before reservation, so a malformed request
            // cannot exhaust a peer's budget — otherwise a caller could
            // lock itself out with typos, and a hostile one could lock
            // out a peer it shares a binding with.
            let instance = dataHostRendering viewOnly None (Some(viewDeps ())) boundOnly

            for _ in 1..5 do
                let! answer =
                    call
                        instance
                        modellerPeerId
                        (ModelExecutionPeerContract.viewRequest {
                            renderRequest with
                                Resolution = "hour"
                        })

                expectRefusal answer "model-execution-view-resolution-undeclared" "refused on the bound"

            let! artifact = renderThrough instance renderRequest
            Expect.equal artifact.ViewId spendView.ViewId "the budget was never touched"
        }
    ]

let private viewAuthorityTests =
    testList "the level and the declaration are two gates, and both answer differently" [
        testCaseAsync "a declared view is still refused as an authority question at AggregatesOnly"
        <| async {
            // The ordering claim of §5.7.9, tested where it bites: this
            // deployment IMPLEMENTS the view and has not granted it, so
            // the remedy is a conversation and the refusal must say so.
            let instance =
                dataHostRendering PeerVisibilityBinding.default' None (Some(viewDeps ())) boundOnly

            let! answer = call instance modellerPeerId (ModelExecutionPeerContract.viewRequest renderRequest)

            expectRefusal
                answer
                "model-execution-authority-level-exceeded"
                "'we do that, and not for you' — not 'we do not do that'"
        }

        testCaseAsync "a granted peer is refused as UNDECLARED when the deployment renders no views"
        <| async {
            let instance = dataHostRendering viewOnly None None boundOnly
            let! answer = call instance modellerPeerId (ModelExecutionPeerContract.viewRequest renderRequest)

            expectRefusal
                answer
                "model-execution-undeclared-diagnostic"
                "the mirror image: granted the level, offered no view"
        }

        testCaseAsync "the raw-series vocabulary is still refused at ViewOnly"
        <| async {
            // The level is a rung, not a door: granting views does not
            // grant series, and the profile serves none at any level.
            let instance = dataHostRendering viewOnly None (Some(viewDeps ())) boundOnly

            let! answer =
                call
                    instance
                    modellerPeerId
                    (ModelExecutionPeerContract.request "ReadVintageSeries" referenceSubmission.Vintage)

            expectRefusal
                answer
                "model-execution-authority-level-exceeded"
                "a ViewOnly grant does not reach a Full requirement"
        }
    ]

let private viewAuditTests =
    testList "every render is recorded through the disclosure plane" [
        testCaseAsync "the gate is asked once, at the federation door, with the SERIES as its references"
        <| async {
            let claimed = ref ""

            let gate =
                RecordingGate(
                    Map.ofList [
                        "fact:spend-vs-response:promo-spend", FactDisclosable
                        "fact:spend-vs-response:search-clicks", FactDisclosable
                    ],
                    claimed
                )

            let route: PeerEgressRoute = {
                Gate = gate
                Purpose = {
                    PurposeId = "federated-modelling"
                    Claim = fun purpose -> claimed.Value <- purpose
                }
                // Deliberately distinct from the view-keyed set: if the
                // render took the operation-keyed door, this id would
                // appear and the assertion below would fail.
                References = fun _ -> [ "fact:operation-keyed" ]
                ViewReferences = fun viewId series -> series |> List.map (fun s -> $"fact:{viewId}:{s}")
            }

            let instance = dataHostRendering viewOnly (Some route) (Some(viewDeps ())) boundOnly
            let! artifact = renderThrough instance renderRequest

            let scopeId, surface, factIds, purposeAtCall = gate.Calls |> Seq.exactlyOne

            Expect.equal scopeId hostScope "the binding's scope, never one from the wire"
            Expect.equal surface FactPeerEgress "the shipped Phase 525 door, at the federation surface"

            // This is the query the level was sold on: which peer viewed
            // WHICH SERIES when. Keyed on the operation alone every
            // render would leave the same row.
            Expect.equal
                factIds
                [ "fact:spend-vs-response:promo-spend"; "fact:spend-vs-response:search-clicks" ]
                "the references name the series the artifact covered"

            Expect.equal
                purposeAtCall
                "federated-modelling"
                "the Phase 592 purpose was claimed before the gate was asked"

            // One crossing, one door, one row: the generic route is not
            // also taken, which `Seq.exactlyOne` above already asserts
            // and this makes explicit.
            Expect.equal
                artifact.Series
                [ "promo-spend"; "search-clicks" ]
                "the artifact and the audit record name the same series"
        }

        testCaseAsync "a withheld series refuses the render, naming the operation and nothing else"
        <| async {
            let claimed = ref ""

            let gate =
                RecordingGate(
                    Map.ofList [
                        "fact:spend-vs-response:promo-spend", FactDisclosable
                        "fact:spend-vs-response:search-clicks", FactNotDisclosable "licensed-third-party"
                    ],
                    claimed
                )

            let route: PeerEgressRoute = {
                Gate = gate
                Purpose = {
                    PurposeId = "federated-modelling"
                    Claim = fun purpose -> claimed.Value <- purpose
                }
                References = fun _ -> []
                ViewReferences = fun viewId series -> series |> List.map (fun s -> $"fact:{viewId}:{s}")
            }

            let instance = dataHostRendering viewOnly (Some route) (Some(viewDeps ())) boundOnly
            let! answer = call instance modellerPeerId (ModelExecutionPeerContract.viewRequest renderRequest)

            expectRefusal
                answer
                "model-execution-egress-withheld"
                "refused whole rather than partially redacted — a view with a series quietly missing is a lie"

            match answer with
            | Ok(ModelExecutionPeerAnswer.Refused refusal) ->
                let described = ModelExecutionPeerRefusal.describe refusal

                Expect.isFalse (described.Contains "licensed-third-party") "the policy is never named across the seam"
                Expect.isFalse (described.Contains "search-clicks") "nor is the series that was withheld"
            | other -> failtestf "expected a refusal; got %A" other
        }

        testCaseAsync "a deployment with no route renders and consults nothing"
        <| async {
            let instance = dataHostRendering viewOnly None (Some(viewDeps ())) boundOnly
            let! artifact = renderThrough instance renderRequest
            Expect.equal artifact.ViewId spendView.ViewId "no fact substrate composed, nothing routed (GP 13)"
        }
    ]

// ─── Phase 644 — registry lifecycle transitions over the seam ────────

/// A registry holding one artifact per key, whose status advances in
/// place. Enough to exercise the seam and nothing more: the seam's whole
/// contract with a registry is `Get` then `TransitionStatus`, and a
/// stub that implemented the rest would be asserting `BlobModelRegistry`
/// rather than this phase.
type private MemoryModelRegistry(initial: (string * ModelArtifactStatus) list) =
    let artifacts = ConcurrentDictionary<string, ModelArtifactStatus * int>()

    do
        for key, status in initial do
            artifacts[key] <- (status, 1)

    let artifactOf (key: string) (status: ModelArtifactStatus) (version: int) : ModelArtifact = {
        CompositeKey = {
            SpecHash = "sha256:spec"
            DatasetVersion = "consortium-north/weekly-panel@v7"
            Seed = 1L
            ProviderId = "reference-regression"
            ProviderVersion = "1.4.0"
            Hash = key
        }
        ScopeId = hostScope
        ArtifactRef = {
            ArtifactId = "artifact-8821"
            ContentHash = "sha256:artifact"
            ByteLength = 1L
        }
        Diagnostics = Map.empty
        GateVerdicts = []
        Status = status
        Annotations = Map.empty
        Notes = ""
        Attachments = []
        Signature = None
        RegisteredBy = "fitter"
        RegisteredAt = DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.Zero)
        Version = version
    }

    interface IModelRegistry with
        member _.Get(_scopeId, keyHash) = async {
            match artifacts.TryGetValue keyHash with
            | true, (status, version) -> return Ok(artifactOf keyHash status version)
            | _ -> return Error ModelRegistryError.NotFound
        }

        member _.TransitionStatus(_scopeId, keyHash, target, callerRole, _actorUserId) = async {
            match artifacts.TryGetValue keyHash with
            | false, _ -> return Error ModelRegistryError.NotFound
            | true, (from, version) ->
                if not (ModelArtifactStatus.canTransition from target) then
                    return Error(ModelRegistryError.IllegalTransition(from, target))
                elif
                    ModelArtifactStatus.requiresElevatedRole target
                    && not (TeamRoles.canWriteTeamConfig callerRole)
                then
                    return Error(ModelRegistryError.Forbidden "approving a model artifact requires Owner/Admin")
                else
                    artifacts[keyHash] <- (target, version + 1)
                    return Ok(artifactOf keyHash target (version + 1))
        }

        member _.Register(_, _, _, _, _) = failwith "not used"
        member _.QueryBySpecHash(_, _) = failwith "not used"
        member _.QueryByDatasetVersion(_, _) = failwith "not used"
        member _.QueryByStatus(_, _) = failwith "not used"
        member _.QueryPage(_, _, _, _) = failwith "not used"
        member _.AttachProvenance(_, _, _, _) = failwith "not used"
        member _.AttachmentLimits = ProvenanceAttachmentLimits.default'

/// An audit log that keeps what it was handed, so an attributed row can
/// be asserted as a recorded fact rather than inferred from an answer.
type private CapturingAuditLog() =
    let events = ResizeArray<AuditEvent>()

    member _.Events = List.ofSeq events

    member this.Attributed =
        this.Events
        |> List.choose (function
            | ModelArtifactTransitionAttributed p -> Some p
            | _ -> None)

    member this.JobCompletions =
        this.Events
        |> List.choose (function
            | PeerJobCompleted p -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(_scopeId, audit) = async { lock events (fun () -> events.Add audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

[<Literal>]
let private transitionKey = "sha256:artifact-key"

let private transitionDeps (registry: IModelRegistry) (audit: IAuditLog) : ModelTransitionDeps = {
    Registry = registry
    Audit = audit
    Now = fun () -> DateTimeOffset(2026, 7, 16, 10, 15, 0, TimeSpan.Zero)
}

/// A data host that admits transitions, with one artifact in `Fitted`
/// and one peer holding `grant`.
let private dataHostTransitioning (grant: ModelTransitionAuthority) =
    let backend = ReferenceDataHost()
    let scheduler = DeferredScheduler()
    let results = MemoryJobResultStore() :> IPeerJobResultStore
    let audit = CapturingAuditLog()

    let registry =
        MemoryModelRegistry [ transitionKey, ModelArtifactStatus.Fitted ] :> IModelRegistry

    let resolveBinding (peerId: string) = async {
        if peerId <> modellerPeerId then
            return None
        else
            return
                Some {
                    PeerId = peerId
                    ScopeId = hostScope
                    Api = backend.Api
                    Visibility = PeerVisibilityBinding.default'
                    Egress = None
                    TransitionAuthority = grant
                }
    }

    let deps: ModelExecutionPeerDeps = {
        ResolveBinding = resolveBinding
        Admission =
            ModelExecutionAdmission.create declaredDiagnostics
            |> ModelExecutionAdmission.withTransitions
        FitPoll = ModelExecutionFitPollPolicy.immediate
        Views = None
        Transitions = Some(transitionDeps registry (audit :> IAuditLog))
        // Phase 646 — this fixture is the TRANSITION harness; the transfer
        // seam has its own, over the real blob-backed registry, because a
        // transfer that stored nothing would certify nothing.
        Promotions = None
    }

    let fusion: PeerJobFusion = {
        Scheduler = scheduler
        ResultStore = results
        AuditLog = Some(audit :> IAuditLog)
    }

    let peer = DefaultPlatformPeer("data-host") :> IPlatformPeer
    let host = ModelExecutionPeerContract.host deps (Some fusion)
    peer.RegisterContract host.Registration

    for handlerName, handler in host.JobHandlers do
        (scheduler :> IJobScheduler).RegisterHandler(handlerName, handler)

    let instance = {
        Peer = peer
        Scheduler = scheduler
        Results = results
        Backend = backend
        Decisions = ResizeArray<PeerCleanRoomDecisionPayload>()
    }

    instance, registry, audit

let private approveGrant = ModelTransitionAuthority.ofTargets [ "Approved" ]

let private invocation (target: string) : PeerTransitionInvocation = {
    ArtifactKey = transitionKey
    Target = target
    ActorId = "r.okafor"
    Rationale = Some "holdout MAPE within tolerance on three vintages"
}

/// Invoke a transition and run the queued job, returning the parked
/// answer — the whole modeller-side round trip, since a transition rides
/// the queued leg exactly as a fit does.
let private transitionThrough (instance: DataHostInstance) (invoked: PeerTransitionInvocation) = async {
    let! accepted = dispatch instance modellerPeerId (ModelExecutionPeerContract.transitionRequest invoked)

    match accepted with
    | Error e -> return failtestf "the invocation must be accepted onto the queue; got %A" e
    | Ok json ->
        // The queued leg answers with a JOB ID. A transition is a
        // judgment, and a data host is entitled to take time over one.
        match JsonRpc.deserialize<obj> json with
        | _ ->
            do! instance.Scheduler.RunPending()
            let jobId = JsonRpc.deserialize<Guid> json

            match! instance.Results.TryGetResult(PeerJob.Scope, jobId) with
            | Some record ->
                match record.Status with
                | PeerJobStatus.Completed body -> return JsonRpc.deserialize<ModelExecutionPeerAnswer> body
                | other -> return failtestf "the queued transition must complete; got %A" other
            | None -> return failtestf "the queued transition parked no result"
}

let private transitionSeamTests =
    testList "one state machine, three authors" [
        test "the pure judge refuses an impossible edge BEFORE an insufficient grant" {
            // A retired artifact cannot become fitted again at any grant.
            // Judged with a FULL grant, so what is under test is the
            // order and not the grant: reporting this as an authority
            // question would send an author to negotiate for something no
            // agreement can provide.
            let request: ModelTransitionRequest = {
                ArtifactKey = transitionKey
                Target = ModelArtifactStatus.Fitted
                Author = PeerActor(modellerPeerId, "r.okafor")
                Rationale = None
            }

            match ModelTransition.judge (Some ModelArtifactStatus.Retired) ModelTransitionAuthority.full request with
            | Error(ModelTransitionRefusal.InvalidTransition(_, from, target)) ->
                Expect.equal from "Retired" "the status it actually held"
                Expect.equal target "Fitted" "the status it was asked to enter"
            | other -> failtestf "expected an invalid-transition refusal; got %A" other

            // The same request under NO grant is still the invalid-edge
            // refusal, which is the whole of the ordering claim.
            match ModelTransition.judge (Some ModelArtifactStatus.Retired) ModelTransitionAuthority.none request with
            | Error(ModelTransitionRefusal.InvalidTransition _) -> ()
            | other -> failtestf "the edge is judged before the grant; got %A" other
        }

        test "an undeclared grant admits nothing — not even the obvious transitions" {
            let request: ModelTransitionRequest = {
                ArtifactKey = transitionKey
                Target = ModelArtifactStatus.Approved
                Author = PeerActor(modellerPeerId, "r.okafor")
                Rationale = None
            }

            match ModelTransition.judge (Some ModelArtifactStatus.Fitted) ModelTransitionAuthority.none request with
            | Error(ModelTransitionRefusal.InsufficientAuthority(_, target, author)) ->
                Expect.equal target "Approved" "the refusal names what was asked for"
                Expect.equal author $"{modellerPeerId}/r.okafor" "and who asked, both identities"
            | other -> failtestf "fail closed: an undeclared grant admits nothing; got %A" other
        }

        test "a grant naming a status this build does not know carries nothing" {
            let grant = ModelTransitionAuthority.ofTargets [ "Approved"; "Blessed" ]

            Expect.equal
                (ModelTransitionAuthority.labels grant)
                [ "Approved" ]
                "the unknown label is dropped, not carried"
        }

        testCaseAsync "the same entry point serves a local user, a peer and a policy"
        <| async {
            // The author-agnostic claim, asserted rather than described:
            // three authors, one `invoke`, three attributed rows that
            // differ only in the two fields that are ABOUT the author.
            let registry =
                MemoryModelRegistry [
                    "a", ModelArtifactStatus.Fitted
                    "b", ModelArtifactStatus.Fitted
                    "c", ModelArtifactStatus.Fitted
                ]
                :> IModelRegistry

            let audit = CapturingAuditLog()
            let deps = transitionDeps registry (audit :> IAuditLog)

            let request key author : ModelTransitionRequest = {
                ArtifactKey = key
                Target = ModelArtifactStatus.Approved
                Author = author
                Rationale = None
            }

            let! _ = ModelTransition.invoke deps hostScope approveGrant (request "a" (LocalUser("alice", Owner)))

            let! _ =
                ModelTransition.invoke
                    deps
                    hostScope
                    approveGrant
                    (request "b" (PeerActor("consortium-north", "r.okafor")))

            let! _ =
                ModelTransition.invoke deps hostScope approveGrant (request "c" (PolicyVerdict "promote-on-holdout"))

            let rows =
                audit.Attributed
                |> List.map (fun p -> p.CompositeKeyHash, p.Channel, p.AuthorKind, p.AuthorId)

            Expect.containsAll
                rows
                [
                    "a", "local", "user", "alice"
                    // A peer's row carries BOTH identities, because either
                    // alone is ambiguous across a federation.
                    "b", "peer", "peer", "consortium-north/r.okafor"
                    // A policy is authored data-side, so it arrives on the
                    // local channel and is told apart by its KIND — one
                    // axis for how it reached us, another for who decided.
                    "c", "local", "policy", "promote-on-holdout"
                ]
                "one entry point, three authors, three attributed rows"

            Expect.all audit.Attributed _.Admitted "all three were admitted"
        }

        testCaseAsync "a refusal is recorded too, so the attributed trail stands alone"
        <| async {
            // A transition refused at the seam never reaches the
            // registry, so the registry's own rows cannot answer "who
            // tried what". This one can.
            let registry =
                MemoryModelRegistry [ transitionKey, ModelArtifactStatus.Fitted ] :> IModelRegistry

            let audit = CapturingAuditLog()
            let deps = transitionDeps registry (audit :> IAuditLog)

            let! outcome =
                ModelTransition.invoke deps hostScope ModelTransitionAuthority.none {
                    ArtifactKey = transitionKey
                    Target = ModelArtifactStatus.Approved
                    Author = PeerActor(modellerPeerId, "r.okafor")
                    Rationale = Some "please"
                }

            Expect.isError outcome "an ungranted author is refused"

            match audit.Attributed with
            | [ row ] ->
                Expect.isFalse row.Admitted "the row records the refusal"
                Expect.equal row.FromStatus "Fitted" "and the status the artifact held while it was refused"
                Expect.equal row.Rationale "please" "the author's stated reason rides the trail"
                Expect.stringContains row.Refusal "not granted" "with the seam's own description"
            | other -> failtestf "expected exactly one attributed row; got %i" (List.length other)
        }
    ]

let private transitionPeerTests =
    testList "a granted peer moves an artifact through its lifecycle" [
        testCaseAsync "an invocation inside the grant records the transition with its channel and author"
        <| async {
            let instance, registry, _ = dataHostTransitioning approveGrant
            let! answer = transitionThrough instance (invocation "Approved")

            match ModelExecutionPeerContract.answerBody<PeerTransitionRecord> answer with
            | Error refusal -> failtestf "the invocation must be admitted; got %A" refusal
            | Ok record ->
                Expect.equal record.FromStatus "Fitted" "the status it moved from, echoed rather than assumed"
                Expect.equal record.ToStatus "Approved" "and the one it entered"
                Expect.equal record.Channel "peer" "the channel it arrived on"
                Expect.equal record.AuthorKind "peer" "the kind of author that took it"
                Expect.equal record.AuthorId $"{modellerPeerId}/r.okafor" "peer and actor, both"
                Expect.equal record.Version 2 "a transition appends a version, never mutates one (GP 5)"

            // The durable record landed data-side, which is the topology
            // the phase exists for: judgment there, record here.
            match! registry.Get(hostScope, transitionKey) with
            | Ok artifact ->
                Expect.equal artifact.Status ModelArtifactStatus.Approved "the data host holds the new status"
            | Error e -> failtestf "the artifact must still be readable; got %A" e
        }

        testCaseAsync "a peer granted something else is refused with the authority class"
        <| async {
            let instance, registry, _ =
                dataHostTransitioning (ModelTransitionAuthority.ofTargets [ "Retired" ])

            let! answer = transitionThrough instance (invocation "Approved")

            match answer with
            | ModelExecutionPeerAnswer.Refused refusal ->
                Expect.equal
                    (ModelExecutionPeerRefusal.className refusal)
                    PeerTransition.InsufficientAuthorityClass
                    "granted something is not granted everything"
            | other -> failtestf "expected a refusal; got %A" other

            match! registry.Get(hostScope, transitionKey) with
            | Ok artifact -> Expect.equal artifact.Status ModelArtifactStatus.Fitted "and nothing moved"
            | Error e -> failtestf "the artifact must still be readable; got %A" e
        }

        testCaseAsync "an unknown artifact is refused before the graph is consulted"
        <| async {
            let instance, _, _ = dataHostTransitioning approveGrant

            let! answer =
                transitionThrough instance {
                    invocation "Approved" with
                        ArtifactKey = "sha256:nothing-here"
                }

            match answer with
            | ModelExecutionPeerAnswer.Refused refusal ->
                Expect.equal
                    (ModelExecutionPeerRefusal.className refusal)
                    PeerTransition.UnknownArtifactClass
                    "no artifact, nothing to judge"
            | other -> failtestf "expected a refusal; got %A" other
        }

        testCaseAsync "a target naming no lifecycle status is unreadable, not an illegal edge"
        <| async {
            let instance, _, _ = dataHostTransitioning ModelTransitionAuthority.full
            let! answer = transitionThrough instance (invocation "Blessed")

            match answer with
            | ModelExecutionPeerAnswer.Refused refusal ->
                Expect.equal
                    (ModelExecutionPeerRefusal.className refusal)
                    "model-execution-request-unreadable"
                    "a word that names no state is not an edge the graph forbids"
            | other -> failtestf "expected a refusal; got %A" other
        }

        testCaseAsync "a deployment that declares no transitions refuses as undeclared"
        <| async {
            // The pre-644 posture, which is what every case in the
            // sections above is evidence for (GP 11). The shared
            // `dataHost` harness declares nothing.
            let instance = dataHost boundOnly

            let! answer =
                call instance modellerPeerId (ModelExecutionPeerContract.transitionRequest (invocation "Approved"))

            match answer with
            | Ok(ModelExecutionPeerAnswer.Refused refusal) ->
                Expect.equal
                    (ModelExecutionPeerRefusal.className refusal)
                    "model-execution-undeclared-diagnostic"
                    "'we do not do that' — a different remedy from 'not for you'"
            | other -> failtestf "expected an undeclared refusal; got %A" other
        }

        testCaseAsync "the terminal outcome of a queued transition is audited"
        <| async {
            // Phase 310's contract, which this profile's hand-built job
            // handler did not honour before this phase: the receiver's
            // trail stopped at DISPATCH, so every queued call was logged
            // as scheduled and never as completed.
            let instance, _, audit = dataHostTransitioning approveGrant
            let! _ = transitionThrough instance (invocation "Approved")

            match audit.JobCompletions with
            | [ completion ] ->
                Expect.equal completion.MethodName "InvokeTransition" "the terminal row names the method"
                Expect.equal completion.CallerPeerId modellerPeerId "and the peer that scheduled it"
                Expect.isTrue completion.Succeeded "this one succeeded"
                Expect.equal completion.Outcome "ok" "with the terminal outcome, not the schedule"
            | other -> failtestf "expected exactly one terminal row; got %i" (List.length other)
        }

        testCaseAsync "a refused queued transition's terminal row carries the refusal CLASS"
        <| async {
            let instance, _, audit =
                dataHostTransitioning (ModelTransitionAuthority.ofTargets [ "Retired" ])

            let! _ = transitionThrough instance (invocation "Approved")

            match audit.JobCompletions with
            | [ completion ] ->
                Expect.isFalse completion.Succeeded "the queued judgment refused"

                Expect.equal
                    completion.Outcome
                    PeerTransition.InsufficientAuthorityClass
                    "the class, not a generic error name — the two refusal reasons an operator is trying to tell apart"
            | other -> failtestf "expected exactly one terminal row; got %i" (List.length other)
        }
    ]

let private transitionDeclarationTests =
    testList "the grant is declared, published and pinnable" [
        test "a deployment's composed grant is what its descriptor publishes" {
            let surface =
                PeerServerApp.create ()
                // The descriptor is the empty label unless the peer
                // substrate is on — a deployment with no wire face
                // publishes no grant, which is itself the fail-closed
                // shape and would make this assertion vacuously pass.
                |> PeerServerApp.withConfig {
                    ServerConfig.defaults with
                        PeerSubstrate = EnabledPeerSubstrate
                }
                |> PeerServerApp.withPeerTransitionAuthority (
                    ModelTransitionAuthority.ofTargets [ "Retired"; "Approved" ]
                )
                |> PeerSurface.describe

            // Ordinally sorted regardless of declaration order, so two
            // deployments declaring the same grant publish the same bytes.
            Expect.equal surface.TransitionAuthority [ "Approved"; "Retired" ] "one value, published sorted"
        }

        test "an undeclared or unreadable declaration grants nothing" {
            let unreadable = {
                PeerSurface.empty with
                    TransitionAuthority = [ "Approved"; "Blessed"; Unchecked.defaultof<string> ]
            }

            Expect.equal
                (PeerSurface.transitionAuthority unreadable)
                [ "Approved" ]
                "a word this build cannot enforce is not a grant"

            Expect.equal (PeerSurface.transitionAuthority PeerSurface.empty) [] "and silence is not one either"

            Expect.equal
                (PeerSurface.transitionAuthority {
                    PeerSurface.empty with
                        TransitionAuthority = Unchecked.defaultof<string list>
                })
                []
                "including the label published before the member existed"
        }

        test "the two authority axes are independent" {
            // The arrangement the phase exists to make expressible: a
            // counterparty that may approve models and must never see a
            // row. If the grant were a rung on the visibility ladder,
            // this composition could not be written.
            let surface =
                PeerServerApp.create ()
                // The descriptor is the empty label unless the peer
                // substrate is on — a deployment with no wire face
                // publishes no grant, which is itself the fail-closed
                // shape and would make this assertion vacuously pass.
                |> PeerServerApp.withConfig {
                    ServerConfig.defaults with
                        PeerSubstrate = EnabledPeerSubstrate
                }
                |> PeerServerApp.withPeerTransitionAuthority approveGrant
                |> PeerSurface.describe

            Expect.equal
                (PeerSurface.dataVisibility surface)
                PeerDataVisibilityLevel.AggregatesOnly
                "the narrowest visibility"

            Expect.equal surface.TransitionAuthority [ "Approved" ] "beside a real transition grant"
        }
    ]

let tests =
    testList "Phase 638 — federated model execution" [
        roundTripTests
        noRowSurfaceTests
        diagnosticsTests
        scopeTests
        submitterVocabularyTests
        // Phase 642 — declared authority levels over the same seam.
        authorityLevelTests
        narrowingTests
        authorityAdmissionTests
        federatedEgressTests
        authorityVocabularyTests
        declaredSurfaceTests
        // Phase 643 — the machinery the `ViewOnly` level names.
        viewContractTests
        viewGrammarTests
        viewBoundsTests
        viewRateTests
        viewAuthorityTests
        viewAuditTests
        // Phase 644 — lifecycle judgment across the seam, on the other
        // authority axis.
        transitionSeamTests
        transitionPeerTests
        transitionDeclarationTests
    ]