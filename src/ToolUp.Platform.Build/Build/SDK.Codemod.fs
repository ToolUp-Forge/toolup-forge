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
    /// Phase 73 — `Fable.Remoting.*` → `ToolUp.Remoting.*`, `Elmish` → `ToolUp.Elmish`.
    | Remoting
    /// Phase 66 — `PlatformMode` → `Subject` / `SurfaceProfile` / `SurfaceRequirement`.
    | Surfaces
    /// Phase 11.C.5 — package-id renames (Tier 2) and interface-shape renames (Tier 3).
    | ApiStability
    /// Phase 815 — the seven 0.x deprecations removed at the 1.0 cut.
    | Deprecations

/// Helpers over `CodemodMigration`.
module CodemodMigration =
    /// The migration doc a finding cites, repo-relative under `docs/migrations/`.
    let doc migration =
        match migration with
        | CodemodMigration.Remoting -> "docs/migrations/73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md"
        | CodemodMigration.Surfaces -> "docs/migrations/0.X.0-platform-mode-to-surfaces.md"
        | CodemodMigration.ApiStability -> "docs/migrations/11-C-5-public-api-stability-cluster.md"
        | CodemodMigration.Deprecations -> "docs/migrations/815-remove-open-deprecations.md"

    /// The phase label the renderings print.
    let label migration =
        match migration with
        | CodemodMigration.Remoting -> "Phase 73"
        | CodemodMigration.Surfaces -> "Phase 66"
        | CodemodMigration.ApiStability -> "Phase 11.C.5"
        | CodemodMigration.Deprecations -> "Phase 815"

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

/// Phase 183 — the consumer codemod for the Epoch-1 `0.x` breaking renames.
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

    // ── The deterministic rewrites ────────────────────────────────────

    /// Every rewrite rule, in application order. Ordering matters only
    /// where two rules could see one token; none do — the Phase 73 open
    /// rule and qualified-access rule are disjoint by construction (one
    /// anchors on `open`, the other excludes it via its lookbehind on
    /// whitespace-only prefixes), and no rule's output is another's input.
    let rewriteRules: CodemodRewriteRule list = [
        // Phase 73 — Remoting. The five namespaces the fork carries. Any
        // OTHER `Fable.Remoting.X` (AspNetCore, DotnetClient, Suave, …)
        // has no ToolUp counterpart and is a review finding below.
        rewrite
            "73-remoting-namespace"
            CodemodMigration.Remoting
            CodemodFileClass.Source
            @"\bFable\.Remoting\.(Server|Client|Json|Giraffe|MsgPack)\b"
            "ToolUp.Remoting.$1"
            "`Fable.Remoting.{Server,Client,Json,Giraffe,MsgPack}` → `ToolUp.Remoting.*` (opens and fully-qualified references)"
        // Phase 73 — Elmish opens. Exactly the three the runtime carries
        // (`Elmish`, `Elmish.React`, `Elmish.HMR`); `open Elmish.Navigation`
        // and the other upstream sub-packages are review findings.
        rewrite
            "73-elmish-open"
            CodemodMigration.Remoting
            CodemodFileClass.Source
            @"^(\s*open\s+)Elmish(\.React|\.HMR)?(?![\w.])"
            "${1}ToolUp.Elmish$2"
            "`open Elmish` / `open Elmish.React` / `open Elmish.HMR` → `open ToolUp.Elmish` / `.React` / `.HMR`"
        // Phase 73 — Elmish qualified access (`Elmish.Program.mkProgram`,
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
        // Phase 66 — env var + Vite define. `TOOLUP_PLATFORM_MODE` inside
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
        // Phase 815 — the AG Grid / AG Charts compat re-exports. The
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
        // Phase 815 — `Program.withErrorHandler onError` in its pipeline
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
        // Phase 815 — `Program.withConsoleTrace` took nothing but the
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
    ]

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

    /// Applies every rewrite rule of `cls` to `text`, line by line.
    /// Returns the rewritten text and one record per (line, rule) that
    /// changed it.
    let rewriteText (cls: CodemodFileClass) (text: string) : string * CodemodRewrite list =
        let rules = rewriteRules |> List.filter (fun r -> r.Applies = cls)
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

        rewritten, List.ofSeq rewrites

    /// Every review-rule match over `text` (the REWRITTEN text), in line
    /// order then rule order.
    let reviewText (cls: CodemodFileClass) (text: string) : CodemodFinding list =
        let rules = reviewRules |> List.filter (fun r -> r.Applies = cls)
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
    let analyseFile (root: string) (cls: CodemodFileClass) (path: string) : CodemodFileResult =
        let bytes = File.ReadAllBytes path
        let bom = hasBom bytes

        let original =
            Encoding.UTF8.GetString(bytes, (if bom then 3 else 0), bytes.Length - (if bom then 3 else 0))

        let rewritten, rewrites = rewriteText cls original

        {
            Path = Path.GetFullPath path
            RelativePath = toRelative root path
            Class = cls
            Original = original
            Rewritten = rewritten
            HasBom = bom
            Rewrites = rewrites
            Findings = reviewText cls rewritten
        }

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
    let plan (root: string) : CodemodPlan =
        if not (Directory.Exists root) then
            failwithf "Codemod: %s is not a directory" root

        let full = Path.GetFullPath root

        {
            Root = full
            Files = walk full |> List.map (fun (cls, path) -> analyseFile full cls path)
        }

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
                    sb.AppendLine("+" + r.After) |> ignore

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