# Migration 273 — SSR hosted-tree error-boundary + degraded fallback

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

A node that throws while a hosted tree renders **server-side** (the Phase 111 server-rendered-fragment
path) risked failing the whole request — one bad subtree 500-ing an entire SSR page (and voiding its
SEO first-paint). Phase 268 gives client render-fault telemetry and Phase 203 checks hydration parity,
but there was no server-side graceful degradation between them. This phase ships the server-side peer of
a client React error boundary.

New surface in `src/ToolUp.PublicRendering/Server/HostRenderBoundary.fs`
(namespace `ToolUp.PublicRendering`, module `HostRenderBoundary`):

- `guard : IHostRenderTelemetrySink -> nodeId:string -> render:(unit -> string) -> string` — wraps a
  hosted subtree render (the Phase 111 HTML-fragment shape). On a thrown node it captures a Phase 268
  `RenderFault` (node id + exception message) to the sink and returns the structured `defaultFallback`
  fragment **instead of propagating**, so the surrounding page completes (no 500). A healthy render is
  returned verbatim (transparent success path, GP 11).
- `guardWith : IHostRenderTelemetrySink -> (HostRenderFault -> 'Fragment) -> nodeId:string -> (unit -> 'Fragment) -> 'Fragment`
  — the generic core; consumer-supplied fallback factory, generic over the fragment type (SSR string,
  or a `XmlNode` / Feliz path).
- `defaultFallback : HostRenderFault -> string` — the neutral default fallback: a stable, purely
  structural `<div class="toolup-host-render-fallback" data-node-id="…" role="note">…</div>` carrying
  the opaque node id and a fixed degraded-state message, with **no exception text** (which would leak
  internals and diverge run-to-run — the exception detail rides the telemetry sink). Deterministic, so a
  matching CSR fallback mount hydrates parity-clean (Phase 203 `HydrationParity`).
- `FallbackClass` / `FallbackMessage` — the stable style/grep handles.

The boundary is a **neutral utility applied at call sites**, not a compose-registered global — a
pipeline that never wraps a render is byte-for-byte unchanged (GP 11) and pays nothing (GP 13). It
depends only on Core (`HostRenderFault` / `IHostRenderTelemetrySink`), so no tree-language type appears
(GP 1).

## How to adopt (opt-in)

```fsharp
open ToolUp.PublicRendering

// Where a handler renders a hosted-tree fragment server-side, wrap it:
let fragment =
    HostRenderBoundary.guard sink "product-grid"
        (fun () -> MyTreeRuntime.renderFragment tree bindings)   // may throw
// `fragment` is the rendered HTML on success, or the structured fallback on a
// thrown node — the surrounding page always completes.
```

Pass `NoOpHostRenderTelemetrySink()` for the sink to degrade silently; pass a real Phase 268 sink to
make the degraded render observable.

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostRenderBoundary"
```

## Rollback

Delete `Server/HostRenderBoundary.fs` + its `<Compile>` entry, delete
`InProcess/HostRenderBoundaryTests.fs` + its `<Compile>` and `Program.fs` registration. No runtime
impact on any deployment that never wrapped a render.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in SSR resilience utility. No current
matrix consumer server-renders a hosted typed-tree; a deployment that never wraps a render is
byte-for-byte unchanged (GP 11/13).
