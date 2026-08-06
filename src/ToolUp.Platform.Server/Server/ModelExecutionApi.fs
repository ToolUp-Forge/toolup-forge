// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ModelExecutionApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── Phase 600 — model-execution submitter API handler ──────────────────
//
// Builds the `ModelExecutionApi` remoting handler: the out-of-process
// submitter face of the fit envelope (449), registry (453 + 599 bulk
// reads), dataset store (448), and scorer (454). Mounted only when
// `ServerConfig.ModelExecution = EnabledModelExecutionApi` (GP 13);
// resolves every substrate lazily from DI per request and reports a typed
// `SubstrateDisabled` refusal naming whichever surface is absent.
//
// **Scope discipline (GP 4).** Every method executes under the caller's
// resolved `AccessContext` scope — wire shapes never carry a scope, so a
// submitter cannot name another team's data (the `JobApi`
// anti-impersonation precedent). **Write gating**: submissions + scoring
// require Owner/Admin in Team/MultiTeam mode (`TeamRoles.canWriteTeamConfig`
// — the `JobApiHandler.ensureWriteAllowed` shape); reads are ungated
// within the caller's scope. **Audit (GP 6)**: submissions ride the Phase
// 599 batch envelope (`ModelFitBatchSubmitted` stamped with the submitter
// identity); fit runs + registrations + scores emit their own Phase
// 449/453/454 rows.
//
// **Refusal totality.** Every method body runs inside `guarded`, so an
// unmapped server-side exception surfaces as
// `ModelExecutionRefusal.Unexpected` — contract-proven: no endpoint can
// return a bare exception on the wire.
//
// **SpecHash opacity (interface-plan D4 / Phase 603).** The submission's
// `SpecHash` is stored verbatim — this handler never re-derives or
// validates it against the payload (`ModelSpecRef.ofPayload` would
// re-hash, so it is deliberately NOT used here).

/// Total refusal mapping for the scoring envelope's typed error.
let private mapScoreError (e: ScoreError) : ModelExecutionScoreRefusal =
    match e with
    | ScoreError.ProviderNotFound k -> ModelExecutionScoreRefusal.ProviderNotFound k
    | ScoreError.NotApproved s -> ModelExecutionScoreRefusal.NotApproved s
    | ScoreError.InputSchemaMismatch r -> ModelExecutionScoreRefusal.InputSchemaMismatch r
    | ScoreError.InputUnavailable r -> ModelExecutionScoreRefusal.InputUnavailable r
    | ScoreError.ProviderFailed(k, m) -> ModelExecutionScoreRefusal.ProviderFailed(k, m)
    | ScoreError.StorageFailure r -> ModelExecutionScoreRefusal.StorageFailure r

/// Total refusal mapping for a per-item enqueue failure (Phase 640).
///
/// Each arm is chosen so the class a caller branches on tells it what to
/// *do*, which is the only reason to type a denial at all:
///   * a missing handler is a capability this deployment does not offer,
///     not a thing that is merely absent — `SubstrateDisabled`, the same
///     class every uncomposed substrate on this face reports;
///   * an unsupported precision is a named, stable scheduler rule
///     declining the registration, and carries no quota to arithmetic
///     against — `PolicyRefused`, not `BudgetDenied` and not `Invalid…`,
///     because the submitter supplied no precision and blaming the
///     submission would send them looking for a defect in their own
///     document;
///   * a storage or trigger failure is diagnostic and may not recur.
let private mapEnqueueRefusal (e: FitEnqueueRefusal) : ModelExecutionRefusal =
    match e with
    | FitEnqueueRefusal.ScheduleRefused(HandlerNotRegistered name) ->
        ModelExecutionRefusal.SubstrateDisabled $"job handler '{name}'"
    | FitEnqueueRefusal.ScheduleRefused(PrecisionUnsupported _) ->
        ModelExecutionRefusal.PolicyRefused "job.precision-unsupported"
    | FitEnqueueRefusal.ScheduleRefused(InvalidCron(expr, reason)) ->
        ModelExecutionRefusal.InvalidSubmission $"invalid cron '{expr}': {reason}"
    | FitEnqueueRefusal.ScheduleRefused(ScheduleError.StorageFailure m) ->
        ModelExecutionRefusal.StorageFailure $"schedule storage failure: {m}"
    | FitEnqueueRefusal.TriggerRefused reason -> ModelExecutionRefusal.StorageFailure $"trigger failed: {reason}"

