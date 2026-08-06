// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 454 — IModelScorer scoring seam ──────────────────────────────
//
// Artifact + new data → predictions, landing as a NEW dataset version
// (statistical-modelling substrate plan, Stage 5). The scored output is an
// ordinary dataset vintage (Phase 448), so it is immediately queryable,
// joinable, assemblable, and chartable through the existing surfaces with
// zero new machinery.
//
// **Provider-symmetric with Phase 449.** The provider that fitted an
// artifact scores it: an `IModelScoreProvider` is resolved by the
// artifact's `ProviderId` (== the fit provider's `Kind`), exactly as the
// fit envelope resolves an `IModelFitProvider` by `ProviderKind`. The
// division of labour is identical — the provider owns the prediction math,
// forge routes + stores + audits, and never predicts (plan D1 / D10; GP 1).
//
// **Six portability rules (GP 12).**
// 1. *Identity by value* — `ArtifactRef` / `DatasetVersionRef` /
//    `ModelArtifact` are value records; `Score` takes / returns values,
//    never a live store or model handle.
// 2. *Async at every boundary* — `Predict` / `Score` return `Async<_>`.
// 3. *Behaviour as data* — the governance policy is a `ModelScorePolicy`
//    record; a refusal is a typed `ScoreError`, never an exception or a
//    caller callback.
// 4. *Stateless between calls* — a provider receives the whole request per
//    `Predict`; it caches nothing across calls (a distributed score worker
//    can be recycled between runs).
// 5. *No cross-shard ordering* — each score is independent; no ordering is
//    promised across scores.
// 6. *Precision at the lower bound* — the input is an immutable vintage
//    addressed by its exact `(scope, dataset, version)` key.

/// Typed failure of the scoring *envelope*. A refused score is data, not an
/// exception — the envelope returns `Error` and audits a
/// `ModelScoreRefused` row (GP 6). Distinct cases so the batch job handler
/// can map each to the right `JobResult` (terminal vs retryable).
[<RequireQualifiedAccess>]
type ScoreError =
    /// No `IModelScoreProvider` is registered for the artifact's
    /// `ProviderId`. Terminal — retrying will not register the provider.
    | ProviderNotFound of providerId: string
    /// The approved-only guard (task C) rejected an artifact whose lifecycle
    /// status is not `Approved` (Phase 453). Terminal under the current
    /// policy — the artifact must be promoted first. Carries the observed
    /// status name.
    | NotApproved of status: string
    /// The input dataset's schema lacks a column the provider declared it
    /// requires (`IModelScoreProvider.RequiredInputColumns`), or the provider
    /// judged the frame incompatible. Terminal — the same input will not
    /// satisfy the provider on retry.
    | InputSchemaMismatch of reason: string
    /// The input dataset version could not be read (not found, unreadable
    /// codec, transient storage failure). May recover — the job layer treats
    /// it as retryable.
    | InputUnavailable of reason: string
    /// The provider's `Predict` raised. Retryable (transient) — the message
    /// is the exception's.
    | ProviderFailed of providerId: string * message: string
    /// Writing the predictions as a new dataset version failed. Retryable.
    | StorageFailure of reason: string

module ScoreError =
    /// Human-readable one-line description for logs + job-result messages.
    let describe =
        function
        | ScoreError.ProviderNotFound k -> sprintf "no IModelScoreProvider registered for provider '%s'" k
        | ScoreError.NotApproved s -> sprintf "scoring requires Approved status; artifact is '%s'" s
        | ScoreError.InputSchemaMismatch r -> sprintf "input schema mismatch: %s" r
        | ScoreError.InputUnavailable r -> sprintf "input dataset version unavailable: %s" r
        | ScoreError.ProviderFailed(k, m) -> sprintf "score provider '%s' failed: %s" k m
        | ScoreError.StorageFailure r -> sprintf "scored-output storage failure: %s" r

/// The predictions a score provider produced for an input frame: an output
/// schema + rows. Forge lands these as a new dataset version; the provider
/// never writes to the store (GP 1 — forge routes + stores, the provider
/// only predicts). The schema is provider-defined — typically the input's
/// panel keys carried forward plus one or more prediction columns, so the
/// output joins back to the input on the keys with no new machinery.
type ScorePrediction = {
    Schema: DatasetSchema
    Rows: DatasetRow list
}

