// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 453 — BlobModelRegistry (default IModelRegistry) ──────────────
//
// The default `IModelRegistry`, layered over `IDataObjectStore`: each model
// artifact is one `StrictlyVersioned` data object keyed by its composite-key
// hash (plan D5). The immutable fit-derived core (composite key, artifact
// blob ref, diagnostics, gate verdicts, registration provenance) is written
// at v1 and copied verbatim into every subsequent version; a lifecycle
// transition changes only `Status` and appends a version — so "lifecycle is a
// status, not a mutation" (GP 5) is enforced by the `StrictlyVersioned`
// policy, not by convention. The store holds no per-call state (rule 4): any
// instance over the same `IDataObjectStore` serves any artifact.
//
// **Distinct from `IResultStore` (plan D11).** This registry stores artifact
// *records* (identity + lifecycle + provenance) whose parameter payload lives
// in `IDataObjectStore` as an opaque blob; `IResultStore` caches module
// outputs. Different lifecycles, different keys, different consumers — the
// registry never stores a module output. See `docs/platform/which-store-for-
// what.md`.
//
// **Query index.** Each artifact's queryable coordinates (spec hash, dataset
// version, status, provider id) are mirrored into the data object's metadata
// sidecar, so `QueryBySpecHash` / `QueryByDatasetVersion` / `QueryByStatus`
// filter on `ListObjects` metadata and download the content blob only for the
// matches they materialise.
//
// **Lineage (plan Stage 4, 8a).** When an `ILineageStore` is composed,
// registration emits the artifact → dataset-version edge (`FromObjectId` =
// the vintage key the fit read, `ToObjectId` = the artifact's composite-key
// hash). Combined with the Phase 452 dataset-assembly provenance, the walk
// artifact → dataset version → assembly spec → sources exists. Best-effort:
// a failed lineage write never fails the registration (mirrors
// `PersistentResultStore`).

/// Reserved lineage `ModuleName` for the artifact → dataset-version edge the
/// registry emits (the producer label on the `LineageLink`).
module ModelRegistrySourceModule =
    [<Literal>]
    let value = "_platform.modelregistry"

/// The immutable on-blob form of a `ModelArtifact` — everything except the
/// store-assigned `Version` (recovered from the data object) and `ScopeId`
/// (structural, derived from the container). `Status` is stored as its stable
/// case-name string for wire stability across DU edits.
type private StoredModelArtifact = {
    CompositeKey: FitCompositeKey
    ArtifactRef: ArtifactRef
    Diagnostics: Map<string, float>
    GateVerdicts: GateVerdict list
    Status: string
    Annotations: Map<string, string>
    Notes: string
    /// Phase 646 — the opaque provenance attachments. **A blob written
    /// before this field existed deserialises it as `null`, not as the
    /// empty list**, so every read goes through the coercion in
    /// `toArtifact`: `[]` is not null in F#, and a downstream `List.map`
    /// over the null would throw at whatever call site happened to touch it
    /// first.
    Attachments: ProvenanceAttachment list
    Signature: ModelArtifactSignature option
    RegisteredBy: string
    RegisteredAt: DateTimeOffset
}

