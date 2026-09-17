// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of SDK.Shared.fs: the Phase 59/60 consent-management
// and ad-panel substrate types, the premium / platform-admin profile DUs and
// the serverless-host / process-profile / consent-store / ad-analytics modes
// that immediately precede `ServerConfig`. Same namespace, same names — only
// the file boundary moved.

// ─── Wave 10 — Phase 59 consent-management substrate types ────────

/// Categories a consumer might request consent for. Loosely mirrors
/// IAB TCF categories and Funding Choices' default category list.
/// `Necessary` is always granted (cannot be denied — strictly-
/// necessary cookies / first-party session). Others depend on
/// consumer + jurisdiction + CMP configuration.
type ConsentCategory =
    | Necessary
    | Functional
    | Analytics
    | Marketing
    | Personalisation
    | ThirdPartyEmbeds

/// Per-category consent decision. `NotYetDecided` is the
/// pre-banner-interaction state.
type ConsentDecision =
    | Granted
    | Denied
    | NotYetDecided

/// Current consent state — snapshot of which categories the user
/// has granted vs denied, with audit metadata. `ConsentVersion`
/// allows consumers to invalidate prior decisions on a CMP policy
/// change.
type ConsentState = {
    Granted: Set<ConsentCategory>
    Denied: Set<ConsentCategory>
    LastUpdatedAt: DateTimeOffset
    ConsentVersion: int
}

module ConsentState =
    /// Default state — only `Necessary` granted, everything else
    /// not yet decided. Appropriate for first-page-load before the
    /// CMP runs.
    let initial =
        let now = DateTimeOffset.UtcNow

        {
            Granted = Set.singleton Necessary
            Denied = Set.empty
            LastUpdatedAt = now
            ConsentVersion = 1
        }

    /// True when the given category is currently granted.
    let isGranted (category: ConsentCategory) (state: ConsentState) : bool = Set.contains category state.Granted

    /// True when every category in `required` is granted.
    let hasAll (required: ConsentCategory list) (state: ConsentState) : bool =
        required |> List.forall (fun c -> isGranted c state)

/// Server-side audit row recorded when `ServerConfig.ConsentAudit =
/// EnabledConsentAudit`. The anonymous-user id is the browser-local
/// UUID until [Phase 62](62-premium-claim-recognition.md) establishes
/// an authenticated-user seam for consent state.
type ConsentEvent = {
    AnonymousUserId: string
    Category: ConsentCategory
    Decision: ConsentDecision
    Timestamp: DateTimeOffset
    CmpProvider: string
}

// ─── Wave 10 — Phase 60 AdPanel substrate types ────────────────────

/// AdSense format DUs mirroring the documented `data-ad-format`
/// values. `Fluid` carries the layout key (for in-content layouts).
type AdFormat =
    | AdAuto
    | AdRectangle
    | AdVertical
    | AdHorizontal
    | AdFluid of layoutKey: string

/// Optional style hint applied to the `<ins>` element's `style`
/// attribute. AdSense's default is `display:block`; consumers may
/// override for fixed-size slots.
type AdStyleHint = { CssStyle: string }

/// Per-slot configuration consumed by the Feliz `<AdSlot>`
/// component. `AdClientId` is the publisher's `ca-pub-XXXX` id;
/// `SlotId` is the AdSense slot identifier minted in the AdSense
/// console.
type AdSlotConfig = {
    AdClientId: string
    SlotId: string
    Format: AdFormat
    Style: AdStyleHint option
}

/// Identity for the audit-side impression / click events.
type AdImpression = {
    SlotId: string
    AdClientId: string
    OccurredAt: DateTimeOffset
    PathAtImpression: string
}

type AdClick = {
    SlotId: string
    AdClientId: string
    OccurredAt: DateTimeOffset
    PathAtClick: string
    ClickToken: string
}

/// AdPanel composition mode — `ClientConfig.AdPanel`. Default
/// `NoAdPanel` strips every `<AdSlot>` render path (slot renders
/// empty fragment without loading AdSense JS). `EnabledAdPanel`
/// activates the substrate; per-slot ad units pick their config up
/// from `AdPanelConfig` and the consent gate runs against
/// `IConsentProvider` (Phase 59).
type AdPanelConfig = {
    DefaultAdClientId: string
    ConsentCategoriesRequired: ConsentCategory list
}

