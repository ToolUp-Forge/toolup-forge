# ToolUp.InterPlatform

Opt-in **inter-platform peer substrate** for ToolUp.Platform. One ToolUp deployment calls a **typed contract** hosted by another ToolUp deployment (a *peer*) over the wire — with identity propagation, a version handshake, long-running-call resolution fused onto the job substrate, and audit. Companion package — deployments that leave `ServerConfig.PeerSubstrate = NoPeerSubstrate` (the default) pay **zero runtime cost** (GP 13).

## What this is

A small, opinionated cross-deployment RPC primitive. You declare a contract as an ordinary **record of functions** (the same shape ToolUp.Remoting uses for in-deployment APIs); the substrate turns it into:

- a **typed initiator proxy** on the calling side (`JsonRpcPeerClient.create<'TApi>`) — each field becomes a function that marshals its arguments, vouches for the caller's identity, calls the peer, and deserialises the typed result; and
- a **fail-closed JSON-RPC 2.0 host** on the receiving side (`JsonRpcPeerHost.contract<'TApi>` + `JsonRpcPeerHost.routes`) — auth-gates every inbound call, rebuilds the call context from the *validated* principal, dispatches to your implementation, and audits the outcome.

Two method shapes are supported:

- **Immediate** — `… -> Async<'T>`. Resolves inside the inbound HTTP request; the typed result rides back in the JSON-RPC response.
- **Long-running** — `… -> Async<PeerJobHandle<'T>>`. The host schedules a background job on `IJobScheduler` and returns a `JobId`; the caller polls `GET /peer/v1/{contractId}/jobs/{jobId}` via the handle's `Poll` closure until the job reaches a terminal state.

The wire format is **JSON-RPC 2.0 over HTTP** — a deliberately open, language-neutral contract (a peer is committed to it across deployments), not the in-tree ToolUp.Remoting transport. That choice keeps non-F# peer SDKs viable for a later phase.

### Shipped surface (Phase 18 — foundation)

| Concern | File | Public types / module |
|---|---|---|
| Identity, versioning, cascade context, errors | [`Shared/PeerTypes.fs`](Shared/PeerTypes.fs) | `PeerIdentity`, `ContractVersion`, `UserContext`, `TargetPeer`, `PeerCallContext`, `PeerError`, `PeerHandshakeError`, `ContractCapability`, `CapabilityList` |
| Long-running-call resolution | [`Shared/PeerJobHandle.fs`](Shared/PeerJobHandle.fs) | `PeerJobId`, `PeerJobStatus<'T>`, `PeerJobHandle<'T>`, module `PeerJobHandle` |
| Wire envelope (JSON-RPC 2.0) | [`Shared/JsonRpcEnvelope.fs`](Shared/JsonRpcEnvelope.fs) | `PeerWirePayload`, `JsonRpcRequest`, `JsonRpcResponse`, module `JsonRpc` |
| Receiver contract surface | [`Server/IPlatformPeer.fs`](Server/IPlatformPeer.fs) | `PeerContractRegistration`, `IPlatformPeer` |
| Identity provider | [`Server/IPeerAuthProvider.fs`](Server/IPeerAuthProvider.fs) | `PeerPrincipal`, `IPeerAuthProvider` |
| Outbound transport | [`Server/IPeerClient.fs`](Server/IPeerClient.fs) | `IPeerClient` |
| Version handshake | [`Server/IPeerHandshake.fs`](Server/IPeerHandshake.fs) | `IPeerHandshake` |
| Peer directory | [`Server/IPeerRegistry.fs`](Server/IPeerRegistry.fs) | `IPeerRegistry` |
| Default identity provider | [`Server/JwtPeerAuthProvider.fs`](Server/JwtPeerAuthProvider.fs) | `JwtPeerAuthProvider` (fail-closed HS256) |
| Default receiver | [`Server/DefaultPlatformPeer.fs`](Server/DefaultPlatformPeer.fs) | `DefaultPlatformPeer` (contract table + cascade guards) |
| Default directory | [`Server/BlobPeerRegistry.fs`](Server/BlobPeerRegistry.fs) | `BlobPeerRegistry` |
| Default handshake | [`Server/InMemoryPeerHandshake.fs`](Server/InMemoryPeerHandshake.fs) | `InMemoryPeerHandshake` |
| Job-substrate fusion | [`Server/PeerJobHandler.fs`](Server/PeerJobHandler.fs) | `IPeerJobResultStore`, `BlobPeerJobResultStore`, `PeerJobFusion`, `PeerJobHandler`, module `PeerJob`, `PeerContractHost` |
| Outbound HTTP transport | [`Server/HttpPeerClient.fs`](Server/HttpPeerClient.fs) | `HttpPeerClient` |
| Typed initiator proxy | [`Server/JsonRpcPeerClient.fs`](Server/JsonRpcPeerClient.fs) | `PeerProxyConfig`, module `JsonRpcPeerClient` |
| JSON-RPC host | [`Server/JsonRpcPeerHost.fs`](Server/JsonRpcPeerHost.fs) | module `JsonRpcPeerHost` (`contract`, `routes`) |
| Compose pipeline | [`Server/PeerCompose.fs`](Server/PeerCompose.fs) | `PeerServerApp` record + `run` |

