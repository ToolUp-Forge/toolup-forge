// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System.Collections.Concurrent

// ─── Layer 4 — default receiver host ─────────────────────────────────
//
// `DefaultPlatformPeer` is the in-process default implementation of
// `IPlatformPeer`: a registry of contracts (registered at compose time)
// plus a `Handle` that runs the cascade guards before dispatching. It
// holds no transport — the JSON-RPC host authenticates the caller,
// rebuilds the `PeerCallContext`, and calls `Handle`; this type only
// owns the contract table and the guard order.
//
// Guard order in `Handle` (all checked here so they run regardless of
// which host invokes the dispatch):
//   1. Unknown contract id            → `PeerContractNotFound`
//   2. Version not in supported set   → `PeerVersionMismatch`
//   3. No remaining hop budget        → `PeerHopLimitExceeded`
//   4. A repeated peer id in `Route`  → `PeerLoopDetected`
// Only after all four pass does the registered `Dispatch` run.

/// In-process `IPlatformPeer`. The contract table is a concurrent
/// dictionary keyed by contract id; registration is idempotent
/// (re-registering an id overwrites). Stateless between dispatches
/// (GP 12 rule 4) — `Handle` reads only the table and the per-call
/// context.
type DefaultPlatformPeer() =
    let contracts = ConcurrentDictionary<string, PeerContractRegistration>()

    /// A route with any repeated peer id is a cascade loop. Returns the
    /// offending route so the caller can diagnose where it doubled back.
    let detectLoop (route: string list) =
        if List.length (List.distinct route) <> List.length route then
            Some route
        else
            None

    interface IPlatformPeer with
        member _.RegisterContract(registration: PeerContractRegistration) =
            contracts[registration.ContractId] <- registration

        member _.Handle(contractId: string, context: PeerCallContext, methodName: string, arguments: string) = async {
            match contracts.TryGetValue contractId with
            | false, _ -> return Error(PeerContractNotFound contractId)
            | true, reg ->
                if not (List.contains context.ContractVersion reg.Versions) then
                    return Error(PeerVersionMismatch(context.ContractVersion, reg.Versions))
                elif context.HopsRemaining <= 0 then
                    return Error PeerHopLimitExceeded
                else
                    match detectLoop context.Route with
                    | Some route -> return Error(PeerLoopDetected route)
                    | None -> return! reg.Dispatch context methodName arguments
        }

        member _.Capabilities() = async {
            return
                contracts.Values
                |> Seq.map (fun reg -> {
                    ContractId = reg.ContractId
                    Versions = reg.Versions
                })
                |> List.ofSeq
        }