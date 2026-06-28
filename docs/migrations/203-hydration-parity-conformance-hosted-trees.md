# Phase 203 — Hydration-parity conformance harness for hosted trees

**What changes.** A new test-only helper — `HydrationParity` in `ToolUp.Platform.Testing` — that
asserts a host-neutral typed-tree renders identically on the server (an SSR HTML fragment) and on the
client (the React mount). It normalises both HTML strings to a canonical token stream and diffs them
node-by-node, naming the first divergent node. The silent class it targets — divergent attribute
order, boolean/void-element form, adjacent text-node boundaries, insignificant whitespace, and
event-handler attributes — otherwise surfaces only as a React hydration-mismatch warning in the
browser console (and a quietly-voided SEO first paint), the same failure class
`PrerenderDeterminismTests` documents for the Phase 57 prerender path.

**Scope.** Purely additive, test/build infrastructure (GP 11/13). BCL-only, so it packs under
`fable/` and runs under both .NET and Fable; zero runtime surface; **byte-for-byte absent from any
consumer build that never references it**. No required migration — there is nothing to adopt at
runtime. A deployment wires the check into its own CI only if it hosts a typed-tree and wants the
parity gate.

## What it does (and what it deliberately does not)

The harness operates on two HTML **strings**: the SSR fragment (the Phase 111
server-rendered-fragment path) and the CSR mount (the Phase 110 `ClientHostBridge` / `withElementView`
output, captured as `outerHTML` after React mounts). It does **not** run React — the actual React
hydration-mismatch warning is browser-side and is verified in the consumer's DevTools console per the
phase acceptance, exactly as Phase 57's determinism pack splits its concern (the byte-stable F# pieces
are pinned in-process; the React behaviour is browser-verified).

`HydrationParity.normalise` canonicalises a fragment:

| Divergence class | Normalisation |
|---|---|
| Attribute ordering | attributes sorted by name |
| Boolean attributes | `checked` / `checked=""` / `checked="checked"` → valueless `checked` |
| Void elements | `<br/>` / `<br />` → `<br>` (no end tag) |
| Self-closing non-void | `<div/>` → `<div></div>` |
| Adjacent text nodes | React `<!-- -->` separators dropped; adjacent runs coalesced |
| Whitespace | layout whitespace collapsed; preserved verbatim inside `pre` / `textarea` / `script` / `style` |
| Event handlers | `on*` attributes and React's `data-reactroot` marker stripped |

`HydrationParity.check ssr csr` returns `Parity` or `Divergence msg`, where `msg` cites the node index
and describes both sides.

## Using it in CI

```fsharp
open ToolUp.Platform.Testing

// ssrFragment: from the Phase 111 IResolvedContentSource / fragment path.
// csrOuterHtml: captured from the browser after the ClientHostBridge mount.
match HydrationParity.check ssrFragment csrOuterHtml with
| HydrationParity.Parity -> ()
| HydrationParity.Divergence msg -> failwith msg
```

The shipped `HydrationParity.divergenceClassFixtures` (one fixture per class above, each modelling the
two renderers' real emission quirks) and `mismatchedFixture` are the worked examples; the
`HydrationParityTests` pack in `ToolUp.Platform.Tests` exercises them under `Build.fsproj --
VerifyAll` (the `Platform` pack).

## Adopting

Nothing to adopt. The helper ships inside `ToolUp.Platform.Testing`; reference it from a test/CI
project only if you host a typed-tree and want the parity gate. A consumer that never references it is
byte-for-byte unchanged.
