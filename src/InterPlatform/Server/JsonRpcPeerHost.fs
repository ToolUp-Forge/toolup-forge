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
//
// **Delegation verification (Phase 330).** The validated principal's
// *end-user* identity is a different question from its *peer* identity:
// it arrived inside the caller's own signed payload, so the token
// signature says who sent the assertion and nothing about whether it is
// true. Before rebuilding the call context, the contract handler
// therefore runs a `Delegated` originator through
// `IPeerAuthProvider.VerifyDelegation` and refuses `PeerUnauthorized` on
// failure — otherwise any peer with a valid signing key could name any
// subject as the originator, which is what the delegation-chain
// signature exists to prevent.

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

    // NonPublic is load-bearing: `HostInvoker` is a `private` type, so F#
    // emits its (F#-public) static members with non-public IL visibility.
    // Without NonPublic the lookup returns null and dispatch NREs.
    let private awaitSerializeMethod =
        typeof<HostInvoker>
            .GetMethod("AwaitSerialize", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)

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
    /// closure only kicks the job off and hands back the id. The
    /// scheduling caller's `PeerId` — read from the *validated* call
    /// context, never the wire body — rides the job payload so the parked
    /// result is owned by that caller and the poll route can refuse any
    /// other peer (Phase 308, GP 4).
    let private scheduleDispatch
        (fusion: PeerJobFusion)
        (handlerName: string)
        : PeerCallContext -> string -> Async<Result<string, PeerError>> =
        fun context argsJson -> async {
            let payload: PeerJobPayload = {
                OwnerPeerId = context.Peer.PeerId
                ArgsJson = argsJson
            }

            let registration: JobRegistration = {
                ScopeId = PeerJob.Scope
                Handler = handlerName
                Payload = JsonRpc.serialize payload
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
                        let innerType = retType.GetGenericArguments()[0]
                        let funcValue = field.GetValue impl
                        let hName = PeerJob.handlerName contractId field.Name
                        let dispatch = scheduleDispatch f hName

                        let jobHandler =
                            PeerJobHandler(funcValue, argTypes, innerType, f.ResultStore) :> IJobHandler

                        (field.Name, dispatch), Some(hName, jobHandler)
                    | None ->
                        let dispatch =
                            fun (_: PeerCallContext) (_: string) -> async {
                                return
                                    Error(
                                        PeerHandler
                                            $"Long-running contract method '{field.Name}' requires the peer job-fusion substrate, which is not enabled"
                                    )
                            }

                        (field.Name, dispatch), None
                else
                    // Immediate methods take no identity decision, so the
                    // call context is dropped at this seam.
                    let immediate = immediateDispatch implObj field argTypes retType
                    (field.Name, (fun (_: PeerCallContext) argsJson -> immediate argsJson)), None)

        let methodMap = perField |> Array.map fst |> Map.ofArray
        let jobHandlers = perField |> Array.choose snd |> Array.toList

        let dispatch: PeerDispatch =
            fun context methodName argsJson ->
                match Map.tryFind methodName methodMap with
                | Some handler -> handler context argsJson
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
    //
    // The peer providers (`IPeerAuthProvider`, `IPlatformPeer`, the
    // optional `PeerJobFusion`, `IAuditLog`) are resolved per-request
    // from `ctx.RequestServices`, not closed over at route-build time.
    // They can only be constructed *inside* compose (they depend on the
    // resolved `ISecretStore` / `IBlobStorage` / `IJobScheduler`), so
    // `PeerCompose` registers them as DI singletons and the handlers read
    // them back here. This keeps `routes` a parameterless value the
    // compose hook appends to the deployment's router.

    /// Resolve a required DI service for the current request.
    let private getService<'T> (ctx: HttpContext) : 'T =
        ctx.RequestServices.GetService(typeof<'T>) :?> 'T

    /// Resolve an optional DI service — `None` when nothing was
    /// registered (e.g. `PeerJobFusion` is absent when the deployment has
    /// no job substrate; `IAuditLog` is absent only in partial test hosts).
    let private tryGetService<'T> (ctx: HttpContext) : 'T option =
        match ctx.RequestServices.GetService(typeof<'T>) with
        | null -> None
        | svc -> Some(svc :?> 'T)

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

    /// Emit the best-effort `PeerCallCompleted` audit row for a resolved
    /// inbound call. Resolved per-request; a partial test host without an
    /// `IAuditLog` registered simply records nothing.
    let private auditPeerCall
        (ctx: HttpContext)
        (contractId: string)
        (methodName: string)
        (callerPeerId: string)
        (rootRequestId: string)
        (result: Result<string, PeerError>)
        : System.Threading.Tasks.Task =
        task {
            match tryGetService<IAuditLog> ctx with
            | None -> ()
            | Some auditLog ->
                let payload: PeerCallCompletedPayload = {
                    ContractId = contractId
                    MethodName = methodName
                    CallerPeerId = callerPeerId
                    RootRequestId = rootRequestId
                    Succeeded = Result.isOk result
                    Outcome =
                        match result with
                        | Ok _ -> "ok"
                        | Error e -> JsonRpc.errorCaseName e
                    OccurredAt = DateTimeOffset.UtcNow
                }

                do! auditLog.Record(PeerJob.Scope, PeerCallCompleted payload) |> Async.StartAsTask
        }

    /// Phase 330 — verify the *originator* a validated principal asserts,
    /// before anything acts on it.
    ///
    /// `ValidatePeerToken` authenticates the calling peer; the `uctx` it
    /// carries rides inside that peer's own signed payload, so the outer
    /// signature proves who sent the assertion and nothing about whether
    /// it is true. On the `Delegated` case the caller is claiming "I am
    /// acting for user U, and peer P authorised me to" — the classic
    /// confused-deputy shape — and the only thing separating a genuine
    /// buyer→broker→seller delegation from an invented one is
    /// `DelegatedAssertion.Signature`, checked against the delegating
    /// peer's own trust anchor. Without this call any peer holding a
    /// valid signing key could name any subject, and the whole delegation
    /// signature would be decorative.
    ///
    /// `Anonymous` and `Direct` short-circuit without touching the auth
    /// provider, so a non-delegating deployment runs exactly the code it
    /// ran before this phase and pays nothing for the check (GP 11 /
    /// GP 13). `Direct` is deliberately NOT covered: it is a single-hop
    /// "this peer vouches for its own user" claim, already bounded by the
    /// authenticated caller, and it carries no signature to verify.
    let private verifyOriginator (auth: IPeerAuthProvider) (principal: PeerPrincipal) : Async<Result<unit, PeerError>> =
        match principal.User with
        | Anonymous
        | Direct _ -> async { return Ok() }
        | Delegated assertion -> auth.VerifyDelegation assertion

    /// `POST /peer/v1/{contractId}` — authenticate, verify any asserted
    /// delegation, rebuild the trusted call context from the validated
    /// principal, dispatch.
    let private contractHandler (contractId: string) : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let auth = getService<IPeerAuthProvider> ctx
            let peer = getService<IPlatformPeer> ctx

            match bearerToken ctx with
            | None -> return! writeJson 401 (JsonRpc.failure "" (PeerUnauthorized "missing bearer token")) next ctx
            | Some token ->
                let! validation = auth.ValidatePeerToken token |> Async.StartAsTask

                match validation with
                | Error e -> return! writeJson 401 (JsonRpc.failure "" e) next ctx
                | Ok principal ->
                    // Fail closed on an unverifiable delegation BEFORE the
                    // body is read: the request never reaches dispatch, and
                    // no audit row attributes work to an originator this
                    // receiver could not authenticate.
                    let! originator = verifyOriginator auth principal |> Async.StartAsTask

                    match originator with
                    | Error e -> return! writeJson 401 (JsonRpc.failure "" e) next ctx
                    | Ok() ->
                        let! body = ctx.ReadBodyFromRequestAsync()

                        match tryParse body with
                        | Error e -> return! writeJson 400 (JsonRpc.failure "" e) next ctx
                        | Ok(request, payload) ->
                            // Trust the validated principal, never the
                            // self-asserted wire payload's identity. The
                            // `Delegated` originator inside it has been
                            // signature-verified above (Phase 330).
                            let trustedContext = {
                                payload.Context with
                                    Peer = principal.Caller
                                    User = principal.User
                            }

                            let! result =
                                peer.Handle(contractId, trustedContext, request.Method, payload.Arguments)
                                |> Async.StartAsTask

                            do!
                                auditPeerCall
                                    ctx
                                    contractId
                                    request.Method
                                    principal.Caller.PeerId
                                    trustedContext.RootRequestId
                                    result

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
    let private capabilitiesHandler: HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let auth = getService<IPeerAuthProvider> ctx
            let peer = getService<IPlatformPeer> ctx

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

    /// `GET /peer/v1/capabilities/profile` — authenticate, then answer
    /// the Phase 18d capability *profile* handshake with this deployment's
    /// aggregated `PeerProfile` (per-version, per-method lifecycle).
    /// Auth-gated identically to `/peer/v1/capabilities` (fail-closed). A
    /// deployment without an `IPeerProfileProvider` registered (a partial
    /// test host) answers an empty profile rather than failing.
    let private capabilitiesProfileHandler: HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let auth = getService<IPeerAuthProvider> ctx

            match bearerToken ctx with
            | None -> return! writeJson 401 (JsonRpc.failure "" (PeerUnauthorized "missing bearer token")) next ctx
            | Some token ->
                let! validation = auth.ValidatePeerToken token |> Async.StartAsTask

                match validation with
                | Error e -> return! writeJson 401 (JsonRpc.failure "" e) next ctx
                | Ok _ ->
                    let! profile =
                        match tryGetService<IPeerProfileProvider> ctx with
                        | Some provider -> provider.LocalProfile() |> Async.StartAsTask
                        | None -> System.Threading.Tasks.Task.FromResult<PeerProfile>([])

                    ctx.SetStatusCode 200
                    ctx.SetContentType "application/json"
                    return! ctx.WriteStringAsync(JsonRpc.serialize profile)
        }

    /// `GET /peer/v1/{contractId}/jobs/{jobId}` — authenticate, then
    /// return the long-running call's current status from the result
    /// store, scoped to the peer that scheduled it (Phase 308). The
    /// parked record carries the scheduling caller's `PeerId`; a
    /// different validated peer polling the same `jobId` is refused
    /// `PeerUnauthorized` (401, no result body) — possession of the
    /// `jobId` is not authorization (GP 4). An absent record (the job has
    /// not finished, or never existed) is reported as `Pending` to every
    /// validated caller — deliberately the same answer for both, so an
    /// unknown `jobId` discloses nothing. A finished job reports its
    /// stored terminal status to its owner only. The status rides in
    /// `Result` as a serialised `PeerJobStatus<string>`, matching what
    /// the client transport's `PollJob` expects. `contractId` is part of
    /// the route for symmetry with the invoke leg; the store is keyed by
    /// scope + job id, so it is not needed for the lookup.
    let private jobStatusHandler (contractId: string) (jobId: Guid) : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let auth = getService<IPeerAuthProvider> ctx
            let fusion = tryGetService<PeerJobFusion> ctx

            match bearerToken ctx with
            | None -> return! writeJson 401 (JsonRpc.failure "" (PeerUnauthorized "missing bearer token")) next ctx
            | Some token ->
                let! validation = auth.ValidatePeerToken token |> Async.StartAsTask

                match validation with
                | Error e -> return! writeJson 401 (JsonRpc.failure "" e) next ctx
                | Ok principal ->
                    match fusion with
                    | None ->
                        return!
                            writeJson
                                200
                                (JsonRpc.failure "" (PeerHandler "peer job-fusion substrate is not enabled"))
                                next
                                ctx
                    | Some f ->
                        let! record = f.ResultStore.TryGetResult(PeerJob.Scope, jobId) |> Async.StartAsTask

                        let statusResponse (status: PeerJobStatus<string>) = {
                            JsonRpc = JsonRpc.version
                            Result = Some(JsonRpc.serialize status)
                            Error = None
                            Id = ""
                        }

                        match record with
                        | None -> return! writeJson 200 (statusResponse PeerJobStatus.Pending) next ctx
                        | Some r when r.OwnerPeerId <> "" && r.OwnerPeerId = principal.Caller.PeerId ->
                            return! writeJson 200 (statusResponse r.Status) next ctx
                        | Some _ ->
                            // Not the scheduling caller (or an owner-less
                            // pre-ownership record, which matches nobody):
                            // refuse without disclosing the stored status.
                            return!
                                writeJson
                                    401
                                    (JsonRpc.failure
                                        ""
                                        (PeerUnauthorized "peer job result is not owned by the calling peer"))
                                    next
                                    ctx
        }

    /// The peer host's Giraffe routes. Mount under the deployment's root
    /// router when the peer substrate is enabled. Every handler resolves
    /// its providers (`IPeerAuthProvider`, `IPlatformPeer`, the optional
    /// `PeerJobFusion`, `IAuditLog`) per-request from `ctx.RequestServices`
    /// — the compose hook registers them as DI singletons. The jobs route
    /// reports a clear "not enabled" error when no `PeerJobFusion` is
    /// registered.
    let routes: HttpHandler =
        choose [
            GET >=> route "/peer/v1/capabilities/profile" >=> capabilitiesProfileHandler
            GET >=> route "/peer/v1/capabilities" >=> capabilitiesHandler
            GET
            >=> routef "/peer/v1/%s/jobs/%O" (fun (contractId, jobId) -> jobStatusHandler contractId jobId)
            POST >=> routef "/peer/v1/%s" contractHandler
        ]