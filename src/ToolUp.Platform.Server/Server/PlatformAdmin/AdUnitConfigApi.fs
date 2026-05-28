// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AdUnitConfigApi

open System
open Giraffe
open Microsoft.AspNetCore.Http
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore

// ─── Phase 61 — AdUnit CRUD admin endpoint ────────────────────────
//
//   GET    /api/_platform/admin/ad-units            — list every slot config
//   GET    /api/_platform/admin/ad-units/{slotId}   — read one
//   POST   /api/_platform/admin/ad-units            — create (body: AdSlotConfig)
//   PUT    /api/_platform/admin/ad-units/{slotId}   — update (body: AdSlotConfig)
//   DELETE /api/_platform/admin/ad-units/{slotId}   — delete
//
// Gated to Platform-Admin role only (Phase 4b). Audit emission on
// every write under `_platform.ads.config`. Mounted only when
// `ServerConfig.EntityStore = EnabledEntityStore`; the default
// `NoEntityStore` skips the route entirely so disabled deployments
// 404 from the Giraffe terminal middleware.
//
// **Storage shape.** `AdSlotConfig` (in `SDK.Shared.fs`) doesn't
// carry the `Id` / `Type` / `Version` fields `IEntityStore` requires.
// The handler wraps it in an internal `AdSlotEntity` record at the
// persistence boundary — clients see the public `AdSlotConfig`
// shape; the store sees `AdSlotEntity`. The entity type is
// registered lazily on first request because the SDK compose root
// (`SDK.Server.fs`) is out of this phase's scope; `EntityRegistry`'s
// `Register` is idempotent under concurrent access (overwrites a
// same-keyed entry in `ConcurrentDictionary`).

[<Literal>]
let private PlatformAdsConfigScope = "_platform.ads.config"

[<Literal>]
let private AdSlotEntityType = "AdSlotConfig"

[<Literal>]
let private SlotIdIndex = "SlotId"

/// Internal storage entity wrapping `AdSlotConfig`. `Id` is the
/// `SlotId`, which is the natural key (AdSense slot identifier);
/// `Type` matches the registered entity-type name; `Version` is
/// assigned by `IEntityStore.Save`.
type AdSlotEntity = {
    Id: EntityId
    Type: string
    Version: int
    AdClientId: string
    SlotId: string
    Format: AdFormat
    Style: AdStyleHint option
}

module private AdSlotEntity =
    let fromConfig (config: AdSlotConfig) : AdSlotEntity = {
        Id = config.SlotId
        Type = AdSlotEntityType
        Version = 0
        AdClientId = config.AdClientId
        SlotId = config.SlotId
        Format = config.Format
        Style = config.Style
    }

    let toConfig (entity: AdSlotEntity) : AdSlotConfig = {
        AdClientId = entity.AdClientId
        SlotId = entity.SlotId
        Format = entity.Format
        Style = entity.Style
    }

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private resolveAccessContext (ctx: HttpContext) (mode: PlatformMode) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (Subject.fromLegacyMode mode userId None)

let private resolveEntityStore (ctx: HttpContext) : IEntityStore option =
    match ctx.RequestServices.GetService(typeof<IEntityStore>) with
    | :? IEntityStore as store -> Some store
    | _ -> None

let private resolveRegistry (ctx: HttpContext) : EntityRegistry option =
    match ctx.RequestServices.GetService(typeof<EntityRegistry>) with
    | :? EntityRegistry as r -> Some r
    | _ -> None

let private resolveAuditLog (ctx: HttpContext) : IAuditLog option =
    match ctx.RequestServices.GetService(typeof<IAuditLog>) with
    | :? IAuditLog as log -> Some log
    | _ -> None

/// Idempotent entity-type registration. Calling on every request is
/// cheap because `EntityRegistry.Register` is a `ConcurrentDictionary`
/// overwrite on a same-shape registration. We declare one index on
/// `SlotId` so a future "find by slot id" lookup path is index-backed
/// rather than scanning every version.
let private ensureRegistered (registry: EntityRegistry) : unit =
    let registration =
        EntityRegistration.create<AdSlotEntity> AdSlotEntityType
        |> EntityRegistration.withIndex SlotIdIndex (fun e -> e.SlotId)

    registry.Register registration

let private writeJson (ctx: HttpContext) (statusCode: int) (payload: 'T) : HttpFuncResult = task {
    let json = JsonConvert.SerializeObject(payload, jsonSettings)
    ctx.Response.StatusCode <- statusCode
    ctx.Response.ContentType <- "application/json; charset=utf-8"
    return! ctx.WriteTextAsync json
}

let private writeError (ctx: HttpContext) (statusCode: int) (message: string) : HttpFuncResult = task {
    ctx.Response.StatusCode <- statusCode
    return! ctx.WriteTextAsync message
}

let private ensurePlatformAdmin (mode: PlatformMode) (ctx: HttpContext) : Result<AccessContext, string> =
    let accessContext = resolveAccessContext ctx mode

    if AccessContext.canModifyPlatformConfig accessContext then
        Ok accessContext
    else
        Error "platform admin role required"

let private readBody (ctx: HttpContext) : System.Threading.Tasks.Task<string> = task {
    use reader = new System.IO.StreamReader(ctx.Request.Body)
    return! reader.ReadToEndAsync()
}

