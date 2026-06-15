# Phase 18c — federation cascade: sizing `HopBudget`

> Setup guide for first-time federation. The cascade primitives
> (`IPeerCascade` / `DefaultPeerCascade`) ship without consumer-side code
> changes — this doc exists because the one knob a consumer **must** size
> for their topology, `HopBudget`, was previously undocumented, and an
> under-sized budget fails calls silently at runtime.

## What this is

When a deployment forwards a peer call onward to a further peer (an
A → B → C cascade), the call carries two routing fields in its
`PeerCallContext`:

- **`Route`** — the ordered list of peers the call has already traversed.
  Loop protection: `DefaultPeerCascade.deriveNext`
  ([`PeerCascade.fs`](../../src/InterPlatform/Server/PeerCascade.fs)) rejects
  a forward whose next peer already appears on the route with
  `PeerLoopDetected route` — independent of budget.
- **`HopsRemaining`** — a countdown initialised from the caller-supplied
  `HopBudget` on `JsonRpcPeerClient.create`
  ([`JsonRpcPeerClient.fs:35`](../../src/InterPlatform/Server/JsonRpcPeerClient.fs)).
  Each hop decrements it; when it reaches `0`, the next `NextHop` / `Forward`
  short-circuits to `PeerHopLimitExceeded` **before any wire call**.

Both guards are evaluated receiver-side in `DefaultPlatformPeer.Handle`
and initiator-side in the cascade, so a forged inbound context cannot
exceed the budget.

## The knob you must size — `HopBudget`

`HopBudget` is the **maximum forward depth** a call originated by this
client may reach. The example value `8` in
[`README.md`](../../src/InterPlatform/README.md) is a generous default, not
a recommendation for every topology. Size it to your federation graph:

| Topology | Forward depth | Suggested `HopBudget` |
|---|---|---|
| Direct 1:1 (A calls B, B never forwards) | 1 | `1` (or `2` for headroom) |
| Linear cascade A → B → C | 2 | `3` |
| N-deep cascade | N | `N + 1` |
| Mesh / unknown depth | graph diameter | diameter `+ 1`, capped at a sane max (`8` is fine) |

Rule of thumb: **`HopBudget = expected maximum hop depth + 1`**. The `+ 1`
absorbs one unplanned forward without tripping the limit; loop protection
(`Route`) is what actually prevents runaway cascades, so the budget does
not need to be defensively large.

**Under-sizing is the failure mode this doc prevents.** Setting
`HopBudget = 1` on a call chain that legitimately forwards twice rejects
the second hop with `PeerHopLimitExceeded` — surfaced to the caller as a
`PeerInvocationException`. The call was authorised and the peers were
reachable; only the budget was wrong.

## Observability — detecting a budget that is too small

`PeerHopLimitExceeded` and `PeerLoopDetected route` are structured
`PeerError`s, distinct from auth / transport failures. When a cascade
call fails:

1. The error variant tells you which guard fired (`HopLimit` vs `Loop`).
2. The Phase 18a audit trail
   ([`18a-cross-deployment-audit-transparency.md`](18a-cross-deployment-audit-transparency.md))
   records the call's `Route`, so you can read the actual path the call
   took and count the hops it needed — compare against the `HopBudget` you
   set. A `PeerLoopDetected` carries the offending route inline.

## Verification

1. Stand up an A → B → C cascade in a test harness with `HopBudget = 2`
   (deliberately one short). Confirm the C hop fails with
   `PeerHopLimitExceeded` and the A→B hop succeeds.
2. Raise `HopBudget` to `3` and confirm the full chain resolves.
3. Wire a cycle (C forwards back to A) with ample budget; confirm
   `PeerLoopDetected` fires with the route, not `PeerHopLimitExceeded` —
   loop protection is budget-independent.

## Rollback

`HopBudget` is a per-call-client value; no persisted state. Lowering or
raising it affects only calls made through that client thereafter. There
is nothing to migrate or revert beyond the config value itself.
