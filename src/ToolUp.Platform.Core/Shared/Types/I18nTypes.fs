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

// ─── Localised error envelope (`ApiError`) ───────────────────────────

/// Coarse error classification surfaced to API callers. Each case
/// maps to an HTTP status family server-side and to a
/// `RemotingErrorMapper` branch client-side. Module-defined codes
/// (Forms validation rules, KB ingestion stages, etc.) wrap in
/// `Module of moduleId * code` so the discriminant stays small while
/// callers retain enough specificity to render.
type ErrorCode =
    | NotAuthenticated
    | NotAuthorized
    | NotFound
    | Conflict
    | ValidationFailed
    | Internal
    /// Phase 56 — inbound rate-limit denial. Module client code
    /// pattern-matches on `err.Code` and reacts to `RateLimited rle`
    /// (e.g. showing the `RateLimitedBanner` Feliz component with the
    /// `RetryAfterSeconds` countdown). Mapped to HTTP 429 by
    /// `RateLimitMiddleware` server-side and to the
    /// `RateLimitedBanner` branch by the client error mapper.
    | RateLimited of RateLimitedError
    | Module of moduleId: string * code: string

module ErrorCode =
    /// Human-readable label for diagnostic output (server logs, dev
    /// `/dev/inspect`). Distinct from the user-visible translated
    /// message — `MessageKey` drives that.
    let label =
        function
        | NotAuthenticated -> "not-authenticated"
        | NotAuthorized -> "not-authorized"
        | NotFound -> "not-found"
        | Conflict -> "conflict"
        | ValidationFailed -> "validation-failed"
        | Internal -> "internal"
        | RateLimited rle -> sprintf "rate-limited:limit=%d,retryAfter=%ds" rle.Limit rle.RetryAfterSeconds
        | Module(m, c) -> $"module:{m}:{c}"

/// Localised error envelope returned by SDK-aware Fable.Remoting
/// handlers. The server populates `MessageKey` with a
/// `TranslationKey` and `Details` with placeholder substitutions
/// (`Map<placeholderName, substitutionValue>`); the client's
/// `RemotingErrorMapper` resolves the key through the active
/// locale's translations and applies `Details` placeholders to the
/// resolved template.
///
/// Existing handlers returning raw strings continue to compile —
/// `ApiError` adoption is per-handler.
type ApiError = {
    Code: ErrorCode
    MessageKey: TranslationKey
    Details: Map<string, string>
}

module ApiError =
    /// Minimal constructor — `Details = Map.empty`.
    let create (code: ErrorCode) (messageKey: TranslationKey) : ApiError = {
        Code = code
        MessageKey = messageKey
        Details = Map.empty
    }

    /// With a single placeholder.
    let withDetail (name: string) (value: string) (err: ApiError) : ApiError = {
        err with
            Details = Map.add name value err.Details
    }

    /// Apply `{placeholderName}`-style substitutions to a resolved
    /// template. Substitution is purely textual; the client and
    /// server use the same algorithm so a translated template's
    /// placeholders render identically on both sides.
    let applyPlaceholders (template: string) (details: Map<string, string>) : string =
        details
        |> Map.fold (fun (s: string) k v -> s.Replace("{" + k + "}", v)) template

    /// Resolve `err.MessageKey` through `translations` for `locale`
    /// (with `fallback`) and apply `err.Details` placeholders.
    /// Returns the key verbatim (with placeholders applied) if no
    /// translation exists — preserves the key for diagnostics.
    let renderMessage
        (translations: Translations)
        (locale: LocaleCode)
        (fallback: LocaleCode option)
        (err: ApiError)
        : string =
        let template = Translations.lookupOrKey translations locale fallback err.MessageKey

        applyPlaceholders template err.Details