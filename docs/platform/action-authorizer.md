# Default-deny action authorization — `IActionAuthorizer`

Hosts that execute **typed UI actions** on behalf of a principal — an external tree-language runtime ([client host bridge](client-host-bridge.md)), a server-driven [live session](live-sessions.md), any custom surface — need to ask one question before executing: *is this principal allowed to perform this action in this scope?* `IActionAuthorizer` is the one seam that question routes through, **default-deny** when no rule matches.

It generalises the Phase 46 `IClientToolAuthorizer` precedent (which stays in place gating AI-drivable client tools) one tier up: host-neutral descriptor, async (the shipped default reads the permission store), scope-aware by construction.

```fsharp
type ActionDescriptor = { Kind: string; Target: string; Scope: string option }

type IActionAuthorizer =
    abstract Authorize: action: ActionDescriptor -> ctx: AccessContext -> Async<AuthorizationDecision>
    // AuthorizationDecision.Allow | Deny of reason
```

`Kind` partitions the action space with a host-defined vocabulary (`"dispatch"`, `"call"`, `"navigate"`, `"ai-tool"`, …); `Target` names the action within the kind; `Scope`, when supplied, pins the action to a scope container — the shipped default denies a descriptor pinned outside the principal's own scope before any rule is consulted (GP 4).

## Policy as data

```fsharp skip=fragment
let policy = {
    Rules = [
        // first matching rule wins; "*" wildcard; trailing "*" prefix-matches
        { Kind = "dispatch"; Target = "reports/*"
          Requirement = ActionRequirement.Permission("reports", ModulePermission.Write) }
        { Kind = "call"; Target = "premium/*"
          Requirement = ActionRequirement.PremiumTier }
        { Kind = "navigate"; Target = "*"
          Requirement = ActionRequirement.Unrestricted }
    ]
}

let authorizer = PermissionStoreActionAuthorizer.create policy permissionStore (Some userClaims)
```

`PermissionStoreActionAuthorizer` resolves `Permission` requirements against `IPermissionStore` per call for a `TeamMember` (a rotated grant takes effect on the next action) and against the request's resolved `AccessContext.ModulePermissions` for other subject kinds; `PremiumTier` resolves via the `IUserClaims` tier provider. `ActionRequirement.All` composes requirements; `Unrestricted` grants (use sparingly — it is the explicit opt-out, not the default).

## The default-deny posture (read this twice)

- **No matching rule → `Deny`.** An empty policy denies everything.
- **Empty effective permissions → `Deny`** for a `Permission` requirement. This is deliberately stricter than `AccessContext.hasPermission`'s pre-RBAC "empty = unrestricted" convenience: an external runtime's action surface is open-ended, so the safe floor is closed.
- **No tier provider → `Deny`** for a `PremiumTier` requirement (fail closed, never silently allow).
- **No authorizer registered → `ActionAuthorizer.denyAll`.** Hosts must fall back to it, so "forgot to wire the authorizer" fails closed.
- **A throwing backing store → `Deny of "… (fail-closed)"`.** Implementations never throw.
- `ActionAuthorizer.allowAll` exists for local dev only; never register it in a production composition.

## Writing a second implementation

Implement `IActionAuthorizer` (external policy engine, OPA bridge, static manifest…) and validate it against the `IActionAuthorizerContract` pack in `ToolUp.Platform.Tests` — deterministic decisions, value-keyed inputs, never-throwing, order-independent parallel calls.

## See also

- [`docs/migrations/113-action-authorizer.md`](../migrations/113-action-authorizer.md) — adoption + verification.
- `src/ToolUp.Platform/technical-guide/02-multi-tenancy-and-access.md` — the permission + scope substrate underneath.