/// Phase 641 — an optional source for the opaque model specification a fit
/// read, keyed by its content hash. Forge never parses the payload (GP 1);
/// it hands the stored bytes back to the provider family that wrote them, so
/// a provider whose `Predict` needs the model's *definition* (not just its
/// fitted parameters) does not need a side channel to find it.
///
/// **Optional by construction (GP 11 / GP 13).** A deployment that composes
/// no spec store gets `ScoreContext.Spec = None` and is byte-for-byte
/// unchanged; nothing in the scoring envelope requires one. It is a separate
/// seam rather than a method on `IModelRegistry` because a spec payload is
/// not part of a governed artifact's record — the registry stores the spec
/// *hash* as identity (plan D5), and a deployment may deliberately keep the
/// payload elsewhere, or nowhere.
type IModelSpecStore =
    /// The spec whose content hash is `specHash`, within `scopeId` (GP 4 —
    /// the lookup is scoped like every other read). `None` when the store
    /// holds no spec for that hash; a storage fault is the implementation's
    /// to log, and reads as `None` here rather than failing a score that may
    /// not need the spec at all.
    abstract TryGet: scopeId: string * specHash: string -> Async<ModelSpecRef option>

/// Phase 641 — the scoped resolution context a score provider receives with
/// every `Predict` call: everything needed to *resolve the model being asked
/// for*, as values.
///
/// **Why this record exists.** Until Phase 641, `Predict` received a bare
/// `ArtifactRef` — an id + content hash + byte length — with no scope and no
/// specification. `ArtifactRef` is deliberately opaque to forge (GP 1), but
/// it is opaque to the *provider* too unless the provider can say which
/// tenant's store to read it from; a provider composed once and shared
/// across scopes therefore could not fetch its own fitted parameters. The
/// asymmetry with the fit side made it plain: `IModelFitProvider.Fit`
/// receives a whole `FitRequest` (scope + spec + vintage + seed), while
/// `IModelScoreProvider.Predict` received three loose arguments, one of them
/// a naked reference. This record restores the symmetry.
///
/// **Values only — no live handle (GP 12 rules 1 + 4).** The context carries
/// no store, no registry, and no callback. Forge *resolves* what a provider
/// might need and passes the results by value; substrate a provider needs to
/// read its own artifact bytes arrives at `create` time, per the
/// companion-authoring convention. A live handle here would be exactly the
/// violation the six portability rules exist to prevent — a distributed
/// score worker cannot be handed one.
///
/// **Scope is the caller's, always (GP 4).** `ScopeId` is taken from the
/// `ScoreRequest`, never from the artifact record's own `ScopeId`. The
/// context therefore cannot widen the scope its caller was operating in: a
/// provider handed an artifact record belonging to another tenant still sees
/// only the requesting scope, and a read it attempts under that scope
/// fails-closed rather than reaching across.
type ScoreContext = {
    /// The scope the score runs under — the caller's own (GP 4). Every read
    /// a provider performs to resolve this model is scoped by this value.
    ScopeId: string
    /// The artifact's full composite identity (plan D5): spec hash, training
    /// vintage token, seed, provider id + version, and the addressable
    /// `Hash`. `Hash` is the artifact's id within `ScopeId`, which is what a
    /// provider keys its own stored fit results on.
    CompositeKey: FitCompositeKey
    /// The opaque fitted-parameter reference — what `Predict` received
    /// alone before Phase 641. Still opaque to forge (GP 1).
    Artifact: ArtifactRef
    /// The artifact's lifecycle status at score time (Phase 453). The
    /// envelope has already applied the approved-only policy; this is here
    /// so a provider can *report* what it scored with, not re-gate it.
    Status: ModelArtifactStatus
    /// The opaque model specification the fit read, when the deployment
    /// composed an `IModelSpecStore` that holds it. `None` otherwise — a
    /// provider that needs the spec and finds `None` refuses with
    /// `ScoreError.ProviderFailed` rather than guessing, and a provider that
    /// stores its own spec keyed on `CompositeKey.Hash` never needs it.
    Spec: ModelSpecRef option
    /// The registrar's structured annotations (Phase 453), carried verbatim.
    /// Forge assigns no meaning to them; a provider family that stamped its
    /// own coordinates at registration reads them back here.
    Annotations: Map<string, string>
    /// The input vintage being scored, by value — so a provider can record
    /// what it scored against without the envelope's `ScoreRequest` leaking
    /// across the seam.
    Input: DatasetVersionRef
}

