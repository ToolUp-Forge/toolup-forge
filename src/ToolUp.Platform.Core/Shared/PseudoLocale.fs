// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PseudoLocale

// ─── Pseudo-localisation (Phase 179) ──────────────────────────────────
//
// A developer running under the reserved `qps-ploc` pseudo-locale sees
// every *externalised* string transformed — vowels accented, length
// padded ~30%, wrapped in `⟦…⟧` markers — so two classes of i18n defect
// become visible at a glance:
//
//   1. an un-externalised hardcoded literal (one that never went through
//      the `tr` helper) renders plain / un-accented, standing out
//      against the accented sea around it;
//   2. a layout that breaks (truncates, wraps, overflows) only on longer
//      translations shows the break under the +30% padding, before a
//      real German / Finnish translation ever ships.
//
// `transform` is a pure `string -> string` over BCL strings only — no
// `#if DEBUG`, no framework types — so it compiles in the Fable client
// tier as well as the server. The vowel-accent map stays inside the
// precomposed Latin-1 range (á é í ó ú, same glyph family as the `fr`
// seed) to avoid UTF-8 round-trip surprises. Placeholder tokens
// (`{name}`) pass through untouched so `ApiError.applyPlaceholders` /
// `trWith` still substitute after transformation.
//
// Design note — vowel-less strings round-trip unchanged. Padding +
// bracketing apply only when there is at least one accentable vowel:
// the transform's whole job is to make translatable *text* visible, so
// a token with nothing to accent (a symbol, a bare number, a code) is
// left exactly as-is rather than dressed up with brackets that carry no
// signal. That also gives the pseudo-locale a crisp, testable contract.

open System.Text

/// The reserved pseudo-locale code. `qps-ploc` is the conventional
/// Windows/ICU "pseudo-locale for localisability testing" tag, so it
/// won't collide with any real IETF BCP 47 language a deployment
/// registers.
let code = LocaleCode "qps-ploc"

/// True when `locale` is the pseudo-locale (case-insensitive, matching
/// `Translations.tryLookup`'s comparison semantics).
let isActive (locale: LocaleCode) : bool = LocaleCode.equals locale code

/// Accent a single vowel, preserving case. Non-vowels pass through.
let private accentVowel (c: char) : char =
    match c with
    | 'a' -> 'á'
    | 'e' -> 'é'
    | 'i' -> 'í'
    | 'o' -> 'ó'
    | 'u' -> 'ú'
    | 'A' -> 'Á'
    | 'E' -> 'É'
    | 'I' -> 'Í'
    | 'O' -> 'Ó'
    | 'U' -> 'Ú'
    | other -> other

/// Pseudo-localise `value`: accent every vowel (outside `{placeholder}`
/// tokens), pad the length by ~30%, and wrap in `⟦…⟧` markers. A string
/// with no accentable vowel — a symbol, a number, a bare code — round-
/// trips unchanged (there is nothing to make visible). Pure; safe on
/// both the server and Fable client tiers.
let transform (value: string) : string =
    if System.String.IsNullOrEmpty value then
        value
    else
        // Accent vowels, but copy `{name}` placeholder spans verbatim so
        // downstream `applyPlaceholders` substitution still matches.
        let accented = StringBuilder(value.Length)
        let mutable inPlaceholder = false
        let mutable touched = false

        for c in value do
            if c = '{' then
                inPlaceholder <- true
                accented.Append c |> ignore
            elif c = '}' then
                inPlaceholder <- false
                accented.Append c |> ignore
            elif inPlaceholder then
                accented.Append c |> ignore
            else
                let mapped = accentVowel c

                if mapped <> c then
                    touched <- true

                accented.Append mapped |> ignore

        if not touched then
            // Nothing to accent — leave the token exactly as it was.
            value
        else
            // ~30% length pad (at least one filler char) using the middle
            // dot, a visually-neutral marker that reads as deliberate
            // padding rather than content.
            let padCount = max 1 (value.Length * 3 / 10)
            let pad = System.String('·', padCount)
            "⟦" + accented.ToString() + pad + "⟧"