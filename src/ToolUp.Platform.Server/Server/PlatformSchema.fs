module ToolUp.Platform.PlatformSchema

open ToolUp.Platform
open ToolUp.Platform.Usage

/// SDK-shipped default `_platform` schema. Currently declares one
/// field (`currencySymbol`) consumed by every module that renders
/// monetary values via `Visualisation.PlatformDefaults`. Apps can
/// extend the platform schema by registering their own `_platform`
/// entry in `ServerConfig.ModuleConfigs` — `mergePlatformSchema`
/// joins them at compose time so admin-UI form rendering and per-
/// scope reads see the union.
let internal sdkDefaultPlatformSchema: ModuleConfigEntry = {
    ModuleKey = ConfigKeys.PlatformModuleKey
    DisplayName = "Platform Defaults"
    Schema = {
        Fields = [
            {
                Key = "currencySymbol"
                DisplayName = "Currency symbol"
                Description =
                    Some "Symbol prefixed to monetary values in charts and reports (e.g. £, $, €). Up to 4 characters."
                Kind = ConfigFieldKind.String(Some 4)
                Required = false
                DefaultJson = "\"£\""
            }
        ]
    }
}

/// Merge the SDK-shipped `_platform` schema with the app-supplied
/// `ServerConfig.ModuleConfigs`. App-declared fields with the same
/// `Key` win on collision so a deployment can override a default;
/// SDK fields the app didn't redeclare are appended so platform-
/// default fields can't be silently lost. When the app supplies no
/// `_platform` entry at all, the SDK default is prepended so the
/// platform tab always exists in the admin UI.
let mergePlatformSchema (appEntries: ModuleConfigEntry list) : ModuleConfigEntry list =
    match appEntries |> List.tryFind (fun e -> e.ModuleKey = ConfigKeys.PlatformModuleKey) with
    | None -> sdkDefaultPlatformSchema :: appEntries
    | Some appPlatform ->
        let appKeys = appPlatform.Schema.Fields |> List.map _.Key |> Set.ofList

        let extraSdkFields =
            sdkDefaultPlatformSchema.Schema.Fields
            |> List.filter (fun f -> not (Set.contains f.Key appKeys))

        let merged = {
            appPlatform with
                Schema = {
                    Fields = appPlatform.Schema.Fields @ extraSdkFields
                }
        }

        appEntries
        |> List.map (fun e ->
            if e.ModuleKey = ConfigKeys.PlatformModuleKey then
                merged
            else
                e)

/// SDK-shipped per-team branding fields (Phase 5e). These extend the
/// reserved `_platform` schema — they surface on the existing Platform
/// Defaults admin tab rather than a dedicated module key, because they
/// are app-level chrome an Owner edits alongside the other platform
/// defaults. Every field is `Required = false` with a blank
/// `DefaultJson`: a blank stored value means "use the deployment
/// default", which the client resolves from `ClientConfig` via
/// `Branding.resolve`. So a team that customises nothing renders
/// byte-for-byte as before.
///
/// `mergeBrandingSchema` appends these only when the deployment is
/// team-scoped (`DeploymentConfig.hasTeamScope`) — branding is
/// meaningless without a team to scope it to, so Anonymous / Individual
/// / AuthenticatedEphemeral deployments never see the fields (GP 13).
let internal sdkBrandingFields: ConfigFieldSchema list = [
    {
        Key = ConfigKeys.BrandingKeys.AppName
        DisplayName = "Application name"
        Description =
            Some
                "Name shown in the sidebar header and browser tab for this team. Up to 60 characters. Leave blank to use the deployment default."
        Kind = ConfigFieldKind.String(Some 60)
        Required = false
        DefaultJson = "\"\""
    }
    {
        Key = ConfigKeys.BrandingKeys.PrimaryColor
        DisplayName = "Primary colour"
        Description =
            Some
                "Brand primary colour as a hex value (e.g. #2563eb). Applied as the `--brand-primary` CSS custom property. Leave blank to use the deployment default."
        Kind = ConfigFieldKind.String(Some 7)
        Required = false
        DefaultJson = "\"\""
    }
    {
        Key = ConfigKeys.BrandingKeys.LogoUrl
        DisplayName = "Logo URL"
        Description =
            Some
                "URL (or app-relative path) of the logo shown in the sidebar header. Up to 512 characters. Leave blank to use the deployment default."
        Kind = ConfigFieldKind.String(Some 512)
        Required = false
        DefaultJson = "\"\""
    }
    {
        Key = ConfigKeys.BrandingKeys.FaviconUrl
        DisplayName = "Favicon URL"
        Description =
            Some
                "URL (or app-relative path) of the browser-tab favicon. Up to 512 characters. Leave blank to use the deployment default (the logo)."
        Kind = ConfigFieldKind.String(Some 512)
        Required = false
        DefaultJson = "\"\""
    }
]

