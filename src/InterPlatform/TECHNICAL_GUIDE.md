# ToolUp.InterPlatform — Technical Guide

Internals, design decisions, and the deferred set for the Phase 18 inter-platform peer substrate (foundation). Read [`README.md`](README.md) first for the overview + how-to-enable.

## Layer map

The substrate is built bottom-up in four layers; the `.fsproj` compile order is the dependency order.

| Layer | Concern | Files |
|---|---|---|
| 1 | Long-running-call resolution (`PeerJobHandle<'T>`, `PeerJobStatus<'T>`) | [`Shared/PeerJobHandle.fs`](Shared/PeerJobHandle.fs) |
| 2 | Identity, versioning, cascade context, error model | [`Shared/PeerTypes.fs`](Shared/PeerTypes.fs) |
| 3 | Wire format — JSON-RPC 2.0 envelope + serialisation | [`Shared/JsonRpcEnvelope.fs`](Shared/JsonRpcEnvelope.fs) |
| 4 | Typed host / client / auth / registry / handshake / job-fusion / compose | everything under `Server/` |

Layer 4 is the only layer with a transport. Layers 1–3 are pure data + serialisation, shared verbatim between the two peer deployments.

## Wire format

JSON-RPC 2.0 over HTTP is the peer wire format — **deliberately not** the in-tree ToolUp.Remoting Giraffe transport. The wire format is a public contract committed to peers across deployments; coupling it to the SDK's internal Remoting protocol would let an SDK upgrade silently break a peer. An open, documented format also keeps a non-F# peer SDK viable later (Phase 18e).

**Request envelope** (`JsonRpcRequest`): `{ JsonRpc = "2.0"; Method; Params; Id }`. `Method` is the contract method name; the contract id rides in the route (`/peer/v1/{contractId}`), not the method string. `Params` is a serialised `PeerWirePayload` `{ Context: PeerCallContext; Arguments: string }` — the propagated identity/version/cascade context plus the method's positional arguments already serialised to a JSON array string by the client proxy. `Id` is derived from the call's `RootRequestId` so the wire id and the audit id line up.

**Response envelope** (`JsonRpcResponse`): `{ JsonRpc = "2.0"; Result: string option; Error: JsonRpcErrorBody option; Id }`. Exactly one of `Result` / `Error` is populated. On the success path the host hand-builds the response so the already-serialised method result rides in `Result` without a second JSON encode.

All F# DU / Option / record bodies are (de)serialised with the universal `FableConverters` converter set (`ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()`) — the same set the rest of the SDK uses for SSE / non-Remoting JSON — so payloads round-trip the F# type system without bespoke converters.

### Error-code map

`PeerError` maps to a JSON-RPC error code (`JsonRpc.errorCode`). The standard reserved codes carry the protocol-level failures; the implementation-defined server range (`-32000 .. -32099`) carries the peer-substrate-specific ones:

| `PeerError` case | Code | Constant |
|---|---|---|
| `PeerUnauthorized` | `-32000` | `unauthorized` |
| `PeerContractNotFound` | `-32001` | `contractNotFound` |
| `PeerVersionMismatch` | `-32002` | `versionMismatch` |
| `PeerLoopDetected` | `-32003` | `loopDetected` |
| `PeerHopLimitExceeded` | `-32004` | `hopLimitExceeded` |
| `PeerHandler` | `-32005` | `handlerError` |
| `PeerTransport` | `-32006` | `transportError` |
| `PeerRequestTooLarge` | `-32007` | `requestTooLarge` |
| `PeerCleanRoomWithheld` | `-32008` | `cleanRoomWithheld` |
| `PeerMethodNotFound` | `-32601` | `methodNotFound` (standard) |
| `PeerDeserialization` | `-32700` | `parseError` (standard) |

The structured `PeerError` rides in the error object's `Data` field (serialised); `Message` is a one-line human string. The DU **case name** (no payload detail — `JsonRpc.errorCaseName`) is the safe label used for audit `Outcome` and metrics, where the message text could leak handler internals.

### HTTP status codes

