# Migration — Phase 138: OAuth credential-at-rest encryption refusal

**Status:** new fail-closed startup preflight (security-class `IConfigValidator`). A deployment with no OAuth connector flow, an Anonymous deployment, or one already on an encrypting / KMS-backed `ISecretStore` is byte-for-byte unchanged. A deployment that registers an `IOAuthCredentialFlow` against a **plaintext** secret store in a non-Anonymous mode now **refuses to start** instead of silently persisting refresh tokens in cleartext.

## What changes

The connector OAuth refresh token (the long-lived credential the whole flow exists to obtain) and the cached access token are written through `ISecretStore.SetSecret`, but nothing in the OAuth path *required* the encrypting decorator. The SDK already enforced the analogous control for the general secret store (`EncryptedSecretStoreModeValidator`); this extends that fail-closed posture to the specific, higher-value case of stored OAuth credentials.

New `OAuthSecretEncryptionModeValidator` (security-class, on the `SkipPreflight`-proof allowlist): when an `IOAuthCredentialFlow` is registered AND the deployment is non-Anonymous AND the composed `ISecretStore` is not the encrypting wrapper (and no cloud-KMS carve-out applies), it emits `Error` naming the cleartext-credentials exposure and the fix. It reuses the same encrypting-store / KMS detection predicate as `EncryptedSecretStoreModeValidator` (`EncryptedSecretStore.ProvidesEncryptionAtRest`) so the two validators can't drift on what "encrypting" means.

## Consumer action

A deployment running connector OAuth in a non-Anonymous mode must compose one of:

- the **encryption-at-rest decorator** over its `ISecretStore` (`EncryptedSecretStore` with a configured master key), or
- a **cloud-KMS-backed `ISecretStore`** (which provides encryption at rest natively — recognised by the shared carve-out).

A deployment already on either is unaffected. A deployment with no OAuth connector flow is unaffected. If startup now refuses, the message names the missing decorator — wire it and restart.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — `OAuthSubstrateTests.fs` / `OAuthSecretEncryptionModeValidatorTests.fs`: a plaintext store + registered OAuth flow + non-Anonymous mode refuses startup; the same with the encrypting decorator (or a KMS-backed store) starts clean; an Anonymous deployment or one with no OAuth flow does not fire; the validator survives `SkipPreflight`.

## Rollback

Remove `OAuthSecretEncryptionModeValidator` from the validator registration and the security-class allowlist. OAuth credential persistence returns to being permitted against a plaintext store with no preflight — refresh tokens may then sit in cleartext at rest, so roll back only with that exposure understood.
