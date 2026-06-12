# Phase 69k — source-generator-driven dispatcher

> **Substrate status: runtime contract only.** What ships today (`Server/Remoting/SourceGenDispatch.fs`) is the stable runtime target a generator emits against: the `[<DispatcherTarget>]` marker attribute, the `IGeneratedDispatchTable<'TContext, 'TImpl>` contract, and the process-wide `GeneratedDispatchRegistry`. The source-generator project itself — and the adapter hot-path that consults the registry — are follow-ups. **There is no consumer action today**; this doc exists so you know what's coming and when opting in will be worth it.

## What it will change

Today's dispatcher reflects over every API record at startup — walking fields, building invocation thunks, registering routes. That cost is paid on every `createApi()`, which is observable in cold-start-sensitive deployments and test harnesses that build many server instances; and a handler/record type mismatch surfaces at *first-request runtime*, not compile time.

The generator moves the dispatch table to **compile time**: it scans for `[<DispatcherTarget>]`-marked API records and emits one `IGeneratedDispatchTable` implementation per record — statically-typed route handlers, no `MethodInfo.Invoke` on the hot path. The runtime consults `GeneratedDispatchRegistry` per API record: a registered table short-circuits reflection; an absent one falls back to today's reflection path. The two paths are **wire-compatible by construction** — byte-identical responses.

## When to opt in (once it ships)

- **Compile-time contract errors** — the headline for most consumers. Handler-signature / record-field mismatches, and missing per-method classifications (e.g. the 69d authorization gate), become build errors instead of first-request failures.
- **Cold-start cost** — serverless-shape deployments and `TestServer`-heavy suites recover the startup reflection cost.
- **AOT / single-file publish** — a reflection-free dispatch path is what makes `PublishAot` viable for a Remoting server.

If none of those bite, the reflection path remains fully supported — the generator is an optimisation, not a replacement.

## Reflection fallback compatibility

The fallback is structural, not configured: the registry is consulted per API record type, so a solution can mix generated and reflected records freely, and a consumer that never references the generator package behaves exactly as today. Re-registration replaces an existing table (idempotent for hot-reload). The byte-pin response-shape test gallery is the regression gate that both paths must pass identically.

## What you can do today

Nothing is required. If you want to be generator-ready ahead of time, marking API records with `[<DispatcherTarget>]` is harmless — the attribute is inert metadata until a generator emits tables for it. Advanced consumers can also hand-author an `IGeneratedDispatchTable` implementation and register it via `GeneratedDispatchRegistry.register` as a proof-of-shape, but the adapter does not yet consult the registry on the request path, so this buys nothing in production yet.

## Verification (once the generator ships)

This section will be filled in with the generator's package reference, the diagnostics it emits, and the cold-start benchmark procedure when the follow-up lands. Until then: no action, nothing to verify.

## See also

- [69-family-overview.md](69-family-overview.md) — family map and adoption sequence.
- Substrate: `src/ToolUp.Platform.Server/Server/Remoting/SourceGenDispatch.fs`.