/// The wire projection of one gate verdict (Phase 449 → wire).
let private toWireGateVerdict (v: GateVerdict) : ModelExecutionGateVerdict = {
    Name = v.Name
    Threshold = v.Threshold
    Direction = GateDirection.name v.Direction
    Observed = v.Observed
    Passed = v.Passed
}

/// The wire projection of a registered artifact (Phase 453 → wire).
///
/// **Timing and cost carry what the registry retains, and nothing
/// invented.** A registered artifact records when it was registered; the
/// fit's own start, completion and provider-reported duration/cost are not
/// stored on it today, so `StartedAt` / `CompletedAt` / `DurationMs` /
/// `Cost` are honestly absent rather than back-filled from `RegisteredAt`.
/// Fabricating them would be worse than omitting them: a consumer cannot
/// tell an invented timestamp from a measured one, and every downstream
/// duration computed from it would be wrong in a way nothing detects.
let private toWireOutcome (a: ModelArtifact) : ModelExecutionOutcome = {
    CompositeKeyHash = a.CompositeKey.Hash
    SpecHash = a.CompositeKey.SpecHash
    DatasetVersion = a.CompositeKey.DatasetVersion
    Seed = a.CompositeKey.Seed
    ProviderId = a.CompositeKey.ProviderId
    ProviderVersion = a.CompositeKey.ProviderVersion
    Artifact =
        Some {
            ArtifactId = a.ArtifactRef.ArtifactId
            ContentHash = a.ArtifactRef.ContentHash
            // Forge's `ArtifactRef` carries a byte length, not a format
            // identifier: the artifact bytes are the provider's, and forge
            // declares nothing about a container it never opens (GP 1).
            Format = None
        }
    Timing = {
        SubmittedAt = a.RegisteredAt
        StartedAt = None
        CompletedAt = None
        DurationMs = None
    }
    Cost = None
    Diagnostics = a.Diagnostics
    GateVerdicts = a.GateVerdicts |> List.map toWireGateVerdict
    Status = ModelArtifactStatus.name a.Status
    Annotations = a.Annotations
    RegisteredAt = a.RegisteredAt
}

/// The wire projection of a dataset version (Phase 448 → wire).
let private toWireDatasetVersion (v: DatasetVersion) : ModelExecutionDatasetVersion = {
    DatasetId = v.DatasetId
    Version = v.Version
    RowCount = v.RowCount
    Format = v.Format
    ContentHash = v.ContentHash
    CreatedAt = v.CreatedAt
}

let private mapDatasetError (what: string) (e: DatasetError) : ModelExecutionRefusal =
    match e with
    | DatasetError.NotFound -> ModelExecutionRefusal.NotFound what
    | DatasetError.VersionNotFound v -> ModelExecutionRefusal.NotFound $"{what} version {v}"
    | other -> ModelExecutionRefusal.StorageFailure(DatasetError.describe other)

/// Parse one wire submission into a `FitRequest` under the resolved
/// scope. `Error` on a malformed gate direction. The submitter-minted
/// `SpecHash` is carried verbatim (opacity — never re-derived).
let private toFitRequest (scopeId: string) (s: ModelExecutionFitSubmission) : Result<FitRequest, string> =
    let gates =
        s.Gates
        |> List.map (fun g ->
            match GateDirection.parse g.Direction with
            | Some d ->
                Ok {
                    Name = g.Name
                    Threshold = g.Threshold
                    Direction = d
                }
            | None -> Error $"gate '{g.Name}' has unknown direction '{g.Direction}' (expected AtLeast/AtMost)")

    match
        gates
        |> List.tryPick (fun g ->
            match g with
            | Error e -> Some e
            | Ok _ -> None)
    with
    | Some e -> Error e
    | None ->
        Ok {
            ScopeId = scopeId
            DatasetVersion = {
                ScopeId = scopeId
                DatasetId = s.DatasetId
                Version = s.DatasetVersion
            }
            SpecRef = {
                Payload = s.SpecPayload
                // Submitter-minted, opaque — stored verbatim (D4/603).
                SpecHash = s.SpecHash
                // Phase 640 — the rule the submitter says it minted under,
                // carried through untouched. Never parsed, never checked
                // against a registry, never used to re-derive the hash: a
                // receiver that validated it would have re-acquired exactly
                // the judgement the opacity posture exists to refuse.
                SpecHashAlgorithm = s.SpecHashAlgorithm
            }
            ProviderKind = s.ProviderKind
            Seed = s.Seed
            Gates =
                gates
                |> List.choose (fun g ->
                    match g with
                    | Ok v -> Some v
                    | Error _ -> None)
            // Phase 451 (plan decision D5) — carried through verbatim from
            // the submitter's declaration; forge never infers it.
            SubmitterClass = s.SubmitterClass
        }

