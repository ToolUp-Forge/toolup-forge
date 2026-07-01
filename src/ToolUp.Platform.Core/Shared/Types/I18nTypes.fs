// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Localised error envelope (`ApiError`) ───────────────────────────
//
// Phase 12a substrate. The locale + translation primitives
// (`LocaleCode` / `TranslationKey` / `Translations`) were split out
// into `Types/LocaleTypes.fs` (Phase 179) so they compile before
// `SDK.Shared.fs` and `ServerConfig` can carry i18n fields. This file
// keeps the `ErrorCode` / `ApiError` envelope, which depends on
// `RateLimitedError` (defined mid-way through `SDK.Shared.fs`) and so
// must compile after it. Same `ToolUp.Platform` namespace, so the
// split needs no `open`.

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