/// Default blob-backed `IModelRegistry` over an `IDataObjectStore`, an
/// `IAuditLog` (GP 6 lifecycle audit), and an optional `ILineageStore` (plan
/// Stage 4 provenance edge — absent when the deployment did not compose
/// lineage, in which case registration silently skips the edge). Construct
/// via `BlobModelRegistry.create` / `createWithLineage`.
type BlobModelRegistry
    (dataObjects: IDataObjectStore, audit: IAuditLog, lineage: ILineageStore option, limits: ProvenanceAttachmentLimits)
    =

    [<Literal>]
    static let DataType = "toolup.modelartifact"

    [<Literal>]
    static let SpecHashKey = "artifact.specHash"

    [<Literal>]
    static let DatasetVersionKey = "artifact.datasetVersion"

    [<Literal>]
    static let StatusKey = "artifact.status"

    [<Literal>]
    static let ProviderIdKey = "artifact.providerId"

    static let jsonOptions = FableConverters.create ()

    /// The query-index sidecar for an artifact's data object. `Status` is the
    /// only field a transition changes, so a `QueryByStatus` read never
    /// touches the content blob.
    let buildMetadata (key: FitCompositeKey) (status: ModelArtifactStatus) : Map<string, string> =
        Map [
            SpecHashKey, key.SpecHash
            DatasetVersionKey, key.DatasetVersion
            ProviderIdKey, key.ProviderId
            StatusKey, ModelArtifactStatus.name status
        ]

    let mapDataObjectError (e: DataObjectError) : ModelRegistryError =
        match e with
        | DataObjectError.NotFound -> ModelRegistryError.NotFound
        | DataObjectError.VersionNotFound v -> ModelRegistryError.StorageFailure $"artifact version {v} not found"
        | DataObjectError.DeleteForbidden -> ModelRegistryError.StorageFailure "artifact delete forbidden"
        | DataObjectError.PolicyMismatch _ -> ModelRegistryError.StorageFailure "artifact versioning policy mismatch"
        | DataObjectError.StorageFailure m -> ModelRegistryError.StorageFailure m

    /// Reconstruct a `ModelArtifact` from a data object's metadata + content
    /// bytes. An unparseable `Status` string is a storage failure (the blob
    /// was written by a future/foreign registry).
    ///
    /// **Phase 646 — attachments are hash-verified HERE, on every read.**
    /// This is the only path a `ModelArtifact` is materialised on, which is
    /// why the verification lives here rather than on a `ReadProvenance`
    /// method somebody could route around: a citation is worth something
    /// only if the bytes behind it are the bytes that were attached, and a
    /// check on one read path is a check the other paths do not run.
    let toArtifact (dobj: DataObject) (content: byte[]) : Result<ModelArtifact, ModelRegistryError> =
        try
            let stored = JsonSerializer.Deserialize<StoredModelArtifact>(content, jsonOptions)

            // A blob written before the field existed carries `null` here,
            // and F#'s `[]` is not null — coerce at the boundary rather
            // than at whichever consumer touches it first.
            let attachments =
                if isNull (box stored.Attachments) then
                    []
                else
                    stored.Attachments

            let corrupt = attachments |> List.tryFind (ProvenanceAttachment.hashVerified >> not)

            match ModelArtifactStatus.parse stored.Status with
            | None -> Error(ModelRegistryError.StorageFailure $"unknown artifact status '{stored.Status}'")
            | Some status ->
                match corrupt with
                | Some attachment ->
                    Error(
                        ModelRegistryError.AttachmentRefused(
                            ProvenanceAttachmentRefusal.HashMismatch(
                                (if isNull (box attachment.ContentHash) then
                                     ""
                                 else
                                     attachment.ContentHash),
                                ProvenanceAttachment.computedHash attachment
                            )
                        )
                    )
                | None ->
                    Ok {
                        CompositeKey = stored.CompositeKey
                        ScopeId = dobj.ScopeId
                        ArtifactRef = stored.ArtifactRef
                        Diagnostics = stored.Diagnostics
                        GateVerdicts = stored.GateVerdicts
                        Status = status
                        Annotations = stored.Annotations
                        Notes = stored.Notes
                        Attachments = attachments
                        Signature = stored.Signature
                        RegisteredBy = stored.RegisteredBy
                        RegisteredAt = stored.RegisteredAt
                        Version = dobj.Version
                    }
        with ex ->
            Error(ModelRegistryError.StorageFailure $"artifact record is unreadable: {ex.Message}")

    /// The stored form of an artifact this registry already materialised —
    /// the immutable core carried verbatim, with only what the caller is
    /// changing substituted (GP 5).
    let toStored (artifact: ModelArtifact) (status: ModelArtifactStatus) : StoredModelArtifact = {
        CompositeKey = artifact.CompositeKey
        ArtifactRef = artifact.ArtifactRef
        Diagnostics = artifact.Diagnostics
        GateVerdicts = artifact.GateVerdicts
        Status = ModelArtifactStatus.name status
        Annotations = artifact.Annotations
        Notes = artifact.Notes
        Attachments = artifact.Attachments
        Signature = artifact.Signature
        RegisteredBy = artifact.RegisteredBy
        RegisteredAt = artifact.RegisteredAt
    }

    /// Persist a stored artifact under its composite-key hash. `StrictlyVersioned`
    /// so registration + every transition is an immutable, append-only
    /// version (GP 5).
    let save (scopeId: string) (stored: StoredModelArtifact) (status: ModelArtifactStatus) = async {
        let objectId = stored.CompositeKey.Hash
        let content = JsonSerializer.SerializeToUtf8Bytes(stored, jsonOptions)
        let meta = buildMetadata stored.CompositeKey status

        match!
            dataObjects.Save(
                scopeId,
                objectId,
                content,
                DataType,
                stored.RegisteredBy,
                meta,
                VersioningPolicy.StrictlyVersioned
            )
        with
        | Ok dobj -> return toArtifact dobj content
        | Error e -> return Error(mapDataObjectError e)
    }

    /// Best-effort artifact → dataset-version lineage edge (plan Stage 4).
    /// Skipped when no `ILineageStore` is composed; a store-write failure is
    /// swallowed so it never fails the registration.
    let recordLineage (scopeId: string) (key: FitCompositeKey) = async {
        match lineage with
        | None -> return ()
        | Some store ->
            let link = {
                LinkId = Guid.NewGuid()
                FromObjectId = key.DatasetVersion
                ToObjectId = key.Hash
                ModuleName = ModelRegistrySourceModule.value
                LinkType = Derived
                Timestamp = DateTime.UtcNow
            }

            let! _ = store.Record(scopeId, link)
            return ()
    }

    /// Materialise every artifact in the scope whose metadata sidecar
    /// satisfies `sidecarMatches`, downloading the content blob only for the
    /// matches. Undecodable matches are skipped (a corrupt blob does not fail
    /// the whole query).
    let queryBy (scopeId: string) (sidecarMatches: Map<string, string> -> bool) = async {
        let! objs = dataObjects.ListObjects scopeId

        let candidates =
            objs
            |> List.filter (fun d -> d.DataType = DataType && sidecarMatches d.Metadata)

        let results = ResizeArray<ModelArtifact>()

        for dobj in candidates do
            match! dataObjects.GetContent(scopeId, dobj.ContentHash) with
            | Ok bytes ->
                match toArtifact dobj bytes with
                | Ok artifact -> results.Add artifact
                | Error _ -> ()
            | Error _ -> ()

        return List.ofSeq results
    }

    interface IModelRegistry with
        member _.Register(scopeId, outcome, registeredBy, annotations, notes) = async {
            let objectId = outcome.CompositeKey.Hash

            // Idempotency (plan D5 / acceptance): an existing composite key
            // returns the existing artifact unchanged — no new version, no
            // audit row (nothing changed).
            let! existing = dataObjects.ListVersions(scopeId, objectId)

            if not (List.isEmpty existing) then
                match! dataObjects.Get(scopeId, objectId) with
                | Ok(dobj, bytes) -> return toArtifact dobj bytes
                | Error e -> return Error(mapDataObjectError e)
            else
                let status = ModelArtifactStatus.initial

                let stored = {
                    CompositeKey = outcome.CompositeKey
                    ArtifactRef = outcome.ArtifactRef
                    Diagnostics = outcome.Diagnostics
                    GateVerdicts = outcome.GateVerdicts
                    Status = ModelArtifactStatus.name status
                    Annotations = annotations
                    Notes = notes
                    // Phase 646 — an artifact is born with no attachments
                    // and no acceptance signature. Both arrive later, by
                    // an explicit act, which is what keeps registration
                    // byte-for-byte what it was (GP 11).
                    Attachments = []
                    Signature = None
                    RegisteredBy = registeredBy
                    RegisteredAt = DateTimeOffset.UtcNow
                }

                match! save scopeId stored status with
                | Error e -> return Error e
                | Ok artifact ->
                    do! recordLineage scopeId outcome.CompositeKey

                    do!
                        audit.Record(
                            scopeId,
                            ModelArtifactRegistered {
                                CompositeKeyHash = artifact.CompositeKey.Hash
                                SpecHash = artifact.CompositeKey.SpecHash
                                DatasetVersion = artifact.CompositeKey.DatasetVersion
                                ProviderId = artifact.CompositeKey.ProviderId
                                ProviderVersion = artifact.CompositeKey.ProviderVersion
                                Status = ModelArtifactStatus.name artifact.Status
                                RegisteredBy = registeredBy
                                ScopeId = scopeId
                            }
                        )

                    return Ok artifact
        }

        member _.Get(scopeId, keyHash) = async {
            match! dataObjects.Get(scopeId, keyHash) with
            | Ok(dobj, bytes) -> return toArtifact dobj bytes
            | Error DataObjectError.NotFound -> return Error ModelRegistryError.NotFound
            | Error e -> return Error(mapDataObjectError e)
        }

        member _.QueryBySpecHash(scopeId, specHash) =
            queryBy scopeId (fun m -> Map.tryFind SpecHashKey m = Some specHash)

        member _.QueryByDatasetVersion(scopeId, datasetVersion) =
            queryBy scopeId (fun m -> Map.tryFind DatasetVersionKey m = Some datasetVersion)

        member _.QueryByStatus(scopeId, status) =
            let target = ModelArtifactStatus.name status
            queryBy scopeId (fun m -> Map.tryFind StatusKey m = Some target)

        member _.QueryPage(scopeId, query, cursor, limit) = async {
            // Phase 599 — sidecar pre-filter on the indexed coordinates
            // (spec hash / dataset version / status), then the
            // annotation-level batch filter + deterministic paging over the
            // materialised matches via the shared pure helpers (so paging
            // semantics cannot drift from a companion implementation). The
            // in-process default materialises the match set per call — the
            // same cost class as the existing query methods; a distributed
            // companion pushes the filters down instead.
            if limit <= 0 then
                return Error(ModelRegistryError.InvalidQuery "limit must be positive")
            else
                let sidecarMatches (m: Map<string, string>) =
                    (List.isEmpty query.SpecHashes
                     || (match Map.tryFind SpecHashKey m with
                         | Some v -> List.contains v query.SpecHashes
                         | None -> false))
                    && (List.isEmpty query.DatasetVersions
                        || (match Map.tryFind DatasetVersionKey m with
                            | Some v -> List.contains v query.DatasetVersions
                            | None -> false))
                    && (List.isEmpty query.Statuses
                        || (match Map.tryFind StatusKey m |> Option.bind ModelArtifactStatus.parse with
                            | Some s -> List.contains s query.Statuses
                            | None -> false))

                let! candidates = queryBy scopeId sidecarMatches
                let matched = candidates |> List.filter (ModelRegistryQuery.matches query)
                return Ok(ModelArtifactPage.page cursor limit matched)
        }

        member _.TransitionStatus(scopeId, keyHash, target, callerRole, actorUserId) = async {
            match! dataObjects.Get(scopeId, keyHash) with
            | Error DataObjectError.NotFound -> return Error ModelRegistryError.NotFound
            | Error e -> return Error(mapDataObjectError e)
            | Ok(dobj, bytes) ->
                match toArtifact dobj bytes with
                | Error e -> return Error e
                | Ok current ->
                    let from = current.Status

                    let auditDenied (reason: string) = async {
                        do!
                            audit.Record(
                                scopeId,
                                ModelArtifactTransitionDenied {
                                    CompositeKeyHash = keyHash
                                    AttemptedStatus = ModelArtifactStatus.name target
                                    ActorUserId = actorUserId
                                    Reason = reason
                                    ScopeId = scopeId
                                }
                            )
                    }

                    // Legality first — an illegal edge is refused regardless
                    // of role. Then the GP 4 role gate on `Approved`.
                    if not (ModelArtifactStatus.canTransition from target) then
                        do!
                            auditDenied
                                $"illegal transition {ModelArtifactStatus.name from} → {ModelArtifactStatus.name target}"

                        return Error(ModelRegistryError.IllegalTransition(from, target))
                    elif
                        ModelArtifactStatus.requiresElevatedRole target
                        && not (TeamRoles.canWriteTeamConfig callerRole)
                    then
                        do! auditDenied "requires Owner/Admin"
                        return Error(ModelRegistryError.Forbidden "approving a model artifact requires Owner/Admin")
                    else
                        let stored = toStored current target

                        match! save scopeId stored target with
                        | Error e -> return Error e
                        | Ok updated ->
                            do!
                                audit.Record(
                                    scopeId,
                                    ModelArtifactTransitioned {
                                        CompositeKeyHash = keyHash
                                        FromStatus = ModelArtifactStatus.name from
                                        ToStatus = ModelArtifactStatus.name target
                                        ActorUserId = actorUserId
                                        ScopeId = scopeId
                                    }
                                )

                            return Ok updated
        }

        member _.AttachmentLimits = limits

        member _.AttachProvenance(scopeId, keyHash, attachments, signature) = async {
            match! dataObjects.Get(scopeId, keyHash) with
            | Error DataObjectError.NotFound -> return Error ModelRegistryError.NotFound
            | Error e -> return Error(mapDataObjectError e)
            | Ok(dobj, bytes) ->
                match toArtifact dobj bytes with
                | Error e -> return Error e
                | Ok current ->
                    let arriving = if isNull (box attachments) then [] else attachments

                    // The shared pure judge, so a companion registry and
                    // the federated transfer seam cannot come to different
                    // conclusions about the same set.
                    match ProvenanceAttachments.validate limits current.Attachments arriving with
                    | Error refusal -> return Error(ModelRegistryError.AttachmentRefused refusal)
                    | Ok() ->
                        let appended = ProvenanceAttachments.append current.Attachments arriving
                        let novel = ProvenanceAttachments.novel current.Attachments arriving

                        // Nothing new and no signature to record: the whole
                        // call is a replay. Writing a version anyway would
                        // make an idempotent re-send indistinguishable from
                        // a real append in the version history, which is
                        // the one place an investigation counts them.
                        if List.isEmpty novel && Option.isNone signature then
                            return Ok current
                        else
                            let stored = {
                                toStored current current.Status with
                                    Attachments = appended
                                    Signature =
                                        match signature with
                                        | Some _ -> signature
                                        | None -> current.Signature
                            }

                            match! save scopeId stored current.Status with
                            | Error e -> return Error e
                            | Ok updated ->
                                do!
                                    audit.Record(
                                        scopeId,
                                        ModelArtifactProvenanceAttached {
                                            CompositeKeyHash = keyHash
                                            AttachmentHashes = novel |> List.map _.ContentHash
                                            MediaTypes = novel |> List.map _.MediaType |> List.distinct
                                            TotalAttachments = List.length appended
                                            TotalBytes = appended |> List.sumBy ProvenanceAttachment.byteLength
                                            SigningKeyId =
                                                updated.Signature |> Option.map _.SigningKeyId |> Option.defaultValue ""
                                            ScopeId = scopeId
                                        }
                                    )

                                return Ok updated
        }

