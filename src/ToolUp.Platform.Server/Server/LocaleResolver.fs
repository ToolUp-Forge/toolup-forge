// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.LocaleResolver

open ToolUp.Platform

// ─── Default ILocaleResolver implementation ───────────────────────────
//
// Phase 12a. Resolves locale via the documented order:
//   1. team config (`_platform.locale`)
//   2. user profile (`_platform.userLocale.{userId}`)
//   3. browser `Accept-Language` first acceptable tag
//   4. constructor-supplied fallback (typically `I18nDefaults.defaultFallback`)
//
// The resolver is stateless between calls (rule 4) — the
// `platformConfig` map is the substrate-provided per-request snapshot
// of `IConfigStore`-resolved platform config.

/// Config keys this resolver consults. Centralised so the admin UI
/// (when authored) can offer the same keys for team / user override.
module LocaleConfigKeys =
    [<Literal>]
    let TeamLocale = "_platform.locale"

    /// Per-user locale lives under a synthesised key — the resolver
    /// expects the caller's per-request platform config snapshot to
    /// already have applied user-level overrides on top of team
    /// values. The key shape is documented here so admin tools that
    /// emit the snapshot use the same name.
    [<Literal>]
    let UserLocalePrefix = "_platform.userLocale."

/// Parse one entry of an `Accept-Language` header. Examples:
///   "en-GB,en;q=0.9,fr;q=0.5"
/// returns the candidates in declared order, ignoring `q` weights
/// for simplicity (RFC 7231 §5.3.1 — the first acceptable match is
/// usually what consumers want and full q-weight ordering is
/// out-of-scope for the v1 substrate). Whitespace-trimmed and
/// lowercased to satisfy the case-insensitive lookup in
/// `Translations.tryLookup`.
let parseAcceptLanguage (header: string) : LocaleCode list =
    if System.String.IsNullOrWhiteSpace header then
        []
    else
        header.Split(',')
        |> Array.map (fun entry ->
            let semi = entry.IndexOf ';'

            let tag =
                if semi < 0 then
                    entry.Trim()
                else
                    entry.Substring(0, semi).Trim()

            tag)
        |> Array.filter (fun s -> s.Length > 0)
        |> Array.map LocaleCode.create
        |> Array.toList

/// Stateless `ILocaleResolver` implementation following the
/// documented resolution-order recipe. The fallback locale is
/// supplied at construction; the SDK default is
/// `I18nDefaults.defaultFallback`.
type DefaultLocaleResolver(fallback: LocaleCode) =
    new() = DefaultLocaleResolver(I18nDefaults.defaultFallback)

    interface ILocaleResolver with
        member _.Resolve(platformConfig, accessContext, acceptLanguage) =
            // 1. team config
            let teamLocale =
                platformConfig
                |> Map.tryFind LocaleConfigKeys.TeamLocale
                |> Option.bind (fun raw ->
                    if System.String.IsNullOrWhiteSpace raw then
                        None
                    else
                        Some(LocaleCode.create raw))

            // 2. per-user override — keyed by user id under
            //    `_platform.userLocale.{userId}`.
            let userLocale =
                let key = LocaleConfigKeys.UserLocalePrefix + accessContext.UserId

                platformConfig
                |> Map.tryFind key
                |> Option.bind (fun raw ->
                    if System.String.IsNullOrWhiteSpace raw then
                        None
                    else
                        Some(LocaleCode.create raw))

            // 3. browser `Accept-Language` (first declared tag wins;
            //    q-weight ordering deferred — see `parseAcceptLanguage`)
            let acceptLocale =
                acceptLanguage
                |> Option.bind (fun h ->
                    match parseAcceptLanguage h with
                    | first :: _ -> Some first
                    | [] -> None)

            // Per spec: user > team > Accept-Language > fallback.
            // (Inverting the doc's read-order: a user who set their
            // own locale wants it honoured over an inherited team
            // setting.)
            userLocale
            |> Option.orElse teamLocale
            |> Option.orElse acceptLocale
            |> Option.defaultValue fallback

/// Convenience constructor matching the implicit-default pattern
/// other SDK substrate uses (`NoOpActivitySink()`, `NoOpModuleQueryBus()`).
let create () : ILocaleResolver =
    DefaultLocaleResolver(I18nDefaults.defaultFallback) :> _