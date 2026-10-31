// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Text
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open Fuaran.Core
open Fuaran.UI.Types
open Fuaran.UI.Ops.Types
open ToolUp.Platform.Auth
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.TeamProviderResolver

// ─── The page store ─────────────────────────────────────────────────────
//
// A module page as a stored document per layer, over the two seams the
// platform already has: `IDataObjectStore` holds the layers (versioned,
// content-hashed, scope-isolated by construction), and `IPermissionStore`
// holds the page-save grants beside the module grants. No new storage seam.
//
// One object per (module, page, layer, principal):
//
//   platform   — in the platform shard: the seed (the page's canonical wire
//                JSON), the platform layer's own op stream, the detached
//                mark and the demanded-document sidecars.
//   team       — in the team's shard: the team's op stream over the platform
//                layer.
//   individual — in the caller's own shard: the viewer's op stream over
//                their team's layer, keyed by their user id.
//
// The shard of every layer but the platform's is the caller's resolved
// scope, never a string the caller names; the platform shard is the
// platform's reserved key. A save is gated by layer (deny by default),
// checked against the fold it lands on (an op that would not apply is
// refused by name, never stored), appended to the layer's hash-chained
// stream through a compare-and-set on the object's version, and audited;
// so is every refusal.
//
// The sidecar is opaque bytes: the demanded document a later tier derives
// from the folded page and signs. This store keeps it beside the layer it
// was saved with and never reads it.

/// A module page: the module that declares it and its name within that
/// module. Both are store-key segments, so both must be safe identifiers.
type PageRef = { Module: string; Page: string }

/// The address of one stored layer: the page, the layer, and whose it is —
/// the team id of a team layer, the user id of an individual layer, the
/// platform shard's id for the platform layer.
type PageLayerKey = {
    Page: PageRef
    Layer: PageLayer
    Principal: string
}

/// A seed: the page's document as canonical wire JSON, and its demanded-
/// document sidecar.
type PageSeed = {
    /// The page's Fuaran-UI tree in its wire JSON. Stored canonically: the
    /// store decodes it and keeps the canonical encoding.
    Document: byte[]
    /// The demanded-document sidecar the seed's document carries. Opaque.
    Sidecar: byte[] option
}

/// One layer as stored.
type StoredLayer = {
    Key: PageLayerKey
    /// The stored object's version.
    Version: int
    /// The object store's content hash of the stored layer.
    ContentHash: string
    /// The platform layer's seed, canonical wire JSON. `None` on every other
    /// layer.
    Seed: string option
    /// SHA-256 of `Seed`.
    SeedHash: string option
    /// Whether the page is detached from its compiled default (platform
    /// layer only).
    Detached: bool
    /// The sidecar saved with the layer's current document.
    Sidecar: byte[] option
    /// The layer's op stream, hash-chained.
    Records: OpRecord<TreeOp<unit>> list
    /// The stream's head: the last record's hash, or the empty genesis.
    ChainHead: string
}

/// A page as it folds for one caller: the platform layer, the caller's
/// team's layer, the caller's own layer.
type FoldedPage = {
    Page: PageRef
    /// The folded document's canonical wire JSON, UTF-8.
    Document: byte[]
    /// SHA-256 (lowercase hex) of `Document` — the content hash the object
    /// store gives these bytes.
    ContentHash: string
    /// The folded document.
    Tree: Node<unit>
    /// Every op that did not apply, each naming its own act and, where one
    /// removed its target, that act too.
    Conflicts: LayerConflict list
    /// The layers folded, platform first.
    Layers: StoredLayer list
    Detached: bool
}

/// A save or reset that landed.
type PageSaveReceipt = {
    Key: PageLayerKey
    /// The stored object's version after the write.
    Version: int
    /// The layer's op-stream head after the write.
    ChainHead: string
    /// SHA-256 of the canonical JSON array of the ops appended.
    OpsHash: string
    /// SHA-256 of the document the layer now folds to for the caller.
    DocumentHash: string
}

/// What offering a seed did. A seed is written once; a stored page is never
/// re-seeded silently — re-seeding is the operator's recorded act.
[<RequireQualifiedAccess>]
type SeedOutcome =
    /// The page had no seed; this one was written.
    | Seeded of version: int * seedHash: string
    /// The stored seed is this one.
    | AlreadySeeded of seedHash: string
    /// The stored seed differs from the one offered. `Reseed` adopts it.
    | Diverged of storedSeedHash: string
    /// The page is detached: the stored platform document stands, and the
    /// compiled default is only the fallback when the store is unreachable.
    | Detached of storedSeedHash: string

/// A re-seed that landed, with the caller's page re-folded over it.
type ReseedReceipt = {
    Version: int
    SeedHash: string
    PreviousSeedHash: string
    /// The caller's page re-folded over the new seed; its conflicts are the
    /// ops of those layers that no longer apply.
    Folded: FoldedPage
}

/// A detach that landed (or found the page already detached).
type DetachReceipt = { Version: int; SeedHash: string }

