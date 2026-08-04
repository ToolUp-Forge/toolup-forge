// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The English Snowball ("Porter2") stemming algorithm, implemented directly
/// against the published algorithm description with no third-party dependency.
///
/// **Why hand-rolled rather than a NuGet stemmer.** GP 1 puts vendor
/// dependencies in companions, which this is — but the dependency budget still
/// buys something. The published English Snowball algorithm is a closed,
/// stable, fully-specified string transformation with no I/O, no configuration
/// and no upstream that can move under us; vendoring a package to obtain it
/// adds a supply-chain edge to an Apache-2.0 SDK for roughly 300 lines of pure
/// suffix arithmetic that is pinned here by explicit vectors. The trade would
/// be different for anything with a data file, a native binary, or a protocol.
///
/// The algorithm is deterministic and allocation-light: no regex, one pass per
/// step, `string` slicing only. It runs once per token on the ingestion path
/// and once per query token, so it is hot enough to care.
module ToolUp.SparseIndices.Snowball.Porter2

open System

// ─── Vowel / region machinery ─────────────────────────────────────

let private isVowel (c: char) =
    match c with
    | 'a'
    | 'e'
    | 'i'
    | 'o'
    | 'u'
    | 'y' -> true
    | _ -> false

/// After the initial y-marking pass, an uppercase 'Y' denotes a y that is
/// acting as a CONSONANT, and is excluded from the vowel test above.
let private isVowelAt (s: string) (i: int) = i >= 0 && i < s.Length && isVowel s[i]

/// R1 = the region after the first non-vowel following a vowel, or the end of
/// the word if there is none. Three prefixes take a special R1 (the algorithm's
/// documented exception) because the generic rule cuts them too short and
/// over-stems `generate` / `communism` / `arsenal`.
let private computeR1 (s: string) =
    let special = [ "gener"; "commun"; "arsen" ]

    match special |> List.tryFind s.StartsWith with
    | Some p -> p.Length
    | None ->
        let mutable i = 1
        let mutable r = s.Length

        while i < s.Length && r = s.Length do
            if not (isVowelAt s i) && isVowelAt s (i - 1) then
                r <- i + 1

            i <- i + 1

        r

/// R2 = R1's rule applied again, starting inside R1.
let private computeR2 (s: string) (r1: int) =
    let mutable i = r1 + 1
    let mutable r = s.Length

    while i < s.Length && r = s.Length do
        if not (isVowelAt s i) && isVowelAt s (i - 1) then
            r <- i + 1

        i <- i + 1

    r

/// A "short syllable": a vowel followed by a non-vowel other than w / x / Y and
/// preceded by a non-vowel, or (word-initially) a vowel followed by a
/// non-vowel. A word is *short* when it ends in a short syllable and R1 is the
/// whole word — `hop`, `bed`, `hoping`→`hope`.
let private endsShortSyllable (s: string) =
    let n = s.Length

    if n >= 3 then
        let a, b, c = s[n - 3], s[n - 2], s[n - 1]

        not (isVowel a)
        && isVowel b
        && not (isVowel c)
        && c <> 'w'
        && c <> 'x'
        && c <> 'Y'
    elif n = 2 then
        isVowel s[0] && not (isVowel s[1])
    else
        false

let private isShortWord (s: string) (r1: int) = r1 >= s.Length && endsShortSyllable s

let private endsWith (s: string) (suffix: string) =
    s.EndsWith(suffix, StringComparison.Ordinal)

let private trimEnd (s: string) (n: int) = s.Substring(0, s.Length - n)

let private containsVowel (s: string) =
    let mutable found = false

    for i in 0 .. s.Length - 1 do
        if isVowelAt s i then
            found <- true

    found

let private endsDouble (s: string) =
    s.Length >= 2
    && s[s.Length - 1] = s[s.Length - 2]
    && (match s[s.Length - 1] with
        | 'b'
        | 'd'
        | 'f'
        | 'g'
        | 'm'
        | 'n'
        | 'p'
        | 'r'
        | 't' -> true
        | _ -> false)

/// Suffix `li` may only be removed after one of these letters.
let private isValidLiEnding (c: char) =
    match c with
    | 'c'
    | 'd'
    | 'e'
    | 'g'
    | 'h'
    | 'k'
    | 'm'
    | 'n'
    | 'r'
    | 't' -> true
    | _ -> false

