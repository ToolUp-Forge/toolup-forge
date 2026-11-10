# 882 — A missing access context refuses, and unrestricted is stated

An empty permission map used to mean two things: "this team never configured permissions" and "nothing was
resolved". Both read as every module. A request that reached a handler without `ScopeResolutionMiddleware`
having stamped a permission map therefore passed every module check a team had configured, and about fifty
handlers carried an `AccessContext.unrestricted (AnonymousSession "anonymous")` fallback for tests that
bypassed the middleware. Now the first meaning is a stated case, and the second cannot be expressed.

## What changes

| Seam | Before | After |
|---|---|---|
| `AccessContext.ModulePermissions` (Core) | `Map<string, ModulePermission list>`; empty = unrestricted | `ModulePermissions` = `Unrestricted \| Restricted of Map<…>`; `Restricted Map.empty` (`ModulePermissions.none`) grants NOTHING |
| Readers | `.IsEmpty`, `Map.tryFind m ctx.ModulePermissions`, `.ContainsKey` | match on the case, or `ModulePermissions.tryFind` / `grantsAccess` / `grants` |
| Constructor | `AccessContext.unrestricted subject` | `AccessContext.ofSubject permissions subject`; `AccessContext.carrying reason subject` (work outside a request carrying its scope: jobs, narrative runs, planners); `AccessContext.system reason` (no principal: static export, evaluation harnesses — audited as `system:<reason>`); `AccessContext.principalId subject` for the identity string |
| Handler fallback | `\| _ -> AccessContext.unrestricted (AnonymousSession userId)` | `AccessContext.tryResolve ctx.RequestServices \|> Option.defaultWith (fun () -> AccessContext.unresolved "<site>")` — anonymous, no module, no role, no scope |
| The scoped `AccessContext` factory | read eleven `Items` keys, substituted `Map.empty` / anonymous for any it missed | reads ONE stamped principal (`AccessContext.ItemsKey`); none stamped ⇒ no service (`tryResolve` answers `None`) |
| `ScopeResolutionMiddleware` | stamped `ToolUp.ModulePermissions` (removed); a fault after the subject stamp was swallowed | stamps the whole principal once, after every grant was read; a fault after its first stamp refuses with 503, audited (`AuthScopeResolutionFailed`) |
| `IPermissionStore.GetEffectivePermissions` | `Result<Map<…>, _>` | `Result<ModulePermissions, _>`: `Unrestricted` for a team whose document holds no member grant and no default (`PermissionStore.isConfigured`), else `Restricted` to the user's effective map |
| Service-account overlay | three never-empty guards | adopted as `Restricted`; the token middleware's and factory's empty guards removed; the store still refuses an empty declared set and an empty meet |

| The request items | eleven string keys (`ToolUp.User`, `UserId`, `Subject`, `StorageScope`, `ScopeResult`, `ModulePermissions`, `ModuleExposure`, `PlatformRole`, `PublicationGrants`, `ServiceAccountPermissions`, and the factory's reassembly) | ONE item, the principal under `AccessContext.ItemsKey`; `AccessContext` gains `User` (the provider's user) and `StorageScope`. Read it with `AccessContext.ofItems` / `stamp`, or the accessors `callerIdOf` / `subjectOf` / `scopeOf` / `userOf` / `platformRoleOf`. Two inputs to resolution stay: the share-token claim and `ServiceAccountPrincipalItemsKey` |
| The caller id | `ToolUp.UserId`, the provider's user (`"anonymous"` for a machine bearer) | `AccessContext.callerId`: `"anonymous"` for an anonymous session, the principal id otherwise — a machine or share-link bearer is named by its account, never `"anonymous"` |
| `RequestViewer` | read three items | `RequestViewer.ofAccess` (a projection); `ofItems` reads the principal |
| `TeamPolicyViewer.resolve` | `services access userId teamId` | `services access teamId` — the user id is the principal's |
| `EntityPrincipal` from a handler | `EntityPrincipal.ofPrincipal ac.UserId` | `AccessContext.entityPrincipal ac` |
| Service-account overlay | its own item, read by the factory | derived from the stamped `ServiceAccountPrincipal` by `Middleware.principalOf`, the one projection |

**Behaviour change for configured teams:** a member with no entry and no team default now holds nothing; before,
their empty effective map read as every module. A team that never configured permissions is unchanged (GP 11).
`PermissionStoreActionAuthorizer` stays default-deny: `Unrestricted` cites no grant for an action requirement.

## The diff, per shape

```fsharp
AccessContext.unrestricted subject                          // before (job / run)
AccessContext.carrying "scheduled job" subject             // after

match ctx.RequestServices.GetService(typeof<AccessContext>) with
| :? AccessContext as ac -> ac
| _ -> AccessContext.unrestricted (AnonymousSession "anonymous")             // before
AccessContext.tryResolve ctx.RequestServices
|> Option.defaultWith (fun () -> AccessContext.unresolved "MyHandler")        // after

if ctx.ModulePermissions.IsEmpty then … else Map.tryFind m ctx.ModulePermissions   // before
match ctx.ModulePermissions with                                                   // after
| ModulePermissions.Unrestricted -> …
| ModulePermissions.Restricted map -> Map.tryFind m map

match ctx.Items.TryGetValue "ToolUp.StorageScope" with                   // before
| true, (:? StorageScope as s) -> …
match AccessContext.scopeOf ctx.Items with                                // after
| Some s -> …

ctx.Items["ToolUp.UserId"] <- box "alice"                                 // before (a test)
AccessContexts.stampUserId ctx.Items "alice"                              // after (the fixture)
```

Tests build contexts through `src/ToolUp.Platform.Tests/TestSupport/AccessContexts.fs` (`forSubject`, `anonymous`,
`user`, `teamMember`, `system`, `restrictedTo`, `permissionsOf`, and the `stamp*` builders over a request's items), linked into each test pack that uses it.

## Codemod

`dotnet run --project Build.fsproj -- Codemod --phase 882` rewrites `AccessContext.unrestricted` to
`AccessContext.ofSubject ModulePermissions.Unrestricted` (behaviour-preserving; review each site: a handler
fallback becomes `unresolved`, background work `carrying` / `system`) and flags `.ModulePermissions.IsEmpty`,
`.ModulePermissions.TryFind` / `ContainsKey` / `Map.tryFind … ModulePermissions` and the retired
retired request items for review, and rewrites `EntityPrincipal.ofPrincipal x.UserId` to `AccessContext.entityPrincipal x`.

## Verification

`dotnet run --project Build.fsproj -- VerifyAccessFallbacks` (also in the `ScopeLiterals` leg of `VerifyAll`) is
red on the retired fallback anywhere under `src/`, `samples/` or `templates/` outside test packs, and on
`ModulePermissions.Unrestricted` constructed outside its three homes (`AccessContext.fs`, `PermissionStore.fs`,
`Middleware.fs`), and on any retired request item key spelled outside `AccessContext.fs`.

## Rollback

Revert the phase's commits; persisted permission documents did not change shape.
