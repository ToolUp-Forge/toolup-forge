# Phase 181 — AIProvider behavioural conformance pack

**Type:** additive, test-only. **No production source changes** — no published-surface
change, no consumer migration required.

## What changed

The live-API `ProviderTestPack` (`src/ToolUp.AIProviders.Tests/Support/ProviderTestPack.fs`)
grew from a *wire-shape* pack into a *behavioural-conformance* pack. Two new
`ProviderSpec`-parameterised arms run for every shipped provider (Claude / OpenAI / Gemini),
alongside the existing direct + factory round-trips:

1. **Structured output** — drives `IAIProvider.SendStructuredMessage` against a trivial object
   schema (expects `Ok` with JSON-parseable `Content`) and against a non-conformable `oneOf`-root
   schema (expects a clean terminal rejection, or a conformant `Ok`).
2. **Capability gating** — routes a multimodal (`ImagePart`) message to a non-vision model and
   asserts a synchronous `UnsupportedCapability("vision", _)` — no vendor HTTP round-trip.

`ProviderSpec` gained one field, `NonVisionModel: string` — the model id the capability-gating arm
constructs the provider with. (Need not be a currently-available model; the rejection fires purely
from the provider's `isVisionCapable` classifier, before any key validation or network call.)

The env-var gate is unchanged: with no `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `GEMINI_API_KEY`
set, the whole per-provider `testList` collapses to a single `Pending` case (GP 13) — a fresh
checkout is byte-for-byte green.

## Native-vs-fallback boundary (why the negative arm accepts two error shapes)

The shipped providers implement `SendStructuredMessage` natively and do **not** pre-validate the
schema. A `oneOf`-root schema is therefore rejected differently per path:

- the **fallback** (`IAIProviderDefaults.sendStructuredViaFallback`, used by external implementers)
  surfaces `SchemaUnsupported`;
- a **native** provider ships the schema to the vendor, whose HTTP 4xx surfaces as `PermanentClient`.

Both are clean, non-retryable rejections. The conformance bar asserts the request is rejected
terminally (or returns a conformant `Ok`); it does **not** force `SchemaUnsupported` specifically,
because that would red the live native paths without any production change. This variance is the
native-vs-fallback boundary Phase 67b documents — the bar pins it uniformly without erasing it.

## For external `IAIProvider` implementers

This pack doubles as the conformance bar an external provider can validate against: implement the
interface, build a `ProviderSpec`, and run `ProviderTestPack.tests yourSpec` with your key set.
The non-conformant-schema arm passes for both native and fallback-based implementations.

## Verification

- `dotnet run --project src/ToolUp.AIProviders.Tests/ToolUp.AIProviders.Tests.fsproj` — green on a
  fresh checkout (every conformance arm `Pending`, not `Failed`).
- With a provider key set, that provider's arms assert structured-output, capability-gating, and
  usage/streaming live.
- `dotnet build ToolUp.Forge.sln` clean; `dotnet run --project Build.fsproj -- Pack` unaffected
  (test project is `IsPackable=false`).

## Rollback

Revert the four touched test files. No consumer or published package is affected.
