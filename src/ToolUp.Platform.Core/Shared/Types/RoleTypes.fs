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