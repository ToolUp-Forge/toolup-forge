# Non-F# peers — TypeScript & Python clients

The inter-platform peer substrate commits to an **open JSON-RPC 2.0 wire format** rather than the in-tree F# transport, precisely so a non-F# deployment can act as a peer. Phase 18e generates idiomatic, dependency-free clients from a contract's language-neutral schema.

## The pipeline

```
F# contract record  ──PeerSchema.fromContract──▶  PeerContractSchema (JSON)
                                                          │
                                   ┌──────────────────────┼──────────────────────┐
                                   ▼                                              ▼
                       TypeScriptClientGen.emit                       PythonClientGen.emit
                                   │                                              │
                                   ▼                                              ▼
                          <contract>Client.ts                          <contract>_client.py
```

The schema is the single source of truth; both generators consume it, so the clients can never drift from the contract.

## Generating a client

```fsharp
open ToolUp.InterPlatform

// The contract is an ordinary record of functions — the same shape the
// in-deployment transport uses. `GetReach` is an immediate method;
// `BuildReport` is long-running (it returns a `PeerJobHandle`).
type ReachRequest = { Segment: string; MinK: int }
type ReachResult = { Reach: int }
type Report = { Url: string }

type BuyerSellerContract = {
    GetReach: ReachRequest -> Async<ReachResult>
    BuildReport: ReachRequest -> Async<PeerJobHandle<Report>>
}

let schema = PeerSchema.fromContract<BuyerSellerContract> "buyer-seller"

// TypeScript
System.IO.File.WriteAllText("buyerSellerClient.ts", TypeScriptClientGen.emit schema)

// Python
System.IO.File.WriteAllText("buyer_seller_client.py", PythonClientGen.emit schema)

// Or persist the neutral schema itself, to generate later / elsewhere:
System.IO.File.WriteAllText("buyer-seller.schema.json", PeerSchema.toJson schema)
```

## Type vocabulary

| F# | Schema | TypeScript | Python |
|---|---|---|---|
| `string` / `Guid` | `PrimString` / `PrimGuid` | `string` | `str` |
| `int` / `int64` | `PrimInt` | `number` | `int` |
| `float` / `decimal` | `PrimFloat` | `number` | `float` |
| `bool` | `PrimBool` | `boolean` | `bool` |
| `DateTimeOffset` | `PrimDateTimeOffset` | `string` (ISO-8601) | `str` (ISO-8601) |
| `'T option` | `OptionOf` | `T \| null` | `Optional[T]` |
| `'T list` / `'T[]` | `ListOf` | `T[]` | `List[T]` |
| record | `RecordRef` | `interface` | `@dataclass` |
| union | `UnionRef` | `unknown` | `Any` |

Records referenced by a method are flattened into the schema's `Records` table and emitted as one interface / dataclass each.

## Using a generated client

Both clients take `(baseUrl, token, callerPeerId)`. `token` is a bearer token the receiver's `JwtPeerAuthProvider` validates (HS256, shared secret) — see "Identity" below. Immediate methods return the typed result; long-running methods return a `jobId` and a `poll*` helper. Both also carry `capabilities()`, the first-contact handshake against `GET /peer/v1/capabilities`, so a non-F# peer can read the receiver's supported contract versions rather than hard-coding one.

A poll helper returns a **three-way terminal discriminator** — `pending` / `succeeded` / `failed` — not an optional result, because a job has three terminal states and an optional result can express two of them ([`FEDERATION_WIRE.md`](FEDERATION_WIRE.md) §5.5.6). `succeeded` carries the decoded result; `failed` carries the receiver's outcome string (the failing error's class) and its payload. Both end the poll loop.

The class name is the contract id split on its separators and PascalCased segment by segment, so `"buyer-seller"` yields `BuyerSellerClient`. The generated TypeScript is erasable-syntax-only: Node runs it directly (≥ 23.6, or ≥ 22.6 with `--experimental-strip-types`) with no build step.

```typescript
const client = new BuyerSellerClient("https://seller.example", token, "buyer-acme");
const reach = await client.GetReach({ Segment: "auto-intenders", MinK: 50 });
const { jobId } = await client.BuildReport({ Segment: "auto-intenders", MinK: 50 });

const poll = await client.pollBuildReport(jobId);
if (poll.state === "succeeded") use(poll.result);
else if (poll.state === "failed") logFailure(poll.outcome, poll.detail);
// "pending" is the only state that means "ask again"
```

```python
client = BuyerSellerClient("https://seller.example", token, "buyer-acme")
reach = client.GetReach({"Segment": "auto-intenders", "MinK": 50})

job_id = client.BuildReport({"Segment": "auto-intenders", "MinK": 50})
poll = client.poll_BuildReport(job_id)
if poll.state == "succeeded":
    use(poll.result)
elif poll.state == "failed":
    log_failure(poll.outcome, poll.detail)
```

## Identity

A non-F# peer must be registered in the receiver's peer directory (`IPeerRegistry`, blob-backed at `_platform/peers/{peerId}.json`) with its public key / allowed contracts, and must mint a bearer token the receiver validates. The generated clients accept the token; minting it (HS256 over the shared secret) is the operator's responsibility — a thin per-language signing helper is the Stage-4 follow-on.

## Verification status

The generators are **execution-verified**. They were emit-verified first — deterministic from the schema, pinned by the `IPeerNonFSharpSdkContract` snapshot tests — and the generated output is now additionally *run*: a real Node and a real Python drive the emitted client against a live `JsonRpcPeerHost` dispatch behind a live bearer-token check, and the documents it produces and consumes are certified against the wire corpus. See [`cross-runtime/`](cross-runtime/).

That step was worth taking rather than assuming. Running the output found three defects reading it had not — the Python client emitted non-canonical JSON, its poll helper crashed on the ordinary `Pending` state, and a dotted contract id produced a class name neither language could parse. String-containment assertions cannot see any of those. The fixes and what a consumer holding a checked-in generated client should do about them are in [`../migrations/189-cross-runtime-federation-conformance-harness.md`](../migrations/189-cross-runtime-federation-conformance-harness.md).

It also found a fourth, which the harness could only *record* rather than fix, because fixing it changed the emitted return type: both poll helpers reported a terminally-failed job as "no result yet", so a caller polling until a result appeared polled a dead job forever. That is the three-way discriminator above, and it is a **breaking change to generated client code** — a consumer holding a checked-in client must regenerate. See [`../migrations/631-generated-client-terminal-job-states.md`](../migrations/631-generated-client-terminal-job-states.md).

## Writing a peer without a generator

The generators are a convenience, not the boundary. The wire itself — every document, its canonical encoding, its hashes, its error and refusal classes, and the versioning discipline — is specified language-neutrally in [`FEDERATION_WIRE.md`](FEDERATION_WIRE.md), with an executable conformance corpus in [`wire-fixtures/`](wire-fixtures/). An implementation in any language certifies against a named profile by round-tripping, re-stamping and refusing those fixtures; the exact call-context encoding the section above calls out is pinned there as `contract-invocation/request.json`.
