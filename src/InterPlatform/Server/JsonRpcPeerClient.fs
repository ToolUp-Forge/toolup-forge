// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Reflection
open Microsoft.FSharp.Reflection
open PeerReflection

// ─── Layer 4 — typed initiator proxy ─────────────────────────────────
//
// `JsonRpcPeerClient.create<'TApi>` reflects over a record-of-functions
// contract type and returns a live proxy: each field becomes a function
// that marshals its positional arguments, builds a `PeerCallContext`,
// calls the underlying `IPeerClient` transport, and deserialises the
// result into the field's declared return type. `Immediate` methods
// (`… -> Async<'T>`) resolve inline; `LongRunning` methods
// (`… -> Async<PeerJobHandle<'T>>`) return a handle whose `Poll` closure
// drives the transport's `PollJob`. A peer-side `PeerError` surfaces as a
// raised `PeerInvocationException` (the typed API presents `Async<'T>`,
// not `Async<Result<_,_>>`).

/// Per-proxy configuration: the transport, the target peer, the calling
/// identity + user context every call through this proxy vouches for, the
/// negotiated contract version, the contract id, and the hop budget a
/// fresh call starts with.
type PeerProxyConfig = {
    Client: IPeerClient
    Target: TargetPeer
    Caller: PeerIdentity
    User: UserContext
    Version: ContractVersion
    ContractId: string
    HopBudget: int
}

type private ProxyInvoker =

    static member private Context(config: PeerProxyConfig) : PeerCallContext = {
        Peer = config.Caller
        User = config.User
        ContractVersion = config.Version
        Route = [ config.Caller.PeerId ]
        RootRequestId = Guid.NewGuid().ToString()
        ParentRequestId = None
        HopsRemaining = config.HopBudget
    }

    static member Immediate<'R>
        (config: PeerProxyConfig, methodName: string, args: obj list, argTypes: Type list)
        : Async<'R> =
        async {
            let payload = {
                Context = ProxyInvoker.Context config
                Arguments = marshalArgs args argTypes
            }

            let! result = config.Client.Invoke(config.Target, config.ContractId, methodName, payload)

            match result with
            | Ok json -> return JsonRpc.deserialize<'R> json
            | Error e -> return raise (PeerInvocationException e)
        }

    static member LongRunning<'U>
        (config: PeerProxyConfig, methodName: string, args: obj list, argTypes: Type list)
        : Async<PeerJobHandle<'U>> =
        async {
            let payload = {
                Context = ProxyInvoker.Context config
                Arguments = marshalArgs args argTypes
            }

            let! result = config.Client.Invoke(config.Target, config.ContractId, methodName, payload)

            match result with
            | Error e -> return raise (PeerInvocationException e)
            | Ok jobIdJson ->
                let jobId = JsonRpc.deserialize<PeerJobId> jobIdJson

                let poll () = async {
                    let! status = config.Client.PollJob(config.Target, config.ContractId, jobId)

                    return
                        match status with
                        | Ok(Completed resultJson) -> Completed(JsonRpc.deserialize<'U> resultJson)
                        | Ok Pending -> Pending
                        | Ok(Failed e) -> Failed e
                        | Error e -> Failed e
                }

                return { JobId = jobId; Poll = poll }
        }

/// Builds typed peer proxies from a record-of-functions contract type.
module JsonRpcPeerClient =

    // NonPublic is load-bearing: `ProxyInvoker` is a `private` type, so F#
    // emits its (F#-public) static members with non-public IL visibility.
    // Without NonPublic the lookup returns null and the proxy build NREs.
    let private immediateMethod =
        typeof<ProxyInvoker>
            .GetMethod("Immediate", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)

    let private longRunningMethod =
        typeof<ProxyInvoker>
            .GetMethod("LongRunning", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)

    /// Reflect over `'TApi` (a record whose fields are contract methods)
    /// and return a live proxy bound to `config`. Each method call routes
    /// through the configured `IPeerClient` transport.
    let create<'TApi> (config: PeerProxyConfig) : 'TApi =
        let apiType = typeof<'TApi>

        if not (FSharpType.IsRecord apiType) then
            failwithf "JsonRpcPeerClient.create requires a record contract type; %s is not a record" apiType.Name

        let fieldValues =
            FSharpType.GetRecordFields apiType
            |> Array.map (fun field ->
                let methodName = field.Name
                let argTypes, retType = functionSignature field.PropertyType

                let isLongRunning =
                    retType.IsGenericType
                    && retType.GetGenericTypeDefinition() = typedefof<PeerJobHandle<_>>

                let rec build (collected: obj list) (cur: Type) : obj =
                    if FSharpType.IsFunction cur then
                        let _, range = FSharpType.GetFunctionElements cur
                        FSharpValue.MakeFunction(cur, (fun arg -> build (collected @ [ arg ]) range))
                    elif isLongRunning then
                        let u = retType.GetGenericArguments().[0]

                        longRunningMethod
                            .MakeGenericMethod(u)
                            .Invoke(null, [| box config; box methodName; box collected; box argTypes |])
                    else
                        immediateMethod
                            .MakeGenericMethod(retType)
                            .Invoke(null, [| box config; box methodName; box collected; box argTypes |])

                build [] field.PropertyType)

        FSharpValue.MakeRecord(apiType, fieldValues) :?> 'TApi