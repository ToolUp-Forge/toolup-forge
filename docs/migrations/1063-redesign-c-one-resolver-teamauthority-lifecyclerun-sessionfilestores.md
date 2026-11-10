# 1063 — One resolver, one team-authority gate, one lifecycle run, one session-file service

Four seams answered one question several times each. Each now answers it once. The codemod covers the
mechanical sites (`dotnet run --project Build.fsproj -- Codemod --phase 1063`) and reports the rest.

## What changes

| Seam | Before | After |
|---|---|---|
| Scope resolution | `IStorageScopeResolver` with `TeamScopeResolver`, `AnonymousScopeResolver`, `AuthenticatedScopeResolver`, `AuthenticatedEphemeralScopeResolver`; `ScopeRequestExtractor` | gone. Resolve the subject with the registered `ISubjectResolver` (the deployment's one `DefaultSubjectResolver`) and derive the scope with `StorageScopeDerivation.tryFromSubject`, or `SubjectScope.resolve subjects derive request`. A request's resolved scope is `ScopeResolution.forRequest ctx` |
| Declared principals | `DeclaredPrincipalResolver` over the retiring resolver | over `ISubjectResolver` + the derivation, so declared work resolves exactly as a request does |
| The membership cache | two caches and two `PlatformReservedScope` subscriptions, each taken with `Async.RunSynchronously` in a constructor | one, in `DefaultSubjectResolver`, taken in `IHostedService.StartAsync` (the composition registers it with `Hosting.register … OnceAtStart`). A test that builds one starts it. `TeamScopeResolver.InvalidateUser` → publish `MembershipChanged` (`ActiveTeamSet`) on the reserved topic |
| `X-User-Id` | a fallback caller id in `ScopeRequestExtractor.userId` and `FileManagement.getUserId` | names no caller. A request carrying only the header is anonymous (`AccessContext.callerIdOf ctx.Items`, `"anonymous"` without a principal) |
| "May this caller act on this team" | six hand-written gates (`effectiveCallerRole`, the four permission-API checks, `ensureManageMembers`, the service-account ladder, `ensureCanResetDataStore`, `TeamPolicyViewer.resolve`'s role read) | `TeamAuthority.require teams bypass principal teamId predicate` → `Result<TeamRole, AuthorityRefusal>`. `AdminBypass` states whether a platform admin is the team's Owner at the gate; the platform role is the principal's, never re-queried |
| Permission write chain | decorators took the actor as `resolveProposer: unit -> string option` / `actorId: string option` (default `"unknown"`); the order was prose | `GrantPolicyPermissionStore`, `DualControlPermissionStore` and `GrantNotificationObserver` take the caller's `AccessContext`. The handler wraps its writes once: `PermissionWrites.asPrincipal principal writes` (the registered `PermissionWrites`) builds grant policy OUTSIDE dual control. The DI `IPermissionStore` is the chain under `AccessContext.system`, which proposes nothing under dual control |
| Tenant lifecycle | eleven entry points | a `LifecycleRun` (`Scope`, `Phase`, `Actor`, `Request`, `Timeout`, `Guard`, `Audit`, `Hooks`; `LifecycleRun.create` takes the phase's default timeout and the in-process lock) and `run`, `runResumable cursor`, `exportThenDeprovision export`. Each answers whether its guard admitted the run (`GuardedRun` / `GuardedExportThenDeprovision`); `Guard = None` for a caller that holds the scope |
| Session file stores | six module-level values in `FileManagement` and a first-request `Async.RunSynchronously` hydration | `SessionFileStores` (registered as `ISessionFileStores`): `Resolve(dataTypes, store, runtime, scope)` awaits the scope's one hydration (a time-boxed `Async.StartChild`). `FileManagement.getStore` / `getStoreForUser` / `configureEvictionMinutes` / `storeEvictionMinutes` are gone; `getFileContents` and `ModuleApiContext.GetFileContents` answer `Async<string>`; `SessionFileStore.Open` builds a hydrated store |

## The edits

```fsharp skip=fragment
// a resolver consumer
let subjects = services.GetRequiredService<ISubjectResolver>()
let! scope = SubjectScope.resolve subjects (StorageScopeDerivation.tryFromSubject (Some logger) config) request

// a lifecycle caller
match! TenantLifecycleAggregator.run { LifecycleRun.create audit hooks Deprovisioning scopeId actor with Guard = Some lock } with
| GuardedRun.Ran summary -> ...
| GuardedRun.AlreadyInProgress -> ...

// a permission write in a handler
let store = PermissionWrites.asPrincipal principal (services.GetRequiredService<PermissionWrites>())

// a file read in a module endpoint
let! contents = m.GetFileContents fileName
```

**Behaviour changes.** A declared principal in a deployment that declares both a team and a user surface,
with no active team, now resolves to its user scope, as a request signed in as it does (the retired
resolver refused it). The service-account, invitation, permission, reset and policy-viewer gates keep their
verdicts exactly (`TeamAuthorityTests` pins each against the code it replaced). A hydration that overruns
its 30 s budget serves what it loaded while the remaining reads finish in the background.

**Verify:** `dotnet build ToolUp.Forge.sln`; the `TeamAuthority`, `DefaultSubjectResolver`, `TenantLifecycle`
and `FileManagement` lists. **Roll back:** pin the previous SDK version.
