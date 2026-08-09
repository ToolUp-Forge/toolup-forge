// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 453 — IModelRegistry seam (+ Phase 599 bulk reads) ───────────
//
// The read/write surface over governed model artifacts (plan Stage 4). An
// evidence-base consumer builds on the query methods — forge provides the
// composite-key structure (plan D5), not the meta-analysis. Registration is
// idempotent under the composite key (registering the same fit twice returns
// the existing artifact, never a duplicate); every lifecycle transition is
// gated + audited (GP 4 / GP 6). Distinct from `IResultStore` — the registry
// never stores module outputs (plan D11).
//
// **Six portability rules (GP 12).**
// 1. *Identity by value* — scopes / hashes / statuses are strings + value
//    records; the caller's role is passed as a `TeamRole` value, never a
//    live principal handle. A `ModelArtifact` is a value.
// 2. *Async at every boundary* — every method returns `Async<_>`.
// 3. *Behaviour as data* — the transition target + caller role are values;
//    a denied transition is a `ModelRegistryError`, never an exception.
// 4. *Stateless between calls* — every call carries its full scope + key;
//    the registry derives everything from `IDataObjectStore` (itself
//    restart-survivable) and caches nothing across calls.
// 5. *No cross-shard ordering* — artifacts are independent; no ordering is
//    promised across artifacts or scopes. A single artifact's lifecycle
//    versions are linear within its `(scopeId, keyHash)` shard.
// 6. *Precision at the lower bound* — timestamps are `DateTimeOffset`;
//    identity is the exact composite key (no approximate matching).

/// Phase 599 — a multi-key registry filter, expressed as data (GP 12 rule
/// 3). Every field is conjunctive; an empty list / `None` means "any". The
/// evidence-base sync read: "all artifacts for this `specHash` set", "all
/// artifacts on this vintage set", "everything from batch X" are each one
/// query value.
type ModelRegistryQuery = {
    /// Match artifacts whose `SpecHash` is in this set (empty = any).
    SpecHashes: string list
    /// Match artifacts whose `DatasetVersion` key is in this set (empty =
    /// any).
    DatasetVersions: string list
    /// Match artifacts currently in one of these statuses (empty = any).
    Statuses: ModelArtifactStatus list
    /// Match artifacts whose registration annotations carry
    /// `FitRequestBatch.BatchIdAnnotationKey = value` — the bulk-outcome
    /// read for one submitted batch. `None` = any.
    BatchId: string option
}

module ModelRegistryQuery =
    /// The match-anything query (page through a whole scope's artifacts).
    let any: ModelRegistryQuery = {
        SpecHashes = []
        DatasetVersions = []
        Statuses = []
        BatchId = None
    }

    /// Whether one artifact satisfies the query (pure — shared by the
    /// default registry and any companion so paging semantics cannot
    /// drift between implementations).
    let matches (query: ModelRegistryQuery) (artifact: ModelArtifact) : bool =
        (List.isEmpty query.SpecHashes
         || List.contains artifact.CompositeKey.SpecHash query.SpecHashes)
        && (List.isEmpty query.DatasetVersions
            || List.contains artifact.CompositeKey.DatasetVersion query.DatasetVersions)
        && (List.isEmpty query.Statuses || List.contains artifact.Status query.Statuses)
        && (match query.BatchId with
            | None -> true
            | Some batchId -> Map.tryFind FitRequestBatch.BatchIdAnnotationKey artifact.Annotations = Some batchId)

/// Phase 599 — one page of a cursor-paginated registry read.
type ModelArtifactPage = {
    /// The page's artifacts, ordered by composite-key hash (ordinal
    /// ascending) — a stable, value-derived order, so the same cursor walk
    /// always yields the same sequence.
    Artifacts: ModelArtifact list
    /// Opaque resume token (the last artifact's composite-key hash).
    /// `None` when this page is the last.
    NextCursor: string option
}

module ModelArtifactPage =
    /// The deterministic paging function over a materialised match set —
    /// pure and shared (like `ModelRegistryQuery.matches`) so every
    /// implementation pages identically: ordinal-ascending by composite-key
    /// hash, resuming strictly after `cursor`.
    let page (cursor: string option) (limit: int) (matched: ModelArtifact list) : ModelArtifactPage =
        let ordered =
            matched
            |> List.sortWith (fun a b -> System.String.CompareOrdinal(a.CompositeKey.Hash, b.CompositeKey.Hash))

        let resumed =
            match cursor with
            | None -> ordered
            | Some c ->
                ordered
                |> List.filter (fun a -> System.String.CompareOrdinal(a.CompositeKey.Hash, c) > 0)

        let pageItems = resumed |> List.truncate limit

        let nextCursor =
            if List.length resumed > limit then
                pageItems |> List.tryLast |> Option.map (fun a -> a.CompositeKey.Hash)
            else
                None

        {
            Artifacts = pageItems
            NextCursor = nextCursor
        }

