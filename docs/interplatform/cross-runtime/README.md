# Cross-runtime conformance drivers

The generated non-F# peer clients, executed.

[`../FEDERATION_WIRE.md`](../FEDERATION_WIRE.md) specifies the seam and
[`../wire-fixtures/`](../wire-fixtures/) is its executable half. Those hold the **.NET emitters** to
the specification. This directory holds the two drivers that additionally hold the clients the SDK
**generates** to it — the output of `TypeScriptClientGen.emit` and `PythonClientGen.emit`, run under
a real Node and a real Python.

The distinction is not academic. A consumer's non-F# peer never runs the .NET emitters; it runs
generated code. Until this harness existed, that code was pinned only by string-containment
assertions, which cannot see a document whose member order is lexicographic, whose whitespace is
significant, or whose union case is encoded as an object where the specification says a bare string.
All three are conformance failures that compile, run, and look right.

## Layout

```
driver.mjs   the Node leg
driver.py    the Python leg
```

Neither is a general-purpose tool. Each makes a **fixed sequence of nine calls** and prints one JSON
report to stdout; it asserts nothing. The interpretation happens in F#, where the corpus and the live
receiver are both in reach — see `CrossRuntimeConformanceHarness` and
`CrossRuntimeFederationConformanceTests` in the platform test pack.

## What a run does

The harness writes the generated client beside the driver (`client.ts` / `client.py`), starts a
loopback receiver on an ephemeral port, and runs:

```
node [--experimental-strip-types] driver.mjs <baseUrl> <token> <callerPeerId>
python driver.py <baseUrl> <token> <callerPeerId>
```

The nine legs, in order. The driver's sequence and the receiver's script are two halves of one
contract — change one and you must change the other:

| # | Leg | Answered by |
|---|---|---|
| 1 | `capabilities` | the live receiver's `Capabilities()` |
| 2 | `immediate` | a live `IPlatformPeer.Handle` dispatch |
| 3 | `handlerError` | a live dispatch into a handler that fails |
| 4 | `corpusResult` | `contract-invocation/response.json`, verbatim |
| 5 | `corpusError` | the first entry of `contract-invocation/errors.json` |
| 6 | `corpusMalformed` | `contract-invocation/reject-malformed.json`, verbatim |
| 7–9 | `pollPending` / `pollCompleted` / `pollFailed` | the three terminal states of `contract-invocation/job-poll.json` |

Legs 1–3 prove the generated client can talk to a real receiver behind a real bearer-token check.
Legs 4–9 prove it reads what the **specification** says, rather than what this codebase happens to
emit — the responses are corpus bytes, served untouched.

## Runtime requirements

- **Node**: any version that can execute a `.ts` module. Type stripping is on by default from 23.6
  and available behind `--experimental-strip-types` from 22.6. The generated TypeScript is
  deliberately erasable-syntax-only so no build step, bundler or toolchain is needed.
- **Python**: 3.8 or later. The generated client uses only `json`, `uuid` and `urllib.request`.

The harness **probes by executing**, not by parsing a version string: it runs a tiny script shaped
like the real driver and takes whichever invocation actually worked. A runtime it cannot find is
reported as a skip carrying the probe's own reason. A skipped leg is never a pass — but a leg that
cannot run is also not a failure of the code under test, and saying which is which is the whole
value of reporting the reason.

## Running it by hand

The legs run as part of the platform test pack, so the ordinary invocation is:

```
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
```

To run only these cases:

```
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter "ToolUp.Platform.Tests.Phase 189"
```

The filter separator is a dot, not a slash; a filter that matches nothing reports "0 tests run …
Success!" and exits 0, so read the count rather than the verdict.

## Proving it can fail

A conformance suite is the archetypal shape that passes by doing nothing, so the pack carries a
negative control: the same pipeline is run once more with the generator's output deliberately
corrupted — an envelope member moved out of declaration order under Node, canonical separators
loosened under Python — and the emission checks must reject it. If the mutation ever stops matching
the generator's output, that case fails rather than silently proving nothing.
