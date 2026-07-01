// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.I18nCoverage

// ─── Translation-coverage gate (Phase 179) ────────────────────────────
//
// Phase 12a asserted "SDK built-in strings translate correctly", but
// nothing *proved* that every shipped key resolves in every locale a
// deployment declares — a missing entry surfaced only as a silent
// `lookupOrKey` fallback-to-key-string at runtime. `audit` closes that
// gap at compose / contract time: given the merged `Translations` table
// and the deployment's registered locales, it reports every
// `TranslationKey` missing a translation in some registered locale.
//
// The check honours `Translations.tryLookup`'s language-only fallback
// (an `"en"` entry covers an `"en-GB"` requirement) by resolving each
// (key, locale) with a `None` explicit-fallback: a locale is only
// "missing" when neither an exact nor a language-only match exists. The
// explicit fallback-locale arm is deliberately NOT used — a fallback
// masking a genuine gap is exactly what this gate exists to catch.
//
// Pure / BCL-only / Fable-safe; the validator is server-consumed but
// carries no framework handle, so the whole module ships in both tiers.

open ToolUp.Platform.ConfigValidation

/// A single per-key coverage gap: the locales (from the required set)
/// that have no exact-or-language-fallback entry for `Key`.
type CoverageGap = {
    Key: TranslationKey
    MissingLocales: LocaleCode list
}

/// Result of a coverage audit. `Gaps` is empty when every audited key
/// resolves in every required locale.
type CoverageReport = { Gaps: CoverageGap list }

module CoverageReport =
    /// True when the audit found no gaps.
    let isComplete (report: CoverageReport) : bool = List.isEmpty report.Gaps

    /// One line per gap: `key 'X' missing in locale(s): fr, de`. Used in
    /// the validator's `Warning` / `Error` message so the abort summary
    /// names the offending key + locale (acceptance criterion).
    let describe (report: CoverageReport) : string =
        report.Gaps
        |> List.map (fun g ->
            let locales = g.MissingLocales |> List.map LocaleCode.value |> String.concat ", "
            $"key '{g.Key}' missing in locale(s): {locales}")
        |> String.concat System.Environment.NewLine

/// Audit an explicit key set against the required locales. A locale
/// counts as covered when `Translations.tryLookup` (with no explicit
/// fallback, so only exact + language-only matches apply) resolves the
/// key. Keys entirely absent from `translations` report every required
/// locale as missing.
let auditKeys
    (translations: Translations)
    (keys: TranslationKey list)
    (requiredLocales: LocaleCode list)
    : CoverageReport =
    let gaps =
        keys
        |> List.distinct
        |> List.choose (fun key ->
            let missing =
                requiredLocales
                |> List.filter (fun loc -> (Translations.tryLookup translations loc None key) |> Option.isNone)

            match missing with
            | [] -> None
            | _ -> Some { Key = key; MissingLocales = missing })

    { Gaps = gaps }

/// Audit every key present in the merged `translations` table against
/// the required locales — the "does every registered string resolve in
/// every declared locale?" check.
let audit (translations: Translations) (requiredLocales: LocaleCode list) : CoverageReport =
    auditKeys translations (translations |> Map.toList |> List.map fst) requiredLocales

/// The stock `ErrorCode` cases whose localised `ApiError` message keys
/// must always resolve — the "stock shell verbs". `Module` is excluded
/// (module-defined codes carry their own key on the envelope).
let private stockErrorCodes: ErrorCode list = [
    NotAuthenticated
    NotAuthorized
    NotFound
    Conflict
    ValidationFailed
    Internal
    RateLimited {
        RetryAfterSeconds = 0
        Limit = 0
        Window = PerMinute
    }
]

/// The SDK's own required key surface: every `sdk.*` key in
/// `I18nDefaults.sdkTranslations` plus the stock `ApiError` message key
/// for each `ErrorCode`. These must resolve in every registered locale
/// whenever the coverage check is on.
let sdkRequiredKeys: TranslationKey list =
    let seedKeys = I18nDefaults.sdkTranslations |> Map.toList |> List.map fst
    let errorKeys = stockErrorCodes |> List.map I18nDefaults.messageKeyFor
    seedKeys @ errorKeys |> List.distinct

/// Build the compose-time coverage validator for the deployment's
/// registered locales + `I18nCoverageMode`. Returns `None` for
/// `NoCoverageCheck` (nothing joins the preflight — zero cost, GP 13).
/// For `WarnOnMissing` / `FailOnMissing`, the returned `IConfigValidator`
/// audits the SDK's own required keys AND every key in the merged
/// `translations` table; a gap yields `Warning` (log + continue) or
/// `Error` (abort startup via the aggregator), naming the key + locale.
let validator
    (translations: Translations)
    (requiredLocales: LocaleCode list)
    (mode: I18nCoverageMode)
    : IConfigValidator option =
    match mode with
    | NoCoverageCheck -> None
    | WarnOnMissing
    | FailOnMissing ->
        let keys =
            sdkRequiredKeys @ (translations |> Map.toList |> List.map fst) |> List.distinct

        { new IConfigValidator with
            member _.Name = "i18n-coverage"
            member _.Timeout = IConfigValidator.defaultTimeout

            member _.Validate() = async {
                let report = auditKeys translations keys requiredLocales

                if CoverageReport.isComplete report then
                    return ValidationResult.Ok
                else
                    let summary = CoverageReport.describe report

                    match mode with
                    | FailOnMissing -> return ValidationResult.Error summary
                    | _ -> return ValidationResult.Warning summary
            }
        }
        |> Some