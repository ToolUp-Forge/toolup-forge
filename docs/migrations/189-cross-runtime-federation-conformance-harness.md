# Migration — cross-runtime federation conformance harness

**Status:** additive. **Consumer action required:** none for an existing deployment. A consumer that
has *checked in* a previously generated Python or TypeScript peer client should regenerate it —
three defects in the generated output are fixed here, and one of them is a crash.

The harness itself is test-tier: no shipped runtime surface changes, no composition changes, and a
deployment that does not federate is byte-for-byte unchanged (GP 13).

## What changed

`PythonClientGen` and `TypeScriptClientGen` now produce output that has been **executed** against a
live receiver and certified against the federation-seam wire corpus, rather than pinned by
string-containment assertions. Running the generated code found four things that reading it did not.

### 1. The generated Python emitted non-canonical JSON

`json.dumps` defaults to `separators=(", ", ": ")` and `ensure_ascii=True`. Both violate the
canonical encoding the wire specification states exhaustively — §3.1 rule 1 (no insignificant
whitespace) and rule 5 (non-ASCII emitted literally). Every request a generated Python client sent
therefore carried spaces after each `:` and `,`, and would have escaped any non-ASCII string value.

The document still parsed, and every call still worked, which is precisely why this survived. It
matters wherever the bytes are the identity rather than the transport: a hash over a request, a
signature over a payload, a byte-comparison in somebody else's conformance suite.

The generated module now carries a `_canonical` helper and routes every outbound document through
it.

**Consumer action:** regenerate. A checked-in client keeps sending non-canonical documents until you
do.

### 2. The generated Python poll helper crashed on the ordinary case

`poll_<Method>` called `status.get("Completed")` on the decoded status. A union case with no payload
rides as a **bare string** (§3.1 rule 11), so `Pending` — the state a poll returns every time before
the job finishes — arrived as `"Pending"` and the call raised `AttributeError`. In other words the
poll helper worked only for a job that had already completed by the first poll.

Now guarded, matching what the TypeScript helper already did.

**Consumer action:** regenerate. This is a crash, not a cosmetic difference.

### 3. A dotted contract id produced an unparseable class name

Contract ids are dotted by convention (`example.orders`). The class name was built by upper-casing
the first character only, yielding `Example.ordersClient` — not a valid identifier in either target
language, so the generated module did not load at all. The id is now split on its separators and
PascalCased segment by segment: `example.orders` → `ExampleOrdersClient`. A single-segment id
(`schemaC` → `SchemaCClient`) is unaffected.

**Consumer action:** if your contract id contains `.`, `-`, `_`, `/` or a space, the generated class
name changes. Update the import site.

Worth noting for calibration: `docs/interplatform/non-fsharp-peers.md` has always shown
`"buyer-seller"` yielding `BuyerSellerClient`, which is what the generator now does and is not what
it did. The documentation described the intended behaviour correctly and nothing checked that the
code agreed — which is the same failure mode, one layer up, as pinning generated code by reading it.

### 4. The generated TypeScript needed a build step it did not admit to

The class used TypeScript **parameter properties** (`constructor(private baseUrl: string)`). That is
the one construct in the emitted file a type *eraser* cannot handle — it has to synthesise an
assignment — so Node refused the module with `ERR_UNSUPPORTED_TYPESCRIPT_SYNTAX` in strip-only mode.
The generated client advertised a dependency-free `fetch` transport and then required a compiler.

The class now declares its fields and assigns them in the constructor body. The emitted file is
erasable-syntax-only, so it runs directly on Node ≥ 23.6, and on ≥ 22.6 behind
`--experimental-strip-types`, with no build step, bundler or toolchain.

**Consumer action:** none. A consumer already running the output through `tsc` or a bundler is
unaffected.

## Added surface

Both generators now emit a `capabilities()` method calling `GET /peer/v1/capabilities`, plus (in
TypeScript) the two interfaces that type it. A non-F# peer previously had no way to ask which
contract versions a receiver supports, so it had to hard-code one and discover a mismatch from a
`PeerVersionMismatch` failure. This is purely additive to the generated output.

## The harness

- `docs/interplatform/cross-runtime/` — the two drivers plus a README describing the nine-leg
  sequence and the runtime requirements.
- `src/ToolUp.Platform.Tests/InProcess/CrossRuntimeConformanceHarness.fs` — client emission, a
  loopback HTTP/1.1 receiver over a live `IPlatformPeer.Handle` behind a live `JwtPeerAuthProvider`,
  runtime probing, and driver execution.
- `src/ToolUp.Platform.Tests/InProcess/CrossRuntimeFederationConformanceTests.fs` — 28 cases.

It reuses the Phase 596 corpus rather than minting a second one: request documents are compared
member-for-member against `contract-invocation/request.json`, and every response the generated
client reads is corpus bytes served verbatim. A second corpus is exactly the drift this harness
exists to prevent.

## What is deliberately not covered

- **The long-running dispatch leg.** The receiver is composed without a job-fusion substrate, so a
  `LongRunning` method fails with a clear "substrate not enabled" error by design. The poll *legs*
  are covered against all three terminal states the corpus pins; only the dispatch half is absent.
- ~~**A failed job is reported as "no result yet".**~~ **FIXED by
  [Phase 631](631-generated-client-terminal-job-states.md).** Both generated poll helpers returned
  `null` for a `Failed` status, because the emitted return type had no room for the failure — so a
  caller polling a job that had already failed polled forever. Both runtimes agreed about it, which
  is what this harness asserted; changing it changed the generated helper's return type and was a
  breaking change to the generated SDK, so it was filed rather than smuggled in here. The helpers
  now return a three-way terminal discriminator carrying the receiver's outcome string, and the
  harness's poll legs assert the failure rather than the shared blind spot. **Regenerate your
  client** — see that migration note for the old and new call shapes.
- **Delegated user assertions.** The generated clients send `User = "Anonymous"`; the `Direct` and
  `Delegated` cases are not reachable from the generated surface at all.

## CI

The cases live in `ToolUp.Platform.Tests`, which the `verify-all` job already gates, so they run on
every push and PR with no workflow change. Both runtimes are present on the standard Linux runner;
if the runner's Node predates type stripping, the Node leg reports itself skipped with the probe's
reason and the Python leg still runs. Pinning a newer Node for that job is a one-line
`actions/setup-node` step in the `verify-all` job, should the skip ever become the normal outcome.
