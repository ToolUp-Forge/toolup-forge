# Phase 61 — PlatformAdmin public-utility widgets (consumer migration)

**What changes.** `PlatformAdminUI` gains a public-utility widget section surfaced when `ClientConfig.PlatformAdminProfile = PublicUtilityPlatformAdminProfile`. Four widgets — TrafficDashboard, RateLimitEventLog, AdUnitConfig, PremiumUserList — render below the existing tabs. Each widget renders a substrate-aware view (live data when the server surface is wired, "substrate not configured" stub otherwise).

**Scope.** Opt-in. Default `PlatformAdminProfile = StandardPlatformAdminProfile` renders `Html.none` for the new section — preserves today's view byte-for-byte. The new widget surface is additive: standard admin / health / team / etc. tabs unchanged.

**Composition shift.** `PlatformAdminUI.create` now takes the active `ClientConfig` as a second argument (previously took only `PlatformAdminConfig option`). SDK consumers using the auto-injected default registration are unaffected — `SDK.Client.run` passes the config in for them. Consumers manually composing `PlatformAdminUI.create` need to update the call site.

## Diff to apply

Auto-injected consumers (the common case):

```fsharp
// Client.fs — flip to the public-utility profile:
let clientConfig = {
    ClientConfig.defaults with
        PlatformAdminProfile = PublicUtilityPlatformAdminProfile
}
```

Manually-composed consumers (rare — only when overriding default registration via `ClientConfig.PlatformAdmin = ExternalPlatformAdmin`):

```fsharp
// Before:
PlatformAdminUI.create config

// After:
PlatformAdminUI.create config clientConfig
```

## Verification

1. `dotnet build` clean; Fable transpile clean.
2. With `PlatformAdminProfile = StandardPlatformAdminProfile` (default) — Platform Admin view shows two tabs (Admins + Settings) unchanged from prior behaviour.
3. With `PlatformAdminProfile = PublicUtilityPlatformAdminProfile` — same two tabs at the top; below them, a "Public utility" section with four widget cards. AdUnitConfig surfaces the configured default ad-client when `AdPanel = EnabledAdPanel _`; the other three widgets render substrate-stubs citing the missing server endpoints.

## Rollback

Revert `ClientConfig.PlatformAdminProfile = StandardPlatformAdminProfile`. The public-utility widget section disappears; existing PlatformAdmin tabs continue rendering as before.

## Consumers

The migration is **N-A** for any consumer where the Standard PlatformAdmin profile suffices. Public-utility-class deployments adopt by setting the public-utility PlatformAdmin profile.

## Deferred follow-up

Server-side fan-in endpoints (`/api/_platform/admin/{traffic,rate-limits,ad-units,premium-users}`) ship in a follow-up commit. Until then the widgets render substrate-stubs explaining what each endpoint will surface.
