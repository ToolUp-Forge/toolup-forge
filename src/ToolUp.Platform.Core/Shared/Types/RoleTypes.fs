// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Role within a team. Determines what actions a member can perform.
type TeamRole =
    | Owner
    | Admin
    | Member

/// Platform-wide administrative role, distinct from `TeamRole`. Platform
/// Admins can change deployment-wide configuration (Platform Knowledge
/// Base content, runtime config knobs, encryption-key destruction)
/// independent of any team membership. A user can hold both
/// `PlatformRole.PlatformAdmin` and any `TeamRole` — they compose, not
/// exclude. The single-case DU is extensible (future cases for read-only
/// platform observers, billing admins, etc.) without breaking callers.
///
/// `RequireQualifiedAccess` keeps this distinct from `ModulePermission.Admin`
/// and `TeamRole.Admin` — three different "Admin" concepts share the
/// codebase.
[<RequireQualifiedAccess>]
type PlatformRole =
    /// Full platform-admin authority — can write to Platform Knowledge
    /// Base scope, mutate runtime configuration, destroy per-scope
    /// encryption keys, and any other deployment-wide admin operation.
    /// Bootstrapped via `TOOLUP_INITIAL_PLATFORM_ADMIN` env var on first
    /// startup; subsequent admins assigned by existing admins via
    /// `PlatformAdminApi.AssignPlatformAdmin`.
    | PlatformAdmin

/// Phase 568 — a module's declared **navigation-role gate**: the role a
/// caller must hold for that module's sidebar entry to render. Optional
/// on `ClientModule` / `ErasedModule` (declare with
/// `ClientModule.withNavRole`); absent means ungated, which is exactly
/// the pre-568 shape for every module that declares nothing (GP 11).
///
/// **Typed, rather than derived from the sidebar GROUP display name.**
/// The group-name convention this replaces had already drifted once: the
/// role filter matched the label `"Platform Admin"` while every shipped
/// SDK built-in declared `"Platform Management"`, so the admin rail
/// rendered for every authenticated caller until commit 4f.2. A group
/// name is presentational and gets renamed; a gate must not move when it
/// does. With the gate declared here, renaming a sidebar group changes
/// zero access behaviour.
///
/// Sits in `RoleTypes.fs` beside the two roles it is evaluated against —
/// `PlatformRole` and `TeamRole` — so the pure fold that reads it
/// (`SidebarVisibility.visible`, which depends on Core types only) can
/// state its contract without a client-tier reference.
///
/// `RequireQualifiedAccess` keeps `NavRole.PlatformAdminOnly` distinct
/// from `TeamCreationPolicy.PlatformAdminOnly` — a different question
/// (who may create a team) sharing this namespace and that case name.
///
/// GP 12 — the sidebar is UI shape only; the server-side per-route
/// guards remain the enforcement.
[<RequireQualifiedAccess>]
type NavRole =
    /// Renders only for callers holding `PlatformRole.PlatformAdmin`.
    /// The typed successor to "is this module's group in the
    /// platform-scoped admin set", and identical to it in effect — a
    /// non-admin never sees the entry, loaded role or not.
    | PlatformAdminOnly
    /// Renders for a caller whose role on the ACTIVE team is
    /// `TeamRole.Owner` or `TeamRole.Admin`, and for any
    /// `PlatformRole.PlatformAdmin` (the two compose — a platform admin
    /// reaching the team-management tools is the escape that unblocks a
    /// team-less admin). Hidden from a plain `TeamRole.Member`.
    ///
    /// **When the active team role is not known the entry RENDERS** — no
    /// active team picked, a deployment with no team scope at all, or the
    /// one-shot boot/team-switch load still in flight. Fail-open is the
    /// deliberate choice on both counts: the pre-568 shape for these
    /// entries was ungated, so an unresolved role must not silently strip
    /// a team Owner of their own management tools (GP 11), and the
    /// server-side Owner/Admin guards are the real boundary anyway
    /// (GP 12) — the same reasoning that leaves the RBAC stage
    /// admit-everything until its fetch resolves.
    | TeamOwnerAdmin