let private tryDeserialise<'T> (body: string) : 'T option =
    if String.IsNullOrWhiteSpace body then
        None
    else
        try
            Some(JsonConvert.DeserializeObject<'T>(body, jsonSettings))
        with _ ->
            None

let private recordAudit (ctx: HttpContext) (event: AuditEvent) : unit =
    resolveAuditLog ctx
    |> Option.iter (fun log -> log.Record(PlatformAdsConfigScope, event) |> Async.Start)

let private withSubstrate
    (mode: PlatformMode)
    (ctx: HttpContext)
    (work: AccessContext -> IEntityStore -> EntityRegistry -> HttpFuncResult)
    : HttpFuncResult =
    task {
        match ensurePlatformAdmin mode ctx with
        | Error msg -> return! writeError ctx 403 msg
        | Ok ac ->
            match resolveEntityStore ctx, resolveRegistry ctx with
            | Some store, Some registry ->
                ensureRegistered registry
                return! work ac store registry
            | _ -> return! writeError ctx 503 "entity store substrate not configured"
    }

let private listHandler (mode: PlatformMode) : HttpHandler =
    fun _next (ctx: HttpContext) ->
        withSubstrate mode ctx (fun _ac store _registry -> task {
            // `ListAll` returns refs sorted by id ascending. Page through with
            // a generous take — admin UIs paginate client-side and the deployment
            // is unlikely to host more than a few hundred slot configs.
            let! refs =
                store.ListAll<AdSlotEntity>(PlatformAdsConfigScope, AdSlotEntityType, skip = 0, take = 1000)
                |> Async.StartAsTask

            let! entities =
                refs
                |> List.map (fun (r: EntityRef<AdSlotEntity>) -> async {
                    let! result = store.Get<AdSlotEntity>(PlatformAdsConfigScope, AdSlotEntityType, r.Id)

                    return
                        match result with
                        | Ok e -> Some(AdSlotEntity.toConfig e)
                        | Error _ -> None
                })
                |> Async.Parallel
                |> Async.StartAsTask

            let configs = entities |> Array.choose id |> Array.toList
            return! writeJson ctx 200 configs
        })

let private getHandler (mode: PlatformMode) (slotId: string) : HttpHandler =
    fun _next (ctx: HttpContext) ->
        withSubstrate mode ctx (fun _ac store _registry -> task {
            let! result =
                store.Get<AdSlotEntity>(PlatformAdsConfigScope, AdSlotEntityType, slotId)
                |> Async.StartAsTask

            match result with
            | Ok entity -> return! writeJson ctx 200 (AdSlotEntity.toConfig entity)
            | Error(EntityError.NotFound _) -> return! writeError ctx 404 (sprintf "ad-unit %s not found" slotId)
            | Error err -> return! writeError ctx 500 (EntityError.message err)
        })

let private upsertHandler (mode: PlatformMode) (auditEventFor: string -> string -> AuditEvent) : HttpHandler =
    fun _next (ctx: HttpContext) ->
        withSubstrate mode ctx (fun ac store _registry -> task {
            let! body = readBody ctx

            match tryDeserialise<AdSlotConfig> body with
            | None -> return! writeError ctx 400 "Malformed AdSlotConfig payload"
            | Some config when String.IsNullOrWhiteSpace config.SlotId ->
                return! writeError ctx 400 "AdSlotConfig.SlotId is required"
            | Some config ->
                let entity = AdSlotEntity.fromConfig config
                let! saveResult = store.Save<AdSlotEntity>(PlatformAdsConfigScope, entity) |> Async.StartAsTask

                match saveResult with
                | Ok ref ->
                    let event = auditEventFor entity.SlotId ac.UserId
                    recordAudit ctx event
                    return! writeJson ctx 200 (AdSlotEntity.toConfig entity, ref.Version)
                | Error err -> return! writeError ctx 500 (EntityError.message err)
        })

let private createHandler (mode: PlatformMode) : HttpHandler =
    upsertHandler mode (fun slotId actor -> AdSlotConfigCreated(slotId, actor, DateTimeOffset.UtcNow))

let private updateHandler (mode: PlatformMode) (_slotId: string) : HttpHandler =
    // The PUT route's `{slotId}` segment is informational; the body is
    // authoritative (the entity carries its own `SlotId`). A mismatch
    // between the path segment and the body's `SlotId` is rare and the
    // store would happily honour the body — we keep the body as truth.
    upsertHandler mode (fun slotId actor -> AdSlotConfigUpdated(slotId, actor, DateTimeOffset.UtcNow))

let private deleteHandler (mode: PlatformMode) (slotId: string) : HttpHandler =
    fun next (ctx: HttpContext) ->
        withSubstrate mode ctx (fun ac store _registry -> task {
            let! result =
                store.Delete(PlatformAdsConfigScope, AdSlotEntityType, slotId)
                |> Async.StartAsTask

            match result with
            | Ok() ->
                recordAudit ctx (AdSlotConfigDeleted(slotId, ac.UserId, DateTimeOffset.UtcNow))
                ctx.Response.StatusCode <- 204
                return! next ctx
            | Error err -> return! writeError ctx 500 (EntityError.message err)
        })

/// Routes table. Mounted by `compose` only when
/// `ServerConfig.EntityStore = EnabledEntityStore`.
let routes (mode: PlatformMode) : HttpHandler list = [
    GET >=> route "/api/_platform/admin/ad-units" >=> listHandler mode
    GET >=> routef "/api/_platform/admin/ad-units/%s" (getHandler mode)
    POST >=> route "/api/_platform/admin/ad-units" >=> createHandler mode
    PUT >=> routef "/api/_platform/admin/ad-units/%s" (updateHandler mode)
    DELETE >=> routef "/api/_platform/admin/ad-units/%s" (deleteHandler mode)
]