/// Resolve an optional DI service (module-level because local bindings
/// cannot carry explicit type parameters).
let private service<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService(typeof<'T>) with
    | :? 'T as s -> Some s
    | _ -> None

let modelExecutionApi (ctx: HttpContext) : ModelExecutionApi =

    let accessContext =
        match service<AccessContext> ctx with
        | Some ac -> ac
        | None ->
            // Test fallback mirroring `JobApiHandler.jobApi`.
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            AccessContext.unrestricted (AnonymousSession userId)

    let scopeOpt = AccessContext.configScope accessContext

    /// The submitter identity + class stamped into the audit trail (GP 6).
    let submitter = accessContext.UserId

    /// Phase 640 — the deployment's opt-in executor policy. Resolved like
    /// every other substrate here, so an unconfigured deployment gets the
    /// permissive value and behaves exactly as it did before this existed
    /// (GP 11 + GP 13).
    let policy =
        match service<ModelExecutionPolicy> ctx with
        | Some p -> p
        | None -> ModelExecutionPolicy.permissive

    /// Owner/Admin write gate in Team/MultiTeam mode — the
    /// `JobApiHandler.ensureWriteAllowed` policy verbatim.
    let ensureWriteAllowed () : Async<Result<unit, ModelExecutionRefusal>> = async {
        match accessContext.Subject with
        | TeamMember(userId, teamId) ->
            match service<ITeamStore> ctx with
            | Some ts ->
                let! role = ts.GetMemberRole(teamId, userId)

                match role with
                | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                | Some r ->
                    return
                        Error(
                            ModelExecutionRefusal.Forbidden
                                $"model execution requires Owner/Admin; your role: {TeamRoles.displayName r}"
                        )
                | None -> return Error(ModelExecutionRefusal.Forbidden "you are not a member of this team")
            | None -> return Error(ModelExecutionRefusal.Forbidden "team management is not available")
        | _ -> return Ok()
    }

    /// Refusal-totality guard: an unmapped exception becomes
    /// `Unexpected` data, never a bare wire exception.
    let guarded (body: unit -> Async<Result<'T, ModelExecutionRefusal>>) : Async<Result<'T, ModelExecutionRefusal>> = async {
        try
            return! body ()
        with ex ->
            return Error(ModelExecutionRefusal.Unexpected ex.Message)
    }

    let withScope (f: string -> Async<Result<'T, ModelExecutionRefusal>>) = async {
        match scopeOpt with
        | None -> return Error ModelExecutionRefusal.ScopeUnavailable
        | Some scope -> return! f scope.ScopeId
    }

    /// Shared submission path: validate provider kinds early (deny typed),
    /// then ride the Phase 599 batch envelope.
    let submitBatch (scopeId: string) (batchId: string) (items: ModelExecutionFitSubmission list) = async {
        match service<IJobScheduler> ctx, service<IAuditLog> ctx with
        | None, _ -> return Error(ModelExecutionRefusal.SubstrateDisabled "job scheduler")
        | _, None -> return Error(ModelExecutionRefusal.SubstrateDisabled "audit log")
        | Some scheduler, Some audit ->
            // Deny-early provider check when the fit substrate is composed;
            // an unknown kind never reaches the queue.
            match service<ModelFitProviderRegistry> ctx with
            | None -> return Error(ModelExecutionRefusal.SubstrateDisabled "model fitting")
            | Some providers ->
                let unknownKind =
                    items
                    |> List.tryPick (fun i ->
                        if (providers.TryResolve i.ProviderKind).IsNone then
                            Some i.ProviderKind
                        else
                            None)

                match unknownKind with
                | Some kind -> return Error(ModelExecutionRefusal.UnknownProvider kind)
                | None ->
                    let parsed = items |> List.map (toFitRequest scopeId)

                    match
                        parsed
                        |> List.tryPick (fun r ->
                            match r with
                            | Error e -> Some e
                            | Ok _ -> None)
                    with
                    | Some reason -> return Error(ModelExecutionRefusal.InvalidSubmission reason)
                    | None ->
                        let batch: FitRequestBatch = {
                            BatchId = batchId
                            ScopeId = scopeId
                            Requests =
                                parsed
                                |> List.choose (fun r ->
                                    match r with
                                    | Ok v -> Some v
                                    | Error _ -> None)
                        }

                        // Phase 451 — the compute-budget guard, when the
                        // deployment composed one. Resolved per request
                        // like every other substrate here, so
                        // `NoComputeBudget` (nothing registered) is a
                        // `None` and one match (GP 13).
                        //
                        // This is where a FEDERATED peer's fit submission
                        // is caught: the peer contract calls
                        // `binding.Api.SubmitFit`, which is this handler,
                        // so a peer never reaches the enqueue without
                        // passing the same budget a local caller does.
                        let budgetGuard = service<ComputeBudgetGuard> ctx

                        match! ModelFitBatch.submitBudgeted scheduler audit budgetGuard submitter batch with
                        | Error(FitBatchError.BudgetDenied denial) ->
                            // Typed, enumerable, and NOT flattened into
                            // `InvalidSubmission` (plan decision D6) — a
                            // submitter that can read the quota and the
                            // spend can decide whether to narrow its
                            // search or wait for the period to roll.
                            return Error(ModelExecutionRefusal.BudgetDenied denial)
                        | Error e -> return Error(ModelExecutionRefusal.InvalidSubmission(FitBatchError.describe e))
                        | Ok s ->
                            return
                                Ok {
                                    BatchId = s.BatchId
                                    ItemCount = s.ItemCount
                                    Jobs =
                                        s.ScheduledJobs
                                        |> List.map (fun (i, jobId) -> {
                                            Index = i
                                            // Rendered, not relayed as a
                                            // `Guid`: the handle is opaque
                                            // on this face (Phase 640), and
                                            // the scheduler's choice of
                                            // identity type is an
                                            // implementation detail that
                                            // stops here.
                                            JobId = string jobId
                                        })
                                    EnqueueFailures =
                                        s.ScheduleFailures
                                        |> List.map (fun (i, refusal) -> i, mapEnqueueRefusal refusal)
                                }
    }

    let withRegistry (f: IModelRegistry -> Async<Result<'T, ModelExecutionRefusal>>) = async {
        match service<IModelRegistry> ctx with
        | None -> return Error(ModelExecutionRefusal.SubstrateDisabled "model registry")
        | Some registry -> return! f registry
    }

    let withDatasets (f: IDatasetStore -> Async<Result<'T, ModelExecutionRefusal>>) = async {
        match service<IDatasetStore> ctx with
        | None -> return Error(ModelExecutionRefusal.SubstrateDisabled "datasets")
        | Some datasets -> return! f datasets
    }

    let submitGuarded (batchId: string) (items: ModelExecutionFitSubmission list) =
        guarded (fun () ->
            withScope (fun scopeId -> async {
                let! rbac = ensureWriteAllowed ()

                match rbac with
                | Error e -> return Error e
                | Ok() -> return! submitBatch scopeId batchId items
            }))

    {
        SubmitFit =
            fun submission ->
                // The degenerate batch of one: correlation id derived from
                // the submitter-minted spec hash + seed so a lone submission
                // still keys its outcome annotations deterministically.
                submitGuarded $"single/{submission.SpecHash}/{submission.Seed}" [ submission ]

        SubmitFitBatch = fun batch -> submitGuarded batch.BatchId batch.Items

        GetOutcome =
            fun keyHash ->
                guarded (fun () ->
                    withScope (fun scopeId ->
                        withRegistry (fun registry -> async {
                            match! registry.Get(scopeId, keyHash) with
                            | Ok artifact -> return Ok(toWireOutcome artifact)
                            | Error ModelRegistryError.NotFound ->
                                return Error(ModelExecutionRefusal.NotFound $"outcome {keyHash}")
                            | Error e ->
                                return Error(ModelExecutionRefusal.StorageFailure(ModelRegistryError.describe e))
                        })))

        QueryOutcomes =
            fun (query, cursor, limit) ->
                guarded (fun () ->
                    withScope (fun scopeId ->
                        withRegistry (fun registry -> async {
                            let statuses =
                                query.Statuses
                                |> List.map (fun s ->
                                    match ModelArtifactStatus.parse s with
                                    | Some status -> Ok status
                                    | None -> Error $"unknown status '{s}'")

                            match
                                statuses
                                |> List.tryPick (fun r ->
                                    match r with
                                    | Error e -> Some e
                                    | Ok _ -> None)
                            with
                            | Some reason -> return Error(ModelExecutionRefusal.InvalidQuery reason)
                            | None ->
                                let registryQuery: ModelRegistryQuery = {
                                    SpecHashes = query.SpecHashes
                                    DatasetVersions = query.DatasetVersions
                                    Statuses =
                                        statuses
                                        |> List.choose (fun r ->
                                            match r with
                                            | Ok v -> Some v
                                            | Error _ -> None)
                                    BatchId = query.BatchId
                                }

                                match! registry.QueryPage(scopeId, registryQuery, cursor, limit) with
                                | Ok page ->
                                    return
                                        Ok {
                                            Outcomes = page.Artifacts |> List.map toWireOutcome
                                            NextCursor = page.NextCursor
                                        }
                                | Error(ModelRegistryError.InvalidQuery r) ->
                                    return Error(ModelExecutionRefusal.InvalidQuery r)
                                | Error e ->
                                    return Error(ModelExecutionRefusal.StorageFailure(ModelRegistryError.describe e))
                        })))

        ResolveLatestDatasetVersion =
            fun datasetId ->
                guarded (fun () ->
                    withScope (fun scopeId ->
                        withDatasets (fun datasets -> async {
                            match! datasets.GetLatest(scopeId, datasetId) with
                            | Ok v -> return Ok(toWireDatasetVersion v)
                            | Error e -> return Error(mapDatasetError $"dataset {datasetId}" e)
                        })))

        ResolveDatasetVersion =
            fun (datasetId, version) ->
                guarded (fun () ->
                    withScope (fun scopeId ->
                        withDatasets (fun datasets -> async {
                            match! datasets.GetVersion(scopeId, datasetId, version) with
                            | Ok v -> return Ok(toWireDatasetVersion v)
                            | Error e -> return Error(mapDatasetError $"dataset {datasetId}" e)
                        })))

        RequestScore =
            fun request ->
                guarded (fun () ->
                    withScope (fun scopeId -> async {
                        let! rbac = ensureWriteAllowed ()

                        match rbac with
                        | Error e -> return Error e
                        | Ok() ->
                            match service<IModelScorer> ctx with
                            | None -> return Error(ModelExecutionRefusal.SubstrateDisabled "model scorer")
                            | Some scorer ->
                                return!
                                    withRegistry (fun registry ->
                                        withDatasets (fun datasets -> async {
                                            match! registry.Get(scopeId, request.ArtifactKeyHash) with
                                            | Error ModelRegistryError.NotFound ->
                                                return
                                                    Error(
                                                        ModelExecutionRefusal.NotFound
                                                            $"artifact {request.ArtifactKeyHash}"
                                                    )
                                            | Error e ->
                                                return
                                                    Error(
                                                        ModelExecutionRefusal.StorageFailure(
                                                            ModelRegistryError.describe e
                                                        )
                                                    )
                                            | Ok artifact when
                                                policy.RefuseGateFailedArtifacts
                                                && artifact.GateVerdicts |> List.exists (fun v -> not v.Passed)
                                                ->
                                                // Phase 640 — the one place
                                                // on this face where the
                                                // gate verdicts a fit
                                                // already produced govern
                                                // what happens next. The
                                                // whole verdict list rides
                                                // the refusal, not just the
                                                // failures: a caller
                                                // deciding whether to
                                                // re-fit needs the margins
                                                // it cleared as much as the
                                                // ones it did not.
                                                return
                                                    Error(
                                                        ModelExecutionRefusal.GateFailed(
                                                            artifact.GateVerdicts |> List.map toWireGateVerdict
                                                        )
                                                    )
                                            | Ok artifact ->
                                                let scoreRequest: ScoreRequest = {
                                                    ScopeId = scopeId
                                                    Artifact = artifact
                                                    Input = {
                                                        ScopeId = scopeId
                                                        DatasetId = request.InputDatasetId
                                                        Version = request.InputVersion
                                                    }
                                                    OutputDatasetId = request.OutputDatasetId
                                                    ScoredBy = submitter
                                                }

                                                match! scorer.Score scoreRequest with
                                                | Error e ->
                                                    return
                                                        Error(ModelExecutionRefusal.ScoreRefused(mapScoreError e))
                                                | Ok outputRef ->
                                                    match!
                                                        datasets.GetVersion(
                                                            scopeId,
                                                            outputRef.DatasetId,
                                                            outputRef.Version
                                                        )
                                                    with
                                                    | Ok v -> return Ok(toWireDatasetVersion v)
                                                    | Error e ->
                                                        return
                                                            Error(
                                                                mapDatasetError
                                                                    $"scored output {outputRef.DatasetId}"
                                                                    e
                                                            )
                                        }))
                    }))
    }