module ScoreContext =
    /// The training vintage this artifact was fitted against, recovered from
    /// the composite key's vintage token. `None` when the token is not a
    /// dataset-version key (an artifact minted outside the Phase 449
    /// envelope). This is the read that made a provider hand-roll a parser
    /// before Phase 641.
    let trainingVintage (context: ScoreContext) : DatasetVersionRef option =
        DatasetVersionRef.tryParseKey context.CompositeKey.DatasetVersion

/// A model-score provider — the execution binding that turns a fitted
/// artifact + a new vintage into predictions (plan Stage 5). Symmetric with
/// `IModelFitProvider` (Phase 449): implemented by the same companion family
/// under `src/ModelProviders/`, keyed by the same stable `Kind`, and
/// resolved by an artifact's `ProviderId`. The reference provider is a
/// trivial deterministic scorer — explicitly **not** statistics — that binds
/// the contract pack and proves the seam.
type IModelScoreProvider =
    /// Stable kind discriminator an artifact's `ProviderId` resolves against
    /// (one provider per kind — the registry rejects duplicates at compose
    /// time).
    abstract Kind: string

    /// Provider version — for observability + parity with the fit provider's
    /// composite identity. Follows the provider's own versioning policy.
    abstract ProviderVersion: string

    /// Input column names this provider requires to score. The envelope
    /// refuses (`ScoreError.InputSchemaMismatch`) an input schema missing any
    /// of them **before** the provider runs — so a provider's `Predict` never
    /// has to defend against a malformed frame it declared it needs. Empty
    /// list = no structural requirement.
    abstract RequiredInputColumns: unit -> string list

    /// Produce predictions for the decoded input frame using the fitted
    /// artifact. `context` is the Phase 641 scoped resolution context —
    /// scope, the full composite identity, the opaque artifact reference,
    /// lifecycle status, annotations, the resolved spec (when a spec store is
    /// composed) and the input vintage; `schema` + `rows` are that vintage's
    /// decoded content. `Error` is a typed refusal (e.g. the artifact is
    /// incompatible with the frame, or its parameters could not be resolved).
    ///
    /// **Determinism (plan D4).** A deterministic provider MUST return
    /// identical predictions for an identical `(context, schema, rows)` — no
    /// wall-clock, no RNG. The reference provider is fully deterministic.
    ///
    /// **Phase 641 changed this signature** from `artifact: ArtifactRef` to
    /// `context: ScoreContext`, which carries that reference as
    /// `context.Artifact`. See `docs/migrations/641-scope-carrying-predict.md`
    /// — a provider migrates by taking `context.Artifact` where it took
    /// `artifact`, and gains everything it previously had no way to obtain.
    abstract Predict:
        context: ScoreContext * schema: DatasetSchema * rows: DatasetRow list ->
            Async<Result<ScorePrediction, ScoreError>>

/// Compose-time index of registered score providers, keyed by `Kind`. Two
/// providers with the same `Kind` is a configuration error — the registry
/// throws at construction (mirrors `ModelFitProviderRegistry`, plan D2) so a
/// deployment fails to start loudly rather than dispatching to an arbitrary
/// winner.
type ModelScoreProviderRegistry(providers: IModelScoreProvider list) =
    do
        let duplicates =
            providers
            |> List.groupBy (fun p -> p.Kind)
            |> List.filter (fun (_, group) -> List.length group > 1)
            |> List.map fst

        if not (List.isEmpty duplicates) then
            failwithf
                "Multiple IModelScoreProvider implementations registered for kind(s): %s. Phase 454 permits one provider per kind."
                (String.concat ", " duplicates)

    let byKind = providers |> List.map (fun p -> p.Kind, p) |> Map.ofList

    /// Every registered provider, in registration order.
    member _.Providers: IModelScoreProvider list = providers

    /// The registered kinds (one per provider, deduplication already
    /// enforced at construction).
    member _.Kinds: string list = providers |> List.map (fun p -> p.Kind)

    /// Resolve a provider by kind (an artifact's `ProviderId`). `None` when no
    /// provider is registered — the envelope surfaces
    /// `ScoreError.ProviderNotFound`.
    member _.TryResolve(kind: string) : IModelScoreProvider option = Map.tryFind kind byKind

