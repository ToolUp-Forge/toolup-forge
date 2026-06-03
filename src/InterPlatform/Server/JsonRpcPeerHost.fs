// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Reflection
open Microsoft.AspNetCore.Http
open Microsoft.FSharp.Reflection
open Giraffe
open ToolUp.Platform
open PeerReflection

// ─── Layer 4 — typed receiver host ───────────────────────────────────
//
// The server half of the typed RPC, symmetric with the client proxy
// (`JsonRpcPeerClient.create`). `JsonRpcPeerHost.contract<'TApi>` reflects
// over the same record-of-functions contract type the client reflects
// over and returns a `PeerContractRegistration` whose `Dispatch` closure
// unmarshals a method's positional arguments, applies them to the
// concrete implementation record, awaits the resulting `Async<'R>`, and
// serialises the result back to the wire. Both halves share
// `PeerReflection` so they agree on the argument wire shape.
//
// `JsonRpcPeerHost.routes` is the Giraffe surface a peer-enabled
// deployment mounts: `POST /peer/v1/{contractId}` dispatches a contract
// call; `GET /peer/v1/capabilities` answers a capability handshake. Both
// authenticate the inbound bearer token before doing any work (fail
// closed — silent-insecure-default lens). The contract handler rebuilds
// the `PeerCallContext` from the *validated* `PeerPrincipal`, never from
// the self-asserted wire payload, so a caller cannot spoof its identity
// by editing the request body.

/// Reflection shim: awaits a boxed `Async<'R>` and serialises its result
/// to the wire. Invoked via `MakeGenericMethod(retType)` from the
/// dispatch builder, which only knows the result type at runtime.
type private HostInvoker =

    static member AwaitSerialize<'R>(work: Async<'R>) : Async<string> = async {
        let! result = work
        return JsonRpc.serialize result
    }