type AdPanelMode =
    | NoAdPanel
    | EnabledAdPanel of AdPanelConfig

// ─── Wave 10 — Phase 62 premium-claim substrate types ──────────────

/// Premium status the SDK reads from the active auth provider's
/// user-metadata. `NotPremium` is the default for anonymous +
/// non-premium-logged-in users.
type PremiumStatus =
    | NotPremium
    | Premium of grantedAt: DateTimeOffset * grantedBy: string * reason: string option

/// Top-level premium model. `AnonymousFirst` is the v1 shipping
/// case — anonymous-by-default with operator-granted premium status.
/// Self-serve / billing-driven models are future cases.
type PremiumModel = | AnonymousFirst

// ─── Wave 10 — Phase 61 PlatformAdmin profile types ────────────────

/// Standard `PlatformAdmin` widget bundle (today's set —
/// HealthMonitor, TeamAdmin, etc.) vs the public-utility bundle
/// (traffic dashboard, rate-limit log, ad-unit config, premium
/// users). The bundle controls which widgets the PlatformAdmin
/// module surfaces; non-applicable widgets auto-skip when their
/// substrate dependency is unwired.
type PlatformAdminProfile =
    | StandardPlatformAdminProfile
    | PublicUtilityPlatformAdminProfile

/// Phase 59 — declarative consent-provider selection visible to the
/// client `ClientConfig.ConsentProvider`. `NoConsentProvider`
/// (default) is the `NoOpConsentProvider` shape — `Necessary` always
/// granted, every other category `NotYetDecided`; appropriate for
/// deployments outside jurisdictions that require explicit consent.
/// `FundingChoicesConsent` wires Google Funding Choices via the
/// `data-ad-client` id (companion-Fable side ships the bootstrap).
/// `CustomConsentProvider` reserves the seam for third-party CMP
/// companions (`ToolUp.Consent.Quantcast`, `Cookiebot`, etc.) that
/// inject their own `IConsentProvider`.
type ConsentProviderMode =
    | NoConsentProvider
    | FundingChoicesConsent of adClientId: string
    | CustomConsentProvider of providerName: string

// ─── Wave 10 — public-utility substrate modes ──────────────────────

/// Phase 16 — host-model selection. Default `KestrelHost` runs the
/// standard long-running Kestrel server with every registered
/// `BackgroundService`. `ServerlessHost` opts the deployment into a
/// serverless-compatible composition: `compose` skips every
/// `BackgroundService` registration (job scheduler, webhook
/// dispatcher, transactional dispatcher, RAG ingestion service) and
/// returns a request/response pipeline a Functions / Lambda / GCF
/// adapter can drive. Apps composing the serverless host adapter
/// companion (e.g. `ToolUp.Hosts.AzureFunctions`) set this flag; apps
/// running under Kestrel leave it at the default.
type ServerlessHostMode =
    /// Default — long-running Kestrel + every registered
    /// `BackgroundService` runs.
    | KestrelHost
    /// Serverless-compatible composition. `compose` skips
    /// `BackgroundService` registrations; the host adapter drives the
    /// request pipeline per invocation. Pair with `JobScheduler =
    /// NoJobScheduler`, `Webhooks = NoWebhooks`, and `Notifications =
    /// NoNotificationsExplicit` for a clean serverless shape; an
    /// inbound-only deployment that wants jobs runs a separate worker
    /// silo with `ProcessProfile = WorkerOnly` against the same
    /// `IBlobStorage` / `IEventStore`.
    | ServerlessHost

