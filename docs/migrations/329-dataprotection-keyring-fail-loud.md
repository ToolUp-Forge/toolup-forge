# Fail-loud DataProtection key-ring backend

**Status:** additive. A deployment with a **healthy** key-ring backend is behaviour-identical after
upgrading. A deployment whose backend is misconfigured / unreachable — which previously booted green
with a silent ephemeral key — now fails preflight with an actionable message. That is the fix, not a
regression.

## What changes

The DataProtection key ring persists through `BlobXmlRepository` under `_platform/dataprotection/`.
Before this change, `GetAllElements` swallowed every backend failure (`try … with _ -> []` / `()`),
so a misconfigured `_platform` container booted with an **empty key ring**: DataProtection minted a
fresh ephemeral key, and the fault surfaced much later as an unexplained 403
`csrf_validation_failed` storm with no log line pointing at the blob store. Two additions close it:

1. **`Warn` on key-ring read failure.** Both `GetAllElements` failure paths (the key-ring `List` and
   each per-key `Download`) now log a `Warn` naming the container, the key-ring prefix, and the
   underlying error — so an empty result caused by a read failure is distinguishable from a
   genuinely-empty first-boot ring (which stays silent).
2. **`dataprotection-keyring-backend` preflight validator.** A new security-class `IConfigValidator`
   (`DataProtectionBackendValidator`, registered automatically in `compose`) probes the key-ring
   prefix at startup: a `List` over `_platform/dataprotection/` (the exact read `GetAllElements`
   performs) plus a sentinel write / readback / best-effort delete. A misconfigured / unreachable /
   write-denied backend fails preflight naming the container, prefix, and underlying error — the app
   does not boot with a silent ephemeral key. An empty prefix passes (a first-boot ring is
   legitimately empty). Security-class per the [327 marker](327-security-class-validator-property.md):
   `SkipPreflight = true` cannot bypass it (an ephemeral-key boot is a cross-instance-auth-state hole).

Surface additions (all additive): `BlobDpKeyRing.Container` / `BlobDpKeyRing.Prefix` are now public
(shared single-source-of-truth constants), `BlobXmlRepository` gains an optional `?logger: ILogger`
constructor parameter, and `ComposeRuntimeServices.registerCachingAndDataProtection` takes the
resolved logger (composed for you inside `compose`).

## Diff per consumer

**None required.** The validator registers automatically in `compose`; the `Warn` logging rides the
already-resolved `ILogger`. A consumer constructing `BlobXmlRepository` directly (unusual) keeps
compiling — the new constructor parameter is optional; pass the resolved `ILogger` to get the
read-failure diagnostic:

```fsharp
BlobXmlRepository(resolvedBlobStorage, resolvedLogger)
```

A consumer calling `ComposeRuntimeServices.registerCachingAndDataProtection` directly (it is a
`compose` internal extracted for per-concern subdivision) adds the logger argument:

```fsharp
registerCachingAndDataProtection services resolvedBlobStorage resolvedLogger
```

## Behaviour / compatibility

- **GP 11 — healthy backends are byte-for-byte unchanged**: the probe passes silently, no new log
  lines, no behaviour difference.
- **A broken backend now refuses to boot.** If your deployment relied on booting through a dead
  key-ring backend (accepting per-boot ephemeral keys — e.g. an ephemeral single-instance dev box
  with a deliberately absent `_platform` container), the preflight `Error` names the fix. There is
  deliberately no bypass via `SkipPreflight`; fix the blob-storage configuration instead.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `ToolUp.Platform.Tests` — `Phase 329 — DataProtection key-ring backend fail-loud` pack: unreachable
  and write-denied backends fail preflight naming container / prefix / underlying error;
  `SkipPreflight = true` still runs the probe; a healthy backend (empty or populated) passes and
  leaves no sentinel; a key-ring read failure emits exactly one `Warn` while a first-boot empty ring
  emits none; the healthy `StoreElement` → `GetAllElements` round-trip is unchanged.

## Rollback

Remove the `DataProtectionBackendValidator` registration line in
`ComposeConfigValidators.registerFirstPartyConfigValidators`. The `Warn` logging in
`BlobXmlRepository` is inert observability and can stay.
