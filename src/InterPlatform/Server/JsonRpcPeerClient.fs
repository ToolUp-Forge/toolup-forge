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
//
// Two entry points, and the distinction is load-bearing (Phase 314):
//
//   * `create` starts a NEW cascade root — fresh `RootRequestId`,
//     `Route = [ caller ]`, `HopsRemaining = HopBudget`. Correct for a
//     call the local deployment originates.
//   * `forward inbound` CONTINUES the cascade the local deployment is
//     currently handling — the outbound context is derived from `inbound`
//     by `PeerCascade.deriveNext`, so `RootRequestId` survives the hop,
//     `Route` gains the local peer, and the hop budget keeps counting
//     down. A doomed hop (loop / exhausted budget) short-circuits to the
//     structured `PeerError` before the wire round-trip, matching the
//     caller-side defence in depth `IPeerCascade.Forward` already gives.
//
// A handler that forwards with `create` silently resets all three, so
// loop detection and hop limits stop spanning the cascade and the
// cross-hop audit correlation is lost. `create` remains the default and
// is byte-for-byte unchanged (GP 11); `forward` is the opt-in.

/// Per-proxy configuration: the transport, the target peer, the calling
/// identity + user context every call through this proxy vouches for, the
/// negotiated contract version, the contract id, and the hop budget a
/// fresh call starts with.
///
/// `HopBudget` seeds a *new* cascade root only. A proxy built with
/// `JsonRpcPeerClient.forward` inherits the remaining budget from the
/// inbound context it continues and ignores this field.
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

    /// The context a single call through the proxy carries.
    ///
    /// `inbound = None` mints a fresh cascade root — the behaviour every
    /// `create` proxy has always had. `Some ctx` continues that cascade
    /// via the single-sourced `PeerCascade.deriveNext` bookkeeping, which
    /// preserves `RootRequestId`, appends the local peer to `Route`,
    /// decrements `HopsRemaining`, and sets `ParentRequestId` — and
    /// rejects a loop / exhausted budget as a structured `PeerError`
    /// rather than letting a doomed hop reach the wire.
    ///
    /// The split of ownership on the continuation path: the four cascade
    /// fields come from the INBOUND context (that is what makes it one
    /// cascade); the identity + version fields come from the proxy CONFIG
    /// (`Peer` via `deriveNext`'s re-key to `localPeer`, plus `User` and
    /// `ContractVersion` below). A forwarding deployment vouches for the
    /// outbound leg itself, on the contract version it negotiated for
    /// *that* leg — propagate the inbound end-user identity deliberately
    /// with `{ config with User = inbound.User }`, never by accident.
    static member private Context
        (config: PeerProxyConfig, inbound: PeerCallContext option)
        : Result<PeerCallContext, PeerError> =
        match inbound with
        | None ->
            Ok {
                Peer = config.Caller
                User = config.User
                ContractVersion = config.Version
                Route = [ config.Caller.PeerId ]
                RootRequestId = Guid.NewGuid().ToString()
                ParentRequestId = None
                HopsRemaining = config.HopBudget
            }
        | Some ctx ->
            PeerCascade.deriveNext ctx config.Caller config.Target
            |> Result.map (fun outbound -> {
                outbound with
                    User = config.User
                    ContractVersion = config.Version
            })

    static member Immediate<'R>
        (
            config: PeerProxyConfig,
            inbound: PeerCallContext option,
            methodName: string,
            args: obj list,
            argTypes: Type list
        ) : Async<'R> =
        async {
            match ProxyInvoker.Context(config, inbound) with
            | Error e ->
                // A doomed hop never leaves the forwarding peer.
                return raise (PeerInvocationException e)
            | Ok context ->
                let payload = {
                    Context = context
                    Arguments = marshalArgs args argTypes
                }

                let! result = config.Client.Invoke(config.Target, config.ContractId, methodName, payload)

                match result with
                | Ok json -> return JsonRpc.deserialize<'R> json
                | Error e -> return raise (PeerInvocationException e)
        }

    static member LongRunning<'U>
        (
            config: PeerProxyConfig,
            inbound: PeerCallContext option,
            methodName: string,
            args: obj list,
            argTypes: Type list
        ) : Async<PeerJobHandle<'U>> =
        async {
            match ProxyInvoker.Context(config, inbound) with
            | Error e ->
                // A doomed hop never leaves the forwarding peer.
                return raise (PeerInvocationException e)
            | Ok context ->
                let payload = {
                    Context = context
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

    /// Shared proxy builder. `inbound = None` is the cascade root
    /// (`create`); `Some ctx` continues an inbound cascade (`forward`).
    /// The reflection shape is identical either way — only the context
    /// each call seeds itself with differs.
    let private proxy<'TApi> (inbound: PeerCallContext option) (config: PeerProxyConfig) : 'TApi =
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
                        let u = retType.GetGenericArguments()[0]

                        longRunningMethod
                            .MakeGenericMethod(u)
                            .Invoke(null, [| box config; box inbound; box methodName; box collected; box argTypes |])
                    else
                        immediateMethod
                            .MakeGenericMethod(retType)
                            .Invoke(null, [| box config; box inbound; box methodName; box collected; box argTypes |])

                build [] field.PropertyType)

        FSharpValue.MakeRecord(apiType, fieldValues) :?> 'TApi

    /// Reflect over `'TApi` (a record whose fields are contract methods)
    /// and return a live proxy bound to `config`. Each method call routes
    /// through the configured `IPeerClient` transport.
    ///
    /// Every call through this proxy starts a **new cascade root**: a
    /// fresh `RootRequestId`, `Route = [ config.Caller.PeerId ]`, and
    /// `HopsRemaining = config.HopBudget`. That is right for a call the
    /// local deployment originates, and wrong for one it is forwarding —
    /// use `forward` to continue an inbound cascade.
    let create<'TApi> (config: PeerProxyConfig) : 'TApi = proxy<'TApi> None config

    /// Like `create`, but every call through the returned proxy
    /// **continues the cascade** described by `inbound` rather than
    /// starting a new one. The outbound context is derived per call by
    /// `PeerCascade.deriveNext inbound config.Caller config.Target`, so
    /// `RootRequestId` survives the hop (GP 7 — correlation rides the
    /// chain, across hops), `Route` gains `config.Caller`,
    /// `HopsRemaining` counts down from the inbound budget, and
    /// `ParentRequestId` points at the cascade root.
    ///
    /// `config.HopBudget` is ignored — the budget belongs to the cascade,
    /// not to this leg. `config.User` and `config.Version` still apply:
    /// the forwarding deployment vouches for the outbound leg itself, on
    /// the version it negotiated for that leg. To propagate the inbound
    /// end-user identity, say so — `{ config with User = inbound.User }`.
    ///
    /// A derivation rejection (`PeerLoopDetected` / `PeerHopLimitExceeded`)
    /// short-circuits the call to a raised `PeerInvocationException`
    /// **before** the wire round-trip, so a doomed hop never leaves the
    /// forwarding peer — the same caller-side defence in depth
    /// `IPeerCascade.Forward` provides, now available to the typed proxy.
    let forward<'TApi> (inbound: PeerCallContext) (config: PeerProxyConfig) : 'TApi = proxy<'TApi> (Some inbound) config