// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.CitationNormaliser

open System
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 6q — Citation normaliser ──────────────────────────────────
//
// Post-stream pass that reconciles the model's emitted citations
// against the per-turn `RetrievedSource` list. Drift variants the
// model often emits in place of the prompted `[¹]` / `[²]` / …
// markers — `(1)`, `[1]`, `²`, `Source 1`, `^1` — are normalised
// to the canonical superscript shape when the digit binds to a
// real retrieved source. Variants whose digit exceeds the retrieved
// count are phantoms; per the active `RagCitationPolicy` the pass
// either leaves them, strips them onto `[unverified]`, or rewrites
// the surrounding sentence.
//
// Operates on text only — tool-call segments, JSON payloads, and
// the assistant's structured fields are untouched. The normaliser
// is pure: input string + sources + policy → output string +
// counters; no side effects, safe to call from any pipeline
// position. Telemetry emission is the caller's responsibility.

/// Deployment-level citation handling policy. Applied per turn
/// against the retrieved-source list.
type RagCitationPolicy =
    /// Default. Normalise valid digits onto `[¹]`; strip / mark
    /// `[unverified]` for digits beyond the retrieved count.
    | Strict
    /// Normalise valid digits but preserve phantoms as plain text.
    /// Useful for deployments where the model surface prose is
    /// important and the user is trusted to interpret unsupported
    /// claims.
    | LenientNormalise
    /// No post-processing — current pre-Phase-6q behaviour.
    | Off

/// Action taken on a single recognised drift variant. Carries
/// enough detail for audit emission per-event: which substring the
/// model emitted, which 1-based digit it referenced, and what the
/// normaliser did with it.
type CitationAction =
    /// Variant normalised onto the canonical `[¹]` / `[²]` / … marker
    /// because the digit binds to a real retrieved-source index.
    | NormalisedToCanonical of sourceIndex: int
    /// Variant stripped (replaced with empty string) under
    /// `LenientNormalise` because the digit didn't bind.
    | StrippedPhantom
    /// Variant replaced with the `[unverified]` tag under `Strict`
    /// because the digit didn't bind. Counted as both a strip and a
    /// tag in aggregate counters.
    | UnverifiedTagged

/// One recognised drift variant + the action the normaliser took.
/// Emitted per-event so audit / dev-endpoint telemetry can attribute
/// behaviour to a specific token rather than a turn-level aggregate.
/// Wire-shape friendly: every field is a primitive or an integer so
/// the JSON payload renders cleanly in `IAuditLog` / dev-endpoint
/// snapshots.
type CitationEvent = {
    /// Exact substring the regex matched — e.g. "(1)", "Source 2",
    /// "²", "^3". Useful for operators correlating model behaviour
    /// with prompt-engineering changes.
    Variant: string
    /// 1-based digit parsed out of the variant.
    Digit: int
    /// What the normaliser did with this match.
    Action: CitationAction
}

/// Counters returned alongside the rewritten text. Callers wire
/// these into audit / dev-endpoint telemetry. `Events` carries the
/// per-event detail; the aggregate counters (`Normalisations` /
/// `Strips` / `UnverifiedTags`) are derived sums kept for
/// backwards-compat with pre-event callers.
type NormaliseResult = {
    Text: string
    Events: CitationEvent list
    Normalisations: int
    Strips: int
    UnverifiedTags: int
}

module NormaliseResult =
    let identity (text: string) : NormaliseResult = {
        Text = text
        Events = []
        Normalisations = 0
        Strips = 0
        UnverifiedTags = 0
    }

// ─── Canonical superscript markers ───────────────────────────────────

/// Render the canonical citation marker for the 1-based source
/// index. Matches `RAGPromptBuilder.formatMatch` — single-digit
/// indices get the unicode superscript; double-digit fall back to
/// `[10]`, `[11]`, …
let canonicalMarker (sourceIndex1based: int) : string =
    if sourceIndex1based < 1 then
        sprintf "[%d]" sourceIndex1based
    else
        let supers = [| '¹'; '²'; '³'; '⁴'; '⁵'; '⁶'; '⁷'; '⁸'; '⁹' |]

        if sourceIndex1based <= 9 then
            sprintf "[%c]" supers[sourceIndex1based - 1]
        else
            sprintf "[%d]" sourceIndex1based

/// The literal `[unverified]` inline tag the renderer treats as a
/// muted-red badge.
[<Literal>]
let UnverifiedTag = "[unverified]"

// ─── Drift-variant detection ─────────────────────────────────────────

/// Regex patterns recognising the common drift variants. Each
/// pattern returns capture group 1 = digit. Order matters at the
/// callsite — more specific patterns (`Source 1`) run before the
/// barer ones (`(1)`).
///
/// The patterns deliberately avoid greedy matching against text the
/// user provided in their prompt; the surrounding regex anchors
/// (`\b`, leading whitespace) keep the pass conservative.
let private parenDigit = Regex(@"\((\d+)\)", RegexOptions.Compiled)

let private bracketDigit = Regex(@"\[(\d+)\]", RegexOptions.Compiled)

let private bareSuperscript =
    Regex(@"(?<![\[\(])([¹²³⁴⁵⁶⁷⁸⁹])(?!\])", RegexOptions.Compiled)

