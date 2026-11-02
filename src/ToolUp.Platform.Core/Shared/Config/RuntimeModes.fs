// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of SDK.Shared.fs: metrics / webhook / audit-log /
// notification / static-path / rate-limit / CORS / SSE / auth-cookie /
// hardening / drift / readiness / rendering / asset / media / deploy-plane
// modes and their validation-adjacent records. Declaration order is
// unchanged from the monolith; same namespace, same names — only the file
// boundary moved.

/// Sink-level cardinality cap configuration. The
/// `MetricDefinition.Tags` allowlist is the structural defence
/// against caller-side cardinality explosions; this knob is the
/// hard ceiling on how many distinct `(tag-set)` combinations a
/// single metric can hold before subsequent combinations are
/// routed to a single overflow series tagged `_overflow=true`.
type MetricsSinkConfig = {
    /// Maximum distinct `(tag-set)` series per metric. New
    /// combinations beyond this ceiling fold into a single overflow
    /// series. The first overflow logs `Warn` once (with metric
    /// name + offending tag); subsequent overflows are silent so
    /// the warning itself doesn't blow up. Default 1000 — defensive
    /// for well-behaved metrics, large enough that legitimate
    /// per-team / per-route partitioning is unlikely to hit it.
    MaxSeriesPerMetric: int
    /// Per-metric overrides. A known-high-cardinality metric (e.g. a
    /// per-team usage counter on a 5000-tenant deployment) can raise
    /// its ceiling without raising the global default. Keys are
    /// post-namespace metric names (`toolup.foo.total`,
    /// `toolup.mymod.bar.total`).
    PerMetricMaxSeries: Map<string, int>
}

module MetricsSinkConfig =
    /// The shipped cardinality guard: 1000 series per metric, no
    /// per-metric override. Widen a single metric's ceiling with
    /// `PerMetricMaxSeries` rather than raising this default.
    let defaults: MetricsSinkConfig = {
        MaxSeriesPerMetric = 1000
        PerMetricMaxSeries = Map.empty
    }

/// Phase 9t — what `EventStoreAuditLog.Record` does when the
/// underlying `IEventStore.Write` (or the payload serialisation)
/// fails. Today's behaviour — catch, count
/// (`audit_write_failures_total`, Phase 114), log at `Warn`, and let
/// the user's action complete — leaves a silent hole in the audit
/// trail: fine for deployments that treat audit as advisory, not
/// acceptable under SOC 2 / HIPAA / GDPR Art. 30 / SOX continuous-
/// audit obligations. The policy makes the trade explicit. Env:
/// `TOOLUP_AUDIT_FAILURE_POLICY=log|refuse|degrade`.
type AuditFailurePolicy =
    /// Default — the pre-9t behaviour byte-for-byte: count + `Warn`,
    /// action completes, audit record lost.
    | LogAndContinue
    /// Compliance-grade: the failure propagates as
    /// `AuditWriteRefusedException`, so the action surfaces a 500
    /// ("audit unavailable") rather than committing un-audited work.
    | RefuseAction
    /// Availability-grade: the failed record spills to a bounded
    /// local fallback directory; `AuditFallbackReplayService`
    /// re-ingests it into the live `IEventStore` once writes recover.
    /// The action completes; the trail heals after the outage.
    | DegradeToFile