/// Opt-in scoring governance policy (task C). Default off (GP 13): any
/// registered artifact scores. On for governed deployments: scoring an
/// artifact whose lifecycle status is not `Approved` (Phase 453) is refused
/// with `ScoreError.NotApproved`. Kept as a record so the gate is one
/// predicate a deployment configures, not a scattered role match.
type ModelScorePolicy = {
    /// When `true`, `Score` requires the artifact's `Status` to be
    /// `Approved` (Phase 453). Default `false`.
    RequireApproved: bool
}

module ModelScorePolicy =
    /// Default — any registered artifact scores (GP 13). The value an
    /// unconfigured deployment gets.
    let permissive: ModelScorePolicy = { RequireApproved = false }

    /// Governed — scoring requires `Approved` status (Phase 453 / GP 4).
    let approvedOnly: ModelScorePolicy = { RequireApproved = true }

/// Metadata keys stamped into the output dataset version's `Metadata` map so
/// the scored vintage is self-describing — it names the artifact that
/// produced it and the input vintage it read. Because `DatasetVersion.Metadata`
/// is an existing field, provenance is queryable/joinable with zero new
/// machinery.
module ScoreProvenance =
    /// Composite-key hash of the scoring artifact (plan D5).
    [<Literal>]
    let ScoredByKey = "scoredBy"

    /// Resolved provider `Kind`.
    [<Literal>]
    let ProviderKey = "scoredByProvider"

    /// Resolved provider version.
    [<Literal>]
    let ProviderVersionKey = "scoredByProviderVersion"

    /// `{scopeId}/{datasetId}@v{version}` of the input vintage scored.
    [<Literal>]
    let InputVersionKey = "scoredFromVersion"

    /// The provenance map for the predictions dataset version produced by
    /// scoring `artifact` against `input`.
    let forOutput (artifact: ModelArtifact) (input: DatasetVersionRef) : Map<string, string> =
        Map [
            ScoredByKey, artifact.CompositeKey.Hash
            ProviderKey, artifact.CompositeKey.ProviderId
            ProviderVersionKey, artifact.CompositeKey.ProviderVersion
            InputVersionKey, DatasetVersionRef.key input
        ]

/// A request to score `Artifact` against the `Input` vintage, landing the
/// predictions under `OutputDatasetId` in `ScopeId`. The artifact carries its
/// own composite key (`ProviderId` + `Status`), so the scorer resolves the
/// provider and applies the approved-guard without a registry round-trip —
/// the caller fetched the artifact from `IModelRegistry` (Phase 453). All
/// three scopes (artifact, input, output) are the caller's team scope (GP 4).
type ScoreRequest = {
    /// Scope the score runs + writes under (GP 4 — structural isolation).
    ScopeId: string
    /// The governed artifact to score with (Phase 453). Its
    /// `CompositeKey.ProviderId` resolves the provider; its `Status` gates
    /// the approved-only policy.
    Artifact: ModelArtifact
    /// The immutable input vintage to score (Phase 448).
    Input: DatasetVersionRef
    /// Dataset id the predictions land under. A new version is created (or
    /// appended) under this id; provenance names the artifact + input.
    OutputDatasetId: string
    /// Actor recorded as the output vintage's `CreatedBy`.
    ScoredBy: string
}

/// The forge-facing scoring seam (task A). Scores a governed artifact against
/// a new dataset vintage, landing the predictions as a **new dataset version**
/// whose provenance names the artifact + input version. Provider resolved by
/// `artifact.CompositeKey.ProviderId`; forge routes + stores + audits, never
/// predicts (plan D1). `Error` is a typed, audited refusal.
type IModelScorer =
    abstract Score: request: ScoreRequest -> Async<Result<DatasetVersionRef, ScoreError>>

