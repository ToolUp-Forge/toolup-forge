// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.IO
open System.Reflection
open System.Text
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
//
// **Cascade authority (Phase 331).** "Rebuilds the call context from the
// validated principal" was true of `Peer` and `User` and of nothing else:
// `HopsRemaining`, `Route`, `RootRequestId` and `ParentRequestId` were
// copied verbatim out of the request body, and the minted token carries
// none of them, so they were unauthenticated by construction. The
// contract handler now derives all four through
// `PeerCascadeAuthority.derive` — see that file for the threat and the
// per-field rule — which is what makes the receiver's hop-limit and loop
// guards bind on something the caller cannot set.
//
// **Wire hardening (Phase 315).** Two wire-level fixes, both narrow:
// the contract route reads the inbound body under a configurable
// ceiling instead of buffering whatever arrives, and the job-poll route
// echoes the polled `jobId` as its JSON-RPC `Id` instead of `""`, so a
// poll response correlates to its request the way a dispatch response
// already does.

/// Phase 315 — the receiver-side wire limits the `/peer/v1/*` handlers
/// enforce. Resolved per-request from DI (`PeerCompose` registers the
/// composed value as a singleton); a host with none registered — a
/// partial test host, or any composition predating this phase — falls
/// back to `PeerWireLimits.defaults`, so this is a tunable and never a
/// required registration (GP 11 / GP 13).
type PeerWireLimits = {
    /// The largest inbound request body, in bytes, the contract route
    /// will accept. Enforced in two places because one is not enough: a
    /// declared `Content-Length` over the ceiling is refused without
    /// reading a byte, and a request that declares nothing (chunked
    /// transfer-encoding, which a hostile peer chooses freely) is
    /// refused the moment the bounded read passes the ceiling. Either
    /// way the receiver never holds more than `MaxRequestBytes` of a
    /// payload it has not agreed to.
    MaxRequestBytes: int64
}

