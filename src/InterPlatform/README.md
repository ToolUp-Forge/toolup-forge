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
| Job-substrate fusion | [`Server/PeerJobHandler.fs`](Server/PeerJobHandler.fs) | `IPeerJobResultStore`, `BlobPeerJobResultStore`, `PeerJobRetentionPolicy`, `PeerJobDocument`, `PeerJobFusion`, `PeerJobHandler`, module `PeerJob`, `PeerContractHost` |
| Outbound HTTP transport | [`Server/HttpPeerClient.fs`](Server/HttpPeerClient.fs) | `HttpPeerClient` |
| Typed initiator proxy | [`Server/JsonRpcPeerClient.fs`](Server/JsonRpcPeerClient.fs) | `PeerProxyConfig`, module `JsonRpcPeerClient` |
| JSON-RPC host | [`Server/JsonRpcPeerHost.fs`](Server/JsonRpcPeerHost.fs) | module `JsonRpcPeerHost` (`contract`, `routes`) |
| Compose pipeline | [`Server/PeerCompose.fs`](Server/PeerCompose.fs) | `PeerServerApp` record + `run` |
| Cross-instance face descriptor (Phase 590) | [`Server/PeerSurface.fs`](Server/PeerSurface.fs) | `ConsumedContract` (in `Shared/PeerTypes.fs`), `ServedContract`, `PeerServes`, `PeerTrustPosture`, `PeerBudgetShape`, `PeerSurface`, `PeerSurfaceExport`, module `PeerSurface` (`describe`, `consumes`, `export`, `exportJson`) |
| Federation-graph preflight (Phase 591) | [`Server/FederationPreflight.fs`](Server/FederationPreflight.fs), [`Server/FederationPin.fs`](Server/FederationPin.fs) | `PinnedTrustFacet`, `PinnedPeerSurface`, `PeerTrustRequirement`, `FederationPinStore`, `FederationPreflightInput`, module `FederationPreflight` (`structuralRules`, `ruleManifest`, `classifiedRuleManifest`, `check`, `FederationPreflightValidator`), module `FederationPin` (`ofSurface`, `ofExport`, `ofExportJson`) |

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

### Don't guard `/peer/` with the *other* peer-auth substrate

The SDK also ships a **static shared-bearer** peer gate — `ServerConfig.PeerRoutePrefixes` + `PeerBearerAuthMiddleware`, in `ToolUp.Platform.Server`. It is a different tool with weaker guarantees: no expiry, no audience, no per-call minting or replay window, no delegated-originator verification, no asymmetric-key option. The two are documented as coexisting on **different prefixes**, and that is load-bearing.

`PeerRoutePrefixes` entries are case-insensitive `StartsWith` prefixes, so a `"/peer/"` entry claims the whole `/peer/v1/` namespace these routes serve — and the bearer middleware runs *ahead of the router*, so it then decides who reaches this companion at all. A typed peer client sends a signed JWT and no `X-Peer-Name` header, so every federation call is answered `401` before dispatch while the composition still looks correct. Mount the bearer flavour on a prefix of its own (`"/api/peer/echo"`).

A composition that trips this logs one `peer-auth-posture:` `Warn` at startup (Phase 317); `PeerBearerAuthMiddleware.auditPeerAuthPosture` returns the same classification as data for a deployment's own preflight. Full posture comparison and the six-rung ladder: [`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md#two-peer-auth-substrates-and-which-one-guards-what-phase-317).

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

### `create` roots a cascade; `forward` continues one (Phase 314)

**A bare `create` starts a NEW cascade root.** Every call through that proxy mints a fresh `RootRequestId`, resets `Route` to `[ caller ]`, and sets `HopsRemaining = HopBudget`. That is exactly right for a call this deployment *originates* — and exactly wrong for one it is *forwarding*.

So a handler that continues an inbound cascade by building another `create` proxy silently discards the inbound route, hop budget, and correlation id: loop detection and hop limits stop spanning the cascade, and the cross-hop audit correlation is lost. **Continuing an inbound cascade uses `forward` instead:**

```fsharp
// Inside a handler for an inbound peer call, `inbound` is the
// PeerCallContext this deployment is currently serving.
let onward = JsonRpcPeerClient.forward<DirectoryContract> inbound {
    Client = httpPeerClient
    Target = { Peer = nextPeerId; BaseUrl = "https://next.example" }
    Caller = thisPeerId        // the forwarding deployment
    User = Anonymous
    Version = v1
    ContractId = "directory"
    HopBudget = 8              // ignored on a forwarding proxy — see below
}

let! caps = onward.GetCapabilities()
```

Each call through a `forward` proxy derives its context from `inbound` via the same `PeerCascade.deriveNext` bookkeeping `IPeerCascade.Forward` uses, so the two forwarding paths compose instead of diverging:

