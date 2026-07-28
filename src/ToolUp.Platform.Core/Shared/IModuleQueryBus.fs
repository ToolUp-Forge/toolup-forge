// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Caller → target-module request envelope. Payload is JSON (produced by
/// a `FableConverters`-compatible serialiser) so the wire format stays
/// portable across in-process, Fable.Remoting, and future distributed
/// (Akka cluster, Orleans) implementations. Typed request / response
/// records live in `ToolUp-SharedTypes` — shared types never live in
/// another module's project.
type ModuleQueryRequest = {
    /// Name of the recipient module (matches `ServerModule.Name` /
    /// `ClientModule.Definition.Id`). Callers pass this as a plain
    /// string — the SDK never hard-codes a module name.
    // Phase 69e.H — a blank routing key is a malformed request (it can
    // only ever miss the bus router); the dispatcher rejects it with a
    // structured validation envelope rather than a downstream NoHandler.
    [<ToolUp.Platform.NotEmpty>]
    TargetModule: string
    /// Module-declared discriminator (e.g. `"latest-analysis"`). The
    /// bus routes `(TargetModule, QueryKey)` to exactly one handler.
    [<ToolUp.Platform.NotEmpty>]
    QueryKey: string
    /// Serialised `'TRequest` payload. Empty string when the query
    /// takes no input.
    Payload: string
}

/// Handler response envelope. Payload mirrors the request convention:
/// JSON string produced by the same serialiser the caller uses.
type ModuleQueryResponse = { Payload: string }

/// Errors surfaced through the `Result` branch of `Ask`. No exceptions
/// leak to callers — the bus catches and wraps every handler failure.
type ModuleQueryError =
    /// Module is present in the deployment but the caller lacks `Read`
    /// permission on it (RBAC).
    | PermissionDenied of moduleName: string
    /// Module is present but has no handler registered for the given
    /// `QueryKey`. Normally a caller bug — handler keys are a shared
    /// contract and typos surface here.
    | NoHandler of moduleName: string * queryKey: string
    /// Handler raised an exception. The bus logs the inner exception
    /// and returns this case with the exception message; the original
    /// exception is not propagated (portability rule 3 — no callback
    /// supervision hooks across the interface).
    | HandlerFailed of message: string

/// Per-invocation context handed to a `ModuleQueryHandler`. Everything
/// the handler needs arrives here — there is no ambient state
/// (portability rule 4; stateless handlers between invocations).
type ModuleQueryContext = {
    /// The authenticated caller's access context. Handlers that read
    /// team-scoped data use `AccessContext.TeamId` the same way event
    /// and storage reads do.
    AccessContext: AccessContext
    /// Name of the module that initiated the query, when known. `None`
    /// when the bus is invoked from the shell, from an AI tool, or
    /// from a test that did not name itself.
    CallerModule: string option
    /// Full request envelope. Handlers read `Request.Payload` to
    /// deserialise the typed request.
    Request: ModuleQueryRequest
}

/// Module-declared query handler. Single-record shape shared by server
/// and client: both sides route a request whose `(TargetModule, QueryKey)`
/// pair matches to this record's `Handle`, differing only in the JSON
/// (de)serialiser wrapped by `ModuleQueryHandler.typed` at the
/// companion layer. `Handle` takes a per-invocation `ModuleQueryContext`
/// (portability rule 4) and returns the serialised response payload.
type ModuleQueryHandler = {
    QueryKey: string
    Handle: ModuleQueryContext -> Async<string>
}

/// Module-to-module typed request/reply. Portable across in-process and
/// distributed implementations — the six portability rules hold:
///
/// 1. **Identity by value.** `ModuleQueryRequest` fields are primitive
///    strings; no live handles cross the boundary.
/// 2. **Async at every method boundary.** `Ask` returns `Async<_>`.
/// 3. **Retry and supervision as data.** v1 ships no retry — a
///    `RetryPolicy` overload is a planned addition. No callback hooks
///    on this interface.
/// 4. **Handlers are stateless between invocations.** `ModuleQueryContext`
///    is the handler's only state source.
/// 5. **No cross-shard ordering.** Point queries; no ordering promised
///    between concurrent `Ask`s.
/// 6. **Precision at the lower bound.** No implicit timing contract.
///
/// ## Return shape
///
/// `Async<Result<ModuleQueryResponse, ModuleQueryError> option>`:
/// - `None` — target module is not registered in this deployment.
///   Callers treat this as graceful degradation (fall back, skip, run
///   without the optional context).
/// - `Some (Ok response)` — handler succeeded; `response.Payload` is a
///   JSON string the caller deserialises into the typed response.
/// - `Some (Error err)` — target is present but the call failed
///   (permission denied, missing handler, handler exception).
///
/// ## Permission check
///
/// The in-process implementation calls
/// `AccessContext.hasPermission moduleName ModulePermission.Read ctx`
/// before dispatching to the handler (RBAC). Empty permission
/// map = unrestricted (opt-in RBAC), same convention as the rest of
/// the SDK.
type IModuleQueryBus =
    /// Dispatch a query to the handler registered for
    /// `(request.TargetModule, request.QueryKey)`. See the type doc
    /// for the three-valued return shape.
    abstract Ask:
        context: AccessContext * request: ModuleQueryRequest ->
            Async<Result<ModuleQueryResponse, ModuleQueryError> option>

