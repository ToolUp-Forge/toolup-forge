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
| `PeerMethodNotFound` | `-32601` | `methodNotFound` (standard) |
| `PeerDeserialization` | `-32700` | `parseError` (standard) |

The structured `PeerError` rides in the error object's `Data` field (serialised); `Message` is a one-line human string. The DU **case name** (no payload detail — `JsonRpc.errorCaseName`) is the safe label used for audit `Outcome` and metrics, where the message text could leak handler internals.

### HTTP status codes

- `401` — missing / invalid / expired bearer token (auth gate, before any dispatch).
- `400` — request body failed to parse (`PeerDeserialization`).
- `200` — auth passed and dispatch ran; a *peer-side* `PeerError` still returns `200` with the error in the JSON-RPC envelope (the HTTP transport succeeded; the RPC failed). This is standard JSON-RPC framing.

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

### Trust boundary

The JSON-RPC host rebuilds the `PeerCallContext` from the **validated `PeerPrincipal`**, never from the self-asserted wire payload:

```fsharp
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
