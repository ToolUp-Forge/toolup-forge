# Receiver-side cascade-budget authority — server-derived hop / route / root id

**Ships in:** ToolUp.InterPlatform (Phase 331).

The JSON-RPC peer host now **derives** the cascade bookkeeping on an inbound
contract call instead of copying it out of the request body. **It can refuse a
call that previously succeeded** — see [Rollout order](#rollout-order) — though
only for callers already sending shapes no in-tree client produces.

---

## What changes

`POST /peer/v1/{contractId}` rebuilt the trusted `PeerCallContext` from the
validated `PeerPrincipal` — but only two of its fields. `Peer` and `User` came
from the principal; `HopsRemaining`, `Route`, `RootRequestId` and
`ParentRequestId` were copied verbatim from `PeerWirePayload.Context`, which is
the caller's own JSON. The minted peer token carries none of those four, so they
were unauthenticated by construction.

`DefaultPlatformPeer.Handle` then ran its hop-limit and loop guards against
them. A peer sending `HopsRemaining = Int32.MaxValue` put the budget guard out
of reach; one sending `Route = []` put the loop guard out of reach; and any
`RootRequestId` it chose became the correlation id of the receiver's own audit
row. The guards were present and decorative.

The handler now calls `PeerCascadeAuthority.derive`, governed by a
`PeerCascadePolicy` resolved from DI:

| Field | Receiver's rule |
|---|---|
| `Route` | The validated caller is guaranteed to be on it, last. An honest route already is — `JsonRpcPeerClient.create` seeds `[ caller ]`, `PeerCascade.deriveNext` appends the forwarding peer — so it passes through untouched. |
| `HopsRemaining` | Clamped to `MaxHopsRemaining` (default **32**). The decrement stays sender-side in `deriveNext`; clamping is not a second decrement. |
| `RootRequestId` | Preserved when well-shaped — that is what keeps a cascade one cascade for cross-hop audit (GP 7) — and **minted by the receiver** when absent or unusable. |
| `ParentRequestId` | Derived from the inbound JSON-RPC envelope id, never from the body. `None` at the originating hop (a derived route naming only the caller). |
| `ContractVersion` | Left alone. `IPlatformPeer.Handle` already measures it against the receiver's own supported set, so it is checked against server-held data rather than trusted. |

Three shapes are refused outright, before dispatch:

- a `Route` entry that is empty, longer than `MaxIdentifierLength` (default
  **128**), or carries a control character → `PeerUnauthorized` (a route the
  receiver will not repeat into a log, an audit row, or the next hop);
- a `Route` deeper than `MaxRouteLength` (default **32**) →
  `PeerHopLimitExceeded` (a cascade past the receiver's declared depth is over
  budget however the budget field reads);
- a `Route` already naming the **receiver** → `PeerLoopDetected`, from the
  guard's new arm in `DefaultPlatformPeer`. No legitimate call carries it: a
  forwarding peer's own `deriveNext` refuses to send to a peer already on the
  route.

All three answer HTTP 200 with the structured JSON-RPC error, the same wire
shape the identical `PeerError` cases have always taken from `Handle`.

**What this does not claim.** Two colluding peers can still bounce a call
between themselves, each hop presenting a fresh in-ceiling budget and a route
naming only itself. No receiver-side rule can see that from a single message;
the bound there is the per-call ceiling plus the per-peer trust decision an
operator already makes by issuing a signing key. What is closed is the
*unilateral* escape — one peer, one call, claiming a budget or a history the
receiver never agreed to.

## What you must do

**Nothing**, if your federation matches the shipped guidance. The ceilings are
set far above it: `docs/migrations/18c-federation-hop-budget.md` sizes
`HopBudget` at "expected maximum hop depth + 1" and calls `8` generous, against
a default ceiling of 32.

Two cases need a compose-time line:

**A federation deeper than 32 hops or 32 route entries.** Raise the ceilings:

```fsharp
PeerServerApp.create ()
|> PeerServerApp.withConfig config
|> PeerServerApp.withLocalPeer thisPeerId
|> PeerServerApp.withCascadePolicy (
    PeerCascadePolicy.defaults
    |> PeerCascadePolicy.withMaxHopsRemaining 64
    |> PeerCascadePolicy.withMaxRouteLength 64)
|> PeerServerApp.run
```

**A tighter federation boundary than the defaults.** Same call, lower numbers —
a two-hop topology can say `withMaxHopsRemaining 2` and have every larger claim
clamped to it.

`LocalPeerId` on the policy you pass is ignored: `run` overwrites it with the
composed `LocalPeer`'s id. The receiver's own identity is the one field this
policy must not let anyone else name. **A deployment that composed no
`LocalPeer` leaves the receiver-on-route arm of the loop guard dormant** — the
same posture, and the same single cause, that
`PeerServerApp.enforceAudienceBinding` already reports as a startup advisory
(or a refusal under `withStrictAudienceBinding`). Composing `withLocalPeer` is
the fix for both.

## Rollout order

Receivers first, then callers — though for this phase the ordering is a
courtesy rather than a requirement, because nothing an in-tree caller emits is
affected.

1. **Upgrade receivers.** A receiver on this version accepts every call a
   pre-331 caller sends, provided that caller's route is well-shaped and within
   the ceilings. Both in-tree proxy entry points satisfy that by construction.
2. **Watch for refusals.** A `PeerUnauthorized` naming `Route`, or a
   `PeerHopLimitExceeded` on a call whose budget field looked healthy, means a
   counterparty is sending a route the receiver will not carry. Read it as a
   finding about that peer, not as a reason to widen the ceilings — then decide.
3. **Callers need no change at all.** `create` and `forward` are untouched, and
   the derived context is byte-for-byte what a `create` call and a `deriveNext`
   continuation already send (pinned by record-equality assertions in
   `PeerCascadeBudgetAuthorityTests`).

## Rollback

Remove the `withCascadePolicy` line, if you added one, to return to the
defaults. There is no switch that disables the derivation: a receiver that
copies the caller's budget is the defect this phase exists to close, not a
supported posture. To loosen it as far as it goes, set the ceilings to the
largest depth you are prepared to fund.

## See also

- [`314-cascade-aware-typed-proxy-forwarding.md`](314-cascade-aware-typed-proxy-forwarding.md)
  — the sender-side half; both are needed for the guards to bind end to end.
- [`18c-federation-hop-budget.md`](18c-federation-hop-budget.md) — sizing
  `HopBudget` for your topology, which is what the receiver's ceiling should sit
  above.
- [`315-peer-host-wire-hardening.md`](315-peer-host-wire-hardening.md) — the
  other receiver-side bound on the same route, and the reason the size check
  sits where it does relative to auth.
