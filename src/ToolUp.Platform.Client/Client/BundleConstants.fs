// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.BundleConstants

open Fable.Core

// Phase 11.G — typed accessors for the three Vite-injected `define`
// constants the reference deployment relies on. Lifting them into the
// SDK saves every consumer composition root from re-declaring three
// `[<Emit>]` `let` bindings. The `typeof X === 'string' ? X : ''`
// guard means consumers whose Vite config doesn't define the constant
// see an empty string instead of a runtime `ReferenceError` — falls
// back gracefully for any deployment that hasn't (yet) wired the
// Vite `define`.
//
// **Open question (Phase 11.G acceptance — verify in MinimalApp):**
// Fable's `[<Emit>]` propagation across `<ProjectReference>` is
// load-bearing here. If a Fable consumer that takes
// `ToolUp.Platform.Client` via `<ProjectReference>` does NOT see these
// Vite-defined constants substituted (the emit-output uses the literal
// `__TOOLUP_MODULE__` token at runtime), consumers will need to keep
// their own three `[<Emit>]` declarations and pass the resolved values
// to `ClientConfig.fromBundleConstantValues`. The `MinimalApp` sample
// (`toolup-forge/samples/MinimalApp/`) verifies this empirically at
// the end of Phase 11.G.

/// Read from the `__TOOLUP_MODULE__` Vite define (case-insensitive
/// substring filter over `ErasedModule.Definition.Name`). Empty
/// string when the define isn't wired — equivalent to "no filter".
[<Emit("(typeof __TOOLUP_MODULE__ === 'string' ? __TOOLUP_MODULE__ : '')")>]
let moduleFilter: string = jsNative

/// Read from the `__AG_GRID_LICENSE__` Vite define. Empty string
/// when unset — AG Grid Enterprise components then render "License
/// Required" overlays but the app still boots in Community-tier.
[<Emit("(typeof __AG_GRID_LICENSE__ === 'string' ? __AG_GRID_LICENSE__ : '')")>]
let agGridLicense: string = jsNative

/// Read from the `__CLERK_PUBLISHABLE_KEY__` Vite define. Empty
/// string when unset — Release builds wiring `ClerkAuthUI` should
/// fail loud rather than silently fall through to anonymous mode.
[<Emit("(typeof __CLERK_PUBLISHABLE_KEY__ === 'string' ? __CLERK_PUBLISHABLE_KEY__ : '')")>]
let clerkPublishableKey: string = jsNative

/// Phase 66 Stream A.8 client-side counterpart — read from the
/// `__TOOLUP_PLATFORM_SURFACES__` Vite define. Comma- / semicolon- /
/// space-separated token list matching the server-side
/// `TOOLUP_PLATFORM_SURFACES` env var (valid tokens: `anonymous`,
/// `anonymous_persistent`, `trial`, `individual`, `team`, `multi_team`,
/// `claim_bearer`). Empty string when unset — `ClientConfigOverrides`
/// then falls back to `Surfaces.anonymous` (the SDK default). Replaces
/// the retired `__TOOLUP_PLATFORM_MODE__` define (clean cutover; no
/// aliasing).
[<Emit("(typeof __TOOLUP_PLATFORM_SURFACES__ === 'string' ? __TOOLUP_PLATFORM_SURFACES__ : '')")>]
let platformSurfaces: string = jsNative

/// Phase 58 — paired with `ServerConfig.Notifications =
/// NoNotificationsExplicit`. When the consumer's Vite config defines
/// `__TOOLUP_NOTIFICATIONS_DISABLED__ = true`, the client's
/// `NotificationClient` skips EventSource instantiation entirely
/// — no `/api/notifications` request, no 404 retry loop, no console
/// warning. Absent / `false` keeps today's behaviour (EventSource
/// is created on first `subscribe`; if the server has no route mounted
/// the defensive 404-fallback in `NotificationClient.onError` is what
/// catches the silent-default).
[<Emit("(typeof __TOOLUP_NOTIFICATIONS_DISABLED__ === 'boolean' ? __TOOLUP_NOTIFICATIONS_DISABLED__ : false)")>]
let notificationsDisabledExplicitly: bool = jsNative