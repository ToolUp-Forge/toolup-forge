# Phase 73 — `Fable.Remoting.*` → `ToolUp.Remoting.*` + `Elmish` → `ToolUp.Elmish`

**Released in:** the SDK release that ships Phase 73 (post 0.4.4 / pre v0.1.0 public flip).

## Upstream acknowledgement

ToolUp.Remoting is a fork of [Zaid Ajaj's Fable.Remoting](https://github.com/Zaid-Ajaj/Fable.Remoting) (MIT). ToolUp.Elmish is a fork of [Eugene Tolmachev's Fable.Elmish](https://github.com/elmish/elmish) (Apache 2.0). This migration doc renames the namespaces in your consumer code — the underlying transports and runtimes still carry the years of careful work those upstream projects built.

**If your use case fits upstream unmodified, you don't need this migration**: use the upstream packages directly. Fable.Remoting remains a well-maintained, broadly adopted RPC transport for F# full-stack apps; Fable.Elmish remains the canonical F# MVU runtime. ToolUp's variants exist because the ToolUp use case grew specific seams that didn't exist upstream (Remoting: per-request `CallContext`, structured error envelopes, `RateLimit`/`Idempotency`/`Audit`/`Validation` attributes, `JobHandle`, schema-versioned wire envelopes; Elmish: `IDispatcher`, `Prefetch`, lifetime-aware `EffectHandle`, structured `ErrorContext`, `Cmd.OfRemoting`) and specific subtractions ToolUp consumers never used (`Cmd.OfFunc` / `OfPromise` / `OfTask` / `OfValueTask` / `OfAsyncWith` / `OfAsyncImmediate` / WebSharper paths / `cmd.obsolete.fs` v3.x shims). The rename makes the divergence explicit so contributors don't expect upstream behaviour from a fork that's drifted; it isn't a repudiation of either upstream project.

Full appreciation lives in [`NOTICE.md`](../../NOTICE.md) — each upstream entry carries a substantive credit paragraph alongside the licence + copyright.

## What changes

Two namespace renames flip in lockstep on the forge side (Phase 73):

| Before | After |
|---|---|
| `namespace Fable.Remoting.*` (~27 fork-source files) | `namespace ToolUp.Remoting.*` (already in place from the 0.4.4 fold — Phase 73 closes the residual narrative references) |
| `namespace Elmish` / `Elmish.HMR` / `Elmish.React` (11 runtime files) | `namespace ToolUp.Elmish` / `ToolUp.Elmish.HMR` / `ToolUp.Elmish.React` |
| `open Fable.Remoting.Server` / `Json` / `Client` / `Giraffe` / `MsgPack` | `open ToolUp.Remoting.Server` / `Json` / `Client` / `Giraffe` / `MsgPack` |
| `open Elmish` / `Elmish.React` / `Elmish.HMR` | `open ToolUp.Elmish` / `ToolUp.Elmish.React` / `ToolUp.Elmish.HMR` |
| Fully-qualified `Fable.Remoting.Server.RouteInfo<HttpContext>` etc. | `ToolUp.Remoting.Server.RouteInfo<HttpContext>` |

The underlying behaviour, wire shape, JSON converter, MVU loop, `Cmd` / `Sub` / `Dispatch` API, `Program<>` lifecycle — none change. This is a name-only flip; consumer business logic is unaffected.

The companion package IDs (`ToolUp.Platform.Core` / `Client` / `Server` / etc.) are unchanged. The transport and runtime still ship folded into `ToolUp.Platform.*` per the 0.4.3 / 0.4.4 folds.

## Diff to apply (per consumer)

Search-and-replace across your consumer `.fs` files. Each pattern below is safe to run in sequence; the `Fable.Remoting` patterns are independent of the `Elmish` ones.

### Remoting

```diff
-open Fable.Remoting.Server
+open ToolUp.Remoting.Server

-open Fable.Remoting.Json
+open ToolUp.Remoting.Json

-open Fable.Remoting.Giraffe
+open ToolUp.Remoting.Giraffe

-open Fable.Remoting.Client
+open ToolUp.Remoting.Client

-open Fable.Remoting.MsgPack
+open ToolUp.Remoting.MsgPack
```

Fully-qualified type references (less common; usually only in adapter / wiring code):

```diff
-Fable.Remoting.Server.RouteInfo<HttpContext>
+ToolUp.Remoting.Server.RouteInfo<HttpContext>

-Fable.Remoting.Json.FableJsonConverter()
+ToolUp.Remoting.Json.FableJsonConverter()
```

> **Note.** The `FableJsonConverter` itself is marked `[<Obsolete>]` and a follow-up migration to `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create()` is tracked separately in TIDY-UP — that migration produces byte-equal wire output via System.Text.Json with no Newtonsoft.Json transitive dep. Phase 73 doesn't gate that follow-up; rename the type now, drop Newtonsoft when convenient.

### Elmish

```diff
-open Elmish
+open ToolUp.Elmish

-open Elmish.React
+open ToolUp.Elmish.React

-open Elmish.HMR
+open ToolUp.Elmish.HMR
```

If any fully-qualified `Elmish.Program.X` / `Elmish.React.Program.X` calls exist (rare — usually only inside the runtime itself), flip to `ToolUp.Elmish.Program.X` / `ToolUp.Elmish.React.Program.X`.

### Configuration / `.fsproj` files

No `PackageReference` changes are required — the transports and runtimes ship folded into `ToolUp.Platform.{Core,Client,Server}`. The companion package IDs do not change in Phase 73.

If a `.fsproj` comment references `Fable.Remoting` or `Elmish` as a substrate name, update the comment to reflect `ToolUp.Remoting` / `ToolUp.Elmish` — this is cosmetic and can ride on the next touch of the file.

## Reframing rationale

The two renames earn the rename through different paths, but the operating principle is the same: **we kept the parts that work for our use case, modified them where the ToolUp use case diverged, and remain genuinely grateful for the upstream work we built on.**

- **`Fable.Remoting` → `ToolUp.Remoting`** is justified by **additions and divergence**. Phases 69b–69k added seams (`CallContext`, error envelope, telemetry, correlation-id, AsyncSeq streaming, authorization metadata, typed validation, idempotency keys, per-method rate-limit, per-method audit, JobHandle, schema-versioned envelopes, source-generator dispatch) that don't exist upstream. The substrate now has its own surface and operational concerns. Continuing to call it Fable.Remoting misleads contributors who arrive expecting upstream behaviour and find a different beast. Zaid Ajaj's wire model and converter heritage remain the substrate; ToolUp.Remoting is what we built on top.
- **`Elmish` → `ToolUp.Elmish`** is justified by **opinionated subtractions**. The Elm Architecture core (`Program` / `Cmd` / `Sub` / `Dispatch` / `init` / `update` / `view`) is preserved bit-for-bit — Eugene Tolmachev's design and 8+ years of community polish, kept verbatim. What changed is the surface area: `Cmd.OfFunc` / `OfPromise` / `OfTask` / `OfValueTask` / `OfAsyncWith` / `OfAsyncImmediate` / WebSharper paths / `cmd.obsolete.fs` v3.x shims are gone (zero observed call sites in the ToolUp consumer base), and ToolUp-specific primitives (`IDispatcher`, `Prefetch`, `EffectHandle`, structured `ErrorContext`, `Cmd.OfRemoting`) replace the void. Upstream Elmish remains the F# MVU standard and the right choice for the broad community it serves.

## Verification steps

After applying the rename across your consumer files:

1. **`dotnet build`** — clean (0 errors). If your build fails with `The value, namespace, type or module 'Elmish' is not defined`, you missed an `open Elmish` / `open Elmish.React` / `open Elmish.HMR` somewhere.
2. **`dotnet fable -o output --noCache`** clean for every client project. Same diagnostic if you missed an `open` — Fable will flag the unresolved namespace exactly like .NET does.
3. **Run your test suite.** Behaviour is byte-identical (this is a name-only rename), so any test failure is most likely a leftover `open Fable.Remoting` / `open Elmish` your search-and-replace missed.
4. **Smoke-test the running app.** ToolUp.Remoting RPC round-trips work unchanged. Elmish MVU dispatch / view / `Cmd` / SSE-via-`Cmd.OfRemoting` works unchanged.

## Rollback

The rename is a one-shot find-replace + recompile. To revert:

1. Revert the consumer-side rename commit(s).
2. Pin your `ToolUp.Platform.*` `PackageReference` to a version BEFORE the Phase 73 ship (the version that ships Phase 73 announces the rename in its release notes — typically the first `0.X.0` post-Phase-73).

ToolUp **does not ship transitional `namespace Fable.Remoting.X = namespace ToolUp.Remoting.X` aliases** — the rename is meant to be visible. If you need to roll back, roll back the PackageReference and the consumer-side opens together.

## Consumer adoption tracking

This phase is shipped on the forge side and consumer adoption rolls through the workspace [`SDK-ADOPTION.md`](../../../SDK-ADOPTION.md) matrix:

| Consumer | Status |
|---|---|
| `toolup-app` | 🟡 pending |
| Concord (Seller) | 🟡 pending |
| Concord (Buyer) | 🟡 pending |
| Xcelsys/portal | 🟡 pending |
| TaxTimeMachine | 🟡 pending |
| `cookbook-apps/` (recipe sweep) | 🟡 pending |

Honest per-consumer file counts (sampled 2026-06-02; may have drifted — re-grep at adoption time):

- **`toolup-app/src`**: ~44 files match `^(open|namespace) Elmish` — mostly each module's `ClientModel.fs` + composition-root files.
- **Concord (Seller + Buyer combined)**: ~52 files.
- **Xcelsys/portal**: ~10–20 files (smaller scope).
- **TaxTimeMachine**: ~5–10 files.
- **cookbook-apps/**: ~50–100 files aggregate across ~10 cooks.

Per-consumer Fable.Remoting counts vary; the pattern is "every server-side handler file + every client-side API-binding module."

**Per-consumer effort estimate**: ~30 minutes of mechanical find-replace + the consumer's own `dotnet build` + smoke-test cycle. Cells in `SDK-ADOPTION.md` flip from 🟡 → ✅ as adoption PRs land. None gate Phase 73's forge close-out.

## See also

- [`NOTICE.md`](../../NOTICE.md) — the canonical upstream-credit landing page.
- [`README.md`](../../README.md) "In-tree client + transport forks" section — narrative summary of what ToolUp.Remoting + ToolUp.Elmish add over upstream.
- Upstream [Fable.Remoting](https://github.com/Zaid-Ajaj/Fable.Remoting) and [Fable.Elmish](https://github.com/elmish/elmish) — the right choice if your use case fits upstream unmodified.
