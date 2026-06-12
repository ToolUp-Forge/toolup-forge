# Phase 121 — Client boot-degradation surface (consumer migration)

**What changes.** The client shell's boot loaders (`GetMyTeams`, `GetActiveTeam`, accessible-modules, configs, flags, platform-role, sole-team auto-select) no longer swallow failures into benign-looking defaults. A failed load still renders with the same default (empty teams, no active team, permissive sidebar, empty config/flag maps) — but the shell now records a typed `BootDegradation` entry and renders one standard, dismissible "Some data failed to load" banner with per-source retry. Companion fix: `UserSession`'s auth-bridge refresh loop counts consecutive `GetJwt` failures and surfaces a persistent failure ("Session refresh is failing — you may be signed out shortly") through `AuthDiagnostics`, the `client.auth.bridge` category logger, and the same banner; re-installing a bridge no longer leaks the previous refresh interval.

**Scope.** `ToolUp.Platform.Client` only. No server change, no wire change.

**Backward compatibility.** Additive — no consumer code must change.

- All rendering defaults on failure are byte-identical to pre-121; only the banner is new, and it renders nothing when no load failed (GP 13).
- A failed `GetActiveTeam` no longer counts as "load completed", so the sole-team auto-select cannot fire off failed data (pre-121 it couldn't fire either, because the swallowed failure resolved to `None` *with* the completed flag — the new behaviour is strictly more conservative only in the failure case).
- New public surface: `BootDegradation` module (type + `add`/`remove` + `banner`), shell `Msg` cases (`BootLoadFailed` / `RetryBootLoad` / `BootLoadRecovered` / `DismissBootDegradations`), `Model.Degradations`, `UserSession.onBridgeHealthChange`, `UserSession.uninstallBridge`.
- Consumers pattern-matching the shell `Msg` or constructing `Client.Model` literals (atypical — custom composition roots) must add the new cases/field.

## Diff to apply

Nothing for stock consumers — upgrade the `ToolUp.Platform.Client` package and the banner lights up on the next boot-load failure.

## New observability

| Signal | Where | Meaning |
|---|---|---|
| `[BootDegradation] <source> load FAILED` | `client.bootstrap` category logger | A boot loader threw; the shell rendered with the default and recorded a banner entry. |
| Degradation banner | shell UI (top of viewport) | One row per failed source with Retry; dismissible; clears automatically when a load succeeds. |
| `bridge-refresh-failing` / `bridge-refresh-recovered` | client `AuthDiagnostics` | Auth bridge failed 3 consecutive refreshes / recovered. |
| `client.auth.bridge` warn | category logger | Same threshold event with remediation hints (provider SDK config, session validity, CSP). |

## Verification

1. `dotnet build src/ToolUp.Platform.Client/ToolUp.Platform.Client.fsproj` + `cd samples/MinimalClient && dotnet fable -o output` — green.
2. Client pack: `cd src/ToolUp.AI.Client.Tests && dotnet fable -o output && node --import ./register-loader.mjs --test output/Program.js` — includes the Phase 121 suite (accumulator dedup, update arms, prefetch-gate flip on failed configs, empty ≠ failed, bridge threshold/recovery/interval sentinel).
3. Manual: stop the server, reload a team-scoped deployment → shell renders with the banner naming the failed loads; restart the server and click Retry → teams restore without a reload. A genuinely teamless user sees no banner.

## Rollback

Revert forge commit `f6262c3`. No data migration.