/// Why the page store refused. Nothing was written.
[<RequireQualifiedAccess>]
type PageStoreError =
    /// The caller does not hold the page grant the layer needs.
    | GrantMissing of grant: PageGrant * layer: PageLayer
    /// The deployment has not enabled this layer.
    | LayerDisabled of PageLayer
    /// The caller's scope cannot hold the layer (an anonymous scope, a team
    /// layer outside a team scope, a principal that is not a safe id).
    | ScopeRefused of reason: string
    /// A module or page name is not a safe identifier.
    | InvalidPageRef of reason: string
    /// The page has no seed.
    | NotSeeded
    /// The save carried no ops.
    | NoOps
    /// An op is outside the stored vocabulary (`UpdateState`,
    /// `ReplaceRoot`).
    | OpOutsideVocabulary of opKind: string
    /// The ops do not apply to the document the layer folds to now.
    | OpsDoNotApply of conflicts: LayerConflict list
    /// The offered document is not a Fuaran-UI wire tree.
    | DocumentUndecodable of reason: string
    /// A stored layer could not be read: a broken chain, an undecodable op,
    /// a malformed object.
    | StoredLayerUnreadable of key: PageLayerKey * reason: string
    /// Another writer moved the layer between this write's read and its
    /// write. Re-read and retry.
    | StaleWrite of expected: int * actual: int
    /// The permission document could not be read; the store fails closed.
    | PermissionsUnavailable of reason: string
    /// The object store refused.
    | Storage of DataObjectError

[<RequireQualifiedAccess>]
module PageStoreError =
    /// The stable code an audit row and a caller group by.
    let code =
        function
        | PageStoreError.GrantMissing _ -> "grant-missing"
        | PageStoreError.LayerDisabled _ -> "layer-disabled"
        | PageStoreError.ScopeRefused _ -> "scope-refused"
        | PageStoreError.InvalidPageRef _ -> "invalid-page-ref"
        | PageStoreError.NotSeeded -> "not-seeded"
        | PageStoreError.NoOps -> "no-ops"
        | PageStoreError.OpOutsideVocabulary _ -> "op-outside-vocabulary"
        | PageStoreError.OpsDoNotApply _ -> "ops-do-not-apply"
        | PageStoreError.DocumentUndecodable _ -> "document-undecodable"
        | PageStoreError.StoredLayerUnreadable _ -> "stored-layer-unreadable"
        | PageStoreError.StaleWrite _ -> "stale-write"
        | PageStoreError.PermissionsUnavailable _ -> "permissions-unavailable"
        | PageStoreError.Storage _ -> "storage"

/// Who may save to the individual layer. Off unless a deployment says
/// otherwise.
[<RequireQualifiedAccess>]
type IndividualLayer =
    /// The individual layer is off: a save to it is refused by name, and a
    /// fold does not read it.
    | Disabled
    /// Every viewer may save their own layer.
    | EveryViewer
    /// Only members whose team document grants them `SaveOwn`.
    | GrantedOnly

/// The deployment's page-store policy, composed at registration.
type PageStoreOptions = {
    IndividualLayer: IndividualLayer
    /// The one team whose permission document may grant `SavePlatform` to a
    /// member who is not a platform admin. `None`: only a platform admin
    /// saves the platform layer.
    PlatformGrantTeam: string option
}

[<RequireQualifiedAccess>]
module PageStoreOptions =
    /// The individual layer off; the platform layer a platform admin's.
    let defaults: PageStoreOptions = {
        IndividualLayer = IndividualLayer.Disabled
        PlatformGrantTeam = None
    }

/// A module page as a stored document per layer, folded platform, then
/// team, then individual. Every member takes the caller's resolved scope
/// and access context: the scope is the shard the team and individual
/// layers live in, and the access context is the principal a save is
/// authorised and attributed to.
type IPageDocumentStore =
    /// Write the page's seed when it has none. A stored seed is never
    /// replaced here: a differing seed reports `Diverged`, a detached page
    /// `Detached`. Needs `SavePlatform`.
    abstract Seed:
        scope: ResolvedScope * caller: AccessContext * page: PageRef * seed: PageSeed ->
            Async<Result<SeedOutcome, PageStoreError>>

    /// Append ops to one layer's stream: the platform layer's, the caller's
    /// team's, or the caller's own. The ops must apply, in order, to the
    /// document that layer folds to now; `sidecar` is the demanded document
    /// of the result.
    abstract AppendOps:
        scope: ResolvedScope *
        caller: AccessContext *
        page: PageRef *
        layer: PageLayer *
        ops: TreeOp<unit> list *
        sidecar: byte[] option ->
            Async<Result<PageSaveReceipt, PageStoreError>>

    /// One layer as stored, for the caller. `None` when the layer holds
    /// nothing.
    abstract ReadLayer:
        scope: ResolvedScope * caller: AccessContext * page: PageRef * layer: PageLayer ->
            Async<Result<StoredLayer option, PageStoreError>>

    /// The page folded for the caller.
    abstract ReadFolded:
        scope: ResolvedScope * caller: AccessContext * page: PageRef -> Async<Result<FoldedPage, PageStoreError>>

    /// Empty one layer's stream: the overlay is removed and the layer folds
    /// to the one below it. The platform layer keeps its seed. History stays
    /// in the object's earlier versions.
    abstract ResetLayer:
        scope: ResolvedScope * caller: AccessContext * page: PageRef * layer: PageLayer ->
            Async<Result<PageSaveReceipt, PageStoreError>>

    /// Replace the seed — the operator adopting a new compiled default —
    /// clear the detached mark, and re-fold the caller's layers over it.
    /// Needs `SavePlatform`.
    abstract Reseed:
        scope: ResolvedScope * caller: AccessContext * page: PageRef * seed: PageSeed ->
            Async<Result<ReseedReceipt, PageStoreError>>

    /// Keep the stored platform document and mark the page detached, so a
    /// module release's seed is not offered as a re-seed. Needs
    /// `SavePlatform`.
    abstract Detach:
        scope: ResolvedScope * caller: AccessContext * page: PageRef -> Async<Result<DetachReceipt, PageStoreError>>