// ─── Exception tables ─────────────────────────────────────────────

let private exceptional =
    dict [
        "skis", "ski"
        "skies", "sky"
        "dying", "die"
        "lying", "lie"
        "tying", "tie"
        "idly", "idl"
        "gently", "gentl"
        "ugly", "ugli"
        "early", "earli"
        "only", "onli"
        "singly", "singl"
        "sky", "sky"
        "news", "news"
        "howe", "howe"
        "atlas", "atlas"
        "cosmos", "cosmos"
        "bias", "bias"
        "andes", "andes"
    ]

/// Words that must not proceed past step 1a — every one of them would be
/// mangled by the later steps (`inning` → `in`, `proceed` → `proce`).
let private step1aExceptions =
    set [
        "inning"
        "outing"
        "canning"
        "herring"
        "earring"
        "proceed"
        "exceed"
        "succeed"
    ]

// ─── Steps ────────────────────────────────────────────────────────

let private step0 (s: string) =
    [ "'s'"; "'s"; "'" ]
    |> List.tryPick (fun suffix ->
        if endsWith s suffix then
            Some(trimEnd s suffix.Length)
        else
            None)
    |> Option.defaultValue s

let private step1a (s: string) =
    if endsWith s "sses" then
        trimEnd s 2
    elif endsWith s "ied" || endsWith s "ies" then
        // `ties` → `tie`, but `cries` → `cri`: more than one letter before the
        // suffix means the `i` is the stem's own.
        if s.Length > 4 then trimEnd s 2 else trimEnd s 1
    elif endsWith s "us" || endsWith s "ss" then
        s
    elif endsWith s "s" then
        // Delete only when a vowel appears before the last two characters —
        // `gas` and `this` keep their s.
        let preceding = trimEnd s 2

        if containsVowel preceding then trimEnd s 1 else s
    else
        s

let private step1b (s: string) (r1: int) =
    let inR1 (suffix: string) = s.Length - suffix.Length >= r1

    if endsWith s "eedly" then
        if inR1 "eedly" then trimEnd s 3 else s
    elif endsWith s "eed" then
        if inR1 "eed" then trimEnd s 1 else s
    else
        let stripped =
            [ "ingly"; "edly"; "ing"; "ed" ]
            |> List.tryPick (fun suffix ->
                if endsWith s suffix then
                    let stem = trimEnd s suffix.Length

                    if containsVowel stem then Some stem else None
                else
                    None)

        match stripped with
        | None -> s
        | Some stem ->
            if endsWith stem "at" || endsWith stem "bl" || endsWith stem "iz" then
                stem + "e"
            elif endsDouble stem then
                trimEnd stem 1
            elif isShortWord stem r1 then
                stem + "e"
            else
                stem

let private step1c (s: string) =
    if s.Length > 2 && (s[s.Length - 1] = 'y' || s[s.Length - 1] = 'Y') then
        if not (isVowelAt s (s.Length - 2)) then
            trimEnd s 1 + "i"
        else
            s
    else
        s

let private step2Rules = [
    "ational", "ate"
    "fulness", "ful"
    "iveness", "ive"
    "ousness", "ous"
    "ization", "ize"
    "tional", "tion"
    "biliti", "ble"
    "lessli", "less"
    "entli", "ent"
    "ation", "ate"
    "alism", "al"
    "aliti", "al"
    "ousli", "ous"
    "iviti", "ive"
    "fulli", "ful"
    "enci", "ence"
    "anci", "ance"
    "abli", "able"
    "izer", "ize"
    "ator", "ate"
    "alli", "al"
    "bli", "ble"
    "ogi", "og"
]

let private step2 (s: string) (r1: int) =
    let applicable (suffix: string) = s.Length - suffix.Length >= r1

    let matched =
        step2Rules
        |> List.tryFind (fun (suffix, _) -> endsWith s suffix && applicable suffix)

    match matched with
    | Some("ogi", replacement) ->
        // `ogi` → `og` only after an l (`apology` → `apolog`).
        if s.Length >= 4 && s[s.Length - 4] = 'l' then
            trimEnd s 3 + replacement
        else
            s
    | Some(suffix, replacement) -> trimEnd s suffix.Length + replacement
    | None ->
        if
            endsWith s "li"
            && applicable "li"
            && s.Length >= 3
            && isValidLiEnding s[s.Length - 3]
        then
            trimEnd s 2
        else
            s