- `401` — missing / invalid / expired bearer token (auth gate, before any dispatch).
- `413` — the request body exceeded the receiver's ceiling (`PeerRequestTooLarge`). Checked *after* the auth + delegation gates and *before* the body is read, so an unauthenticated caller can neither learn the ceiling nor make the receiver buffer anything. See [Request-body ceiling](#request-body-ceiling).
- `400` — request body failed to parse (`PeerDeserialization`).
- `200` — auth passed and dispatch ran; a *peer-side* `PeerError` still returns `200` with the error in the JSON-RPC envelope (the HTTP transport succeeded; the RPC failed). This is standard JSON-RPC framing.

### Request-body ceiling

`POST /peer/v1/{contractId}` reads the inbound body under a configurable ceiling — `PeerWireLimits.MaxRequestBytes`, **8 MiB by default**, set with `PeerServerApp.withWireLimits`. Over-ceiling requests answer `413` with a structured `PeerRequestTooLarge` naming the limit.

The ceiling is enforced twice, because either check alone is bypassable: a declared `Content-Length` over the limit is refused without reading a byte, and a request that declares nothing (chunked transfer-encoding, which the *caller* chooses) is stopped by a bounded read the moment it passes the limit. So the receiver never buffers more than the ceiling of a payload it has not agreed to — auth-gating bounds *who* can send one, not *how large* it is.

The other `/peer/v1/*` routes are `GET` and carry no body.

### Job-poll response correlation

`GET /peer/v1/{contractId}/jobs/{jobId}` echoes the polled `jobId` as the response's JSON-RPC `Id`, on every answer including the refusals — the poll leg's counterpart to the dispatch leg echoing `request.Id`. There is no request envelope on a `GET`, so the `jobId` the caller addressed the request with is the correlation key both sides already share.

## Two peer-auth substrates, and which one guards what (Phase 317)

The SDK ships **two unrelated ways** for one deployment to authenticate another, and they are legitimately different tools rather than two attempts at one:

- **The static-bearer substrate** — `ServerConfig.PeerRoutePrefixes` + `PeerBearerAuthMiddleware` (Phase 37 / Phase 137), in `ToolUp.Platform.Server`. A caller names itself in `X-Peer-Name` and presents a shared secret read from `ISecretStore` at `peers/{peerName}/bearer`; a constant-time match stamps `HttpContext.Items["PeerName"]` and the request continues.
- **The signed-JWT substrate** — this companion. Per-call HS256 (or Phase 343 ES256 / RS256) tokens, validated fail-closed by `IPeerAuthProvider` inside the `/peer/v1/*` handlers.

They are **not** the same guarantee, and the gap is the whole reason this section exists:

