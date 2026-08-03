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
//   4. A repeated peer id in `Route`, or the receiver's own id already on
//      it                             → `PeerLoopDetected`
// Only after all four pass does the registered `Dispatch` run.
//
// **Phase 331 — what the guards run on.** The budget and the route are
// wire fields, and until Phase 331 the host copied them verbatim out of
// the request body, so guards 3 and 4 were evaluating numbers the caller
// chose: `HopsRemaining = Int32.MaxValue` made guard 3 unreachable and
// `Route = []` made guard 4 unreachable. The host now derives the
// context through `PeerCascadeAuthority.derive` before calling `Handle`
// — the budget is clamped to the receiver's ceiling and the validated
// caller is guaranteed to be on the route — so the guards below bind on
// server-derived values. The guards themselves are unchanged; what
// changed is that they can no longer be waved away from the wire.
//
// The receiver-on-route arm of guard 4 is new with Phase 331 and lives
// here rather than in the derivation because this is where the loop
// question is already asked. It needs the deployment's own id, which
// arrives through the primary constructor; the parameterless
// `DefaultPlatformPeer()` — the pre-331 shape, still used by partial
// hosts and tests — leaves it blank and that arm simply never fires
// (GP 11). No legitimate call can carry it: a forwarding peer's own
// `PeerCascade.deriveNext` refuses to send to a peer already on the
// route, so a route naming the receiver is either a doubled-back cascade
// or a fabricated one.

/// In-process `IPlatformPeer`. The contract table is a concurrent
/// dictionary keyed by contract id; registration is idempotent
/// (re-registering an id overwrites). Stateless between dispatches
/// (GP 12 rule 4) — `Handle` reads only the table, the per-call context,
/// and the immutable local peer id.
type DefaultPlatformPeer(localPeerId: string) =
    let contracts = ConcurrentDictionary<string, PeerContractRegistration>()

    /// A route with any repeated peer id — or one already naming this
    /// deployment — is a cascade loop. Returns the offending route so the
    /// caller can diagnose where it doubled back.
    let detectLoop (route: string list) =
        if List.length (List.distinct route) <> List.length route then
            Some route
        elif
            not (System.String.IsNullOrWhiteSpace localPeerId)
            && List.contains localPeerId route
        then
            Some route
        else
            None

    /// The pre-331 shape: no declared local identity, so the
    /// receiver-on-route arm of the loop guard never fires. Kept so every
    /// existing construction site — partial test hosts included — is
    /// source-compatible (GP 11).
    new() = DefaultPlatformPeer("")

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