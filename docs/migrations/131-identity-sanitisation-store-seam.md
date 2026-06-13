# Phase 131 — Identity sanitisation at the store seam (consumer migration)

**What changes.** The `IdentitySanitiser` guarantee is now enforced structurally at the `ITeamStore`, `IPermissionStore`, **and** `IShareTokenStore` parameter seams, not only on the caller's own principal id. Every caller-supplied id that becomes a blob-key segment — `teamId` / `userId` (team + permission stores) and `scopeId` / `tokenId` (share-token store) — is validated before any blob write or read. A traversal-shaped id (`../`, `\`, NUL / control chars, whitespace), the reserved `_platform` scope, or a Windows reserved device name is **rejected** instead of being interpolated into a chosen path.

- Team / permission **write** seam shipped earlier (forge `7b1bc88`) via `SanitisingTeamStore` / `SanitisingPermissionStore`.
- This phase adds `SanitisingShareTokenStore` (the share-token resource/scope seam) and read-seam defence-in-depth on the share-token `List*` methods.

**Scope.** Server-side only; no wire change. The decorators are wired unconditionally by the SDK composition root (`ComposeTeamRuntime` for team/permission, `ComposeNotifications` for the share-token store), including around a consumer-supplied store and any `withShareTokenStoreDecorator` chain. SDK-mounted routes need nothing from you.

**Behavioural change.** Ids that the stores *previously accepted* because they happened to contain odd characters now return an `Error` (`StorageFailed` for the share-token mutating methods; the existing `Error string` for team/permission). The share-token `List*` reads degrade to an **empty** result on a traversal `scopeId` (they can no longer enumerate a chosen `_platform/share-tokens/...` prefix).

**Who is affected.** Only consumers whose team / user / scope id scheme uses characters **outside** the sanitiser allowlist (`A–Z a–z 0–9 - _ .`, length 1–256, no leading `.`). Guid-shaped ids, OIDC `sub` claims, and email-shaped subjects are unaffected — existing deployments are byte-for-byte identical.

## Diff to apply

Nothing for the common case. If your deployment mints non-Guid team / user / share-token scope ids, audit the charset against the allowlist:

```fsharp
// Allowed id charset (ToolUp.Platform.Auth.IdentitySanitiser):
//   alphanumerics, '-', '_', '.'  — length 1..256, no leading '.',
//   not a Windows reserved device name (CON/PRN/AUX/NUL/COM1.../LPT1...),
//   not the reserved scope "_platform".
```

A scheme using `/`, `:`, spaces, or other separators must be remapped to an allowlisted form (e.g. percent-/base32-encode the foreign characters) before the id reaches a store method.

**Membership rows are admin-asserted.** `AddTeamMember` / `CreateTeamWithOwner` sanitise `memberId` / `ownerId` but do **not** verify that the id resolves to a provisioned principal — there is no `IUserDirectory` lookup in the default composition. Treat `GetTeamMembers` output as "who an admin asserted belongs to this team", not as verified identity. A deployment needing existence-proof wires its own check at the `ITeamStore` seam.

## Verification

- `dotnet build` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — the `Phase 131 — store-seam id sanitisation` list passes, including the `StoreIdSanitising contract` pack bound against the in-memory **and** local-file blob backends.
- Manual: `AddTeamMember("t", "../../_platform/permissions/t", Owner)` and `Issue { ScopeId = "../../_platform"; ... }` both return `Error` before any blob is written.

## Rollback

Remove the `SanitisingShareTokenStore` wrap in `ComposeNotifications` (the team/permission seam from `7b1bc88` is independent). Reverts to the prior behaviour where odd-character share-token scope/token ids reach the blob-key builder.