/// Selects how `compose` registers `INotificationChannel` and the
/// `/api/notifications` SSE route. Default:
/// `NotificationsAuto` — `compose` flips to `InMemoryNotifications`
/// whenever a feature that publishes notifications is active
/// (`JobScheduler <> NoJobScheduler`, `Mode = MultiTeam`, or a
/// companion like `composeWithAI` / `composeWithRAG` is wrapping),
/// otherwise `NoNotifications`. Apps can override explicitly to pin
/// behaviour or to swap in a distributed backend.
type NotificationMode =
    /// `compose` decides based on the rest of the config and on
    /// companion wrappings (`composeWithAI` / `composeWithRAG` add
    /// themselves to `extensions.NotificationConsumers`). Default —
    /// the lightweight shape gets `NoNotifications`; deployments
    /// running jobs / MultiTeam / AI / RAG get `InMemoryNotifications`
    /// without any extra config.
    | NotificationsAuto
    /// No `INotificationChannel` is registered, no `/api/notifications`
    /// SSE route is mounted. Force this mode when you want to suppress
    /// the SSE endpoint even though a notification-publishing feature
    /// is active (e.g. a deployment that uses webhooks for fan-out
    /// instead).
    | NoNotifications
    /// Phase 58 — like `NoNotifications` but emits a bundle-constant
    /// signal to the client that the absence is deliberate. The
    /// client's `NotificationClient` reads
    /// that bundle constant and skips
    /// EventSource instantiation entirely (no 404 retry loop, no
    /// console warnings). Use this mode in serverless / public-utility
    /// deployments where SSE is fundamentally inappropriate and the
    /// silent 404 loop would otherwise burn client CPU / bandwidth.
    | NoNotificationsExplicit
    /// `InMemoryNotificationChannel` is registered, `/api/notifications`
    /// SSE route is mounted. Single-process fan-out — multi-instance
    /// deployments need a distributed backend.
    | InMemoryNotifications
    /// `RedisNotificationChannel` is registered against the supplied
    /// connection string; `/api/notifications` SSE route is mounted.
    /// Per-scope channel naming gives structural scope isolation at
    /// transport level. Requires the `src/NotificationChannels/Redis/`
    /// companion package.
    | RedisNotifications of connectionString: string

/// Helpers for `NotificationMode`. `resolve` answers the auto-detection
/// question that `compose` runs at composition time: given the user's
/// declared mode plus the rest of the relevant config (job scheduler,
/// platform mode, declared companion consumers), which `NotificationMode`
/// is actually in effect? Extracted so unit tests can pin the resolution
/// rules directly without spinning up a `WebApplicationBuilder`.
module NotificationMode =
    /// Resolve `NotificationsAuto` against the rest of the config.
    /// Other modes pass through unchanged. Rules:
    ///   - `JobScheduler <> NoJobScheduler` → publishes dead-letter
    ///     notifications, so notifications need to flow.
    ///   - `hasMultiTeamSwitcher` → membership-change events feed the
    ///     client team-switch reset path; the caller passes
    ///     `DeploymentConfig.hasMultiTeamSwitcher config`.
    ///   - Any consumer in `notificationConsumers` (typically
    ///     `composeWithAI` / `composeWithRAG` declaring themselves) →
    ///     publishes through the channel.
    /// Otherwise the lightweight default flips to `NoNotifications`.
    let resolve
        (declared: NotificationMode)
        (jobScheduler: JobSchedulerMode)
        (hasMultiTeamSwitcher: bool)
        (notificationConsumers: string list)
        : NotificationMode =
        match declared with
        | NotificationsAuto ->
            let needs =
                jobScheduler <> NoJobScheduler
                || hasMultiTeamSwitcher
                || not (List.isEmpty notificationConsumers)

            if needs then InMemoryNotifications else NoNotifications
        | other -> other

// `PageConfig` and `ModuleDefinition` previously lived here. They moved
// to `Client/SDK.ClientTypes.fs` when the `Icon` field changed type from
// `string` (URL path) to `ReactElement` (typed SVGR-imported component).
// `ReactElement` is Fable-only, so the records can no longer live in
// shared types. Server-side code never constructed `ModuleDefinition`
// anyway (only doc-comment references), so the move was safe.

// ─── Module name filter ────────────────────────────────────────────

/// Case- and whitespace-insensitive substring match against module
/// names. Shared by `ServerConfig.ModuleFilter` and
/// `ClientConfig.ModuleFilter` so single-module dev runs behave the
/// same on both sides.
module ModuleFilter =
    let private normalise (s: string) = s.ToLowerInvariant().Replace(" ", "")

    /// `true` when the filter is absent/blank or when `name` contains
    /// the filter as a case-insensitive substring (whitespace ignored).
    let matches (filter: string option) (name: string) : bool =
        match filter with
        | None -> true
        | Some f when String.IsNullOrWhiteSpace f -> true
        | Some f -> (normalise name).Contains(normalise f)

    /// Filter `items` by `nameOf >> matches filter`. An absent or blank
    /// filter keeps every item — `matches` already encodes that, so this
    /// is a single `List.filter` with no duplicated guard.
    let apply (filter: string option) (nameOf: 'a -> string) (items: 'a list) : 'a list =
        items |> List.filter (nameOf >> matches filter)