module BlobModelRegistry =
    /// The default blob-backed model registry — no lineage edge emitted
    /// (compose `createWithLineage` to wire the artifact → dataset-version
    /// provenance edge when an `ILineageStore` is present).
    let create (dataObjects: IDataObjectStore) (audit: IAuditLog) : IModelRegistry =
        BlobModelRegistry(dataObjects, audit, None, ProvenanceAttachmentLimits.default') :> IModelRegistry

    /// A blob-backed model registry that emits the artifact → dataset-version
    /// lineage edge on registration (plan Stage 4).
    let createWithLineage (dataObjects: IDataObjectStore) (audit: IAuditLog) (lineage: ILineageStore) : IModelRegistry =
        BlobModelRegistry(dataObjects, audit, Some lineage, ProvenanceAttachmentLimits.default') :> IModelRegistry

    /// Phase 646 — the same registry at a deployment-declared provenance
    /// attachment cap. The cap is a deployment's decision (a data host
    /// serving a consortium's exploration records wants a different one
    /// from a single-tenant install), and declaring it is what lets a
    /// counterparty size a transfer before sending it.
    let createWithLimits
        (dataObjects: IDataObjectStore)
        (audit: IAuditLog)
        (lineage: ILineageStore option)
        (limits: ProvenanceAttachmentLimits)
        : IModelRegistry =
        BlobModelRegistry(dataObjects, audit, lineage, limits) :> IModelRegistry