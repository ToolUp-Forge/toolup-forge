# Phase 619 — Secure-by-default authorization for `IReportApi` (consumer migration)

**What changes.** Every method on `ToolUp.Reporting.IReportApi` moved from `[<AllowAnonymous>]` to `[<RequiresClaim "scope">]`. An **unauthenticated caller is now refused** on `ListTemplates`, `SaveTemplate`, `DeleteTemplate` and `Render`, by the dispatcher's Phase 69d classifier, before the handler runs. A new `ReportApiHandler.withManagementGate` decorator adds an optional in-handler second gate on the two mutating methods.

**BREAKING, deliberately.** A deployment that today reaches this API anonymously — because it mounts the reporting API on a surface admitting `AnonymousKind`, or because it never wired an auth-context resolver at all — will start seeing `ErrorCategory.Auth` denials. That is the point. Reports became a **disclosure egress door** in Phase 564 (narrative placeholders carrying fact refs leave the deployment through `Render`), and an egress door whose default classification is "anyone" contradicts the default-deny posture the rest of the authorization surface is built on. The pre-619 doc comments already claimed Owner/Admin gating; nothing enforced it — the identical tell that was closed on the DSR export/erasure endpoints (`IDataSubjectRequestApi`), whose comment likewise asserted "Owner / Admin gated upstream" over code that gated nothing.

**Scope.** Server-side enforcement only. No wire change: denials use the existing categorised `ErrorCategory.Auth` envelope, so a client reading the `error` body still parses. `ToolUp.Reporting.Core` is untouched. No data migration.

**Version.** Minor bump under the SemVer-on-`0.x` policy (breaking changes are permitted in a minor).

## The classification, method by method

| Method | Before | After | Why |
|---|---|---|---|
| `ListTemplates` | `[<AllowAnonymous>]` | `[<RequiresClaim "scope">]` | Template bodies and placeholder schemas are scope-owned business content, and they describe exactly what the deployment can render. |
| `SaveTemplate` | `[<AllowAnonymous>]` | `[<RequiresClaim "scope">]` + optional management gate | A template is executable render content; writing one is a privileged scope mutation. |
| `DeleteTemplate` | `[<AllowAnonymous>]` | `[<RequiresClaim "scope">]` + optional management gate | As `SaveTemplate`, and destructive besides. |
| `Render` | `[<AllowAnonymous>]` | `[<RequiresClaim "scope">]` | The Phase 564 disclosure egress door. Deliberately **not** admin-gated — rendering is the ordinary user-facing operation, and the fact-level `FactExport` gate decides which *values* a principal may egress. |

**No method stays anonymous, and there is no share-token exception.** No method on this contract takes a token; the scope is resolved from the caller upstream. forge's token-gated public surface is `IPublicFormApi`, which carries `[<PublicEndpoint>]` and takes the token as a parameter. A public report-share surface, if ever wanted, is a separate contract of that shape — not a relaxation here.

**Why `[<RequiresClaim "scope">]` and not a role.** Against the default `ForgeAuthContext` resolver, `HasClaim("scope", None)` resolves to exactly `not isAnonymous` — the forge convention for a scope-owned surface that is neither role-gated nor tenant-only, and the same gate `IConfigApi` / `TeamApi` / `ITeamInviteApi` / `MaintenanceApi` / `IUsageQueryApi` already apply. A role gate was rejected on both available strings:

- `"PlatformAdmin"` would make report templates manageable only by platform admins, breaking per-team template management in every deployment.
- `"Owner"` / `"Admin"` — the strings the pre-619 comments named — are **Phase 132 dead gates**. The first-party auth providers leave `AuthenticatedUser.Roles` empty, so any role other than the server-resolved `"PlatformAdmin"` denies *every* caller, admins included.

`[<TenantScoped>]` was also rejected: it requires `HasTenant()`, which a personal-scope owner in a single-user deployment does not have, so it would refuse the very caller who owns the scope.

**What the gate does not do.** `[<RequiresClaim "scope">]` is not a per-scope binding — it admits any authenticated subject, including a share-token `ClaimBearer`. That a caller may only reach *their own* scope is enforced structurally by the `StorageScope` resolver upstream (GP 4). That is what the pre-619 comment was gesturing at; the difference is that there is now a gate in front of it.