// ─── Server configuration ──────────────────────────────────────────

/// What to do when `ServerConfig.PublicPath` does not exist on disk at
/// startup. Saturn's `use_static` defaulted to a silent skip; the post-
/// SAFE composition pipeline makes this an explicit choice so production
/// can fail loudly on a missing static-asset deployment instead of
/// returning 404s for every SPA route.
type StaticPathBehaviour =
    /// Log a warning and skip `UseStaticFiles`. Backward-compatible
    /// default — appropriate for `dotnet run` development where the
    /// Vite dev server is serving assets and `deploy/public` is empty.
    | Warn
    /// Throw at startup with a clear error message. Production
    /// deployments should choose this so a misconfigured container or
    /// missing build artefact crashes the process instead of silently
    /// breaking the SPA.
    | RequireExist
    /// Skip `UseStaticFiles` without logging. Use when the deployment
    /// has no static assets at all (pure API server, asset CDN front-
    /// end, etc.) and the warning is noise.
    | SkipSilent

/// Phase 66 Stream C.3 (design §3.10 + D21). A single fixed-window
/// rate-limit policy: `PermitLimit` requests are allowed per
/// `WindowSeconds`; the next `QueueLimit` requests queue rather than
/// being rejected outright. Beyond that, the limiter returns `429 Too
/// Many Requests` with a `Retry-After` header.
///
/// **Divergence from the design's `Window: TimeSpan`.** The shipped
/// shape keeps `WindowSeconds: int` (the pre-C.3 field name) rather
/// than the design §3.10 `Window: TimeSpan`. `TimeSpan` is awkward in
/// a Core shared type (Fable surface), and keeping seconds-as-int
/// leaves env parsing, the middleware's `TimeSpan.FromSeconds`
/// conversion, the validators, and the existing tests unchanged. The
/// middleware converts to `TimeSpan` at the .NET rate-limiter boundary.
type RateLimitPolicy = {
    PermitLimit: int
    WindowSeconds: int
    QueueLimit: int
}

module RateLimitPolicy =
    /// Partition key for a subject under the fixed-window limiter. The
    /// partition is implied by subject kind: anonymous traffic
    /// partitions on client IP (the session id is client-minted and
    /// unbounded, so it can't be the limiter key), authenticated users
    /// on user id, team members on team id (one shared budget across a
    /// team's members), claim-bearers on token id.
    ///
    /// `clientIp` is passed in rather than read off the subject because
    /// `AnonymousSession` carries only a client-minted session id, not
    /// the remote IP — the middleware supplies the resolved remote IP.
    /// Pure (no `HttpContext` dependency) so the D.7 test pack can
    /// exercise every branch directly.
    let partitionFor (clientIp: string) (subject: Subject) : string =
        match subject with
        | AnonymousSession _ -> sprintf "ip:%s" clientIp
        | AuthenticatedUser uid -> sprintf "user:%s" uid
        | TeamMember(_, tid) -> sprintf "team:%s" tid
        | ClaimBearer claim -> sprintf "token:%s" claim.TokenId
        // Phase 1002 — a publication reader gets a budget of its own,
        // never a member's or a team's.
        | PublicationReader uid -> sprintf "reader:%s" uid