let private step3Rules = [
    "ational", "ate"
    "tional", "tion"
    "alize", "al"
    "icate", "ic"
    "iciti", "ic"
    "ical", "ic"
    "ness", ""
    "ful", ""
]

let private step3 (s: string) (r1: int) (r2: int) =
    let applicable (suffix: string) = s.Length - suffix.Length >= r1

    match
        step3Rules
        |> List.tryFind (fun (suffix, _) -> endsWith s suffix && applicable suffix)
    with
    | Some(suffix, replacement) -> trimEnd s suffix.Length + replacement
    | None ->
        if endsWith s "ative" && s.Length - 5 >= r2 then
            trimEnd s 5
        else
            s

let private step4Suffixes = [
    "ement"
    "ance"
    "ence"
    "able"
    "ible"
    "ment"
    "ant"
    "ent"
    "ism"
    "ate"
    "iti"
    "ous"
    "ive"
    "ize"
    "al"
    "er"
    "ic"
]

let private step4 (s: string) (r2: int) =
    match
        step4Suffixes
        |> List.tryFind (fun suffix -> endsWith s suffix && s.Length - suffix.Length >= r2)
    with
    | Some suffix -> trimEnd s suffix.Length
    | None ->
        if endsWith s "ion" && s.Length - 3 >= r2 && s.Length >= 4 then
            match s[s.Length - 4] with
            | 's'
            | 't' -> trimEnd s 3
            | _ -> s
        else
            s

let private step5 (s: string) (r1: int) (r2: int) =
    if endsWith s "e" then
        let stem = trimEnd s 1

        if s.Length - 1 >= r2 then
            stem
        elif s.Length - 1 >= r1 && not (endsShortSyllable stem) then
            stem
        else
            s
    elif endsWith s "l" && s.Length - 1 >= r2 && s.Length >= 2 && s[s.Length - 2] = 'l' then
        trimEnd s 1
    else
        s

// ─── Entry point ──────────────────────────────────────────────────

/// Stem one already-lower-cased English word. Words of two letters or fewer,
/// and the algorithm's exception list, are returned unchanged.
///
/// **Not idempotent, and it does not need to be.** `stem "embedded" = "embed"`
/// while `stem "embed" = "emb"` — a known property of the algorithm, not a
/// defect here. It costs nothing in retrieval because the index and the query
/// each analyse RAW text exactly once: no term is ever stemmed twice. (What it
/// does cost is that a query for `embed` will not match a document that says
/// `embedded`, which is the algorithm's own accuracy ceiling.)
let stem (word: string) : string =
    if String.IsNullOrEmpty word then
        word
    else
        let lowered = word.ToLowerInvariant()

        match exceptional.TryGetValue lowered with
        | true, replacement -> replacement
        | false, _ ->

            if lowered.Length <= 2 then
                lowered
            else
                // Strip a leading apostrophe, then mark consonantal y as Y so the
                // vowel tests above ignore it. Restored at the end.
                let s =
                    if lowered.StartsWith("'", StringComparison.Ordinal) then
                        lowered.Substring 1
                    else
                        lowered

                let marked =
                    let chars = s.ToCharArray()

                    if chars.Length > 0 && chars[0] = 'y' then
                        chars[0] <- 'Y'

                    for i in 1 .. chars.Length - 1 do
                        if chars[i] = 'y' && isVowel chars[i - 1] then
                            chars[i] <- 'Y'

                    String(chars)

                let r1 = computeR1 marked
                let r2 = computeR2 marked r1

                let afterStep1a = marked |> step0 |> step1a

                if step1aExceptions.Contains(afterStep1a.Replace('Y', 'y')) then
                    afterStep1a.Replace('Y', 'y')
                else
                    let result =
                        afterStep1a
                        |> fun w -> step1b w r1
                        |> step1c
                        |> fun w -> step2 w r1
                        |> fun w -> step3 w r1 r2
                        |> fun w -> step4 w r2
                        |> fun w -> step5 w r1 r2

                    result.Replace('Y', 'y')