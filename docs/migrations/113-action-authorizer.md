# Migration — Phase 113: host-neutral default-deny action authorizer

**Status:** additive, opt-in. A pipeline that uses neither the seam nor an external UI runtime is byte-for-byte unchanged (GP 11) and pays nothing (GP 13). The Phase 46 `IClientToolAuthorizer` (AI-tool gating) is untouched and its contract pack stays green.

## What changes

A generic **default-deny action-authorization seam** for hosts that execute typed UI actions on behalf of a principal (external tree-language runtimes, server-driven live sessions, custom hosts). Generalises the Phase 46 precedent one tier up: keyed on a host-neutral `ActionDescriptor` rather than an AI tool name, async (the shipped default reads `IPermissionStore`), and **deny-by-default** — including when no authorizer is registered at all (`ActionAuthorizer.denyAll` is the absent-seam fallback).

New public surface:

| Symbol | Where | Purpose |
|---|---|---|
| `ActionDescriptor` / `AuthorizationDecision` / `IActionAuthorizer` | `ToolUp.Platform.Core` `Shared/Types/ActionAuthorization.fs` | The seam: `Authorize: ActionDescriptor -> AccessContext -> Async<AuthorizationDecision>` |
| `ActionRequirement` / `ActionRule` / `ActionPolicy` | same file | Policy as data (first matching rule wins; wildcard + prefix matching; empty policy denies everything) |
| `ActionAuthorizer.denyAll` / `allowAll` | same file | Absent-seam fallback / DEV-ONLY allow-all |
| `PermissionStoreActionAuthorizer.create` | `ToolUp.Platform.Server` `Server/Scope/PermissionStoreActionAuthorizer.fs` | The shipped default over `IPermissionStore` + the `IUserClaims` tier provider |
| `IActionAuthorizerContract` | `ToolUp.Platform.Tests` | Conformance pack for second implementations |

## Adopting it

```fsharp
let policy = {
    Rules = [
        { Kind = "dispatch"; Target = "reports/*"
          Requirement = ActionRequirement.Permission("reports", ModulePermission.Write) }
        { Kind = "call"; Target = "premium/*"
          Requirement = ActionRequirement.PremiumTier }
    ]
}

let authorizer = PermissionStoreActionAuthorizer.create policy permissionStore (Some userClaims)
// register: services.AddSingleton<IActionAuthorizer>(authorizer)
// (e.g. via ComposeExtensions.ServiceConfig)
```

Hosts consult it before executing an action; `Deny` carries a reason for the denial audit. Composes with [Phase 110](110-client-host-bridge.md) (client tree-hosting runtimes) and [Phase 112](112-live-session-host.md) (server-driven dispatch).

**Semantics to note (deliberately stricter than `AccessContext.hasPermission`):** an EMPTY effective-permission map **denies** a `Permission` requirement — there is no pre-RBAC "empty = unrestricted" exception on this seam. A `PremiumTier` requirement with no tier provider registered denies (fail closed). A descriptor pinned to a scope other than the principal's own denies regardless of rules (GP 4). A throwing backing store denies (fail closed), never throws.

## Breaking change

None. New types only; no existing interface or behaviour changes.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `ActionAuthorizer (Phase 113)` suite covers policy matching, default-deny on the empty policy, grant→Allow (incl. `implies` widening), tier gating, cross-scope denial, `All` composition, fail-closed storage blips, and the contract binding; the Phase 46 `IClientToolAuthorizer` pack runs unchanged.

## Rollback

Remove the DI registration; hosts fall back to `denyAll` (closed). The Core types are inert when unused.
