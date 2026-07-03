// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 275 — hosted-tree i18n resolution seam ──────────────────────
//
// Phase 264 projects host DATA into the binding namespace, but not
// localized STRINGS + placeholder interpolation; and Phase 179 is a
// SOURCE-string coverage gate that cannot see a tree emitted at runtime.
// So a hosted view fell back to literal strings regardless of locale. This
// file ships the neutral `IHostI18nResolver` the host threads into the
// render path (beside the Phase 264 projection), against which a hosted
// tree resolves i18n key + placeholder bindings — extending Phase 179's
// localization guarantee to the dynamic hosted surface.
//
// **Renderer-neutral (GP 1).** The resolver is keyed on a `string` i18n key
// + a `Map<string,string>` placeholder map + a `LocaleCode`; no
// tree-language type appears. The HOST decides the active locale (server:
// from the request / principal via `ILocaleResolver`; client: from the
// browser via `I18n.useLocale`) and passes it in — the resolver is a pure
// function of (key, args, locale) over the Phase 179 substrate, so it
// resolves identically on both tiers.
//
// **Missing key is OBSERVABLE, never a silent blank (GP 2).** On a miss the
// resolver returns the KEY (with placeholders applied) — visible, not a
// blank — AND notifies an `onMiss` sink so the gap feeds the Phase 179
// coverage signal, exactly as the client `I18n.tr` helper logs a one-shot
// warning. A hosted tree can no longer silently swallow an unlocalized
// string.
//
// **Pseudolocalisation passthrough.** When the active locale is the
// reserved `qps-ploc` pseudo-locale, the resolved (or key-fallback) string
// is run through `PseudoLocale.transform` — so a hosted tree participates
// in the existing Phase 179 pseudo-loc audit (an un-externalised literal
// that never reaches `Resolve` renders un-accented and stands out).
//
// Zero cost when unused (GP 13); a pipeline that never constructs a
// resolver is byte-for-byte unchanged (GP 11).

/// The neutral i18n resolver a hosted tree resolves localized-string
/// bindings against. `Resolve` takes an i18n `key`, a placeholder `args`
/// map, and the host-resolved active `locale`, and returns the localized,
/// placeholder-substituted string.
type IHostI18nResolver =
    /// Resolve `key` for `locale`, applying `args` placeholder
    /// substitutions. On a hit: the translated template (pseudo-localised
    /// when the locale is pseudo) with `{name}` placeholders substituted. On
    /// a miss: the KEY itself (placeholders applied) — observable, never a
    /// silent blank.
    abstract Resolve: key: string -> args: Map<string, string> -> locale: LocaleCode -> string

[<RequireQualifiedAccess>]
module HostI18nResolver =

    /// Build a resolver over a Phase 179 `Translations` table + a `fallback`
    /// locale + an `onMiss` sink. `onMiss key locale` fires once per
    /// unresolved key (the coverage signal Phase 179 consumes); the resolver
    /// still returns the key (placeholders applied) so the miss is visible.
    let create
        (translations: Translations)
        (fallback: LocaleCode)
        (onMiss: string -> LocaleCode -> unit)
        : IHostI18nResolver =
        { new IHostI18nResolver with
            member _.Resolve key args locale =
                let template =
                    match Translations.tryLookup translations locale (Some fallback) key with
                    | Some t -> t
                    | None ->
                        onMiss key locale
                        key // observable fallback to the key — never a blank

                // Pseudolocalise before placeholder substitution — `transform`
                // preserves `{name}` spans, so substitution still matches.
                let localised =
                    if PseudoLocale.isActive locale then
                        PseudoLocale.transform template
                    else
                        template

                ApiError.applyPlaceholders localised args
        }

    /// Build a resolver that only returns the key on a miss (no coverage
    /// sink). The miss is still observable via the returned key (not a
    /// blank); use `create` to wire the Phase 179 coverage signal.
    let ofTranslations (translations: Translations) (fallback: LocaleCode) : IHostI18nResolver =
        create translations fallback (fun _ _ -> ())

    /// A miss-recording decorator: every unresolved key is appended to a
    /// running list (readable via `.Misses`) and forwarded, so a host /
    /// test can surface which hosted-tree keys are unlocalized (the Phase
    /// 179 coverage signal for the dynamic surface). Mirrors the
    /// `CountingHostRenderTelemetrySink` accumulation shape.
    type MissRecordingHostI18nResolver(translations: Translations, fallback: LocaleCode) =
        let misses = ResizeArray<string * LocaleCode>()
        let inner = create translations fallback (fun key locale -> misses.Add(key, locale))

        /// The (key, locale) pairs that failed to resolve since construction.
        member _.Misses = List.ofSeq misses

        interface IHostI18nResolver with
            member _.Resolve key args locale = inner.Resolve key args locale