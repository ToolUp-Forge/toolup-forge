# Migration — Phase 18a: cross-deployment audit transparency

**Status:** additive, opt-in. No consumer is required to act; nothing changes for a deployment that does not opt in.

## What changes

The `ToolUp.InterPlatform` companion gains an SDK-shipped peer contract,
`_platform.peer.audit`, that lets a **calling** peer read back the
**receiving** peer's audit record of its own calls. This closes the
foundation's first follow-on (Phase 18): the substrate already emits one
`PeerCallCompleted` row per inbound call; 18a makes that row queryable by
the peer that made the call, scoped so a peer can only ever see its own
calls.

New public surface (all in `ToolUp.InterPlatform`):

- `PeerAuditQuery` — filter record (`ContractId` / `MethodName` / `SinceUtc` / `FailuresOnly` / `Limit`). No caller-id field, by design.
- `PeerAuditEntry` — a caller-visible audit row. Omits `CallerPeerId` (always the querying peer).
- `IPeerAuditApi` — the typed contract (`{ QueryCalls: PeerAuditQuery -> Async<PeerAuditEntry list> }`).
- `PeerAudit` module — reserved `contractId` (`_platform.peer.audit`), `queryMethod`, `v1`, `maxLimit` (500), `defaultLimit` (100).
- `PeerAuditContractHost.project` / `PeerAuditContractHost.registration` — the pure projection + the bespoke context-aware dispatch.
- `PeerServerApp.withPeerAuditTransparency` — the compose opt-in + a new `AuditTransparency: bool` field on `PeerServerApp` (defaults `false`).

## Diff to apply

### Receiver (a deployment that hosts peer contracts and wants to expose its audit trail)

```diff
 PeerServerApp.create ()
 |> PeerServerApp.withConfig serverConfig          // PeerSubstrate = EnabledPeerSubstrate
 |> PeerServerApp.withLocalPeer sellerIdentity
 |> PeerServerApp.withContract buyerSellerHost
+|> PeerServerApp.withPeerAuditTransparency        // register _platform.peer.audit
 |> PeerServerApp.run
```

That is the only change. The contract reads the already-resolved
`IAuditLog`; no new substrate is required. A deployment running
`AuditLog = NoAuditLog` may still opt in — the contract answers with an
empty trail.

### Caller (a deployment that calls a peer and wants to reconcile against the peer's log)

```diff
+let audit =
+    JsonRpcPeerClient.create<IPeerAuditApi> {
+        Client = httpPeerClient
+        Target = { Peer = sellerId; BaseUrl = "https://seller.example" }
+        Caller = buyerId
+        User = Anonymous
+        Version = PeerAudit.v1
+        ContractId = PeerAudit.contractId
+        HopBudget = 8
+    }
+
+let! failures =
+    audit.QueryCalls
+        { ContractId = None
+          MethodName = None
+          SinceUtc = None
+          FailuresOnly = true
+          Limit = 100 }
```

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "IPeerAuditTransparency"` — 13 passed, 0 failed. Covers caller-scoping (a peer sees only its own rows), cross-peer isolation (another peer's rows never appear), the filter / ordering / `maxLimit`-clamp behaviour, the `QueryCalls`-only method guard, and a typed-proxy round-trip over a loopback transport.
- Strip check: a deployment that does **not** call `withPeerAuditTransparency` registers no `_platform.peer.audit` contract — a `QueryCalls` against it returns `PeerContractNotFound`. `NoPeerSubstrate` deployments are unaffected (the whole companion short-circuits, GP 13).

## Rollback

Remove the `withPeerAuditTransparency` line on the receiver (and drop the
`IPeerAuditApi` proxy on the caller). The contract de-registers; no
persisted state is written by 18a (it only *reads* the existing audit
trail), so there is nothing to clean up.
