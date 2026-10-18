# One declared-principal shape — `DeclaredPrincipal` in Platform.Core, `StandingSubscriptionPrincipal` deprecated onto it

**Ships in:** ToolUp.Platform.Core (`DeclaredPrincipal`, moved here from ToolUp.Platform.Server — same namespace,
same fields), ToolUp.Platform.Server (the `DeclaredPrincipal.user` / `userOf` / `missingGrant` readings, unchanged),
ToolUp.Reporting.Core (`StandingSubscriptionPrincipal`, now an obsolete abbreviation of `DeclaredPrincipal`), and
ToolUp.Reporting.Server (`ReportingCompose.StandingSubscriptions.Principals` retyped; `StandingSubscriptionRefusal.missingGrant`
deprecated). Rides the 0.28.0 draft, in the breaking class it already carries.

**Affected:** a deployment that declares standing report subscriptions (Phase 1005) and names the principal type, or
that calls `StandingSubscriptionRefusal.missingGrant`. A deployment that declares data-source deliveries (Phase 1015)
recompiles unchanged. Nothing changes at runtime.

## What changes

1. **One record carries a declared principal.** A standing subscription and a declared data delivery both run as a
   `ToolUp.Platform.DeclaredPrincipal` — a name, a user id and a label. The record lives in ToolUp.Platform.Core, so
   every tier that declares work names the same type; the readings that need the server-tier identity type stay with
   `DeclaredPrincipalResolver` in ToolUp.Platform.Server.
2. **`StandingSubscriptionPrincipal` is the same type under its earlier name.** It is an `[<Obsolete>]` type
   abbreviation of `DeclaredPrincipal`: F# source that names it still compiles, with warning FS0044, and builds
   exactly the value the declaration path takes. Being an abbreviation, it has no IL class of its own, so an assembly
   compiled against 0.27.0 must be recompiled — source does not change.
3. **`StandingSubscriptions.Principals` is a `DeclaredPrincipal list`.** A record literal of principals needs no edit.
4. **`StandingSubscriptionRefusal.missingGrant` is deprecated** in favour of `DeclaredPrincipal.missingGrant`, which
   it already returned. It still works.

## What to do

- **Rename the type where you name it** (optional until the next major; the warning says so):

  ```diff
  -let principals: StandingSubscriptionPrincipal list = [
  +let principals: DeclaredPrincipal list = [
       { Name = "reports"; UserId = "svc-reports"; DisplayName = "Weekly reports" }
   ]
  ```

  `DeclaredPrincipal` is in `ToolUp.Platform`; open it where you open `ToolUp.Reporting`.
- **Replace `StandingSubscriptionRefusal.missingGrant e` with `DeclaredPrincipal.missingGrant e`.**
- **Recompile** every assembly that references ToolUp.Reporting.Core.

## Verify

Build with warnings visible: no FS0044 naming `StandingSubscriptionPrincipal` or
`StandingSubscriptionRefusal.missingGrant` remains once you have renamed. The Phase 1005 list in
`ToolUp.Platform.Tests` (`ReportSubscriptionScopeTests`) still runs every standing subscription under its declared
principal's scope, and pins that the earlier name is the shared record.

## Rollback

Pin ToolUp.Reporting.Core, ToolUp.Reporting.Server, ToolUp.Platform.Core and ToolUp.Platform.Server back to 0.27.0
together. Source that kept the earlier name compiles against either version.
