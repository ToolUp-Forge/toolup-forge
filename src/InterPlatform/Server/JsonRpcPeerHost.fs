// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Reflection
open Microsoft.AspNetCore.Http
open Microsoft.FSharp.Reflection
open Giraffe
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

    /// Reflect over `'TApi` (a record whose fields are contract methods)
    /// and return a `PeerContractRegistration` bound to `impl`. Each
    /// `Immediate` method (`… -> Async<'T>`) dispatches inline.
    /// `LongRunning` methods (`… -> Async<PeerJobHandle<'T>>`) require the
    /// job-fusion substrate and fail with a clear `PeerHandler` error
    /// until it lands.
    let contract<'TApi> (contractId: string) (versions: ContractVersion list) (impl: 'TApi) : PeerContractRegistration =
        let apiType = typeof<'TApi>

        if not (FSharpType.IsRecord apiType) then
            failwithf "JsonRpcPeerHost.contract requires a record contract type; %s is not a record" apiType.Name

        let implObj = box impl

        let methodMap =
            FSharpType.GetRecordFields apiType
            |> Array.map (fun field ->
                let argTypes, retType = functionSignature field.PropertyType

                let isLongRunning =
                    retType.IsGenericType
                    && retType.GetGenericTypeDefinition() = typedefof<PeerJobHandle<_>>

                let handler =
                    if isLongRunning then
                        fun (_: string) -> async {
                            return
                                Error(
                                    PeerHandler
                                        $"Long-running contract method '{field.Name}' requires the peer job-fusion substrate, which is not yet enabled"
                                )
                        }
                    else
                        immediateDispatch implObj field argTypes retType

                field.Name, handler)
            |> Map.ofArray

        let dispatch: PeerDispatch =
            fun _context methodName argsJson ->
                match Map.tryFind methodName methodMap with
                | Some handler -> handler argsJson
                | None -> async { return Error(PeerMethodNotFound methodName) }

        {
            ContractId = contractId
            Versions = versions
            Dispatch = dispatch
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

    /// The peer host's Giraffe routes. Mount under the deployment's root
    /// router when the peer substrate is enabled.
    let routes (auth: IPeerAuthProvider) (peer: IPlatformPeer) : HttpHandler =
        choose [
            GET >=> route "/peer/v1/capabilities" >=> capabilitiesHandler auth peer
            POST >=> routef "/peer/v1/%s" (contractHandler auth peer)
        ]