| Field | `create` | `forward inbound` |
|---|---|---|
| `RootRequestId` | fresh per call | **preserved** from `inbound` (GP 7 — correlation rides the chain, across hops) |
| `Route` | `[ config.Caller.PeerId ]` | `inbound.Route @ [ config.Caller.PeerId ]` |
| `HopsRemaining` | `config.HopBudget` | `inbound.HopsRemaining - 1` — **`HopBudget` is ignored**; the budget belongs to the cascade, not to this leg |
| `ParentRequestId` | `None` | `Some inbound.RootRequestId` |
| `Peer` | `config.Caller` | `config.Caller` (re-keyed to the forwarder) |
| `User` / `ContractVersion` | `config.User` / `config.Version` | `config.User` / `config.Version` — **the config still wins** |

That last row is deliberate: a forwarding deployment vouches for the outbound leg itself, on the contract version it negotiated for *that* leg. To propagate the inbound end-user identity onward, say so — `{ config with User = inbound.User }` — rather than have it happen by accident.

**A doomed hop never leaves the forwarding peer.** If the derivation is rejected — the target is already on the route (`PeerLoopDetected`), or the budget is exhausted (`PeerHopLimitExceeded`) — the call short-circuits to a raised `PeerInvocationException` carrying that `PeerError`, **before** the wire round-trip. This is the same caller-side defence in depth `IPeerCascade` already provided, now reachable from the typed proxy.

### The receiver derives the same fields, it does not trust them (Phase 331)

Everything above is the *sender's* bookkeeping, and a sender that wants the guards to bind is not the one to worry about. Those four fields ride inside the request body, and the peer token carries none of them, so on arrival they are a self-assertion: `HopsRemaining = Int32.MaxValue` would put the receiver's hop-limit guard out of reach and `Route = []` would put its loop guard out of reach.

The receiver therefore derives its trusted `PeerCallContext` rather than copying it — `PeerCascadeAuthority.derive`, governed by a `PeerCascadePolicy`:

| Field | Receiver's rule |
|---|---|
| `Route` | the validated caller is guaranteed to be on it, last. An honest route (from `create` or `deriveNext`) already is, so it passes through untouched; a route deeper than `MaxRouteLength`, or carrying an empty / over-length / control-character entry, is refused. |
| `HopsRemaining` | clamped to `MaxHopsRemaining`. The decrement stays sender-side — clamping is not a second decrement. |
| `RootRequestId` | preserved when well-shaped (that is what keeps a cascade one cascade for audit), minted by the receiver when absent or unusable. |
| `ParentRequestId` | derived from the inbound JSON-RPC envelope id, never from the body; `None` at the originating hop. |
| `ContractVersion` | left alone — `IPlatformPeer.Handle` already measures it against the receiver's own supported set. |

Defaults are far above the documented `HopBudget` guidance (32 hops, 32 route entries, 128-character identifiers), so an existing federation is unaffected; tune them with `PeerServerApp.withCascadePolicy`. What this does **not** claim to stop is two colluding peers bouncing a call between themselves, each hop presenting a fresh in-ceiling budget — no receiver-side rule can see that from a single message. What it closes is the unilateral escape: one peer, one call, claiming a budget or a history the receiver never agreed to.

## Clean-room gate (Phase 18b + 311)

A **clean-room contract** answers an approved query against sensitive data with privacy-preserving outputs only — cohort counts at or above a k-anonymity floor, small cells suppressed, output shape constrained — never row-level data. `ICleanRoomBroker` ships that mechanism; `PeerServerApp.withCleanRoomTemplate` is what makes it *run*:

```fsharp
let reachTemplate: CleanRoomTemplate = {
    TemplateId = "reach"
    AllowedMethods = Set.ofList [ "EstimateReach"; "Histogram" ]
    Floor = {
        MinCohortSize = 50
        SuppressionThreshold = 50
        PermittedShapes = Set.ofList [ Count; Histogram ]
    }
}

PeerServerApp.create ()
|> PeerServerApp.withConfig config
|> PeerServerApp.withLocalPeer thisPeerId
|> PeerServerApp.withContract reachHost                            // contract id "example.reach"
|> PeerServerApp.withCleanRoomTemplate "example.reach" reachTemplate
|> PeerServerApp.run
```

**The floor is applied by the substrate, not by the handler.** The composed template wraps the contract's dispatch closure — the only route the receiver has to the wire — so every method's answer passes the gate whether or not the handler ever calls the broker. Gated methods answer with a `CohortResult`; an answer in any other shape is **withheld**, because a floor that cannot be evaluated must not be assumed cleared. Three invariants are the wrapper's own, so they hold even when a deployment substitutes its own `ICleanRoomBroker`: a method off `AllowedMethods` is refused before the handler runs, an uncheckable answer is withheld, and a release is re-checked against the composed floor.

