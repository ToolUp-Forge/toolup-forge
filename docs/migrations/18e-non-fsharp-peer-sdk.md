# Migration — Phase 18e: non-F# peer SDK (schema + generators)

**Status:** additive, new surface. No consumer action required — this targets *non-F#* peers (F# peers keep using the typed F# proxy). Nothing changes for an existing deployment.

## What changes

`ToolUp.InterPlatform` gains a language-neutral **schema export** of a peer contract plus **TypeScript and Python client generators** that consume it. This realises the foundation's open-JSON-RPC-wire choice: a non-F# deployment can act as a peer using a generated, dependency-free client.

New public surface (all in `ToolUp.InterPlatform`):

- `PeerTypeRef` / `PeerMethodLifetime` / `PeerMethodSchema` / `PeerRecordSchema` / `PeerContractSchema` — the neutral schema.
- `PeerSchema.fromContract<'TApi>` / `PeerSchema.toJson` / `PeerSchema.formatVersion` — reflect a contract into the schema + serialise it.
- `TypeScriptClientGen.emit` / `TypeScriptClientGen.tsType` — emit a `.ts` client (dependency-free `fetch`).
- `PythonClientGen.emit` / `PythonClientGen.pyType` — emit a `.py` client (dependency-free `urllib.request`).

## Diff to apply

There is no consumer-side F# diff. To generate clients for a contract:

```fsharp
open ToolUp.InterPlatform

let schema = PeerSchema.fromContract<BuyerSellerContract> "buyer-seller"
System.IO.File.WriteAllText("buyerSellerClient.ts", TypeScriptClientGen.emit schema)
System.IO.File.WriteAllText("buyer_seller_client.py", PythonClientGen.emit schema)
```

See [`docs/interplatform/non-fsharp-peers.md`](../interplatform/non-fsharp-peers.md) for the type-vocabulary table, the generated-client usage, and the identity / registration steps.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "NonFSharpSdk"` — 11 passed, 0 failed. Covers the neutral type vocabulary (primitive / option / list / record / long-running unwrap), record flattening into the schema's `Records` table, schema JSON round-trip (golden-file-style), and TypeScript + Python emit-correctness (the generated source carries the expected interfaces / dataclasses / class + typed method signatures + the JSON-RPC POST + bearer-auth + long-running-poll skeleton).

## What is deferred (18e.tail)

The **cross-runtime round-trip harness** — executing a generated TS / Python client against a live `JsonRpcPeerHost` under Node / Python in CI — is the follow-on. It pins the exact F#-DU wire encoding of the call context the generated `context()` constructs, and needs Node + Python runtimes wired into the test matrix (env-gated, mirroring the `AIProviders.Tests` live-arm pattern). The generators themselves are deterministic and snapshot-tested, so emit-correctness is verified today; execution-verification is the tail.

## Rollback

The schema + generators are pure additions with no compose-time or runtime footprint (nothing is wired into `PeerServerApp.run`); a consumer that never calls `PeerSchema` / the generators is unaffected. Remove the generated client files to roll back a non-F# peer.