/// Cross-origin Fable.Remoting surface for client → server queries.
/// The client does not pass an `AccessContext` — the server resolves it
/// per request from DI (populated by `ScopeResolutionMiddleware`) and
/// forwards the call to the in-process `IModuleQueryBus`. Keeping this
/// record separate from the infrastructure interface means a distributed
/// bus swap (Akka, Orleans) replaces `IModuleQueryBus` without the API
/// surface changing.
type IModuleQueryBusApi = {
    /// Route `request` through the server's `IModuleQueryBus`. Returns
    /// the same three-valued shape as the infrastructure interface.
    /// Permission denial surfaces as `Some (Error (PermissionDenied _))`
    /// rather than an HTTP 403 so the client can branch on it like any
    /// other typed error. Dispatcher-anonymous by design — the bus's
    /// RBAC check against the resolved `AccessContext` is the gate.
    [<AllowAnonymous>]
    Ask: ModuleQueryRequest -> Async<Result<ModuleQueryResponse, ModuleQueryError> option>
}

module ModuleQueryBusApi =
    /// Fable.Remoting endpoint prefix. Matches the pattern used by
    /// `IConfigApi`, `PlatformApi`, etc. — `/api/{typeName}/{methodName}`.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"

/// One direction of a `ModuleQueryContract`'s payload translation — the
/// pair of functions that turn a typed value into the envelope's
/// `Payload` string and back. Deliberately a *value* (a record of two
/// functions), not an interface or a reflection hook: the shared tier
/// has no JSON stack of its own (the server serialises through
/// System.Text.Json + `FableConverters`, the Fable client through
/// `Fable.SimpleJson`), so the declaring module supplies whichever
/// serialiser its tier can execute and the contract carries it by value.
/// That keeps the type Fable-safe and keeps identity by value across the
/// boundary (GP 12 rule 1).
///
/// `Decode` returns `Result` rather than raising: a payload mismatch is
/// an expected failure mode of a wire contract, and the caller-side and
/// handler-side wrappers both project it into a `ModuleQueryError` that
/// names the contract (see `ModuleQueryContract.decodeFailure`).
type ModuleQueryCodec<'T> = {
    Encode: 'T -> string
    Decode: string -> Result<'T, string>
}

/// A cross-module query declared **once**, in the providing module's
/// shared tier, and referenced by both ends: the handler registration
/// (`ServerModule.withQueryContract` / `ClientModule.withQueryContract`)
/// and every caller (`ModuleQueryBus.askContract` /
/// `ModuleQueryClient.askContract`).
///
/// ## What it replaces
///
/// A stringly query makes caller and handler agree on three things by
/// hand — the target module name, the query key, and the JSON shape of
/// the payload — and every mismatch surfaces at *request* time as
/// `NoHandler` or a deserialisation failure. A contract collapses all
/// three into one shared value, so a key typo or a payload-shape drift
/// is a *compile* error at whichever end stopped matching.
///
/// ## What it does not change
///
/// Nothing below it. `withQueryContract` lowers to an ordinary
/// `ModuleQueryHandler` on the existing registration list, and
/// `askContract` builds an ordinary `ModuleQueryRequest`, so the wire
/// shape, the registry, the RBAC check, the duplicate-key rejection and
/// the tracing spans are byte-for-byte what they were (GP 11). A
/// contract-registered handler answers a stringly ask and vice versa —
/// the stringly path stays the interop fallback for callers that cannot
/// reference the contract value (another deployment, a script, a
/// non-F# peer).
type ModuleQueryContract<'Req, 'Resp> = {
    /// Name of the module that answers this query — the same routing key
    /// a stringly `ModuleQueryRequest.TargetModule` carries.
    TargetModule: string
    /// Module-declared discriminator, identical to the `QueryKey` the
    /// lowered `ModuleQueryHandler` registers under.
    QueryKey: string
    /// Request-payload translation. `Encode` runs caller-side, `Decode`
    /// handler-side.
    RequestCodec: ModuleQueryCodec<'Req>
    /// Response-payload translation. `Encode` runs handler-side,
    /// `Decode` caller-side.
    ResponseCodec: ModuleQueryCodec<'Resp>
}