// ─── The stored object ──────────────────────────────────────────────────

/// One layer's stored body, before and after the object store.
type private LayerBody = {
    Seed: string option
    Detached: bool
    SeedSidecar: byte[] option
    Sidecar: byte[] option
    Records: OpRecord<TreeOp<unit>> list
}

module private LayerBody =
    [<Literal>]
    let Format = "toolup.page-layer/1"

    let empty: LayerBody = {
        Seed = None
        Detached = false
        SeedSidecar = None
        Sidecar = None
        Records = []
    }

    let private writeBytes (w: Utf8JsonWriter) (name: string) (bytes: byte[] option) =
        match bytes with
        | Some b -> w.WriteString(name, Convert.ToBase64String b)
        | None -> w.WriteNull name

    let encode (key: PageLayerKey) (body: LayerBody) : byte[] =
        use buffer = new MemoryStream()

        do
            use w = new Utf8JsonWriter(buffer)
            w.WriteStartObject()
            w.WriteString("format", Format)
            w.WriteString("module", key.Page.Module)
            w.WriteString("page", key.Page.Page)
            w.WriteString("layer", PageLayer.toToken key.Layer)
            w.WriteString("principal", key.Principal)

            match body.Seed with
            | Some seed ->
                w.WritePropertyName "seed"
                w.WriteRawValue(seed, skipInputValidation = false)
            | None -> ()

            w.WriteBoolean("detached", body.Detached)
            writeBytes w "seedSidecar" body.SeedSidecar
            writeBytes w "sidecar" body.Sidecar
            w.WriteString("ops", Jsonl.write PageLayers.stream.Witness body.Records)
            w.WriteEndObject()

        buffer.ToArray()

    let decode (bytes: byte[]) : Result<LayerBody, string> =
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
            let root = doc.RootElement

            let text name =
                match root.TryGetProperty(name: string) with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | _ -> None

            let bytesOf name =
                text name |> Option.map Convert.FromBase64String

            if text "format" <> Some Format then
                Error(sprintf "not a stored page layer (format %A)" (text "format"))
            else
                let seed =
                    match root.TryGetProperty "seed" with
                    | true, v when v.ValueKind = JsonValueKind.Object -> Some(v.GetRawText())
                    | _ -> None

                let detached =
                    match root.TryGetProperty "detached" with
                    | true, v when v.ValueKind = JsonValueKind.True -> true
                    | _ -> false

                let ops = text "ops" |> Option.defaultValue ""

                let records =
                    if ops = "" then
                        Ok []
                    else
                        Jsonl.readVerified PageLayers.stream ops
                        |> Result.mapError StreamLoadFault.toString

                records
                |> Result.map (fun records -> {
                    LayerBody.Seed = seed
                    Detached = detached
                    SeedSidecar = bytesOf "seedSidecar"
                    Sidecar = bytesOf "sidecar"
                    Records = records
                })
        with ex ->
            Error ex.Message

// ─── The authority ──────────────────────────────────────────────────────

/// The page-save rule, as a pure function of what the store resolved: the
/// caller's platform role, their role on the team the layer belongs to, the
/// explicit grants of that team's document, and the explicit grants of the
/// deployment's platform-grant team.
[<RequireQualifiedAccess>]
module PageAuthority =

    /// The grant `layer` needs.
    let needed (layer: PageLayer) : PageGrant =
        match layer with
        | PageLayer.Platform -> PageGrant.SavePlatform
        | PageLayer.Team -> PageGrant.SaveTeam
        | PageLayer.Individual -> PageGrant.SaveOwn

    /// Whether the caller may save to `layer`. Deny by default: nothing is
    /// granted but what a role implies or a document grants explicitly.
    /// `PlatformAdmin` satisfies `SavePlatform` and `SaveTeam` whatever any
    /// document says; a team `Owner` or `Admin` satisfies `SaveTeam` for
    /// their own team; `SaveOwn` follows the deployment's `IndividualLayer`.
    let permits
        (options: PageStoreOptions)
        (layer: PageLayer)
        (platformRole: PlatformRole option)
        (teamRole: TeamRole option)
        (teamGrants: PageGrant list)
        (platformTeamGrants: PageGrant list)
        : Result<unit, PageStoreError> =
        let admin = platformRole = Some PlatformRole.PlatformAdmin

        let refused () =
            Error(PageStoreError.GrantMissing(needed layer, layer))

        match layer with
        | PageLayer.Platform ->
            if admin || List.contains PageGrant.SavePlatform platformTeamGrants then
                Ok()
            else
                refused ()
        | PageLayer.Team ->
            match teamRole with
            | _ when admin -> Ok()
            | Some TeamRole.Owner
            | Some TeamRole.Admin -> Ok()
            | _ when List.contains PageGrant.SaveTeam teamGrants -> Ok()
            | _ -> refused ()
        | PageLayer.Individual ->
            match options.IndividualLayer with
            | IndividualLayer.Disabled -> Error(PageStoreError.LayerDisabled PageLayer.Individual)
            | IndividualLayer.EveryViewer -> Ok()
            | IndividualLayer.GrantedOnly ->
                if List.contains PageGrant.SaveOwn teamGrants then
                    Ok()
                else
                    refused ()

