# Migration — Phase 229: DSR export/erasure endpoint authorization

**What changed**

The `IDataSubjectRequestApi` (GDPR export / erasure) was previously
annotated `[<AllowAnonymous>]` on every method with no in-handler role
check — so export and erasure of any subject's data by id were reachable
by any caller the deployment surface admitted. It is now **Platform-Admin
only**:

1. Every method carries `[<RequiresRole "PlatformAdmin">]` (the dispatcher's
   classifier rejects non-admins) instead of `[<AllowAnonymous>]`.
2. `DataSubjectRequestApiHandler.create` gained an `accessContext:
   AccessContext` parameter and re-checks `canModifyPlatformConfig`
   in-handler (defence in depth — the gate no longer depends solely on the
   deployment's auth middleware). A non-admin caller receives
   `Error "platform admin role required"`.

**Who must act**

- **Consumers using the standard compose** (`AIServerApp` / `RAGServerApp`
  with `DataSubjectRequests = Enabled …`): **no code change.** The compose
  root (`BuildRouteHandlers`) passes the request's `AccessContext`
  automatically. The only behaviour change is the intended one: DSR
  endpoints now require Platform Admin. If a deployment was (incorrectly)
  relying on non-admin DSR access, grant the operator Platform Admin
  (`TOOLUP_INITIAL_PLATFORM_ADMIN` / the admin store).
- **Consumers calling `DataSubjectRequestApiHandler.create` directly**
  (rare — a custom mount): add the `accessContext` argument between
  `actorUserId` and `audit`:
  ```fsharp
  DataSubjectRequestApiHandler.create
      exporters handlers policy scopeId accessContext.UserId
      accessContext            // ← new
      audit asyncDeps
  ```

**Verification**

- `dotnet build` your custom-mount project — the compiler flags the missing
  argument.
- A non-admin call to `RequestExport` / `PreviewErasure` / `ConfirmErasure`
  returns `Error "platform admin role required"`; an admin call proceeds.

**Rollback**

Revert the forge commit. No data migration — the change is authorization
only.
