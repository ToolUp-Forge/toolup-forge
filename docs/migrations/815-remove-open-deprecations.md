# Migration — Phase 815: the seven open deprecations are removed

**Status.** Removed in `0.23.0`. Every `[<Obsolete>]` notice on the `0.x` public surface named "a future
major" as its removal target; 1.0 is that major, and the decision at the cut was **remove all seven,
carry none** — the `openDeprecations` allowance in `v1-readiness.json` stays at zero and the
scorecard's `open-deprecations` row reads `0 open` by removal, not by allowance. Each notice named its
replacement (the [Phase 258 policy](258-deprecation-lifecycle.md) guarantees that), so this page is the
replacement table, the codemod that applies the mechanical half, and nothing else.

`grep -rn '(obsolete)' api-baselines/` finds nothing on this tree, and the
[Phase 258 renderer](../platform/deprecation-policy.md) is now proven against an in-pack fixture rather
than against a live deprecation, so the next deprecation someone marks is still visible to the gate.

## Per-member replacement

| Removed | Package | Use instead |
|---|---|---|
| `ToolUp.Platform.AgGrid` (the compat re-export module) | `ToolUp.Platform.Client` | `open Feliz.AgGrid` — every forwarded member has the same name there; the package already arrives transitively |
| `ToolUp.Platform.AgGrid.ThemeClass` | `ToolUp.Platform.Client` | the Theming API, below |
| `ToolUp.Platform.AgChart` (the compat re-export module) | `ToolUp.Platform.Client` | `open Feliz.AgCharts` — same members, same names; `ChartPalette.accentColor <- …` still assigns the real slot |
| `Feliz.AgGrid.ThemeClass` (`Alpine` / `AlpineDark` / `Balham` / `BalhamDark` / `Material`) | `Feliz.AgGrid` | `AgGrid.theme Theme.themeAlpine` / `Theme.themeBalham` / `Theme.themeMaterial`; a `*Dark` class is `Theme.themeX \|> Theme.withPart Theme.colorSchemeDark`. Drop the `prop.className` wrapper and the `theme = "legacy"` prop — the Theming API needs no stylesheet import |
| `ToolUp.Elmish.Program.withConsoleTrace` | `ToolUp.Platform.Client` | `Program.withTrace (fun msg model _ -> …)` logging `Program.safeMsgRepr msg` / `model` — the bounded reprs the shim logged. Under the SDK shell, `ClientConfig.EnableElmishConsoleTrace = true` is the same trace through the `client.elmish.trace` category |
| `ToolUp.Elmish.Program.withErrorHandler` | `ToolUp.Platform.Client` | `Program.withErrorReporter (fun ctx -> onError (ctx.Message, ctx.Exception))` — the shim's own body; `ErrorContext` also carries `Phase` / `ModuleId` / `CorrelationId` |
| `ToolUp.Platform.RemotingHelpers.makePermissionGuardedApi` | `ToolUp.Platform.Server` | `ServerModule.create name \|> ServerModule.withGuardedApi factory` (the same module-access gate) plus per-method `[<RequiresRole>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` — [69d-authorization-metadata.md](69d-authorization-metadata.md) |

The `ClerkAuthUI` union case is also marked `[<Obsolete>]` in source but is **not** one of the seven: the
renderer reads attributes on types and members, not on union cases, so the scorecard never counted it
and it is untouched here.

## The codemod

`dotnet run --project Build.fsproj -- Codemod <consumer-dir> [--check]`
([Phase 183](183-consumer-codemod-analyzer-breaking-renames.md)) carries the Phase 815 rules. Rewritten,
because the mapping is 1:1: the two compat `open`s and any qualified `ToolUp.Platform.AgGrid.…` /
`ToolUp.Platform.AgChart.…` reference; `Program.withConsoleTrace`; and `Program.withErrorHandler onError`
in pipeline position with a named callback. Reported for you to reshape, because the target depends on
the site: a `withErrorHandler` with an inline lambda or the program applied on the same line; every
`ThemeClass.*` use (the wrapper `div` goes too); and every `makePermissionGuardedApi` call, which
becomes a `ServerModule` composition rather than a handler.

## Verification

- `dotnet build` a consumer after the codemod; the report names every remaining site.
- The generated [`CHANGELOG.md`](../../CHANGELOG.md) lists each removal under **Removed** in the `0.23.0`
  section, and [`upgrading-to-1.0.md`](upgrading-to-1.0.md) §5 carries the same table.

## Rollback

Pin `ToolUpSdkVersion` at `0.22.x`; the seven members are present and still `[<Obsolete>]` there.

## Version notes

BREAKING — removals of public members. Under the SemVer-on-`0.x` policy this is MINOR-class; it rides
the `0.23.0` slot, which the surface diff since `v0.22.0` already classifies as breaking
(`VerifySemVerBump` proposes exactly `0.23.0`), so no further bump is demanded by this phase.
