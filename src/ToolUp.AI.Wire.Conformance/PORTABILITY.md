# ToolUp.AI.Wire — portability guarantee

`ToolUp.AI.Wire` and the three provider wire mappers (OpenAI, Gemini, Claude)
are **host-portable**: the same F# source compiles to a .NET backend
(`System.Text.Json` under the hood) and a Fable/browser backend (the
platform's native JSON under the hood), and both backends produce the
**same bytes out** and the **same values in**.

This package is the gate that *earns* that claim instead of asserting it. It
is not a unit-test pack — it is a **conformance contract** (GP 12): a fixture
corpus run on **both** hosts, failing the build on any divergence.

## The guarantee

For every fixture in the corpus, on **both** the .NET and Fable hosts:

1. **Request byte-parity.** `buildRequestBody` emits **byte-identical** JSON
   on both hosts — same field set, same member order, same number / string /
   base64 encoding. The provider's HTTP request body is therefore independent
   of which host assembled it.
2. **Response structural-parity.** `parseResponse` reifies an **identical**
   `AIProviderResponse` on both hosts — content, tool calls (id / name /
   re-serialized arguments), stop reason, and `TokenUsage` (prompt / cached /
   output tokens).
3. **Streaming-assembly parity.** The pure `ClaudeStreaming` state machine
   folds a canned SSE `data:` chunk sequence into the **identical** finalized
   response and the identical sequence of surfaced text deltas on both hosts —
   including out-of-band tool-argument assembly, split usage accounting, and
   the post-stream `{}` default-fill for a zero-input tool call.

A browser host that supplies a `fetch`-backed `IHttpTransport` (Phase 251) can
therefore reuse these connector mappings unchanged and obtain the same wire
behaviour the server host produces.

## How it is enforced

The dual-run harness is expressed **once** — `ConformanceSuite.fs`, a single
source compiled by both hosts against signature-compatible test facades
(Expecto on .NET, `node:test` on Fable). Because the assertion logic is one
file, it **cannot drift** between hosts; only the corpus values and the two
backends vary.

| Host | Project | Runner | Invocation |
|---|---|---|---|
| .NET | `ToolUp.AI.Wire.Conformance.fsproj` | Expecto | `dotnet run --project src/ToolUp.AI.Wire.Conformance/ToolUp.AI.Wire.Conformance.fsproj` |
| Fable | `Fable/ToolUp.AI.Wire.Conformance.Fable.fsproj` | `node:test` | `dotnet tool restore` → `dotnet fable -o output --noCache` → `node --test output/ProgramFable.js` (from `Fable/`) |

CI runs **both** legs and fails on any drift (`.github/workflows/checks.yml`,
job `ai-wire-conformance`). A green build on a single host proves nothing about
portability; only a green build on **both** does.

The golden corpus data is **not** duplicated here — it is source-linked from
the provider fixture set (one source of truth), so a fixture added there is
exercised by this gate automatically.

## Bounds of the guarantee

The contract covers the **pure wire-mapping surface** and nothing below it:

- **HTTP egress is out of scope.** `IHttpTransport` (the host's `fetch` /
  `HttpClient`), TLS, redirects, connection reuse, and real network timing are
  the host's responsibility. The retry/backoff *policy* and error
  *classification* are pure and portable (Phase 251) and are gated separately;
  the bytes that travel are not this package's concern.
- **Streaming transport framing is out of scope.** The `ClaudeStreaming`
  machine is gated over a **canned** `data:` payload sequence. SSE framing,
  chunk boundaries, and back-pressure belong to the host's transport; the gate
  proves only that *given* the payloads, assembly is host-independent.
- **Structured-output is gated as request shaping, not provider enforcement.**
  Parity covers the request JSON the mapper emits for a structured-output
  schema; whether the provider honours it is the provider's behaviour, not a
  portability property.
- **Opaque / closure-erased fields are excluded by construction.** The portable
  contract carries data by value (GP 12). Any host-only capability that cannot
  cross as data — live handles, closures, host-specific options objects — is
  not part of the mapped surface and so cannot be gated. The corpus stays
  within the value-typed contract.
- **Numeric / escaping domain is bounded to the unambiguous range.** Fixtures
  stay within integral numbers, simple decimals, and string-keyed objects where
  byte-stability is unambiguous across the two JSON backends (no integer-like
  object keys, which a browser engine would reorder). Widening that domain is a
  deliberate corpus extension, not an assumed guarantee.

When the mapped surface grows (a new provider, a new request feature, a new
response field), the corpus grows with it in the same change — that is the
discipline this package exists to enforce.
