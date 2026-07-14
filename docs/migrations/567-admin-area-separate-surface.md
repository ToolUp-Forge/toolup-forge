# Phase 567 — Admin Area (two-surface sidebar navigation)

**Status:** additive, opt-in. A pre-567 app is byte-for-byte unchanged; the new
behaviour is off unless you set `ClientConfig.AdminSurface = SeparateArea`.

## What changes

The SDK's ~15 admin built-ins (the "Platform Management" / "Team Management" groups)
otherwise clutter every consumer's sidebar rail alongside product modules. Phase 567
adds a **navigation-area axis** — `ModuleArea = Product | Administration` — and an opt-in
sidebar mode that renders **one area at a time**:

- the product rail carries the app's modules plus a single role-gated **"Administration"**
  switcher entry;
- flipping into the admin area shows only admin modules, with a **"Back to app"** entry;
- the active team is preserved across the flip (this is a navigation surface, not a
  synthetic admin team), so team-scoped admin pages keep their context.

A module's **effective area** is `Administration` if it either declares
`Area = Administration` (via `ClientModule.withArea`) **or** sits in an admin sidebar group
(`ClientConfig.isAdminSidebarGroup` — "Platform Management" / "Platform Admin" / "Team
Management"). The latter is how the SDK's admin built-ins move to the admin area with **no
registration change** (GP 9). The switcher only renders when the caller actually has an
admin-area module to switch to — a plain user (whose platform-scoped groups are already
stripped upstream) sees no switcher (GP 12; server enforcement stays authoritative).

## Diff per consumer

**Opt in** — one field on your `ClientConfig`:

```diff
  let config = {
      ClientConfig.create handlers with
          ...
+         AdminSurface = SeparateArea
  }
```

**Optionally** place a *consumer* module in the admin area:

```fsharp
ClientModule.create { ... }
|> ClientModule.withArea Administration
```

No change is required to consume the default (`InlineGroups`) — existing apps keep today's
inline admin groups exactly.

## Verification

- `AdminSurface = SeparateArea`: a non-admin authenticated user sees no admin entries and no
  switcher; a platform admin sees the switcher, flips into an admin-only rail with the active
  team unchanged, and can flip back via "Back to app". Deep-linking (URL or programmatic) to a
  route owned by an `Administration`-area module flips the rendered area automatically.
- Default `InlineGroups`: sidebar is byte-identical to pre-567.

## Rollback

Remove the `AdminSurface = SeparateArea` override (or set it to `InlineGroups`) — the field
defaults to `InlineGroups`, so unsetting it fully reverts.