let private sourceLabel =
    Regex(@"\bSource\s+(\d+)\b", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

let private caretDigit = Regex(@"\^(\d+)\b", RegexOptions.Compiled)

/// Unicode-superscript → ASCII digit. Used by the
/// `bareSuperscript` branch to find which source the marker
/// refers to.
let private superscriptToDigit (ch: char) : int option =
    match ch with
    | '¹' -> Some 1
    | '²' -> Some 2
    | '³' -> Some 3
    | '⁴' -> Some 4
    | '⁵' -> Some 5
    | '⁶' -> Some 6
    | '⁷' -> Some 7
    | '⁸' -> Some 8
    | '⁹' -> Some 9
    | _ -> None

// ─── Normaliser pass ─────────────────────────────────────────────────

/// Outcome of evaluating one regex match against the source list +
/// policy. `Replacement` is the literal string that replaces the
/// matched substring in the output; `Event` is `Some` when the
/// disposition recognises the match (drives audit emission). `None`
/// means the match parsed cleanly but the policy is `Off` so the
/// substring passes through untouched — no event, no replacement
/// change.
type private MatchOutcome = {
    Replacement: string
    Event: CitationEvent option
}

let private replaceOne (rx: Regex) (text: string) (replacer: Match -> MatchOutcome) =
    let events = ResizeArray<CitationEvent>()

    let rewritten =
        rx.Replace(
            text,
            MatchEvaluator(fun m ->
                let outcome = replacer m

                match outcome.Event with
                | Some evt -> events.Add evt
                | None -> ()

                outcome.Replacement)
        )

    rewritten, events |> List.ofSeq

/// Decide how to replace a recognised variant. `digit` is the
/// 1-based source index the variant refers to. Returns the
/// replacement string + the `CitationAction` we attribute (`None`
/// when policy is `Off` and the variant passes through untouched).
let private dispositionFor
    (sourceCount: int)
    (policy: RagCitationPolicy)
    (digit: int)
    : string * CitationAction option =
    if digit >= 1 && digit <= sourceCount then
        canonicalMarker digit, Some(NormalisedToCanonical digit)
    else
        match policy with
        | Strict -> UnverifiedTag, Some UnverifiedTagged
        | LenientNormalise -> "", Some StrippedPhantom
        | Off -> "PASS_THROUGH", None

let private buildOutcome (raw: string) (digit: int) (sourceCount: int) (policy: RagCitationPolicy) : MatchOutcome =
    let replacement, action = dispositionFor sourceCount policy digit

    match action with
    | None ->
        // Off policy — leave the variant untouched, no event.
        { Replacement = raw; Event = None }
    | Some act -> {
        Replacement = replacement
        Event =
            Some {
                Variant = raw
                Digit = digit
                Action = act
            }
      }

let private replaceWithDigit (rx: Regex) (text: string) (sourceCount: int) (policy: RagCitationPolicy) =
    replaceOne rx text (fun m ->
        match Int32.TryParse(m.Groups[1].Value) with
        | true, digit -> buildOutcome m.Value digit sourceCount policy
        | false, _ -> { Replacement = m.Value; Event = None })

let private replaceSuperscript (text: string) (sourceCount: int) (policy: RagCitationPolicy) =
    replaceOne bareSuperscript text (fun m ->
        match superscriptToDigit m.Value[0] with
        | Some digit -> buildOutcome m.Value digit sourceCount policy
        | None -> { Replacement = m.Value; Event = None })

/// Normalise citations in `text` against the retrieved source list
/// per `policy`. Returns the rewritten text plus the per-event
/// detail and aggregate counters derived from it. `Off` policy
/// short-circuits to identity.
let normalise (sources: RetrievedSource list) (policy: RagCitationPolicy) (text: string) : NormaliseResult =
    if policy = Off || String.IsNullOrEmpty text then
        NormaliseResult.identity text
    else
        let sourceCount = sources.Length
        let mutable currentText = text
        let events = ResizeArray<CitationEvent>()

        let applyPass (passFn: string -> string * CitationEvent list) =
            let next, passEvents = passFn currentText
            currentText <- next
            events.AddRange passEvents

        // 1. `Source 1` / `source 1` — most specific (literal word
        //    + digit). Run first so the more generic patterns don't
        //    chew on the digit alone.
        applyPass (fun t -> replaceWithDigit sourceLabel t sourceCount policy)

        // 2. `[1]` — bracketed digit. Run before `(1)` so the
        //    bracketed shape doesn't get double-processed.
        applyPass (fun t -> replaceWithDigit bracketDigit t sourceCount policy)

        // 3. `(1)` — parenthesised digit. The most common LLM drift.
        applyPass (fun t -> replaceWithDigit parenDigit t sourceCount policy)

        // 4. `^1` — markdown-superscript shorthand.
        applyPass (fun t -> replaceWithDigit caretDigit t sourceCount policy)

        // 5. Bare unicode superscript `¹` not already wrapped in
        //    `[...]`. Runs LAST so prior passes can wrap their
        //    output in `[¹]` first and this scan doesn't double-up.
        applyPass (fun t -> replaceSuperscript t sourceCount policy)

        let allEvents = events |> List.ofSeq

        let normalisations =
            allEvents
            |> List.sumBy (fun e ->
                match e.Action with
                | NormalisedToCanonical _ -> 1
                | _ -> 0)

        let unverified =
            allEvents
            |> List.sumBy (fun e ->
                match e.Action with
                | UnverifiedTagged -> 1
                | _ -> 0)

        let strips =
            allEvents
            |> List.sumBy (fun e ->
                match e.Action with
                | StrippedPhantom -> 1
                | UnverifiedTagged -> 1 // existing test contract: unverified counts as a strip too
                | _ -> 0)

        {
            Text = currentText
            Events = allEvents
            Normalisations = normalisations
            Strips = strips
            UnverifiedTags = unverified
        }