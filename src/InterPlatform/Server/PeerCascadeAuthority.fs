// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System

// ─── Layer 5 — federation orchestration: receiver-side authority ─────
//
// The receiver-side dual of `PeerCascade` (Phase 331 / Phase 314).
//
// `PeerCascade.deriveNext` is the *sender's* bookkeeping: a forwarding
// deployment appends itself to `Route`, decrements `HopsRemaining`, and
// preserves `RootRequestId`. That is defence in depth on the side that
// has every reason to be honest. The receiver's problem is the other
// one: those four cascade fields arrive inside `PeerWirePayload.Context`,
// which is a self-assertion. The peer token authenticates *who is
// calling*; it carries no cascade fields at all, so nothing about the
// budget, the route history or the correlation id is authenticated by
// construction.
//
// Left copied verbatim (the pre-331 host did exactly that) a peer sets
// `HopsRemaining = Int32.MaxValue` to make the receiver's hop-limit
// guard unreachable, `Route = []` to make its loop guard unreachable,
// and any `RootRequestId` it likes to attribute its calls to somebody
// else's cascade in the receiver's own audit log. The guards are then
// evaluating attacker-supplied data — present, and decorative.
//
// So the receiver derives them instead, from what it has actually
// verified plus its own policy:
//
//   * **`Route`** — the validated caller is guaranteed to be on it. A
//     well-behaved caller already puts itself last (both
//     `JsonRpcPeerClient.create` and `PeerCascade.deriveNext` do), so
//     that route passes through untouched; a stripped or truncated one
//     gains the caller back. The receiver cannot recover hops the caller
//     erased *upstream* of itself — nobody can, from one message — but a
//     caller can no longer erase its OWN participation, which is what
//     the next hop's loop detection runs on.
//   * **`HopsRemaining`** — clamped to the receiver's own ceiling. The
//     decrement stays where it belongs, sender-side in `deriveNext`:
//     decrementing here as well would spend a legitimate cascade's
//     budget twice and change the behaviour of every existing
//     deployment (GP 11). Clamping changes nothing for a caller inside
//     the ceiling and makes `Int32.MaxValue` worth exactly the ceiling.
//   * **`RootRequestId`** — minted by the receiver when absent or
//     unusable, preserved when the caller supplied a well-shaped one
//     (that is what makes a cascade one cascade — GP 7). Always
//     shape-bounded, so it cannot be an unbounded or control-character
//     payload riding into an audit surface.
//   * **`ParentRequestId`** — derived from the inbound JSON-RPC request
//     id, which is the id this receiver will echo in its response, and
//     never copied from the body.
//
// What this does NOT claim: two colluding peers can still bounce a call
// between themselves, each hop presenting a fresh in-ceiling budget and
// a route naming only itself. No receiver-side rule can see that from a
// single message — the bound there is the ceiling per call plus the
// per-peer trust decision the operator already makes by issuing a
// signing key. What is closed is the *unilateral* escape: one peer, one
// call, claiming a budget or a history the receiver never agreed to.
//
// Six portability rules (GP 12): the policy and the derivation are
// values and pure functions (identity by value, stateless), guard
// outcomes stay `PeerError` data, and the ceilings are explicit
// integers.

/// Receiver-side cascade policy: the ceilings a deployment is willing to
/// honour on the self-asserted cascade fields, plus its own peer id.
///
/// Resolved per-request from DI (`PeerCompose` registers the composed
/// value as a singleton); a host with none registered — a partial test
/// host, or any composition predating this phase — falls back to
/// `PeerCascadePolicy.defaults`, so this is a tunable and never a
/// required registration (GP 11 / GP 13).
type PeerCascadePolicy = {
    /// This deployment's own peer id, used to refuse a call whose route
    /// already names the receiver. Filled in by `PeerServerApp.run` from
    /// the composed `LocalPeer`; blank when no local identity was
    /// composed, in which case that one guard simply never fires — the
    /// same posture the audience-binding check takes for the same
    /// reason (`PeerServerApp.auditAudienceBinding`).
    LocalPeerId: string
    /// The largest hop budget the receiver will carry forward from a
    /// wire assertion. An inbound `HopsRemaining` above this is clamped
    /// down to it; one below is honoured as-is.
    MaxHopsRemaining: int
    /// The deepest route the receiver will accept. A longer one is
    /// refused as `PeerHopLimitExceeded` — a cascade past the receiver's
    /// declared depth is over budget however the budget field reads.
    MaxRouteLength: int
    /// The longest correlation id / route entry the receiver will carry.
    /// Bounds `RootRequestId`, `ParentRequestId` and every `Route` entry.
    MaxIdentifierLength: int
}

