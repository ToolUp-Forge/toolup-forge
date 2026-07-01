// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.I18n

open System
open Feliz
open Fable.Core.JsInterop
open ToolUp.Platform

let private log = Logger.forCategory "client.i18n"

// ─── Client-tier I18n surface ─────────────────────────────────────────
//
// Phase 12a. Mirrors the `FeatureFlags` context pattern: the shell
// mounts a `LocaleProvider` at the root supplying (a) the active
// `LocaleCode` from the prefetched `ClientModuleContext.PlatformConfig`
// (`_platform.locale`) and (b) the merged `Translations` table
// (SDK seed + every module's registered translations). Deep views
// read translations via the `tr` hook without threading the table
// or locale through props.
//
// Outside a provider the hook returns the empty table and the
// fallback locale, so isolated component tests don't crash.

// ─── Contexts ────────────────────────────────────────────────────────

let private localeContext =
    React.createContext<LocaleCode> (defaultValue = I18nDefaults.defaultFallback)

let private translationsContext =
    React.createContext<Translations> (defaultValue = I18nDefaults.sdkTranslations)

let private fallbackContext =
    React.createContext<LocaleCode> (defaultValue = I18nDefaults.defaultFallback)

/// Mount the locale + translations context. The shell calls this at
/// the root with the prefetched locale (from `_platform.locale` in
/// the platform-config snapshot) and the table merged from
/// `I18nDefaults.sdkTranslations` + every module's contribution.
let provider
    (locale: LocaleCode)
    (translations: Translations)
    (fallback: LocaleCode)
    (children: ReactElement)
    : ReactElement =
    localeContext.Provider(
        locale,
        translationsContext.Provider(translations, fallbackContext.Provider(fallback, children))
    )

// ─── Hooks ────────────────────────────────────────────────────────────

/// Hook — current locale from the active provider, or the SDK
/// fallback when no provider is mounted.
let useLocale () : LocaleCode = React.useContext localeContext

/// Hook — the merged `Translations` table.
let useTranslations () : Translations = React.useContext translationsContext

/// Hook — the deployment-wide fallback locale.
let useFallback () : LocaleCode = React.useContext fallbackContext

// ─── `tr` helper ──────────────────────────────────────────────────────

/// Per-session memo of keys we've already warned about so a repeated
/// lookup on the same missing key only logs once.
let private warnedKeys = System.Collections.Generic.HashSet<TranslationKey>()

let private warnMissing (locale: LocaleCode) (key: TranslationKey) =
    if warnedKeys.Add key then
        log.Warn
            $"missing translation for key '{key}' under locale '{LocaleCode.value locale}'. Falling back to key text."

/// Resolve `key` for the active locale. Returns the translated string
/// if registered, or the key verbatim (with a one-shot `console.warn`)
/// otherwise. Hook — call from a React component body.
///
/// Phase 179 — when the active locale is the reserved pseudo-locale
/// (`qps-ploc`), the resolved-or-key string is run through
/// `PseudoLocale.transform` (vowels accented, +30% padding, `⟦…⟧`
/// markers). The resolution itself is unchanged — the pseudo-locale
/// carries no entries, so lookup falls through the fallback locale to
/// the English string, which is then accented. An un-externalised
/// hardcoded literal never reaches `tr`, so it renders un-accented and
/// stands out. Byte-for-byte the pre-179 path for any real locale.
let tr (key: TranslationKey) : string =
    let locale = useLocale ()
    let translations = useTranslations ()
    let fallback = useFallback ()

    let resolved =
        match Translations.tryLookup translations locale (Some fallback) key with
        | Some v -> v
        | None ->
            warnMissing locale key
            key

    if PseudoLocale.isActive locale then
        PseudoLocale.transform resolved
    else
        resolved

/// Variant of `tr` that applies placeholder substitutions (same
/// `{name}` syntax as the server-side `ApiError.applyPlaceholders`).
let trWith (key: TranslationKey) (details: Map<string, string>) : string =
    let template = tr key
    ApiError.applyPlaceholders template details

// ─── Number / date / currency formatting ──────────────────────────────
//
// Uses the browser's `Intl` API. Falls back gracefully when running
// outside a browser (Fable tests) by formatting with .NET defaults.

/// Format a number per the active locale.
let formatNumber (value: float) : string =
    let locale = useLocale ()

    let formatted =
        emitJsExpr (LocaleCode.value locale, value) "new Intl.NumberFormat($0).format($1)"

    string formatted

/// Format a currency amount per the active locale.
let formatCurrency (currencyCode: string) (value: float) : string =
    let locale = useLocale ()

    let formatted =
        emitJsExpr
            (LocaleCode.value locale, currencyCode, value)
            "new Intl.NumberFormat($0, { style: 'currency', currency: $1 }).format($2)"

    string formatted

/// Format a date per the active locale.
let formatDate (value: DateTime) : string =
    let locale = useLocale ()
    let isoString = value.ToString("o")

    let formatted =
        emitJsExpr (LocaleCode.value locale, isoString) "new Intl.DateTimeFormat($0).format(new Date($1))"

    string formatted

// ─── Remoting error mapper ────────────────────────────────────────────

/// Translate an `ApiError` envelope into a user-visible string for
/// the active locale, applying placeholder substitutions. Used by
/// client code that surfaces errors via toasts / inline error UI.
/// Returns the resolved + substituted template, or — when the
/// translation is missing — the key text with placeholders applied
/// (matches server-side `renderMessage` semantics so server and
/// client agree on the visible string for diagnostic comparison).
let renderApiError (err: ApiError) : string =
    let locale = useLocale ()
    let translations = useTranslations ()
    let fallback = useFallback ()
    ApiError.renderMessage translations locale (Some fallback) err