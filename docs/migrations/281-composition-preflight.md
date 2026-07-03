# Phase 281 — Composition well-formedness preflight

**Status:** additive. No consumer action required; a pre-281 well-formed app is byte-for-byte unchanged.

## What changes

A first-party `IConfigValidator`, `CompositionValidator.CompositionWellFormednessValidator`
(`ToolUp.Platform.Server/Server/CompositionValidator.fs`), now runs at compose-time preflight over the
Phase 280 `CompositionManifest`. It fails startup (before the app serves traffic) on a **malformed
composition**, with a readable, alternative-enumerating message. Three rules ship:

| Rule code | Severity | Fires when |
|---|---|---|
| `duplicate-component-id` | Error | Two composed units (module / companion slot / datatype / tool) resolve to the same `ComponentId`. |
| `companion-slot-legality` | Error | Two implementations in one multi-impl companion slot share a sub-id (`Name` / `Kind`) — not uniquely addressable. |
| `orphaned-tool-reference` | Error | A tool's declared `SourceModule` names no registered module (reserved `_`-prefixed / `ToolUp.Platform` sources are exempt). |

The validator is registered automatically by `ServerApp.run` (folded into the composition extension
hook), built from the live manifest + the app's tool→module reference edges — so it validates exactly
what was composed and cannot drift from a hand-declared list.

## Behaviour / compatibility

- **GP 11 — additive.** A well-formed composition yields no defects; the validator returns `Ok` and
  logs one `[preflight] composition-well-formedness: Ok` line alongside the existing validator family.
  No composition that was well-formed before 281 fails after it.
- **`SkipPreflight` honoured.** The validator is non-security-class, so the existing
  `ServerConfig.SkipPreflight` emergency-boot lever bypasses it — no new always-on gate a deployment
  cannot switch off.
- **Both boot modes.** Because it registers via the extension `ServiceConfig` hook before the
  `StartupModes` branch, it runs under both `NormalBoot` and the `--validate-config` dry-run.

## Diff per consumer

None. This is an SDK-internal preflight addition. A consumer whose composition trips a rule sees a
`ConfigPreflightFailedException` at startup naming the offending component id + the valid
alternatives; the fix is to disambiguate the id (`ServerModule.withComponentId` for a module; a
distinct `DataType.Id` / `AIToolDefinition.Name` / companion sub-id otherwise) or correct the tool's
`SourceModule`. A deployment that must boot despite the defect can set `SkipPreflight = true`.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `ToolUp.Platform.Tests` — `CompositionValidator` pack: duplicate id / illegal slot / orphaned tool
  each fail preflight with a readable message; a well-formed composition passes; the validator is
  non-security-class.

## Rollback

Remove the `CompositionValidator.serviceRegistration` fold in `ServerApp.run` (the validator is never
registered) — no other surface changes. The `CompositionValidator` module is pure and unreferenced
otherwise.