/// Phase 66 Stream C.3 (design §3.10 + D21) — per-subject-kind rate
/// limiting. `Default` applies to any subject kind without a `PerShape`
/// override; `PerShape` carries per-kind policies (e.g. a tight window
/// for `AnonymousKind`, a looser one for `UserKind`). A subject-kind
/// lookup that misses `PerShape` falls back to `Default`; when
/// `Default` is also `None`, that kind is unlimited.
///
/// **Default = no rate limiting.** `RateLimitConfig.none` (both
/// `Default = None` and `PerShape = Map.empty`) registers no limiter
/// at all — byte-for-byte the pre-C.3 `RateLimit = None` behaviour
/// (GP 11 backward-compatible default).
type RateLimitConfig = {
    /// Policy for any subject kind without a `PerShape` entry. `None` =
    /// no default limit (only `PerShape` kinds are limited).
    Default: RateLimitPolicy option
    /// Per-subject-kind policy overrides. A kind present here uses its
    /// policy; a kind absent falls back to `Default`.
    PerShape: Map<SubjectKind, RateLimitPolicy>
}

module RateLimitConfig =
    /// No rate limiting — no default, no per-shape overrides. The
    /// `ServerConfig` default; byte-for-byte the pre-C.3 pipeline (no
    /// `UseRateLimiter`, GP 11).
    let none: RateLimitConfig = { Default = None; PerShape = Map.empty }

    /// One policy for every subject kind. `PerShape` stays empty; every
    /// kind resolves to `policy` via the `Default` fallback.
    let uniform (policy: RateLimitPolicy) : RateLimitConfig = {
        Default = Some policy
        PerShape = Map.empty
    }

    /// Per-kind policies only, no default. Subject kinds absent from `m`
    /// are unlimited.
    let perShape (m: Map<SubjectKind, RateLimitPolicy>) : RateLimitConfig = { Default = None; PerShape = m }

    /// A default policy plus per-kind overrides. Kinds in `overrides`
    /// use their policy; all others fall back to `defaultPolicy`.
    let withOverrides
        (defaultPolicy: RateLimitPolicy)
        (overrides: Map<SubjectKind, RateLimitPolicy>)
        : RateLimitConfig =
        {
            Default = Some defaultPolicy
            PerShape = overrides
        }

    /// Resolve the policy for a subject kind: a `PerShape` entry wins,
    /// otherwise `Default`. `None` = that kind is unlimited.
    let policyFor (config: RateLimitConfig) (kind: SubjectKind) : RateLimitPolicy option =
        config.PerShape |> Map.tryFind kind |> Option.orElse config.Default

    /// `true` when this config would register a limiter at all — any
    /// default or any per-shape entry. `RateLimitConfig.none` returns
    /// `false` (the pre-C.3 "no `UseRateLimiter`" path).
    let isEnabled (config: RateLimitConfig) : bool =
        config.Default.IsSome || not (Map.isEmpty config.PerShape)

/// Typed CORS allowlist for the SDK's built-in CORS middleware.
/// Maps to `Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder`
/// — see `compose` for the registration. `Origins = ["*"]` is honoured
/// as `AllowAnyOrigin`; otherwise the listed origins become the explicit
/// allowlist. Same convention applies to `Methods` and `Headers`.
/// `AllowCredentials` cannot combine with wildcard origins (browsers
/// reject the combination); `compose` refuses to start — before any CORS
/// policy is registered — if the deployment sets both.
///
/// For CORS shapes that don't fit (per-route policies, dynamic origin
/// validation, vary-by-header), use `ServerApp.withPreMiddleware` and
/// register the policy by hand — `withPreMiddleware` runs before scope
/// resolution so OPTIONS preflight short-circuits cleanly.
type CorsConfig = {
    /// Origins to allow. `["*"]` = any origin (no credentials);
    /// explicit list otherwise. Default in `CorsConfig.permissive` is
    /// `["*"]`.
    Origins: string list
    /// HTTP methods to allow. `["*"]` = any. Default: `["GET"; "POST"; "OPTIONS"]`.
    Methods: string list
    /// Request headers to allow. `["*"]` = any. Default: `["*"]`.
    Headers: string list
    /// Whether the browser should send cookies / `Authorization`
    /// headers cross-origin. Cannot combine with wildcard origins.
    /// Default: `false`.
    AllowCredentials: bool
}