## What a deployment must wire

**Most deployments: nothing.** If your reporting API is mounted through `Api.make` on an authenticated surface, the default auth-context resolver already reads Phase 66's `Subject` + `AuthenticatedUser` from `HttpContext.Items` and your signed-in callers keep working unchanged.

**1. Confirm the mount admits the callers you expect.** If the reporting routes sit under a `SurfaceRequirement` that includes `AnonymousKind`, anonymous requests reached the dispatcher before and were allowed; they now reach it and are denied. Tighten the admit set to match:

```fsharp
// Before: the module's surface admitted anonymous callers.
DefaultSurfaceRequirement = SurfaceRequirement.userOrTeam   // or stricter
```

**2. Confirm an auth-context resolver is armed.** `Api.make` arms the default resolver for every record API type, so this is automatic — unless you pass a custom `?authContext`. A custom resolver must answer `HasClaim("scope", None)` truthfully for authenticated callers, or every reporting call denies:

```fsharp
member _.HasClaim(claim, value) =
    match claim, value with
    | "scope", None -> not (isAnonymous ())   // <- required by IReportApi
    | ...
```

**3. Optional — restore the Owner/Admin restriction on writes.** The attribute gate admits any authenticated caller. If your templates should only be managed by an Owner or Admin, compose the in-handler gate. It is a decorator, so it composes over either factory:

```fsharp
open ToolUp.Reporting

let api =
    ReportApiHandler.createWithDisclosureGate gate principal templateStore registry storeBlob audit config scopeId
    |> ReportApiHandler.withManagementGate (fun () -> async {
        // your deployment's role model — forge cannot express it, because
        // the first-party providers carry no role vocabulary.
        return! isOwnerOrAdmin callerId scopeId
    })
```

`SaveTemplate` / `DeleteTemplate` then return `Error ReportApiHandler.TemplateManagementDenied` for a caller the predicate refuses, without reaching the inner handler. `ListTemplates` / `Render` are untouched. The predicate is read **per call**, not snapshotted at composition, so a revoked caller is refused on their next write rather than at the next restart.

Not composing it is a supported posture (GP 13): the attribute gate still refuses anonymous callers, which is this phase's breaking default.

## Restoring the prior reach deliberately

There is no config flag, and that is intentional: a flag defaulting to the old behaviour would ship the fix and leave the defect. If a deployment genuinely needs an anonymous reporting reach, the honest route is to say so in your own contract rather than to weaken the SDK's:

1. Declare your own record with the methods you want public, carrying `[<PublicEndpoint>]` and taking a share token as a parameter (the `IPublicFormApi` shape).
2. Have its handler validate the token through `IShareTokenStore`, resolve the scope from the *token*, and delegate to an `IReportApi` built for that scope.

That keeps the reachable surface enumerable — it appears in `AuthorizationSurface.anonymousReachable`, where a security review will see it — instead of hiding an anonymous door behind a contract that claims to be gated.

## Verification

1. **Anonymous is refused.** Call any of the four methods with no session. Expect an `ErrorCategory.Auth` denial; the server-side reason is `anonymous-not-permitted`.
2. **An authenticated caller passes.** The same call signed in succeeds unchanged — including a non-admin with no team, who owns their own scope.
3. **The manifest agrees.** `AuthorizationSurface.ofApiRecord<IReportApi> componentId |> AuthorizationSurface.anonymousReachable` is empty. Before 619 it listed all four methods. Diffing the two shapes reports `CriticalAuthorizationDrift`.
4. **Contract pack.** `src/ToolUp.Platform.Tests/InProcess/ReportingAuthorizationTests.fs` — per-method classification, the anonymous deny driven through the real default resolver, a falsifier fixture proving that probe can still *allow* the pre-619 shape, the authenticated allow, the dead-gate and route-round-trip checks, the manifest assertions, and the management-gate behaviour.

## Rollback

Re-annotate the four `IReportApi` fields `[<AllowAnonymous>]` and drop any `withManagementGate` composition. Nothing else changes — no data, no wire, no storage. Doing so returns the four methods to `AuthorizationSurface.anonymousReachable`, which is exactly the signal the composition golden-file gate is there to raise.