A withhold reaches the caller as `PeerCleanRoomWithheld templateId` — the template id and nothing more. The broker's reasons name cohort sizes, and a caller that can vary its query and read them back has a counting oracle over exactly the data the floor protects; the full reason is recorded receiver-side as a `PeerCleanRoomDecision` audit row (suppressed-cell labels included), and audit transparency below is the deliberate, caller-scoped route to any of it.

Composing a template for a contract id this deployment does not host **refuses to start**: a privacy gate that looks composed and never runs is worse than no gate at all. `PeerServerApp.auditCleanRoomTemplates` reports the same finding as data for a deployment's own preflight. A composition that gates nothing wraps nothing and costs nothing.

### Privacy-budget ledger (Phase 190)

The floor decides about **one** answer. Cohort floors do not compose — differencing two in-floor cohorts that overlap in all but one record recovers that record, and no per-query check can see it because *each query passed*. `PeerServerApp.withPrivacyBudget` bounds the series:

```fsharp
app
|> PeerServerApp.withCleanRoomTemplate "example.reach" reachTemplate
|> PeerServerApp.withPrivacyBudget (
       PrivacyBudgetMeter.create
           (BlobPrivacyBudgetLedger blobs)
           (PrivacyBudgetPolicy.create 50m 1m))          // 50 ε ceiling, 1 ε per answer
```

Budgets are keyed per `(template, counterparty, epoch)`. Once the declared ceiling is reached every further answer under that template is **withheld** — through the same dispatch closure the handler has no say in — and `IPrivacyBudgetLedger.RemainingBudget` is the auditable reading. Charges add: **basic (sequential) composition**, the standard bound.

The debit is two-phase and that is the design. `ReserveBudget` runs *before* the handler, so no answer reaches the wire on credit; `RecordSpend` settles once the outcome is known, so a dispatch that errored returns its ε rather than eroding a budget nobody spent. A *withheld* answer is charged by default, because a free refusal is a counting oracle over the cohort size; `PrivacyBudgetPolicy.withWithholdCharge WithholdFree` opts out of that. `BlobPrivacyBudgetLedger` takes every reservation through a conditional (compare-and-swap) write and **refuses a backend without conditional writes at construction** — a ledger that over-admits under load reads as defended and is not.

**It is an accounting control, not a differential-privacy guarantee.** ε-DP is a property of a randomised mechanism; the shipped broker suppresses and refuses but adds no noise, so summing ε over deterministic answers bounds nothing formally. What it bounds is how many questions a counterparty may ask under a declared schedule, enforced and auditable. A deployment needing the formal guarantee substitutes an `ICleanRoomBroker` that randomises, at which point these values become that mechanism's real privacy loss. Collusion between counterparties is out of scope, the charge falls on the validated immediate caller rather than a cascade origin, and a refilling epoch (`DailyBudget` / `MonthlyBudget`) is a deliberate weakening of `PerpetualBudget`. Full rationale: `Server/PrivacyBudgetLedger.fs`.

A composition that never calls `withPrivacyBudget` reads no ledger and costs nothing.

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

## Peer surface descriptor (Phase 590)

`PeerSurface.describe` emits a deployment's **cross-instance face as data** — the instance's *label*: what a counterparty may rely on without seeing inside. It is **derived from the composed peer registrations by construction, never hand-listed** — a new `withContract` / `withConsumedContract` registration surfaces with zero descriptor edits:

- **Serves** — every hosted contract (id + wire versions, from the same builders `PeerServerApp.run` composes, including the audit-transparency contract when opted in), the long-running **routines** it fuses onto the job substrate (advertised only when a job scheduler is actually composed, under their canonical `_platform.peer.{contractId}.{method}` handler names), and the `/peer/v1` endpoint templates.
- **Consumes** — the contracts this instance *calls* on counterparts, declared at compose time with `withConsumedContract` (build declarations with `PeerSurface.consumes<'TApi>` so each stays tied to a real contract type) plus the expected counterpart role. Purely descriptive — dispatch still goes through `JsonRpcPeerClient.create`.
- **TrustPosture** — what the composition wires by construction: fail-closed HS256 bearer JWTs with per-call key reads, whether inbound audiences are bound to the local peer id (exactly when `withLocalPeer` is declared), trust-anchor delegation verification, the freshness-window replay stance, and the deployment-managed transport stance.
- **Budgets** — the cascade guard shape (per-call hop budget + route loop detection) and whether long-running dispatch is available.