module CorsConfig =
    /// Wide-open CORS — any origin, any method, any header, no
    /// credentials. Suitable for public APIs and dev environments.
    let permissive: CorsConfig = {
        Origins = [ "*" ]
        Methods = [ "*" ]
        Headers = [ "*" ]
        AllowCredentials = false
    }

    /// Allowlist a specific list of origins with the typical
    /// credentialed-API shape (GET/POST/OPTIONS, any header,
    /// credentials enabled).
    let forOrigins (origins: string list) : CorsConfig = {
        Origins = origins
        Methods = [ "GET"; "POST"; "OPTIONS" ]
        Headers = [ "*" ]
        AllowCredentials = true
    }

/// How the SDK handles auth for SSE endpoints
/// (`/api/ai/events`, `/api/notifications`).
///
/// Browser `EventSource` cannot send custom request headers — only
/// cookies travel automatically — so a deployment using header-based
/// auth (`X-User-Id` / `Authorization: Bearer <jwt>`) needs special
/// handling for these endpoints.
///
/// `QueryParamFallback` (default): SSE endpoints are exempt from
/// `AuthEnforcementMiddleware`. The handlers fall back to the
/// `?userId=` query parameter for scope resolution. Convenient for
/// dev / Anonymous mode / `HeaderAuthProvider`-only deployments.
/// **Trade-off:** the userId is client-supplied with no
/// cryptographic proof; any browser can subscribe to any scopeId.
/// Acceptable for dev / single-user / trusted-network deployments.
///
/// `CookieRequired`: SSE endpoints go through the same
/// `AuthEnforcementMiddleware` as every other `/api/*` request.
/// Auth providers must read the JWT from a cookie (set via
/// `OidcAuthConfig.TokenLocation = Cookie name`). The client
/// `IAuthBridge` writes the JWT to `document.cookie` on sign-in so
/// EventSource handshakes carry it automatically. Production
/// recommendation for any deployment with multiple users on the
/// same network.
type SseAuthMode =
    | QueryParamFallback
    | CookieRequired

/// Phase 9j — opt-in HTTP-surface security hardening. Distinct from
/// the static `ServerConfig.SecurityPosture.SecurityHeaders` map: this mode drives
/// the *companion-aware* `CspMiddleware` (a `Content-Security-Policy`
/// auto-generated from every registered `ICspContributor`, correct
/// by construction) plus the `CsrfMiddleware` cross-origin POST
/// guard.
///
///   * `NoSecurityHardening` (default, GP 13) — neither middleware
///     stamps anything and the `/api/csrf-token` route is not
///     mounted. A deployment on the default retains today's
///     behaviour exactly.
///   * `DefaultSecurityHardening` — aggregated CSP header on every
///     response; CSRF token required on state-changing `/api/*`
///     requests. `style-src` keeps `'unsafe-inline'` so Feliz /
///     Tailwind dynamic styles keep working.
///   * `StrictSecurityHardening` — as Default, but `script-src` /
///     `style-src` drop `'unsafe-inline'` (deployment must serve
///     nonce-driven tags) and `object-src 'none'` +
///     `upgrade-insecure-requests` are added.
///
/// The opt-in `SecurityHardening` and the existing static
/// `SecurityHeaders` map compose: a per-route handler (or the
/// `SecurityHeaders` map) that already wrote a `Content-Security-Policy`
/// header wins — `CspMiddleware` only sets the header when absent.
type SecurityHardeningMode =
    | NoSecurityHardening
    | DefaultSecurityHardening
    | StrictSecurityHardening

/// Phase 53 — `IConversationStore` substrate opt-in. Promotes AI
/// assistant conversations from ephemeral `AIAssistantHandler` state
/// to a first-class persisted record so conversations are auditable,
/// recoverable, and re-runnable. Default `NoConversationStore` (GP 13):
/// `AIAssistantHandler` resolves `IConversationStore` as `null` and
/// runs its byte-for-byte pre-Phase-53 path — no audit emission, no
/// persistence cost.
///
/// `EnabledConversationStore { RetentionDays }` registers
/// `PersistentConversationStore` (built on `IDataObjectStore` —
/// inherits versioning + content-hash dedup + DSR erasure surface
/// for free), the `ConversationEraseHandler` (one of the
/// `IErasureHandler`s composed into the `ErasurePipeline` when
/// `DataSubjectRequests = Enabled`), and the five audit cases
/// under `_platform.conversations.*`. `RetentionDays` is recorded
/// in the deployment's config-drift snapshot but pruning is not
/// automatic — operators schedule pruning via a job under
/// `IJobScheduler`. (Today there's no first-party pruner; the
/// retention field is forward-looking + audit-visible.)
type ConversationStoreMode =
    | NoConversationStore
    | EnabledConversationStore of retentionDays: int

