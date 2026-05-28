// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// A flag's current runtime value. Two shapes:
///   * `Bool` — straight boolean gates (rollouts, kill-switches).
///   * `Variant` — closed set of named strings with a declared default,
///     for A/B/n style experiments ("legacy" | "new" | "beta").
/// Reads on an unknown key fall back to the module's declared default
/// (see `FeatureFlag.DefaultValue`).
[<RequireQualifiedAccess>]
type FlagValue =
    | Bool of bool
    | Variant of options: string list * value: string

/// Declared surface for one flag. Modules list these at `register()`
/// time so the admin UI can render a form, typos are caught at read
/// time (unknown-key reads log a warning), and docs show ownership.
type FeatureFlag = {
    /// Stable dotted key. Module authors own their namespace
    /// (`skuanalysis.new-grid`, `platform.toast-centre`). The SDK
    /// never interprets the key — it's just a string-scoped lookup.
    Key: string
    /// Value returned when no scope has set this flag. Also drives the
    /// admin-UI form input type (`Bool` → checkbox, `Variant` → dropdown).
    DefaultValue: FlagValue
    /// Admin-UI description / help text.
    Description: string
    /// Team or person who owns the flag's lifecycle — shown in the
    /// admin UI so stale flags have an owner when it's time to retire
    /// them.
    Owner: string option
}

/// Scope a flag evaluation walks. The resolution order is
/// `User → Team → Platform → declared default`; missing layers are
/// skipped (Anonymous mode has no User/Team; Individual has no Team).
///
/// `FlagScope` is a DU, not a bare `string`, because scope-id collision
/// between team ids and user ids (both blob-named) would be a silent
/// team-isolation bug. The DU forces the store to disambiguate
/// at the type level.
[<RequireQualifiedAccess>]
type FlagScope =
    | User of userId: string
    | Team of teamId: string
    | Platform

module FlagScope =
    /// Blob-path suffix — `user-{id}` / `team-{id}` / `_platform`.
    /// Mirrors the `StorageScope.Container` convention so on-disk
    /// layouts stay consistent across stores.
    let slug =
        function
        | FlagScope.User id -> $"user-{id}"
        | FlagScope.Team id -> $"team-{id}"
        | FlagScope.Platform -> "_platform"

/// Discriminates between a flag being set and a flag being cleared in
/// a `FlagChangedPayload`. `Set` carries the new value; `Cleared` has
/// none — the flag reverts to the next layer of the scope walk.
///
/// `RequireQualifiedAccess` is mandatory: `Set` would otherwise shadow
/// the built-in `Set<'T>` collection constructor at call sites.
[<RequireQualifiedAccess>]
type FlagChangeAction =
    | Set of FlagValue
    | Cleared

/// Audit-event payload recorded in `IEventStore` whenever a feature
/// flag override is set or cleared. Serialised as JSON (via
/// `FableJsonConverter` server-side) and stored in
/// `ModuleEvent.Payload`. Event `ScopeId` matches the flag scope's
/// slug so team-scoped audit queries remain isolated (team
/// isolation). Platform-scope flag changes land in the reserved
/// `_platform` scope, visible to platform admins only.
type FlagChangedPayload = {
    /// Declared flag key that changed.
    Key: string
    /// Scope at which the change was made. Mirrors the event's
    /// `ScopeId` but preserved in the payload for downstream
    /// consumers that replay across scopes.
    Scope: FlagScope
    /// What happened — set (with the new value) or cleared.
    Action: FlagChangeAction
    /// `AccessContext.UserId` of the caller who made the change.
    /// Attribution is audit-only — the SDK does not gate future reads
    /// on `ChangedBy`.
    ChangedBy: string
}

module FeatureFlagEvents =
    /// Reserved source-module identifier used for `ModuleEvent.SourceModule`
    /// on feature-flag audit events. Namespaced under `_sdk` so it
    /// cannot collide with an app's registered module names.
    [<Literal>]
    let SourceModule = "_sdk.FeatureFlags"

    /// Canonical `ModuleEvent.EventType` value for flag changes.
    /// Downstream consumers match on this literal.
    [<Literal>]
    let FlagChanged = "FlagChanged"