/// Merge the Phase 5e branding fields into the `_platform` entry of the
/// app-supplied `ServerConfig.ModuleConfigs`. Same collision semantics
/// as `mergePlatformSchema`: an app-declared field with the same `Key`
/// wins, SDK fields the app didn't redeclare are appended. When no
/// `_platform` entry exists yet (e.g. `IncludePlatformDefaults = false`),
/// a branding-only `_platform` entry is prepended so the fields still
/// surface on the Platform Defaults tab. Called from `compose` only on
/// team-scoped deployments.
let mergeBrandingSchema (appEntries: ModuleConfigEntry list) : ModuleConfigEntry list =
    match appEntries |> List.tryFind (fun e -> e.ModuleKey = ConfigKeys.PlatformModuleKey) with
    | None ->
        let brandingEntry = {
            ModuleKey = ConfigKeys.PlatformModuleKey
            DisplayName = "Platform Defaults"
            Schema = { Fields = sdkBrandingFields }
        }

        brandingEntry :: appEntries
    | Some platform ->
        let existingKeys = platform.Schema.Fields |> List.map _.Key |> Set.ofList

        let extraFields =
            sdkBrandingFields
            |> List.filter (fun f -> not (Set.contains f.Key existingKeys))

        let merged = {
            platform with
                Schema = {
                    Fields = platform.Schema.Fields @ extraFields
                }
        }

        appEntries
        |> List.map (fun e ->
            if e.ModuleKey = ConfigKeys.PlatformModuleKey then
                merged
            else
                e)

/// SDK-shipped default `_platform.notification_prefs` schema (Phase 6f).
/// Declares the per-team kill switches for transactional email / SMS /
/// push delivery and the `From:` address used by every email sink.
/// Defaults are intentionally `false` on every `*.enabled` flag — the
/// SDK ships in safe-quiet mode; admins explicitly opt teams in.
///
/// `TransactionalDispatcher` reads this schema's effective values via
/// `IConfigStore.GetEffective` before invoking any sink. A `false`
/// kill switch short-circuits to `Skipped "team_opted_out"` with no
/// audit emission and no retry — the team has not authorised outbound
/// delivery, so we treat the publish as if it never happened.
///
/// `email.fromAddress` is intentionally optional (`Required = false`).
/// Sinks fall back to a vendor / env-supplied default; an unset value
/// against an SMTP sink with no env override results in
/// `PermanentFailure "no from address configured"` at first attempt,
/// which surfaces as `NotificationDeliveryFailed` in the audit trail.
let internal sdkNotificationPrefsSchema: ModuleConfigEntry = {
    ModuleKey = ConfigKeys.NotificationPrefsModuleKey
    DisplayName = "Notification Preferences"
    Schema = {
        Fields = [
            {
                Key = ConfigKeys.NotificationPrefsKeys.EmailEnabled
                DisplayName = "Email notifications enabled"
                Description =
                    Some
                        "Team-wide kill switch for transactional email (job completion, alert digests, member events). When off, no email is sent regardless of source."
                Kind = ConfigFieldKind.Bool
                Required = true
                DefaultJson = "false"
            }
            {
                Key = ConfigKeys.NotificationPrefsKeys.EmailFromAddress
                DisplayName = "Email From: address"
                Description =
                    Some
                        "Address used as the From: header on outbound transactional email. Leave blank to use the deployment-level default supplied by the email sink companion."
                Kind = ConfigFieldKind.String(Some 254)
                Required = false
                DefaultJson = "\"\""
            }
            {
                Key = ConfigKeys.NotificationPrefsKeys.SmsEnabled
                DisplayName = "SMS notifications enabled"
                Description =
                    Some
                        "Team-wide kill switch for transactional SMS. SMS is billed per-message by the upstream vendor (e.g. Twilio); leaving off avoids surprise charges."
                Kind = ConfigFieldKind.Bool
                Required = true
                DefaultJson = "false"
            }
            {
                Key = ConfigKeys.NotificationPrefsKeys.PushEnabled
                DisplayName = "Mobile push enabled"
                Description =
                    Some
                        "Team-wide kill switch for mobile push notifications. Requires registered push tokens via the address book; users without registered devices are silently skipped."
                Kind = ConfigFieldKind.Bool
                Required = true
                DefaultJson = "false"
            }
            // Phase 30d — Schema-only sandbox row cap. Clamps
            // `IDataCatalog.GetSyntheticSample`'s `count` parameter
            // for `ModulePermission.SchemaOnly` callers. Sits on this
            // schema because `_platform.notification_prefs` is the
            // established per-scope partner-policy lane; the field
            // name is unrelated to notification delivery despite the
            // schema's name. Default `100` matches
            // `SyntheticSampleGenerator.DefaultMaxSampleRows`.
            {
                Key = ConfigKeys.NotificationPrefsKeys.SchemaOnlyMaxSampleRows
                DisplayName = "Schema-only sandbox sample cap"
                Description =
                    Some
                        "Maximum synthetic-row count a partner with the Schema-only RBAC grant may request via IDataCatalog.GetSyntheticSample. Larger requests are clamped, not refused. Default 100; raise if partner integration tests need more rows."
                Kind = ConfigFieldKind.Int(Some 1, Some 100_000)
                Required = false
                DefaultJson = "100"
            }
        ]
    }
}

