module ToolUp.Platform.ModuleQueryBus

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Tracing

// The `ModuleQueryHandler` record itself now lives in
// `Shared/IModuleQueryBus.fs` so server and client share a single
// shape; what follows is the server-specific serialisation layer
// (STJ + FableConverters) and the in-process bus impl.

// ─── JSON helpers ─────────────────────────────────────────────────

/// Shared `FableConverters` setup. Handlers serialise typed
/// requests / responses through the same converter the Fable client
/// uses for SSE / non-Remoting JSON, so payloads round-trip losslessly
/// through DUs, options, and records (the platform's SSE serialisation
/// rule).
module private Json =
    let private options = FableConverters.create ()

    let serialize<'T> (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let deserialize<'T> (payload: string) : 'T =
        if System.String.IsNullOrEmpty payload then
            JsonSerializer.Deserialize<'T>("null", options)
        else
            JsonSerializer.Deserialize<'T>(payload, options)

// ─── Typed handler constructor ────────────────────────────────────

module ModuleQueryHandler =
    /// Construct a handler from typed request / response functions.
    /// The returned record deserialises the request JSON, calls the
    /// typed handler, and serialises the typed response back to JSON.
    ///
    /// `'TRequest` and `'TResponse` must be types shared across module
    /// boundaries — live in `ToolUp-SharedTypes` or be primitives.
    /// They must never be defined inside another module's project
    /// (GP 10).
    let typed<'TRequest, 'TResponse>
        (queryKey: string)
        (handle: ModuleQueryContext -> 'TRequest -> Async<'TResponse>)
        : ModuleQueryHandler =
        {
            QueryKey = queryKey
            Handle =
                fun ctx -> async {
                    let req = Json.deserialize<'TRequest> ctx.Request.Payload
                    let! resp = handle ctx req
                    return Json.serialize resp
                }
        }

// ─── Typed contracts (Phase 584) ──────────────────────────────────

/// Server-tier `ModuleQueryContract` construction. The shared tier owns
/// the contract *type* (it has no JSON stack of its own); this is where
/// the server's serialiser — System.Text.Json with the `FableConverters`
/// set, the same one `ModuleQueryHandler.typed` and `ask` already use —
/// is baked into a contract's codecs, so a contract declared here
/// round-trips byte-for-byte with the stringly path it lowers onto.
///
/// Declare the contract once in the providing module's shared tier and
/// reference the value from both ends:
/// ```
/// let latestAnalysis =
///     ModuleQueryBus.contract<LatestAnalysisReq, LatestAnalysisResp> "SkuAnalysis" "latest"
///
/// // provider
/// serverModule |> ServerModule.withQueryContract latestAnalysis handleLatest
///
/// // caller — the key is no longer a string it has to get right
/// let! result = ModuleQueryBus.askContract bus access latestAnalysis { DatasetId = id }
/// ```
let contract<'TRequest, 'TResponse>
    (targetModule: string)
    (queryKey: string)
    : ModuleQueryContract<'TRequest, 'TResponse> =
    ModuleQueryContract.create
        targetModule
        queryKey
        (ModuleQueryContract.codecOfThrowing Json.serialize<'TRequest> Json.deserialize<'TRequest>)
        (ModuleQueryContract.codecOfThrowing Json.serialize<'TResponse> Json.deserialize<'TResponse>)

