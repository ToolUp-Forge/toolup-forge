# Phase 579 — compose-time duplicate query-handler rejection (consumer migration)

**What changes.** Both module query buses route `(TargetModule, QueryKey)` to **exactly one** handler. Until now, registering the same pair twice folded silently — the last registration won and the earlier handler was discarded with no diagnostic. From Phase 579 a duplicate pair is a **fatal compose-time defect** on both tiers: `ModuleQueryBus.buildRegistry` (server, reached from `ServerApp.run`) and `ModuleQueryClient.buildRegistry` (client, reached from `Client.run`'s `buildQueryBus`) both delegate to the tier-shared `ModuleQueryRegistry.build` in `ToolUp.Platform.Core`, which raises before anything binds. This mirrors the existing duplicate-`Kind` rejection on `INotificationSink` and the duplicate-`Name` rejection on `IAuditSink`.

**Scope.** A composition with no duplicate pair is unaffected — the registry it produces is identical to the pre-579 fold (GP 11). The check is purely structural over the registration list; the SDK names no module (GP 9). Nothing to opt into and nothing to configure.

**Who is affected.** Only a deployment that was already shipping a silent shadow. Its symptom before 579 was a request-time one: a caller got `NoHandler`, or an answer from the *other* handler, depending on registration order — which is exactly what makes it worth failing at startup instead.

## What the failure looks like

```
Compose-time defect: duplicate module query handler for module "Reports" on
query key "latest" (2 registrations). The bus routes (TargetModule, QueryKey)
to exactly one handler, so all but one registration would be silently shadowed
and callers would see NoHandler or the wrong handler at request time. Give each
handler a distinct QueryKey, or drop the redundant registration.
```

Every collision is reported, not just the first — one restart names the whole misconfiguration.

## How to find and fix the collision

The message names the module and the query key. Two shapes account for essentially every case:

1. **One module registering the same key twice.** Usually a copy-paste in `ServerModule.QueryHandlers` (or `ErasedModule.ClientQueryHandlers`) where the second entry was meant to carry a different key:

   ```fsharp
   QueryHandlers = [
       ModuleQueryHandler.typed "latest" handleLatest
       ModuleQueryHandler.typed "latest" handleHistory   // ← meant to be "history"
   ]
   ```

   Fix: give the second handler its own key, or delete it if it is genuinely redundant.

2. **Two module registrations sharing a module name.** The registry is keyed by `ServerModule.Name` / `ClientModule.Definition.Id`, so two distinct modules composed under the same name merge their handler lists and collide on any shared key. Fix: give each module a distinct name/id. (A name collision is worth fixing on its own — it also confuses RBAC, which is keyed by module name.)

To locate the registrations, grep the composition root for the key named in the message:

```powershell
Select-String -Path src\**\*.fs -Pattern 'ModuleQueryHandler.typed "latest"'
Select-String -Path src\**\*.fs -Pattern 'ClientModuleQueryHandler.typed "latest"'
```

If both a **server** and a **client** handler exist for the same `(module, key)`, that is **not** a collision and is not rejected — the client bus deliberately falls through to the server for keys it does not hold locally. Only duplicates *within one tier's* registration list fail.

## Verification

1. Boot the server: with no duplicate pair, startup and every existing cross-module query behave exactly as before.
2. Add a deliberate duplicate to one module's `QueryHandlers`: startup must refuse, naming that module and key.
3. Do the same to a client module's `ClientQueryHandlers`: `Client.run` must refuse at `buildQueryBus`, before the shell binds.
4. Test pack: `InProcess/ModuleQueryBusTests.fs` in `ToolUp.Platform.Tests` runs the duplicate-rejection assertions against **both** tier entry points, alongside the unchanged `IModuleQueryBus` contract pack.

## Rollback

There is no opt-out flag — a rejected composition is a genuine defect and the fix is to remove the duplicate registration. If you need to unblock a deployment immediately, drop or rename the redundant handler; behaviour then matches whichever handler the pre-579 fold happened to keep (the last one registered).