The audit payload (`PeerCallCompletedPayload`) and the `PeerCallCompleted` `AuditEvent` case live in the core platform's audit types ([`../ToolUp.Platform.Core/Shared/AuditTypes.fs`](../ToolUp.Platform.Core/Shared/AuditTypes.fs)), serialised by [`../ToolUp.Platform.Server/Server/AuditLog.fs`](../ToolUp.Platform.Server/Server/AuditLog.fs) — the substrate is a *producer* of that event, not its owner.

## Why a companion, not core SDK

Federation is an **opt-in capability**, not platform substrate. A single-deployment analytics app never calls a peer; mounting a public `/peer/v1/*` route, resolving `IPlatformPeer` / `IPeerClient` / `IPeerAuthProvider` in DI, and wiring peer audit for that app is wrong-by-default — both as dead weight and as needless public attack surface. Keeping it a companion means the substrate is present only when a deployment explicitly federates.

The companion is a *consumer* of substrate (`IBlobStorage` for the directory + job-result store, `ISecretStore` for signing keys, optionally `IJobScheduler` for long-running calls, `IAuditLog` for the outcome event), not substrate itself. It is **server-only** — there is no Fable client surface; the `Shared/` types are shared between two *server* deployments, not between a server and a browser.

## How to enable

The substrate is selected by a single `ServerConfig` field — `PeerSubstrate`, mirroring `EntityStoreMode` / `JobSchedulerMode` (binary, opt-in). Compose with `PeerServerApp` (the [`PeerCompose`](Server/PeerCompose.fs) companion root), which wraps a base `ServerApp` and adds peer-specific `with*` helpers:

```fsharp
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

let config = {
    ServerConfig.defaults with
        Port = 5000
        Mode = Team
        PeerSubstrate = EnabledPeerSubstrate
        // Required ONLY for long-running contract methods; immediate-only
        // contracts need no scheduler:
        JobScheduler = InProcessJobScheduler
}

[<EntryPoint>]
let main _ =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig config
    |> PeerServerApp.withAuth (StaticJwtAuthProvider(...))
    |> PeerServerApp.withStorage (LocalFileStorage("data"))
    |> PeerServerApp.withLocalPeer { PeerId = "seller"; DisplayName = "Seller Deployment" }
    |> PeerServerApp.withContract (JsonRpcPeerHost.contract<DirectoryContract> "directory" [ v1 ] >> applyImpl)
    |> PeerServerApp.run
```

`PeerServerApp.run`, when `PeerSubstrate = EnabledPeerSubstrate`:

1. Registers the peer DI singletons resolved from already-present substrate:
   `IPeerAuthProvider` (`JwtPeerAuthProvider` over `ISecretStore`), `IPeerJobResultStore` + `IPeerRegistry` (over `IBlobStorage`), `IPlatformPeer` (`DefaultPlatformPeer`), `IPeerClient` (`HttpPeerClient`), `IPeerHandshake` (`InMemoryPeerHandshake`).
