# Declared-route effect containment: a route's declaration binds what it reaches

**Ships in:** ToolUp.Platform.Server (`ServerModule.RouteEffects`, `ServerModule.withRouteEffect`,
`RouteEffectDeclaration`, `RouteEffectObserver`, `RouteEffectContext`, `RouteEffectContainment`,
`EgressRouteContext`) and ToolUp.Platform.Core (`HostCapabilityBoundary`,
`HostCapabilityRegistry.installBoundary`). Ships in 0.28.0 (Phase 773).

**Who is affected:**
- Code that builds a `ServerModule` as a full record literal. Add `RouteEffects = []` (FS0764 otherwise). Code that
  starts from `ServerModule.create` is unaffected.
- Module authors who want a route held to what it declares. Nothing changes for a module that declares nothing: its
  handler list is not wrapped at all.

## What a consumer sees

`ServerModule.withRouteEffect method path declaration` declares, for one exact route, an optional effect envelope
(`CompanionCapability`) and an optional egress grant (`EgressGrant`). `ServerApp.addModule` wraps the module's handlers
once, inside the Phase 770 seam-aware wrapper. While a declared route runs:

- a host-capability invocation through any registry built by `HostCapabilityRegistry.create` goes through
  `CompositionCapabilityGate.guardInvoke` with the route's envelope. Every invocation counts as effecting, so a `Pure`
  route is refused every one. The refusal is `HostCapabilityOutcome.Denied` naming the route, and the
  `RouteEffectObserver` receives the `CapabilityDenial`;
- an outbound call through a platform client to an origin outside the route's grant raises `EgressDeniedException`
  before any socket opens and writes an `EgressDenied` row. A `Pure` envelope permits no outbound call. The grant
  narrows the installed egress policy and never widens it;
- outbound calls are attributed to the owning module (`EgressComponentContext`), not to the platform.

`addModule` registers a default `RouteEffectObserver` (with `TryAddSingleton`) for a module that declares a route. It
writes a `CompositionCapabilityRefused` row through the composition's `IAuditLog` under the `standard` profile label.

## Verified compositions

Register your own observer so the row names your profile:

```fsharp skip=fragment
services.AddSingleton<RouteEffectObserver>(fun provider ->
    RouteEffectObserver(
        VerifiedCompositionProfile.auditingObserver
            (provider.GetRequiredService<IAuditLog>())
            "_platform"
            CompositionProfile.Verified
    ))
```

## What it does not do

It does not sandbox handlers. A route that opens its own socket, builds its own `HttpClient`, or uses an
`IHostCapabilityRegistry` implementation of its own is outside both checks. Undeclared routes stay effect-labelled,
not contained.

## Verification

`dotnet <ToolUp.Platform.Tests.dll> --filter-test-list "Phase 773"` covers the `Pure` refusal and its audit row, the
undeclared route unchanged, exact egress origins, the `route-effect` surface entries, and the new
`IHostCapabilityRegistry` contract pack, bound with and without a route boundary in force.

## Rollback

Remove the `withRouteEffect` calls. With no declaration nothing is wrapped and nothing is registered.
