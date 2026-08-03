# Phase 314 — cascade-aware typed proxy forwarding

> **Additive. No consumer is required to change anything.** `JsonRpcPeerClient.create`
> is byte-for-byte unchanged and `PeerProxyConfig` gained no field (GP 11). This doc
> exists because a deployment that *forwards* peer calls has been silently getting the
> wrong cascade semantics, and there was previously no typed way to get the right ones.

## What changes

One new function in [`src/InterPlatform/Server/JsonRpcPeerClient.fs`](../../src/InterPlatform/Server/JsonRpcPeerClient.fs):

```fsharp
JsonRpcPeerClient.forward<'TApi> (inbound: PeerCallContext) (config: PeerProxyConfig) : 'TApi
```

`create` starts a **new cascade root** — fresh `RootRequestId`, `Route = [ caller ]`,
`HopsRemaining = HopBudget`. `forward` **continues the cascade** described by `inbound`,
seeding each call from `PeerCascade.deriveNext` — the same bookkeeping `IPeerCascade.Forward`
uses, so the safe primitive and the ergonomic proxy no longer have divergent semantics.

| Field | `create` | `forward inbound` |
|---|---|---|
| `RootRequestId` | fresh per call | preserved from `inbound` |
| `Route` | `[ config.Caller.PeerId ]` | `inbound.Route @ [ config.Caller.PeerId ]` |
| `HopsRemaining` | `config.HopBudget` | `inbound.HopsRemaining - 1` (`HopBudget` ignored) |
| `ParentRequestId` | `None` | `Some inbound.RootRequestId` |
| `Peer` | `config.Caller` | `config.Caller` |
| `User` / `ContractVersion` | from `config` | from `config` |

A derivation rejection (`PeerLoopDetected` / `PeerHopLimitExceeded`) short-circuits the call
to a raised `PeerInvocationException` **before** the wire round-trip, so a doomed hop never
leaves the forwarding peer.

`InterPlatform.fsproj` moves `Server\PeerCascade.fs` above `Server\JsonRpcPeerClient.fs` in the
compile order (cascade depends only on `Shared/PeerTypes.fs`) so the derivation stays
single-sourced rather than re-implemented proxy-side. No consumer-visible effect.

## Who is affected

Only deployments that **forward** an inbound peer call onward — an A → B → C cascade where B
builds a proxy while handling A's call. A deployment that only originates calls, or only
receives them, is unaffected and needs no edit.

Symptoms of the defect this fixes, if you have a forwarding handler today: audit rows on
different hops of one logical call carry different `RootRequestId`s; `PeerLoopDetected` never
fires on a cycle that passes through your deployment; a cascade exceeds the depth its
originator budgeted for.

## The change, per file

In the handler that forwards — one call site, one line:

```diff
-let onward = JsonRpcPeerClient.create<DirectoryContract> {
+// `inbound` is the PeerCallContext of the call this handler is serving.
+let onward = JsonRpcPeerClient.forward<DirectoryContract> inbound {
     Client = httpPeerClient
     Target = { Peer = nextPeerId; BaseUrl = "https://next.example" }
     Caller = thisPeerId
     User = Anonymous
     Version = v1
     ContractId = "directory"
     HopBudget = 8
 }
```

Two things worth stating because they are silent otherwise:

- **`HopBudget` is ignored on a forwarding proxy.** The budget belongs to the cascade, seeded
  by whoever originated it. Leaving the field set is harmless; expecting it to apply is not.
- **`User` and `ContractVersion` still come from the config**, not from `inbound`. A forwarding
  deployment vouches for the outbound leg itself, on the version it negotiated for that leg.
  To propagate the inbound end-user identity onward, do it deliberately:

  ```diff
  -    User = Anonymous
  +    User = inbound.User
  ```

- **The rejection surface widens.** A `forward` proxy call can now raise
  `PeerInvocationException(PeerLoopDetected …)` or `PeerInvocationException PeerHopLimitExceeded`
  without any wire call having happened. If your handler distinguishes transport failures from
  peer-side rejections, these two are neither — they are local, and they mean the hop was
  refused before it was attempted.

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
```

The `Phase 314 cascade-aware typed proxy forwarding` list in
[`PeerFederationTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/PeerFederationTests.fs)
pins a two-hop cascade preserving one `RootRequestId` end to end, the route / budget threading,
both doomed-hop rejections landing with an empty transport call log, and `create` still minting
a fresh root per call.

In your own deployment, the check is an audit one: a forwarded call should now produce
`PeerCallCompleted` rows at every hop under a single `RootRequestId`.

## Rollback

Revert the one-line call site back to `JsonRpcPeerClient.create` — nothing else in the
substrate depends on which entry point a consumer chose. `forward` holds no state and
registers nothing.

## See also

- [`src/InterPlatform/README.md`](../../src/InterPlatform/README.md) — "`create` roots a cascade; `forward` continues one"
- [`18c-federation-hop-budget.md`](18c-federation-hop-budget.md) — sizing `HopBudget` for your topology