/// The governed model-artifact registry (plan Stage 4). Every method is
/// team-scoped by `scopeId` (GP 4 — a structural partition inherited from
/// `IDataObjectStore`, not a post-filter). A new lifecycle state is a new
/// version, never an in-place mutation of the artifact's fit-derived core
/// (GP 5).
type IModelRegistry =
    /// Register an artifact from a completed `FitOutcome` under `scopeId`.
    /// The artifact is born `Fitted` and keyed by the outcome's composite key
    /// (plan D5). **Idempotent**: registering an outcome whose composite key
    /// already exists in the scope returns the existing artifact unchanged —
    /// a re-register is not a new artifact and does not append a version. A
    /// changed seed / vintage / provider yields a *different* composite key
    /// and therefore a distinct artifact. `annotations` + `notes` are the
    /// registrar's structured + free-text metadata.
    abstract Register:
        scopeId: string * outcome: FitOutcome * registeredBy: string * annotations: Map<string, string> * notes: string ->
            Async<Result<ModelArtifact, ModelRegistryError>>

    /// Fetch one artifact by its composite-key hash. `NotFound` when no
    /// artifact with that hash exists in the scope. Returns the latest
    /// lifecycle version.
    abstract Get: scopeId: string * keyHash: string -> Async<Result<ModelArtifact, ModelRegistryError>>

    /// Every artifact in the scope whose fit read the same opaque model spec
    /// (matching `SpecHash`) — all vintages of one modelling decision (plan
    /// Stage 4). Empty list when none match. The read surface a branch ×
    /// vintage evidence base walks.
    abstract QueryBySpecHash: scopeId: string * specHash: string -> Async<ModelArtifact list>

    /// Every artifact in the scope fit against the same dataset vintage
    /// (matching `DatasetVersion`, the `{scopeId}/{datasetId}@v{version}`
    /// key). All models on one vintage. Empty list when none match.
    abstract QueryByDatasetVersion: scopeId: string * datasetVersion: string -> Async<ModelArtifact list>

    /// Every artifact in the scope currently in `status`. Empty list when
    /// none match.
    abstract QueryByStatus: scopeId: string * status: ModelArtifactStatus -> Async<ModelArtifact list>

    /// Phase 599 — cursor-paginated multi-key read (the evidence-base sync
    /// path + bulk outcome retrieval). Filters are `ModelRegistryQuery`
    /// data; ordering is deterministic (composite-key hash, ordinal
    /// ascending — `ModelArtifactPage.page`), so the same cursor walk
    /// always yields the same sequence. `limit` must be positive
    /// (`InvalidQuery` otherwise); a `cursor` from a previous page resumes
    /// strictly after it.
    abstract QueryPage:
        scopeId: string * query: ModelRegistryQuery * cursor: string option * limit: int ->
            Async<Result<ModelArtifactPage, ModelRegistryError>>

    /// Transition an artifact to `target`. Gated (GP 4): a transition into
    /// `Approved` requires `callerRole` to satisfy
    /// `TeamRoles.canWriteTeamConfig` (Owner/Admin) — a Member is refused
    /// with `Forbidden`. An edge the lifecycle graph forbids (or a
    /// self-transition) is `IllegalTransition`. On success the artifact gains
    /// a new immutable version with the advanced `Status` (GP 5); every
    /// transition — allowed or denied — is audited (GP 6). `actorUserId` is
    /// recorded on the audit trail.
    abstract TransitionStatus:
        scopeId: string * keyHash: string * target: ModelArtifactStatus * callerRole: TeamRole * actorUserId: string ->
            Async<Result<ModelArtifact, ModelRegistryError>>

    /// Phase 646 — append opaque provenance attachments to an artifact, and
    /// (optionally) record the acceptance signature over it.
    ///
    /// **Append-only**: nothing already attached is dropped or replaced,
    /// and an attachment whose content hash is already held is a no-op
    /// rather than a duplicate — which is what makes a re-sent promotion
    /// transfer idempotent without a separate idempotency store. The
    /// arriving set is validated by the shared pure
    /// `ProvenanceAttachments.validate` against `AttachmentLimits`, so a
    /// companion implementation cannot admit a set the default refuses:
    /// a declared digest that does not recompute, or a set over the cap,
    /// is `AttachmentRefused` and nothing is written.
    ///
    /// `signature` is `None` on a local append and `Some` on an accepted
    /// promotion. Recording one twice with different content is not
    /// possible in the honest flow (the signing input is a function of the
    /// artifact's identity + attachment set), so a later signature simply
    /// replaces the stored one; the versioned write keeps the earlier
    /// record readable (GP 5).
    ///
    /// Like every other write here, the result is a new immutable version
    /// of the artifact record; `NotFound` when the scope holds no artifact
    /// with that hash.
    abstract AttachProvenance:
        scopeId: string *
        keyHash: string *
        attachments: ProvenanceAttachment list *
        signature: ModelArtifactSignature option ->
            Async<Result<ModelArtifact, ModelRegistryError>>

    /// Phase 646 — the attachment cap this registry enforces, DECLARED
    /// rather than discovered.
    ///
    /// A caller sizes a transfer against this before sending it, and a
    /// federated seam publishes it to a counterparty. A bound a caller can
    /// only learn by exceeding it is a bound that turns every large
    /// transfer into a round trip and a refusal.
    abstract AttachmentLimits: ProvenanceAttachmentLimits