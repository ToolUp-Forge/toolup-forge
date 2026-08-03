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

Both clients take `(baseUrl, token, callerPeerId)`. `token` is a bearer token the receiver's `JwtPeerAuthProvider` validates (HS256, shared secret) — see "Identity" below. Immediate methods return the typed result; long-running methods return a `jobId` and a `poll*` helper.

```typescript
const client = new BuyerSellerClient("https://seller.example", token, "buyer-acme");
const reach = await client.GetReach({ Segment: "auto-intenders", MinK: 50 });
const { jobId } = await client.BuildReport({ Segment: "auto-intenders", MinK: 50 });
const report = await client.pollBuildReport(jobId);   // null until the job finishes
```

```python
client = BuyerSellerClient("https://seller.example", token, "buyer-acme")
reach = client.GetReach({"Segment": "auto-intenders", "MinK": 50})
```

## Identity

A non-F# peer must be registered in the receiver's peer directory (`IPeerRegistry`, blob-backed at `_platform/peers/{peerId}.json`) with its public key / allowed contracts, and must mint a bearer token the receiver validates. The generated clients accept the token; minting it (HS256 over the shared secret) is the operator's responsibility — a thin per-language signing helper is the Stage-4 follow-on.

## Verification status

The generators are **emit-verified**: deterministic from the schema and pinned by the `IPeerNonFSharpSdkContract` snapshot tests. Executing a generated client against a live `JsonRpcPeerHost` end-to-end (the cross-runtime round-trip harness under Node / Python) is the **18e.tail** follow-on — it pins the exact F#-DU wire encoding of the call context that the generated `context()` constructs.

## Writing a peer without a generator

The generators are a convenience, not the boundary. The wire itself — every document, its canonical encoding, its hashes, its error and refusal classes, and the versioning discipline — is specified language-neutrally in [`FEDERATION_WIRE.md`](FEDERATION_WIRE.md), with an executable conformance corpus in [`wire-fixtures/`](wire-fixtures/). An implementation in any language certifies against a named profile by round-tripping, re-stamping and refusing those fixtures; the exact call-context encoding the section above calls out is pinned there as `contract-invocation/request.json`.
