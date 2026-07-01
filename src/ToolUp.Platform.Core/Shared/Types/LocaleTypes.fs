// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Locale + translation primitives ──────────────────────────────────
//
// Phase 12a substrate. `LocaleCode` is a thin wrapper over IETF BCP 47
// tags ("en", "en-GB", "fr", "fr-CA", …); `TranslationKey` is the
// declared lookup key a view / module references; `Translations` is
// the per-key per-locale map a module ships alongside its registration.
//
// Split out of `I18nTypes.fs` (Phase 179) so these dependency-free
// primitives compile BEFORE `SDK.Shared.fs` — `ServerConfig` carries a
// `RegisteredLocales: LocaleCode list` + `I18nCoverageMode` field, and a
// record field type must be declared earlier in the compile order. The
// localised-error envelope (`ErrorCode` / `ApiError`) stays in
// `I18nTypes.fs` (it depends on `RateLimitedError`, defined mid-way
// through `SDK.Shared.fs`, so it must compile after it). Both live in
// the same `ToolUp.Platform` namespace, so no `open` crosses the split.
//
// Kept in `ToolUp.Platform.Core` so both server-tier compose code
// (resolver registration, error envelope serialisation) and client-
// tier views (`tr` helper) reference the same types without one
// pulling the other's transitive dependency graph in.

/// IETF BCP 47 locale code ("en", "en-GB", "fr", …). Comparison is
/// case-insensitive at the lookup layer; the wrapped value stays
/// verbatim so logs and config dumps show what the caller wrote.
type LocaleCode = LocaleCode of string

module LocaleCode =
    let value (LocaleCode s) = s
    let create (s: string) = LocaleCode(s.Trim())
    let en = LocaleCode "en"
    let enGB = LocaleCode "en-GB"
    let fr = LocaleCode "fr"
    let frCA = LocaleCode "fr-CA"
    let de = LocaleCode "de"
    let es = LocaleCode "es"

    /// True if two locales match case-insensitively.
    let equals (LocaleCode a) (LocaleCode b) =
        System.String.Equals(a, b, System.StringComparison.OrdinalIgnoreCase)

    /// The language-only portion of a locale ("en-GB" → "en"). Used
    /// for fallback lookup: when a translation is registered for "en"
    /// but a request resolves to "en-US", the language match wins
    /// over the missing-key fallback.
    let language (LocaleCode s) =
        let dash = s.IndexOfAny([| '-'; '_' |])

        if dash < 0 then
            LocaleCode s
        else
            LocaleCode(s.Substring(0, dash))

/// Declared lookup key for a translatable string. Conventionally
/// dot-separated and namespaced ("sdk.shell.signOut",
/// "sales-analysis.dataset.uploadPrompt"). Kept as a string alias so
/// modules can declare keys without importing the namespace.
type TranslationKey = string

/// Per-key per-locale translation table. Outer key is the
/// `TranslationKey`; inner key is the `LocaleCode`. A module's
/// `register()` may attach its own `Translations` map; the SDK
/// merges every registered module's table with its own seed
/// translations at compose time.
type Translations = Map<TranslationKey, Map<LocaleCode, string>>

module Translations =
    /// The empty translations table.
    let empty: Translations = Map.empty

    /// Look up a translation. Resolution order:
    ///   1. exact (key, locale) match
    ///   2. language-only fallback (key, language locale) — so
    ///      "en-US" finds an "en" registration
    ///   3. fallback locale match if supplied
    ///   4. `None` — caller decides whether to display the key
    ///      verbatim or log a missing-translation warning.
    let tryLookup
        (translations: Translations)
        (locale: LocaleCode)
        (fallback: LocaleCode option)
        (key: TranslationKey)
        : string option =
        match Map.tryFind key translations with
        | None -> None
        | Some perLocale ->
            // Linear scan over the per-locale map — `LocaleCode` is a
            // single-case DU so the structural map respects identity,
            // not case-insensitive equality. Callers typically register
            // exactly the locale strings consumers will request, so
            // this is a one-element scan in the common case.
            let exact =
                perLocale
                |> Map.toSeq
                |> Seq.tryFind (fun (loc, _) -> LocaleCode.equals loc locale)
                |> Option.map snd

            match exact with
            | Some v -> Some v
            | None ->
                let langOnly = LocaleCode.language locale

                let langMatch =
                    perLocale
                    |> Map.toSeq
                    |> Seq.tryFind (fun (loc, _) -> LocaleCode.equals loc langOnly)
                    |> Option.map snd

                match langMatch, fallback with
                | Some v, _ -> Some v
                | None, Some fb ->
                    perLocale
                    |> Map.toSeq
                    |> Seq.tryFind (fun (loc, _) -> LocaleCode.equals loc fb)
                    |> Option.map snd
                | None, None -> None

    /// Look up a translation with a guaranteed string result —
    /// returns the key verbatim if no translation is registered. The
    /// `tr` helper builds on this; callers that want to *detect*
    /// missing translations (for logging) use `tryLookup`.
    let lookupOrKey
        (translations: Translations)
        (locale: LocaleCode)
        (fallback: LocaleCode option)
        (key: TranslationKey)
        : string =
        tryLookup translations locale fallback key |> Option.defaultValue key

    /// Merge a module's translations into a base table. On key
    /// collision the right-hand argument wins per-locale — module
    /// translations override SDK seed translations, latest-registered
    /// module wins over earlier. Documented at the compose site.
    let merge (left: Translations) (right: Translations) : Translations =
        right
        |> Map.fold
            (fun acc key perLocale ->
                match Map.tryFind key acc with
                | None -> Map.add key perLocale acc
                | Some existing ->
                    let merged = perLocale |> Map.fold (fun m l v -> Map.add l v m) existing
                    Map.add key merged acc)
            left

// ─── i18n coverage-gate policy (Phase 179) ────────────────────────────

/// Policy governing the compose-time translation-coverage gate
/// (`I18nCoverage.validator`). Declared here rather than in
/// `I18nCoverage.fs` because `ServerConfig` (in `SDK.Shared.fs`) carries
/// an `I18nCoverageMode` field and the field's type must precede the
/// record in the compile order.
///
/// Default `NoCoverageCheck` (GP 11) — a deployment that doesn't opt in
/// pays nothing and behaves exactly as before. `WarnOnMissing` logs a
/// `Warn` per gap and continues; `FailOnMissing` joins the
/// `IConfigValidator` preflight and aborts startup, naming the missing
/// key + locale.
type I18nCoverageMode =
    | NoCoverageCheck
    | WarnOnMissing
    | FailOnMissing