/// Phase 38 — compose-time content root for the public-rendering
/// companion. Absolute path to a directory holding `pages/`,
/// `news/`, `events/`, etc., plus an optional `redirects.csv`.
/// `ToolUp.PublicRendering.MarkdownContentLoader` walks this tree at
/// startup and (in dev) watches it for changes.
///
/// The type lives in `Platform.Core` so `ServerConfig.PublicRendering`
/// can reference it without introducing a Core→companion dependency.
/// The companion-side helpers `ToolUp.PublicRendering.Slug`,
/// `LayoutName`, `PublicPage`, etc. stay in the companion namespace.
type ContentRoot = ContentRoot of absolutePath: string

module ContentRoot =
    let value (ContentRoot p) = p

/// Phase 38 — `IPublicContentApi` substrate opt-in.
///
///   * `NoPublicRendering` (default, GP 13) — no `/sitemap.xml`
///     handler, no markdown-watcher hosted service, no redirect
///     middleware, no public-page catch-all. Strip-imports byte-for-
///     byte to the pre-Phase-38 behaviour; deployments that don't
///     opt in pay zero runtime cost.
///   * `EnabledPublicRendering root` — the
///     `ToolUp.PublicRendering` companion's `MarkdownContentLoader`
///     reads `root`, the catch-all `PublicPageHandler` mounts at
///     lowest precedence, the redirect map applies, and the sitemap
///     handler emits at `/sitemap.xml`.
type PublicRenderingMode =
    | NoPublicRendering
    | EnabledPublicRendering of root: ContentRoot

/// Phase 26 — deploy-plane substrate opt-in. Default `NoDeployPlane`
/// (GP 13) registers nothing: no `IBuildOrchestrator`, no
/// `IDeployPipeline`, no `ITenantFleet`, no `Tenant` entity
/// registration, no `_platform.build` / `_platform.deploy` event
/// emission. Byte-for-byte identical to pre-Phase-26 behaviour for any
/// deployment that does not opt in.
///
/// `SingleNodeDeployPlane` registers the three SDK-shipped defaults
/// (`JobSchedulerBuildOrchestrator` over `IJobScheduler`,
/// `DefaultDeployPipeline`, `EntityStoreTenantFleet`) plus the
/// `Tenant` entity. `IContainerScheduler` is **consumer-supplied** —
/// the SDK does not register a default. Operators wire a backend
/// (`DockerLocalContainerScheduler` is the dev-grade reference
/// companion; Fly Machines / K8s / CloudRun ship as downstream
/// cloud-specific companions). When `SingleNodeDeployPlane` is set
/// without an `IContainerScheduler` in DI, an `IConfigValidator` emits
/// a startup error.
///
/// **Dependencies.** `SingleNodeDeployPlane` requires
/// `JobScheduler = InProcessJobScheduler` (for the build orchestrator's
/// dispatch substrate) and `EntityStore = FeatureMode.Enabled` (for the
/// tenant catalog). A future config validator may enforce; for now the
/// composition root raises at construction time if either is missing.
///
/// A distributed companion (an Akka-cluster-sharded build orchestrator)
/// replaces the singletons via DI and adds new cases here without
/// changing existing consumers.
type DeployPlaneMode =
    /// No deploy-plane infrastructure registered. Default — keeps the
    /// SDK lean for deployments not running the Layer 3 deploy plane.
    | NoDeployPlane
    /// Single-node defaults registered:
    /// `JobSchedulerBuildOrchestrator` + `DefaultDeployPipeline` +
    /// `EntityStoreTenantFleet` + `Tenant` entity. Consumer supplies
    /// `IContainerScheduler` separately via DI.
    | SingleNodeDeployPlane