[<RequireQualifiedAccess>]
module PeerCascadePolicy =

    /// 32 hops.
    ///
    /// Deliberately generous rather than tight, for the same reason the
    /// wire ceiling is: the documented `HopBudget` guidance sizes a
    /// federation at "expected maximum hop depth + 1" and calls `8`
    /// generous, so no existing deployment should meet this (GP 11). It
    /// is still a genuine bound — `Int32.MaxValue` is not 32 — and a
    /// deployment that wants a tighter federation boundary lowers it.
    let defaultMaxHopsRemaining = 32

    /// 32 entries — the route counterpart of the hop ceiling. A route
    /// deeper than the budget could ever have paid for is over budget.
    let defaultMaxRouteLength = 32

    /// 128 characters. A peer id is a deployment-assigned label and a
    /// correlation id is a GUID (36); 128 leaves room for a prefixed
    /// scheme without leaving room for a payload.
    let defaultMaxIdentifierLength = 128

    /// The policy a composition that never says otherwise runs under.
    /// `LocalPeerId` is blank here and filled in at compose time from the
    /// deployment's own `LocalPeer`.
    let defaults: PeerCascadePolicy = {
        LocalPeerId = ""
        MaxHopsRemaining = defaultMaxHopsRemaining
        MaxRouteLength = defaultMaxRouteLength
        MaxIdentifierLength = defaultMaxIdentifierLength
    }

    /// Declare the receiver's own peer id. `PeerServerApp.run` does this
    /// from the composed `LocalPeer`; a hand-built host sets it here.
    let withLocalPeerId (peerId: string) (policy: PeerCascadePolicy) : PeerCascadePolicy = {
        policy with
            LocalPeerId = peerId
    }

    /// Narrow (or widen) the hop-budget ceiling.
    let withMaxHopsRemaining (hops: int) (policy: PeerCascadePolicy) : PeerCascadePolicy = {
        policy with
            MaxHopsRemaining = hops
    }

    /// Narrow (or widen) the accepted route depth.
    let withMaxRouteLength (entries: int) (policy: PeerCascadePolicy) : PeerCascadePolicy = {
        policy with
            MaxRouteLength = entries
    }

    /// Narrow (or widen) the accepted correlation-id / route-entry
    /// length.
    let withMaxIdentifierLength (chars: int) (policy: PeerCascadePolicy) : PeerCascadePolicy = {
        policy with
            MaxIdentifierLength = chars
    }

/// Receiver-side derivation of the trusted `PeerCallContext`. Pure and
/// total — every rejection is a `PeerError`, never an exception — so a
/// deployment can run the same derivation in its own tests without
/// standing up a host.
[<RequireQualifiedAccess>]
module PeerCascadeAuthority =

    /// The refusal reason for a route carrying an entry the receiver will
    /// not repeat into a log, an audit row, or the next hop's route.
    let malformedRouteEntry =
        "peer-cascade: the call's asserted Route carries an entry that is empty, over-length, or contains control characters. The receiver refuses a route it cannot safely carry forward rather than sanitising one it was never able to verify."

    /// Is `value` an identifier the receiver is willing to carry: present,
    /// within the length ceiling, and free of control characters (which
    /// have no place in a peer id or a correlation id and every place in
    /// a log-injection payload)?
    let isWellShapedIdentifier (maxLength: int) (value: string) : bool =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= maxLength
        && not (value |> Seq.exists Char.IsControl)

    /// Derive the trusted call context for an inbound request.
    ///
    /// `caller` / `user` come from the validated `PeerPrincipal` (the
    /// pre-331 host already rebuilt those two); `requestId` is the
    /// inbound JSON-RPC envelope id; `wire` is the self-asserted context
    /// from the request body, of which only `ContractVersion` survives
    /// unexamined — and that one is checked against the receiver's own
    /// supported set by `IPlatformPeer.Handle` a moment later, so it is
    /// already measured against server-held data rather than trusted.
    ///
    /// The loop and hop-limit *guards* stay in `DefaultPlatformPeer.Handle`
    /// where they have always been; this function's job is to make sure
    /// the values those guards run on are the receiver's, not the
    /// caller's.
    let derive
        (policy: PeerCascadePolicy)
        (caller: PeerIdentity)
        (user: UserContext)
        (requestId: string)
        (wire: PeerCallContext)
        : Result<PeerCallContext, PeerError> =
        let wellShaped = isWellShapedIdentifier policy.MaxIdentifierLength

        if wire.Route |> List.exists (wellShaped >> not) then
            Error(PeerUnauthorized malformedRouteEntry)
        elif List.length wire.Route > policy.MaxRouteLength then
            // Not literally a repeat, so not `PeerLoopDetected`: a route
            // deeper than the receiver's ceiling is a cascade that has
            // outrun the budget the receiver is prepared to fund, which
            // is exactly what `PeerHopLimitExceeded` names — and it is a
            // case every peer, including one on a pre-331 SDK, already
            // deserialises.
            Error PeerHopLimitExceeded
        else
            // The validated caller is on the route, last. A well-behaved
            // caller already put it there (`create` seeds
            // `[ caller ]`, `deriveNext` appends the forwarding peer), so
            // this is a no-op on every honest call and byte-for-byte
            // GP 11; a stripped route gains back the one hop the receiver
            // can actually prove.
            let route =
                match List.tryLast wire.Route with
                | Some last when last = caller.PeerId -> wire.Route
                | _ -> wire.Route @ [ caller.PeerId ]

            // A correlation id survives when the caller supplied a usable
            // one — that is what keeps a cascade a single cascade for
            // audit (GP 7) — and is minted here otherwise. "Otherwise"
            // covers both the first hop, where there is nothing to
            // preserve, and a caller that supplied something the receiver
            // will not carry.
            let rootRequestId =
                if wellShaped wire.RootRequestId then
                    wire.RootRequestId
                else
                    Guid.NewGuid().ToString()

            // The parent of the work about to run is the inbound request
            // itself, identified by the envelope id the receiver will
            // echo — never by the body's own `ParentRequestId`, which is
            // the field a caller would edit to attribute its call to
            // somebody else's parent. At the originating hop there is no
            // parent, and the derived route is what says so: one entry
            // means the validated caller is the only peer on it. Both
            // legitimate shapes come out exactly as they went in — `None`
            // for a `create` call, `Some rootRequestId` for a
            // `deriveNext` continuation, whose envelope id is its root id.
            let parentRequestId =
                if wellShaped requestId && List.length route > 1 then
                    Some requestId
                else
                    None

            Ok {
                wire with
                    Peer = caller
                    User = user
                    Route = route
                    RootRequestId = rootRequestId
                    ParentRequestId = parentRequestId
                    HopsRemaining = min wire.HopsRemaining policy.MaxHopsRemaining
            }