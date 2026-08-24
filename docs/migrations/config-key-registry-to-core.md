# Migration — the `ConfigKeys` registry moves to `ToolUp.Platform.Core`

**What changes.** The central config-key registry — `ConfigKeys.Names`, `ConfigKeys.all`,
the `ConfigKeyDescriptor` / `ConfigKeyType` types, and `ConfigKeys.ReferenceDoc` — moves from
`ToolUp.Platform.Server` (`Server/ConfigKeyDescriptor.fs`) to `ToolUp.Platform.Core`
(`Shared/Types/ConfigKeyDescriptor.fs`). It merges into the `ToolUp.Platform.ConfigKeys` module
that Core already declared for the reserved config-subsystem identifiers (`PlatformModuleKey`,
`BrandingKeys`, `NotificationPrefsKeys`), which previously lived in `ConfigTypes.fs`.

**Every qualified name is unchanged.** `ConfigKeys.Names.authMode`, `ConfigKeys.all`,
`ConfigKeys.PlatformModuleKey` all still resolve exactly as before. What moved is the *assembly*.

**Why.** The registry's own contract is that a `*FromEnv` reader cites `Names.*` rather than
inlining a string literal, so a renamed variable is a compile error instead of silent drift from
the generated reference. That mechanism was structurally unavailable to the largest reader in the
SDK: `ServerConfig.fromEnv` lives in `Platform.Core`, and Core takes no dependency on Server. The
result was predictable in hindsight — `fromEnv` read 87 environment variables, of which **72 had
no descriptor at all**, so `--print-config` silently omitted them and
`docs/reference/config-reference.md` did not document them. Moving the registry down a tier makes
the intended mechanism reachable from every reader in the SDK.

## Do I need to change anything?

**Almost certainly not.** `ToolUp.Platform.Server` references `ToolUp.Platform.Core`, so any
consumer that already compiled against `ConfigKeys.*` still resolves it with no edit.

Two cases need action:

1. **You reference SDK assemblies directly rather than by package.** Add a reference to
   `ToolUp.Platform.Core` where you previously relied on `ToolUp.Platform.Server` alone to supply
   `ConfigKeys`.
2. **You reflect over the registry by assembly.** A lookup keyed on
   `typeof<...>.Assembly.GetName().Name = "ToolUp.Platform.Server"` now finds nothing; the type
   lives in `ToolUp.Platform.Core`. Prefer `typeof<ConfigKeys.ConfigKeyDescriptor>.Assembly`.

## New coverage — 140 descriptors added

The registry grew from 40 descriptors to **180**: every `TOOLUP_*` string literal in shipped
(non-test) source now carries one. That includes the 72 composition-shaping variables read by
`fromEnv` — among them ten `TOOLUP_ACCEPT_*` preflight escape hatches whose siblings were already
documented — plus 68 read by companions (LDAP, Entra, GitHub auth, SMTP / SendGrid / Twilio, the
cloud storage and secret-store companions) and a small "Build & tooling" group read by the build,
the analyzer and the benchmarks rather than at startup.

Consequences, all additive:

- `--print-config` now prints every one of them, redacting the two flagged `IsSecret`
  (`TOOLUP_SMTP_PASSWORD`, `TOOLUP_GCS_CREDENTIALS_JSON`) alongside the existing secrets.
- `docs/reference/config-reference.md` documents all 180 across 12 category sections.

No behaviour changed: every variable was already read exactly as before. This is a documentation
and introspection fix, not a correctness one.

## The gate that let it drift

`ConfigReferenceTests` asserted that "every env var read by the `*FromEnv` dispatch readers has a
descriptor" — but scanned a hard-coded list of four reader files. `SDK.Shared.fs` was not among
them, so the check could not see the largest reader in the codebase and reported clean while 72
variables were undocumented. Enumerating readers is precisely the thing that drifts.

The gate now quantifies over the tree instead, in two arms:

- **Arm 1** scans every non-test `.fs` under `src/` and requires a descriptor for each `TOOLUP_*`
  **string literal**. Scanning string literals rather than raw text is load-bearing: a raw-text
  scan would demand descriptors for prose mentions, including the glob `TOOLUP_MODULE_BINDING_*`
  and `TOOLUP_PLATFORM_MODE`, retired in Phase 66 and read nowhere.
- **Arm 2** requires every `ConfigKeys.Names` binding to have an entry in `all`. Arm 1 stops
  seeing a variable the moment a reader switches to citing `Names.*` — which is what the registry
  asks readers to do — so without arm 2 the fix would have reopened the same hole from the other
  side.

Both arms carry an explicit vacuity guard, because the original failure mode was a check that
passed by looking at nothing.

## Verification

```bash
dotnet build ToolUp.Forge.sln
dotnet run --project Build.fsproj -- VerifyAll
```

To regenerate the reference doc after adding a descriptor:

```bash
pwsh dev-scripts/generate-config-reference.ps1
```

## Rollback

Revert the commit. The registry returns to `ToolUp.Platform.Server`, the descriptor set returns to
40, and the coverage gate returns to its four-file list. Nothing else in the SDK reads the moved
types, so the revert is self-contained.