/// Construction + the shared failure vocabulary for `ModuleQueryContract`.
/// Tier-neutral (no JSON dependency), so both buses layer their own
/// serialiser on top: see `ModuleQueryBus.contract` (server, STJ +
/// `FableConverters`) and `ModuleQueryClient.contract` (client,
/// `Fable.SimpleJson`).
[<RequireQualifiedAccess>]
module ModuleQueryContract =
    /// Wrap encode / decode functions into a codec. Use when the module
    /// already owns a serialiser for the type.
    let codec (encode: 'T -> string) (decode: string -> Result<'T, string>) : ModuleQueryCodec<'T> = {
        Encode = encode
        Decode = decode
    }

    /// Wrap a *throwing* deserialiser (the shape both tiers' JSON stacks
    /// expose) into a `Result`-returning codec. The exception message
    /// becomes the decode detail; nothing propagates.
    let codecOfThrowing (encode: 'T -> string) (decode: string -> 'T) : ModuleQueryCodec<'T> = {
        Encode = encode
        Decode =
            fun payload ->
                try
                    Ok(decode payload)
                with ex ->
                    Error ex.Message
    }

    /// Declare a contract from a target module, a query key, and the two
    /// codecs.
    let create
        (targetModule: string)
        (queryKey: string)
        (requestCodec: ModuleQueryCodec<'Req>)
        (responseCodec: ModuleQueryCodec<'Resp>)
        : ModuleQueryContract<'Req, 'Resp> =
        {
            TargetModule = targetModule
            QueryKey = queryKey
            RequestCodec = requestCodec
            ResponseCodec = responseCodec
        }

    /// `"<TargetModule>.<QueryKey>"` — the contract's identity in
    /// diagnostics, matching the `query <module>.<key>` activity name the
    /// bus opens.
    let describe (contract: ModuleQueryContract<'Req, 'Resp>) : string =
        sprintf "%s.%s" contract.TargetModule contract.QueryKey

    /// The decode-failure message both ends raise/return. Always names
    /// the contract (module + key) and which direction failed, because a
    /// payload mismatch is otherwise indistinguishable from an ordinary
    /// handler exception once it reaches `HandlerFailed`.
    let decodeFailure (contract: ModuleQueryContract<'Req, 'Resp>) (direction: string) (detail: string) : string =
        sprintf
            "Module query contract \"%s\": the %s payload did not decode — %s. Caller and handler must reference the same ModuleQueryContract value; a stringly caller on this key must send the shape the contract declares."
            (describe contract)
            direction
            detail

    /// Handler-side request decode. Raises on failure so the bus's
    /// existing catch turns it into `Some (Error (HandlerFailed …))` —
    /// no new error case, no change to either bus implementation.
    let decodeRequestOrRaise (contract: ModuleQueryContract<'Req, 'Resp>) (payload: string) : 'Req =
        match contract.RequestCodec.Decode payload with
        | Ok value -> value
        | Error detail -> failwith (decodeFailure contract "request" detail)

    /// Caller-side response decode, projected into the same
    /// `ModuleQueryError` channel the rest of the ask returns through.
    let decodeResponse
        (contract: ModuleQueryContract<'Req, 'Resp>)
        (payload: string)
        : Result<'Resp, ModuleQueryError> =
        match contract.ResponseCodec.Decode payload with
        | Ok value -> Ok value
        | Error detail -> Error(HandlerFailed(decodeFailure contract "response" detail))

    /// Build the stringly envelope a contract ask sends. Exposed so the
    /// two tiers' `askContract` share one construction site and a
    /// stringly caller can be shown the exact envelope a contract emits.
    let request (contract: ModuleQueryContract<'Req, 'Resp>) (value: 'Req) : ModuleQueryRequest = {
        TargetModule = contract.TargetModule
        QueryKey = contract.QueryKey
        Payload = contract.RequestCodec.Encode value
    }

    /// Lower a contract + typed handler onto the ordinary
    /// `ModuleQueryHandler` both registries already hold. The contract
    /// path adds no registration shape of its own — a contract-registered
    /// handler is indistinguishable, at the registry, from one built by
    /// `ModuleQueryHandler.typed` (GP 11), so duplicate rejection
    /// (Phase 579), the surface descriptor and the tracing spans all see
    /// it unchanged.
    let handler
        (contract: ModuleQueryContract<'Req, 'Resp>)
        (handle: ModuleQueryContext -> 'Req -> Async<'Resp>)
        : ModuleQueryHandler =
        {
            QueryKey = contract.QueryKey
            Handle =
                fun ctx -> async {
                    let request = decodeRequestOrRaise contract ctx.Request.Payload
                    let! response = handle ctx request
                    return contract.ResponseCodec.Encode response
                }
        }

    /// Compose-time guard for the third string a contract subsumes: a
    /// contract registered on a module whose `Name` is not the contract's
    /// `TargetModule` would register under a routing key no caller of
    /// that contract ever asks for, and surface as `NoHandler` at request
    /// time. Raises with both names; returns unit when they agree.
    let ensureTargetMatches (moduleName: string) (contract: ModuleQueryContract<'Req, 'Resp>) : unit =
        if moduleName <> contract.TargetModule then
            failwith (
                sprintf
                    "Compose-time defect: module \"%s\" registers the query contract \"%s\", whose TargetModule is \"%s\". The bus routes on the registering module's name, so callers of this contract would ask \"%s\" and see NoHandler. Register the contract on the module it names, or correct the contract's TargetModule."
                    moduleName
                    (describe contract)
                    contract.TargetModule
                    contract.TargetModule
            )

/// Tier-shared registry construction for the module query buses. Both
/// the server (`ModuleQueryBus.buildRegistry`) and the client
/// (`ModuleQueryClient.buildRegistry`) route `(TargetModule, QueryKey)`
/// to exactly one handler, so the duplicate check lives here once and
/// both compose paths inherit the same failure shape — the check is
/// purely structural over the registration list, so the SDK never names
/// a module (GP 9).
module ModuleQueryRegistry =
    /// Render the compose-time failure text for the colliding
    /// `(moduleName, queryKey, registrationCount)` triples. Every
    /// collision is reported (not just the first) so one restart names
    /// the whole misconfiguration.
    let private renderCollisions (collisions: (string * string * int) list) : string =
        collisions
        |> List.map (fun (moduleName, queryKey, count) ->
            sprintf
                "Compose-time defect: duplicate module query handler for module \"%s\" on query key \"%s\" (%d registrations). The bus routes (TargetModule, QueryKey) to exactly one handler, so all but one registration would be silently shadowed and callers would see NoHandler or the wrong handler at request time. Give each handler a distinct QueryKey, or drop the redundant registration."
                moduleName
                queryKey
                count)
        |> String.concat "\n"

    /// Build the `(module → queryKey → handler)` registry from a flat
    /// `(moduleName, handler) list`, rejecting duplicate
    /// `(moduleName, queryKey)` pairs as a fatal compose-time defect.
    /// Mirrors the transactional-sink duplicate-`Kind` and audit-sink
    /// duplicate-`Name` rejections: the deployment fails to start rather
    /// than running with a handler silently shadowed. A registration
    /// list with no duplicates produces byte-identical output to the
    /// prior last-wins fold (GP 11).
    let build (entries: (string * ModuleQueryHandler) list) : Map<string, Map<string, ModuleQueryHandler>> =
        let grouped = entries |> List.groupBy fst

        let collisions =
            grouped
            |> List.collect (fun (moduleName, pairs) ->
                pairs
                |> List.map (fun (_, h) -> h.QueryKey)
                |> List.groupBy id
                |> List.filter (fun (_, occurrences) -> List.length occurrences > 1)
                |> List.map (fun (queryKey, occurrences) -> moduleName, queryKey, List.length occurrences))

        if not (List.isEmpty collisions) then
            failwith (renderCollisions collisions)

        grouped
        |> List.map (fun (moduleName, pairs) ->
            let inner = pairs |> List.map (fun (_, h) -> h.QueryKey, h) |> Map.ofList

            moduleName, inner)
        |> Map.ofList