// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of SDK.Shared.fs: the Phase 11.G curated app-supplied
// overrides that `ServerConfig.fromEnv` composes on top of its env-derived
// baseline. Same namespace, same names — only the file boundary moved.

// ─── Phase 11.G — curated app-supplied overrides for `ServerConfig.fromEnv` ──

/// Curated app-supplied overrides that compose on top of
/// `ServerConfig.fromEnv`'s env-var-derived baseline. Each `Some`
/// wins over the env-derived value; each `None` lets `fromEnv`
/// use the env default. Reference-app posture knobs (webhooks,
/// audit log, default hardening) are pre-bundled in
/// `ServerConfigOverrides.referenceApp`.
type ServerConfigOverrides = {
    /// Override `ServerConfig.PublicPath`. Default
    /// `"deploy/public"` (per `ServerConfig.defaults`); reference
    /// apps typically set `"public"`.
    PublicPath: string option
    /// Phase 66 Stream A.2 — override `ServerConfig.Surfaces`.
    /// Reference posture: `Some Surfaces.individual` (matches the
    /// retiring `Mode = Individual` reference default). Consumers
    /// declaring mixed-mode override per their deployment.
    Surfaces: SurfaceProfile list option
    /// Override `ServerConfig.Webhooks`. Reference posture:
    /// `EnabledWebhooks`.
    Webhooks: WebhookMode option
    /// Override `ServerConfig.AuditLog`. Reference posture:
    /// `EnabledAuditLog`.
    AuditLog: AuditLogMode option
    /// Override `ServerConfig.SecurityHardening`. Reference
    /// posture: `DefaultSecurityHardening`.
    SecurityHardening: SecurityHardeningMode option
    /// Override `ServerConfig.SlowRequestThresholdOverrides` with
    /// app-supplied per-route ceilings (KB upload, AI inference,
    /// large file paths). Reference posture supplies its own map.
    SlowRequestThresholdOverrides: Map<string, TimeSpan> option
    /// Override `ServerConfig.EnableDevEndpoints`. Reference
    /// posture: `Some true` under `#if DEBUG`, `Some false`
    /// otherwise.
    EnableDevEndpoints: bool option
    /// Override `ServerConfig.AutoBootstrapDevAdmin`. Reference
    /// posture: `Some "dev-admin"` under `#if DEBUG`, omit
    /// otherwise.
    AutoBootstrapDevAdmin: string option
    /// Override `ServerConfig.IncludePlatformDefaults`. Consumers
    /// whose modules render no monetary values set `Some false`
    /// to drop the irrelevant `Platform Defaults` admin tab.
    IncludePlatformDefaults: bool option
    /// Override `ServerConfig.ShareTokenStore`. `None` (default) leaves
    /// the resolved value at `ServerConfig.defaults.ShareTokenStore`
    /// (`NoShareTokenStore`); deployments that issue signed share-links
    /// — publishable forms, magic-login links, public dashboards, or the
    /// auto-mounted `ITeamInviteApi` (whose impl hard-depends on
    /// `IShareTokenStore`) — set `Some EnabledShareTokenStore` here
    /// rather than patching the resolved `ServerConfig` record after
    /// `fromEnv`. The compose-time `ClaimBearer`-surface auto-promotion
    /// (`ComposeNotifications`) still applies on top of whatever this
    /// resolves to.
    ShareTokenStore: ShareTokenStoreMode option
}

module ServerConfigOverrides =
    let empty: ServerConfigOverrides = {
        PublicPath = None
        Surfaces = None
        Webhooks = None
        AuditLog = None
        SecurityHardening = None
        SlowRequestThresholdOverrides = None
        EnableDevEndpoints = None
        AutoBootstrapDevAdmin = None
        IncludePlatformDefaults = None
        ShareTokenStore = None
    }

    /// Reference-deployment posture — webhooks on, audit on,
    /// default security hardening on, single-shape Individual
    /// surface. Matches the reference composition root's bundled
    /// feature set.
    let referenceApp: ServerConfigOverrides = {
        empty with
            Surfaces = Some Surfaces.individual
            Webhooks = Some EnabledWebhooks
            AuditLog = Some EnabledAuditLog
            SecurityHardening = Some DefaultSecurityHardening
    }