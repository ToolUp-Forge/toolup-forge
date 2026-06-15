// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Layer 5 — federation orchestration: cascade ─────────────────────
//
// A cascade is a multi-hop peer call: A calls B, and B — while handling
// A's call — forwards onward to C. The foundation already bakes the
// receiver-side guards into `DefaultPlatformPeer.Handle` (loop detection
// over `Route`, `HopsRemaining` budget). What the foundation does NOT
// do is the *outbound* bookkeeping a forwarding handler must perform:
// append itself to `Route`, decrement `HopsRemaining`, preserve the
// cascade-wide `RootRequestId`, and re-key `Peer` to itself. Hand-rolled
// at each forwarding site, that bookkeeping drifts; `IPeerCascade` makes
// it a first-class, single-sourced primitive — and fails a doomed hop
// (loop / budget-exhausted) *before* the wire round-trip, complementing
// the receiver's authoritative guard with caller-side defence in depth.
//
// Aggregation of fanned cascade results stays out of the SDK (see
// `IPeerFanout`); the cascade primitive owns routing bookkeeping only.
//
// Six portability rules (GP 12): identity by value (`PeerIdentity` /
// `PeerCallContext` records), async at the wire boundary (`Forward`
// returns `Async<_>`), guard outcomes as data (`PeerError`), stateless
// (every derivation is a pure function of the inbound context), and the
// hop budget is an explicit integer (precision at the lower bound).

/// Cascade-orchestration seam over the shipped peer foundation. The
/// default implementation is `DefaultPeerCascade`; the interface is a
/// seam so a deployment can layer its own trust-anchor policy on top of
/// the neutral routing bookkeeping (the SDK ships the bookkeeping; trust
/// policy beyond the foundation's per-hop auth is a deployment concern).
type IPeerCascade =
    /// Derive the outbound call context for the next hop from the context
    /// the local deployment is currently handling. Appends `localPeer` to
    /// `Route`, decrements `HopsRemaining`, preserves `RootRequestId`, and
    /// re-keys `Peer` to `localPeer`. Returns `PeerLoopDetected` if the
    /// next peer already appears on the route, or `PeerHopLimitExceeded`
    /// if no hop budget remains — both checked here, before any wire call.
    abstract NextHop:
        inbound: PeerCallContext * localPeer: PeerIdentity * next: TargetPeer -> Result<PeerCallContext, PeerError>

    /// Forward to the next hop: derive the outbound context via `NextHop`,
    /// then invoke `call` with it. A loop / budget rejection short-circuits
    /// to the structured error without calling `call` at all.
    abstract Forward:
        inbound: PeerCallContext *
        localPeer: PeerIdentity *
        next: TargetPeer *
        call: (PeerCallContext -> Async<Result<'T, PeerError>>) ->
            Async<Result<'T, PeerError>>

/// Default cascade orchestration. Pure routing bookkeeping over the
/// foundation's already-shipped `Route` / `HopsRemaining` semantics; holds
/// no state between hops (GP 12 rule 4).
type DefaultPeerCascade() =

    /// Pure derivation shared by `NextHop` and `Forward`. Kept module-free
    /// of side effects so it is trivially testable and matches the
    /// foundation's receiver-side guard order (loop before budget).
    static member deriveNext
        (inbound: PeerCallContext)
        (localPeer: PeerIdentity)
        (next: TargetPeer)
        : Result<PeerCallContext, PeerError> =
        // The route the call will carry once the local peer has forwarded
        // it — originator first, local peer appended (the receiver-side
        // guard in DefaultPlatformPeer.Handle checks this same shape).
        let extendedRoute = inbound.Route @ [ localPeer.PeerId ]

        // Loop: the next peer is already on the route (it would re-receive
        // a call it has handled), or the route itself already repeats.
        let nextId = next.Peer.PeerId

        let wouldLoop =
            List.contains nextId extendedRoute
            || (List.length (List.distinct extendedRoute) <> List.length extendedRoute)

        if wouldLoop then
            Error(PeerLoopDetected(extendedRoute @ [ nextId ]))
        elif inbound.HopsRemaining <= 0 then
            // No budget to make even one more hop.
            Error PeerHopLimitExceeded
        else
            Ok {
                inbound with
                    Peer = localPeer
                    Route = extendedRoute
                    HopsRemaining = inbound.HopsRemaining - 1
                    ParentRequestId = Some inbound.RootRequestId
            }

    interface IPeerCascade with
        member _.NextHop(inbound, localPeer, next) =
            DefaultPeerCascade.deriveNext inbound localPeer next

        member _.Forward(inbound, localPeer, next, call) = async {
            match DefaultPeerCascade.deriveNext inbound localPeer next with
            | Error e -> return Error e
            | Ok outbound -> return! call outbound
        }

[<RequireQualifiedAccess>]
module PeerCascade =
    /// Construct the default cascade orchestrator behind the `IPeerCascade`
    /// seam.
    let create () : IPeerCascade = DefaultPeerCascade() :> IPeerCascade

    /// Pure next-hop derivation, exposed module-style for call sites that
    /// want the bookkeeping without holding the interface.
    let deriveNext
        (inbound: PeerCallContext)
        (localPeer: PeerIdentity)
        (next: TargetPeer)
        : Result<PeerCallContext, PeerError> =
        DefaultPeerCascade.deriveNext inbound localPeer next