// ─── The default implementation ─────────────────────────────────────────

/// `IPageDocumentStore` over `IDataObjectStore` and `IPermissionStore`.
/// Stateless between calls (GP 12, rule 4): every member reads what it needs
/// from the two stores; concurrent saves to one layer are settled by the
/// object store's compare-and-set, the loser told `StaleWrite`.
type BlobPageDocumentStore
    (
        objects: IDataObjectStore,
        permissions: IPermissionStore,
        roles: TeamRoleLookup,
        audit: IAuditLog,
        options: PageStoreOptions
    ) =

    [<Literal>]
    let DataType = "toolup.page-layer"

    let platformShard = ScopeKey.scopeId ScopeKey.platform

    let ok x = async { return Ok x }
    let fail e = async { return Error e }

    let bind (f: 'a -> Async<Result<'b, PageStoreError>>) (m: Async<Result<'a, PageStoreError>>) = async {
        match! m with
        | Ok a -> return! f a
        | Error e -> return Error e
    }

    let safeId (what: string) (value: string) (refusal: string -> PageStoreError) =
        match IdentitySanitiser.sanitiseScopeId value with
        | Ok clean when clean = value -> Ok value
        | Ok _
        | Error _ -> Error(refusal (sprintf "%s '%s' is not a safe identifier" what value))

    let checkPage (page: PageRef) =
        safeId "module" page.Module PageStoreError.InvalidPageRef
        |> Result.bind (fun _ -> safeId "page" page.Page PageStoreError.InvalidPageRef)
        |> Result.map (fun _ -> page)

    let objectId (key: PageLayerKey) =
        sprintf "pages/%s/%s/%s/%s" key.Page.Module key.Page.Page (PageLayer.toToken key.Layer) key.Principal

    /// The shard and key of `layer` for this caller. The platform layer
    /// lives in the platform shard; the team layer in the caller's team
    /// scope; the individual layer in the caller's own scope, under their
    /// user id.
    let locate (scope: ResolvedScope) (caller: AccessContext) (page: PageRef) (layer: PageLayer) =
        if scope.IsAnonymous then
            Error(PageStoreError.ScopeRefused "the anonymous scope holds no page layers")
        else
            match layer with
            | PageLayer.Platform ->
                Ok(
                    platformShard,
                    {
                        PageLayerKey.Page = page
                        Layer = layer
                        Principal = platformShard
                    }
                )
            | PageLayer.Team ->
                match scope.Key, caller.TeamId with
                | Some(ScopeKey.Team teamId), Some callerTeam when teamId = callerTeam ->
                    Ok(
                        scope.ScopeId,
                        {
                            PageLayerKey.Page = page
                            Layer = layer
                            Principal = teamId
                        }
                    )
                | Some(ScopeKey.Team _), _ ->
                    Error(PageStoreError.ScopeRefused "the caller is not acting in the team the scope resolved to")
                | _ -> Error(PageStoreError.ScopeRefused "a team layer lives in a team scope")
            | PageLayer.Individual ->
                safeId "principal" caller.UserId PageStoreError.ScopeRefused
                |> Result.map (fun userId ->
                    scope.ScopeId,
                    {
                        PageLayerKey.Page = page
                        Layer = layer
                        Principal = userId
                    })

    let load (shard: string) (key: PageLayerKey) : Async<Result<StoredLayer option, PageStoreError>> = async {
        match! objects.Get(shard, objectId key) with
        | Error DataObjectError.NotFound -> return Ok None
        | Error e -> return Error(PageStoreError.Storage e)
        | Ok(meta, bytes) ->
            match LayerBody.decode bytes with
            | Error reason -> return Error(PageStoreError.StoredLayerUnreadable(key, reason))
            | Ok body ->
                return
                    Ok(
                        Some {
                            StoredLayer.Key = key
                            Version = meta.Version
                            ContentHash = meta.ContentHash
                            Seed = body.Seed
                            SeedHash = body.Seed |> Option.map PageLayers.digest
                            Detached = body.Detached
                            Sidecar = body.Sidecar
                            Records = body.Records
                            ChainHead = OpStream.head PageLayers.stream.Config body.Records
                        }
                    )
    }

    let bodyOf (stored: StoredLayer option) (seedSidecar: byte[] option) : LayerBody =
        match stored with
        | None -> LayerBody.empty
        | Some s -> {
            LayerBody.Seed = s.Seed
            Detached = s.Detached
            SeedSidecar = seedSidecar
            Sidecar = s.Sidecar
            Records = s.Records
          }

    /// The seed sidecar is not on `StoredLayer` (it is the platform layer's
    /// own bookkeeping); read it back from the object when a write must keep
    /// it.
    let seedSidecarOf (shard: string) (key: PageLayerKey) = async {
        match! objects.Get(shard, objectId key) with
        | Ok(_, bytes) ->
            match LayerBody.decode bytes with
            | Ok body -> return body.SeedSidecar
            | Error _ -> return None
        | Error _ -> return None
    }

    let write
        (shard: string)
        (key: PageLayerKey)
        (caller: AccessContext)
        (body: LayerBody)
        (expected: int)
        : Async<Result<int * string, PageStoreError>> =
        async {
            let head = OpStream.head PageLayers.stream.Config body.Records

            let metadata =
                Map.ofList [
                    "module", key.Page.Module
                    "page", key.Page.Page
                    "layer", PageLayer.toToken key.Layer
                    "principal", key.Principal
                    "chainHead", head
                ]

            match!
                ConditionalDataObjectStore.saveIfVersion
                    objects
                    shard
                    (objectId key)
                    (LayerBody.encode key body)
                    DataType
                    caller.UserId
                    metadata
                    VersioningPolicy.Versioned
                    expected
            with
            | Ok saved -> return Ok(saved.Version, head)
            | Error(ConditionalSaveError.VersionConflict(e, a)) -> return Error(PageStoreError.StaleWrite(e, a))
            | Error(ConditionalSaveError.SaveFailed e) -> return Error(PageStoreError.Storage e)
        }

    let grantsOf (teamId: string) (userId: string) : Async<Result<PageGrant list, PageStoreError>> = async {
        try
            let! doc = permissions.GetTeamPermissions teamId
            return Ok(TeamPermissions.pageGrantsFor userId doc)
        with
        | PermissionStoreUnavailableException reason -> return Error(PageStoreError.PermissionsUnavailable reason)
        | ex -> return Error(PageStoreError.PermissionsUnavailable ex.Message)
    }

    let roleOf (teamId: string) (userId: string) : Async<Result<TeamRole option, PageStoreError>> = async {
        try
            let! role = roles teamId userId
            return Ok role
        with ex ->
            return Error(PageStoreError.PermissionsUnavailable ex.Message)
    }

    /// Resolve the caller's authority over `layer` through the permission
    /// store and the team roles, and apply the rule.
    let authorise (key: PageLayerKey) (caller: AccessContext) : Async<Result<unit, PageStoreError>> =
        let teamOf () =
            match key.Layer, caller.TeamId with
            | PageLayer.Team, _ -> Some key.Principal
            | _, team -> team

        let platformTeamGrants () =
            match key.Layer, options.PlatformGrantTeam with
            | PageLayer.Platform, Some team -> grantsOf team caller.UserId
            | _ -> ok []

        let teamGrants () =
            match key.Layer, teamOf () with
            | PageLayer.Platform, _
            | _, None -> ok []
            | _, Some team -> grantsOf team caller.UserId

        let teamRole () =
            match key.Layer with
            | PageLayer.Team -> roleOf key.Principal caller.UserId
            | _ -> ok None

        platformTeamGrants ()
        |> bind (fun platformGrants ->
            teamGrants ()
            |> bind (fun grants ->
                teamRole ()
                |> bind (fun role -> async {
                    return PageAuthority.permits options key.Layer caller.PlatformRole role grants platformGrants
                })))

    /// The layers the caller's page folds: the platform layer (required),
    /// the team layer when the scope is a team's, the caller's own layer
    /// when the deployment enables it. Stops at `upTo`.
    let viewLayers
        (scope: ResolvedScope)
        (caller: AccessContext)
        (page: PageRef)
        (upTo: PageLayer)
        : Async<Result<StoredLayer * StoredLayer list, PageStoreError>> =
        let optional layer =
            if PageLayer.rank layer > PageLayer.rank upTo then
                ok None
            else
                match layer, scope.Key, options.IndividualLayer with
                | PageLayer.Team, Some(ScopeKey.Team _), _
                | PageLayer.Individual, _, (IndividualLayer.EveryViewer | IndividualLayer.GrantedOnly) ->
                    match locate scope caller page layer with
                    | Ok(shard, key) -> load shard key
                    | Error _ -> ok None
                | _ -> ok None

        match locate scope caller page PageLayer.Platform with
        | Error e -> fail e
        | Ok(shard, key) ->
            load shard key
            |> bind (fun platform ->
                match platform with
                | None -> fail PageStoreError.NotSeeded
                | Some platform ->
                    optional PageLayer.Team
                    |> bind (fun team ->
                        optional PageLayer.Individual
                        |> bind (fun own -> ok (platform, List.choose id [ Some platform; team; own ]))))

    let streamsOf (layers: StoredLayer list) : LayerStream list =
        layers
        |> List.map (fun l -> {
            LayerStream.Layer = l.Key.Layer
            Principal = l.Key.Principal
            Records = l.Records
        })

    let seedOf (platform: StoredLayer) : Result<Node<unit>, PageStoreError> =
        match platform.Seed with
        | None -> Error(PageStoreError.StoredLayerUnreadable(platform.Key, "the platform layer holds no seed"))
        | Some seed ->
            PageLayers.decodeDocument seed
            |> Result.mapError (fun reason -> PageStoreError.StoredLayerUnreadable(platform.Key, reason))

    let folded (page: PageRef) (platform: StoredLayer) (layers: StoredLayer list) =
        seedOf platform
        |> Result.map (fun seed ->
            let fold = PageLayers.fold seed (streamsOf layers)
            let text = PageLayers.encodeDocument fold.Document

            {
                FoldedPage.Page = page
                Document = Encoding.UTF8.GetBytes text
                ContentHash = PageLayers.digest text
                Tree = fold.Document
                Conflicts = fold.Conflicts
                Layers = layers
                Detached = platform.Detached
            })

    let auditScope (key: PageLayerKey) (scope: ResolvedScope) =
        match key.Layer with
        | PageLayer.Platform -> AuditScope.streamKey AuditScope.PlatformScope
        | _ -> scope.ScopeId

    let callerTrail (scope: ResolvedScope) =
        if scope.IsAnonymous then
            AuditScope.streamKey AuditScope.PlatformScope
        else
            scope.ScopeId

    /// Run a write; on refusal, record it under the caller's trail.
    let audited
        (scope: ResolvedScope)
        (caller: AccessContext)
        (page: PageRef)
        (layer: PageLayer)
        (act: string)
        (ops: TreeOp<unit> list)
        (attempt: Async<Result<'T, PageStoreError>>)
        =
        async {
            let! result = attempt

            match result with
            | Ok _ -> ()
            | Error e ->
                let missing =
                    match e with
                    | PageStoreError.GrantMissing(grant, _) -> Some(PageGrant.toToken grant)
                    | _ -> None

                do!
                    audit.Record(
                        callerTrail scope,
                        PageWriteRefused {
                            PageWriteRefusedPayload.Module = page.Module
                            Page = page.Page
                            Layer = PageLayer.toToken layer
                            ActorId = caller.UserId
                            Act = act
                            Reason = PageStoreError.code e
                            MissingGrant = missing
                            OpsHash = PageLayers.opsHash ops
                            OccurredAt = DateTimeOffset.UtcNow
                        }
                    )

            return result
        }

    let recordSaved
        (scope: ResolvedScope)
        (caller: AccessContext)
        (receipt: PageSaveReceipt)
        (act: string)
        (opCount: int)
        =
        audit.Record(
            auditScope receipt.Key scope,
            PageSaved {
                PageSavedPayload.Module = receipt.Key.Page.Module
                Page = receipt.Key.Page.Page
                Layer = PageLayer.toToken receipt.Key.Layer
                Principal = receipt.Key.Principal
                ActorId = caller.UserId
                Act = act
                OpCount = opCount
                OpsHash = receipt.OpsHash
                DocumentHash = receipt.DocumentHash
                ChainHead = receipt.ChainHead
                Version = receipt.Version
                OccurredAt = DateTimeOffset.UtcNow
            }
        )

    let decodeSeed (seed: PageSeed) : Result<string, PageStoreError> =
        try
            Encoding.UTF8.GetString seed.Document
            |> PageLayers.decodeDocument
            |> Result.map PageLayers.encodeDocument
            |> Result.mapError PageStoreError.DocumentUndecodable
        with ex ->
            Error(PageStoreError.DocumentUndecodable ex.Message)

    let platformWrite
        (scope: ResolvedScope)
        (caller: AccessContext)
        (page: PageRef)
        (then': string * PageLayerKey * StoredLayer option -> Async<Result<'T, PageStoreError>>)
        =
        match
            checkPage page
            |> Result.bind (fun page -> locate scope caller page PageLayer.Platform)
        with
        | Error e -> fail e
        | Ok(shard, key) ->
            authorise key caller
            |> bind (fun () -> load shard key)
            |> bind (fun stored -> then' (shard, key, stored))

    interface IPageDocumentStore with

        member _.Seed(scope, caller, page, seed) =
            audited scope caller page PageLayer.Platform "seed" []
            <| match decodeSeed seed with
               | Error e -> fail e
               | Ok canonical ->
                   let seedHash = PageLayers.digest canonical

                   platformWrite scope caller page (fun (shard, key, stored) ->
                       match stored with
                       | Some s when s.Detached -> ok (SeedOutcome.Detached(defaultArg s.SeedHash ""))
                       | Some s when s.SeedHash = Some seedHash -> ok (SeedOutcome.AlreadySeeded seedHash)
                       | Some s -> ok (SeedOutcome.Diverged(defaultArg s.SeedHash ""))
                       | None ->
                           let body = {
                               LayerBody.empty with
                                   Seed = Some canonical
                                   SeedSidecar = seed.Sidecar
                                   Sidecar = seed.Sidecar
                           }

                           write shard key caller body 0
                           |> bind (fun (version, _) -> async {
                               do!
                                   audit.Record(
                                       AuditScope.streamKey AuditScope.PlatformScope,
                                       PageReseeded {
                                           PageReseededPayload.Module = page.Module
                                           Page = page.Page
                                           ActorId = caller.UserId
                                           PreviousSeedHash = None
                                           SeedHash = seedHash
                                           ConflictCount = 0
                                           Version = version
                                           OccurredAt = DateTimeOffset.UtcNow
                                       }
                                   )

                               return Ok(SeedOutcome.Seeded(version, seedHash))
                           }))

        member _.AppendOps(scope, caller, page, layer, ops, sidecar) =
            audited scope caller page layer "append" ops
            <| match checkPage page with
               | Error e -> fail e
               | Ok page ->
                   match ops, ops |> List.tryPick PageLayers.outsideVocabulary with
                   | [], _ -> fail PageStoreError.NoOps
                   | _, Some kind -> fail (PageStoreError.OpOutsideVocabulary kind)
                   | _, None ->
                       match locate scope caller page layer with
                       | Error e -> fail e
                       | Ok(shard, key) ->
                           authorise key caller
                           |> bind (fun () -> viewLayers scope caller page layer)
                           |> bind (fun (platform, layers) ->
                               let below = layers |> List.filter (fun l -> l.Key.Layer <> layer)
                               let current = layers |> List.tryFind (fun l -> l.Key.Layer = layer)
                               let records = current |> Option.map _.Records |> Option.defaultValue []

                               match seedOf platform with
                               | Error e -> fail e
                               | Ok seed ->
                                   let existing = {
                                       LayerStream.Layer = layer
                                       Principal = key.Principal
                                       Records = records
                                   }

                                   let before, after =
                                       PageLayers.foldThen
                                           seed
                                           (streamsOf below @ [ existing ])
                                           layer
                                           key.Principal
                                           (List.length records)
                                           ops

                                   if not (List.isEmpty after.Conflicts) then
                                       fail (PageStoreError.OpsDoNotApply after.Conflicts)
                                   else
                                       match
                                           OpStream.appendMany
                                               PageLayers.stream
                                               (Actor.Human caller.UserId)
                                               ops
                                               before.Document
                                               records
                                       with
                                       | Error(_, refusal) ->
                                           fail (
                                               PageStoreError.OpsDoNotApply [
                                                   {
                                                       Act = {
                                                           LayerAct.Layer = layer
                                                           Principal = key.Principal
                                                           Seq = List.length records
                                                       }
                                                       Op = TreeOp.Batch ops
                                                       Cause = ConflictCause.Refused refusal
                                                   }
                                               ]
                                           )
                                       | Ok(_, appended) -> async {
                                           let! seedSidecar =
                                               match layer with
                                               | PageLayer.Platform -> seedSidecarOf shard key
                                               | _ -> async { return None }

                                           let body = {
                                               bodyOf current seedSidecar with
                                                   Records = appended
                                                   Sidecar = sidecar
                                           }

                                           let expected = current |> Option.map _.Version |> Option.defaultValue 0

                                           match! write shard key caller body expected with
                                           | Error e -> return Error e
                                           | Ok(version, head) ->
                                               let receipt = {
                                                   PageSaveReceipt.Key = key
                                                   Version = version
                                                   ChainHead = head
                                                   OpsHash = PageLayers.opsHash ops
                                                   DocumentHash =
                                                       PageLayers.digest (PageLayers.encodeDocument after.Document)
                                               }

                                               do! recordSaved scope caller receipt "append" (List.length ops)
                                               return Ok receipt
                                         })

        member _.ReadLayer(scope, caller, page, layer) =
            match checkPage page |> Result.bind (fun page -> locate scope caller page layer) with
            | Error e -> fail e
            | Ok(shard, key) -> load shard key

        member _.ReadFolded(scope, caller, page) =
            match checkPage page with
            | Error e -> fail e
            | Ok page ->
                viewLayers scope caller page PageLayer.Individual
                |> bind (fun (platform, layers) -> async { return folded page platform layers })

        member _.ResetLayer(scope, caller, page, layer) =
            audited scope caller page layer "reset" []
            <| match checkPage page |> Result.bind (fun page -> locate scope caller page layer) with
               | Error e -> fail e
               | Ok(shard, key) ->
                   authorise key caller
                   |> bind (fun () -> load shard key)
                   |> bind (fun current -> async {
                       let! seedSidecar =
                           match layer with
                           | PageLayer.Platform -> seedSidecarOf shard key
                           | _ -> async { return None }

                       let body = {
                           bodyOf current seedSidecar with
                               Records = []
                               Sidecar =
                                   match layer with
                                   | PageLayer.Platform -> seedSidecar
                                   | _ -> None
                       }

                       let expected = current |> Option.map _.Version |> Option.defaultValue 0

                       match current with
                       | None ->
                           // Nothing stored: the layer already folds to the one below.
                           return
                               Ok {
                                   PageSaveReceipt.Key = key
                                   Version = 0
                                   ChainHead = OpStream.head PageLayers.stream.Config []
                                   OpsHash = PageLayers.opsHash []
                                   DocumentHash = ""
                               }
                       | Some _ ->
                           match! write shard key caller body expected with
                           | Error e -> return Error e
                           | Ok(version, head) ->
                               let! view = viewLayers scope caller key.Page layer

                               let documentHash =
                                   match view with
                                   | Ok(platform, layers) ->
                                       match folded key.Page platform layers with
                                       | Ok page -> page.ContentHash
                                       | Error _ -> ""
                                   | Error _ -> ""

                               let receipt = {
                                   PageSaveReceipt.Key = key
                                   Version = version
                                   ChainHead = head
                                   OpsHash = PageLayers.opsHash []
                                   DocumentHash = documentHash
                               }

                               do! recordSaved scope caller receipt "reset" 0
                               return Ok receipt
                   })

        member _.Reseed(scope, caller, page, seed) =
            audited scope caller page PageLayer.Platform "reseed" []
            <| match decodeSeed seed with
               | Error e -> fail e
               | Ok canonical ->
                   let seedHash = PageLayers.digest canonical

                   platformWrite scope caller page (fun (shard, key, stored) ->
                       match stored with
                       | None -> fail PageStoreError.NotSeeded
                       | Some current ->
                           let body = {
                               bodyOf stored seed.Sidecar with
                                   Seed = Some canonical
                                   Detached = false
                                   Sidecar = seed.Sidecar
                           }

                           write shard key caller body current.Version
                           |> bind (fun (version, _) -> viewLayers scope caller key.Page PageLayer.Individual)
                           |> bind (fun (platform, layers) ->
                               match folded key.Page platform layers with
                               | Error e -> fail e
                               | Ok page -> async {
                                   do!
                                       audit.Record(
                                           AuditScope.streamKey AuditScope.PlatformScope,
                                           PageReseeded {
                                               PageReseededPayload.Module = page.Page.Module
                                               Page = page.Page.Page
                                               ActorId = caller.UserId
                                               PreviousSeedHash = current.SeedHash
                                               SeedHash = seedHash
                                               ConflictCount = List.length page.Conflicts
                                               Version = platform.Version
                                               OccurredAt = DateTimeOffset.UtcNow
                                           }
                                       )

                                   return
                                       Ok {
                                           ReseedReceipt.Version = platform.Version
                                           SeedHash = seedHash
                                           PreviousSeedHash = defaultArg current.SeedHash ""
                                           Folded = page
                                       }
                                 }))

        member _.Detach(scope, caller, page) =
            audited scope caller page PageLayer.Platform "detach" []
            <| platformWrite scope caller page (fun (shard, key, stored) ->
                match stored with
                | None -> fail PageStoreError.NotSeeded
                | Some current when current.Detached ->
                    ok {
                        DetachReceipt.Version = current.Version
                        SeedHash = defaultArg current.SeedHash ""
                    }
                | Some current -> async {
                    let! seedSidecar = seedSidecarOf shard key

                    let body = {
                        bodyOf stored seedSidecar with
                            Detached = true
                    }

                    match! write shard key caller body current.Version with
                    | Error e -> return Error e
                    | Ok(version, _) ->
                        let seedHash = defaultArg current.SeedHash ""

                        do!
                            audit.Record(
                                AuditScope.streamKey AuditScope.PlatformScope,
                                PageDetached {
                                    PageDetachedPayload.Module = key.Page.Module
                                    Page = key.Page.Page
                                    ActorId = caller.UserId
                                    SeedHash = seedHash
                                    Version = version
                                    OccurredAt = DateTimeOffset.UtcNow
                                }
                            )

                        return
                            Ok {
                                DetachReceipt.Version = version
                                SeedHash = seedHash
                            }
                  })

/// Composition for the page store.
[<RequireQualifiedAccess>]
module PageDocumentStoreCompose =

    /// The identity the page store's registration is contributed under: its
    /// companion slot, named by the interface. A composition that asks whether
    /// the page store is composed (the layout tools do) reads this, so the two
    /// sides cannot name different slots.
    let componentId: ComponentId = ComponentId.forCompanionSlot "IPageDocumentStore"

    /// Adds `IPageDocumentStore` (the `BlobPageDocumentStore` default) under
    /// `options`, over the registered `IDataObjectStore`, `IPermissionStore`
    /// and `IAuditLog`, and the team roles of the registered `ITeamStore`
    /// when there is one (none: no team role satisfies a save). Every
    /// dependency is read when the store is first resolved, never per call.
    let withPageDocumentStore (options: PageStoreOptions) (app: ServerApp) : ServerApp =
        let register (services: IServiceCollection) =
            services.AddSingleton<IPageDocumentStore>(fun (sp: IServiceProvider) ->
                let roles =
                    let teams = sp.GetService<ITeamStore>()

                    if isNull (box teams) then
                        noTeamRoles
                    else
                        teamStoreRoles teams

                BlobPageDocumentStore(
                    sp.GetRequiredService<IDataObjectStore>(),
                    sp.GetRequiredService<IPermissionStore>(),
                    roles,
                    sp.GetRequiredService<IAuditLog>(),
                    options
                )
                :> IPageDocumentStore)

        app |> ServerApp.addContribution (Contribution.service componentId register)