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
    TargetModule: string
    /// Module-declared discriminator (e.g. `"latest-analysis"`). The
    /// bus routes `(TargetModule, QueryKey)` to exactly one handler.
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
    /// other typed error.
    Ask: ModuleQueryRequest -> Async<Result<ModuleQueryResponse, ModuleQueryError> option>
}

module ModuleQueryBusApi =
    /// Fable.Remoting endpoint prefix. Matches the pattern used by
    /// `IConfigApi`, `PlatformApi`, etc. — `/api/{typeName}/{methodName}`.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"