/// Ask through a contract: encode with the contract's request codec,
/// dispatch the ordinary stringly envelope, decode with its response
/// codec. Same three-valued return as `ask`; a response that does not
/// decode surfaces as `Some (Error (HandlerFailed …))` whose message
/// names the contract (`ModuleQueryContract.decodeFailure`) rather than
/// throwing at the call site.
let askContract
    (bus: IModuleQueryBus)
    (ctx: AccessContext)
    (contract: ModuleQueryContract<'TRequest, 'TResponse>)
    (request: 'TRequest)
    : Async<Result<'TResponse, ModuleQueryError> option> =
    async {
        let! result = bus.Ask(ctx, ModuleQueryContract.request contract request)

        return
            result
            |> Option.map (fun outcome ->
                outcome
                |> Result.bind (fun r -> ModuleQueryContract.decodeResponse contract r.Payload))
    }

// ─── Typed caller-side helper ─────────────────────────────────────

/// Serialise a typed request, call the bus, deserialise the typed
/// response. The raw `IModuleQueryBus.Ask` stays string-based (wire-
/// portable across in-process / Akka / Orleans), and callers opt into
/// the typed projection at the call site.
///
/// Typed call sites in server-side modules look like:
/// ```
/// let! result =
///     ModuleQueryBus.ask<LatestAnalysisReq, LatestAnalysisResp>
///         bus access "SkuAnalysis" "latest" { DatasetId = datasetId }
/// ```
let ask<'TRequest, 'TResponse>
    (bus: IModuleQueryBus)
    (ctx: AccessContext)
    (targetModule: string)
    (queryKey: string)
    (request: 'TRequest)
    : Async<Result<'TResponse, ModuleQueryError> option> =
    async {
        let payload = Json.serialize request

        let! result =
            bus.Ask(
                ctx,
                {
                    TargetModule = targetModule
                    QueryKey = queryKey
                    Payload = payload
                }
            )

        return
            result
            |> Option.map (Result.map (fun r -> Json.deserialize<'TResponse> r.Payload))
    }

// ─── In-memory implementation ─────────────────────────────────────

/// Default in-process bus. Holds an immutable `(module → queryKey →
/// handler)` registry built from each `ServerModule.QueryHandlers` at
/// compose time. Singleton-safe: no per-request state.
///
/// - Missing module → `None` (graceful degradation by design).
/// - Permission fail → `Some (Error (PermissionDenied _))`.
/// - Missing handler → `Some (Error (NoHandler _))`.
/// - Handler raised → logs the exception, returns
///   `Some (Error (HandlerFailed message))`. The raw exception does not
///   cross the interface (GP 12 rule 3).
type InMemoryModuleQueryBus
    (registry: Map<string, Map<string, ModuleQueryHandler>>, logger: ILogger, activitySink: IActivitySink) =

    let logError (moduleName: string) (queryKey: string) (ex: exn) =
        logger.Error($"Module query {moduleName}.{queryKey} failed", Some ex)

    interface IModuleQueryBus with
        member _.Ask(ctx, request) = async {
            // Phase 9l — child activity per cross-module query so the
            // trace shows the caller module → bus → target handler
            // chain as nested spans. Always inherits from
            // `Activity.Current`: in-process queries ride the
            // caller's async-local context (request handler →
            // `ModuleQueryBus.ask`).
            let queryActivityOpt =
                activitySink.StartActivity(sprintf "query %s.%s" request.TargetModule request.QueryKey, None)

            try
                match registry |> Map.tryFind request.TargetModule with
                | None ->
                    // Target module is not in this deployment — graceful
                    // degradation. Callers that care branch on `None`.
                    return None
                | Some moduleHandlers ->
                    // Phase 4 RBAC: caller must have Read on target module.
                    // Empty permission map = unrestricted (opt-in RBAC), same
                    // convention as `makePermissionGuardedApi`.
                    if not (AccessContext.hasPermission request.TargetModule ModulePermission.Read ctx) then
                        return Some(Error(PermissionDenied request.TargetModule))
                    else
                        match moduleHandlers |> Map.tryFind request.QueryKey with
                        | None -> return Some(Error(NoHandler(request.TargetModule, request.QueryKey)))
                        | Some handler ->
                            try
                                let! payload =
                                    handler.Handle {
                                        AccessContext = ctx
                                        CallerModule = None
                                        Request = request
                                    }

                                return Some(Ok { Payload = payload })
                            with ex ->
                                logError request.TargetModule request.QueryKey ex
                                return Some(Error(HandlerFailed ex.Message))
            finally
                queryActivityOpt |> Option.iter _.Dispose()
        }

/// Build the registry map from a flat `(moduleName, handler) list`.
/// Used by `ServerApp.addModule` to accumulate handlers across modules.
/// Duplicate `(moduleName, queryKey)` pairs are a fatal compose-time
/// defect (Phase 579) — the bus routes each pair to exactly one handler,
/// so a duplicate used to silently shadow all but the last registration
/// and surface as `NoHandler` (or the wrong handler) at request time.
/// The check is tier-shared with the client bus; see
/// `ModuleQueryRegistry.build`.
let buildRegistry (entries: (string * ModuleQueryHandler) list) = ModuleQueryRegistry.build entries