[<RequireQualifiedAccess>]
module PeerWireLimits =

    /// 8 MiB.
    ///
    /// Deliberately generous rather than tight: a peer contract's
    /// arguments are a JSON array, and an 8 MiB argument array is far
    /// past anything the substrate is shaped for, so no existing
    /// deployment should meet this ceiling (GP 11). It is still a
    /// genuine narrowing — Kestrel's own `MaxRequestBodySize` default is
    /// 30 MB, and the point of this phase is that "auth-gated" bounds
    /// *who* can push a large body at the receiver, not *how large*.
    ///
    /// A deployment that genuinely exchanges larger payloads raises it
    /// with `PeerServerApp.withWireLimits`; one that wants a tighter
    /// federation boundary lowers it. Both are one line, and neither is
    /// a wire-format change — the ceiling is per-receiver policy.
    let defaultMaxRequestBytes = 8L * 1024L * 1024L

    /// The limits a composition that never says otherwise runs under.
    let defaults: PeerWireLimits = {
        MaxRequestBytes = defaultMaxRequestBytes
    }

    /// Narrow (or widen) the inbound contract-body ceiling.
    let withMaxRequestBytes (bytes: int64) (limits: PeerWireLimits) : PeerWireLimits = {
        limits with
            MaxRequestBytes = bytes
    }

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
    ///
    /// **Phase 310 — the correlation id rides too.** The execution side
    /// records the call's *terminal* outcome, and it has to file that row
    /// under the same `RootRequestId` as the schedule-time row this dispatch
    /// produces, or the pair cannot be joined. That id is the one Phase 331
    /// DERIVED on `trustedContext` — not the caller's asserted value — so
    /// taking it from the call context here is what keeps the terminal row
    /// as unforgeable as the schedule-time one.
    let private scheduleDispatch
        (fusion: PeerJobFusion)
        (handlerName: string)
        : PeerCallContext -> string -> Async<Result<string, PeerError>> =
        fun context argsJson -> async {
            let payload: PeerJobPayload = {
                OwnerPeerId = context.Peer.PeerId
                ArgsJson = argsJson
                RootRequestId = context.RootRequestId
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

                        // Phase 310 — the handler is told which contract
                        // method it serves, and given the fusion's audit
                        // log, so its terminal row is attributable. Both
                        // are known here and nowhere downstream: a job
                        // handler runs without a request.
                        let jobHandler =
                            PeerJobHandler(
                                funcValue,
                                argTypes,
                                innerType,
                                f.ResultStore,
                                f.AuditLog,
                                contractId,
                                field.Name
                            )
                            :> IJobHandler

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

    /// Phase 315 — read the request body under a ceiling, replacing the
    /// unbounded `ctx.ReadBodyFromRequestAsync()`.
    ///
    /// The old read buffered whatever arrived. Auth-gating narrows *who*
    /// can do that to peers holding a valid signing key, which is a real
    /// bound on the attacker set and no bound at all on the memory: one
    /// validated-but-hostile (or simply buggy) peer could hand the
    /// receiver an arbitrarily large string, and a federation deployment
    /// trusts its peers to be authentic, not to be well-behaved.
    ///
    /// Two checks, because either alone is bypassable:
    ///
    ///   * A declared `Content-Length` over the ceiling is refused
    ///     without reading a byte. Cheap, and the common honest case.
    ///   * The read itself stops at `cap + 1` bytes. `Content-Length` is
    ///     absent under chunked transfer-encoding — which the *caller*
    ///     chooses — and is in any case a claim, not a measurement, so a
    ///     header check alone would be a suggestion. This is what makes
    ///     the acceptance criterion true: the refusal happens before the
    ///     body is fully buffered, not after measuring it.
    ///
    /// Deliberately a single forward-only pass over `Request.Body` with
    /// no `EnableBuffering`, and it runs BEFORE the response starts. The
    /// estate has a standing hazard here: a stage that reads the request
    /// body after an earlier stage disposed it (or after the response has
    /// begun) throws where the framework's exception handler can no
    /// longer run, and the caller sees a 502 for a call that succeeded.
    /// Nothing downstream re-reads the body — `auditPeerCall` works from
    /// values already materialised here — so that shape is not created.
    let private readCappedBody
        (ctx: HttpContext)
        (limits: PeerWireLimits)
        : System.Threading.Tasks.Task<Result<string, PeerError>> =
        task {
            let cap = limits.MaxRequestBytes
            let declared = ctx.Request.ContentLength

            if declared.HasValue && declared.Value > cap then
                return Error(PeerRequestTooLarge cap)
            else
                let buffer = Array.zeroCreate<byte> 16384
                use collected = new MemoryStream()
                let mutable total = 0L
                let mutable overflowed = false
                let mutable finished = false

                while not finished do
                    let! read = ctx.Request.Body.ReadAsync(buffer, 0, buffer.Length, ctx.RequestAborted)

                    if read = 0 then
                        finished <- true
                    else
                        total <- total + int64 read

                        if total > cap then
                            // Stop at the ceiling: the bytes past it are
                            // never copied anywhere, and the ones before
                            // it are dropped with the MemoryStream.
                            overflowed <- true
                            finished <- true
                        else
                            collected.Write(buffer, 0, read)

                if overflowed then
                    return Error(PeerRequestTooLarge cap)
                else
                    return Ok(Encoding.UTF8.GetString(collected.ToArray()))
        }

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

    /// Phase 343 — authenticate an inbound token, fail-closed even when
    /// the provider *throws*.
    ///
    /// `IPeerAuthProvider` is contractually total: every defect is
    /// `Error (PeerUnauthorized …)`. The default provider was not, on one
    /// path — a signature segment that is not valid base64url reached
    /// `Base64Url.decode`, which throws, and `JwtCrypto.result` is a
    /// `Result` computation expression rather than an exception boundary,
    /// so it escaped to here and the handler answered **500**. That is
    /// fixed at source in `PeerJwt.verifySignature`; this is the backstop
    /// that stops the class recurring, because the *host* is where the
    /// consequence lives and it must not depend on every present and
    /// future provider being exception-free.
    ///
    /// It matters beyond tidiness: a status code an unauthenticated
    /// caller can flip at will is an error oracle, distinguishing
    /// "malformed encoding" from "wrong key" before any credential has
    /// been accepted. Every credential defect now answers 401 with the
    /// same shape.
    ///
    /// Cancellation is deliberately NOT swallowed — the `when` guard lets
    /// it propagate. A disconnected client is not a rejected credential,
    /// and turning one into a 401 would both lie in the logs and write to
    /// a response nobody is reading.
    let private authenticateWith
        (validate: unit -> Async<Result<PeerPrincipal, PeerError>>)
        : Async<Result<PeerPrincipal, PeerError>> =
        async {
            try
                return! validate ()
            with ex when not (ex :? OperationCanceledException) ->
                return
                    Error(
                        PeerUnauthorized
                            $"Peer token could not be validated ({ex.GetType().Name}) — refusing the call rather than reporting a server fault for a credential defect"
                    )
        }

    let private authenticate (auth: IPeerAuthProvider) (token: string) : Async<Result<PeerPrincipal, PeerError>> =
        authenticateWith (fun () -> auth.ValidatePeerToken token)

    /// Phase 629 — authenticate an inbound token FOR the contract the
    /// request addressed, so Phase 338's `cid` binding is enforced by the
    /// shipped host.
    ///
    /// **Without this, `ContractBoundCalls` was unreachable from an
    /// ordinary composition.** The scoped seam shipped in Phase 338 but
    /// nothing on the receiving side ever called it: the host validated
    /// every contract call through the plain `ValidatePeerToken`, which
    /// passes `expectedContract = None`. Under `ContractBoundCalls` that
    /// path deliberately REFUSES a `cid`-carrying token — "this receiver
    /// cannot see which contract the call is for" — so a deployment that
    /// composed the binding refused every scoped token its counterparties
    /// minted, and a deployment that did not compose it accepted a token
    /// minted for a different contract. Only a deployment that
    /// implemented its own validation path got the binding it asked for.
    /// The contract id is right here in the route, so the host is the
    /// only place that can supply it.
    ///
    /// **The Phase 338 claim ordering is preserved by construction**, not
    /// by re-implementing it: `ValidateScopedPeerToken` runs the same
    /// `PeerJwt.finishValidation` the unscoped path does, where the
    /// contract-scope check sits with the other claim checks and the
    /// replay claim is spent LAST — after the signature, `exp`, `nbf`,
    /// `aud` and `cid`. An unauthenticated caller still cannot burn
    /// seen-set entries with forged tokens.
    ///
    /// A provider that does not implement `IPeerCallScopedAuth` falls
    /// back to the plain path — it has no binding to enforce. And under
    /// the default `UnscopedCalls` policy `ValidateScopedPeerToken` is
    /// `ValidatePeerToken` with the contract id discarded, so every
    /// pre-629 composition validates byte-for-byte as it did (GP 11).
    /// The other three routes stay on the unscoped path deliberately:
    /// capability discovery and a job poll are not contract dispatch, and
    /// a token bound to a contract is not being spent against one there.
    let private authenticateForContract
        (auth: IPeerAuthProvider)
        (contractId: string)
        (token: string)
        : Async<Result<PeerPrincipal, PeerError>> =
        match auth with
        | :? IPeerCallScopedAuth as scoped ->
            authenticateWith (fun () -> scoped.ValidateScopedPeerToken(token, contractId))
        | _ -> authenticate auth token

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
                // Phase 629 — validated FOR this contract, so a composed
                // `ContractBoundCalls` policy binds the `cid` claim here
                // rather than needing a deployment-authored validation
                // path. Same position in the sequence the unscoped call
                // occupied: before the delegation check, before the body
                // is read.
                let! validation = authenticateForContract auth contractId token |> Async.StartAsTask

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
                        // Phase 315 — the size ceiling sits HERE, after
                        // the credential and delegation checks and
                        // before the read, which is the only position
                        // that satisfies both neighbours. Moving it
                        // above auth would answer 413 to an
                        // unauthenticated caller, reopening exactly the
                        // status-code oracle Phase 343 closed; moving it
                        // below the read would be a measurement, not a
                        // limit. Phase 330's ordering is untouched — the
                        // body is still not read until the delegation
                        // has been verified.
                        let limits =
                            tryGetService<PeerWireLimits> ctx |> Option.defaultValue PeerWireLimits.defaults

                        let! bodyResult = readCappedBody ctx limits

                        match bodyResult with
                        // The request id is unknown — it lives in the
                        // body we declined to read — so the refusal
                        // carries `""`, the same as every other
                        // pre-parse failure on this route.
                        | Error e -> return! writeJson 413 (JsonRpc.failure "" e) next ctx
                        | Ok body ->
                            match tryParse body with
                            | Error e -> return! writeJson 400 (JsonRpc.failure "" e) next ctx
                            | Ok(request, payload) ->
                                // Phase 331 — the trusted context is
                                // DERIVED, not copied. Identity came from
                                // the validated principal before this
                                // phase (and the `Delegated` originator
                                // inside it has been signature-verified
                                // above, Phase 330); the cascade
                                // bookkeeping — hop budget, route
                                // history, correlation ids — was still
                                // copied verbatim from the wire, which is
                                // the half the caller controls and the
                                // half the receiver's guards run on.
                                let cascade =
                                    tryGetService<PeerCascadePolicy> ctx
                                    |> Option.defaultValue PeerCascadePolicy.defaults

                                let derived =
                                    PeerCascadeAuthority.derive
                                        cascade
                                        principal.Caller
                                        principal.User
                                        request.Id
                                        payload.Context

                                match derived with
                                // A refused shape answers 200 with the
                                // structured error, the same wire shape
                                // `Handle` gives the identical `PeerError`
                                // cases — one answer per case, whichever
                                // stage raised it. No audit row, matching
                                // every other pre-dispatch refusal on this
                                // route (the parse failure above, the size
                                // ceiling before it): nothing was
                                // dispatched, and the row would have to be
                                // filed under a correlation id the
                                // receiver just declined to accept.
                                | Error e -> return! writeJson 200 (JsonRpc.failure request.Id e) next ctx
                                | Ok trustedContext ->
                                    let! result =
                                        peer.Handle(contractId, trustedContext, request.Method, payload.Arguments)
                                        |> Async.StartAsTask

                                    // The audit row carries the DERIVED
                                    // correlation id, so a caller cannot
                                    // file its calls under somebody else's
                                    // cascade (or under an unbounded
                                    // string) in this receiver's log.
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
                let! validation = authenticate auth token |> Async.StartAsTask

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
                let! validation = authenticate auth token |> Async.StartAsTask

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
    ///
    /// **Phase 315 — the response `Id` correlates.** Every response this
    /// route emits carries the polled `jobId` as its JSON-RPC `Id`,
    /// where all of them previously carried `""`. A dispatch response
    /// echoes `request.Id`, so a caller can pair it with the call that
    /// produced it; a poll response could not be paired with anything,
    /// which is a hole in the wire's own correlation contract and a real
    /// problem for a non-F# peer SDK that pipelines polls over one
    /// connection. The poll is a `GET` and carries no JSON-RPC request
    /// envelope, so there is no request id to echo — the `jobId` is the
    /// only identifier both sides already agree on, and it is the one
    /// the caller addressed the request with.
    ///
    /// The in-tree client ignores `Id` on this leg (`HttpPeerClient`
    /// reads `Result` / `Error` only), so no existing caller observes a
    /// behaviour change — it gains a field it was not reading (GP 11).
    /// Echoing it on the refusal paths too discloses nothing: it is the
    /// value the caller put in the URL.
    let private jobStatusHandler (contractId: string) (jobId: Guid) : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let auth = getService<IPeerAuthProvider> ctx
            let fusion = tryGetService<PeerJobFusion> ctx
            let correlationId = string jobId

            match bearerToken ctx with
            | None ->
                return! writeJson 401 (JsonRpc.failure correlationId (PeerUnauthorized "missing bearer token")) next ctx
            | Some token ->
                let! validation = authenticate auth token |> Async.StartAsTask

                match validation with
                | Error e -> return! writeJson 401 (JsonRpc.failure correlationId e) next ctx
                | Ok principal ->
                    match fusion with
                    | None ->
                        return!
                            writeJson
                                200
                                (JsonRpc.failure correlationId (PeerHandler "peer job-fusion substrate is not enabled"))
                                next
                                ctx
                    | Some f ->
                        let! record = f.ResultStore.TryGetResult(PeerJob.Scope, jobId) |> Async.StartAsTask

                        let statusResponse (status: PeerJobStatus<string>) = {
                            JsonRpc = JsonRpc.version
                            Result = Some(JsonRpc.serialize status)
                            Error = None
                            Id = correlationId
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
                                        correlationId
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