2. When `JobScheduler <> NoJobScheduler`, additionally registers `PeerJobFusion` (scheduler + result store) so long-running methods can park a background job. Absent it, long-running dispatch returns a clear `PeerHandler "… not enabled"` error.
3. On first `IPlatformPeer` resolution, runs every registered contract builder — registering each contract on the peer and its long-running job handlers on the scheduler.
4. Mounts `JsonRpcPeerHost.routes` onto the SDK route chain.
5. Delegates the rest to `ServerApp.run`.

When `PeerSubstrate = NoPeerSubstrate`, `run` short-circuits to `ServerApp.run app.Base` — **byte-for-byte** the shape of a base `ServerApp.run`: no DI registrations, no `/peer/v1` routes, no peer audit (GP 13).

### Required keys

`JwtPeerAuthProvider` reads a peer's symmetric HS256 signing key from `ISecretStore` on **every** issue / validate, at scope `_platform`, key `peers/{peerId}/signing-key` (rotation flows through immediately). Seed each trusted peer's key out of band before the first call:

```fsharp
secrets.SetSecret("_platform", "peers/buyer/signing-key", sharedKey) |> Async.RunSynchronously |> ignore
```

## How to author a contract

A contract is a **record whose fields are functions**. Declare it once, shared by both peers:

```fsharp
type DirectoryContract = {
    GetCapabilities: unit -> Async<string list>            // immediate
    BuildReport: ReportRequest -> Async<PeerJobHandle<Report>>   // long-running
}
```

> **The contract record must NOT be `private`.** The host reflects via `FSharpType.IsRecord`, which (without the private-representation flag) reads a `private` record as a *non-record* and `JsonRpcPeerHost.contract` rejects it. Declare it `internal` or public, never `private`.

**Receiver side** — supply an implementation value and host it:

```fsharp
let directoryImpl : DirectoryContract = {
    GetCapabilities = fun () -> async { return [ "directory.list"; "directory.lookup" ] }
    BuildReport = fun req -> async { return reportJobHandle req }
}

// fusion: PeerJobFusion option -> PeerContractHost
let directoryHost = JsonRpcPeerHost.contract<DirectoryContract> "directory" [ v1 ] fusion directoryImpl
```

Register it with `PeerServerApp.withContract`. Immediate-only contracts ignore the threaded `fusion`; long-running methods use it to schedule the background job.

**Caller side** — build a typed proxy and call it like a local API:

```fsharp
let proxy = JsonRpcPeerClient.create<DirectoryContract> {
    Client = httpPeerClient
    Target = { Peer = sellerId; BaseUrl = "https://seller.example" }
    Caller = buyerId
    User = Anonymous
    Version = v1
    ContractId = "directory"
    HopBudget = 8
}

let! caps = proxy.GetCapabilities()     // immediate — resolves inline
let! handle = proxy.BuildReport req      // long-running — returns a poll handle
let! report = PeerJobHandle.resolve handle
```

A peer-side `PeerError` surfaces on the caller as a raised `PeerInvocationException` — the typed API presents `Async<'T>`, not `Async<Result<_, _>>`.

`HopBudget` is the maximum forward depth a call may reach in a multi-hop cascade (`8` above is a generous default, not a per-topology recommendation). Size it to your federation graph — see [`docs/migrations/18c-federation-hop-budget.md`](../../docs/migrations/18c-federation-hop-budget.md). An under-sized budget rejects a legitimate forward with `PeerHopLimitExceeded`.

## Audit transparency (Phase 18a)

The substrate records one `PeerCallCompleted` audit row per inbound call, keyed by the *validated* caller. Audit transparency lets a calling peer read back the receiver's record of **its own** calls — to reconcile what it asked for against what the counterpart logged ("I asked for k≥50 and got 47 rows — confirm the gate suppressed three").

**Receiver side** — opt in when composing:

```fsharp
PeerServerApp.create ()
|> PeerServerApp.withConfig config            // PeerSubstrate = EnabledPeerSubstrate
|> PeerServerApp.withContract directoryHost
|> PeerServerApp.withPeerAuditTransparency    // registers _platform.peer.audit
|> PeerServerApp.run
```

**Caller side** — build a typed `IPeerAuditApi` proxy and query:

