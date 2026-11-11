// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Text
open System.Text.RegularExpressions

/// The migration a codemod rule implements — cited on every rewrite and finding.
[<RequireQualifiedAccess>]
type CodemodMigration =
    /// `Fable.Remoting.*` → `ToolUp.Remoting.*`, `Elmish` → `ToolUp.Elmish`.
    | Remoting
    /// `PlatformMode` → `Subject` / `SurfaceProfile` / `SurfaceRequirement`.
    | Surfaces
    /// Package-id renames (Tier 2) and interface-shape renames (Tier 3).
    | ApiStability
    /// The seven 0.x deprecations removed at the 1.0 cut.
    | Deprecations
    /// The per-built-in client mode types become one
    /// `SlotFill`, and `ClientConfig`'s per-built-in fields become the
    /// `Slots` map.
    | ShellSlots
    /// The string-shaped agent-loop entry is removed, and the
    /// history window moves into the loop's limits.
    | AgentLoop
    /// `IDataSource` declares what a connector can do as a
    /// `Capabilities` record.
    | Capabilities
    /// `IModelFitProvider`, `IModelRegistry` and `IExternalComputeDispatcher`
    /// declare what an implementation can do as a `Capabilities` record.
    | ModelCapabilities
    /// The caller-less remoting members removed with no mechanical replacement.
    | RemotingLeftovers
    /// `IJobScheduler.Schedule` answers `ScheduleOutcome`
    /// (`Scheduled` / `Coalesced`) and `TriggerOnce` refuses with a typed
    /// `TriggerError`.
    | JobOutcomes
    /// `IFactStore` and the fact-tier seams take a `ResolvedScope` only, and
    /// every fact-store member answers `Result<_, FactStoreError>`.
    | OneScopeForm
    /// Every hosted service declares a `Placement` through
    /// `Hosting.register`, and the in-process scheduler and the webhook
    /// dispatcher are each built by one factory over an options record.
    | BackgroundPlacement
    /// A companion root is an options record composed onto a `ServerApp`
    /// with one `withX options` entry point, not a record wrapping it.
    | CompanionOptions
    /// `ServerConfig` is nested by concern (`SecurityPosture`,
    /// `RiskAcknowledgements`, `MultiInstance`, `Observability`, `Stores`)
    /// and every payload-free two-case `*Mode` is the one `FeatureMode`.
    | NestedConfig
    /// The server tier's namespaces name the part a file is compiled
    /// into (`ToolUp.Platform.Infra` / `.Config` / `.Scope` / `.Jobs` /
    /// `.Audit`, the umbrella's `.Compose` and `.Files`), and a consumer's
    /// opens are derived from the names it uses. Table-driven, so it runs
    /// only when named (`--phase 1198`), never under the default
    /// every-migration pass.
    | SubsystemNamespaces
    /// `UserId` / `TeamId` are typed at every store and service seam, in
    /// `ScopeKey`'s payloads and in the Core records off the wire.
    | TypedIdentity
    /// The request pipeline is one list of stages with streaming inside it:
    /// `InvocationResult` is gone, the fork's unions are qualified, and a
    /// streaming method may carry the pre-flight markers.
    | RequestPipeline
    /// The remoting rate-limit store is `IRemotingRateLimitStore`, both
    /// store interfaces answer `Admission`, and a `RouteLimit` declares its
    /// `StorePosture`.
    | RateLimitAdmission
    /// The client's one async state and memo primitives: `Prefetch`'s stall
    /// reporter is removed.
    | RemoteAndMemo
    /// A shipped AI connector is a `Connector` record plus `create*`
    /// helpers over the one `ProviderCall.send`; `StopReason` is a union
    /// and `RetryPolicy` carries a `RateLimitFloor`.
    | ConnectorKernel
    /// The infrastructure seams report failure as data: `IEventStore.Write`,
    /// the `IJobStore` writes, `IJobScheduler.Cancel` / `Disable` /
    /// `Enable` and `IWebhookDispatcher.Dispatch` answer a `Result`, and a
    /// live-session handler is asynchronous.
    | FailureAsData
    /// Audit failure is data at the seam: `IAuditLog.Record` answers
    /// `Result<unit, AuditWriteError>`, `IAuditLog option` and the `schedule`
    /// parameters are gone, and `IPermissionStore`'s reads answer a `Result`.
    | AuditFailureAsData
    /// The client shell hosts every module through one function over
    /// `Model.Hosts`, and the boot sources are one table: `ModuleStates`
    /// and `ResetCounters` are `Hosts`, and the two first-init gate fields
    /// are `AwaitingFirstInit`.
    | ModuleHost
    /// Audit events are an open registry: a companion's events left Core's
    /// `AuditEvent` for the companion's own family, and an archive is read
    /// with the audit options.
    | OpenAuditRegistry
    /// `Data.table` takes typed `Column<'row>` values in place of header
    /// strings and pre-rendered element rows.
    | AdminPageKit
    /// The three in-process retrieval stores share one snapshot index: the
    /// HNSW store's own unreadable-snapshot exception is the one
    /// `ToolUp.Platform.Infra.RagScopeSnapshotUnreadableException`.
    | SnapshotIndex
    /// An access context's module permissions are a closed type with a
    /// stated `Unrestricted`, and a request with no resolved principal is
    /// served a context that grants nothing.
    | AccessContextRefuses
    /// Ingestion is an attempt advanced through one `IIngestionAttemptStore`:
    /// the Data Manager observer and the knowledge base's status helpers and
    /// `MarkIngestionFailed` closure are gone.
    | IngestionAttempt
    /// `ClientConfig` is a record of concern records (`Chrome`, `Auth`,
    /// `PublicUtility`, `Locale`, `Shell`, `Composition` beside `Slots`), its
    /// on/off unions are `FeatureMode` or `ChromeFill`, the AI assistant
    /// family is three slot fills, and the `ClerkAuthUI` case is gone.
    | ClientConfigByConcern
    /// One resolver (`ISubjectResolver` + `StorageScopeDerivation`; the
    /// `IStorageScopeResolver` family is gone), one team-authority gate, the
    /// permission write chain built over the principal, one `LifecycleRun`
    /// with three entry points, and the session file stores as a service.
    | OneResolverOneGate
    /// The knowledge base's policies are one `KnowledgePolicies` record (the
    /// three single-flag records, the eight `KnowledgeApiDeps` policy fields
    /// and the DOCX setters go), its index read is a `Result`, the RAG
    /// validators are functions, and the dead RAG surface is deleted.
    | KnowledgePolicies
    /// A module's view is `'Model -> RenderContext<'Msg> -> PageContent`
    /// (`withFullWidthView` and the tuple view are gone), the toolkit's
    /// formatters take the caller's locale, and `Layout.AppShell` is the
    /// one shell entry, over an `AppShellProps` record.
    | OneViewContract
    /// One budget vocabulary: the compute budget's period, denial and usage
    /// twins and their four adapters are the seam's `BudgetPeriod`,
    /// `BudgetDenial` and `BudgetUsage`; the AI budgets declare a
    /// `BudgetPosture`, and the task-status registry is a DI singleton.
    | OneBudgetVocabulary

/// Helpers over `CodemodMigration`.
module CodemodMigration =
    /// The migration doc a finding cites, repo-relative under `docs/migrations/`.
    let doc migration =
        match migration with
        | CodemodMigration.Remoting -> "docs/migrations/73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md"
        | CodemodMigration.Surfaces -> "docs/migrations/0.X.0-platform-mode-to-surfaces.md"
        | CodemodMigration.ApiStability -> "docs/migrations/11-C-5-public-api-stability-cluster.md"
        | CodemodMigration.Deprecations -> "docs/migrations/815-remove-open-deprecations.md"
        | CodemodMigration.ShellSlots -> "docs/migrations/879-shell-slots.md"
        | CodemodMigration.AgentLoop -> "docs/migrations/1077-redesign-f-turnoutcome-loopstate-and-loop-step.md"
        | CodemodMigration.Capabilities -> "docs/migrations/1095-redesign-h-deliveryplan-and-connector-capabilities.md"
        | CodemodMigration.RemotingLeftovers -> "docs/migrations/1053-redesign-b-defects-remoting.md"
        | CodemodMigration.JobOutcomes ->
            "docs/migrations/1066-redesign-d-schedulerpolicy-coalesced-and-joblifecycle-transition.md"
        | CodemodMigration.ModelCapabilities ->
            "docs/migrations/1094-redesign-h-factscompose-options-modelling-dedup-and-fact-tools.md"
        | CodemodMigration.OneScopeForm -> "docs/migrations/1157-one-scope-form.md"
        | CodemodMigration.BackgroundPlacement ->
            "docs/migrations/1067-redesign-d-hosting-register-placement-and-options-records.md"
        | CodemodMigration.CompanionOptions -> "docs/migrations/876-composition-contributions.md"
        | CodemodMigration.NestedConfig -> "docs/migrations/877-nested-server-config.md"
        | CodemodMigration.SubsystemNamespaces -> "docs/migrations/1198-subsystem-namespaces.md"
        | CodemodMigration.TypedIdentity -> "docs/migrations/878-typed-identity.md"
        | CodemodMigration.RequestPipeline ->
            "docs/migrations/1056-redesign-b-request-pipeline-as-stages-with-streaming-inside.md"
        | CodemodMigration.RateLimitAdmission -> "docs/migrations/1068-redesign-d-rate-limit-unification.md"
        | CodemodMigration.RemoteAndMemo -> "docs/migrations/1070-redesign-e-remote-and-memo-primitives.md"
        | CodemodMigration.ConnectorKernel -> "docs/migrations/1079-redesign-f-the-connector-kernel.md"
        | CodemodMigration.FailureAsData -> "docs/migrations/873-portable-infrastructure-interfaces.md"
        | CodemodMigration.AuditFailureAsData ->
            "docs/migrations/1062-redesign-c-audit-failure-as-data-and-permission-store-result.md"
        | CodemodMigration.ModuleHost -> "docs/migrations/1071-redesign-e-bootsources-table-and-initmodule-by-id.md"
        | CodemodMigration.OpenAuditRegistry -> "docs/migrations/874-open-audit-event-registry.md"
        | CodemodMigration.AdminPageKit -> "docs/migrations/1073-redesign-e-admin-page-kit-and-sub-models.md"
        | CodemodMigration.SnapshotIndex ->
            "docs/migrations/1084-redesign-g-scopedsnapshotindex-under-the-three-stores.md"
        | CodemodMigration.AccessContextRefuses -> "docs/migrations/882-access-context-refuses.md"
        | CodemodMigration.IngestionAttempt -> "docs/migrations/1085-redesign-g-ingestion-attempt-and-its-lattice.md"
        | CodemodMigration.ClientConfigByConcern ->
            "docs/migrations/1074-redesign-e-clientconfig-by-concern-and-the-ai-family-as-slots.md"
        | CodemodMigration.OneResolverOneGate ->
            "docs/migrations/1063-redesign-c-one-resolver-teamauthority-lifecyclerun-sessionfilestores.md"
        | CodemodMigration.KnowledgePolicies ->
            "docs/migrations/1087-redesign-g-knowledge-policies-the-hook-and-the-deletions.md"
        | CodemodMigration.OneViewContract ->
            "docs/migrations/1075-redesign-e-one-view-contract-toolkit-locale-and-appshell.md"
        | CodemodMigration.OneBudgetVocabulary ->
            "docs/migrations/1081-redesign-f-budget-posture-and-the-compute-twins-retired.md"

    /// The phase label the renderings print.
    let label migration =
        match migration with
        | CodemodMigration.Remoting -> "Phase 73"
        | CodemodMigration.Surfaces -> "Phase 66"
        | CodemodMigration.ApiStability -> "Phase 11.C.5"
        | CodemodMigration.Deprecations -> "Phase 815"
        | CodemodMigration.ShellSlots -> "Phase 879"
        | CodemodMigration.AgentLoop -> "Phase 1077"
        | CodemodMigration.Capabilities -> "Phase 1095"
        | CodemodMigration.JobOutcomes -> "Phase 1066"
        | CodemodMigration.ModelCapabilities -> "Phase 1094"
        | CodemodMigration.RemotingLeftovers -> "Phase 1053"
        | CodemodMigration.OneScopeForm -> "Phase 1157"
        | CodemodMigration.BackgroundPlacement -> "Phase 1067"
        | CodemodMigration.CompanionOptions -> "Phase 876"
        | CodemodMigration.NestedConfig -> "Phase 877"
        | CodemodMigration.SubsystemNamespaces -> "Phase 1198"
        | CodemodMigration.TypedIdentity -> "Phase 878"
        | CodemodMigration.RequestPipeline -> "Phase 1056"
        | CodemodMigration.RateLimitAdmission -> "Phase 1068"
        | CodemodMigration.RemoteAndMemo -> "Phase 1070"
        | CodemodMigration.ConnectorKernel -> "Phase 1079"
        | CodemodMigration.FailureAsData -> "Phase 873"
        | CodemodMigration.AuditFailureAsData -> "Phase 1062"
        | CodemodMigration.ModuleHost -> "Phase 1071"
        | CodemodMigration.OpenAuditRegistry -> "Phase 874"
        | CodemodMigration.AdminPageKit -> "Phase 1073"
        | CodemodMigration.SnapshotIndex -> "Phase 1084"
        | CodemodMigration.AccessContextRefuses -> "Phase 882"
        | CodemodMigration.IngestionAttempt -> "Phase 1085"
        | CodemodMigration.ClientConfigByConcern -> "Phase 1074"
        | CodemodMigration.OneResolverOneGate -> "Phase 1063"
        | CodemodMigration.KnowledgePolicies -> "Phase 1087"
        | CodemodMigration.OneViewContract -> "Phase 1075"
        | CodemodMigration.OneBudgetVocabulary -> "Phase 1081"

/// Which files a rule applies to. The Phase 11.C.5 Tier 2 renames are
/// PACKAGE ids — they live in `.fsproj` / `Directory.Packages.props` —
/// and the F# namespaces inside those packages deliberately did not move,
/// so a package-id rule over a `.fs` file would rewrite a namespace that
/// still exists. The two classes never share a rule.
[<RequireQualifiedAccess>]
type CodemodFileClass =
    /// `.fs` / `.fsx` — namespace, type, field and symbol rules.
    | Source
    /// `.fsproj` / `.props` / `.targets` — package-id rules.
    | Project

/// A deterministic, context-free rewrite: one regex, one replacement,
/// applied per line. `Rewrite` receives the match and returns the
/// replacement text, so a rule can map a captured token through a table
/// (`Individual` → `Surfaces.individual`) rather than only substitute it.
type CodemodRewriteRule = {
    /// Stable id, printed on every rewrite (`73-remoting-namespace`).
    Id: string
    /// The migration the rule implements.
    Migration: CodemodMigration
    /// The file class the rule applies to.
    Applies: CodemodFileClass
    /// The pattern, matched per line.
    Pattern: Regex
    /// A line the rule must leave alone even where `Pattern` matches
    /// (an `Elmish.` qualified-access rewrite must not touch a consumer's
    /// own `namespace Elmish.X` declaration).
    Unless: Regex option
    /// The replacement for one match.
    Rewrite: Match -> string
    /// One line describing the rewrite, for the migration doc and the report.
    Summary: string
}

/// A site the codemod REPORTS for human review and never rewrites: the
/// target depends on the surrounding code, so any mechanical choice
/// would be a guess.
type CodemodReviewRule = {
    /// Stable id, printed on every finding (`66-mode-match`).
    Id: string
    /// The migration whose doc section the guidance points at.
    Migration: CodemodMigration
    /// The file class the rule applies to.
    Applies: CodemodFileClass
    /// The pattern, matched per line of the REWRITTEN text.
    Pattern: Regex
    /// What to do at the site, and where in the migration doc to read.
    Guidance: string
}

/// One line the codemod changed.
type CodemodRewrite = {
    /// The rule that changed it.
    RuleId: string
    /// 1-based line number in the original file.
    Line: int
    /// The line before this rule ran.
    Before: string
    /// The line after this rule ran.
    After: string
}

/// One site reported for human review.
type CodemodFinding = {
    /// The review rule that matched.
    RuleId: string
    /// The migration whose doc the guidance cites.
    Migration: CodemodMigration
    /// 1-based line number in the (rewritten) file.
    Line: int
    /// The line's text, trimmed.
    Text: string
    /// What to do at the site.
    Guidance: string
}

/// The codemod's decision about one file.
type CodemodFileResult = {
    /// Absolute path.
    Path: string
    /// Path relative to the walked root, forward-slashed — the key the
    /// renderings and the golden-file pack use.
    RelativePath: string
    /// The file class the rules were selected by.
    Class: CodemodFileClass
    /// The text as read (BOM stripped; see `HasBom`).
    Original: string
    /// The text after every rewrite rule ran.
    Rewritten: string
    /// Whether the file opened with a UTF-8 BOM, so `apply` writes one back.
    HasBom: bool
    /// Every line a rewrite rule changed, in file order.
    Rewrites: CodemodRewrite list
    /// Every site a review rule matched, in file order.
    Findings: CodemodFinding list
} with

    /// Whether `apply` would write this file.
    member this.Changed = this.Original <> this.Rewritten

/// The codemod's decision about a tree — what `apply` writes and what
/// the report lists. Pure: computing a plan touches nothing.
type CodemodPlan = {
    /// The walked root, absolute.
    Root: string
    /// Every file the walk visited, in sorted relative-path order.
    Files: CodemodFileResult list
} with

    /// The files `apply` would write.
    member this.ChangedFiles = this.Files |> List.filter _.Changed

    /// Every finding across the tree, with its file.
    member this.AllFindings =
        this.Files
        |> List.collect (fun f -> f.Findings |> List.map (fun x -> f.RelativePath, x))