/// The default `IModelScorer`: resolve the provider by the artifact's
/// `ProviderId`, apply the approved-only guard, read the input vintage,
/// invoke the provider's `Predict`, land the predictions as a new dataset
/// version carrying provenance metadata, and audit the outcome. Every path
/// — success and every refusal — emits exactly one audit row (GP 6):
/// `ModelScored` on success, `ModelScoreRefused` on any `Error`.
///
/// This is the "small-frame synchronous path for interactive use" (task B);
/// `ModelScoringJobHandler` fuses the same envelope onto `IJobScheduler` for
/// batch scoring.
type ModelScorer
    (
        registry: ModelScoreProviderRegistry,
        datasets: IDatasetStore,
        audit: IAuditLog,
        policy: ModelScorePolicy,
        specs: IModelSpecStore option
    ) =

    /// Read every row of an input vintage, paging in fixed windows until the
    /// page's `TotalRows` is reached. `Error` lifts a `DatasetError` to a
    /// `ScoreError.InputUnavailable`.
    let readAllRows (input: DatasetVersionRef) : Async<Result<DatasetRow list, ScoreError>> =
        // The store caps `Limit`; page until we have collected every row.
        let pageSize = 1000

        let rec loop (offset: int64) (acc: DatasetRow list) : Async<Result<DatasetRow list, ScoreError>> = async {
            let query: DatasetPageQuery = {
                Offset = offset
                Limit = pageSize
                Filters = []
            }

            match! datasets.ReadPage(input.ScopeId, input.DatasetId, input.Version, query) with
            | Error e -> return Error(ScoreError.InputUnavailable(DatasetError.describe e))
            | Ok page ->
                let acc = acc @ page.Rows
                let read = offset + int64 (List.length page.Rows)

                if List.isEmpty page.Rows || read >= page.TotalRows then
                    return Ok acc
                else
                    return! loop read acc
        }

        loop 0L []

    /// Refuse a score: audit a `ModelScoreRefused` row (GP 6) and return the
    /// typed error. One place so every refusal path is audited identically.
    let refuse (request: ScoreRequest) (error: ScoreError) : Async<Result<DatasetVersionRef, ScoreError>> = async {
        do!
            audit.Record(
                request.ScopeId,
                ModelScoreRefused {
                    CompositeKeyHash = request.Artifact.CompositeKey.Hash
                    ProviderId = request.Artifact.CompositeKey.ProviderId
                    InputVersion = DatasetVersionRef.key request.Input
                    Reason = ScoreError.describe error
                    ScopeId = request.ScopeId
                }
            )

        return Error error
    }

    /// Phase 641 — the scoped resolution context handed to the provider.
    /// **`ScopeId` is `request.ScopeId`**, the caller's own scope, never
    /// `request.Artifact.ScopeId`: an artifact record naming another tenant
    /// cannot widen the scope a provider then reads under (GP 4). The spec
    /// lookup is scoped by the same value for the same reason.
    let contextFor (request: ScoreRequest) : Async<ScoreContext> = async {
        let! spec =
            match specs with
            | None -> async { return None }
            | Some store -> store.TryGet(request.ScopeId, request.Artifact.CompositeKey.SpecHash)

        return {
            ScopeId = request.ScopeId
            CompositeKey = request.Artifact.CompositeKey
            Artifact = request.Artifact.ArtifactRef
            Status = request.Artifact.Status
            Spec = spec
            Annotations = request.Artifact.Annotations
            Input = request.Input
        }
    }

    /// Phase 641 — the pre-641 construction shape, preserved so a deployment
    /// that composes no spec store keeps its exact call site (GP 11).
    /// Equivalent to passing `None`.
    new(registry: ModelScoreProviderRegistry, datasets: IDatasetStore, audit: IAuditLog, policy: ModelScorePolicy) =
        ModelScorer(registry, datasets, audit, policy, None)

    interface IModelScorer with
        member _.Score(request: ScoreRequest) : Async<Result<DatasetVersionRef, ScoreError>> = async {
            let providerId = request.Artifact.CompositeKey.ProviderId

            // Task C — approved-only guard. Default off (GP 13): any
            // registered artifact scores. On: a non-`Approved` artifact is a
            // typed, audited refusal.
            if
                policy.RequireApproved
                && request.Artifact.Status <> ModelArtifactStatus.Approved
            then
                return! refuse request (ScoreError.NotApproved(ModelArtifactStatus.name request.Artifact.Status))
            else
                match registry.TryResolve providerId with
                | None -> return! refuse request (ScoreError.ProviderNotFound providerId)
                | Some provider ->
                    // Read the input vintage's schema, then enforce the
                    // provider's declared required columns before it runs.
                    match!
                        datasets.GetVersion(request.Input.ScopeId, request.Input.DatasetId, request.Input.Version)
                    with
                    | Error e -> return! refuse request (ScoreError.InputUnavailable(DatasetError.describe e))
                    | Ok inputVersion ->
                        let have = inputVersion.Schema.Columns |> List.map (fun c -> c.Name) |> Set.ofList

                        let missing =
                            provider.RequiredInputColumns()
                            |> List.filter (fun c -> not (Set.contains c have))

                        if not (List.isEmpty missing) then
                            return!
                                refuse
                                    request
                                    (ScoreError.InputSchemaMismatch(
                                        sprintf
                                            "input dataset lacks required column(s): %s"
                                            (String.concat ", " missing)
                                    ))
                        else
                            match! readAllRows request.Input with
                            | Error e -> return! refuse request e
                            | Ok rows ->
                                // Phase 641 — the provider is handed the
                                // scoped resolution context, not a naked
                                // `ArtifactRef`, so it can resolve the model
                                // it is being asked to score.
                                let! context = contextFor request

                                let! predicted = async {
                                    try
                                        let! r = provider.Predict(context, inputVersion.Schema, rows)
                                        return r
                                    with ex ->
                                        return Error(ScoreError.ProviderFailed(providerId, ex.Message))
                                }

                                match predicted with
                                | Error e -> return! refuse request e
                                | Ok prediction ->
                                    // Forge lands the predictions as a new
                                    // dataset version — an ordinary vintage,
                                    // queryable/joinable with zero new
                                    // machinery — carrying provenance metadata.
                                    // Phase 482 — the predictions inherit the
                                    // input vintage's privacy-provenance labels
                                    // automatically (propagation enforced here
                                    // in the producing executor, not left to
                                    // callers), so a clean-room-derived input
                                    // carries its guarantee into the scored
                                    // output.
                                    let metadata =
                                        ScoreProvenance.forOutput request.Artifact request.Input
                                        |> DataProvenanceLabels.writeInto inputVersion.Labels

                                    match!
                                        datasets.Create(
                                            request.ScopeId,
                                            request.OutputDatasetId,
                                            prediction.Schema,
                                            prediction.Rows,
                                            request.ScoredBy,
                                            metadata,
                                            Versioned
                                        )
                                    with
                                    | Error e ->
                                        return! refuse request (ScoreError.StorageFailure(DatasetError.describe e))
                                    | Ok outputVersion ->
                                        let outputRef: DatasetVersionRef = {
                                            ScopeId = outputVersion.ScopeId
                                            DatasetId = outputVersion.DatasetId
                                            Version = outputVersion.Version
                                        }

                                        do!
                                            audit.Record(
                                                request.ScopeId,
                                                ModelScored {
                                                    CompositeKeyHash = request.Artifact.CompositeKey.Hash
                                                    ProviderId = providerId
                                                    ProviderVersion = request.Artifact.CompositeKey.ProviderVersion
                                                    InputVersion = DatasetVersionRef.key request.Input
                                                    OutputVersion = DatasetVersionRef.key outputRef
                                                    RowCount = outputVersion.RowCount
                                                    ScopeId = request.ScopeId
                                                }
                                            )

                                        return Ok outputRef
        }

module ModelScorer =
    /// Construct the default scorer over a provider registry, dataset store,
    /// audit log, and governance policy. Pass `ModelScorePolicy.permissive`
    /// for the ungoverned default (GP 13). No spec store is composed, so
    /// `ScoreContext.Spec` is `None` — unchanged from before Phase 641.
    let create
        (registry: ModelScoreProviderRegistry)
        (datasets: IDatasetStore)
        (audit: IAuditLog)
        (policy: ModelScorePolicy)
        : IModelScorer =
        ModelScorer(registry, datasets, audit, policy) :> IModelScorer

    /// Phase 641 — the same scorer with a composed `IModelSpecStore`, so
    /// every `Predict` receives the artifact's opaque specification as
    /// `ScoreContext.Spec` alongside its scope + composite identity. Additive:
    /// deployments that do not hold specs keep `create` and pay nothing
    /// (GP 13).
    let createWithSpecStore
        (registry: ModelScoreProviderRegistry)
        (datasets: IDatasetStore)
        (audit: IAuditLog)
        (policy: ModelScorePolicy)
        (specs: IModelSpecStore)
        : IModelScorer =
        ModelScorer(registry, datasets, audit, policy, Some specs) :> IModelScorer