```fsharp
let audit = JsonRpcPeerClient.create<IPeerAuditApi> {
    Client = httpPeerClient
    Target = { Peer = sellerId; BaseUrl = "https://seller.example" }
    Caller = buyerId
    User = Anonymous
    Version = PeerAudit.v1
    ContractId = PeerAudit.contractId
    HopBudget = 8
}

let! mine = audit.QueryCalls { ContractId = None; MethodName = None; SinceUtc = None; FailuresOnly = true; Limit = 100 }
```

**Scoping is the load-bearing guarantee:** the receiver answers only with rows where the *authenticated* caller made the call. `PeerAuditQuery` carries no caller-id field, so a peer cannot widen its scope, and `PeerAuditEntry` omits `CallerPeerId` (always the querying peer) — cross-peer leakage is impossible by construction. A deployment with `AuditLog = NoAuditLog` still registers the contract but answers with an empty trail.

## Capability negotiation (Phase 18d)

The foundation handshake (`IPeerHandshake.Negotiate`) resolves the single highest *contract* version both sides support. 18d adds **per-method** negotiation with **deprecation windows** — so a caller learns at connect time that a method it depends on is `Deprecated` (with a sunset version) or `Removed`, instead of hitting a runtime `PeerMethodNotFound`.

**Receiver side** — declare a method lifecycle profile and compose it:

```fsharp
let v1, v2, v3 = { Major = 1; Minor = 0 }, { Major = 2; Minor = 0 }, { Major = 3; Minor = 0 }

// Reflection auto-populates every method as Active at every version;
// the overlay marks specific (method, version) pairs Deprecated / Removed.
let directoryProfile =
    PeerCapabilityNegotiation.profileFor<DirectoryContract> "directory" [ v1; v2 ] [
        "GetCapabilities", v2, Deprecated { DeprecatedSince = v2; RemovedIn = Some v3; Note = "use ListContracts" }
    ]

PeerServerApp.create ()
|> PeerServerApp.withConfig config
|> PeerServerApp.withContract directoryHost
|> PeerServerApp.withContractProfile directoryProfile   // served at /peer/v1/capabilities/profile
|> PeerServerApp.run
```

**Caller side** — negotiate a method through the handshake:

```fsharp
match! handshake.NegotiateMethod(target, "directory", "GetCapabilities") with
| Ok res ->
    match res.Status with
    | Active -> ()                                   // safe to call at res.Version
    | Deprecated notice -> log $"deprecated, removed in {notice.RemovedIn}: {notice.Note}"
    | Removed notice -> failwith $"gone: {notice.Note}"
| Error (RemoteProfileUnavailable e) -> ()          // remote unreachable
| Error e -> ()                                      // ContractNotAdvertised / MethodNotAdvertised / NoMutual
```

A peer that predates 18d (no `/capabilities/profile` route) degrades cleanly: its bare `CapabilityList` is read as an all-`Active` profile, so a 18d-aware caller still negotiates. The new endpoint is purely additive — `GET /peer/v1/capabilities` is byte-for-byte unchanged, and a deployment that never calls `withContractProfile` advertises versions-only profiles (no per-method lifecycle).

## Routes

Four routes, all auth-gated (fail-closed — a missing / invalid / expired bearer token is rejected *before* dispatch):

| Route | Purpose |
|---|---|
| `POST /peer/v1/{contractId}` | Dispatch an immediate call, or schedule a long-running one (returns a `JobId`). |
| `GET  /peer/v1/capabilities` | Version handshake — answers with a bare `CapabilityList`. |
| `GET  /peer/v1/capabilities/profile` | Phase 18d — per-method capability profile (lifecycle / deprecation windows). |
| `GET  /peer/v1/{contractId}/jobs/{jobId}` | Poll a long-running call to a terminal `PeerJobStatus`. |

## See also

- [`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md) — wire format + error-code map, the fail-closed JWT identity layer, job-fusion internals, the six-rule GP 12 portability audit verdict, and the Phase 18a–18e follow-on boundaries.
- [`../ToolUp.Scheduling/README.md`](../ToolUp.Scheduling/README.md) — the companion-package shape this mirrors.
