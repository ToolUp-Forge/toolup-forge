// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ModuleQueryClient

open Fable.SimpleJson
open ToolUp.Platform

let private log = Logger.forCategory "client.module-query"

// ─── JSON helpers ─────────────────────────────────────────────────

/// Client-side (de)serialisation uses `Fable.SimpleJson`, which
/// round-trips the same shape the server's `FableConverters`
/// produces — unions, options, records all survive the boundary
/// without additional configuration.
module private Json =
    let inline serialize<'T> (value: 'T) : string = Json.serialize value

    let inline deserialize<'T> (payload: string) : 'T =
        if System.String.IsNullOrEmpty payload then
            Json.parseAs<'T> "null"
        else
            Json.parseAs<'T> payload

// ─── Typed handler constructor ────────────────────────────────────

/// Typed client-side handler constructor. The record lives in
/// `Shared/IModuleQueryBus.fs` (single shape across server and client);
/// this module only layers the Fable.SimpleJson serialiser on top so
/// module authors write strongly-typed `handle` functions and let the
/// SDK deal with the string payload contract.
module ClientModuleQueryHandler =
    /// Construct a handler from typed request / response functions.
    /// Mirrors `ModuleQueryHandler.typed` on the server side; types
    /// referenced here must live in a shared module (ToolUp-SharedTypes)
    /// so both caller and handler see the same shape.
    let inline typed<'TRequest, 'TResponse>
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

// ─── Typed caller-side helper ─────────────────────────────────────

/// Serialise a typed request, dispatch through the bus, deserialise the
/// typed response. Mirrors `ModuleQueryBus.ask` on the server side; the
/// raw `IModuleQueryBus.Ask` remains string-based so the wire format
/// stays portable across in-process, HTTP, and future distributed buses.
let inline ask<'TRequest, 'TResponse>
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

// ─── Client bus implementation ────────────────────────────────────

/// Client-side `IModuleQueryBus`. Tries the in-browser registry first
/// and falls back to the server's `IModuleQueryBusApi` via Fable.Remoting
/// when no local handler matches. A single instance is constructed by
/// `SDK.Client.run` at shell startup and handed to every module through
/// `ClientModuleContext.QueryBus`.
///
/// **Error channels.**
/// - Module not locally registered AND HTTP call returns `None` →
///   `None` (graceful degradation).
/// - Locally registered module but the specific key is not →
///   falls through to HTTP so server-only keys still work.
/// - Handler raised locally → logs to `console.error` and returns
///   `Some (Error (HandlerFailed _))`. The raw exception is not
///   re-thrown (portability rule 3).
type ClientModuleQueryBus(registry: Map<string, Map<string, ModuleQueryHandler>>) =

    // Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
    // Constructed once per ClientModuleQueryBus instance (the bus itself is a singleton in the composition root).
    let remoteApi: IModuleQueryBusApi =
        Api.makeProxy<IModuleQueryBusApi> (customOptions = UserSession.withRequestHeaders)

    interface IModuleQueryBus with
        member _.Ask(ctx, request) = async {
            match registry |> Map.tryFind request.TargetModule with
            | Some moduleHandlers ->
                match moduleHandlers |> Map.tryFind request.QueryKey with
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
                        log.Error(
                            $"Client module query {request.TargetModule}.{request.QueryKey} failed: {ex.Message}",
                            Some ex
                        )

                        return Some(Error(HandlerFailed ex.Message))
                | None ->
                    // Module has some client-side handlers but not for
                    // this key — server may have it. Fall through to HTTP.
                    return! remoteApi.Ask request
            | None ->
                // No client-side handler for this module at all. Let
                // the server answer; empty `None` response from there
                // still means "not deployed" (graceful degradation).
                return! remoteApi.Ask request
        }

// ─── Registry construction ────────────────────────────────────────

/// Build the per-module registry map from a flat
/// `(moduleName, handler) list`. Duplicate `(moduleName, queryKey)`
/// pairs are a fatal compose-time defect (Phase 579), raised out of
/// `Client.run`'s `buildQueryBus` before the shell binds — the client
/// bus routes each pair to exactly one handler, so a duplicate used to
/// silently shadow all but the last registration and fall through to
/// the server (or answer from the wrong handler) at request time. The
/// check is tier-shared with the server bus; see
/// `ModuleQueryRegistry.build`.
let buildRegistry (entries: (string * ModuleQueryHandler) list) = ModuleQueryRegistry.build entries