/// The consumer codemod for the Epoch-1 `0.x` breaking renames.
///
/// Three rename clusters shipped as prose migration docs only, so every
/// consumer hand-applied the same `open` / symbol edits: Phase 73's
/// `Fable.Remoting.*` → `ToolUp.Remoting.*` and `Elmish` → `ToolUp.Elmish`
/// namespace moves (`docs/migrations/73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md`),
/// Phase 66's `PlatformMode` → `Subject` / `SurfaceProfile` redesign
/// (`docs/migrations/0.X.0-platform-mode-to-surfaces.md`), and Phase
/// 11.C.5's package-id and interface renames
/// (`docs/migrations/11-C-5-public-api-stability-cluster.md`), and Phase
/// 815's removal of the seven 0.x deprecations at the 1.0 cut
/// (`docs/migrations/815-remove-open-deprecations.md`). This module
/// is the deciding half of the `Codemod` FAKE target `SDK.Build.fs`
/// registers (`dotnet run -- Codemod <dir> [--check]`): the rules as data,
/// the per-file rewrite, the walk, and the two renderings (diff and
/// findings). It takes no FAKE dependency and runs no process, so the
/// golden-file pack (CodemodTests, in the Build tests project) decides every rule
/// with no build, no tool restore and no clock.
///
/// **Deterministic or reported — never guessed.** A rule is a REWRITE only
/// where the mapping is 1:1 and context-free: an `open` of a namespace
/// that moved, a field that was renamed, a type that was consolidated
/// under a name with the same shape. Everything whose target depends on
/// the surrounding code — a `match ctx.Mode with` whose arms map onto the
/// `Subject` DU differently per site, a `PlatformMode` handler parameter
/// that simply drops, an `IAuditSink` whose contract changed shape — is a
/// REVIEW rule: the site is listed per file with the migration section to
/// read, and the bytes are left alone.
///
/// **Idempotent by construction.** Every rewrite's output matches no
/// rewrite's input (`ToolUp.Remoting.Server` is not `Fable.Remoting.*`;
/// `Surfaces = Surfaces.individual` carries no `Mode =`), so a second run
/// over migrated source plans zero rewrites. Review findings are computed
/// over the REWRITTEN text, so the report is the same on every run rather
/// than shrinking after the first — a consumer who hand-migrated already
/// gets the same list and no diff (GP 13).
///
/// **Line-local.** Rules never span a line break, and the text is split
/// on its own terminators and re-joined with them, so CRLF / LF files and
/// a UTF-8 BOM survive a rewrite byte-for-byte outside the changed lines.
///
/// This module: the rules, the per-file rewrite, the walk, `apply`, and the renderings.
module Codemod =

    // ── Rule construction ─────────────────────────────────────────────

    let private rx (pattern: string) =
        Regex(pattern, RegexOptions.CultureInvariant)

    let private rewrite id migration applies pattern replacement summary = {
        Id = id
        Migration = migration
        Applies = applies
        Pattern = rx pattern
        Unless = None
        Rewrite = (fun (m: Match) -> m.Result replacement)
        Summary = summary
    }

    let private review id migration applies pattern guidance = {
        Id = id
        Migration = migration
        Applies = applies
        Pattern = rx pattern
        Guidance = guidance
    }

    /// Phase 66 §1 / §2 — the `PlatformMode` case a `Mode =` assignment
    /// named, mapped to the pre-named `Surfaces` list with the same
    /// deployment shape. `AuthenticatedEphemeral` is the trial shape
    /// (authenticated, ephemeral storage), which the boot summary and
    /// the `Surfaces` module both spell `trial`.
    let private surfacesFor (platformModeCase: string) =
        match platformModeCase with
        | "Anonymous" -> "Surfaces.anonymous"
        | "AuthenticatedEphemeral" -> "Surfaces.trial"
        | "Individual" -> "Surfaces.individual"
        | "Team" -> "Surfaces.team"
        | "MultiTeam" -> "Surfaces.multiTeam"
        | other -> failwithf "Codemod: no Surfaces mapping for PlatformMode case %s" other

    // ── The shell-slot tables ─────────────────────────────

    /// The twenty `ClientConfig` fields that became `ClientConfig.Slots`
    /// fields of the same name. The mode cases of each are the field's
    /// name behind `No` / `Default` / `Configured` / `External` (`Enabled`
    /// for the Home module), with a `UI` suffix for the preference centre.
    let private slotFields =
        "HomeModule|DataManager|TeamManager|TeamConfig|WebhookAdmin|ServiceAccountAdmin|ExternalContactManager|NotificationPreferences|ModuleVisibilityAdmin|SessionSecurity|PermissionsAdmin|UsageDashboard|AuditViewer|DataIngestionAdmin|PlatformAdmin|HealthMonitor|ServiceStatusBoard|DataSubjectRequestAdmin|MigrationAdmin|CompositionInspector"

    /// The same, less the data manager — whose configured cases take a
    /// `DataManagerChoice` rather than a label, so they are reported.
    let private labelledSlotFields =
        "HomeModule|TeamManager|TeamConfig|WebhookAdmin|ServiceAccountAdmin|ExternalContactManager|NotificationPreferences|ModuleVisibilityAdmin|SessionSecurity|PermissionsAdmin|UsageDashboard|AuditViewer|DataIngestionAdmin|PlatformAdmin|HealthMonitor|ServiceStatusBoard|DataSubjectRequestAdmin|MigrationAdmin|CompositionInspector"

    /// The twenty `{ Name; Icon }` records that became `ModuleLabel` —
    /// one per labelled slot, plus the platform-users module's.
    let private labelRecords =
        "HomeModule|TeamManager|TeamConfig|WebhookAdmin|ServiceAccountAdmin|ExternalContactManager|NotificationPreferences|ModuleVisibilityAdmin|SessionSecurity|PermissionsAdmin|UsageDashboard|AuditViewer|DataIngestionAdmin|PlatformAdmin|HealthMonitor|ServiceStatusBoard|DataSubjectRequestAdmin|MigrationAdmin|CompositionInspector|PlatformUsers"

    // ── The Phase 877 tables ──────────────────────────────────────────

    /// The thirty-seven payload-free two-case `*Mode` unions that became
    /// `FeatureMode`, as (type, off case, on case). The table is the
    /// migration doc's mode map; a type that kept its own shape (a
    /// payload, a third case, a choice between substrates) is not here.
    let featureModes: (string * string * string) list = [
        "PlatformKnowledgeBaseMode", "NoPlatformKnowledgeBase", "EnabledPlatformKnowledgeBase"
        "LineageStoreMode", "NoLineageStore", "EnabledLineageStore"
        "LineageDurabilityMode", "NoDurableLineage", "EnabledDurableLineage"
        "JobProgressMode", "NoJobProgress", "EnabledJobProgress"
        "ShareTokenStoreMode", "NoShareTokenStore", "EnabledShareTokenStore"
        "DataIngestionMode", "NoDataIngestion", "EnabledDataIngestion"
        "ColumnMappingMode", "NoColumnMapping", "EnabledColumnMapping"
        "OAuthRefresherMode", "NoOAuthRefresher", "EnabledOAuthRefresher"
        "OAuth1aMode", "NoOAuth1a", "EnabledOAuth1a"
        "EntityStoreMode", "NoEntityStore", "EnabledEntityStore"
        "EntityOutboxMode", "NoEntityOutbox", "EnabledEntityOutbox"
        "EntityGraphProjectionMode", "NoEntityGraphProjection", "EnabledEntityGraphProjection"
        "ModelFittingMode", "NoModelFitting", "EnabledModelFitting"
        "ModelExecutionApiMode", "NoModelExecutionApi", "EnabledModelExecutionApi"
        "ExternalComputeMode", "NoExternalCompute", "CustomExternalCompute"
        "ComputeBudgetMode", "NoComputeBudget", "EnabledComputeBudget"
        "TelemetrySinkMode", "NoTelemetrySink", "CustomTelemetrySink"
        "UsageMeteringMode", "NoUsageMetering", "EnabledUsageMetering"
        "MetricsEndpointMode", "NoMetricsEndpoint", "EnabledMetricsEndpoint"
        "WebhookMode", "NoWebhooks", "EnabledWebhooks"
        "AuditLogMode", "NoAuditLog", "EnabledAuditLog"
        "AuthCookieIssuanceMode", "NoAuthCookieIssuance", "EnabledAuthCookieIssuance"
        "ConfigDriftDetectionMode", "NoConfigDriftDetection", "EnabledConfigDriftDetection"
        "RateLimiterMode", "NoRateLimiter", "EnabledRateLimiter"
        "SmokeTestMode", "NoSmokeTest", "EnabledSmokeTest"
        "AssetStoreMode", "NoAssetStore", "EnabledAssetStore"
        "MediaLibraryMode", "NoMediaLibrary", "EnabledMediaLibrary"
        "DeploymentReadinessMode", "NoReadinessReport", "EnabledReadinessReport"
        "DeploymentVerificationMode", "NoDeploymentVerification", "EnabledDeploymentVerification"
        "ConsentAuditMode", "NoConsentAudit", "EnabledConsentAudit"
        "AdAnalyticsMode", "NoAdAnalytics", "EnabledAdAnalytics"
        "PeerSubstrateMode", "NoPeerSubstrate", "EnabledPeerSubstrate"
        "FactStoreMode", "NoFactStore", "EnabledFactStore"
        "TenantLifecycleMode", "NoTenantLifecycle", "EnabledTenantLifecycle"
        "UserSchemaAuthoringMode", "NoUserSchemaAuthoring", "EnabledUserSchemaAuthoring"
        "ExternalContactStoreMode", "NoExternalContactStore", "EnabledExternalContactStore"
        "PresenceMode", "NoPresence", "EnabledPresence"
    ]

    /// The `ServerConfig` fields that moved into a concern record, as
    /// (concern, fields). The table is the migration doc's field map.
    let concernFields: (string * string list) list = [
        "SecurityPosture",
        [
            "RequireHttps"
            "TrustForwardedHeaders"
            "TrustedProxyCidrs"
            "SecurityHeaders"
            "SecurityHardening"
            "Cors"
            "SseAuthMode"
            "AuthCookieIssuance"
            "MaxRequestBodyBytes"
            "WebhookUrlAllowedHosts"
        ]
        "RiskAcknowledgements",
        [
            "AcceptForwardedHeadersFromAnyProxy"
            "AcceptHeaderAuthWhenAuthRequired"
            "AcceptPlaintextSecretsWhenAuthRequired"
            "AcceptInProcessSchedulerInMultiInstance"
            "AcceptInProcessIngestionInMultiInstance"
            "AcceptSharedEmbeddingCacheInTeamMode"
            "AcceptEphemeralRagIndex"
            "AcceptLocalEmbedderAtScale"
            "AcceptStickyRoutedAiInMultiInstance"
            "AcceptNoRateLimitWhenAuthRequired"
            "AcceptUnsignedPublishable"
            "AcceptQueryParamSseAuthWhenAuthRequired"
            "AcceptSameSiteOnlyCsrfWhenAuthRequired"
            "AcceptUnboundAudienceWhenAuthRequired"
            "AcceptInMemoryOAuthStateInMultiInstance"
            "AcceptInMemoryShareTokenRateLimiterInMultiInstance"
            "AcceptPendingInviteStoreInMultiInstance"
            "AcceptInviteByEmailWithoutDirectory"
            "AcceptEphemeralShareTokenKey"
            "AcceptAnonymousModeWithAI"
        ]
        "MultiInstance", [ "ReplicaCount"; "ProcessProfile"; "PeerRoutePrefixes"; "PeerSubstrate" ]
        "Observability",
        [
            "LogLevel"
            "TraceCategories"
            "HealthStateTracking"
            "MetricsEndpoint"
            "MetricsSink"
            "MetricsHistory"
            "TelemetrySink"
            "SlowRequestThreshold"
            "SlowRequestThresholdOverrides"
            "AlertRules"
            "LogStore"
            "DatadogReadback"
        ]
        "Stores",
        [
            "EventStore"
            "ResultStore"
            "ShareTokenStore"
            "ServiceAccounts"
            "EntityStore"
            "EntityOutbox"
            "GraphStore"
            "TimeSeriesStore"
            "Datasets"
            "SessionRegistry"
            "ConversationStore"
            "AssetStore"
            "RateLimitStore"
            "ConsentStateStore"
            "ExternalContactStore"
            "FactStore"
        ]
    ]

    /// Field → concern, over `concernFields`.
    let private concernOf: Map<string, string> =
        concernFields
        |> List.collect (fun (concern, fields) -> fields |> List.map (fun f -> f, concern))
        |> Map.ofList

    let private concernNames = concernFields |> List.map fst

    /// The moved fields whose NAME is also a label on other records a
    /// consumer is likely to hold (a deps record's `EventStore`, an
    /// overrides record's `SecurityHardening`): a `.Field` read on an
    /// arbitrary receiver cannot be proved to be a `ServerConfig` read,
    /// so it is rewritten only behind a receiver that names the config
    /// and reported otherwise. `DatadogReadback` is also a `ClientConfig`
    /// field, and a client shell reads it as `ctx.Config.DatadogReadback`,
    /// so it is never rewritten, only reported.
    let private sharedLabels =
        set [
            "EventStore"
            "EntityStore"
            "ResultStore"
            "Datasets"
            "MetricsSink"
            "SlowRequestThresholdOverrides"
            "DatadogReadback"
            "SecurityHardening"
            "ProcessProfile"
            "ShareTokenStore"
            "RateLimitStore"
            "LogLevel"
            "Cors"
        ]

    let private movedFields = concernOf |> Map.keys |> List.ofSeq
    let private alternation (names: string seq) = String.Join("|", names)

    /// A receiver that is a `ServerConfig` by name: the conventional
    /// bindings, `app.Config`, and the defaults value itself.
    let private configReceiver =
        @"(?:\b(?:config|cfg|serverConfig|conf|effective|effectiveConfig|finalConfig|baseConfig|merged|defaults)|\.Config|\.defaults)"

    // ── The deterministic rewrites ────────────────────────────────────

    /// Every rewrite rule, in application order. Ordering matters only
    /// where two rules could see one token; none do — the Phase 73 open
    /// rule and qualified-access rule are disjoint by construction (one
    /// anchors on `open`, the other excludes it via its lookbehind on
    /// whitespace-only prefixes), and no rule's output is another's input.
    let rewriteRules: CodemodRewriteRule list = [
        // Remoting. The five namespaces the fork carries. Any
        // OTHER `Fable.Remoting.X` (AspNetCore, DotnetClient, Suave, …)
        // has no ToolUp counterpart and is a review finding below.
        rewrite
            "73-remoting-namespace"
            CodemodMigration.Remoting
            CodemodFileClass.Source
            @"\bFable\.Remoting\.(Server|Client|Json|Giraffe|MsgPack)\b"
            "ToolUp.Remoting.$1"
            "`Fable.Remoting.{Server,Client,Json,Giraffe,MsgPack}` → `ToolUp.Remoting.*` (opens and fully-qualified references)"
        // Elmish opens. Exactly the three the runtime carries
        // (`Elmish`, `Elmish.React`, `Elmish.HMR`); `open Elmish.Navigation`
        // and the other upstream sub-packages are review findings.
        rewrite
            "73-elmish-open"
            CodemodMigration.Remoting
            CodemodFileClass.Source
            @"^(\s*open\s+)Elmish(\.React|\.HMR)?(?![\w.])"
            "${1}ToolUp.Elmish$2"
            "`open Elmish` / `open Elmish.React` / `open Elmish.HMR` → `open ToolUp.Elmish` / `.React` / `.HMR`"
        // Elmish qualified access (`Elmish.Program.mkProgram`,
        // `Elmish.Cmd.none`, `Elmish.React.Program.run`). The lookbehind
        // leaves `ToolUp.Elmish.` (already migrated), `Fable.Elmish` (the
        // upstream package name in prose) and string-literal mentions
        // alone; the `Unless` guard leaves a consumer's own
        // `namespace Elmish.X` / `module Elmish.X` declaration alone.
        {
            rewrite
                "73-elmish-qualified"
                CodemodMigration.Remoting
                CodemodFileClass.Source
                @"(?<![\w.'""`])Elmish\.(?=(Program|Cmd|Sub|React|HMR)\b)"
                "ToolUp.Elmish."
                "`Elmish.Program.X` / `Elmish.Cmd.X` / `Elmish.Sub.X` / `Elmish.React.X` / `Elmish.HMR.X` → `ToolUp.Elmish.…`" with
                Unless = Some(rx @"^\s*(namespace|module)\b")
        }
        // Phase 66 §3 — the five `Accept*InAuthenticatedMode` flags.
        rewrite
            "66-accept-flags"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\bAccept(HeaderAuth|PlaintextSecrets|NoRateLimit|QueryParamSseAuth|UnboundAudience)InAuthenticatedMode\b"
            "Accept$1WhenAuthRequired"
            "`Accept{HeaderAuth,PlaintextSecrets,NoRateLimit,QueryParamSseAuth,UnboundAudience}InAuthenticatedMode` → `Accept…WhenAuthRequired`"
        // Env var + Vite define. `TOOLUP_PLATFORM_MODE` inside
        // `__TOOLUP_PLATFORM_MODE__` is followed by `_`, which the
        // negative lookahead admits, so one rule covers both spellings.
        rewrite
            "66-platform-mode-env"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"TOOLUP_PLATFORM_MODE(?![A-Za-z0-9])"
            "TOOLUP_PLATFORM_SURFACES"
            "`TOOLUP_PLATFORM_MODE` → `TOOLUP_PLATFORM_SURFACES` (and the `__TOOLUP_PLATFORM_MODE__` Vite define)"
        rewrite
            "66-bundle-constant"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\bBundleConstants\.platformMode\b"
            "BundleConstants.platformSurfaces"
            "`BundleConstants.platformMode` → `BundleConstants.platformSurfaces`"
        // Phase 66 §8 — the zero-argument read renames 1:1; `configure`
        // changed its parameter type and is a review finding.
        rewrite
            "66-user-session-get-mode"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\bUserSession\.getMode\b"
            "UserSession.getSubjectKind"
            "`UserSession.getMode ()` → `UserSession.getSubjectKind ()`"
        // Phase 66 §1 / §2 — `Mode = <case>` in a ServerConfig / ClientConfig
        // record expression. The lookbehind excludes `ctx.Mode = Anonymous`
        // (an equality READ, whose replacement is a predicate helper and a
        // review finding), and the case name is what identifies the field
        // as the platform one rather than a consumer's own `Mode`.
        {
            Id = "66-mode-field"
            Migration = CodemodMigration.Surfaces
            Applies = CodemodFileClass.Source
            Pattern =
                rx
                    @"(?<![\w.])Mode\s*=\s*(?:PlatformMode\.)?(Anonymous|AuthenticatedEphemeral|Individual|Team|MultiTeam)\b(?!\s*->)"
            Unless = None
            Rewrite = (fun m -> "Surfaces = " + surfacesFor m.Groups[1].Value)
            Summary =
                "`Mode = Anonymous | AuthenticatedEphemeral | Individual | Team | MultiTeam` (ServerConfig / ClientConfig) → `Surfaces = Surfaces.anonymous | trial | individual | team | multiTeam`"
        }
        // Phase 11.C.5 Tier 3.2 — same field shape, one name.
        rewrite
            "11c5-audit-retry-policy"
            CodemodMigration.ApiStability
            CodemodFileClass.Source
            @"\bAuditReplicatorRetryPolicy\b"
            "RetryPolicy"
            "`AuditReplicatorRetryPolicy` → `RetryPolicy` (same `{ MaxAttempts; InitialBackoff; MaxBackoff }` shape; `.defaults` carries over)"
        // Phase 11.C.5 Tier 3.3 — the two literals with a single DU case.
        // `Push` needs a `PushVariant` and is a review finding.
        rewrite
            "11c5-sink-kind"
            CodemodMigration.ApiStability
            CodemodFileClass.Source
            @"\bNotificationKind\.SinkKind\.(Email|Sms)\b"
            "SinkKind.$1"
            "`NotificationKind.SinkKind.Email` / `.Sms` → `SinkKind.Email` / `SinkKind.Sms`"
        // Phase 11.C.5 Tier 2 — package ids, project files only.
        rewrite
            "11c5-package-audit-sinks"
            CodemodMigration.ApiStability
            CodemodFileClass.Project
            @"\bToolUp\.Platform\.AuditSinks\."
            "ToolUp.AuditSinks."
            "`ToolUp.Platform.AuditSinks.*` → `ToolUp.AuditSinks.*` (package ids in `.fsproj` / `Directory.Packages.props`)"
        rewrite
            "11c5-package-notification-channels"
            CodemodMigration.ApiStability
            CodemodFileClass.Project
            @"\bToolUp\.Platform\.NotificationChannels\."
            "ToolUp.NotificationChannels."
            "`ToolUp.Platform.NotificationChannels.*` → `ToolUp.NotificationChannels.*`"
        rewrite
            "11c5-package-metrics"
            CodemodMigration.ApiStability
            CodemodFileClass.Project
            @"\bToolUp\.Platform\.Metrics\.OpenTelemetry\b"
            "ToolUp.Metrics.OpenTelemetry"
            "`ToolUp.Platform.Metrics.OpenTelemetry` → `ToolUp.Metrics.OpenTelemetry`"
        rewrite
            "11c5-package-azure-blob"
            CodemodMigration.ApiStability
            CodemodFileClass.Project
            @"\bToolUp\.Storage\.Azure\b"
            "ToolUp.Storage.AzureBlob"
            "`ToolUp.Storage.Azure` → `ToolUp.Storage.AzureBlob`"
        rewrite
            "11c5-package-oidc-client"
            CodemodMigration.ApiStability
            CodemodFileClass.Project
            @"\bToolUp\.AuthProviders\.OidcClient\b"
            "ToolUp.AuthProviders.Oidc.Client"
            "`ToolUp.AuthProviders.OidcClient` → `ToolUp.AuthProviders.Oidc.Client`"
        // The AG Grid / AG Charts compat re-exports. The
        // module names are gone; every member they forwarded lives under
        // the standalone binding's module with the same name, so an
        // `open` or a qualified access moves 1:1. `\b` keeps
        // `ToolUp.Platform.AgGridEnterprise` / `.AgChartExport` — both
        // still real — out of reach.
        rewrite
            "815-aggrid-compat-module"
            CodemodMigration.Deprecations
            CodemodFileClass.Source
            @"\bToolUp\.Platform\.AgGrid\b"
            "Feliz.AgGrid"
            "`ToolUp.Platform.AgGrid` (the compat re-export) → `Feliz.AgGrid` (opens and qualified references)"
        rewrite
            "815-agchart-compat-module"
            CodemodMigration.Deprecations
            CodemodFileClass.Source
            @"\bToolUp\.Platform\.AgChart\b"
            "Feliz.AgCharts"
            "`ToolUp.Platform.AgChart` (the compat re-export) → `Feliz.AgCharts` (opens and qualified references)"
        // `Program.withErrorHandler onError` in its pipeline
        // form (a named callback, nothing after it but the next `|>` or
        // the line end). The upstream-shape callback composes onto the
        // structured reporter exactly as the removed shim did internally,
        // so the rewrite is the shim's own body. A lambda argument, or a
        // call with the program applied on the same line, is a review
        // finding below.
        rewrite
            "815-elmish-error-handler"
            CodemodMigration.Deprecations
            CodemodFileClass.Source
            @"\bProgram\.withErrorHandler\s+([A-Za-z_][\w'.]*)(?=\s*(\|>|$))"
            "Program.withErrorReporter (fun ctx -> $1 (ctx.Message, ctx.Exception))"
            "`Program.withErrorHandler onError` → `Program.withErrorReporter (fun ctx -> onError (ctx.Message, ctx.Exception))`"
        // `Program.withConsoleTrace` took nothing but the
        // program, so its replacement is a fixed expression: the
        // upstream `withTrace` hook, logging the same bounded reprs the
        // shim logged (`safeMsgRepr` — never the live msg/model object).
        rewrite
            "815-elmish-console-trace"
            CodemodMigration.Deprecations
            CodemodFileClass.Source
            @"\bProgram\.withConsoleTrace\b"
            "Program.withTrace (fun msg model _ -> printfn \"New message: %s -> updated state: %s\" (Program.safeMsgRepr msg) (Program.safeMsgRepr model))"
            "`Program.withConsoleTrace` → `Program.withTrace` with a console-logging callback over `Program.safeMsgRepr`"
        // A `ClientConfig` field set to one of its own mode
        // cases moves under `Slots` (a nested copy-and-update). The
        // lookahead ties the field to ITS cases, so a consumer-owned
        // field of the same name holding anything else — and the
        // `ClientConfigOverrides` field, which holds `Some …` — is left
        // alone. Runs before the case rules below, which then rewrite the
        // right-hand side.
        rewrite
            "879-slot-field"
            CodemodMigration.ShellSlots
            CodemodFileClass.Source
            (@"(?<![\w.])(?<field>"
             + slotFields
             + @")(?<eq>\s*=\s*)(?=(?:No|Default|Enabled|Configured|External)\k<field>(?:UI)?\b|(?:Configured)?MappingDataManager\b)")
            "Slots.${field}${eq}"
            "`TeamManager = NoTeamManager` → `Slots.TeamManager = NoTeamManager` (then the case rules) — each of the twenty per-built-in `ClientConfig` fields is a `ClientConfig.Slots` field of the same name"
        {
            Id = "879-slot-case-nullary"
            Migration = CodemodMigration.ShellSlots
            Applies = CodemodFileClass.Source
            Pattern = rx (@"(?<![\w])(?<case>No|Default|Enabled)(?:" + slotFields + @")(?:UI)?\b")
            Unless = None
            Rewrite =
                fun m ->
                    match m.Groups["case"].Value with
                    | "No" -> "SlotFill.Empty"
                    | _ -> "SlotFill.Default"
            Summary =
                "`NoTeamManager` → `SlotFill.Empty`, `DefaultTeamManager` / `EnabledHomeModule` → `SlotFill.Default` (every slot's off and built-in cases)"
        }
        rewrite
            "879-slot-case-configured"
            CodemodMigration.ShellSlots
            CodemodFileClass.Source
            (@"(?<![\w])Configured(?:" + labelledSlotFields + @")(?:UI)?\b")
            "SlotFill.Configured"
            "`ConfiguredTeamManager label` → `SlotFill.Configured label` (every labelled slot; the label is the same `{ Name; Icon }` shape, now `ModuleLabel`)"
        rewrite
            "879-slot-case-external"
            CodemodMigration.ShellSlots
            CodemodFileClass.Source
            (@"(?<![\w])External(?:" + slotFields + @")(?:UI)?\b")
            "SlotFill.External"
            "`ExternalTeamManager myModule` → `SlotFill.External myModule` (every slot, the data manager included)"
        // The bare mapping case only: `ConfiguredMappingDataManager` is
        // reported below (its argument moves inside an option), a
        // qualified read (`catalog.MappingDataManager`) is the message
        // catalog, and a field assignment is the catalog record's field.
        rewrite
            "879-column-mapping"
            CodemodMigration.ShellSlots
            CodemodFileClass.Source
            @"(?<![\w.])MappingDataManager\b(?!\s*=)(?!\.)"
            "(SlotFill.Configured(DataManagerChoice.ColumnMapping None))"
            "`MappingDataManager` → `(SlotFill.Configured(DataManagerChoice.ColumnMapping None))` (parenthesised, so it stays one argument after `Some`)"
        rewrite
            "879-module-label"
            CodemodMigration.ShellSlots
            CodemodFileClass.Source
            (@"(?<![\w])(?:" + labelRecords + @")Config\b")
            "ModuleLabel"
            "`TeamManagerConfig` (and the nineteen other `{ Name; Icon }` records) → `ModuleLabel`"
        // The history window moved into the loop's limits. A
        // literal window maps 1:1; any other expression is reported below.
        rewrite
            "1077-history-window-default"
            CodemodMigration.AgentLoop
            CodemodFileClass.Source
            @"\bMaxHistoryMessages\s*=\s*None\b"
            "Limits = AgentLoopLimits.defaults"
            "`MaxHistoryMessages = None` → `Limits = AgentLoopLimits.defaults`"
        rewrite
            "1077-history-window-literal"
            CodemodMigration.AgentLoop
            CodemodFileClass.Source
            @"\bMaxHistoryMessages\s*=\s*Some\s*(?:\(\s*(\d+)\s*\)|(\d+)\b)"
            "Limits = { AgentLoopLimits.defaults with HistoryWindow = $1$2 }"
            "`MaxHistoryMessages = Some n` → `Limits = { AgentLoopLimits.defaults with HistoryWindow = n }`"
        rewrite
            "1077-history-window-constant"
            CodemodMigration.AgentLoop
            CodemodFileClass.Source
            @"\bAIAssistantServerConfig\.DefaultMaxHistoryMessages\b"
            "AgentLoopLimits.DefaultHistoryWindow"
            "`AIAssistantServerConfig.DefaultMaxHistoryMessages` → `AgentLoopLimits.DefaultHistoryWindow`"
        // The watch seam moved beside `IDataSource`, so a
        // fully-qualified reference names its new namespace.
        rewrite
            "1095-watch-seam-namespace"
            CodemodMigration.Capabilities
            CodemodFileClass.Source
            @"\bToolUp\.DataSources\.Common\.(IWatchableFileSource|WatchedObject)\b"
            "ToolUp.Platform.$1"
            "`ToolUp.DataSources.Common.IWatchableFileSource` / `WatchedObject` → `ToolUp.Platform.…`"
        // `Schedule` answers a `ScheduleOutcome`, so the result type a
        // helper or an implementer declares for it moves with it. Only the
        // annotation moves: a site that reads the job id is reported below.
        rewrite
            "1066-schedule-result-type"
            CodemodMigration.JobOutcomes
            CodemodFileClass.Source
            @"\bResult<\s*(?:JobId|Guid)\s*,\s*ScheduleError\s*>"
            "Result<ScheduleOutcome, ScheduleError>"
            "`Result<JobId, ScheduleError>` (a `Schedule` answer) → `Result<ScheduleOutcome, ScheduleError>`"
        // An implementer that re-typed the platform's `TriggerOnce` refusals
        // answered with its exact sentences; each is one `TriggerError` case.
        rewrite
            "1066-trigger-error-not-found"
            CodemodMigration.JobOutcomes
            CodemodFileClass.Source
            @"\bError\s*\(?\s*\$""Job %A\{(\w+)\} not found in scope %s\{(\w+)\}""\s*\)?"
            "Error(TriggerError.JobNotFound($2, $1))"
            "`Error $\"Job %A{jobId} not found in scope %s{scopeId}\"` → `Error(TriggerError.JobNotFound(scopeId, jobId))`"
        rewrite
            "1066-trigger-error-cancelled"
            CodemodMigration.JobOutcomes
            CodemodFileClass.Source
            @"\bError\s*\(?\s*""Cannot trigger a cancelled job""\s*\)?"
            "Error TriggerError.JobCancelled"
            "`Error \"Cannot trigger a cancelled job\"` → `Error TriggerError.JobCancelled`"
        rewrite
            "1066-trigger-error-disabled"
            CodemodMigration.JobOutcomes
            CodemodFileClass.Source
            @"\bError\s*\(?\s*""Cannot trigger a disabled job; enable it first""\s*\)?"
            "Error TriggerError.JobDisabled"
            "`Error \"Cannot trigger a disabled job; enable it first\"` → `Error TriggerError.JobDisabled`"
        // A fact-store call handed `scope.ScopeId` already holds the resolved
        // scope: pass the scope itself. Only the members no other store
        // spells, so a string-keyed store's `Get(x.ScopeId, …)` is untouched.
        rewrite
            "1157-resolved-scope-id"
            CodemodMigration.OneScopeForm
            CodemodFileClass.Source
            @"\.(AssertBatch|QueryWithCompetition|QuerySupersessionChain|QueryPopulation)\(\s*([A-Za-z_][\w.]*)\.ScopeId\s*,"
            ".$1($2,"
            "`store.AssertBatch(scope.ScopeId, …)` (and `QueryWithCompetition` / `QuerySupersessionChain` / `QueryPopulation`) → `store.AssertBatch(scope, …)`: the caller already holds the resolved scope"
        rewrite
            "1157-render-offenders"
            CodemodMigration.OneScopeForm
            CodemodFileClass.Source
            @"\bFactWrite\.renderOffenders\s+(?:\([^()]*\)|[\w.]+)\s+"
            "FactStoreError.describe "
            "`FactWrite.renderOffenders total offenders` → `FactStoreError.describe refused` (`prepare` answers one `MalformedDrafts`)"
        rewrite
            "1157-same-container"
            CodemodMigration.OneScopeForm
            CodemodFileClass.Source
            @"\bCoverageNarrativeOptions\.sameContainer\b"
            "CoverageNarrativeOptions.everyScope"
            "`CoverageNarrativeOptions.sameContainer` → `CoverageNarrativeOptions.everyScope` (the narrative commits under the resolved scope's own container)"
        // The startup-registration shape moved with the placement rule.
        rewrite
            "1067-at-start"
            CodemodMigration.BackgroundPlacement
            CodemodFileClass.Source
            @"\bProcessProfileGate\.atStart\b"
            "Hosting.atStart"
            "`ProcessProfileGate.atStart` → `Hosting.atStart`"
        // Phase 877 — the thirty-seven two-case modes are one. The
        // qualified spelling (`AuditLogMode.NoAuditLog`) is consumed with
        // its case, so the type rule below never sees it.
        rewrite
            "877-feature-mode-off"
            CodemodMigration.NestedConfig
            CodemodFileClass.Source
            (sprintf
                @"\b(?:(?:%s)\.)?(?:%s)\b"
                (alternation (featureModes |> List.map (fun (t, _, _) -> t)))
                (alternation (featureModes |> List.map (fun (_, off, _) -> off))))
            "FeatureMode.Disabled"
            "`NoAuditLog` / `NoEntityStore` / … (the off case of every payload-free two-case mode) → `FeatureMode.Disabled`"
        rewrite
            "877-feature-mode-on"
            CodemodMigration.NestedConfig
            CodemodFileClass.Source
            (sprintf
                @"\b(?:(?:%s)\.)?(?:%s)\b"
                (alternation (featureModes |> List.map (fun (t, _, _) -> t)))
                (alternation (featureModes |> List.map (fun (_, _, on) -> on))))
            "FeatureMode.Enabled"
            "`EnabledAuditLog` / `EnabledEntityStore` / `CustomTelemetrySink` / … (the on case) → `FeatureMode.Enabled`"
        rewrite
            "877-feature-mode-type"
            CodemodMigration.NestedConfig
            CodemodFileClass.Source
            (sprintf @"\b(?:%s)\b" (alternation (featureModes |> List.map (fun (t, _, _) -> t))))
            "FeatureMode"
            "`FeatureMode` / `FeatureMode` / … (a type annotation on a retired mode) → `FeatureMode`"
        // A moved field read off a receiver that names the config.
        {
            rewrite
                "877-config-member"
                CodemodMigration.NestedConfig
                CodemodFileClass.Source
                (sprintf
                    @"(%s)\.(%s)\b"
                    configReceiver
                    (alternation (Set.toList (sharedLabels.Remove "DatadogReadback"))))
                ""
                "`config.EventStore` / `app.Config.LogLevel` / … (a moved field read off a receiver named as the config) → `config.<Concern>.<Field>`" with
                Rewrite =
                    fun m ->
                        let field = m.Groups[2].Value
                        sprintf "%s.%s.%s" m.Groups[1].Value concernOf[field] field
        }
        // A moved field whose name no other record a consumer holds is
        // likely to carry: the read is a `ServerConfig` read on any VALUE
        // receiver (a lowercase-initial binding, or `.Config`). A
        // capitalised segment is a module or namespace path — `ToolUp.AssetStore`,
        // `Consent.ConsentStateStore`, `module X.EntityOutbox` — and is
        // never a field read, whatever its last segment is called.
        {
            rewrite
                "877-moved-member"
                CodemodMigration.NestedConfig
                CodemodFileClass.Source
                (sprintf
                    @"(?<=\b[a-z_][\w']*|\.Config)\.(%s)\b"
                    (alternation (movedFields |> List.filter (fun f -> not (sharedLabels.Contains f)))))
                ""
                "`.ReplicaCount` / `.RequireHttps` / `.AcceptEphemeralRagIndex` / … (a moved field only `ServerConfig` carries) → `.<Concern>.<Field>`" with
                Rewrite =
                    fun m ->
                        let field = m.Groups[1].Value
                        sprintf ".%s.%s" concernOf[field] field
        }
        // A scope key built from a string literal wraps the literal.
        rewrite
            "878-scopekey-literal"
            CodemodMigration.TypedIdentity
            CodemodFileClass.Source
            @"\bScopeKey\.(Team|User)\s*\(?\s*(""[^""\r\n]*"")\s*\)?"
            "ScopeKey.${1}(${1}Id $2)"
            "`ScopeKey.Team \"acme\"` / `ScopeKey.User \"alice\"` → `ScopeKey.Team(TeamId \"acme\")` / `ScopeKey.User(UserId \"alice\")`"
        // The three retired string abbreviations, in a type position
        // (after `:`, `of`, `*` or `<`, or as a generic argument before
        // `>`), are the `string` they abbreviated.
        rewrite
            "878-retired-abbreviation"
            CodemodMigration.TypedIdentity
            CodemodFileClass.Source
            @"(?<=(?:(?<!:):|\bof|\*|<)\s*)\b(?:EntityId|TenantId|ScopeId)\b|(?<=,\s*)\b(?:EntityId|TenantId|ScopeId)\b(?=\s*>)"
            "string"
            "`id: EntityId` / `of TenantId` / `Map<string, ScopeId>` (a retired `string` abbreviation in a type position) → `string`"
        // The fork's error-handler result is qualified: an error handler's
        // `Propagate x` / `PropagateCategorised(c, x)` names its union.
        rewrite
            "1056-error-result"
            CodemodMigration.RequestPipeline
            CodemodFileClass.Source
            @"(?<![.\w])(PropagateCategorised|Propagate)\b(?=\s*[(\w""])"
            "ErrorResult.$1"
            "`Propagate x` / `PropagateCategorised(category, x)` in an error handler → `ErrorResult.Propagate x` / `ErrorResult.PropagateCategorised(category, x)`"
        // The remoting rate-limit store, named by its namespace (the Phase 56
        // `ToolUp.Platform.Infra.IRateLimitStore` shares the simple name and
        // is unchanged, so only the qualified spelling is mechanical).
        rewrite
            "1068-remoting-store"
            CodemodMigration.RateLimitAdmission
            CodemodFileClass.Source
            @"\bToolUp\.Remoting\.Server\.IRateLimitStore\b"
            "ToolUp.Remoting.Server.IRemotingRateLimitStore"
            "`ToolUp.Remoting.Server.IRateLimitStore` → `ToolUp.Remoting.Server.IRemotingRateLimitStore`"
        // A write awaited for its side effect, through a receiver
        // whose name says it is the event store, keeps today's failure path
        // (the default store raised on a lost write) through the one adapter.
        rewrite
            "873-event-store-write"
            CodemodMigration.FailureAsData
            CodemodFileClass.Source
            @"\bdo!\s+(\w*[eE]ventStore)\.Write\b\s*"
            "do! EventStore.writeOrRaise $1 "
            "`do! eventStore.Write evt` → `do! EventStore.writeOrRaise eventStore evt` (a lost write still raises, now carrying `EventStoreError`; match `Write`'s `Result` where the site can decide)"
        // The same for a job-store write through a receiver named for it.
        rewrite
            "873-job-store-write"
            CodemodMigration.FailureAsData
            CodemodFileClass.Source
            @"\bdo!\s+(\w*[jJ]obStore)\.(Save|Update|RecordRun)\b"
            "do! JobStoreWrite.orRaise <| $1.$2"
            "`do! jobStore.Save job` (and `Update` / `RecordRun`) → `do! JobStoreWrite.orRaise <| jobStore.Save job` (a refused write still raises, now carrying `JobStoreError`)"
        // A custom IAuditLog whose Record answered unit now answers Ok.
        rewrite
            "1062-record-unit"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"(\bmember\s+\w+\.Record\s*\(\s*\w+\s*,\s*\w+\s*\)\s*=\s*)(?:async\s*\{\s*return\s*\(\)\s*\}|async\.Return\s*\(\s*\))"
            "${1}async { return Ok() }"
            "`member _.Record(scope, event) = async { return () }` (or `async.Return()`) → `async { return Ok() }`"
        // The option-wrapped log passed with its scheduler: the log alone.
        rewrite
            "1062-audit-log-argument"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"\(?\bSome\s*\(?\s*(\w*[aA]udit\w*|\w*[lL]og)\s*\)?\)?\s+(?:Async\.Start|Async\.StartImmediate)\b"
            "$1"
            "`(Some auditLog) Async.Start` (an `IAuditLog option` and its `schedule`) → `auditLog`"
        // A custom IAuditLog takes the typed scope and the row's actor; an
        // implementation keyed on the event store's string matches the scope
        // by its stream key.
        rewrite
            "1062-record-implementer-scope"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"(\bmember\s+\w+\.Record\s*\(\s*)(\w+)(\s*,\s*)(_?audit\w*|_?event\w*|_)(\s*\))"
            "${1}AuditScope.StreamKey ${2}, _actor, ${4}${5}"
            "`member _.Record(scopeId, audit)` → `member _.Record(AuditScope.StreamKey scopeId, _actor, audit)` (`Record` takes `AuditScope * actor: AuditSubject * AuditEvent`; a decorator forwards both unchanged)"
        rewrite
            "1062-get-audit-trail-implementer-scope"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"(\bmember\s+\w+\.GetAuditTrail\s*\(\s*)(\w+)(\s*,)"
            "${1}AuditScope.StreamKey ${2}${3}"
            "`member _.GetAuditTrail(scopeId, …)` → `member _.GetAuditTrail(AuditScope.StreamKey scopeId, …)`"
        // The envelope carries the actor the row persisted, never one guessed
        // from the stream key.
        rewrite
            "1062-envelope-from-scope-id"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"\bAuditEnvelope\.fromScopeId\b"
            "AuditEnvelope.create AuditSubject.unattributed"
            "`AuditEnvelope.fromScopeId scopeId at event` → `AuditEnvelope.create AuditSubject.unattributed scopeId at event` (or the actor the row carried)"
        // An IAuditLog option is an IAuditLog; NoOpAuditLog is the absent value.
        rewrite
            "1062-audit-log-option"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"\b((?:ToolUp\.Platform\.)?IAuditLog)\s+option\b"
            "$1"
            "`IAuditLog option` → `IAuditLog` (pass `NoOpAuditLog()` where `None` was passed)"
        // A module's state is read through its host.
        rewrite
            "1071-module-state-read"
            CodemodMigration.ModuleHost
            CodemodFileClass.Source
            @"\b([\w.]+)\.ModuleStates\s*\|>\s*Map\.tryFind\s+"
            "$1 |> Client.moduleState "
            "`model.ModuleStates |> Map.tryFind id` → `model |> Client.moduleState id` (the shell keeps a `ModuleHost` per module in `Model.Hosts`)"
        // The two first-init gate fields are one set.
        rewrite
            "1071-first-init-gates"
            CodemodMigration.ModuleHost
            CodemodFileClass.Source
            @"^(\s*)ConfigsPrefetch\s*=.*$"
            "$1AwaitingFirstInit = Client.firstInitGates"
            "`ConfigsPrefetch = …` in a full `Client.Model` literal → `AwaitingFirstInit = Client.firstInitGates` (delete the `FlagsPrefetch` line beside it)"
        // The HNSW store's unreadable-snapshot exception is the one the
        // shared snapshot index raises.
        rewrite
            "1084-hnsw-snapshot-exception"
            CodemodMigration.SnapshotIndex
            CodemodFileClass.Source
            @"\b(?:(?:ToolUp\.RAG\.VectorStores\.Hnsw\.)?HnswVectorStore\.)?HnswScopeSnapshotUnreadableException\b"
            "ToolUp.Platform.Infra.RagScopeSnapshotUnreadableException"
            "`HnswScopeSnapshotUnreadableException` → `ToolUp.Platform.Infra.RagScopeSnapshotUnreadableException` (its `Fault` says unreadable or corrupt)"
        // The vector store's exception moved with the index into Infra.
        rewrite
            "1084-rag-snapshot-exception-home"
            CodemodMigration.SnapshotIndex
            CodemodFileClass.Source
            @"\b(?:ToolUp\.RAG\.)?InMemoryVectorStore\.RagScopeSnapshotUnreadableException\b"
            "ToolUp.Platform.Infra.RagScopeSnapshotUnreadableException"
            "`InMemoryVectorStore.RagScopeSnapshotUnreadableException` → `ToolUp.Platform.Infra.RagScopeSnapshotUnreadableException`"
        // The fallback constructor states what it grants.
        rewrite
            "882-unrestricted-constructor"
            CodemodMigration.AccessContextRefuses
            CodemodFileClass.Source
            @"\bAccessContext\.unrestricted\b"
            "AccessContext.ofSubject ModulePermissions.Unrestricted"
            "`AccessContext.unrestricted subject` → `AccessContext.ofSubject ModulePermissions.Unrestricted subject` (same behaviour, stated; review where it was a fallback)"
        // A write's actor is a projection of the principal.
        rewrite
            "882-entity-principal"
            CodemodMigration.AccessContextRefuses
            CodemodFileClass.Source
            @"\bEntityPrincipal\.ofPrincipal\s+(\w+)\.UserId\b"
            "AccessContext.entityPrincipal $1"
            "`EntityPrincipal.ofPrincipal ctx.UserId` → `AccessContext.entityPrincipal ctx` (the actor is a projection of the principal)"
        // The knowledge base's terminal set is the lattice's.
        rewrite
            "1085-is-terminal-status"
            CodemodMigration.IngestionAttempt
            CodemodFileClass.Source
            @"\bKnowledgeBase\.ServerIngestionStatusStream\.isTerminalStatus\b"
            "IngestionStatus.isTerminal"
            "`KnowledgeBase.ServerIngestionStatusStream.isTerminalStatus` → `IngestionStatus.isTerminal` (KnowledgeBase.Core, beside the type; a bare `isTerminalStatus` is reported, since a consumer may have its own)"
        // A test double's no-op failure closure has no field to fill.
        rewrite
            "1085-mark-ingestion-failed-stub"
            CodemodMigration.IngestionAttempt
            CodemodFileClass.Source
            @"^[ \t]*MarkIngestionFailed\s*=\s*fun\s+_\s+_\s+_\s*->\s*async\.Return\s*\(\s*\)[ \t]*$"
            ""
            "`MarkIngestionFailed = fun _ _ _ -> async.Return()` in a full `KnowledgeApiDeps` literal → an empty line (the field is gone; delete the line)"
        // Phase 1074 — the client's payload-free on/off unions are 877's
        // `FeatureMode`; the chrome rows' are `ChromeFill`. The labels move
        // in the `ClientConfig` nesting pass, after these.
        rewrite
            "1074-feature-mode-off"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\b(?:(?:CommandPaletteMode|AuthTokenStorage)\.)?(?:NoCommandPalette|ClientCookieAndLocalStorage)\b"
            "FeatureMode.Disabled"
            "`NoCommandPalette` / `ClientCookieAndLocalStorage` → `FeatureMode.Disabled`"
        rewrite
            "1074-feature-mode-on"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\b(?:(?:CommandPaletteMode|AuthTokenStorage)\.)?(?:DefaultCommandPalette|ServerSetHttpOnlyCookie)\b"
            "FeatureMode.Enabled"
            "`DefaultCommandPalette` / `ServerSetHttpOnlyCookie` → `FeatureMode.Enabled`"
        rewrite
            "1074-feature-mode-type"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"(?<=:\s*)\b(?:CommandPaletteMode|AuthTokenStorage)\b"
            "FeatureMode"
            "`: CommandPaletteMode` / `: AuthTokenStorage` (a type annotation) → `: FeatureMode`"
        rewrite
            "1074-chrome-off"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\b(?:(?:AdminSurfaceMode|ObservabilityModuleMode|PlatformUsersMode)\.)?(?:InlineGroups|NoObservabilityModule|NoPlatformUsers)\b"
            "ChromeFill.Off"
            "`InlineGroups` / `NoObservabilityModule` / `NoPlatformUsers` → `ChromeFill.Off`"
        rewrite
            "1074-chrome-on"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\b(?:(?:AdminSurfaceMode|ObservabilityModuleMode|PlatformUsersMode)\.)?(?:SeparateArea|DefaultObservabilityModule|DefaultPlatformUsers)\b"
            "ChromeFill.On()"
            "`SeparateArea` / `DefaultObservabilityModule` / `DefaultPlatformUsers` → `ChromeFill.On()`"
        rewrite
            "1074-chrome-type"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"(?<=:\s*)\b(?:AdminSurfaceMode|ObservabilityModuleMode|PlatformUsersMode)\b"
            "ChromeFill<unit>"
            "`: AdminSurfaceMode` / `: ObservabilityModuleMode` / `: PlatformUsersMode` → `: ChromeFill<unit>`"
        // The AI assistant family is three slot fills.
        rewrite
            "1074-ai-assistant-none"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\b(?:AIAssistantMode\.)?NoAIAssistant\b"
            "AISlotFills.empty"
            "`NoAIAssistant` → `AISlotFills.empty` (every AI slot `Empty`)"
        rewrite
            "1074-ai-assistant-default"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\b(?:AIAssistantMode\.)?DefaultAIAssistant\b"
            "AISlotFills.defaults"
            "`DefaultAIAssistant` → `AISlotFills.defaults` (every AI slot `Default`)"
        rewrite
            "1074-ai-assistant-configured"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\b(?:AIAssistantMode\.)?ConfiguredAIAssistant\b"
            "AISlotFills.configured"
            "`ConfiguredAIAssistant branding` → `AISlotFills.configured branding` (the assistant slot `Configured`, the other two `Default`)"
        rewrite
            "1074-ai-assistant-type"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\bAIAssistantMode\b"
            "AISlotFills"
            "`AIAssistantMode` → `AISlotFills`"
        // The vendor-named sign-in case is the ClerkUI companion's. A
        // pattern (`| ClerkAuthUI cfg ->`) is reported instead.
        {
            rewrite
                "1074-clerk-auth-ui"
                CodemodMigration.ClientConfigByConcern
                CodemodFileClass.Source
                @"\b(?:AuthUIMode\.)?ClerkAuthUI\b"
                "ToolUp.AuthProviders.ClerkRegister.authUI"
                "`ClerkAuthUI cfg` → `ToolUp.AuthProviders.ClerkRegister.authUI cfg` (the vendor-neutral `ProviderAuthUI (\"clerk\", box cfg)`; add the companion's `ClerkRegister.handler` to `Composition.Handlers.AuthUIHandlers`)" with
                Unless = Some(rx @"\|\s*(?:AuthUIMode\.)?ClerkAuthUI\b")
        }
        // The two data managers take the same configuration.
        rewrite
            "1074-column-mapping-unlabelled"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\bDataManagerChoice\.ColumnMapping\s*(?:\(\s*None\s*\)|None\b)"
            "DataManagerChoice.ColumnMapping DataManagerConfig.unlabelled"
            "`DataManagerChoice.ColumnMapping None` → `DataManagerChoice.ColumnMapping DataManagerConfig.unlabelled`"
        // A guarded run is a `LifecycleRun` whose guard is the lock.
        rewrite
            "1063-lifecycle-guarded-with"
            CodemodMigration.OneResolverOneGate
            CodemodFileClass.Source
            @"\bTenantLifecycleAggregator\.runGuardedWith\s+(\w+)\s+([\w.]+)\s+(\w+|\[[^\]\n]*\])\s+(\w+)\s+(\w+|""[^""\n]*"")\s+(\w+|""[^""\n]*"")"
            "TenantLifecycleAggregator.run { TenantLifecycleAggregator.LifecycleRun.create $2 $3 $4 $5 $6 with Guard = Some $1 }"
            "`runGuardedWith lock audit hooks phase scope actor` → `run { LifecycleRun.create audit hooks phase scope actor with Guard = Some lock }` (the same `GuardedRun`)"
        rewrite
            "1063-lifecycle-export-with"
            CodemodMigration.OneResolverOneGate
            CodemodFileClass.Source
            @"\bTenantLifecycleAggregator\.exportThenDeprovisionWith\s+(\w+)\s+([\w.]+)\s+(\w+)\s+(\w+|\[[^\]\n]*\])\s+(\w+|""[^""\n]*"")\s+(\w+|""[^""\n]*"")"
            "TenantLifecycleAggregator.exportThenDeprovision $3 { TenantLifecycleAggregator.LifecycleRun.create $2 $4 Deprovisioning $5 $6 with Guard = Some $1 }"
            "`exportThenDeprovisionWith lock audit export hooks scope actor` → `exportThenDeprovision export { LifecycleRun.create audit hooks Deprovisioning scope actor with Guard = Some lock }` (the same `GuardedExportThenDeprovision`)"
        // The one resolver: a scope is a subject projected.
        rewrite
            "1063-storage-scope-resolver"
            CodemodMigration.OneResolverOneGate
            CodemodFileClass.Source
            @"\bIStorageScopeResolver\b"
            "ISubjectResolver"
            "`IStorageScopeResolver` → `ISubjectResolver` (derive the storage scope from the resolved subject with `StorageScopeDerivation.tryFromSubject`, or `SubjectScope.resolve`)"
        // The original-object data type is the policy record's.
        rewrite
            "1087-original-object-data-type"
            CodemodMigration.KnowledgePolicies
            CodemodFileClass.Source
            @"\bKnowledgeObjectRetentionPolicy\.ObjectDataType\b"
            "KnowledgePolicies.OriginalObjectDataType"
            "`KnowledgeObjectRetentionPolicy.ObjectDataType` → `KnowledgePolicies.OriginalObjectDataType` (the same literal)"
        // The drop triple moved with the hook.
        rewrite
            "1087-emit-ingestion-drop"
            CodemodMigration.KnowledgePolicies
            CodemodFileClass.Source
            @"\bRAGCompose\.emitIngestionDrop\b"
            "VectorisationHook.emitIngestionDrop"
            "`RAGCompose.emitIngestionDrop` → `VectorisationHook.emitIngestionDrop` (the post-save hook's own module)"
        // The DOCX mode moved into KnowledgeBase.Core beside the policy record.
        rewrite
            "1087-docx-mode"
            CodemodMigration.KnowledgePolicies
            CodemodFileClass.Source
            @"\b(?:KnowledgeBase\.)?DocxExtraction\.DocxExtractionMode\b"
            "DocxExtractionMode"
            "`KnowledgeBase.DocxExtraction.DocxExtractionMode` → `DocxExtractionMode` (KnowledgeBase.Core, `SharedTypes`)"
        // A view takes the shell's `RenderContext` and returns `PageContent`.
        rewrite
            "1075-full-width-view"
            CodemodMigration.OneViewContract
            CodemodFileClass.Source
            @"\bwithFullWidthView\s+([A-Za-z_][\w']*)\s*$"
            "withView (fun model ctx -> PageContent.FullWidth($1 model ctx.Dispatch))"
            "`withFullWidthView view` → `withView (fun model ctx -> PageContent.FullWidth(view model ctx.Dispatch))`"
        rewrite
            "1075-split-panel-view"
            CodemodMigration.OneViewContract
            CodemodFileClass.Source
            @"\bClientModule\.withView\s+([A-Za-z_][\w']*)\s*$"
            "ClientModule.withView (fun model ctx -> PageContent.SplitPanel($1 model ctx.Dispatch))"
            "`ClientModule.withView view` (a `(left, right)` view) → `ClientModule.withView (fun model ctx -> PageContent.SplitPanel(view model ctx.Dispatch))`"
        // The localised twins take the plain names.
        rewrite
            "1075-build-sections-with"
            CodemodMigration.OneViewContract
            CodemodFileClass.Source
            @"\bbuildSectionsWith\b"
            "buildSections"
            "`Sidebar.buildSectionsWith messages` → `Sidebar.buildSections messages` (the English `buildSections` is gone)"
        rewrite
            "1075-banner-with"
            CodemodMigration.OneViewContract
            CodemodFileClass.Source
            @"\bBootDegradation\.bannerWith\b"
            "BootDegradation.banner"
            "`BootDegradation.bannerWith messages` → `BootDegradation.banner messages` (the English `banner` is gone)"
        // The compute budget's twins are the seam's types. The adapters were
        // identities up to the twin, so an applied adapter is its argument
        // and a point-free one is `id`.
        rewrite
            "1081-adapter-applied"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\b(?:ComputeBudgetPeriod\.toBudgetPeriod|ComputeBudgetDenial\.(?:toBudgetDenial|ofBudgetDenial)|ComputeBudgetUsage\.(?:toBudgetUsage|ofBudgetUsage))\s+([A-Za-z_][\w']*(?:\.[A-Za-z_][\w']*)*)"
            "$1"
            "an applied compute-budget adapter (`ComputeBudgetPeriod.toBudgetPeriod p`, `ComputeBudgetDenial.toBudgetDenial d` / `ofBudgetDenial d`, `ComputeBudgetUsage.toBudgetUsage u` / `ofBudgetUsage u`) → its argument"
        rewrite
            "1081-adapter-point-free"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\b(?:ComputeBudgetPeriod\.toBudgetPeriod|ComputeBudgetDenial\.(?:toBudgetDenial|ofBudgetDenial)|ComputeBudgetUsage\.(?:toBudgetUsage|ofBudgetUsage))\b"
            "id"
            "a point-free compute-budget adapter → `id`"
        rewrite
            "1081-denial-describe"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\bComputeBudgetDenial\.describe\b"
            "ComputeBudgetPolicy.describe"
            "`ComputeBudgetDenial.describe` → `ComputeBudgetPolicy.describe` (over the seam's `BudgetDenial`, the same wording)"
        rewrite
            "1081-usage-empty"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\bComputeBudgetUsage\.empty\b"
            "BudgetUsage.empty ComputeBudget.Domain"
            "`ComputeBudgetUsage.empty scope period` → `BudgetUsage.empty ComputeBudget.Domain scope period`"
        rewrite
            "1081-usage-json"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\bComputeBudgetJson\.serialiseUsage\b"
            "BudgetUsageJson.serialise"
            "`ComputeBudgetJson.serialiseUsage` → `BudgetUsageJson.serialise` (it was the shared ledger's codec under the twin)"
        rewrite
            "1081-period-type"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\bComputeBudgetPeriod\b"
            "BudgetPeriod"
            "`ComputeBudgetPeriod` → `BudgetPeriod` (the same cases, labels and keys; the seam adds `Hourly`)"
        rewrite
            "1081-denial-type"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\bComputeBudgetDenial\b"
            "BudgetDenial"
            "`ComputeBudgetDenial` → `BudgetDenial` (the seam's denial, in the `compute-budget` domain)"
        rewrite
            "1081-usage-type"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\bComputeBudgetUsage\b"
            "BudgetUsage"
            "`ComputeBudgetUsage` → `BudgetUsage` (the seam's row, in the `compute-budget` domain)"
    ]

    // ── The review rules ──────────────────────────────────────────────

    /// Every review rule. Matched against the REWRITTEN line, so a site a
    /// rewrite already handled is not re-reported, and the report reads
    /// the same on every run.
    let reviewRules: CodemodReviewRule list = [
        review
            "73-remoting-unknown-namespace"
            CodemodMigration.Remoting
            CodemodFileClass.Source
            @"\bFable\.Remoting\b"
            "a `Fable.Remoting` namespace the ToolUp fork does not carry (only Server / Client / Json / Giraffe / MsgPack moved) — check the `ToolUp.Remoting.*` surface for the equivalent, or keep the upstream package"
        review
            "73-elmish-unknown-namespace"
            CodemodMigration.Remoting
            CodemodFileClass.Source
            @"(?<![\w.'""`])Elmish\.(?!(Program|Cmd|Sub|React|HMR)\b)\w+|^\s*open\s+Elmish\.(?!React\b|HMR\b)\w+"
            "an `Elmish.*` sub-namespace the ToolUp runtime does not carry (Navigation / UrlParser / Debug / Browser stay upstream) — choose the ToolUp equivalent or keep the upstream package"
        review
            "73-fable-json-converter"
            CodemodMigration.Remoting
            CodemodFileClass.Source
            @"\bFableJsonConverter\b"
            "the Newtonsoft-backed `FableJsonConverter` was retired in the STJ migration — use `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create()` and `System.Text.Json.JsonSerializer` (see the Phase 73 doc's Remoting note)"
        review
            "66-platform-mode-type"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\bPlatformMode\b"
            "`PlatformMode` is retired: `ServerConfig.Mode` / `ClientConfig.Mode` became `Surfaces: SurfaceProfile list`, `AccessContext.Mode` became `Subject: Subject`, and a handler-factory `mode: PlatformMode` parameter drops (§6 — read the subject from `HttpContext.Items[\"ToolUp.Subject\"]`)"
        review
            "66-mode-match"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\.Mode\s+with\b"
            "§4 — the arm mapping onto `Subject` is per site: a predicate read becomes `AccessContext.isAnonymous` / `isAuthenticated` / `inTeamScope` / `isClaimBearer`; a scope read becomes `match ctx.Subject with TeamMember(uid, tid) -> … | _ -> …`"
        review
            "66-mode-read"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"(?<![\w.])(\w+)\.Mode\b(?!\s+with\b)"
            "a `.Mode` read: on `ServerConfig` / `ClientConfig` it is now `.Surfaces` (a list), on `AccessContext` it is `.Subject` — an equality test against a `PlatformMode` case becomes the matching `AccessContext.is*` predicate (§4); a consumer-owned `Mode` field is a false positive here"
        review
            "66-anonymous-route-prefix"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\bwithAnonymousRoute\b|\bAnonymousRoutePrefixes\b|\bAcceptShallowAnonymousRoutePrefix\b"
            "§5 — anonymous route prefixes are deleted; declare a per-route `SurfaceRequirement.claimBearerOnly` / `.anonymousOnly` / `.public_` via `ServerModule.withRouteSurfaceRequirement`"
        review
            "66-audit-sink-contract"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\binterface\s+IAuditSink\s+with\b"
            "§7 — a custom `IAuditSink` declares `SchemaVersion = AuditSchemaVersion.current` and takes `AuditEnvelope list` (was `AuditEvent list`) in `Deliver`"
        review
            "66-user-session-configure"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\bUserSession\.configure\b"
            "§8 — `UserSession.configure` takes a `SubjectKind` (was `PlatformMode`); pick the kind the deployment's surfaces admit"
        review
            "66-dev-default-user-id"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\bDevDefaultUserId\b"
            "`ClientConfig.DevDefaultUserId` is deleted — the public-utility-with-admin shape it approximated is `Surfaces = Surfaces.anonymousAndIndividual` (worked example 4)"
        review
            "66-auth-enforcement-middleware"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\bAuthEnforcementMiddleware\b"
            "`AuthEnforcementMiddleware` is retired; `SurfaceEnforcementMiddleware` driven by `SurfaceRequirementRegistry` replaces it — a direct reference usually means a hand-wired pipeline that should now come from the composition root"
        review
            "66-rate-limit-option"
            CodemodMigration.Surfaces
            CodemodFileClass.Source
            @"\bRateLimit\s*=\s*(Some\b|None\b)"
            "`ServerConfig.RateLimit` is no longer an option: `RateLimitConfig.none` is the default, and a policy is `{ Default = Some policy; PerShape = Map.empty }` (per-shape keyed by `SubjectKind`)"
        review
            "11c5-sink-kind-push"
            CodemodMigration.ApiStability
            CodemodFileClass.Source
            @"\bNotificationKind\.SinkKind\.Push\b|\bKind\s*=\s*""Push""|\bKind\s*=\s*""(Email|Sms)"""
            "T3.3 — `INotificationSink.Kind` is the `SinkKind` DU: `SinkKind.Email` / `SinkKind.Sms` / `SinkKind.Push PushVariant.WebPush` (or `Fcm` / `Apns` / `Other name`) — choose the push variant"
        review
            "11c5-auth-provider-impl"
            CodemodMigration.ApiStability
            CodemodFileClass.Source
            @"\binterface\s+IAuthProvider\s+with\b|:\?>\s*HttpContext\b"
            "T3.1 — `IAuthProvider.GetUser` / `ValidateRequest` take `RequestContext` (was `obj`): unwrap once at the entry point with `RequestContext.value ctx :?> HttpContext`; callers pass `RequestContext.ofHttpContext ctx`"
        review
            "11c5-client-config-create"
            CodemodMigration.ApiStability
            CodemodFileClass.Source
            @"\bClientConfig\.create\b"
            "T3.4 — `ClientConfig.create` takes a trailing `ClientHandlerRegistry` argument; pass the registry you wire (or `ClientHandlerRegistry.empty` explicitly)"
        review
            "11c5-retry-policy-fields"
            CodemodMigration.ApiStability
            CodemodFileClass.Source
            @"\bMaxRetries\s*=|\bBackoffMs\s*="
            "T3.2 — `RetryPolicy` is `{ MaxAttempts; InitialBackoff; MaxBackoff }`: `MaxAttempts` INCLUDES the first attempt (`MaxRetries = 3` becomes `MaxAttempts = 4`), `BackoffMs` becomes `InitialBackoff: TimeSpan` plus a `MaxBackoff` cap"
        review
            "11c5-embedding-factory-args"
            CodemodMigration.ApiStability
            CodemodFileClass.Source
            @"\bcreateWithModel\b|\bcreateWithBatchSize\b"
            "T3.5 — the OpenAI EMBEDDING factories take `secretStore` first (`createWithModel secretStore model dimensions`, `createWithBatchSize secretStore batchSize`); the Claude / OpenAI AI factories already did and need no change"
        review
            "815-elmish-error-handler-inline"
            CodemodMigration.Deprecations
            CodemodFileClass.Source
            @"\bProgram\.withErrorHandler\b"
            "`Program.withErrorHandler` is removed: wrap the upstream-shape callback as `Program.withErrorReporter (fun ctx -> onError (ctx.Message, ctx.Exception))` — a named callback in pipeline position is rewritten; an inline lambda or a call with the program applied on the same line is left for you to reshape"
        review
            "815-remoting-permission-guarded-api"
            CodemodMigration.Deprecations
            CodemodFileClass.Source
            @"\bmakePermissionGuardedApi\b"
            "`RemotingHelpers.makePermissionGuardedApi name factory` is removed: compose the module as `ServerModule.create name |> ServerModule.withGuardedApi factory` (the same module-access gate) and declare method-level authorisation with `[<RequiresRole>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` per docs/migrations/69d-authorization-metadata.md"
        review
            "815-aggrid-theme-class"
            CodemodMigration.Deprecations
            CodemodFileClass.Source
            @"\bThemeClass\.(Alpine|AlpineDark|Balham|BalhamDark|Material)\b"
            "the legacy `ThemeClass.*` CSS-class strings are removed: drop the `prop.className` wrapper and the `theme = \"legacy\"` prop, and pass `AgGrid.theme Theme.themeAlpine` / `Theme.themeBalham` / `Theme.themeMaterial` (a `*Dark` class is `|> Theme.withPart Theme.colorSchemeDark`) — no stylesheet import is needed under the Theming API"
        review
            "879-configured-data-manager"
            CodemodMigration.ShellSlots
            CodemodFileClass.Source
            @"(?<![\w])Configured(?:Mapping)?DataManager\b"
            "the data slot's configured cases take a `DataManagerChoice`: `ConfiguredDataManager cfg` is `SlotFill.Configured(DataManagerChoice.FileUpload cfg)` and `ConfiguredMappingDataManager cfg` is `SlotFill.Configured(DataManagerChoice.ColumnMapping(Some cfg))` — the argument moves inside the choice, so the site is left for you (and a `DataManager =` field above it is `Slots.DataManager =`)"
        review
            "879-mode-type"
            CodemodMigration.ShellSlots
            CodemodFileClass.Source
            (@"(?<![\w])(?:" + slotFields + @")Mode\b")
            "the per-built-in mode type is gone: a value of it is a `SlotFill<ModuleLabel>` (`SlotFill<DataManagerChoice>` for the data manager), and a case qualified through the type (`TeamManagerMode.NoTeamManager`) is the bare `SlotFill` case"
        review
            "879-slot-read"
            CodemodMigration.ShellSlots
            CodemodFileClass.Source
            (@"\b\w*(?:[Cc]onfig|defaults)\.(?:" + slotFields + @")\b(?!\s*=)")
            "a read of a per-built-in `ClientConfig` field: it is `config.Slots.<field>` now, and a `match` on it takes the `SlotFill` cases (`Empty` / `Default` / `Configured` / `External`)"
        // A field whose value starts on the NEXT line (Fantomas splits a
        // long assignment that way) cannot be tied to its case on the
        // line, and a message-catalog override uses the same names, so
        // it is reported rather than moved.
        review
            "879-slot-field-split"
            CodemodMigration.ShellSlots
            CodemodFileClass.Source
            (@"^\s*(?:" + slotFields + @")\s*=\s*$")
            "if this is a `ClientConfig` field, it is `Slots.<field> =` now (a nested copy-and-update) — the codemod moves the field only when its mode case is on the same line; a message-catalog field of the same name stays as it is"
        // The string-shaped loop entry is removed. Its twelfth
        // argument changes type, so the call is reshaped by hand.
        review
            "1077-run-agent-loop"
            CodemodMigration.AgentLoop
            CodemodFileClass.Source
            @"\brunAgentLoop\b"
            "`AIAgentEngine.runAgentLoop` is removed: call `runAgentLoopWithInput` with the same arguments, passing `ModelInput.ofSystemPrompt \"SystemPromptBuilder\" systemPrompt messages` where the system prompt was (or build an `AIAgentEngine.TurnContext` and call `AIAgentEngine.run`)"
        review
            "1077-history-window"
            CodemodMigration.AgentLoop
            CodemodFileClass.Source
            @"\b(?:MaxHistoryMessages|effectiveMaxHistory)\b"
            "the history window is `AIAssistantServerConfig.Limits.HistoryWindow` now: set `Limits = { AgentLoopLimits.defaults with HistoryWindow = <n> }`, and read it as `AIAssistantServerConfig.limits cfg |> AgentLoopLimits.historyWindow`"
        // A capability is read from the declared record, never
        // probed off the connector's type (a decorator's type hides it).
        review
            "1095-capability-probe"
            CodemodMigration.Capabilities
            CodemodFileClass.Source
            @":\?\s*(?:[\w.]+\.)?(?:IWatchableFileSource|IEmitsNativePayload)\b"
            "a connector's capabilities are its declared `Capabilities` record now: read `ConnectorCapabilities.watchable` / `ConnectorCapabilities.native` (or `source.Capabilities`), never the type — a decorator forwards its inner connector's record"
        // A model or compute capability is read from the declared record,
        // never probed off the implementation's type.
        review
            "1094-capability-probe"
            CodemodMigration.ModelCapabilities
            CodemodFileClass.Source
            @":\?\s*(?:[\w.]+\.)?(?:IModelEvaluationMetrics|IModelEvaluationPlanMetrics|IModelRegistrationNovelty|IExternalCallbackCapableBackend|IIsolatedComputeBackend)\b"
            "a model or compute capability is the implementation's declared `Capabilities` record now: read `provider.Capabilities.Metrics` / `.PlanMetrics`, `registry.Capabilities.Novelty`, `dispatcher.Capabilities.Callback` / `.Isolated`, never the type; a decorator answers with its inner implementation's record"
        review
            "1094-isolated-backend"
            CodemodMigration.ModelCapabilities
            CodemodFileClass.Source
            @"\binterface\s+(?:[\w.]+\.)?IIsolatedComputeBackend\s+with\b"
            "`IIsolatedComputeBackend` is removed: move the posture into the backend's `Capabilities` (`{ BackendCapabilities.none with Isolated = Some <posture> }`) and delete this interface block; the codemod declared `BackendCapabilities.none`, which reads as standard-only until you do"
        // A `Schedule` call site: whether it reads the job id, and whether
        // it should act on a coalesced job, is the site's decision.
        review
            "1066-schedule-call"
            CodemodMigration.JobOutcomes
            CodemodFileClass.Source
            @"^(?!.*\bmember\b).*\.Schedule\s*(?:\(|\s+[\w{])"
            "`Schedule` answers `Ok(Scheduled jobId)` for a job it created and `Ok(Coalesced jobId)` for one an idempotency key recovered: `Ok _` still compiles; a site that reads the id matches both (`Ok(Scheduled jobId | Coalesced jobId)`) or calls `ScheduleOutcome.jobId`, and a site that fires what it schedules fires only `Scheduled`, so a replayed request runs once"
        review
            "1066-trigger-once-call"
            CodemodMigration.JobOutcomes
            CodemodFileClass.Source
            @"^(?!.*\bmember\b).*\.TriggerOnce\s*\("
            "`TriggerOnce` refuses with a `TriggerError` (`JobNotFound` / `JobCancelled` / `JobDisabled` / `SchedulerStopping` / `QuotaExceeded` / `DispatchRefused`): match the cases, or render the old sentence with `TriggerError.message`"
        review
            "1066-scheduler-implementer"
            CodemodMigration.JobOutcomes
            CodemodFileClass.Source
            @"\bmember\s+\w+\.(?:Schedule|TriggerOnce)\b"
            "an `IJobScheduler` implementer: `Schedule` returns `ScheduleOutcome` and `TriggerOnce` returns `TriggerError`; a decorator forwards both unchanged, and a scheduler builds on `SchedulerPolicy` (`scheduleValidated`, `triggerable`, `setStatus`, `eventTriggered`) instead of re-typing the policy"
        // A fact-store call handed a string scope: no mechanical target
        // exists, because nothing turns a string into a resolved scope.
        review
            "1157-string-scope-call"
            CodemodMigration.OneScopeForm
            CodemodFileClass.Source
            @"\.(?:Assert|AssertBatch|Get|Query|QueryWithCompetition|QuerySupersessionChain|QueryPopulation)\(\s*(?:""[^""]*""|\w*[sS]copeId\b)\s*,"
            "an `IFactStore` member takes a `ResolvedScope`, never a string: on a request `ScopeResolution.forRequest ctx`, in a job `JobContext.Scope`, for declared standing work a principal resolved through `DeclaredPrincipalResolver`. Every member answers `Result<_, FactStoreError>`: match it, or `FactStoreResult.orRaise` where the caller has no failure channel (a string-keyed store's own member is not affected)"
        review
            "1157-fact-store-implementer"
            CodemodMigration.OneScopeForm
            CodemodFileClass.Source
            @"\binterface\s+(?:[\w.]+\.)?(?:IFactStore|IFactDisclosureGate|IDisclosureTaintIndexSource|IFactTableWriter)\s+with\b"
            "an implementer of a fact-tier seam: delete the `string` scope members and keep the `ResolvedScope` ones; an `IFactStore` answers `Result<_, FactStoreError>` from every member, and wraps a read in `FactStoreResult.ofRead` so a fault is `StorageFault`, never absence"
        review
            "1157-coherence-scopes"
            CodemodMigration.OneScopeForm
            CodemodFileClass.Source
            @"\b(?:withCoherenceChecks|withCoherenceChecksConfig)\b|\bHealthScope\b"
            "coherence sweeps and their health probe name DECLARED PRINCIPALS now (`withCoherenceChecks cadence principal`, `withCoherenceChecksConfig config cadence principals`, `CoherenceConfig.HealthPrincipal`), each resolved through the platform's `DeclaredPrincipalResolver`; the `_platform` default sweep is gone"
        review
            "1053-thread-safe-cell"
            CodemodMigration.RemotingLeftovers
            CodemodFileClass.Source
            @"\bThreadSafeCell\.(?:create|set|get|computeOnce)\b"
            "`ThreadSafeCell` is removed with no replacement; a `Lazy<'T>` covers `computeOnce`"
        review
            "1053-reader"
            CodemodMigration.RemotingLeftovers
            CodemodFileClass.Source
            @"\b(?:ReaderBuilder\b|Reader\.(?:run|returnM|bindM)\b|Remoting\.fromReader\b)"
            "`Reader`, `ReaderBuilder` and `Remoting.fromReader` are removed: use `Remoting.fromContext` and read services from the context"
        review
            "1053-get-service"
            CodemodMigration.RemotingLeftovers
            CodemodFileClass.Source
            @"\b(?:getService|resolve)<"
            "the global `Extensions` module is removed: `getService<'T>` is Giraffe's `ctx.GetService<'T>()`, and `resolve<'T>` goes with `Reader`. A local function of this name is not affected"
        review
            "1053-stackalloc"
            CodemodMigration.RemotingLeftovers
            CodemodFileClass.Source
            @"\bWrite\.stackalloc\b"
            "`Write.stackalloc` is removed: it returned a span over a popped frame. Allocate the buffer with `NativePtr.stackalloc` in the function that reads it"
        review
            "1053-parsing-arguments-error"
            CodemodMigration.RemotingLeftovers
            CodemodFileClass.Source
            @"\bParsingArgumentsError\b"
            "`ParsingArgumentsError` is removed: `DecodeError.render` is the sentence it carried"
        review
            "1053-interpret-integer-as"
            CodemodMigration.RemotingLeftovers
            CodemodFileClass.Source
            @"\binterpretIntegerAs\b"
            "`Read.interpretIntegerAs typ n` is removed: it was `Read.interpretIntegerAsFrom typ 64 n`, and a caller that knows the source width should say it"
        review
            "1053-write-fable"
            CodemodMigration.RemotingLeftovers
            CodemodFileClass.Source
            @"\bWrite\.Fable\b"
            "`Write.Fable` no longer ships: no browser path writes MessagePack. Copy `src/ToolUp.AI.Client.Tests/Remoting/MsgPackWrite.fs` if you need the writer"
        // A gated hosted service: the matrix is gone, and the unit's author
        // declares where it runs.
        review
            "1067-should-register-background-service"
            CodemodMigration.BackgroundPlacement
            CodemodFileClass.Source
            @"\bProcessProfileGate\.shouldRegisterBackgroundService\b|\b\w+Subsystem\b(?=.*\bProcessProfileGate\b)|\bBackgroundSubsystem\b"
            "the profile matrix is removed: register the hosted service with `Hosting.register services (BackgroundUnit.create \"<name>\" <placement> factory)` (or `BackgroundUnit.ofInstance` for an instance) and drop the `if`. Every subsystem the matrix named is `SharedWork`; the webhook and transactional dispatchers add `|> BackgroundUnit.outbound`"
        // A hosted service exempt from the matrix stated its placement in
        // prose; it states it as a value now.
        review
            "1067-exempt-hosted-service"
            CodemodMigration.BackgroundPlacement
            CodemodFileClass.Source
            @"\bProcessProfileGate\.(?:addExempt|exempt|addDeferredJobs|SchedulerRegistrationReason)\b|\bHostedServiceExemption\b"
            "exemptions are removed: register the service with `Hosting.register services unit` and choose its `Placement` — `OnceAtStart` for a one-shot or a scheduler registration (`addDeferredJobs label decls` is `Hosting.deferredJobs label decls`), `ProcessLocal` for a loop over this process's own state, `SharedWork` for a loop draining shared state. A companion holding a `ServerApp` can contribute it as a value: `ServerApp.addContribution (Contribution.hosted owner unit)`"
        // An ungated hosted service now goes through the one gate.
        review
            "1067-ungated-hosted-service"
            CodemodMigration.BackgroundPlacement
            CodemodFileClass.Source
            @"\bAdd(?:Singleton|HostedService)\s*<\s*(?:[\w.]+\.)?IHostedService\s*>|\bAddHostedService\s*<"
            "every hosted service is registered through `Hosting.register services unit` with a declared `Placement`: `OnceAtStart` for a one-shot, `SharedWork` for a store-draining loop, `ProcessLocal` for a loop bound to this process; a profile that does not admit the placement registers nothing"
        // The four in-process scheduler factories are one.
        review
            "1067-scheduler-factory"
            CodemodMigration.BackgroundPlacement
            CodemodFileClass.Source
            @"\bJobScheduler\.(?:create|createWithCatchUp|createWithExternalCompute|createWithExternalCallback)\b|\bnew\s+(?:[\w.]+\.)?InProcessJobScheduler\s*\("
            "`InProcessJobScheduler` has one factory: `JobScheduler.create { Store = …; EventStore = …; NotificationChannel = …; Config = …; Logger = …; ActivitySink = … } { SchedulerOptions.defaults with TriggerWatermark = Some w; ExternalDispatcher = Some d; ExternalHandleStore = Some h; DistributedLock = Some l; ProgressSink = JobScheduler.progressSinkFor deps; ScopeCarrier = Some c }` — set only the fields the old factory took (`createWithCatchUp` set the watermark, `createWithExternalCompute` the dispatcher, `createWithExternalCallback` the dispatcher and handle store). A progress sink is no longer derived from `JobProgress` inside the constructor: pass `JobScheduler.progressSinkFor deps`"
        // The three webhook construction paths are one.
        review
            "1067-webhook-factory"
            CodemodMigration.BackgroundPlacement
            CodemodFileClass.Source
            @"\bWebhookDispatcher\.(?:create|createWithLock)\b|\bnew\s+(?:[\w.]+\.)?WebhookDispatcherService\s*\("
            "`WebhookDispatcherService` has one factory: `WebhookDispatcher.create { Registry = …; DeliveryLog = …; EventStore = …; HttpClient = …; RetryPolicy = …; Logger = …; ActivitySink = …; SecretStore = …; GetRateLimiter = …; UrlPolicy = … } WebhookOptions.defaults`, with `{ WebhookOptions.defaults with DistributedLock = Some lock }` where `createWithLock` passed a lock"
        // The declared-data-sources startup service goes through the gate.
        review
            "1067-declared-data-sources"
            CodemodMigration.BackgroundPlacement
            CodemodFileClass.Source
            @"\bwithDeclaredDataSources\s+(?!config\b)"
            "`DeclaredDelivery.withDeclaredDataSources` takes the deployment's `ServerConfig` first (it registers through `Hosting.register`): `withDeclaredDataSources config declared watchPoll services`"
        // The AI companion root no longer wraps the deployment.
        review
            "876-ai-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bAIServerApp\b|\bwithAI\s+[A-Za-z_][\w.]*\s+[A-Za-z_][\w.]*\s+[A-Za-z_(]"
            "`AIServerApp` is gone: the AI tier is a companion composed onto the `ServerApp`. Build its options with `AIOptions.create factory providerProfile` and the AI-only setters (`AIOptions.withAIConfig`, `withModuleAIContexts`, `withToolEffects`, `withReflectionTools`, `withAIAnsweringTools`, `withPlatformProvider`, …), and compose it with `app |> AICompose.withAI options |> ServerApp.run`. An `AIServerApp.withX` delegator is the `ServerApp.withX` builder applied to the deployment, on either side of `withAI`; `withAnswerVerifier`, `withNumericFidelityGate`, `withAnswerProvenanceAnchors` and `withFastPathTriage` are `AICompose.withX` over the `ServerApp`; `AIServerApp.run` is `ServerApp.run`; `createFrom` and the old `withAI factory providerProfile configure` are `withAI (AIOptions.create factory providerProfile |> …)`; a `.Base` read is the `ServerApp` itself"
        // The RAG companion root no longer wraps the AI root.
        review
            "876-rag-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bRAGServerApp\b|\bwithRAG\s+[A-Za-z_][\w.]*\s+[A-Za-z_][\w.]*\s+[A-Za-z_][\w.]*\s+[A-Za-z_(]"
            "`RAGServerApp` is gone: RAG is a companion composed onto the `ServerApp`. Build its options with `RagOptions.create factory providerProfile embedder` and the RAG setters (`RagOptions.withTopK`, `withVectorStore`, `withFacts`, …; the AI tier's options through `RagOptions.withAIOptions`, `withAIConfig`, `withModuleAIContexts`), and compose it with `app |> RAGCompose.withRAG options |> ServerApp.run`, which composes the AI tier too — do not also call `AICompose.withAI`. A `RAGServerApp.withX` delegator is the `ServerApp.withX` builder applied to the deployment, on either side of `withRAG`; `mapAI f` that edited the AI layer's `Base` is a `ServerApp` builder (a companion's `register` now takes the `ServerApp`), and one that edited AI settings is `RagOptions.withAIOptions`; `RAGServerApp.run` is `ServerApp.run`; `createFrom` and the old `withRAG factory providerProfile embedder configure` are `withRAG (RagOptions.create factory providerProfile embedder |> …)`"
        // The remaining companion roots no longer wrap the deployment either.
        review
            "876-forms-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bFormsServerApp\b|\bwithForms\s+\(fun\b"
            "`FormsServerApp` is gone: Forms is a companion composed onto the `ServerApp`. Build its options with `FormsOptions.create ()` and the Forms setters (`FormsOptions.withFormSchema`, `withWorkflow`, `withGuard`, `withAction`, `withActionPolicy`, `withActionLedger`, …), and compose it with `app |> FormsCompose.withForms options |> ServerApp.run`. A `FormsServerApp.withX` delegator (`withConfig`, `withAuth`, `addModules`, …) is the `ServerApp.withX` builder applied to the deployment, on either side of `withForms`; `FormsServerApp.run` is `ServerApp.run`; `createFrom` and the old `withForms (fun f -> …)` configurator are `withForms (FormsOptions.create () |> …)`. A second `withForms` replaces the first"
        review
            "876-scheduling-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bSchedulingServerApp\b"
            "`SchedulingServerApp` is gone: scheduling is a companion composed onto the `ServerApp`. Build its options with `SchedulingOptions.create ()` and `SchedulingOptions.withCalendarBridge` / `withCalendarPollTrigger` / `withReminders`, and compose it with `app |> SchedulingCompose.withScheduling options |> ServerApp.run`. A `SchedulingServerApp.withX` delegator is the `ServerApp.withX` builder applied to the deployment; `SchedulingServerApp.run` is `ServerApp.run`. The calendar renewal helpers (`withChannelRenewal`, `withSubscriptionRenewal`) take the `ServerApp`; `MicrosoftGraphSubscriptions.compose` takes and returns `ServerApp * SchedulingOptions`"
        review
            "876-peer-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bPeerServerApp\b"
            "`PeerServerApp` is gone: the peer substrate is a companion composed onto the `ServerApp`. Build its options with `PeerOptions.create ()` and the peer setters (`PeerOptions.withLocalPeer`, `withContract`, `withCleanRoomTemplate`, …), and compose it with `app |> PeerCompose.withPeer options |> ServerApp.run`. A `PeerServerApp.withX` delegator is the `ServerApp.withX` builder applied to the deployment; `PeerServerApp.run` is `ServerApp.run`; `withCommutativeCipher` is `PeerCompose.withCommutativeCipher` over the `ServerApp`. The audits (`PeerCompose.auditAudienceBinding`, `enforce*`, …), `PeerSurface.describe` and `PeerGateway.surface` take the `(app, options)` pair; `PeerGateway.withAggregate` takes and returns it; the option-only helpers (`cleanRoomTemplateMap`, `jobResultStore`, `peerAuthProvider`, `federationPreflightInput`, …) take `PeerOptions`"
        review
            "876-public-rendering-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bPublicRenderingServerApp\b|\bwithPublicRendering\s+\(fun\b"
            "`PublicRenderingServerApp` is gone: public rendering is a companion composed onto the `ServerApp`. Build its options with `PublicRenderingOptions.create ()` and the setters (`PublicRenderingOptions.withLayout`, `withSite`, `withFeed`, `withRenderCache`, …), and compose it with `app |> withPublicRendering options |> ServerApp.run`. A `PublicRenderingServerApp.withX` delegator is the `ServerApp.withX` builder applied to the deployment; `PublicRenderingServerApp.run` is `ServerApp.run`; `createFrom` and the old `withPublicRendering (fun pr -> …)` configurator are `withPublicRendering (PublicRenderingOptions.create () |> …)`; `exportStatic*` is `PublicRenderingExport.exportStatic*` over the `(app, options)` pair. A second `withPublicRendering` replaces the first"
        review
            "876-asset-store-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bAssetStoreServerApp(Module)?\b"
            "`AssetStoreServerApp` is gone: the asset store is a companion composed onto the `ServerApp`. Build its options with `AssetStoreCompanionOptions.create ()` and its setters (`withOptions`, `withRenderer`, `withDerivativeProfile`, `withAssetStore`, `withAsyncDerivation`, `withDerivativeDlq`, …), and compose it with `app |> AssetCompose.withAssetStore options |> ServerApp.run`. A delegator is the `ServerApp.withX` builder applied to the deployment; `AssetStoreServerApp.run` is `ServerApp.run`"
        review
            "876-media-library-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bMediaLibraryServerApp\b"
            "`MediaLibraryServerApp` is gone: the media library is a companion composed onto the `ServerApp`. Build its options with `MediaLibraryCompanionOptions.create ()` and its setters (`withOptions`, `withDerivation`, `withTranscoder`, `withMediaLibrary`, `withEdgeCache`, `withDelegatedUrlSigner`), and compose it with `app |> MediaCompose.withMediaLibrary options |> ServerApp.run`. A delegator is the `ServerApp.withX` builder applied to the deployment; `MediaLibraryServerApp.run` is `ServerApp.run`"
        review
            "876-algorithms-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bAlgorithmsServerApp\b|\bwithAlgorithms\s+\(fun\b"
            "`AlgorithmsServerApp` is gone: the algorithms catalog is a companion composed onto the `ServerApp`. Build its options with `AlgorithmsOptions.create ()` and `AlgorithmsOptions.withProvider` / `withProviders` / `withoutAITools` / `withoutCatalogApi`, and compose it with `app |> AlgorithmsCompose.withAlgorithms options`. The old `withAlgorithms (fun a -> …)` configurator is `withAlgorithms (AlgorithmsOptions.create () |> …)`; `AlgorithmsServerApp.run` is `ServerApp.run`. A second `withAlgorithms` replaces the first"
        review
            "876-passkey-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bPasskeyServerApp\b|\bPasskeyCompose\.(create|run|withBase|withConfig)\b"
            "the passkey wrapper is gone: the passkey ceremonies are a companion composed onto the `ServerApp`. `PasskeyCompose.create config |> PasskeyCompose.run` is `app |> PasskeyCompose.withPasskey config |> ServerApp.run`; `withBase` is the `ServerApp` itself and `withConfig` is a new `PasskeyConfig` passed to `withPasskey`"
        review
            "876-scim-options"
            CodemodMigration.CompanionOptions
            CodemodFileClass.Source
            @"\bScimServerApp\b|\bScimMode\b|\bEnabledScim\b|\bNoScim\b"
            "`ScimServerApp` and `ScimMode` are gone: the SCIM endpoint is a companion composed onto the `ServerApp`. `ScimServerApp.ofServerApp app |> ScimServerApp.withScim config |> ScimServerApp.run` is `app |> ScimRoutes.withScim config |> ServerApp.run`; a deployment that does not call `withScim` mounts nothing"
        // Phase 877 — a moved field whose name other records carry too,
        // read off a receiver the rewrite could not name as the config.
        review
            "877-shared-label-receiver"
            CodemodMigration.NestedConfig
            CodemodFileClass.Source
            (sprintf
                @"(?<!\b(?:%s|config|cfg|serverConfig|conf|effective|effectiveConfig|finalConfig|baseConfig|merged|defaults|Config))\.(?:%s)\b"
                (alternation concernNames)
                (alternation (Set.toList sharedLabels)))
            "if this receiver is a `ServerConfig`, the field moved: `EventStore` / `EntityStore` / `ResultStore` / `Datasets` / `ShareTokenStore` / `RateLimitStore` read `.Stores.<Field>`, `MetricsSink` / `SlowRequestThresholdOverrides` / `DatadogReadback` / `LogLevel` read `.Observability.<Field>`, `SecurityHardening` / `Cors` read `.SecurityPosture.<Field>` and `ProcessProfile` reads `.MultiInstance.ProcessProfile`; a deps or options record of your own with the same label is unchanged"
        // Phase 878 — a seam that takes a user or team id takes it typed.
        review
            "878-typed-seam-call"
            CodemodMigration.TypedIdentity
            CodemodFileClass.Source
            @"\.(?:AddMember|RemoveMember|ChangeMemberRole|GetTeamsForUser|GetTeamMembers|GetMemberRole|GetActiveTeam|SetActiveTeam|CreateTeam|DeleteTeam|GetTeam|SetArchived|PurgeTeam|PurgeUser|GetTeamPermissions|SetTeamPermissions|GetEffectivePermissions|SetUserModulePermissions|IsPlatformAdmin|AssignPlatformAdmin|RevokePlatformAdmin|GetTeamKey|SetTeamKey|DeleteTeamKey|HasTeamKey|GetPremiumStatus|RevokePremium|GrantsFor|ListForTeam|Heartbeat|Join|Move|Leave|ListForUser|RevokeAllForUser|ListByIssuer|GetPreferences|SavePreferences|ListPending|GetDigestWatermarks|SetDigestWatermark|Erase|Export|Preview|OnProvisioned|OnDeprovisioned|OnDeprovisionPreview|TriggerOnce|Rollback|BeginDeploy|RestartTenant|EvictTenant|CancelBuild)\s*\("
            "if this is a platform store or service seam, its user and team id parameters are `UserId` / `TeamId` now: wrap a string at the call (`UserId userId`, or `UserId.tryCreate raw` for an id from a request), and read `.Value` where a string is needed; `ITeamStore.GetActiveTeam` answers `TeamId option`"
        review
            "878-scopekey-payload"
            CodemodMigration.TypedIdentity
            CodemodFileClass.Source
            @"\bScopeKey\.(?:Team|User)\b(?!\s*\(\s*(?:TeamId|UserId)\b)"
            "`ScopeKey.Team` carries a `TeamId` and `ScopeKey.User` a `UserId`: wrap a string payload (`ScopeKey.Team(TeamId teamId)`), and a pattern `ScopeKey.Team teamId` binds the typed id (`teamId.Value` is the string)"
        review
            "878-team-id-reader"
            CodemodMigration.TypedIdentity
            CodemodFileClass.Source
            @"\bStorageScope\.tryTeamId\b"
            "`StorageScope.tryTeamId` answers `TeamId option`: map `TeamId.value` where a string is needed"
        // A retired abbreviation the type-position rewrite could not place:
        // after `->` (a return type, or a match arm yielding a value of
        // the same name) or in a tuple of values.
        review
            "878-retired-abbreviation-site"
            CodemodMigration.TypedIdentity
            CodemodFileClass.Source
            @"(?<![.\w""])(?:EntityId|TenantId|ScopeId)\b(?!\s*(?:=|:(?!:)))"
            "`EntityId`, `TenantId` and `ScopeId` are gone: each was an abbreviation for `string`. If this names the type, write `string`; if it is a value or a field of your own with the same name, it is unchanged"
        // `InvocationResult` folded into the request pipeline's response.
        review
            "1056-invocation-result"
            CodemodMigration.RequestPipeline
            CodemodFileClass.Source
            @"\bInvocationResult\b|\bProxy\.makeApiProxy\b"
            "`InvocationResult` and `Proxy.makeApiProxy` are gone: the proxy is the request pipeline's `invoke` stage and nothing outside it sees its result. Serve the record through `Remoting.buildHttpHandler` (Giraffe) or `app.UseRemoting` (ASP.NET Core), which run every seam; a test that drove the proxy alone drives the handler instead"
        review
            "1056-unclassified"
            CodemodMigration.RequestPipeline
            CodemodFileClass.Source
            @"\bMethodClassification\.Unclassified\b|\bRemoting\.Server\.Unclassified\b|\bAuthClassifier\.unclassified\s*\(\s*AuthClassifier\.classify\b"
            "`MethodClassification.Unclassified` is gone: an unmarked method has no classification (`AuthClassifier.classify` omits it) and a composition with a resolver refuses it at start. `AuthClassifier.unclassified` takes the API record's type: `AuthClassifier.unclassified typeof<MyApi>`"
        review
            "1056-qualified-union"
            CodemodMigration.RequestPipeline
            CodemodFileClass.Source
            @"(?<![.\w])(?:Ignore|StaticValue|FromContextAsync|FromContext|MessagePack)\b(?!\s*[:=])"
            "if this is a case of `ErrorResult` (`Ignore`), `ProtocolImplementation` (`Empty` / `StaticValue` / `FromContext` / `FromContextAsync`), `SerializationType` (`Json` / `MessagePack`), `BodyNormalisation` (`Enabled` / `Disabled`) or `AuthDecision` (`Allow` / `Deny`), it is qualified now: `ErrorResult.Ignore`, `ProtocolImplementation.StaticValue impl`, …"
        review
            "1056-streaming-producer"
            CodemodMigration.RequestPipeline
            CodemodFileClass.Source
            @"\bAsyncStream\.from(?:Callback|Subscription)\b"
            "a streaming producer now runs under the request's abort token (a client that leaves cancels it) and behind a bounded channel: a `fromCallback` producer that outruns its consumer is held in `emit` until it reads; a `fromSubscription` feed drops its oldest waiting value instead. A producer that relied on unbounded buffering, or on running past a disconnect, is reviewed here"
        // Phase 1068 — the remoting store answers `Admission`.
        review
            "1068-remoting-decision"
            CodemodMigration.RateLimitAdmission
            CodemodFileClass.Source
            @"\bRateLimitAllowed\b|\bRateLimitDenied\b"
            "`RateLimitDecision` is gone: `TryAcquire` answers `Admission` — `RateLimitAllowed` is `Admission.Admit remaining` (`Admission.Admit _` in a pattern) and `RateLimitDenied retryAfter` is `Admission.Deny retryAfter`"
        // The bare simple name: the remoting store if the file opens
        // `ToolUp.Remoting.Server` and calls `TryAcquire`, else the Phase 56
        // store, which keeps its name.
        review
            "1068-bare-store-name"
            CodemodMigration.RateLimitAdmission
            CodemodFileClass.Source
            @"(?<![.\w])IRateLimitStore\b"
            "if this is the remoting store (its member is `TryAcquire`, from `ToolUp.Remoting.Server`) it is `IRemotingRateLimitStore` now; the Phase 56 `IRateLimitStore` (`IncrementAndCheck`) keeps its name"
        // The Phase 56 store answers `Admission` too.
        review
            "1068-inbound-decision"
            CodemodMigration.RateLimitAdmission
            CodemodFileClass.Source
            @"\b(?:AllowWithRemaining|DenyWithError)\b"
            "if this matches an `IncrementAndCheck` result, it answers `Admission` now: `AllowWithRemaining remaining` is `Admission.Admit remaining` and `DenyWithError rle` is `Admission.Deny retryAfter` (`RateLimitedError.ofDenial threshold window retryAfter` rebuilds the payload). A `RateLimitDecisionEvent.Decision` keeps the old shape; an implementation returns `InboundRateLimitDecision.toAdmission` of it"
        // A full `RouteLimit` literal gains a field.
        review
            "1068-route-limit-posture"
            CodemodMigration.RateLimitAdmission
            CodemodFileClass.Source
            @"\bOnExceeded\s*=(?!.*\bStorePosture\b)"
            "if this is a full `RouteLimit` literal it needs `StorePosture = StorePosture.FailOpen` (today's behaviour) or `StorePosture.FailClosed` (refuse and audit when the store fails); a `{ limit with OnExceeded = … }` copy keeps the posture it copies"
        // `Prefetch`'s stall reporter had no caller anywhere.
        review
            "1070-prefetch-stall"
            CodemodMigration.RemoteAndMemo
            CodemodFileClass.Source
            @"\bPrefetch\.(?:warnIfStalled|setStallReporter|resetStallWarning)\b"
            "`Prefetch.warnIfStalled`, `Prefetch.setStallReporter` and `Prefetch.resetStallWarning` are removed with no replacement: delete the call. A gate that can hang is a `Remote<_, _>` whose `Failed` case the view renders (`StateViews.remote`); a timeout belongs to the request that loads it"
        // A shipped connector's constructor is gone: each vendor is a
        // `Connector` plus `create*` helpers. The constructor's argument
        // types decide the helper (a key or a secret store), so these are
        // reported, not rewritten.
        review
            "1079-claude-constructor"
            CodemodMigration.ConnectorKernel
            CodemodFileClass.Source
            @"\bClaudeAIProvider\.ClaudeAIProvider\b|(?<![.\w])new\s+ClaudeAIProvider\s*\("
            "the `ClaudeAIProvider` constructors are gone: `ClaudeAIProvider(apiKey)` is `ClaudeAIProvider.createWithApiKey apiKey`, `(apiKey, model)` is `createWithApiKeyAndModel`, `(apiKey, model, maxTokens, effort)` is `createWithApiKeyModelMaxTokensAndEffort`, `(secretStore)` is `create`, `(secretStore, model)` is `createWithModel`, `(secretStore, model, maxTokens, effort)` is `createWithSecretStore`; a key read on every call is `createWithKeySource fetch model maxTokens effort`, and a custom client is `ConnectorProvider(ClaudeAIProvider.connector client fetch maxTokens effort, model)`"
        review
            "1079-openai-constructor"
            CodemodMigration.ConnectorKernel
            CodemodFileClass.Source
            @"\bOpenAIProvider\.OpenAIProvider\b|(?<![.\w])new\s+OpenAIProvider\s*\("
            "the `OpenAIProvider` constructors are gone: `OpenAIProvider(apiKey)` is `OpenAIProvider.createWithApiKey apiKey`, `(apiKey, model)` is `createWithApiKeyAndModel`, `(secretStore)` is `create`, `(secretStore, model)` is `createWithModel`; a key read on every call is `createWithKeySource fetch model`, and a custom client is `ConnectorProvider(OpenAIProvider.connector client fetch, model)`"
        review
            "1079-gemini-constructor"
            CodemodMigration.ConnectorKernel
            CodemodFileClass.Source
            @"\bGeminiAIProvider\.GeminiAIProvider\b|(?<![.\w])new\s+GeminiAIProvider\s*\("
            "the `GeminiAIProvider` constructors are gone: `GeminiAIProvider(apiKey)` is `GeminiAIProvider.createWithApiKey apiKey`, `(apiKey, model)` is `createWithApiKeyAndModel`, `(secretStore)` is `create`, `(secretStore, model)` is `createWithModel`; a key read on every call is `createWithKeySource fetch model`, and a custom client is `ConnectorProvider(GeminiAIProvider.connector client fetch, model)`"
        review
            "1079-copilot-constructor"
            CodemodMigration.ConnectorKernel
            CodemodFileClass.Source
            @"\bCopilotAIProvider\.CopilotAIProvider\b|(?<![.\w])new\s+CopilotAIProvider\s*\(|\bMicrosoft365CopilotProvider\b"
            "the `CopilotAIProvider` constructors are gone: `(endpoint, apiKey[, model])` is `CopilotAIProvider.createWithApiKey` / `createWithApiKeyAndModel`, `(secretStore, endpoint[, model])` is `create` / `createWithModel`, `(endpoint, credential[, model])` is `createWithCredential` / `createWithCredentialAndModel`, and an explicit api-version is `createWithAuth endpoint apiVersion auth model`. `Microsoft365CopilotProvider` is gone too: use `Microsoft365CopilotAIProvider.create*`, or `createWithHttpClient` for a custom client"
        // `StopReason` is a union: a string literal beside it is a token.
        review
            "1079-stop-reason-token"
            CodemodMigration.ConnectorKernel
            CodemodFileClass.Source
            @"\bStopReason\s*(?:=|<>)\s*""|\.StopReason\s+""|""(?:end_turn|tool_use|max_tokens)""\s*(?:=|<>)\s*\w[\w.]*\.StopReason\b"
            "if this is an `AIProviderResponse`, `StopReason` is a union now: `\"end_turn\"` is `StopReason.EndTurn`, `\"tool_use\"` is `StopReason.ToolUse`, `\"max_tokens\"` is `StopReason.MaxTokens` and any other token is `StopReason.Other \"…\"`; `.Token` reads the string back. `AILatencyRecord.StopReason` is still the recorded string"
        // Failure as data at the infrastructure seams.
        review
            "873-write-call"
            CodemodMigration.FailureAsData
            CodemodFileClass.Source
            @"^(?!.*\bmember\b)(?!.*\bwriteOrRaise\b).*\.Write\s*(?:\(\s*(?:evt|event|e|ev)\s*\)|\s+(?:evt|event|ev)\b|\s*\{)"
            "if this is `IEventStore.Write`, it answers `Result<unit, EventStoreError>`: match it where the site can decide (a lost write is `EventStoreError.WriteFailed`), or `EventStore.writeOrRaise store evt` where the failure path is already an exception"
        review
            "873-job-store-call"
            CodemodMigration.FailureAsData
            CodemodFileClass.Source
            @"^(?!.*\bmember\b)(?!.*\bJobStoreWrite\b).*\bdo!\s+[\w.]+\.(?:Save|Update|RecordRun)\s*[\s({]"
            "if this is an `IJobStore` write, it answers `Result<unit, JobStoreError>` (`WriteFailed` / `PriorUnreadable`): match it, or `JobStoreWrite.orRaise <| store.Save job` where the failure path is already an exception"
        review
            "873-job-transition-call"
            CodemodMigration.FailureAsData
            CodemodFileClass.Source
            @"^(?!.*\bmember\b)(?!.*\bJobTransition\b).*\bdo!\s+[\w.]+\.(?:Cancel|Disable|Enable)\s*\("
            "`IJobScheduler.Cancel` / `Disable` / `Enable` answer `Result<unit, JobTransitionError>`: `Busy` (an in-flight dispatch held the lease; nothing changed, retry) or `StoreFailed` — match it, render it with `JobTransitionError.message`, or `JobTransition.orRaise` where the failure path is already an exception"
        review
            "873-implementer"
            CodemodMigration.FailureAsData
            CodemodFileClass.Source
            @"\binterface\s+(?:[\w.]+\.)?(?:IEventStore|IJobStore|IWebhookDispatcher|ILiveSessionHost)\b"
            "an implementer of a seam Phase 873 retyped: `IEventStore.Write` answers `Result<unit, EventStoreError>`, the `IJobStore` writes `Result<unit, JobStoreError>`, `IWebhookDispatcher.Dispatch` `Async<Result<unit, WebhookDispatchError>>` (`Dropped` when not enqueued), and `ILiveSessionHost.Subscribe` takes `string -> Async<unit>`; a decorator propagates its inner `Error` unchanged and runs its hook only after `Ok`"
        review
            "873-scheduler-implementer"
            CodemodMigration.FailureAsData
            CodemodFileClass.Source
            @"\bmember\s+\w+\.(?:Cancel|Disable|Enable)\s*\(\s*\w+\s*,\s*\w+\s*\)"
            "if this implements `IJobScheduler`, `Cancel` / `Disable` / `Enable` answer `Result<unit, JobTransitionError>`: a scheduler over `SchedulerPolicy.setStatus` maps its `JobStoreError` with `JobTransitionError.StoreFailed`, and a lease it could not take is `Busy`"
        // Audit failure is data at the seam.
        review
            "1062-record-call"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"^(?!.*\bmember\b)(?!.*\bAuditRow\.).*\bdo!\s+[\w.]*[aA]udit\w*\.Record\s*\("
            "if this is `IAuditLog.Record`, it answers `Result<unit, AuditWriteError>` (an `Error` only under `RefuseAction`): propagate it (`AuditRow.emit`), `|> AuditRow.orRefuse` where the site has no error channel, or `|> AuditRow.bestEffort` where the row records a decision already taken"
        review
            "1062-audit-implementer"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"\binterface\s+(?:[\w.]+\.)?IAuditLog\b"
            "an `IAuditLog` implementer: `Record` answers `Async<Result<unit, AuditWriteError>>` and never raises — `Ok` once the row lands, `Error StoreUnavailable` for a lost row when the deployment's policy refuses, `Ok` when it continues"
        review
            "1062-permission-store-implementer"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"\binterface\s+(?:[\w.]+\.)?IPermissionStore\b"
            "an `IPermissionStore` implementer: the reads answer `Result<_, PermissionStoreError>` (wrap a document in `Ok`; an absent one is `Ok TeamPermissions.empty`; an unreadable one is `Error`, never empty), and `UpdateTeamPermissions(teamId, change)` is one guarded read-modify-write"
        review
            "1062-record-arguments"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"^(?!.*\bmember\b).*(?:\.(?:Record|GetAuditTrail)\s*\(\s*|\bAuditRow\.emit\s+\w+\s+)(?!\(?(?:AuditScope|StorageScope|ResolvedScope)\.)[\w""(]"
            "if this is `IAuditLog.Record` / `GetAuditTrail` / `AuditRow.emit`, the scope is an `AuditScope` by its shape (`AuditScope.team teamId`, `AuditScope.platform`, `StorageScope.auditScope scope`, `ResolvedScope.auditScope scope`, or `AuditScope.shard key` for a string-keyed store's shard) and `Record` takes the row's actor next: `AuditSubject.fromSubject` of the request's principal where the site holds one, else `AuditSubject.system \"<component>\"` — never an invented user"
        review
            "1062-audit-log-match"
            CodemodMigration.AuditFailureAsData
            CodemodFileClass.Source
            @"\bmatch\s+\w*[aA]udit\w*\s+with\b"
            "if this matches an audit log's `Some` / `None`, the log is no longer optional: keep the `Some` arm over the value (over `NoOpAuditLog` it does what the `None` arm did)"
        // Every other read or write of the module-state map.
        review
            "1071-module-states"
            CodemodMigration.ModuleHost
            CodemodFileClass.Source
            @"\bModuleStates\b"
            "`Model.ModuleStates` is `Model.Hosts : Map<string, ModuleHost>`: a module's state is `host.State` (`Client.moduleState id model`), and a full model literal writes `Hosts = Map.empty` or `Hosts = Map.ofList [ id, { State = box state; Generation = 0 } ]`; `Client.observeModuleStates` takes the prior `Hosts`"
        // Fields with no successor of their own.
        review
            "1071-removed-shell-fields"
            CodemodMigration.ModuleHost
            CodemodFileClass.Source
            @"\b(?:FlagsPrefetch|ResetCounters)\b"
            "`FlagsPrefetch` and `ResetCounters` are gone: delete the line from a full `Client.Model` literal. The first-init gates are `AwaitingFirstInit` (a set of boot-source keys), and a module's reset count is its host's `Generation`"
        // Since 0.41.0 the shell's module message names its module.
        review
            "1071-module-msg-id"
            CodemodMigration.ModuleHost
            CodemodFileClass.Source
            @"\bModuleMsg\s*\(\s*box\b|\bModuleMsg\s+\w+\s*->"
            "`ModuleMsg` carries the module id: construct `ModuleMsg(moduleId, box message)` and match `ModuleMsg(moduleId, message)`; a message for a module with no host is dropped"
        // A companion's audit events left Core's union for its own family.
        review
            "874-moved-audit-case"
            CodemodMigration.OpenAuditRegistry
            CodemodFileClass.Source
            @"(?<!["".])\b(?:AuditEvent\.)?(?:FormSubmitted|FormSubmissionUpdated|WorkflowTransitioned|WorkflowActionExecuted|PlatformDocumentUploaded|PlatformDocumentDeleted|KnowledgeOriginalRetrieved|KnowledgeOriginalRetrievalDenied|KnowledgeScopeErased|KnowledgeDocumentDeduplicated|KnowledgeDocumentsPurged|ContentScanned|AssetUploaded|AssetDeleted|PasskeyCredentialRegistered|PasskeyCredentialRemoved|FactImportAccepted|FactImportRefused|CertificateIssued|KnowledgeIngestionDropped|ArtefactSigned|SigningKeyRotated|ConversationExported|BeaconRejected|ConversationReplayed|AnswerVerificationPassed|AnswerVerificationFlagged|CrossModuleRead|ConversationsPurged|PeerCallCompleted|PeerJobCompleted|PeerCleanRoomDecision|FederationRoundCompleted|FederationParticipantDropped|FederationRunAborted)\b(?!"")"
            "if this is an `AuditEvent` case, the companion that emits it declares it now: open its `*AuditEvents` module, construct with its family's `event` (`FormsAuditEvent.event (FormSubmitted p)`), and match with its active pattern (`FormsAudit(FormSubmitted p)`) or `tryRead`; Core carries it as `AuditEvent.Opaque`"
        // An archive or a ledger is read with the audit options.
        review
            "874-archive-read"
            CodemodMigration.OpenAuditRegistry
            CodemodFileClass.Source
            @"\bDeserialize<[^>]*(?:AuditEvent|AuditEnvelope|Archive\w*|Ledger\w*)>\s*\(.*\bPlatformJson\.options\b"
            "if this reads an audit archive, a ledger or a sink payload, read it with `AuditLog.AuditEventJson.options`: a companion's event is archived under its kind, which `PlatformJson.options` cannot decode"
        // Since 0.42.0 the toolkit's table is typed over its rows.
        review
            "1073-data-table-columns"
            CodemodMigration.AdminPageKit
            CodemodFileClass.Source
            @"\bData\.table\s*(?:\[\s*""|\(?\s*\w*[hH]eaders?\b)"
            "`Data.table` takes `Column<'row>` values and the rows themselves: write each header as `{ Header = \"…\"; Align = Data.Left; Cell = fun row -> … }` (the first column `Left`, figures `Right`) and pass the row values, not pre-rendered element lists"
        // A fallback for a request with no principal grants nothing.
        review
            "882-unresolved-fallback"
            CodemodMigration.AccessContextRefuses
            CodemodFileClass.Source
            @"\bAccessContext\.ofSubject\s+ModulePermissions\.Unrestricted\s+\(\s*(?:Subject\.)?AnonymousSession\b"
            "if this is a handler's fallback for a request with no resolved principal, write `AccessContext.tryResolve ctx.RequestServices |> Option.defaultWith (fun () -> AccessContext.unresolved \"<site>\")` (it grants nothing); work outside a request names `AccessContext.system \"<reason>\"` or `AccessContext.carrying \"<reason>\" subject`"
        // The permission value is a closed type: an empty map no longer means every module.
        review
            "882-permissions-map-read"
            CodemodMigration.AccessContextRefuses
            CodemodFileClass.Source
            @"\.ModulePermissions\s*(?:\.(?:IsEmpty|TryFind|ContainsKey|Count)\b|\|>\s*Map\.)|Map\.\w+\s+\S+\s+\w+\.ModulePermissions\b"
            "`ModulePermissions` is `Unrestricted | Restricted of Map<…>`: match on the case or use `ModulePermissions.tryFind` / `grantsAccess` / `grants`; `Restricted Map.empty` grants nothing, so an `IsEmpty` test for \"unrestricted\" is `= ModulePermissions.Unrestricted`"
        // The request's identity, scope, user and grants are one stamp.
        review
            "882-retired-request-item"
            CodemodMigration.AccessContextRefuses
            CodemodFileClass.Source
            @"""ToolUp\.(?:User|UserId|Subject|StorageScope|ScopeResult|ModulePermissions|ModuleExposure|PlatformRole|PublicationGrants|ServiceAccountPermissions)"""
            "this request item is no longer stamped: the request's principal is ONE `AccessContext` under `AccessContext.ItemsKey`. Read it with `AccessContext.ofItems ctx.Items` or an accessor (`callerIdOf` for `ToolUp.UserId`, `subjectOf`, `scopeOf`, `userOf`, `platformRoleOf`), write it with `AccessContext.stamp`, and copy only `AccessContext.ItemsKey` to a background context"
        // The team-policy viewer is a projection of the principal.
        review
            "882-team-policy-viewer"
            CodemodMigration.AccessContextRefuses
            CodemodFileClass.Source
            @"\bTeamPolicyViewer\.resolve\b|\bConversationVisibility\.resolveViewer\b"
            "`TeamPolicyViewer.resolve` takes `services access teamId` and `ConversationVisibility.resolveViewer` takes `ctx teamId`: drop the user-id argument, the viewer's id is the principal's (`AccessContext.callerId`)"
        // The constants that spelled two retired keys are gone.
        review
            "882-retired-item-constant"
            CodemodMigration.AccessContextRefuses
            CodemodFileClass.Source
            @"\b(?:SurfaceEnforcement\.SubjectItemsKey|ServiceAccountPermissionsItemsKey)\b"
            "`SurfaceEnforcement.SubjectItemsKey` and `ServiceAccountTokenHandler.ServiceAccountPermissionsItemsKey` are gone: read the subject with `AccessContext.subjectOf ctx.Items`; a service account's permissions are its principal's (`ServiceAccountPrincipalItemsKey`), adopted by scope resolution as the request's `ModulePermissions`"
        // The Data Manager observer is the one observer over the Data
        // Manager's attempt store.
        review
            "1085-data-manager-observer"
            CodemodMigration.IngestionAttempt
            CodemodFileClass.Source
            @"\bDataManagerIngestionObserver\.create\b"
            "`DataManagerIngestionObserver.create store channel logger` → `IngestionObserver.create (IngestionAttemptStore.dataManager store blobStorage logger) channel logger`; the post-save hook begins each file's attempt before it enqueues"
        // The failure closure is a settle on the attempt store.
        review
            "1085-mark-ingestion-failed"
            CodemodMigration.IngestionAttempt
            CodemodFileClass.Source
            @"\bMarkIngestionFailed\b"
            "`KnowledgeApiDeps.MarkIngestionFailed` is gone: settle a document with `KnowledgeIngest.settle deps docId (Failed reason)` (it forgets the attempt, writes the index and announces), and drop the field from a full `KnowledgeApiDeps` literal"
        // The knowledge base's status cache and its helpers are gone.
        review
            "1085-status-helpers"
            CodemodMigration.IngestionAttempt
            CodemodFileClass.Source
            @"\b(?:statusCache|beginIngestionAttempt|publishTerminalStatus|updateIndexChunkCount|isTerminalStatus)\b|\bServerIndexStorage\.(?:setStatus|updateStatus)\b"
            "a document's status is read from storage (`persistedStatus storage container docId`, or `GetStatus`); begin an ingestion with `KnowledgeIngest.enqueue deps doc chunkPairs`, settle one with `KnowledgeIngest.settle`, and announce a terminal with `terminalAnnouncement` + `AttemptAnnouncement.publish`; the knowledge base's `isTerminalStatus` is `IngestionStatus.isTerminal`"
        // The status stream seeds from storage.
        review
            "1085-status-stream"
            CodemodMigration.IngestionAttempt
            CodemodFileClass.Source
            @"\bingestionStatusStream\s+\w+\s*$|\bingestionStatusStream\s*$"
            "`ingestionStatusStream docId` → `ingestionStatusStream storage container docId`: the first element is read from storage, not a process-local cache"
        // Phase 1074 — what the codemod cannot prove or reshape.
        review
            "1074-no-active-team-landing"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\bNoActiveTeamLanding(?:ModuleId)?\s*=\s*Some\b"
            "the two no-active-team inputs are one chrome row: `NoActiveTeamLandingModuleId = Some id` is `Chrome.NoActiveTeamLanding = ChromeFill.On(NoActiveTeamLandingFill.Module id)` and `NoActiveTeamLanding = Some cfg` is `Chrome.NoActiveTeamLanding = ChromeFill.On(NoActiveTeamLandingFill.BuiltIn cfg)` (when both were set the module won — keep `Module`)"
        review
            "1074-datadog-readback"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"(?<![\w.])(?:Chrome\.)?DatadogReadback\s*=\s*(?:EnabledDatadogReadback|NoDatadogReadback)\b"
            "if this is a `ClientConfig` field, it is `Chrome.DatadogReadback = ChromeFill.On cfg` (or `ChromeFill.Off`) — the same `DatadogReadbackConfig` the server's `EnabledDatadogReadback` carries; `ServerConfig.Observability.DatadogReadback` keeps its mode"
        review
            "1074-shared-field-read"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"(?<![\w.])(?:clientConfig|config|cfg|ctx\.Config)\.(?:Observability|DatadogReadback|PlatformUsers|Presence|ModuleFilter|ExpectedModules|Locale|Handlers|Offline)\b(?!\.(?:Resolution|MessageCatalogOverride)\b)"
            "if this reads a `ClientConfig`, the field moved into a concern record: `.Chrome.Observability` / `.Chrome.DatadogReadback` / `.Chrome.PlatformUsers`, `.Shell.Presence` / `.Shell.Offline`, `.Composition.ModuleFilter` / `.Composition.ExpectedModules` / `.Composition.Handlers`, `.Locale.Resolution` — a `ServerConfig` read of the same name is unchanged"
        review
            "1074-ai-family-append"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\bappendAssistantModule\b"
            "`appendAssistantModule` is gone: the assistant family is three slots — pass `AISlotFills` to `AIClientConfig.run` / `program`, or fill them on a config with `AIClientConfig.withAISlots`"
        review
            "1074-clerk-auth-ui-pattern"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\|\s*(?:AuthUIMode\.)?ClerkAuthUI\b"
            "the `ClerkAuthUI` case is removed: a Clerk deployment is `ProviderAuthUI (\"clerk\", cfg)` — match that case instead"
        review
            "1074-data-manager-label"
            CodemodMigration.ClientConfigByConcern
            CodemodFileClass.Source
            @"\bDataManagerChoice\.(?:FileUpload|ColumnMapping)\s*\(?\s*(?:Some\s*)?\(?\s*\{"
            "a data manager's configuration is `{ Label = Some { Name = …; Icon = … }; Group = … }` now (`Label = None` keeps the manager's own), the same for both managers; `ColumnMapping (Some cfg)` drops the `Some`"
        // The lifecycle wrappers are one record and three functions.
        review
            "1063-lifecycle-wrappers"
            CodemodMigration.OneResolverOneGate
            CodemodFileClass.Source
            @"\bTenantLifecycleAggregator\.(?:runWithRequest|runWithDefaults|runWithDefaultsRequest|runGuarded|runGuardedRequest|runResumable)\b"
            "build a `LifecycleRun` (`LifecycleRun.create audit hooks phase scope actor`, then `with Request = Some r` / `Timeout = t` / `Guard = None` for a caller that already holds the scope) and call `run`, `runResumable cursor` or `exportThenDeprovision export`; all three answer whether the guard admitted the run (`GuardedRun.Ran summary` / `AlreadyInProgress`), and `runResumable` takes its ledger, progress and retry policy as one `LifecycleCursor`"
        review
            "1063-export-then-deprovision"
            CodemodMigration.OneResolverOneGate
            CodemodFileClass.Source
            @"\bTenantLifecycleAggregator\.exportThenDeprovision\s+\w+\s+\w+\s"
            "`exportThenDeprovision audit export hooks scope actor` → `exportThenDeprovision export (LifecycleRun.create audit hooks Deprovisioning scope actor)`; it answers `GuardedExportThenDeprovision` (`Ran` / `ExportFailed` / `AlreadyInProgress`), not a `Result`"
        // The retired resolvers and their header fallback.
        review
            "1063-retired-resolvers"
            CodemodMigration.OneResolverOneGate
            CodemodFileClass.Source
            @"\b(?:TeamScopeResolver|AnonymousScopeResolver|AuthenticatedEphemeralScopeResolver|AuthenticatedScopeResolver|ScopeRequestExtractor|TestStorageScopeResolver|fixedScopeResolver)\b|\.InvalidateUser\b"
            "the per-mode scope resolvers are gone: resolve the subject with the registered `ISubjectResolver` and derive the scope with `StorageScopeDerivation.tryFromSubject` (or `SubjectScope.resolve subjects derive request`); a request's resolved scope is `ScopeResolution.forRequest ctx`; evict a member's cached active team by publishing `MembershipChanged` (`ActiveTeamSet`) on the reserved topic; a test fake is `Fakes.TestSubjectResolver subject`. `X-User-Id` names no caller any more"
        review
            "1063-file-management-registry"
            CodemodMigration.OneResolverOneGate
            CodemodFileClass.Source
            @"\bFileManagement\.(?:getUserId|getStore|getStoreForUser|configureEvictionMinutes|storeEvictionMinutes)\b"
            "the session file stores are a registered service: resolve `ISessionFileStores` and `Resolve(dataTypes, dataObjectStore, runtime, scope)` (it awaits the scope's one hydration); the caller id is `AccessContext.callerIdOf ctx.Items`, never the `X-User-Id` header; the eviction TTL is the `SessionFileStores` constructor's"
        review
            "1063-file-contents-async"
            CodemodMigration.OneResolverOneGate
            CodemodFileClass.Source
            @"\.GetFileContents\b|\bFileManagement\.getFileContents\b"
            "`GetFileContents` / `getFileContents` answer `Async<string>` now (the scope's store is awaited): bind it with `let!` inside the endpoint's `async`, or use `FromFile`"
        // The decorators take the caller; the chain is built once per caller.
        review
            "1063-permission-decorators"
            CodemodMigration.OneResolverOneGate
            CodemodFileClass.Source
            @"\b(?:GrantPolicyPermissionStore|DualControlPermissionStore|GrantNotificationObserver)\s*\("
            "the decorators take the caller's `AccessContext` in place of an actor id or a `unit -> string option` closure; a handler wraps its writes once with `PermissionWrites.asPrincipal principal writes` (the registered `PermissionWrites`), which builds grant policy outside dual control"
        // The three single-flag policy records are fields of one record.
        review
            "1087-policy-records"
            CodemodMigration.KnowledgePolicies
            CodemodFileClass.Source
            @"\bKnowledge(?:Dedup|Versioning|ObjectRetention)Policy\b|\b(?:DedupUploads|VersionUploads|RetainOriginalsInObjectStore)\b"
            "`KnowledgeDedupPolicy`, `KnowledgeVersioningPolicy` and `KnowledgeObjectRetentionPolicy` are the `Dedup`, `Versioning` and `ObjectRetention` fields of `KnowledgePolicies` (`KnowledgePolicies.defaults`): compose with `withDocumentDedup` / `withDocumentVersioning` / `withObjectStoreRetention` (unchanged), or register one `KnowledgePolicies` value; a direct `AddSingleton<KnowledgeDedupPolicy>` reads nothing any more"
        // The deps record carries one policy field.
        review
            "1087-deps-policy-fields"
            CodemodMigration.KnowledgePolicies
            CodemodFileClass.Source
            @"^\s*(?:UploadPolicy|DedupPolicy|VersioningPolicy|QuotaPolicy|RetentionPolicy|ScanPolicy|ArchiveImportPolicy|UrlIngestionPolicy)\s*=|\.(?:DedupPolicy|VersioningPolicy|ScanPolicy|ArchiveImportPolicy|UrlIngestionPolicy)\b"
            "a full `KnowledgeApiDeps` literal sets `Policies = { KnowledgePolicies.defaults with … }` in place of its eight policy fields (`Upload`, `Dedup`, `Versioning`, `Quota`, `Retention`, `Scan`, `Archive`, `Url`), and `Queue` is an `IngestionQueue option`; a handler reads `deps.Policies.Quota` (and so on)"
        // The DOCX setters are a builder now.
        review
            "1087-docx-setters"
            CodemodMigration.KnowledgePolicies
            CodemodFileClass.Source
            @"\b(?:enableStructuredDocxExtraction|resetToFlatDocxExtraction)\b"
            "the module-level DOCX setters are gone: compose `KnowledgeBase.ServerUploadPolicy.withDocxExtraction DocxExtractionMode.Structured` on the `ServerApp` (it sets `KnowledgePolicies.DocxMode`); an extraction outside the API takes the mode in `ExtractDeps.composed ocr tables mode`"
        // The thirteen RAG validator classes are functions.
        review
            "1087-rag-validator-classes"
            CodemodMigration.KnowledgePolicies
            CodemodFileClass.Source
            @"\b(?:TeamModeLocalEmbedder|RagPersistence|TeamModeSharedEmbeddingCache|RetrievalDefaults|ChunkingConfig|CitationDevEndpoint|RagIngestionInstance|VacuumSchedule|RAGRateLimitConfigured|LocalEmbeddingProviderInProductionMode|RAGHandlersRegistered|RAGConfigBounds|InProcessIndexReplica)Validator\s*\("
            "the RAG validator classes are functions returning `IConfigValidator` (`ConfigValidator.create`): call the lower-case name with the same tupled arguments (`ragPersistenceValidator (config, storage, store)`), drop the `:> IConfigValidator` and any explicit timeout; `ragIngestionInstanceValidator` also takes the overflow policy and the queue's `DroppedLast60s`"
        // The deleted RAG surface has no replacement.
        review
            "1087-deleted-rag-surface"
            CodemodMigration.KnowledgePolicies
            CodemodFileClass.Source
            @"\b(?:withIndexConversations|IndexConversations|withContextualHeader|RecordDequeue)\b"
            "deleted, with no replacement: `RagOptions.withIndexConversations` / `IndexConversations` were never read, `Chunking.withContextualHeader` had no caller, and `IngestionQueue.RecordDequeue` belonged to the drainer seam (`IIngestionQueue.Dequeue` decrements the gauge itself); a test that drained the queue through `.Reader` reads `IIngestionQueue.TryDequeue` / `Dequeue`, and a synchronous `.Enqueue job` is `EnqueueAsync job`"
        // The index read is a result.
        review
            "1087-load-index-result"
            CodemodMigration.KnowledgePolicies
            CodemodFileClass.Source
            @"\bloadIndex\b"
            "`loadIndex storage container` returns `Result<KnowledgeDocument list, IndexReadError>`: an absent index is `Ok []`, an unreadable or corrupt one an `Error` — a decision refuses on it (`IndexReadError.describe`), a listing reads `loadListing logger storage container`"
        // Phase 1075 — the view contract, where the view is written inline.
        review
            "1075-view-lambda"
            CodemodMigration.OneViewContract
            CodemodFileClass.Source
            @"\b(?:withView|withFullWidthView)\s*\(\s*fun\b(?!\s+model\s+ctx\s*->\s*PageContent\.)|\bwithPages\b"
            "if this view is not yet `'Model -> RenderContext<'Msg> -> PageContent`: it takes `model ctx`, dispatches with `ctx.Dispatch`, reads strings from `ctx.Messages`, and returns a `PageContent` — a `(left, right)` pair is `PageContent.SplitPanel(left, right)`, a single pane `PageContent.FullWidth body`"
        review
            "1075-use-messages"
            CodemodMigration.OneViewContract
            CodemodFileClass.Source
            @"\bMessageCatalogProvider\.useMessages\s*\(\s*\)"
            "in a module view, read the resolved catalog from `ctx.Messages` (and the locale from `ctx.Locale`): a view needs no `[<ReactComponent>]` hook site to reach it"
        review
            "1075-toolkit-locale"
            CodemodMigration.OneViewContract
            CodemodFileClass.Source
            @"\bOutputFormatting\.(?:formatCurrency|formatDecimal|formatInt|fallbackLocale)\b|\bInput\.(?:currency|integerInput)\b"
            "the toolkit takes the caller's locale: `OutputFormatting.formatDecimal locale value`, `formatInt locale value`, `formatCurrency locale currencyCode value`, `Input.currency locale …`, `Input.integerInput locale …` — a view passes `ctx.Locale`; `fallbackLocale` is gone"
        review
            "1075-app-shell"
            CodemodMigration.OneViewContract
            CodemodFileClass.Source
            @"\bLayout\.(?:AppShellWith|AppShell|userButton)\b"
            "`Layout.AppShell` takes one `AppShellProps` record — `{ Sidebar = sidebarProps; Content = content; SidePanel = …; HeaderAction = …; IdentityAction = Layout.headerIdentityAction (); InputsWidth = … }`; the positional form and `AppShellWith` are gone, and `Layout.userButton` is the `IdentityAction` slot"
        // The compute twins: the sites a type rename cannot finish.
        review
            "1081-denial-class"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\b\w*[Dd]enial\w*\.SubmitterClass\b"
            "a compute denial is a `BudgetDenial`: the submitter class is `.ClassLabel`, and the denial also carries `Domain` (`ComputeBudget.Domain`). An audit row's `ComputeBudgetDeniedPayload.Denial` keeps `SubmitterClass`: it is the frozen row shape, `ComputeBudgetDeniedFigures`"
        review
            "1081-record-literal"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @":\s*(?:BudgetDenial|BudgetUsage)\s*=\s*\{"
            "a literal of the seam's `BudgetDenial` / `BudgetUsage` that stood for a compute twin needs `Domain = ComputeBudget.Domain`, and a denial names the class `ClassLabel`; prefer `BudgetUsage.empty ComputeBudget.Domain scope period with …`"
        review
            "1081-audit-denial"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\bComputeBudgetDenied\s*\{"
            "`ComputeBudgetDeniedPayload.Denial` is the frozen row shape `ComputeBudgetDeniedFigures`: build it with `ComputeBudgetDeniedFigures.ofDenial denial` from the `BudgetDenial` the policy refused with"
        review
            "1081-period-match"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\|\s*BudgetPeriod\.(?:Perpetual|Daily|Monthly)\s*->"
            "a match over a compute budget's period is a match over `BudgetPeriod`, which has a fourth case, `Hourly`: give it an arm"
        // The AI budget posture and the task registry.
        review
            "1081-read-policy"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\.ReadPolicy\b"
            "if this is an `AIBudgetEnforcer` / `AISpendEnforcer`, `ReadPolicy` returns `Result<policy, string>`: a config-store failure is an `Error`, decided by the declared `BudgetPosture` (`AIOptions.withBudgetPosture`), never read as unbounded"
        review
            "1081-assistant-api"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\baiAssistantApi\b"
            "`aiAssistantApi` takes the server's `AITaskStatusRegistry` after the SSE manager: `aiAssistantApi config contexts sseManager taskRegistry ctx` — pass the composed one, or one `AITaskStatusRegistry()` shared by the requests that must see each other's tasks"
        review
            "1081-task-registry"
            CodemodMigration.OneBudgetVocabulary
            CodemodFileClass.Source
            @"\bAITaskStatusRegistry\.Shared\b"
            "`AITaskStatusRegistry.Shared` is gone: `AICompose` registers one registry per composed server; resolve it from DI (`services.GetService(typeof<AITaskStatusRegistry>)`), or register `AITaskStatusRegistry()` in a hand-built container"
    ]


    // ── The Capabilities member ───────────────────────────────────────
    //
    // A seam that gained an abstract `Capabilities` member needs one line in
    // every implementer. Which line depends on the type it sits in — the
    // record's `none`, or the record naming the capability interfaces the
    // same type (or object expression) already implements — so this pass
    // reads the implementer's whole block rather than one line. A seam is
    // data: its interface, its record, and its capability interfaces with the
    // field each one fills.

    type private CapabilitySeam = {
        Rule: string
        Seam: string
        Record: string
        /// Capability interface name and the record field it fills, in
        /// record-field order.
        Fields: (string * string) list
    }

    let private capabilitySeams: CapabilitySeam list = [
        {
            Rule = "1095-capabilities-member"
            Seam = "IDataSource"
            Record = "ConnectorCapabilities"
            Fields = [ "IWatchableFileSource", "Watchable"; "IEmitsNativePayload", "Native" ]
        }
        {
            Rule = "1094-fit-provider-capabilities-member"
            Seam = "IModelFitProvider"
            Record = "ModelFitProviderCapabilities"
            Fields = [
                "IModelEvaluationMetrics", "Metrics"
                "IModelEvaluationPlanMetrics", "PlanMetrics"
            ]
        }
        {
            Rule = "1094-registry-capabilities-member"
            Seam = "IModelRegistry"
            Record = "RegistryCapabilities"
            Fields = [ "IModelRegistrationNovelty", "Novelty" ]
        }
        {
            Rule = "1094-backend-capabilities-member"
            Seam = "IExternalComputeDispatcher"
            Record = "BackendCapabilities"
            Fields = [ "IExternalCallbackCapableBackend", "Callback" ]
        }
    ]

    let private implementerLine (seam: CapabilitySeam) =
        rx (
            @"^\s*(?<head>interface\s+(?:[\w.]+\.)?"
            + seam.Seam
            + @"|\{\s*new\s+(?:[\w.]+\.)?"
            + seam.Seam
            + @")\s+with\s*$"
        )

    let private capabilityInterface (seam: CapabilitySeam) =
        rx (
            @"\binterface\s+(?:[\w.]+\.)?(?<name>"
            + (seam.Fields |> List.map fst |> String.concat "|")
            + @")\s+with\b"
        )

    let private declaresCapabilities = rx @"\bmember\s+\w+\.Capabilities\b"
    let private typeHeader = rx @"^\s*(?:type|and)\s"
    let private indentOf (line: string) = line.Length - line.TrimStart().Length
    let private isBlank (line: string) = String.IsNullOrWhiteSpace line

    /// The `[first, last)` lines of the block an implementation at `at`
    /// belongs to: an object expression's own lines, or the whole body of the
    /// type it is declared in (where its other interfaces live).
    let private implementerBlock (lines: string[]) (at: int) (isObjectExpression: bool) =
        let k = indentOf lines[at]

        let endOf (start: int) (indent: int) =
            let mutable e = start + 1

            while e < lines.Length && (isBlank lines[e] || indentOf lines[e] > indent) do
                e <- e + 1

            e

        let header =
            if isObjectExpression then
                None
            else
                seq { at - 1 .. -1 .. 0 }
                |> Seq.tryFind (fun h -> not (isBlank lines[h]) && indentOf lines[h] < k && typeHeader.IsMatch lines[h])

        match header with
        | Some h -> h, endOf h (indentOf lines[h])
        | None -> at, endOf at k

    /// The member a block of `seam` declaring `capabilities` (interface
    /// names, in record-field order) gains. In an object expression `this` is
    /// the seam's interface, so its other interfaces are reached by a
    /// downcast; in a type it is the type itself, and an upcast suffices.
    let private capabilitiesMember (seam: CapabilitySeam) (isObjectExpression: bool) (capabilities: string list) =
        match capabilities with
        | [] -> $"member _.Capabilities = {seam.Record}.none"
        | names ->
            let field name =
                let fieldName = seam.Fields |> List.find (fun (i, _) -> i = name) |> snd
                let cast = if isObjectExpression then ":?>" else ":>"
                $"{fieldName} = Some(this {cast} {name})"

            $"""member this.Capabilities = {{ {seam.Record}.none with {names |> List.map field |> String.concat "; "} }}"""

    /// Adds the `Capabilities` member after every implementation of a
    /// capability seam that does not declare one. `segments` alternate
    /// content and terminator (see `lineSplitter`); the member line takes
    /// the implementation line's own terminator.
    let private addCapabilitiesMembers (segments: string[]) : string[] * CodemodRewrite list =
        let lines = [| for i in 0..2 .. segments.Length - 1 -> segments[i] |]
        let out = Array.copy segments
        let rewrites = ResizeArray<CodemodRewrite>()

        for seam in capabilitySeams do
            let implementer = implementerLine seam
            let capability = capabilityInterface seam

            for at in 0 .. lines.Length - 1 do
                let m = implementer.Match lines[at]

                if m.Success then
                    let isObjectExpression = m.Groups["head"].Value.StartsWith "{"
                    let first, last = implementerBlock lines at isObjectExpression

                    let block = lines[first .. last - 1]

                    if not (block |> Array.exists declaresCapabilities.IsMatch) then
                        let implemented =
                            block
                            |> Array.choose (fun l ->
                                let c = capability.Match l
                                if c.Success then Some c.Groups["name"].Value else None)

                        let capabilities =
                            seam.Fields
                            |> List.map fst
                            |> List.filter (fun name -> Array.contains name implemented)

                        let memberIndent =
                            lines[at + 1 ..]
                            |> Array.tryFind (isBlank >> not)
                            |> Option.map indentOf
                            |> Option.filter (fun i -> i > indentOf lines[at])
                            |> Option.defaultValue (indentOf lines[at] + 4)

                        let terminator =
                            if 2 * at + 1 < segments.Length then
                                segments[2 * at + 1]
                            else
                                "\n"

                        let after =
                            lines[at]
                            + terminator
                            + String(' ', memberIndent)
                            + capabilitiesMember seam isObjectExpression capabilities

                        out[2 * at] <- after

                        rewrites.Add {
                            RuleId = seam.Rule
                            Line = at + 1
                            Before = lines[at]
                            After = after
                        }

        out, List.ofSeq rewrites

    // ── Phase 877 — nesting `ServerConfig` record-update expressions ──
    //
    // A record update that sets `Port` and `EntityStore` has to set `Port`
    // and `Stores = { base.Stores with EntityStore = … }` instead, which no
    // line regex can do: the labels of one expression regroup, and the inner
    // base is the outer one. So the pass reads whole expressions. It is
    // deterministic exactly when the base is a plain identifier path
    // (`defaults`, `ServerConfig.defaults`, `app.Config`) and every top-level
    // item is `Label = …`; a base that is an application or a parenthesised
    // expression cannot be re-read as `base.Concern` without a binding, and
    // is reported instead. A label other records carry too (`EventStore` on a
    // deps record) proves nothing on its own: such an expression is nested
    // only behind a base named as the config, or beside a label only
    // `ServerConfig` has, and is reported otherwise.

    /// `{ base with …` on one line (`rest` is what follows `with`).
    let private updateOpener =
        Regex(@"\{\s*(?<base>[^{}]+?)\s+with\b(?<rest>.*)$", RegexOptions.CultureInvariant)

    /// The second line of the two-line opener `{` / `base with`.
    let private withLine =
        Regex(@"^\s*(?<base>[^{}]+?)\s+with\s*$", RegexOptions.CultureInvariant)

    let private simplePath =
        Regex(@"^[A-Za-z_][\w']*(?:\.[A-Za-z_][\w']*)*$", RegexOptions.CultureInvariant)

    let private labelLine =
        Regex(@"^(?<label>[A-Z][\w']*)\s*=(?!=)", RegexOptions.CultureInvariant)

    /// Brace depth change over `text`, skipping string literals and a
    /// trailing `//` comment.
    let private braceDelta (text: string) =
        let mutable depth = 0
        let mutable inString = false
        let mutable i = 0

        while i < text.Length do
            let c = text[i]

            if inString then
                if c = '\\' then
                    i <- i + 1
                elif c = '"' then
                    inString <- false
            elif c = '"' then
                inString <- true
            elif c = '/' && i + 1 < text.Length && text[i + 1] = '/' then
                i <- text.Length
            elif c = '{' then
                depth <- depth + 1
            elif c = '}' then
                depth <- depth - 1

            i <- i + 1

        depth

    /// The index of the `}` that closes the `{` opened before `text`
    /// (depth starts at 1), or -1 when it is not on this line.
    let private closingBrace (text: string) =
        let mutable depth = 1
        let mutable inString = false
        let mutable i = 0
        let mutable found = -1

        while found < 0 && i < text.Length do
            let c = text[i]

            if inString then
                if c = '\\' then
                    i <- i + 1
                elif c = '"' then
                    inString <- false
            elif c = '"' then
                inString <- true
            elif c = '/' && i + 1 < text.Length && text[i + 1] = '/' then
                i <- text.Length
            elif c = '{' || c = '[' || c = '(' then
                depth <- depth + 1
            elif c = '}' || c = ']' || c = ')' then
                depth <- depth - 1

                if depth = 0 then
                    found <- i

            i <- i + 1

        found

    /// Splits single-line items on `;` at depth 0.
    let private splitItems (text: string) =
        let items = ResizeArray<string>()
        let sb = StringBuilder()
        let mutable depth = 0
        let mutable inString = false
        let mutable i = 0

        while i < text.Length do
            let c = text[i]

            if inString then
                if c = '\\' && i + 1 < text.Length then
                    sb.Append(c).Append(text[i + 1]) |> ignore
                    i <- i + 1
                else
                    if c = '"' then
                        inString <- false

                    sb.Append c |> ignore
            elif c = '"' then
                inString <- true
                sb.Append c |> ignore
            elif c = '{' || c = '[' || c = '(' then
                depth <- depth + 1
                sb.Append c |> ignore
            elif c = '}' || c = ']' || c = ')' then
                depth <- depth - 1
                sb.Append c |> ignore
            elif c = ';' && depth = 0 then
                items.Add(sb.ToString())
                sb.Clear() |> ignore
            else
                sb.Append c |> ignore

            i <- i + 1

        items.Add(sb.ToString())
        items |> Seq.map _.Trim() |> Seq.filter (fun s -> s <> "") |> List.ofSeq

    /// One top-level item of a record-update expression: the comment
    /// lines above it, its label, and its lines (the first holds the
    /// label; continuations keep their own indentation).
    type private UpdateItem = {
        Comments: string list
        Label: string
        Lines: string list
    }

    /// One record-update expression the pass read: where it sits, its
    /// base, its items, and the text after its closing brace.
    type private UpdateBlock = {
        First: int
        Last: int
        OpenerPrefix: string
        Base: string
        Items: UpdateItem list
        Suffix: string
    }

    let private indentOf' (line: string) = line.Length - line.TrimStart().Length

    /// Reads the record-update expression opening at `at`, or `None` when
    /// the line opens none. `Error` names why an expression that holds a
    /// moved label cannot be nested mechanically.
    let private readUpdate (lines: string[]) (at: int) : Result<UpdateBlock, string> option =
        let line = lines[at]
        // A record update quoted in a comment is prose, not code.
        let isComment = line.TrimStart().StartsWith "//"
        let oneLine = if isComment then Match.Empty else updateOpener.Match line

        let twoLine =
            if
                not isComment
                && not oneLine.Success
                && line.TrimEnd().EndsWith "{"
                && at + 1 < lines.Length
            then
                let m = withLine.Match lines[at + 1]
                if m.Success then Some m else None
            else
                None

        match oneLine.Success, twoLine with
        | false, None -> None
        | _ ->
            let baseText, rest, itemsFrom, openerPrefix =
                if oneLine.Success then
                    let restText = oneLine.Groups["rest"].Value
                    let prefix = line.Substring(0, oneLine.Index)
                    oneLine.Groups["base"].Value.Trim(), restText, at + 1, prefix
                else
                    twoLine.Value.Groups["base"].Value.Trim(), "", at + 2, line.Substring(0, line.LastIndexOf '{')

            let closeOnOpener = if oneLine.Success then closingBrace rest else -1

            let last, suffix, inner =
                if closeOnOpener >= 0 then
                    at, rest.Substring(closeOnOpener + 1), Some(rest.Substring(0, closeOnOpener))
                else
                    let mutable depth = 1 + (if oneLine.Success then braceDelta rest else 0)

                    let mutable i = itemsFrom
                    let mutable closing = -1

                    while closing < 0 && i < lines.Length do
                        let d = braceDelta lines[i]

                        if depth + d <= 0 then closing <- i else depth <- depth + d
                        i <- i + 1

                    if closing < 0 then
                        -1, "", None
                    else
                        // Where on the closing line the expression ends.
                        let text = lines[closing]
                        let mutable depth' = depth
                        let mutable pos = -1
                        let mutable k = 0

                        while pos < 0 && k < text.Length do
                            if text[k] = '{' then
                                depth' <- depth' + 1
                            elif text[k] = '}' then
                                depth' <- depth' - 1

                                if depth' = 0 then
                                    pos <- k

                            k <- k + 1

                        closing, text.Substring(pos + 1), None

            if last < 0 then
                None
            else
                let items: Result<UpdateItem list, string> =
                    match inner with
                    | Some inline' ->
                        splitItems inline'
                        |> List.map (fun item ->
                            let m = labelLine.Match item

                            if m.Success then
                                Ok {
                                    Comments = []
                                    Label = m.Groups["label"].Value
                                    Lines = [ item ]
                                }
                            else
                                Error(sprintf "an item that is not `Label = …`: `%s`" item))
                        |> List.fold
                            (fun acc r ->
                                match acc, r with
                                | Ok xs, Ok x -> Ok(xs @ [ x ])
                                | Error e, _
                                | _, Error e -> Error e)
                            (Ok [])
                    | None ->
                        let body = [
                            if rest.Trim() <> "" then
                                yield rest.Trim(), true
                            for i in itemsFrom .. last - 1 do
                                yield lines[i], false
                            if last >= itemsFrom then
                                let text = lines[last]
                                let cut = text.Length - suffix.Length - 1
                                let head = text.Substring(0, max 0 cut)

                                if head.Trim() <> "" then
                                    yield head, false
                        ]

                        let fieldIndent =
                            body
                            |> List.tryPick (fun (l, inlineFirst) ->
                                if inlineFirst || l.Trim() = "" then
                                    None
                                else
                                    Some(indentOf' l))
                            |> Option.defaultValue 0

                        let mutable comments = []
                        let mutable current: UpdateItem option = None
                        let items = ResizeArray<UpdateItem>()
                        let mutable fault = None

                        for text, inlineFirst in body do
                            let trimmed = text.Trim()

                            if trimmed = "" then
                                ()
                            elif inlineFirst || indentOf' text = fieldIndent then
                                if trimmed.StartsWith "//" then
                                    comments <- comments @ [ trimmed ]
                                elif trimmed.StartsWith "}" then
                                    match current with
                                    | Some c -> current <- Some { c with Lines = c.Lines @ [ text ] }
                                    | None -> fault <- Some "a `}` before any item"
                                else
                                    let m = labelLine.Match trimmed

                                    if m.Success then
                                        current |> Option.iter items.Add

                                        current <-
                                            Some {
                                                Comments = comments
                                                Label = m.Groups["label"].Value
                                                Lines = [ text ]
                                            }

                                        comments <- []
                                    else
                                        fault <- Some(sprintf "an item that is not `Label = …`: `%s`" trimmed)
                            else
                                match current with
                                | Some c -> current <- Some { c with Lines = c.Lines @ [ text ] }
                                | None -> fault <- Some(sprintf "text before the first item: `%s`" trimmed)

                        current |> Option.iter items.Add

                        match fault with
                        | Some f -> Error f
                        | None -> Ok(List.ofSeq items)

                let movedLabelLine =
                    Regex(sprintf @"^\s*(?:%s)\s*=(?!=)" (alternation movedFields), RegexOptions.CultureInvariant)

                // Whether a TOP-LEVEL item of this expression sets a moved
                // field: the items of a one-line form, else the lines at
                // the field indentation (a nested concern block's own items
                // are deeper, and are that block's business).
                let holdsMovedLabel =
                    match inner with
                    | Some text -> splitItems text |> List.exists movedLabelLine.IsMatch
                    | None ->
                        let candidates = [
                            if rest.Trim() <> "" then
                                yield! splitItems rest
                            for i in itemsFrom..last do
                                yield lines[i]
                        ]

                        let fieldIndent =
                            candidates
                            |> List.tryFind (fun l -> l.Trim() <> "" && not (l.Trim().StartsWith "//"))
                            |> Option.map indentOf'
                            |> Option.defaultValue 0

                        candidates
                        |> List.exists (fun l -> indentOf' l <= fieldIndent && movedLabelLine.IsMatch l)

                let baseIsConfig =
                    simplePath.IsMatch baseText
                    && (match baseText.Split '.' |> Array.last with
                        | "defaults"
                        | "Config"
                        | "config"
                        | "cfg"
                        | "serverConfig"
                        | "conf"
                        | "effective"
                        | "effectiveConfig"
                        | "finalConfig"
                        | "baseConfig"
                        | "merged" -> true
                        | _ -> false)

                match items with
                | _ when not holdsMovedLabel || baseText.StartsWith "new " -> None
                | Error e -> Some(Error e)
                | Ok items when not (items |> List.exists (fun i -> concernOf.ContainsKey i.Label)) -> None
                | Ok items when
                    (match baseText.Split '.' |> Array.last with
                     | seg -> List.contains seg concernNames)
                    ->
                    None
                | Ok items when
                    not baseIsConfig
                    && items
                       |> List.forall (fun i -> not (concernOf.ContainsKey i.Label) || sharedLabels.Contains i.Label)
                    ->
                    Some(
                        Error(
                            sprintf
                                "`%s` is not named as the config and every moved label it sets (%s) is one other records carry too; if it is a `ServerConfig`, nest by hand, otherwise nothing changes"
                                baseText
                                (items
                                 |> List.filter (fun i -> sharedLabels.Contains i.Label)
                                 |> List.map _.Label
                                 |> alternation)
                        )
                    )
                | Ok items when not (simplePath.IsMatch baseText) ->
                    Some(
                        Error(
                            sprintf
                                "the base `%s` is not a plain path, so `%s.<Concern>` cannot be written for the nested record"
                                baseText
                                baseText
                        )
                    )
                | Ok items ->
                    Some(
                        Ok {
                            First = at
                            Last = last
                            OpenerPrefix = openerPrefix
                            Base = baseText
                            Items = items
                            Suffix = suffix
                        }
                    )

    /// The nested rendering of a block, as lines.
    let private renderNested (block: UpdateBlock) : string list =
        // A bare `{` keeps its own column; `Config = {` nests under the
        // line that carries it.
        let openerIndent = indentOf' (block.OpenerPrefix + "x")
        let pad n = String(' ', n)
        let fi = openerIndent + 8

        let shifted (item: UpdateItem) (toIndent: int) =
            let from = item.Lines |> List.head |> indentOf'

            item.Lines
            |> List.mapi (fun i l ->
                if i = 0 then
                    pad toIndent + l.Trim()
                else
                    let current = indentOf' l
                    pad (max 0 (current - from + toIndent)) + l.Trim())

        [
            yield
                (if block.OpenerPrefix.Trim() = "" then
                     pad openerIndent + "{"
                 else
                     block.OpenerPrefix.TrimEnd() + " {")
            yield pad (openerIndent + 4) + block.Base + " with"
            let mutable emitted = Set.empty

            for item in block.Items do
                match concernOf.TryFind item.Label with
                | None ->
                    for c in item.Comments do
                        yield pad fi + c

                    yield! shifted item fi
                | Some concern when emitted.Contains concern -> ()
                | Some concern ->
                    emitted <- emitted.Add concern
                    yield pad fi + concern + " = {"
                    yield pad (fi + 4) + block.Base + "." + concern + " with"

                    for inner in block.Items |> List.filter (fun i -> concernOf.TryFind i.Label = Some concern) do
                        for c in inner.Comments do
                            yield pad (fi + 8) + c

                        yield! shifted inner (fi + 8)

                    yield pad fi + "}"

            yield pad openerIndent + "}" + block.Suffix
        ]

    /// Reads every record-update expression in `lines`, nesting the
    /// provable ones. Answers the rewritten segments, one rewrite per
    /// nested expression and one finding per expression it could not
    /// prove.
    let private nestServerConfigUpdates (segments: string[]) : string[] * CodemodRewrite list * CodemodFinding list =
        let lines = [| for i in 0..2 .. segments.Length - 1 -> segments[i] |]
        let out = Array.copy segments
        let rewrites = ResizeArray<CodemodRewrite>()
        let findings = ResizeArray<CodemodFinding>()
        let mutable at = 0

        while at < lines.Length do
            match readUpdate lines at with
            | None -> at <- at + 1
            | Some(Error why) ->
                findings.Add {
                    RuleId = "877-record-update-unprovable"
                    Migration = CodemodMigration.NestedConfig
                    Line = at + 1
                    Text = lines[at].Trim()
                    Guidance =
                        sprintf
                            "this record update sets a field that moved into a concern record, and the codemod could not nest it: %s. Bind the base to a name and write `<Concern> = { <base>.<Concern> with <Field> = … }` by hand"
                            why
                }

                at <- at + 1
            | Some(Ok block) ->
                let terminator =
                    if 2 * block.First + 1 < segments.Length then
                        segments[2 * block.First + 1]
                    else
                        "\n"

                let rendered = renderNested block
                let after = String.Join(terminator, rendered)
                out[2 * block.First] <- after

                for i in block.First + 1 .. block.Last do
                    out[2 * i] <- ""

                    if 2 * i + 1 < out.Length then
                        out[2 * i + 1] <- ""

                rewrites.Add {
                    RuleId = "877-record-update"
                    Line = block.First + 1
                    Before = lines[block.First]
                    After = after
                }

                at <- block.Last + 1

        out, List.ofSeq rewrites, List.ofSeq findings

    // ── Phase 1074 — `ClientConfig` labels into their concern records ──
    //
    // F# nests a copy-and-update label (`{ c with Shell.ActiveModule = … }`),
    // so a moved `ClientConfig` field is the same item under a qualified
    // label. What no line regex can tell is whether a label belongs to a
    // `ClientConfig` update: `ClientConfigOverrides` and the message
    // catalog carry the same names. So the pass reads the update's BASE —
    // the `{ base with` opener above the label — and moves the labels of an
    // update whose base names a client configuration (`ClientConfig.defaults`,
    // `ClientConfig.create …`, `config`, `cfg`, `clientConfig`), at the
    // update's own label column only (a nested record's labels are not the
    // config's).

    /// The `ClientConfig` fields that moved, as (concern, fields), with the
    /// three renamed on the way listed by their new names in
    /// `clientRenames`. The table is the migration doc's field map.
    let clientConcernFields: (string * string list) list = [
        "Chrome", [ "DatadogReadback"; "Observability"; "PlatformUsers"; "AdminSurface" ]
        "Auth", [ "AuthUI"; "AuthTokenStorage"; "AuthBridge"; "DevDefaultUserId" ]
        "PublicUtility",
        [
            "PrerenderRoutes"
            "ConsentProvider"
            "AdPanel"
            "PremiumModel"
            "PlatformAdminProfile"
        ]
        "Locale", [ "Locale"; "MessageCatalogOverride" ]
        "Shell",
        [
            "LoadingIndicator"
            "ActiveModule"
            "BrowserHistory"
            "NotAuthorisedView"
            "CommandPalette"
            "HomeRecents"
            "ToastCentre"
            "GlobalOverlays"
            "InputsPaneWidth"
            "GridModules"
            "Presence"
            "Offline"
        ]
        "Composition",
        [
            "Handlers"
            "RequestSeam"
            "ModuleFilter"
            "ShowDebugOnlyModules"
            "ExpectedModules"
            "PublicEntryDispatchers"
            "OnError"
            "EnableElmishConsoleTrace"
            "OnElmishError"
        ]
    ]

    /// The moved fields whose name changed with the move.
    let private clientRenames =
        Map [
            "AdminSurface", "AdminArea"
            "AuthTokenStorage", "HttpOnlySession"
            "Locale", "Resolution"
        ]

    /// Field → its qualified path under the concern record.
    let private clientPathOf: Map<string, string> =
        clientConcernFields
        |> List.collect (fun (concern, fields) ->
            fields
            |> List.map (fun f -> f, concern + "." + (clientRenames.TryFind f |> Option.defaultValue f)))
        |> Map.ofList

    /// The fields a server or catalog record also carries: a READ of one is
    /// not provably a `ClientConfig` read, so it is reported, not moved.
    let private clientSharedReads =
        set [
            "Observability"
            "DatadogReadback"
            "PlatformUsers"
            "Presence"
            "ModuleFilter"
            "ExpectedModules"
            "Locale"
            "Handlers"
            "Offline"
        ]

    let private clientUpdateOpener =
        Regex(@"\{\s*(?<base>[^{}]+?)\s+with\b(?<rest>.*)$", RegexOptions.CultureInvariant)

    let private clientUpdateWithLine =
        Regex(@"^\s*(?<base>[^{}]+?)\s+with\s*$", RegexOptions.CultureInvariant)

    let private clientBase =
        Regex(
            @"^(?:ClientConfig\.(?:defaults|create\b.*)|config|cfg|clientConfig|baseConfig|clientCfg|ctx\.Config)$",
            RegexOptions.CultureInvariant
        )

    let private clientLabel =
        Regex(
            sprintf @"(?<![\w.])(?<label>%s)(?=\s*=(?!=))" (String.Join("|", clientPathOf |> Map.keys)),
            RegexOptions.CultureInvariant
        )

    /// A moved field read off a receiver named as the client config.
    let private clientRead =
        Regex(
            sprintf
                @"(?<receiver>(?<![\w.])(?:clientConfig|config|cfg|ClientConfig\.defaults|ctx\.Config))\.(?<field>%s)\b"
                (String.Join(
                    "|",
                    clientPathOf
                    |> Map.keys
                    |> Seq.filter (fun f -> not (clientSharedReads.Contains f))
                )),
            RegexOptions.CultureInvariant
        )

    let private indentOfLine (line: string) = line.Length - line.TrimStart().Length

    /// Moves the labels of every `ClientConfig` copy-and-update in
    /// `segments` (alternating content / terminator) and the reads off a
    /// receiver named as the config. Answers the rewritten segments and one
    /// rewrite per changed line.
    let private nestClientConfigLabels (segments: string[]) : string[] * CodemodRewrite list =
        let out = Array.copy segments
        let rewrites = ResizeArray<CodemodRewrite>()
        // (opener indent, is a client update, the label column once seen)
        let blocks = System.Collections.Generic.Stack<int * bool * int option ref>()

        for i in 0..2 .. segments.Length - 1 do
            let line = segments[i]
            let trimmed = line.TrimStart()
            let isComment = trimmed.StartsWith "//"
            let indent = indentOfLine line

            if not isComment && trimmed <> "" then
                let closes () =
                    let openIndent, _, _ = blocks.Peek()
                    indent <= openIndent

                while blocks.Count > 0 && closes () do
                    blocks.Pop() |> ignore

            let mutable after = line

            if not isComment then
                let one = clientUpdateOpener.Match line

                let two =
                    if one.Success then
                        Match.Empty
                    else
                        clientUpdateWithLine.Match line

                // A label of the enclosing client update moves even when its
                // own value opens a record (`Handlers = { … with … }`).
                if blocks.Count > 0 then
                    let _, isClient, column = blocks.Peek()

                    if isClient then
                        if
                            column.Value.IsNone
                            && (labelLine.IsMatch trimmed || not (one.Success || two.Success))
                        then
                            column.Value <- Some indent

                        if column.Value = Some indent then
                            let m = clientLabel.Match line

                            if m.Success && m.Index = indent then
                                after <-
                                    line.Substring(0, m.Index)
                                    + clientPathOf[m.Value]
                                    + line.Substring(m.Index + m.Length)

                let opener = clientUpdateOpener.Match after

                if opener.Success then
                    let isClient = clientBase.IsMatch(opener.Groups["base"].Value.Trim())
                    let rest = opener.Groups["rest"]

                    if isClient then
                        let moved = clientLabel.Replace(rest.Value, (fun m -> clientPathOf[m.Value]))
                        after <- after.Substring(0, rest.Index) + moved

                    if not (rest.Value.Contains "}") then
                        blocks.Push(indent, isClient, ref None)
                elif two.Success then
                    blocks.Push(indent, clientBase.IsMatch(two.Groups["base"].Value.Trim()), ref None)

                after <-
                    clientRead.Replace(
                        after,
                        (fun m -> m.Groups["receiver"].Value + "." + clientPathOf[m.Groups["field"].Value])
                    )

            if after <> line then
                out[i] <- after

                rewrites.Add {
                    RuleId = "1074-client-config-field"
                    Line = i / 2 + 1
                    Before = line
                    After = after
                }

        out, List.ofSeq rewrites

    // ── Per-file decision ─────────────────────────────────────────────

    /// The file class a path belongs to, or `None` when the walk skips it.
    let classify (path: string) =
        match Path.GetExtension(path).ToLowerInvariant() with
        | ".fs"
        | ".fsx" -> Some CodemodFileClass.Source
        | ".fsproj"
        | ".props"
        | ".targets" -> Some CodemodFileClass.Project
        | _ -> None

    /// Splits text into alternating content / terminator segments so the
    /// terminators (`\r\n`, `\n`, `\r`) are re-emitted verbatim.
    let private lineSplitter = Regex(@"(\r\n|\n|\r)", RegexOptions.CultureInvariant)

    /// Applies every rewrite rule of `cls` to `text`, line by line, for a
    /// file its compiling projects let `reach` (the Phase 1198 pass
    /// derives opens within it; see `SubsystemNamespaces.ProjectScope`).
    /// Returns the rewritten text and one record per (line, rule) that
    /// changed it.
    let rewriteTextIn
        (reach: SubsystemNamespaces.FileReach)
        (only: CodemodMigration list)
        (cls: CodemodFileClass)
        (text: string)
        : string * CodemodRewrite list =
        let selected (migration: CodemodMigration) =
            match migration with
            // Table-driven and derived, so never implied by "every
            // migration": a consumer names it.
            | CodemodMigration.SubsystemNamespaces -> List.contains migration only
            | _ -> only.IsEmpty || List.contains migration only

        let rules =
            rewriteRules |> List.filter (fun r -> r.Applies = cls && selected r.Migration)

        let segments = lineSplitter.Split text
        let rewrites = ResizeArray<CodemodRewrite>()

        let rewritten =
            segments
            |> Array.mapi (fun i segment ->
                if i % 2 = 1 then
                    segment
                else
                    let lineNumber = i / 2 + 1

                    rules
                    |> List.fold
                        (fun (line: string) rule ->
                            let guarded =
                                match rule.Unless with
                                | Some unless -> unless.IsMatch line
                                | None -> false

                            if guarded || not (rule.Pattern.IsMatch line) then
                                line
                            else
                                let after = rule.Pattern.Replace(line, MatchEvaluator rule.Rewrite)

                                if after <> line then
                                    rewrites.Add {
                                        RuleId = rule.Id
                                        Line = lineNumber
                                        Before = line
                                        After = after
                                    }

                                after)
                        segment)
            |> String.concat ""

        match cls with
        | CodemodFileClass.Source ->
            let nested, nestedRewrites =
                if selected CodemodMigration.NestedConfig then
                    let segments, nestedRewrites, _ =
                        nestServerConfigUpdates (lineSplitter.Split rewritten)

                    segments, nestedRewrites
                else
                    lineSplitter.Split rewritten, []

            let clientNested, clientRewrites =
                if selected CodemodMigration.ClientConfigByConcern then
                    nestClientConfigLabels nested
                else
                    nested, []

            let withMembers, added =
                if selected CodemodMigration.ModelCapabilities then
                    addCapabilitiesMembers clientNested
                else
                    clientNested, []

            let withNamespaces, derived =
                if selected CodemodMigration.SubsystemNamespaces then
                    let segments, moves =
                        SubsystemNamespaces.rewrite SubsystemNamespaces.embeddedIndex.Value reach withMembers

                    segments,
                    moves
                    |> List.map (fun (rule, line, before, after) -> {
                        RuleId = rule
                        Line = line + 1
                        Before = before
                        After = after
                    })
                else
                    withMembers, []

            String.concat "" withNamespaces,
            List.ofSeq rewrites @ nestedRewrites @ clientRewrites @ added @ derived
            |> List.sortBy _.Line
        | CodemodFileClass.Project -> rewritten, List.ofSeq rewrites

    /// `rewriteTextIn` for a file whose project reaches the server tier.
    let rewriteTextFor (only: CodemodMigration list) (cls: CodemodFileClass) (text: string) =
        rewriteTextIn SubsystemNamespaces.consumerReach only cls text

    /// Applies every rewrite rule of `cls` to `text`, line by line.
    let rewriteText (cls: CodemodFileClass) (text: string) : string * CodemodRewrite list = rewriteTextFor [] cls text

    /// The findings the nesting pass raises over `text` — the REWRITTEN
    /// text, like every review finding, so a second run over migrated
    /// source reports the same lines as the first.
    let private nestFindings
        (only: CodemodMigration list)
        (cls: CodemodFileClass)
        (text: string)
        : CodemodFinding list =
        match cls with
        | CodemodFileClass.Source when only.IsEmpty || List.contains CodemodMigration.NestedConfig only ->
            let _, _, findings = nestServerConfigUpdates (lineSplitter.Split text)
            findings
        | _ -> []

    /// Every review-rule match over `text` (the REWRITTEN text), in line
    /// order then rule order.
    let reviewTextFor (only: CodemodMigration list) (cls: CodemodFileClass) (text: string) : CodemodFinding list =
        let rules =
            reviewRules
            |> List.filter (fun r -> r.Applies = cls && (only.IsEmpty || List.contains r.Migration only))

        let segments = lineSplitter.Split text

        [
            for i in 0..2 .. segments.Length - 1 do
                let line = segments[i]

                for rule in rules do
                    if rule.Pattern.IsMatch line then
                        {
                            RuleId = rule.Id
                            Migration = rule.Migration
                            Line = i / 2 + 1
                            Text = line.Trim()
                            Guidance = rule.Guidance
                        }
        ]

    let private utf8Bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]

    let private hasBom (bytes: byte[]) =
        bytes.Length >= 3
        && bytes[0] = utf8Bom[0]
        && bytes[1] = utf8Bom[1]
        && bytes[2] = utf8Bom[2]

    let private toRelative (root: string) (path: string) =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    /// Decides one file: read, rewrite, review. Pure apart from the read.
    /// `scope` says which projects under the root reach the server tier
    /// (read once per plan); `None` decides it for this file's tree alone.
    let analyseFileIn
        (scope: SubsystemNamespaces.ProjectScope.Scope option)
        (only: CodemodMigration list)
        (root: string)
        (cls: CodemodFileClass)
        (path: string)
        : CodemodFileResult =
        let bytes = File.ReadAllBytes path
        let bom = hasBom bytes

        let original =
            Encoding.UTF8.GetString(bytes, (if bom then 3 else 0), bytes.Length - (if bom then 3 else 0))

        let reach =
            if List.contains CodemodMigration.SubsystemNamespaces only then
                let scope =
                    scope
                    |> Option.defaultWith (fun () -> SubsystemNamespaces.ProjectScope.ofTree root)

                SubsystemNamespaces.ProjectScope.reachOf scope path
            else
                SubsystemNamespaces.consumerReach

        let rewritten, rewrites = rewriteTextIn reach only cls original

        // Phase 1198: an open the move leaves dead is reported, never deleted.
        let deadOpens =
            if
                List.contains CodemodMigration.SubsystemNamespaces only
                && cls = CodemodFileClass.Source
            then
                SubsystemNamespaces.likelyDeadOpens SubsystemNamespaces.embeddedIndex.Value reach rewritten
                |> List.map (fun (line, text) -> {
                    RuleId = "1198-likely-dead-open"
                    Migration = CodemodMigration.SubsystemNamespaces
                    Line = line
                    Text = text
                    Guidance =
                        "Nothing in the code this open covers names anything it reaches, so it looks dead after the move. Remove it if the file still compiles: record labels and extension members are not read here."
                })
            else
                []

        {
            Path = Path.GetFullPath path
            RelativePath = toRelative root path
            Class = cls
            Original = original
            Rewritten = rewritten
            HasBom = bom
            Rewrites = rewrites
            Findings =
                reviewTextFor only cls rewritten @ nestFindings only cls rewritten @ deadOpens
                |> List.sortBy _.Line
        }

    /// Decides one file, deciding its project's reach for itself.
    let analyseFileFor (only: CodemodMigration list) (root: string) (cls: CodemodFileClass) (path: string) =
        analyseFileIn None only root cls path

    /// Every review-rule match over `text` (the REWRITTEN text).
    let reviewText (cls: CodemodFileClass) (text: string) : CodemodFinding list = reviewTextFor [] cls text

    /// Decides one file under every migration.
    let analyseFile (root: string) (cls: CodemodFileClass) (path: string) : CodemodFileResult =
        analyseFileFor [] root cls path

    // ── The walk ──────────────────────────────────────────────────────

    /// Directories the walk prunes rather than enumerates: build output,
    /// package caches and VCS metadata carry no consumer source, and an
    /// unpruned `node_modules` is the difference between a second and a
    /// minute.
    let prunedDirectories =
        set [ "bin"; "obj"; "node_modules"; ".git"; ".fable"; ".vs"; ".idea"; "packages" ]

    /// Every codemod-eligible file under `root`, absolute, sorted by
    /// relative path so the plan, the diff and the report are stable.
    let walk (root: string) : (CodemodFileClass * string) list =
        let rec visit (dir: string) = seq {
            for file in Directory.EnumerateFiles dir do
                match classify file with
                | Some cls -> yield cls, file
                | None -> ()

            for sub in Directory.EnumerateDirectories dir do
                if not (prunedDirectories.Contains(Path.GetFileName sub)) then
                    yield! visit sub
        }

        let full = Path.GetFullPath root

        visit full
        |> Seq.sortWith (fun (_, a) (_, b) -> String.CompareOrdinal(toRelative full a, toRelative full b))
        |> List.ofSeq

    /// Decides the whole tree under `root`. Pure apart from reads: no
    /// file is written until `apply`.
    let planFor (only: CodemodMigration list) (root: string) : CodemodPlan =
        if not (Directory.Exists root) then
            failwithf "Codemod: %s is not a directory" root

        let full = Path.GetFullPath root

        let scope =
            if List.contains CodemodMigration.SubsystemNamespaces only then
                Some(SubsystemNamespaces.ProjectScope.ofTree full)
            else
                None

        {
            Root = full
            Files =
                walk full
                |> List.map (fun (cls, path) -> analyseFileIn scope only full cls path)
        }

    /// Decides the whole tree under `root` under every migration.
    let plan (root: string) : CodemodPlan = planFor [] root

    /// The migration a `--phase` argument names, by its label's number
    /// (`877` for `Phase 877`), or `None` when no migration carries it.
    let migrationOfPhase (phase: string) : CodemodMigration option =
        [
            CodemodMigration.Remoting
            CodemodMigration.Surfaces
            CodemodMigration.ApiStability
            CodemodMigration.Deprecations
            CodemodMigration.ShellSlots
            CodemodMigration.AgentLoop
            CodemodMigration.Capabilities
            CodemodMigration.ModelCapabilities
            CodemodMigration.RemotingLeftovers
            CodemodMigration.JobOutcomes
            CodemodMigration.OneScopeForm
            CodemodMigration.BackgroundPlacement
            CodemodMigration.CompanionOptions
            CodemodMigration.NestedConfig
            CodemodMigration.TypedIdentity
            CodemodMigration.SubsystemNamespaces
            CodemodMigration.RequestPipeline
            CodemodMigration.RateLimitAdmission
            CodemodMigration.RemoteAndMemo
            CodemodMigration.ConnectorKernel
            CodemodMigration.FailureAsData
            CodemodMigration.AuditFailureAsData
            CodemodMigration.ModuleHost
            CodemodMigration.OpenAuditRegistry
            CodemodMigration.AdminPageKit
            CodemodMigration.SnapshotIndex
            CodemodMigration.AccessContextRefuses
            CodemodMigration.IngestionAttempt
            CodemodMigration.ClientConfigByConcern
            CodemodMigration.OneResolverOneGate
            CodemodMigration.KnowledgePolicies
            CodemodMigration.OneViewContract
            CodemodMigration.OneBudgetVocabulary
        ]
        |> List.tryFind (fun m -> CodemodMigration.label m = "Phase " + phase.Trim())

    /// Writes every changed file in `plan`, preserving its BOM and line
    /// terminators, and returns the absolute paths written (in plan
    /// order). Unchanged files are not touched.
    let apply (plan: CodemodPlan) : string list = [
        for file in plan.ChangedFiles do
            File.WriteAllText(file.Path, file.Rewritten, UTF8Encoding file.HasBom)
            file.Path
    ]

    // ── Renderings ────────────────────────────────────────────────────

    /// The `--check` rendering: per changed file, every rewritten line as
    /// a `-`/`+` pair with its rule, in file order. Empty-plan text says
    /// so rather than printing nothing.
    let renderDiff (plan: CodemodPlan) : string =
        let sb = StringBuilder()

        match plan.ChangedFiles with
        | [] ->
            sb.AppendLine "Codemod: no rewrites pending — every deterministic rename is already applied."
            |> ignore
        | changed ->
            for file in changed do
                sb.AppendLine(sprintf "--- a/%s" file.RelativePath) |> ignore
                sb.AppendLine(sprintf "+++ b/%s" file.RelativePath) |> ignore

                for r in file.Rewrites do
                    sb.AppendLine(sprintf "@@ L%d [%s]" r.Line r.RuleId) |> ignore
                    sb.AppendLine("-" + r.Before) |> ignore

                    // A rewrite that inserts a line carries it
                    // after a terminator: each line gets its own `+`.
                    for line in lineSplitter.Split r.After |> Array.chunkBySize 2 |> Array.map Array.head do
                        sb.AppendLine("+" + line) |> ignore

            let rewriteCount = changed |> List.sumBy (fun f -> f.Rewrites.Length)

            sb.AppendLine(sprintf "Codemod: %d rewrite(s) across %d file(s) pending." rewriteCount changed.Length)
            |> ignore

        sb.ToString()

    /// The per-file findings list: every site left for human review with
    /// its rule, line, text and guidance, grouped by file in plan order,
    /// then the migration docs to read. Says so when there is nothing.
    let renderFindings (plan: CodemodPlan) : string =
        let sb = StringBuilder()
        let withFindings = plan.Files |> List.filter (fun f -> not f.Findings.IsEmpty)

        match withFindings with
        | [] -> sb.AppendLine "Codemod: no sites need human review." |> ignore
        | files ->
            let total = files |> List.sumBy (fun f -> f.Findings.Length)

            sb.AppendLine(
                sprintf "Codemod: %d site(s) in %d file(s) need human review (not rewritten):" total files.Length
            )
            |> ignore

            for file in files do
                sb.AppendLine(sprintf "  %s" file.RelativePath) |> ignore

                for x in file.Findings do
                    sb.AppendLine(sprintf "    L%d [%s] %s" x.Line x.RuleId x.Text) |> ignore

                    sb.AppendLine(sprintf "      -> %s: %s" (CodemodMigration.label x.Migration) x.Guidance)
                    |> ignore

            let docs =
                files
                |> List.collect (fun f -> f.Findings |> List.map _.Migration)
                |> List.distinct
                |> List.sortBy CodemodMigration.label

            sb.AppendLine "  Read:" |> ignore

            for d in docs do
                sb.AppendLine(sprintf "    %s — %s" (CodemodMigration.label d) (CodemodMigration.doc d))
                |> ignore

        sb.ToString()

    /// The one-line summary of what `apply` wrote (or would write).
    let renderSummary (plan: CodemodPlan) : string =
        let changed = plan.ChangedFiles
        let rewriteCount = changed |> List.sumBy (fun f -> f.Rewrites.Length)
        let findingCount = plan.AllFindings.Length

        sprintf
            "Codemod: %d file(s) visited, %d rewrite(s) in %d file(s), %d site(s) for review."
            plan.Files.Length
            rewriteCount
            changed.Length
            findingCount