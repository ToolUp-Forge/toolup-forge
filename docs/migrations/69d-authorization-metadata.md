# Phase 69d.tail — Per-method authorization metadata, classifier default-on (consumer migration)

**What changes.** `Api.make` now arms the dispatcher's startup classifier for **every** F# record API type: each record method must carry exactly one honest classification — `[<RequiresRole "...">]`, `[<RequiresClaim "...">]`, `[<TenantScoped>]`, `[<AllowAnonymous>]`, or `[<PublicEndpoint>]` — or the server **refuses to start** with a diagnostic naming the record and the unclassified field(s). Pre-0.5.0 the resolver was only composed when the record already carried at least one attribute, which silently skipped enforcement for entirely unannotated records — the exact "module author forgot the guard" defect class this phase closes. Authorization moves from handler-internal "remember to guard" to dispatcher-enforced refuse-to-start (GP 4 structural enforcement).

**Scope.** Server-side enforcement only; no wire change (denials use the existing `ErrorCategory.Auth` envelope). Every forge SDK API record ships annotated, so SDK-mounted routes need nothing from you.

**BREAKING for consumers with unannotated API records.** A consumer record composed through `Api.make` with any unclassified method fails at startup:

```
ToolUp.Remoting refused to start: API record 'MyApi' has 2 unclassified method(s): [GetThings; SaveThing]. ...
```

## Diff to apply — the three common patterns

```fsharp
open ToolUp.Platform // the tier-shared attribute mirrors (Fable-safe)

type MyApi = {
    // 1. Admin-only method (was: role check inside the handler)
    [<RequiresRole "Admin">]
    PromoteUser: string -> Async<unit>

    // 2. Tenant-scoped method (any team-bound caller)
    [<TenantScoped>]
    GetThings: unit -> Async<Thing list>

    // 3. Public / anonymous-reachable
    [<AllowAnonymous>]
    GetVersion: unit -> Async<string>
}
```

- Use the `ToolUp.Platform.*` attributes on records the Fable client compiles (Shared files); the `ToolUp.Remoting.Server.*` family is for server-only records. The classifier recognises both by simple name and they compose identically.
- `[<RequiresClaim "scope">]` is the forge convention for "any authenticated caller" (not role-gated, not tenant-only, never anonymous).
- Multi-attribute methods AND their requirements: `[<RequiresRole "Admin">] [<TenantScoped>]` enforces both.
- Drop record-level guards after annotating — the per-method attribute is the enforcement. (`Api.makePermissionGuardedApi` was retired earlier; per-method attribution is its replacement.)
- Non-record API types keep the dormant pre-69d behaviour, but composing any attribute-driven seam against a non-record refuses startup with its own diagnostic.
- Internal/private record types classify exactly like public ones (the reflectors use `BindingFlags.NonPublic`) — visibility is not an escape hatch.

## Verification

1. Annotate, then start the server: an already-correctly-guarded handler's startup log is byte-for-byte unchanged.
2. Temporarily remove one attribute: startup must fail naming the record + method.
3. Contract pack: `InProcess/AuthorizationTests.fs` in `ToolUp.Platform.Tests` (per-attribute evaluation, AND-semantics, fail-closed paths, startup refusal, and the sweep-enforcement list every forge record is pinned by).

## Rollback

Supply an explicit `?authContext` resolver to `Api.make` to restore opt-in arming per call site, or revert forge commit `986584d`. No data migration.
