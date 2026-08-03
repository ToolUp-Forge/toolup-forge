# Peer-auth posture advisory — static bearer vs signed JWT

**Ships in:** ToolUp.Platform.Server (Phase 317).

**Nothing you compose changes behaviour, and nothing refuses.** This phase adds
one classified startup `Warn` and a public classification function. Both auth
paths — the static-bearer middleware and the signed-JWT peer host — run exactly
as they did (GP 11), and a deployment that registers no `PeerRoutePrefixes` never
runs the classifier at all (GP 13).

---

## Why

The SDK ships two unrelated peer-auth substrates:

- **Static bearer** — `ServerConfig.PeerRoutePrefixes` + `PeerBearerAuthMiddleware`
  (Phase 37 / 137). `X-Peer-Name` names the caller; a shared secret from
  `ISecretStore` at `peers/{peerName}/bearer` authenticates it.
- **Signed JWT** — the `ToolUp.InterPlatform` companion (Phase 18 and its
  follow-ons). Per-call tokens, fail-closed validation, `/peer/v1/*`.

They are documented as coexisting **on different prefixes**, and until this phase
nothing checked that they did. `PeerRoutePrefixes` entries are ordinary
case-insensitive `StartsWith` prefixes, so `"/peer/"` — the most natural name to
reach for — claims the whole `/peer/v1/` namespace the signed-JWT host serves.
`PeerBearerAuthMiddleware` is registered *ahead of the Giraffe router*, so when
that happens the static-bearer gate runs first and decides who reaches the host.

It fails quietly in both directions:

- A typed peer client presents a signed peer JWT and **no** `X-Peer-Name` header,
  so every federation call is answered `401 missing_peer_name_header` before
  dispatch. The federation surface looks composed and answers nothing.
- If static bearers *are* seeded and callers *do* send `X-Peer-Name`, the
  federation edge has grown a second, weaker, never-expiring credential to
  distribute out of band — and its refusals are audited under
  `_platform.peer.bearer`, not the peer call trail.

## The posture ladder

`PeerBearerAuthMiddleware.auditPeerAuthPosture : ServerConfig -> PeerAuthPosture`
classifies a composition. Pure and total; six rungs, weakest-guarantee-last:

| Rung | `PeerSubstrate` | `PeerRoutePrefixes` | Warns |
|---|---|---|---|
| `NoPeerAuthSurface` | off | empty | no |
| `SignedPeerAuthOnly` | on | empty | no |
| `StaticBearerOnly` | off | disjoint from `/peer/v1/` | no |
| `StaticBearerOnReservedNamespace` | off | covers `/peer/v1/` | no |
| `BothSubstratesDisjoint` | on | disjoint from `/peer/v1/` | no |
| `StaticBearerShadowsSignedPeer` | on | covers `/peer/v1/` | **yes** |

Overlap is tested in both directions, because both are real shapes: a prefix
*shorter* than the namespace (`"/"`, `"/peer"`, `"/peer/"`) swallows all of it,
and one *longer* (`"/peer/v1/ledger"`) claims part of it. An empty prefix matches
every path, so it counts too. The comparison is case-insensitive, matching the
runtime gate it models (`PeerRouteRegistry.isPeerRoute`).

`StaticBearerOnReservedNamespace` is classified but deliberately **silent**:
warning about a collision with a host the deployment does not run is a warning
about a composition that does not exist, and an advisory that fires on a correct
configuration is one operators learn to ignore. It is still data, so a deployment
that wants to hold that line early can assert it in its own preflight.

## What you must do

**Nothing, unless you see the new line.** It reads:

```
peer-auth-posture: ServerConfig.PeerRoutePrefixes entr(ies) [/peer/] cover the
'/peer/v1/' namespace this deployment's signed-JWT peer host serves, …
```

If you see it, decide which substrate you meant to guard those routes with:

1. **You meant the federation surface** — move the static-bearer prefix off
   `/peer/`. The two substrates are documented as coexisting on different
   prefixes; `"/api/peer/echo"` is the shape the SDK uses in its own examples:

   ```fsharp
   let config = {
       ServerConfig.defaults with
           PeerSubstrate = EnabledPeerSubstrate
           PeerRoutePrefixes = [ "/api/peer/echo" ]   // was [ "/peer/" ]
   }
   ```

2. **You meant the static-bearer flavour** to guard those routes — that is a
   legitimate posture, but say so deliberately. Note that in this shape a typed
   peer client cannot call you at all (it sends no `X-Peer-Name`), so callers
   must be built against the bearer contract, not the peer contract.

## Asserting the posture in your own preflight

The classification is a value, not a log line, so a deployment can pin its own
posture without booting a server:

```fsharp
open ToolUp.Platform.PeerBearerAuthMiddleware

match auditPeerAuthPosture config with
| SignedPeerAuthOnly
| BothSubstratesDisjoint _ -> ()
| posture -> failwithf "unexpected peer-auth posture: %A" posture
```

`advisePeerAuthPosture : ILogger -> ServerConfig -> unit` runs the same check and
emits the same line, for a deployment that wants it in its own startup sequence
as well as the SDK's.

## Source-compatibility notes

Purely additive. `ToolUp.Platform.PeerBearerAuthMiddleware` gained one literal
(`SignedPeerRouteNamespace`), one DU (`PeerAuthPosture`) and four functions
(`shadowsSignedPeerNamespace`, `auditPeerAuthPosture`, `peerAuthPostureAdvisory`,
`advisePeerAuthPosture`). No existing type, signature or default moved, and
`PeerServerApp` gained no field — so `create ()` + every `with*` helper are
source-compatible.

## See also

- [`src/InterPlatform/TECHNICAL_GUIDE.md`](../../src/InterPlatform/TECHNICAL_GUIDE.md) —
  the full posture comparison table.
- [Phase 309 — audience-binding enforcement](309-peer-audience-binding-enforcement.md)
- [Phase 330 — peer delegation verification](330-peer-delegation-verification.md)
- [Phase 339 — peer transport TLS enforcement](339-peer-transport-tls-enforcement.md)