| Property | Static bearer | Signed-JWT peer substrate |
|---|---|---|
| Expiry | none — valid until an operator rotates the secret | `exp` required, 5-minute minted lifetime, 60 s skew ([Validation is fail-closed at every step](#validation-is-fail-closed-at-every-step)) |
| Audience | none — the token names no receiver | `aud` fixed-time compared against the receiver's own peer id (Phase 130 / 309) |
| Per-call minting | none — one long-lived value replayed verbatim | minted per call; `IPeerReplayGuard` + call scoping available on top (Phase 338) |
| Delegated originator | none — `X-Peer-Name` is self-asserted | `VerifyDelegation` run by the host before dispatch, mandatory (Phase 330) |
| Asymmetric keys | no — symmetric and shared, so the receiver can mint what the caller can | `AsymmetricPeerAuthProvider`, ES256 / RS256, verify-only custody (Phase 343) |
| Transport posture | the deployment's ingress problem | https-only off loopback by default (Phase 339) |
| Trust boundary | the header is the identity | the call context is rebuilt from the **validated principal**, never the wire body ([Trust boundary](#trust-boundary)) |

**When each is right.** A static bearer needs no key ceremony and is the right tool for a small, operator-controlled set of internal callers on a route the deployment already fronts with TLS. It is the wrong tool for a federation edge with an organisation you do not operate — that is what this companion is for.

### The overlap advisory

`PeerRoutePrefixes` entries are ordinary case-insensitive `StartsWith` prefixes, so `"/peer/"` — the most natural name to reach for — claims the whole `/peer/v1/` namespace [the host routes](README.md#routes) serve. `PeerBearerAuthMiddleware` is registered *ahead of the Giraffe router*, so when that happens the static-bearer gate runs first and decides who reaches the signed-JWT host. It fails quietly in both directions:

- A typed peer client presents a signed peer JWT and **no** `X-Peer-Name` header, so every federation call is answered `401 missing_peer_name_header` before dispatch. The federation surface looks composed and answers nothing.
- If static bearers *are* seeded and callers *do* send `X-Peer-Name`, the federation edge has grown a second, weaker, never-expiring credential to distribute out of band — and its refusals are audited under `_platform.peer.bearer`, not the peer call trail.

`PeerBearerAuthMiddleware.auditPeerAuthPosture : ServerConfig -> PeerAuthPosture` classifies the composition at compose time. Six rungs, weakest-guarantee-last:

| Rung | Meaning |
|---|---|
| `NoPeerAuthSurface` | neither substrate composed |
| `SignedPeerAuthOnly` | this companion alone — the strongest posture |
| `StaticBearerOnly` | the bearer flavour on prefixes of its own; legitimate |
| `StaticBearerOnReservedNamespace` | bearer prefix covers `/peer/v1/`, but `PeerSubstrate = NoPeerSubstrate`, so nothing is shadowed **yet** |
| `BothSubstratesDisjoint` | both composed, disjoint prefixes — the documented coexistence |
| `StaticBearerShadowsSignedPeer` | the defect: the host is serving and a bearer prefix covers its namespace |

`configurePipeline` logs one `peer-auth-posture:` `Warn` at startup for the last rung only, naming the offending prefixes, the `401` symptom and the fix. **Advisory only** — neither auth path changes behaviour, and a composition that registers no peer prefix never runs the classifier at all (GP 13). `StaticBearerOnReservedNamespace` is deliberately silent: warning about a collision with a host the deployment does not run is a warning about a composition that does not exist, and an advisory that fires on a correct configuration is one operators learn to ignore. It stays classified so a deployment that wants to hold the line early can assert it in its own preflight — the same posture-as-data shape `auditAudienceBinding` (Phase 309) and `auditFederationGraph` (Phase 591) take.

## Identity layer

`JwtPeerAuthProvider` is the BCL-only (no package dependency), fail-closed default `IPeerAuthProvider`. It mints and validates **HS256** bearer tokens using a symmetric per-peer signing key read from `ISecretStore` at scope `_platform`, key `peers/{peerId}/signing-key`. The same secret is shared out of band with the peers a deployment talks to; both sides sign / verify with it.

**The key is read on every issue / validate** (GP 12 rule 4) — there is no cached key, so a rotated key flows through immediately.

### Validation is fail-closed at every step

`ValidatePeerToken` returns `Error (PeerUnauthorized …)` on *any* defect — there is no path that returns a principal for an unverified credential and no "auth disabled" mode:

1. **Format** — token must split into exactly three Base64URL parts.
2. **Issuer** — `iss` claim must be present; its signing key must exist in `ISecretStore` (the key lookup is keyed by the *issuer*, so the validator independently re-derives whose key to check rather than trusting the token).
3. **Algorithm** — `checkAlgorithm` rejects anything but `alg: HS256` *before* touching the signature — defence in depth against algorithm confusion (`alg: none`, `alg: RS256` verified against an HS256 secret, etc.).
4. **Signature** — HMAC-SHA256 over `{header}.{payload}`, compared in constant time (`CryptographicOperations.FixedTimeEquals`, length-checked first).
5. **Expiry** — `exp` is **required** ("no expiry" is never a safe default for a bearer credential); a token past `exp + 60s` skew is rejected.
6. **Not-before** — `nbf` is optional; a present, future `nbf` (beyond 60s skew) rejects the token.

Clock-skew tolerance is 60 seconds (second-precision, the JWT standard's lower bound — GP 12 rule 6). Minted tokens carry a **5-minute** lifetime (`exp = iat + 300`) — a peer token is minted per call, not cached. The `UserContext` rides in a `uctx` string claim, serialised through the universal converter set so the DU survives the wire.

### Delegation

`VerifyDelegation` validates a `DelegatedAssertion` by HMAC over the canonical `{Subject}|{chain}` byte string, signed by the **last** peer in the delegation chain (its key read from `ISecretStore`), compared constant-time. An empty chain is rejected. The `Delegated` `UserContext` case + the cascade fields are wired from day one so a future federation phase does not force a v2 wire break — but the foundation only *verifies* a delegation assertion; it does not yet mint cascades.

**Verification is mandatory, and the host performs it (Phase 330).** `ValidatePeerToken` authenticates the *calling peer*; the `uctx` it returns rode inside that peer's own signed payload, so a `Delegated` case is an assertion the outer signature says nothing about. `JsonRpcPeerHost`'s contract-dispatch path therefore runs a `Delegated` originator through `VerifyDelegation` **before** rebuilding the call context and refuses `PeerUnauthorized` on failure — without it, any peer holding a valid signing key could name any subject as the originator. `Anonymous` / `Direct` short-circuit untouched. A bespoke host built on `IPeerAuthProvider` must do the same; the compatibility consequences are in [`docs/migrations/330-peer-delegation-verification.md`](../../docs/migrations/330-peer-delegation-verification.md). Relatedly, a `uctx` claim that is present but will not deserialise is now an explicit rejection rather than a silent downgrade to `Anonymous`.

### Trust boundary

The JSON-RPC host rebuilds the `PeerCallContext` from the **validated `PeerPrincipal`**, never from the self-asserted wire payload:

```fsharp skip=fragment
let trustedContext = {
    payload.Context with
        Peer = principal.Caller     // from the verified token, not the body
        User = principal.User
}
```

A caller cannot spoof its identity by editing the request body — the body's `Peer` / `User` are overwritten with the cryptographically-verified principal before dispatch.

## Cascade guards

`DefaultPlatformPeer.Handle` runs four guards in order before any registered `Dispatch` runs (all checked in the peer, so they hold regardless of which host invoked the dispatch):

1. Unknown contract id → `PeerContractNotFound`
2. Requested version not in the contract's supported set → `PeerVersionMismatch (requested, supported)`
3. `HopsRemaining <= 0` → `PeerHopLimitExceeded`
4. A repeated peer id anywhere in `Route` → `PeerLoopDetected route`

`ContractVersion` compares structurally with Major dominating. The contract table is a `ConcurrentDictionary` keyed by contract id; registration is idempotent (re-registering an id overwrites). `Handle` is stateless between dispatches (GP 12 rule 4) — it reads only the table and the per-call context.

## Job-substrate fusion

A long-running contract method has shape `… -> Async<PeerJobHandle<'T>>`. It cannot resolve inside the inbound HTTP request, so the host fuses it onto the existing job substrate (`IJobScheduler`) — **no new runtime**.

**Dispatch side** (`scheduleDispatch`): schedules a `Manual`-trigger `JobRegistration` under scope `PeerJob.Scope = "_platform"`, handler name `PeerJob.handlerName contractId methodName` = `_platform.peer.{contractId}.{methodName}`, `CreatedBy = PeerJob.SourceModule = "_platform.peer"`, then `TriggerOnce` and returns the assigned `JobId` (serialised) for the caller to poll. Job payload is a `PeerJobPayload` envelope: the call's argument JSON plus the **validated** scheduling caller's `PeerId` (read from the trusted call context, never the wire body) — `JobContext` carries no scheduling-caller identity, so the owner rides the payload (Phase 308).

**Execution side** (`PeerJobHandler : IJobHandler`): the scheduler dispatches it; it unmarshals the call arguments from the payload envelope, applies the contract implementation, resolves the returned `PeerJobHandle<'T>` to a terminal state, and persists the serialised result stamped with the envelope's owner. The handler always finishes the *job* as `Success` (the job of *capturing the terminal status* succeeded); a peer-side failure is recorded as a `Failed` status in the result store — **not** a job retry, because re-running a deterministic peer computation would double-execute it.

**Result store** (`IPeerJobResultStore` / `BlobPeerJobResultStore`): the job substrate's own `JobResult` carries no payload, so the typed (serialised) result is parked here keyed by `JobId`, as a `PeerJobRecord` carrying the scheduling caller's `PeerId`. Default layout: one JSON document per job under the reserved `_platform` container at `peers/jobs/{scopeId}/{jobId}.json`, mirroring `BlobPeerRegistry`. Absence of a stored record *is* the `Pending` signal — the poll route reports `None` as `Pending`.

**Retention** (Phase 316): the stored document (`PeerJobDocument`) carries the `PeerJobRecord` fields plus two retention stamps, and the store honours a `PeerJobRetentionPolicy` value — `Ttl` (expiry from the terminal write), `DeleteOnRead` + `GraceWindow` (reclaim once read, with a window wide enough that a retried poll still resolves). Policy is data, not behaviour (GP 12 rule 3), so a distributed store honours the same contract; `IPeerJobResultStore.Retention` reports the one in force. Enforcement is **lazy, on read** — an expired document reads as absent and its blob is deleted in the same call, which keeps the store free of a background sweeper (GP 13). The default is a deliberately generous 30-day TTL with delete-on-read off (GP 11), and a document written before Phase 316 carries no stamp, so it is kept indefinitely. A retired record is indistinguishable from one that never existed — the same non-disclosure posture Phase 308 takes for an unknown `jobId`. See [`docs/migrations/316-peer-job-result-retention.md`](../../docs/migrations/316-peer-job-result-retention.md).

**Poll-route ownership** (Phase 308): `GET /peer/v1/{contractId}/jobs/{jobId}` compares the parked record's owner against the polling principal's `PeerId`. A different validated peer is refused `PeerUnauthorized` (401, no result body) — possession of the server-minted `jobId` Guid is not authorization (GP 4). An absent record reports `Pending` to every validated caller, deliberately the same answer for "not finished" and "never existed", so an unknown `jobId` discloses nothing.

**Zero cost when unused** (GP 13): the `PeerJobFusion` singleton is registered only when `JobScheduler <> NoJobScheduler`. Absent it, a long-running method's dispatch is a closure that immediately returns `PeerHandler "Long-running contract method '…' requires the peer job-fusion substrate, which is not enabled"`, and the method contributes no job handler.

### Clean-room gate

A contract composed with `PeerServerApp.withCleanRoomTemplate contractId template` has its `PeerContractRegistration.Dispatch` wrapped by `CleanRoomGate.wrap`, so the composed privacy floor is applied to **every** answer of **every** method on that contract — by the substrate, not by the handler. A handler that never calls `ICleanRoomBroker.Enforce` (the shape Phase 18b's broker relied on and nothing checked) cannot release row-level data: its answer travels down the wrapper, which is the only route `IPlatformPeer` has to the wire.

Three invariants are checked by the wrapper regardless of which `ICleanRoomBroker` is resolved — the seam is substitutable, the composed floor is not:

1. **Surface** — a method outside `template.AllowedMethods` is refused *before the handler runs*.
2. **Checkability** — an answer that does not deserialise as a `CohortResult` is withheld. A gated method answering in some other shape has produced something the floor cannot be evaluated against, and a privacy gate's failure mode is silence.
3. **Release post-condition** — a `Released` decision is re-checked with `PrivacyGate.isStricterOrEqual template.Floor (PrivacyGate.observed released)`; a broker that released below the floor is overridden.

The broker's own `Enforce` runs between (1) and (3) and is where the suppression / gate-composition mechanism lives.

A withhold reaches the caller as `PeerCleanRoomWithheld templateId` (code `-32008`, HTTP `200` like every other structured dispatch outcome) carrying **no quantitative detail**: the broker's reasons name cohort sizes, and a caller able to vary its query and read them back has a counting oracle over the protected data. The full reason is recorded receiver-side as a `PeerCleanRoomDecision` audit row; the Phase 18a audit-transparency contract is the deliberate, caller-scoped route to any of it.

`withCleanRoomTemplate` naming a contract id the deployment does not host is a composition defect and `run` refuses to start — an inert privacy gate that looks composed is the failure this seam exists to remove. `PeerServerApp.auditCleanRoomTemplates` reports the same finding as data for a deployment's own preflight.

A composition that never calls `withCleanRoomTemplate` wraps nothing, probes nothing, and registers the same `PeerContractRegistration` values it did before (GP 11 / GP 13). The raw `ICleanRoomBroker` API is unchanged for bespoke callers — notably any caller that has a caller-requested gate to compose, which the peer wire format does not carry.

## Audit emission

The foundation ships **one** audit event — `PeerCallCompleted` — emitted best-effort per inbound call by the contract handler after dispatch reaches a terminal outcome (`auditPeerCall`). It resolves `IAuditLog` per-request; a partial test host without one registered simply records nothing.

`PeerCallCompletedPayload` (defined in [`../ToolUp.Platform.Core/Shared/AuditTypes.fs`](../ToolUp.Platform.Core/Shared/AuditTypes.fs), serialised by [`../ToolUp.Platform.Server/Server/AuditLog.fs`](../ToolUp.Platform.Server/Server/AuditLog.fs)):

| Field | Source |
|---|---|
| `ContractId` / `MethodName` | the dispatched call |
| `CallerPeerId` | the **validated** `PeerPrincipal.Caller.PeerId` — never the wire body |
| `RootRequestId` | the cascade-wide correlation id, shared across every hop |
| `Succeeded` | `true` when dispatch returned `Ok` |
| `Outcome` | `"ok"` on success, else the `PeerError` DU case name |
| `OccurredAt` | wall-clock resolution time |

Reserved `SourceModule = "_platform.peer"`. PII-free: identities are peer ids plus a correlation id — never end-user payload. The event records under scope `PeerJob.Scope = "_platform"`.

`PeerCleanRoomDecision` joins it on a gated contract: one row per gated dispatch, carrying `TemplateId`, `Released`, the `SuppressedCells` labels, and the gate's own `Reason`. Same family, same scope, same PII-free terms — the labels are author-chosen histogram buckets, never a cell value. It is the only place the withhold reason exists; see [Clean-room gate](#clean-room-gate) for why the wire refusal stays quiet.

> The phase originally sketched five events (`PeerCallStarted` / `…Completed` / `…Failed` / `PeerHandshakeCompleted` / `…Failed`). The foundation collapses the call lifecycle to the single terminal `PeerCallCompleted` (which carries success/failure in `Succeeded` + `Outcome`, removing the need for separate started/failed events) and defers handshake audit to the follow-on transparency phase (18a). Documenting only the shipped event keeps this guide honest.

## Six-rule portability audit (GP 12 / Phase 9c)

The foundation introduces five interfaces. Each is audited against the six framework-portability rules so a future distributed binding (Akka.NET / Orleans / a hosted gateway) can implement them without a signature change.

| Interface | R1 identity by value | R2 async at boundary | R3 retry/supervision as data | R4 stateless between calls | R5 no cross-shard ordering | R6 precision at lower bound |
|---|---|---|---|---|---|---|
| `IPlatformPeer` | ✓ ids + `PeerCallContext` are values | ✓ `Handle` / `Capabilities` return `Async<_>` | ✓ failure is `PeerError` (typed DU); no callbacks | ✓ default reads only the contract table + context | ✓ per-contract dispatch is independent; no ordering promise | ✓ no temporal field it owns |
| `IPeerClient` | ✓ `TargetPeer` / ids by value | ✓ `Invoke` / `PollJob` return `Async<_>` | ✓ `Result<_, PeerError>`; retry is caller-side | ✓ HTTP transport holds no per-call state | ✓ each call independent | ✓ — |
| `IPeerAuthProvider` | ✓ `PeerIdentity` / `DelegatedAssertion` by value | ✓ all three members `Async<_>` | ✓ `Result<_, PeerError>`; fail-closed, policy-free | ✓ reads the signing key fresh every call (no cache) | ✓ token issue/validate are order-independent | ✓ `exp`/`nbf` at second precision (JWT lower bound) |
| `IPeerHandshake` | ✓ `TargetPeer` / `CapabilityList` by value | ✓ `Negotiate` / `LocalCapabilities` `Async<_>` | ✓ `Result<_, PeerHandshakeError>` | ✓ default holds only the local peer ref + fetch fn | ✓ negotiation per-target independent | ✓ — |
| `IPeerRegistry` | ✓ `PeerId` string keys, records by value | ✓ `Resolve` / `List` / `Register` / `Remove` `Async<_>` | ✓ returns `option` / `Result`; no supervision shape | ✓ `BlobPeerRegistry` reads/writes through to blob each call | ✓ per-peer document; no cross-key ordering | ✓ — |

All five pass. No interface carries a live handle, a fire-and-forget `Tell`, a callback, a supervision-strategy object, or a framework serialisation attribute.

## Simplifications + deferrals (foundation)

Known boundaries — each a single "follow-up", not a hidden bug:

- **Single audit event** — only `PeerCallCompleted`. No `PeerCallStarted` (no in-flight visibility), no separate handshake audit. The handshake-transparency surface is Phase 18a.
- **In-memory handshake** — `InMemoryPeerHandshake` negotiates against the local peer's live capabilities + a direct capability fetch; there is no cached / persisted negotiation result. Sophisticated negotiation (capability intersection policy, downgrade rules) is Phase 18d.
- **No fan-out / cascade minting** — the cascade fields (`Route`, `HopsRemaining`, `RootRequestId`, `ParentRequestId`) and the `Delegated` `UserContext` case are *reserved and enforced* (loop + hop guards run) but the foundation does not itself mint a multi-hop cascade or aggregate fan-out results. `IPeerFanout` / `IPeerCascade` + federation trust anchors are a follow-on substrate phase.
- **Long-running result GC — closed (Phase 316), with one boundary left.** `BlobPeerJobResultStore` now honours a `PeerJobRetentionPolicy` (`Ttl` / `DeleteOnRead` / `GraceWindow`), defaulting to a generous 30-day TTL. Enforcement is lazy — an expired document reads as absent and is deleted on that read — so a result nobody ever polls occupies storage until something reads it or an external sweep runs. Configuring the policy from compose (`PeerServerApp.withJobRetention`) is not yet wired: a deployment overriding it registers its own `IPeerJobResultStore` constructed with the desired policy.
- **Single signing-key per peer** — `peers/{peerId}/signing-key` is one symmetric secret; there is no key-id / rotation-overlap window (rotate by replacing the secret; in-flight 5-minute tokens signed with the old key fail closed after the swap). Asymmetric (RS256 / EdDSA) peer keys are out of scope for the foundation.
- **`alg` is HS256-only** — by design (the fail-closed default). A deployment that wants asymmetric keys supplies its own `IPeerAuthProvider`.
- **Host-only local identity** — `withLocalPeer` is optional; a host-only deployment that never calls peers leaves it `None`, and the calling-side singletons fail closed (no signing key for an empty id) if actually used. No compile-time enforcement that a calling deployment set it.

## Follow-on phase boundaries

The foundation deliberately stops at "two deployments, one typed call, verified identity, audited outcome". The roadmap's follow-on phases extend it:

- **18a — cross-deployment audit transparency** — the richer audit surface (handshake events, in-flight `PeerCallStarted`, a queryable cross-deployment trail). Substrate-shape; remains in this companion when authored.
- **18b — clean-room broker** — k-anonymity, suppression, output-shape constraints. Commercial-domain privacy primitives; lands outside the OSS companion.
- **18c — federation primitives** — `IPeerFanout`, `IPeerCascade`, federation trust anchors, aggregation. The aggregation strategy is deliberately out of the SDK.
- **18d — capability negotiation** — sophisticated handshake: capability intersection policy, version-downgrade rules, negotiated-result caching.
- **18e — non-F# peer SDK** — a peer SDK in another language against this same JSON-RPC 2.0 wire format (the reason the wire format is open and decoupled from ToolUp.Remoting).

## Test surface

The foundation's behaviour is covered by an in-process contract pack bound against the default receiver plus a genuine two-deployment worked example over `Microsoft.AspNetCore.TestHost`:

| File | What it exercises |
|---|---|
| `IPlatformPeerContract.tests` (in-process binding) | The ≥ 8 contract-surface tests over a fresh contract table with no transport — register / dispatch / version mismatch / unknown contract / unknown method / hop limit / loop detection / capabilities. |
| `PlatformPeerTests.workedExampleTests` | A buyer deployment calls a typed contract on a seller deployment across an HTTP boundary through the real `JsonRpcPeerHost.routes` (auth-gated, fail-closed) and the real `HttpPeerClient`. Covers: happy-path round trip + a single `PeerCallCompleted` audit row + a `RootRequestId` matching on both sides of the wire; and identity validation (a buyer signing with the wrong key is rejected `PeerUnauthorized`, and the unauthorized call never reaches dispatch so no audit row is emitted). |

Run via `dotnet run --project Build.fsproj -- VerifyAll` (all Expecto packs) — **not** `dotnet test`.

## See also

- [`README.md`](README.md) — overview, public surface, how to enable, how to author a contract.
- [`../ToolUp.Scheduling/TECHNICAL_GUIDE.md`](../ToolUp.Scheduling/TECHNICAL_GUIDE.md) — the companion technical-guide shape this mirrors.