```fsharp
let app =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig config
    |> PeerServerApp.withLocalPeer localIdentity
    |> PeerServerApp.withContract directoryHost
    |> PeerServerApp.withConsumedContract
        (PeerSurface.consumes<UpstreamRegistryContract> "registry" [ v1 ] "hub")

let surface = PeerSurface.describe app      // pure, on demand
let pinned  = PeerSurface.exportJson surface // canonical JSON + SHA-256 stamp
```

The export is **deterministic and hash-stamped**: every list is sorted before serialisation, so the same composition always yields the same bytes and the same `SurfaceHash` regardless of registration order — and any registration change produces a new hash. A counterparty (or an external federation-composition tool) pins the export of an instance it can never introspect live and detects staleness by re-hashing; the live handshake endpoints answer "what do you serve *right now*", the export answers "what did the instance I validated against look like". A deployment on `NoPeerSubstrate` yields the empty surface without running a single contract builder — zero cost when unused.

## Federation-graph preflight (Phase 591)

A deployment that consumes a peer contract no counterparty serves — at an incompatible version, or under a trust posture the counterparty never declared — used to discover it at call time. You cannot introspect another organisation's deployment, so the preflight validates against the label each counterparty **published**: pin its `PeerSurface` export and the composition's federation edges are checked before traffic.

```fsharp
// The counterparty's published export, verified against the stamp agreed out of band.
let sellerPin =
    FederationPin.ofExportJson "seller-ssp" "peers/seller-ssp.surface.json" agreedHash DateTimeOffset.UtcNow document

let app =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig config
    |> PeerServerApp.withConsumedContract (PeerSurface.consumes<IReachApi> "reach" [ v1 ] "seller")
    |> PeerServerApp.withPinnedCounterparty (sellerPin |> Result.defaultWith failwith)
    |> PeerServerApp.withRequiredPeerTrust PeerTrustRequirement.audienceBound
    |> PeerServerApp.withPinnedSurfaceMaxAge (TimeSpan.FromDays 90.0)
```

Three rules, exported as data through `FederationPreflight.ruleManifest` / `classifiedRuleManifest` (the same `CompositionRuleDescriptor` / `ClassifiedCompositionRule` shapes the intra-app composition rules project, so an external pre-build checker needs no new vocabulary):

| Code | Severity | Fires when |
|---|---|---|
| `peer-contract-unsatisfied` | Error | A consumed contract no pinned counterparty serves, or one serves at no version this deployment speaks. Compatibility is a non-empty version intersection — the handshake's own highest-mutual discipline. |
| `peer-trust-mismatch` | Error | A required trust facet a pinned counterparty's label contradicts, or does not declare at all (an omitted facet is no claim, not a weaker one). |
| `peer-surface-stale` | Warning | A pin older than the declared maximum age. An aged pin is the *absence* of fresh evidence rather than evidence of drift, so it reports and never refuses. |

The rules run as a **structural-class** `IConfigValidator` in the composition-validator pass, so `ServerConfig.SkipPreflight` does not bypass them — every rule is a pure sweep over declared data already in memory and reaches no counterparty. `PeerServerApp.auditFederationGraph` runs the identical check as a value, so a deployment can assert its own edges without booting a server.

**Contract-level checking is what makes a heterogeneous federation safe**: nothing here inspects a counterparty's *composition*, only the wire face it already publishes. An aggregate group (Phase 595) pins exactly like a single instance — its posture facets are floors over the exposing members, and a facet the members disagree on publishes as `mixed:a|b`, which satisfies no requirement because a counterparty may rely on neither stance. A composition that pins nothing registers no validator, checks nothing, and is byte-for-byte a pre-591 composition.

## Routes

Four routes, all auth-gated (fail-closed — a missing / invalid / expired bearer token is rejected *before* dispatch):

| Route | Purpose |
|---|---|
| `POST /peer/v1/{contractId}` | Dispatch an immediate call, or schedule a long-running one (returns a `JobId`). |
| `GET  /peer/v1/capabilities` | Version handshake — answers with a bare `CapabilityList`. |
| `GET  /peer/v1/capabilities/profile` | Phase 18d — per-method capability profile (lifecycle / deprecation windows). |
| `GET  /peer/v1/{contractId}/jobs/{jobId}` | Poll a long-running call to a terminal `PeerJobStatus`. Owner-scoped: only the peer that scheduled the call can read its result — any other validated peer is refused `PeerUnauthorized` with no result body. |

## See also

- [`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md) — wire format + error-code map, the fail-closed JWT identity layer, job-fusion internals, the six-rule GP 12 portability audit verdict, and the Phase 18a–18e follow-on boundaries.
- [`../ToolUp.Scheduling/README.md`](../ToolUp.Scheduling/README.md) — the companion-package shape this mirrors.