/// Same merge semantics as `mergePlatformSchema` but for the Phase 6f
/// `_platform.notification_prefs` schema. App-declared fields override
/// SDK defaults by `Key`; absent entry causes SDK default to be
/// prepended so the prefs tab always exists in the admin UI when
/// transactional sinks are registered.
let mergeNotificationPrefsSchema (appEntries: ModuleConfigEntry list) : ModuleConfigEntry list =
    match
        appEntries
        |> List.tryFind (fun e -> e.ModuleKey = ConfigKeys.NotificationPrefsModuleKey)
    with
    | None -> sdkNotificationPrefsSchema :: appEntries
    | Some appPrefs ->
        let appKeys = appPrefs.Schema.Fields |> List.map _.Key |> Set.ofList

        let extraSdkFields =
            sdkNotificationPrefsSchema.Schema.Fields
            |> List.filter (fun f -> not (Set.contains f.Key appKeys))

        let merged = {
            appPrefs with
                Schema = {
                    Fields = appPrefs.Schema.Fields @ extraSdkFields
                }
        }

        appEntries
        |> List.map (fun e ->
            if e.ModuleKey = ConfigKeys.NotificationPrefsModuleKey then
                merged
            else
                e)

/// SDK-shipped default `_platform.usage` schema (Phase 9d). Declares
/// the per-team compute-quota fields read by
/// `BlobBackedTeamQuotaPolicy`. All defaults are `0` (= unrestricted)
/// so a deployment that enables `UsageMetering = EnabledUsageMetering`
/// but doesn't customise quotas pays nothing — the policy short-
/// circuits on `0` for every check.
///
/// Schema lives in a dedicated module key (`_platform.usage`) rather
/// than extending the general `_platform` schema. Quota fields have a
/// distinct lifecycle (admin-only, billing-coupled) and crowding them
/// into the general settings schema would couple unrelated concerns.
let internal sdkUsageSchema: ModuleConfigEntry = {
    ModuleKey = UsageConfigKey.value
    DisplayName = "Usage Quotas"
    Schema = {
        Fields = [
            {
                Key = UsageConfigKey.maxConcurrentJobs
                DisplayName = "Maximum concurrent jobs"
                Description =
                    Some
                        "Hard cap on the number of concurrent background jobs this team can dispatch via TriggerOnce. Cron- and event-triggered jobs are not counted (operator-configured rates). Set 0 to leave unrestricted."
                Kind = ConfigFieldKind.Int(Some 0, None)
                Required = false
                DefaultJson = "0"
            }
            {
                Key = UsageConfigKey.maxAITokensPerDay
                DisplayName = "Maximum AI input tokens per day"
                Description =
                    Some
                        "Daily ceiling on AI prompt tokens for this team across every provider. The pre-call estimator is advisory; the post-call metered value is authoritative for billing. Set 0 to leave unrestricted."
                Kind = ConfigFieldKind.Float(Some 0.0, None)
                Required = false
                DefaultJson = "0"
            }
            {
                Key = UsageConfigKey.maxAITokensPerMonth
                DisplayName = "Maximum AI input tokens per month"
                Description =
                    Some
                        "Calendar-month ceiling on AI prompt tokens for this team. Resets on the 1st of each UTC month. Set 0 to leave unrestricted."
                Kind = ConfigFieldKind.Float(Some 0.0, None)
                Required = false
                DefaultJson = "0"
            }
        ]
    }
}

/// Same merge semantics as `mergePlatformSchema` but for the Phase 9d
/// `_platform.usage` schema. App-declared fields override SDK defaults
/// by `Key`; absent entry causes SDK default to be prepended so the
/// quota tab always exists in the admin UI when usage metering is
/// enabled.
let mergeUsageSchema (appEntries: ModuleConfigEntry list) : ModuleConfigEntry list =
    match appEntries |> List.tryFind (fun e -> e.ModuleKey = UsageConfigKey.value) with
    | None -> sdkUsageSchema :: appEntries
    | Some appUsage ->
        let appKeys = appUsage.Schema.Fields |> List.map _.Key |> Set.ofList

        let extraSdkFields =
            sdkUsageSchema.Schema.Fields
            |> List.filter (fun f -> not (Set.contains f.Key appKeys))

        let merged = {
            appUsage with
                Schema = {
                    Fields = appUsage.Schema.Fields @ extraSdkFields
                }
        }

        appEntries
        |> List.map (fun e -> if e.ModuleKey = UsageConfigKey.value then merged else e)

/// Resolve `Visualisation.PlatformDefaults` for the caller's scope.
/// Server-side render paths (audit reports, server-rendered narratives,
/// AI tool outputs) call this to read the same currency symbol the
/// client uses, parsed from the persisted `_platform` config.
/// Async at every boundary (Phase 9c rule 2); stateless between
/// invocations (Phase 9c rule 4).
module PlatformDefaultsResolver =
    let resolve (configStore: IConfigStore) (scope: StorageScope) : Async<Visualisation.PlatformDefaults> = async {
        let! values = configStore.GetRaw(scope, ConfigKeys.PlatformModuleKey)
        return Visualisation.PlatformDefaults.fromConfig values
    }