/// Phase 16a — process-profile selection. Default `AllInOne` runs
/// every `IHostedService` and HTTP middleware in one binary
/// (today's behaviour). `WebOnly` skips every `BackgroundService` —
/// the silo serves `/api/*` only; jobs scheduled by requests are
/// picked up by the worker silo via the persistent `IJobStore`.
/// `WorkerOnly` skips HTTP middleware and `/api/*` routes —
/// `compose` returns a worker-only `IHostBuilder`; the silo runs the
/// scheduler / webhook dispatcher / RAG ingestion / transactional
/// dispatcher. `DispatcherOnly` runs only the outbound-side
/// dispatchers (transactional + webhook) — for deployments wanting
/// outbound-delivery isolation.
///
/// Coordination across silos relies on a distributed notification
/// channel (Phase 6e Redis) and Phase 9i `IDistributedLock` for
/// cross-silo single-leader concerns (cron tick, webhook retry
/// timer).
type ProcessProfile =
    /// Default. Web tier + every `BackgroundService` in one process.
    /// Single-binary deployments.
    | AllInOne
    /// HTTP middleware + handlers; no `BackgroundService` work.
    /// Multiple instances scale stateless; a separate `WorkerOnly`
    /// silo drains jobs.
    | WebOnly
    /// Background work only; no HTTP middleware, no `/api/*` routes.
    /// One instance unless paired with `IDistributedLock` for
    /// single-leader coordination.
    | WorkerOnly
    /// Only the transactional + webhook dispatchers; no scheduler,
    /// no RAG ingestion, no `/api/*`. Outbound-delivery isolation.
    | DispatcherOnly

/// Phase 56 — `IRateLimitStore` substrate opt-in. Default
/// `NoRateLimitStore` strips the entire inbound rate-limit middleware
/// + `IRateLimitStore` registration. `InMemoryRateLimitStore`
/// activates the single-instance default — concurrent-dictionary +
/// per-key TTL eviction; appropriate for Kestrel single-instance
/// dev/test. External-store variants (Azure Table Storage / Redis /
/// Cosmos / DynamoDB) ship as `ToolUp.RateLimit.<store>` sub-
/// companion packages that register their own `IRateLimitStore`
/// against the seam.
type RateLimitStoreMode =
    /// No `IRateLimitStore` registered; the new
    /// `RateLimitMiddleware` is not mounted. Default.
    | NoRateLimitStore
    /// In-memory single-instance store. Dev / Kestrel-default.
    /// Single-instance only — multi-instance deployments share
    /// counts only via an external store.
    | InMemoryRateLimitStore
    /// Operator-supplied external `IRateLimitStore` implementation
    /// (registered as a singleton in DI by the companion's
    /// composition extension).
    | ExternalRateLimitStore

/// Phase 59 — server-side consent-audit opt-in. Default
/// `NoConsentAudit` strips the `/api/_platform/consent-audit`
/// endpoint and no `ConsentEvent` is persisted server-side; client-
/// side consent state still works (lives in the browser via
/// `IConsentProvider`). `EnabledConsentAudit` mounts the endpoint
/// and lands events via the configured `IAuditLog` for deployments
/// needing demonstrable evidence of consent decisions.
type ConsentAuditMode =
    | NoConsentAudit
    | EnabledConsentAudit

/// Phase 159 — server-side durable per-subject consent-state store
/// opt-in. Default `NoConsentStateStore` registers nothing — consent
/// state lives only in the browser via `IConsentProvider` (Phase 59),
/// byte-for-byte unchanged (GP 13). `InMemoryConsentStateStore`
/// registers the single-instance dev store (does NOT survive restart).
/// `EntityBackedConsentStateStore` registers the durable production
/// store over `IEntityStore` — requires `EntityStore =
/// EnabledEntityStore` (the compose path prepends the `ConsentRecord`
/// entity registration automatically). Distinct from `ConsentAudit`
/// (which mounts the client-event audit endpoint); this is the
/// authoritative server-side read-back store.
type ConsentStateStoreMode =
    | NoConsentStateStore
    | InMemoryConsentStateStore
    | EntityBackedConsentStateStore

/// Phase 60 — server-side ad-analytics opt-in. Default
/// `NoAdAnalytics` strips the `/api/_platform/ads/analytics`
/// endpoint. `EnabledAdAnalytics` mounts the endpoint and lands
/// `AdImpression` / `AdClick` audit events via `IAuditLog`. Distinct
/// from the client-side `ClientConfig.AdPanel` mode that controls
/// whether `<AdSlot>` Feliz components render at all.
type AdAnalyticsMode =
    | NoAdAnalytics
    | EnabledAdAnalytics