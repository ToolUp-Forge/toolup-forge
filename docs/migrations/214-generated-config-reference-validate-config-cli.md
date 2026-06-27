# Phase 214 — Generated config reference + `--print-config` / `--validate-config` CLI (consumer migration)

**What changes.** A central config-key registry (`ConfigKeys.all` in `src/ToolUp.Platform.Server/Server/ConfigKeyDescriptor.fs`) now declares every environment variable the SDK reads at startup — its name, value type, default, one-line description, and whether the resolved value is a secret. From that one list the SDK:

- projects the generated reference doc [`docs/reference/config-reference.md`](../reference/config-reference.md) (regenerable, never hand-maintained — a coverage test fails if it drifts or a dispatch reader consults an undocumented var); and
- backs two **opt-in startup flags** wired at the tail of `SDK.Server.compose`:
  - `--print-config` — resolves every config key and prints its effective value (env value or declared default), **secrets redacted**, then exits `0` without binding a listener. Runs before preflight, so it prints even when the config would fail validation. Answers "why didn't my flag take effect?".
  - `--validate-config` — runs the full `ConfigValidatorAggregator` preflight (the `ComposeConfigValidators` first-party set **plus** every companion `IConfigValidator`), prints the per-validator summary, and exits `0` on success or `1` with the failure summary on any `Error` — **without booting the server**.

**Scope.** Server-side, ops-tooling only. **No `ToolUp.*` library API change**, no wire change, no new required composition-root call. A consumer gets the flags and the reference doc the moment it bumps the SDK; normal boot (neither flag present) runs the prior `compose` sequence byte-for-byte (GP 13).

## Consumer action — none required

There is nothing to adopt in consumer code. The flags are available to any composition root that ends in `ServerApp.run` (or `AIServerApp.run` / `RAGServerApp.run` — they all funnel through `SDK.Server.compose`). To use them:

```bash
# Print the effective resolved config (secrets redacted), no server boot:
dotnet run --project src/MyApp.Server -- --print-config

# Run the startup preflight and exit non-zero on a bad config, no server boot:
dotnet run --project src/MyApp.Server -- --validate-config && echo "config OK"
```

`--validate-config` is well-suited to a CI / pre-deploy gate: it exercises the same validator set that would abort a real boot, but exits cleanly with `0` / `1` and a readable summary instead of crashing a half-started server.

If a consumer's own composition root already parses CLI args, the two flags pass straight through `detect` as `NormalBoot` unless they exactly match — there is no collision with an app's own flag set.

## Regenerating the reference doc (SDK contributors only)

`docs/reference/config-reference.md` is generated. After adding or changing a `ConfigKeyDescriptor`, regenerate it:

```powershell
pwsh ./dev-scripts/generate-config-reference.ps1
```

(or `TOOLUP_REGEN_CONFIG_REFERENCE=1 dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list ConfigReference`). The `ConfigReference` test pack in `ToolUp.Platform.Tests` fails until the committed doc matches the registry.

## Verification

1. `dotnet run --project src/MyApp.Server -- --validate-config` on a healthy config prints the validator summary and exits `0`; the HTTP server never binds.
2. Force a bad config (e.g. `TOOLUP_BLOB_STORAGE=azure` with no Azure credentials → the cloud backend falls back to local and `blob-storage-selection` returns `Error`): `--validate-config` prints the failure summary and exits `1`, again without booting.
3. `--print-config` shows each key's effective value; a set secret (e.g. `TOOLUP_ADMIN_TOKEN`) renders as `<redacted>`, a set non-secret shows its value, an unset key shows its `(default)` or `(unset)`.
4. A normal boot with neither flag is unchanged.
5. Test packs in `ToolUp.Platform.Tests`: `ConfigReference` (registry coverage + reference-doc golden) and `ConfigStartupMode` (flag detection, redaction, validate-config exit contract).

## Rollback

Remove the two flags from the invocation — there is nothing else to roll back, since absent flags leave boot byte-for-byte unchanged. To revert the SDK feature entirely, revert forge commits `524b5a3` (registry + doc) and `553d526` (startup modes). No data migration.