/// Builds typed contract registrations + the Giraffe host surface from a
/// record-of-functions contract type.
module JsonRpcPeerHost =

    let private awaitSerializeMethod =
        typeof<HostInvoker>.GetMethod("AwaitSerialize", BindingFlags.Static ||| BindingFlags.Public)

    /// Build the per-method dispatch closure for an `Immediate` field:
    /// unmarshal args → apply to the implementation function → await +
    /// serialise. A peer-side `PeerInvocationException` surfaces as the
    /// original `PeerError`; any other handler exception collapses to
    /// `PeerHandler`.
    let private immediateDispatch
        (impl: obj)
        (field: PropertyInfo)
        (argTypes: Type list)
        (retType: Type)
        : string -> Async<Result<string, PeerError>> =
        let funcValue = field.GetValue impl
        let awaitSerialize = awaitSerializeMethod.MakeGenericMethod(retType)

        fun argsJson -> async {
            try
                let args = unmarshalArgs argsJson argTypes
                let boxedAsync = applyFunction funcValue args
                let serializeAsync = awaitSerialize.Invoke(null, [| boxedAsync |]) :?> Async<string>
                let! resultJson = serializeAsync
                return Ok resultJson
            with
            | PeerInvocationException e -> return Error e
            | ex -> return Error(PeerHandler ex.Message)
        }

    /// Render a `ScheduleError` to a one-line diagnostic for the
    /// `PeerHandler` error returned when scheduling a long-running call
    /// fails. The structured `ScheduleError` is server-internal — the peer
    /// only ever sees a `PeerHandler` string — so this is a flattening,
    /// not a wire mapping.
    let private scheduleErrorMessage (e: ScheduleError) : string =
        match e with
        | ScheduleError.InvalidCron(expr, reason) -> $"invalid cron '{expr}': {reason}"
        | ScheduleError.HandlerNotRegistered name -> $"job handler '{name}' is not registered"
        | ScheduleError.PrecisionUnsupported(supplied, supported) ->
            let sup = supported |> List.map string |> String.concat ", "
            $"job precision {supplied} unsupported (supported: {sup})"
        | ScheduleError.StorageFailure msg -> $"job storage failure: {msg}"

    /// Build the per-method dispatch closure for a `LongRunning` field
    /// when the job-fusion substrate is present: schedule a `_platform`
    /// `Manual` job under the contract method's handler name, trigger it
    /// once, and return the assigned `JobId` (serialised) for the caller
    /// to poll. The job's *typed* result is captured by the registered
    /// `PeerJobHandler` and parked in the `IPeerJobResultStore`; this
    /// closure only kicks the job off and hands back the id.
    let private scheduleDispatch
        (fusion: PeerJobFusion)
        (handlerName: string)
        : string -> Async<Result<string, PeerError>> =
        fun argsJson -> async {
            let registration: JobRegistration = {
                ScopeId = PeerJob.Scope
                Handler = handlerName
                Payload = argsJson
                Trigger = Manual
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = JobPrecision.Minute
                CreatedBy = PeerJob.SourceModule
                Tags = Map.empty
            }

            let! scheduled = fusion.Scheduler.Schedule registration

            match scheduled with
            | Error e -> return Error(PeerHandler $"failed to schedule long-running peer job: {scheduleErrorMessage e}")
            | Ok jobId ->
                let! triggered = fusion.Scheduler.TriggerOnce(PeerJob.Scope, jobId, PeerJob.SourceModule)

                match triggered with
                | Error msg -> return Error(PeerHandler $"failed to trigger long-running peer job: {msg}")
                | Ok() -> return Ok(JsonRpc.serialize jobId)
        }

    /// Reflect over `'TApi` (a record whose fields are contract methods)
    /// and return a `PeerContractHost` bound to `impl` — the transport-
    /// agnostic `PeerContractRegistration` plus the `(handlerName,
    /// IJobHandler)` pairs the compose hook registers with the scheduler.
    /// Each `Immediate` method (`… -> Async<'T>`) dispatches inline.
    /// `LongRunning` methods (`… -> Async<PeerJobHandle<'T>>`) schedule a
    /// background job when `fusion` is `Some`; when `fusion` is `None`
    /// (the substrate is off, or the deployment has no job substrate) they
    /// fail with a clear `PeerHandler` error and contribute no job handler.
    let contract<'TApi>
        (contractId: string)
        (versions: ContractVersion list)
        (fusion: PeerJobFusion option)
        (impl: 'TApi)
        : PeerContractHost =
        let apiType = typeof<'TApi>

        if not (FSharpType.IsRecord apiType) then
            failwithf "JsonRpcPeerHost.contract requires a record contract type; %s is not a record" apiType.Name

        let implObj = box impl

        // Each field yields its (methodName, dispatch) pair and, for a
        // fusion-backed long-running method, an optional (handlerName,
        // IJobHandler) the compose hook registers with the scheduler.
        let perField =
            FSharpType.GetRecordFields apiType
            |> Array.map (fun field ->
                let argTypes, retType = functionSignature field.PropertyType

                let isLongRunning =
                    retType.IsGenericType
                    && retType.GetGenericTypeDefinition() = typedefof<PeerJobHandle<_>>

                if isLongRunning then
                    match fusion with
                    | Some f ->
                        let innerType = retType.GetGenericArguments().[0]
                        let funcValue = field.GetValue impl
                        let hName = PeerJob.handlerName contractId field.Name
                        let dispatch = scheduleDispatch f hName

                        let jobHandler =
                            PeerJobHandler(funcValue, argTypes, innerType, f.ResultStore) :> IJobHandler

                        (field.Name, dispatch), Some(hName, jobHandler)
                    | None ->
                        let dispatch =
                            fun (_: string) -> async {
                                return
                                    Error(
                                        PeerHandler
                                            $"Long-running contract method '{field.Name}' requires the peer job-fusion substrate, which is not enabled"
                                    )
                            }

                        (field.Name, dispatch), None
                else
                    (field.Name, immediateDispatch implObj field argTypes retType), None)

        let methodMap = perField |> Array.map fst |> Map.ofArray
        let jobHandlers = perField |> Array.choose snd |> Array.toList

        let dispatch: PeerDispatch =
            fun _context methodName argsJson ->
                match Map.tryFind methodName methodMap with
                | Some handler -> handler argsJson
                | None -> async { return Error(PeerMethodNotFound methodName) }

        {
            Registration = {
                ContractId = contractId
                Versions = versions
                Dispatch = dispatch
            }
            JobHandlers = jobHandlers
        }

    // ─── Giraffe host surface ────────────────────────────────────────

    /// Extract the bearer token from the `Authorization` header.
    let private bearerToken (ctx: HttpContext) : string option =
        match ctx.TryGetRequestHeader "Authorization" with
        | Some header when header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
            Some(header.Substring(7).Trim())
        | _ -> None

    /// Parse the request envelope and its structured payload in one step;
    /// either failing collapses to `PeerDeserialization`.
    let private tryParse (body: string) : Result<JsonRpcRequest * PeerWirePayload, PeerError> =
        try
            let request = JsonRpc.deserialize<JsonRpcRequest> body
            let payload = JsonRpc.deserialize<PeerWirePayload> request.Params
            Ok(request, payload)
        with ex ->
            Error(PeerDeserialization ex.Message)

    /// Write a JSON-RPC response body at `statusCode`.
    let private writeJson (statusCode: int) (response: JsonRpcResponse) : HttpHandler =
        fun (_: HttpFunc) (ctx: HttpContext) ->
            ctx.SetStatusCode statusCode
            ctx.SetContentType "application/json"
            ctx.WriteStringAsync(JsonRpc.serialize response)

    /// `POST /peer/v1/{contractId}` — authenticate, rebuild the trusted
    /// call context from the validated principal, dispatch.
    let private contractHandler (auth: IPeerAuthProvider) (peer: IPlatformPeer) (contractId: string) : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            match bearerToken ctx with
            | None -> return! writeJson 401 (JsonRpc.failure "" (PeerUnauthorized "missing bearer token")) next ctx
            | Some token ->
                let! validation = auth.ValidatePeerToken token |> Async.StartAsTask

                match validation with
                | Error e -> return! writeJson 401 (JsonRpc.failure "" e) next ctx
                | Ok principal ->
                    let! body = ctx.ReadBodyFromRequestAsync()

                    match tryParse body with
                    | Error e -> return! writeJson 400 (JsonRpc.failure "" e) next ctx
                    | Ok(request, payload) ->
                        // Trust the validated principal, never the
                        // self-asserted wire payload's identity.
                        let trustedContext = {
                            payload.Context with
                                Peer = principal.Caller
                                User = principal.User
                        }

                        let! result =
                            peer.Handle(contractId, trustedContext, request.Method, payload.Arguments)
                            |> Async.StartAsTask

                        match result with
                        | Ok resultJson ->
                            // Build the response by hand so the
                            // already-serialised result rides in
                            // `Result` without a second JSON encode.
                            let response = {
                                JsonRpc = JsonRpc.version
                                Result = Some resultJson
                                Error = None
                                Id = request.Id
                            }

                            return! writeJson 200 response next ctx
                        | Error e -> return! writeJson 200 (JsonRpc.failure request.Id e) next ctx
        }

    /// `GET /peer/v1/capabilities` — authenticate, then answer the
    /// capability handshake. Auth-gated for the same fail-closed posture
    /// as contract dispatch; capability discovery is not anonymous.
    let private capabilitiesHandler (auth: IPeerAuthProvider) (peer: IPlatformPeer) : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            match bearerToken ctx with
            | None -> return! writeJson 401 (JsonRpc.failure "" (PeerUnauthorized "missing bearer token")) next ctx
            | Some token ->
                let! validation = auth.ValidatePeerToken token |> Async.StartAsTask

                match validation with
                | Error e -> return! writeJson 401 (JsonRpc.failure "" e) next ctx
                | Ok _ ->
                    let! capabilities = peer.Capabilities() |> Async.StartAsTask
                    ctx.SetStatusCode 200
                    ctx.SetContentType "application/json"
                    return! ctx.WriteStringAsync(JsonRpc.serialize capabilities)
        }

    /// `GET /peer/v1/{contractId}/jobs/{jobId}` — authenticate, then
    /// return the long-running call's current status from the result
    /// store. `None` (the job has not finished) is reported as `Pending`;
    /// a finished job reports its stored terminal status. The status rides
    /// in `Result` as a serialised `PeerJobStatus<string>`, matching what
    /// the client transport's `PollJob` expects. `contractId` is part of
    /// the route for symmetry with the invoke leg; the store is keyed by
    /// scope + job id, so it is not needed for the lookup.
    let private jobStatusHandler
        (auth: IPeerAuthProvider)
        (fusion: PeerJobFusion option)
        (contractId: string)
        (jobId: Guid)
        : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            match bearerToken ctx with
            | None -> return! writeJson 401 (JsonRpc.failure "" (PeerUnauthorized "missing bearer token")) next ctx
            | Some token ->
                let! validation = auth.ValidatePeerToken token |> Async.StartAsTask

                match validation with
                | Error e -> return! writeJson 401 (JsonRpc.failure "" e) next ctx
                | Ok _ ->
                    match fusion with
                    | None ->
                        return!
                            writeJson
                                200
                                (JsonRpc.failure "" (PeerHandler "peer job-fusion substrate is not enabled"))
                                next
                                ctx
                    | Some f ->
                        let! status = f.ResultStore.TryGetResult(PeerJob.Scope, jobId) |> Async.StartAsTask
                        let resolved = status |> Option.defaultValue PeerJobStatus.Pending

                        let response = {
                            JsonRpc = JsonRpc.version
                            Result = Some(JsonRpc.serialize resolved)
                            Error = None
                            Id = ""
                        }

                        return! writeJson 200 response next ctx
        }

    /// The peer host's Giraffe routes. Mount under the deployment's root
    /// router when the peer substrate is enabled. `fusion` is `Some` when
    /// the job-fusion substrate is present (enables long-running call
    /// polling); `None` leaves the jobs route reporting a clear
    /// "not enabled" error.
    let routes (auth: IPeerAuthProvider) (peer: IPlatformPeer) (fusion: PeerJobFusion option) : HttpHandler =
        choose [
            GET >=> route "/peer/v1/capabilities" >=> capabilitiesHandler auth peer
            GET
            >=> routef "/peer/v1/%s/jobs/%O" (fun (contractId, jobId) -> jobStatusHandler auth fusion contractId jobId)
            POST >=> routef "/peer/v1/%s" (contractHandler auth peer)
        ]