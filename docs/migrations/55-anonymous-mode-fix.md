# Phase 55 — Anonymous-mode sidebar visibility fix (consumer migration)

**What changes.** Under `ServerConfig.Mode = Anonymous`, the `/api/accessibility/list` endpoint now returns every Managed module as Accessible, rather than the empty list it returned before. Consumers running Anonymous-mode deployments no longer need the `Mode = Individual + DevDefaultUserId = Some "dev-admin"` workaround to keep their sidebar populated.

**Scope.** Server-side fix only — no client-side change. The existing client-side intersection (`Accessible ∩ Managed`) becomes a no-op when the server reports Accessible = Managed.

## Diff to apply

Consumers that pinned `Mode = Individual + DevDefaultUserId = Some "dev-admin"` purely to plug the Anonymous-mode empty-sidebar bug can now switch:

```fsharp
// Before — localhost-only workaround:
ServerApp.empty
|> ServerApp.withMode Individual
|> ServerApp.withDevDefaultUserId (Some "dev-admin")

// After — Anonymous mode genuinely supported:
ServerApp.empty
|> ServerApp.withMode Anonymous
```

`ClientConfig.Mode` flips to `Anonymous` symmetrically; any `Team*` / `PlatformAdmin` / `UsageDashboard` settings configured for Individual mode should be set to `No*` defaults to avoid surfacing surfaces that are intrinsically incompatible with Anonymous deployments.

## Verification

1. `dotnet build` clean.
2. Boot the deployment in Anonymous mode; navigate to any registered user module from a fresh anonymous session — the module renders.
3. Compare the startup log against the pre-migration Individual + dev-default-user-id deployment: the only diff should be the absence of the dev-admin bootstrap log lines.
4. Confirm `/api/accessibility/list` returns the full Managed list (not empty) for an unauthenticated GET.

## Rollback

Revert `ClientConfig.Mode = Anonymous` to the prior `Mode = Individual + DevDefaultUserId` shape if a regression surfaces. The server-side fix is independent of client-side mode — running the new server code with a client still on Individual mode is safe; the new code path only activates when both halves are Anonymous.

## Consumers

The fix is **N-A** for any consumer running a Team-scoped or Individual-scoped deployment by design — they were never affected by the bug. Only Anonymous-mode deployments adopt.
