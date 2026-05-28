# Phase 58 — Notifications-disabled-explicit mode (consumer migration)

**What changes.** A new `ServerConfig.Notifications` case — `NoNotificationsExplicit` — pairs the server-side route-skip behaviour of `NoNotifications` with a Vite-define-time bundle constant that lets the client's `NotificationClient` skip EventSource instantiation entirely. Consumers that today pin `Notifications = InMemoryNotifications` purely to make the `/api/notifications` route exist (and so plug the client's 404 retry loop) can drop the pin, declare the absence as deliberate, and stop paying for SSE machinery they don't use.

A defensive fallback in `NotificationClient.onError` also lands: on a fatal `EventSource` failure (typical cause: a 404 from a server that did not mount `/api/notifications`), the client now closes out for the session rather than retry-looping. Consumers who set `Notifications = NoNotificationsExplicit` but forget the Vite define still benefit from the new behaviour, they just pay one 404 per tab to get there.

**Scope.** Two-sided. Server-side requires bumping `ToolUp.Platform.Server` to the version that carries `NoNotificationsExplicit` in the `NotificationMode` DU. Client-side requires bumping `ToolUp.Platform.Client` to the version that carries the new `BundleConstants.notificationsDisabledExplicitly` accessor + the `NotificationClient` gate.

## Diff to apply

### Server-side composition root

```fsharp
// Before — pinned to InMemoryNotifications purely to plug the client 404:
ServerApp.empty
|> ServerApp.withMode Anonymous
|> ServerApp.withNotificationsMode InMemoryNotifications

// After — explicit-off, matches the deployment's actual intent:
ServerApp.empty
|> ServerApp.withMode Anonymous
|> ServerApp.withNotificationsMode NoNotificationsExplicit
```

`NoNotificationsExplicit` is server-side equivalent to `NoNotifications` (no `INotificationChannel`, no `/api/notifications` route, no SSE keepalive timer); the difference is the new bundle constant the client reads.

### Client-side Vite config

```ts
// vite.config.mts
import { defineConfig } from "vite";

export default defineConfig({
  define: {
    // existing constants...
    __TOOLUP_MODULE__: JSON.stringify(process.env.TOOLUP_MODULE ?? ""),
    __AG_GRID_LICENSE__: JSON.stringify(process.env.AG_GRID_LICENSE ?? ""),
    __CLERK_PUBLISHABLE_KEY__: JSON.stringify(process.env.CLERK_PUBLISHABLE_KEY ?? ""),
    // Phase 58 — match the server-side ServerConfig.Notifications setting.
    // `true` only when the deployment runs `Notifications = NoNotificationsExplicit`.
    __TOOLUP_NOTIFICATIONS_DISABLED__: JSON.stringify(true),
  },
});
```

Drive the value from the same env var that picks the server config when the two halves can otherwise drift (e.g. `process.env.TOOLUP_NOTIFICATIONS_DISABLED === "true"`). The fallback in `NotificationClient.onError` covers single-attempt drift, but pairing the constant with the server config is the cleaner shape.

## Verification

1. `dotnet build` clean.
2. Boot the deployment. Open the SPA in a fresh tab with DevTools Network open.
3. Confirm zero requests to `/api/notifications` over the session (filter by URL substring). Pre-migration, you would have seen at least one — followed by either a retry loop (legacy clients) or a single 404 (clients with the new fallback but no Vite define).
4. Confirm the browser console carries the one-shot info line `Notifications explicitly disabled (__TOOLUP_NOTIFICATIONS_DISABLED__); skipping EventSource for this session` rather than the warn line `SSE connection error — EventSource will retry automatically`.
5. Regression check: switch the deployment back to `InMemoryNotifications` and rebuild without the Vite define; confirm `/api/notifications` requests resume and SSE envelopes round-trip into the toast centre (or whatever feature your deployment uses).

## Rollback

Revert the server config to `Notifications = InMemoryNotifications` (or whatever the prior pin was) and the Vite define to `__TOOLUP_NOTIFICATIONS_DISABLED__: JSON.stringify(false)` (or remove it entirely; absent reads as `false`). The two halves are independent — running the new server with an old client is safe (the client 404s and the new defensive fallback catches the loop), and running the old server with a new client is safe (the client uses the Vite define directly and never opens EventSource).

## Consumers

The migration is **N-A** for production-shape deployments that need SSE (jobs + AI), and for any consumer with its own real-time substrate. A consumer currently pinning `InMemoryNotifications` solely to plug the SSE 404 should adopt `NoNotificationsExplicit` instead.

Any serverless / public-utility deployment should start on `NoNotificationsExplicit` from its first commit and never carry the `InMemoryNotifications` pin.
