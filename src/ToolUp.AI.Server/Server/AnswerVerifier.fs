// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.AnswerVerifier

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI

// ─── Numeric-fidelity answer gate (Phase 523) ────────────────────────
//
// A post-response stage that makes "no unverified numbers" mechanical. It
// extracts every numeric token from an assistant answer, canonicalises it
// under the metric registry's display rules (rounding / format / percent /
// unit-scaling aware — values are compared, never surface strings), and
// checks each against the facts in the turn's retrieved set (the
// `ChunkOrigin.Fact` sources). This is the regex-grade check with the
// audit-grade consequence: mismatches flag inline via an SSE verification
// event, ride the persisted `ConversationMessage`, emit one audit record
// per unmatched token (GP 6), and feed verified/unmatched metric counters
// the eval harness consumes.
//
// The seam (`IAnswerVerifier`) is what the future LLM-judge qualitative
// tier implements — same shape, a smarter body. This default is the
// deterministic numeric tier.
//
// **Opt-in; `Off` is byte-identical (GP 11 / GP 13).** The gate runs only
// when a deployment composes `AIServerApp.withNumericFidelityGate` in a
// non-`Off` mode; absent, `runVerificationStage` short-circuits to the
// answer verbatim with no verdict, no SSE event, no audit, no metric.

// ─── Gate mode ───────────────────────────────────────────────────────

/// Escalation level of the answer gate (Phase 523.C). Server-only config
/// carried through DI from `withNumericFidelityGate`.
///
///  * `AnswerGateOff` — the default. No verification; the answer path is
///    byte-for-byte the pre-523 path (GP 13).
///  * `AnswerGateAnnotate` — verify + surface the verdict (SSE event +
///    `ConversationMessage.Verification` + audit + metrics) and append a
///    non-destructive footnote listing any unverified figures. The answer
///    body is otherwise untouched.
///  * `AnswerGateStrict` — as `Annotate`, and additionally *withhold* each
///    sentence carrying an unverified number behind an explicit inline flag
///    (never a silent rewrite — the flag names why the figure was removed).
type AnswerGateMode =
    | AnswerGateOff
    | AnswerGateAnnotate
    | AnswerGateStrict

module AnswerGateMode =
    let toString (mode: AnswerGateMode) : string =
        match mode with
        | AnswerGateOff -> "Off"
        | AnswerGateAnnotate -> "Annotate"
        | AnswerGateStrict -> "Strict"

    /// Parse a config string (`"Annotate"` / `"Strict"`, case-insensitive);
    /// anything unrecognised — including `null` / `""` — resolves to the
    /// safe `Off` default (GP 13).
    let parse (raw: string) : AnswerGateMode =
        match (raw |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
        | "annotate" -> AnswerGateAnnotate
        | "strict" -> AnswerGateStrict
        | _ -> AnswerGateOff

// ─── Metric registrations (Phase 523.E) ──────────────────────────────

/// Counter of numeric tokens that matched a fact in the retrieved set.
[<Literal>]
let VerifiedTotal = "toolup.ai.answer.verified"

/// Counter of numeric tokens that matched NO fact in the retrieved set
/// while facts WERE in scope — the anti-hallucination signal the eval
/// harness (Phase 14j) charts.
[<Literal>]
let UnmatchedTotal = "toolup.ai.answer.unmatched"

/// SDK-owned answer-gate registrations. Wired by
/// `AIServerApp.withNumericFidelityGate` into `ServerApp.MetricRegistrations`
/// so `PrometheusMetricsSink` / `OtelMetricsSink` pre-allocate the series
/// at compose time and emissions flow rather than silently drop. Same
/// pattern as `AILatencyMetrics.registrations`.
let registrations: MetricRegistration list = [
    {
        Module = None
        Definition = {
            Name = VerifiedTotal
            Kind = Counter
            Description = "Numeric answer tokens verified against a retrieved fact (tags: provider + model + mode)"
            Unit = "1"
            Tags = [ "provider"; "model"; "mode" ]
        }
    }
    {
        Module = None
        Definition = {
            Name = UnmatchedTotal
            Kind = Counter
            Description =
                "Numeric answer tokens with no matching retrieved fact while facts were in scope "
                + "(tags: provider + model + mode)"
            Unit = "1"
            Tags = [ "provider"; "model"; "mode" ]
        }
    }
]

// ─── Audit (Phase 523.D) ─────────────────────────────────────────────

/// Reserved `IEventStore` source-module for the numeric-fidelity audit
/// trail. Filter `IEventStore.ReadBySource scope AnswerVerifier.AuditSourceModule`
/// for unverified-number events in isolation (GP 6).
[<Literal>]
let AuditSourceModule = "_platform.ai.answer_verification"

[<Literal>]
let AuditEventType = "UnverifiedNumber"

/// Payload of an `UnverifiedNumber` audit event (JSON into
/// `ModuleEvent.Payload`). PII-free — the numeric token + its canonical
/// value + the turn identifiers only.
type UnverifiedNumberAudit = {
    TaskId: Guid
    ConversationId: Guid
    /// The numeric token exactly as it appeared in the answer.
    Token: string
    /// The canonical decimal value it normalised to (invariant string).
    Canonical: string
    Mode: string
    /// How many facts were in scope this turn (0 ⇒ nothing to check against,
    /// but such tokens are `NoFactsInScope`, never audited as unmatched).
    FactsInScope: int
    ProviderName: string
    ProviderModel: string
}

// ─── Canonicalisation (Phase 523.A) ──────────────────────────────────
//
// Registry display format → canonical comparison value. The rules:
//  * Unicode minus / en-dash / figure-dash are folded to ASCII `-`, so
//    `−1.3` and `-1.3` are the same number.
//  * Currency glyphs (`£ $ €`), thousands separators, and surrounding
//    whitespace are stripped, so `£21,800` and `21800` are the same.
//  * A parenthesised value `(1,234)` is a negative (accountancy form).
//  * A trailing `%` folds the value to its fraction (`-134%` → `-1.34`),
//    so a fraction quote and a percent quote of the same quantity agree.
//  * The token's *apparent precision* (decimal places, +2 for a percent)
//    is what the match rounds to — a number quoted to fewer decimals is a
//    faithful rounding, a genuinely different number is not.

module Canonical =

    /// Fold the several Unicode minus / dash code points to ASCII `-`.
    let private normaliseSign (s: string) =
        s.Replace('−', '-').Replace('–', '-').Replace('—', '-').Replace('‒', '-')

    /// Decimal-place precision implied by a .NET numeric format string, in
    /// *fraction* space — so a percent format `"P1"` (one decimal on the
    /// ×100 value) is three fraction decimals. `None` for an empty / verbatim
    /// format (`""`). Used to canonicalise a stored `FactValue`'s precision
    /// under the metric registry's declared `DisplayFormat` (Phase 519).
    let formatPrecision (fmt: string) : int option =
        if String.IsNullOrWhiteSpace fmt then
            None
        else
            let f = fmt.Trim()
            let letter = Char.ToUpperInvariant f.[0]

            let digits =
                match Int32.TryParse(f.Substring 1) with
                | true, n -> n
                | _ -> 2 // .NET default for N/C/F/P when no digit is given

            match letter with
            | 'P' -> Some(digits + 2) // percent renders the ×100 value
            | 'C'
            | 'N'
            | 'F' -> Some digits
            | _ -> None

    /// Parse a raw numeric token to `(fractionValue, apparentPrecision)`.
    /// `apparentPrecision` is the decimal places in *fraction* space
    /// (percent adds 2). `None` when the token carries no numeric core.
    /// Never throws.
    let parse (raw: string) : (decimal * int) option =
        if String.IsNullOrWhiteSpace raw then
            None
        else
            let trimmed = (normaliseSign raw).Trim()

            let paren = trimmed.StartsWith "(" && trimmed.EndsWith ")"

            let body =
                if paren then
                    trimmed.Substring(1, trimmed.Length - 2)
                else
                    trimmed

            let isPercent = body.Contains "%"

            // Keep only digits, a single leading sign, and the decimal point;
            // this discards currency glyphs, thousands separators, and the
            // percent sign in one pass.
            let core =
                body.ToCharArray()
                |> Array.filter (fun c -> Char.IsDigit c || c = '-' || c = '.')
                |> System.String

            match Decimal.TryParse(core, NumberStyles.Number, CultureInfo.InvariantCulture) with
            | true, v ->
                let signed = if paren then -(abs v) else v
                let value = if isPercent then signed / 100m else signed

                // Precision from the raw numeral, not the divided decimal —
                // so `-134%` reports 2 fraction places (0 percent decimals + 2)
                // rather than whatever `134/100m` happens to store.
                let numeralDecimals =
                    match core.IndexOf '.' with
                    | -1 -> 0
                    | i -> core.Length - i - 1

                let precision = numeralDecimals + (if isPercent then 2 else 0)
                Some(value, precision)
            | _ -> None

    /// Round `value` to `places` (clamped to a sane decimal range), rounding
    /// halves away from zero to match display rendering.
    let round (places: int) (value: decimal) : decimal =
        Math.Round(value, min 28 (max 0 places), MidpointRounding.AwayFromZero)

    /// Whether an answer token matches a fact value. The comparison rounds
    /// BOTH to the token's apparent precision — the coarser precision the
    /// answer author chose — so `-1.3`, `-1.34`, and `-134%` all verify
    /// against a stored `-1.34`, while a genuinely different number does not.
    let valuesMatch (tokenPrecision: int) (tokenValue: decimal) (factValue: decimal) : bool =
        round tokenPrecision tokenValue = round tokenPrecision factValue

// ─── Token extraction (Phase 523.B) ──────────────────────────────────

/// A numeric token located in an answer, with its position (for Strict-mode
/// sentence withholding).
type NumericToken = {
    Text: string
    Index: int
    Length: int
}

// Signed (incl. Unicode minus), optionally currency-prefixed, grouped,
// optionally-decimal, optionally-percent number. Non-overlapping,
// left-to-right — `£21,800` matches whole rather than as `21` + `800`.
let private numberRegex =
    Regex(@"[-−–]?[$£€]?\d[\d,]*(?:\.\d+)?%?", RegexOptions.Compiled)

/// Extract every numeric token from `answer`, in reading order. Trailing
/// grouping punctuation captured from surrounding prose is trimmed off the
/// reported token; a lone 1–2 digit integer wrapped in `[n]` (a citation
/// marker) is skipped so citation digits are not mistaken for quantities.
let extractTokens (answer: string) : NumericToken list =
    if String.IsNullOrEmpty answer then
        []
    else
        [
            for m in numberRegex.Matches answer do
                // Trim a trailing separator the greedy class swallowed from
                // prose (`£21,800,` → `£21,800`).
                let trimmed = m.Value.TrimEnd(',', '.')
                let length = trimmed.Length

                if length > 0 then
                    let before = if m.Index > 0 then Some answer.[m.Index - 1] else None

                    let after =
                        if m.Index + length < answer.Length then
                            Some answer.[m.Index + length]
                        else
                            None

                    let isCitationMarker =
                        before = Some '['
                        && after = Some ']'
                        && trimmed.Length <= 2
                        && trimmed |> Seq.forall Char.IsDigit

                    if not isCitationMarker then
                        {
                            Text = trimmed
                            Index = m.Index
                            Length = length
                        }
        ]

// ─── Facts in scope ──────────────────────────────────────────────────

/// A retrieved fact a turn can verify against (Phase 523.B) — projected
/// from the `ChunkOrigin.Fact` retrieved sources so the verifier takes no
/// dependency on the retrieval wire record beyond the fields it reads.
type ScopedFact = {
    FactId: string
    /// The canonical display rendering (produced under the metric registry's
    /// `DisplayFormat` upstream in fact-first retrieval).
    Rendering: string
    /// Registered metric id, when known — lets the verifier consult the
    /// registry for the fact's declared precision. Empty when the retrieval
    /// projection did not carry it (canonicalisation then parses the
    /// rendering symmetrically, which already reflects the display rules).
    Metric: string
}

/// Project the fact-origin retrieved sources onto `ScopedFact`s. Non-fact
/// sources (documents, notes, narratives) are dropped — the numeric gate
/// verifies against the exact fact tier, not similarity prose.
let scopedFacts (sources: RetrievedSource list) : ScopedFact list =
    sources
    |> List.choose (fun s ->
        match s.Origin, s.FactRendering with
        | Fact, Some rendering ->
            Some {
                FactId = s.FactId |> Option.defaultValue ""
                Rendering = rendering
                Metric = ""
            }
        | _ -> None)

// ─── The seam (Phase 523.B) ──────────────────────────────────────────

/// Pluggable answer-verification seam. The default `NumericFidelityVerifier`
/// is the deterministic numeric tier; the future LLM-judge qualitative tier
/// implements the SAME interface (a smarter body over the same inputs).
///
/// **GP 12.** Stateless per call — every input arrives as a parameter and
/// nothing is retained between calls. The interface is a pure, synchronous
/// in-memory computation, so it carries no retry obligation of its own; an
/// I/O-backed implementation (the qualitative judge) owns its own retry
/// policy as data internally, exactly as `IAIProvider` does. Implementations
/// MUST NOT throw — an unparseable token is a verdict, never an exception.
type IAnswerVerifier =
    abstract Verify:
        answer: string * facts: ScopedFact list * registry: Grounding.IMetricRegistry option * mode: string ->
            AnswerVerification

module NumericFidelity =

    /// Pure verification: extract, canonicalise, and match every numeric
    /// token against the fact tier. `registry` (when present) supplies each
    /// fact's declared display precision (Phase 519); absent, the fact's own
    /// canonical rendering is parsed symmetrically.
    let verify
        (answer: string)
        (facts: ScopedFact list)
        (registry: Grounding.IMetricRegistry option)
        (mode: string)
        : AnswerVerification =
        // Pre-parse the facts once — `(fact, value, precision)`.
        let parsedFacts =
            facts
            |> List.choose (fun f ->
                let metric = registry |> Option.bind (fun r -> r.TryGetMetric f.Metric)

                match Canonical.parse f.Rendering with
                | None -> None
                | Some(v, p) ->
                    let precision =
                        match metric |> Option.bind (fun m -> Canonical.formatPrecision m.DisplayFormat) with
                        | Some fp -> max p fp
                        | None -> p

                    Some(f, v, precision))

        let hasFacts = not (List.isEmpty facts)

        let numbers =
            extractTokens answer
            |> List.map (fun tok ->
                match Canonical.parse tok.Text with
                | None ->
                    // Extraction only yields numerics, so this is defensive.
                    // A token we cannot canonicalise cannot be verified.
                    {
                        Token = tok.Text
                        Canonical = None
                        Verdict = (if hasFacts then NumberUnmatched else NoFactsInScope)
                        MatchedFactId = None
                    }
                | Some(tv, tp) ->
                    let canonical = Some((Canonical.round tp tv).ToString(CultureInfo.InvariantCulture))

                    if not hasFacts then
                        {
                            Token = tok.Text
                            Canonical = canonical
                            Verdict = NoFactsInScope
                            MatchedFactId = None
                        }
                    else
                        match parsedFacts |> List.tryFind (fun (_, fv, _) -> Canonical.valuesMatch tp tv fv) with
                        | Some(f, _, _) -> {
                            Token = tok.Text
                            Canonical = canonical
                            Verdict = NumberVerified
                            MatchedFactId = (if f.FactId = "" then None else Some f.FactId)
                          }
                        | None -> {
                            Token = tok.Text
                            Canonical = canonical
                            Verdict = NumberUnmatched
                            MatchedFactId = None
                          })

        {
            Verified = numbers |> List.filter (fun n -> n.Verdict = NumberVerified) |> List.length
            Unmatched = numbers |> List.filter (fun n -> n.Verdict = NumberUnmatched) |> List.length
            Unverifiable = numbers |> List.filter (fun n -> n.Verdict = NoFactsInScope) |> List.length
            Numbers = numbers
            Mode = mode
        }

/// The default deterministic numeric-fidelity verifier (Phase 523.B).
type NumericFidelityVerifier() =
    interface IAnswerVerifier with
        member _.Verify(answer, facts, registry, mode) =
            NumericFidelity.verify answer facts registry mode

// ─── Answer rewrites (Phase 523.C) ───────────────────────────────────

[<Literal>]
let private strictFlag =
    "[⚠ figure withheld — an unverified number could not be matched to a grounded fact]"

/// `Annotate`: append a non-destructive footnote listing the unverified
/// figures. The answer body is left intact; only a clearly-marked note is
/// added. Returns the answer verbatim when nothing was unmatched. (`Strict`
/// withholds the sentences instead and adds a count-only note — it never
/// re-lists the values.)
let private appendUnverifiedFootnote (answer: string) (verification: AnswerVerification) : string =
    let unmatched =
        verification.Numbers
        |> List.filter (fun n -> n.Verdict = NumberUnmatched)
        |> List.map _.Token

    match unmatched with
    | [] -> answer
    | tokens ->
        let list = String.Join(", ", tokens)

        answer
        + "\n\n> ⚠️ **Unverified figures:** "
        + list
        + " — these numbers were not found in the retrieved facts and may be inaccurate."

/// `Strict` withholding: replace every sentence carrying an unmatched number
/// with an explicit inline flag (never a silent rewrite), then append the
/// footnote. Sentences are delimited by `. ! ? \n`; replacements are applied
/// right-to-left so earlier offsets stay valid. Verified / unverifiable
/// sentences are untouched.
let private withholdUnverifiedSentences (answer: string) (verification: AnswerVerification) : string =
    let tokens = extractTokens answer

    // `verify` maps tokens 1:1 to `Numbers` in order, so the lengths match;
    // guard defensively and skip the rewrite if they ever diverge.
    if tokens.Length <> verification.Numbers.Length then
        appendUnverifiedFootnote answer verification
    else
        let terminators = [| '.'; '!'; '?'; '\n' |]

        let sentenceBounds (index: int) : int * int =
            let mutable start = index - 1

            while start >= 0 && not (Array.contains answer.[start] terminators) do
                start <- start - 1

            let mutable stop = index

            while stop < answer.Length && not (Array.contains answer.[stop] terminators) do
                stop <- stop + 1

            // include the terminator so it is removed with the sentence
            (start + 1), (if stop < answer.Length then stop + 1 else stop)

        // Distinct sentence spans containing at least one unmatched token.
        let spans =
            List.zip tokens verification.Numbers
            |> List.choose (fun (tok, verdict) ->
                if verdict.Verdict = NumberUnmatched then
                    Some(sentenceBounds tok.Index)
                else
                    None)
            |> List.distinct
            |> List.sortByDescending fst

        let withheld =
            spans
            |> List.fold
                (fun (text: string) (start, stop) -> text.Substring(0, start) + strictFlag + text.Substring stop)
                answer

        // A COUNT-ONLY note — never the raw values. They were just withheld;
        // re-listing them in a footnote (as `Annotate` does) would defeat the
        // withholding.
        match verification.Unmatched with
        | 0 -> withheld
        | n ->
            let plural = if n = 1 then "figure" else "figures"
            withheld + $"\n\n> ⚠️ {n} unverified {plural} withheld pending grounding."

// ─── The post-response stage (Phase 523.C / D / E) ───────────────────

/// The composed gate config resolved from DI (`withNumericFidelityGate`).
/// Absent from DI ⇒ the gate is `Off` and `runVerificationStage`
/// short-circuits (GP 13).
type AnswerGate = {
    Mode: AnswerGateMode
    Verifier: IAnswerVerifier
}

let private auditJsonOptions = FableConverters.create ()

// ─── The provenance join (Phase 680) ─────────────────────────────────
//
// Two verifiable chains meet here and nowhere else. The serve-tier chain
// (Phase 657's boot verification into Phase 658's hash-chained ledger)
// ends at "a runtime action happened"; the grounding chain (provenance
// traversal into signed grounding certificates) ends at "this fact was
// produced this way". The answer-verification verdict is the one event
// that is BOTH — a runtime action whose whole content is a claim about
// facts — so it is the row that can name each chain's end and let a
// reader walk between them.
//
// What that costs is one audit row per verified answer, carrying the
// fact ids the answer cites, a digest over them, and — where a
// deployment has them — the certificate covering the chain and the
// sealed composition the process affirmed at boot. Nothing here issues,
// signs, or verifies anything: it records the anchors, and each anchor
// resolves in the store that owns it.
//
// **Additive by construction.** The join rides a NEW entry point; the
// pre-680 `runVerificationStage` delegates to it with an empty join, so
// a deployment that composes neither an `IAuditLog` nor an anchors
// implementation runs the byte-identical pre-680 path (GP 11 / GP 13).

/// The audit half of the verification stage: where the typed row is
/// recorded, and what deployment-side anchors it may name.
///
/// A record rather than two more positional parameters on an already
/// long signature — and a NEW record rather than fields on `AnswerGate`,
/// which every consumer of `withAnswerVerifier` constructs.
type AnswerAuditJoin = {
    /// Where the typed `AnswerVerification*` row is recorded. `None`
    /// records nothing, which is what a deployment with no audit log
    /// composed has to mean.
    AuditLog: IAuditLog option
    /// The deployment-side anchors the row may name. `None` leaves every
    /// join field `None` — an honest absence, not a placeholder.
    Anchors: IAnswerProvenanceAnchors option
}

[<RequireQualifiedAccess>]
module AnswerAuditJoin =
    /// No audit row, no anchors — the pre-Phase-680 behaviour exactly.
    let none: AnswerAuditJoin = { AuditLog = None; Anchors = None }

    /// Record the row through `auditLog`, naming no deployment anchors.
    /// The shape a deployment that has an audit log but neither
    /// certificates nor a sealed composition composes.
    let auditOnly (auditLog: IAuditLog) : AnswerAuditJoin = {
        AuditLog = Some auditLog
        Anchors = None
    }

[<RequireQualifiedAccess>]
module AnswerProvenanceAnchors =

    /// Anchors that name nothing. Behaviourally identical to composing
    /// none; useful where a value is required rather than an option.
    let none: IAnswerProvenanceAnchors =
        { new IAnswerProvenanceAnchors with
            member _.CompositionSealId = None
            member _.TryCertificateRef(_, _, _) = async { return None }
        }

    /// Anchors naming a composition seal and no certificate — the shape a
    /// deployment running a verified composition profile without the
    /// certificate substrate composes.
    let compositionSeal (sealId: string option) : IAnswerProvenanceAnchors =
        { new IAnswerProvenanceAnchors with
            member _.CompositionSealId = sealId
            member _.TryCertificateRef(_, _, _) = async { return None }
        }

    /// The composition-seal identity a boot verification affirmed: the
    /// digest of the deploy record the sealed composition binding names.
    ///
    /// **`None` unless the verdict was affirmative.** A binding is present
    /// on a drifted or failed boot too, and naming its seal on an answer
    /// row would assert precisely what the boot check declined to affirm —
    /// that the running composition is the sealed one. Under the
    /// log-and-serve default such a process keeps serving, which is exactly
    /// when the distinction is load-bearing.
    let fromBootVerification
        (result: BootVerificationResult)
        (binding: SealedCompositionBinding option)
        : IAnswerProvenanceAnchors =
        let sealId =
            if BootVerificationVerdict.isAffirmative result.Verdict then
                binding |> Option.map _.Binding.DeployRecordDigest
            else
                None

        compositionSeal sealId

/// Stable wire label for a token's verdict, matching the vocabulary the
/// `AnswerVerificationTokenAudit.Verdict` field documents.
let private verdictLabel (verdict: NumericVerdict) : string =
    match verdict with
    | NumberVerified -> "verified"
    | NumberUnmatched -> "unmatched"
    | NoFactsInScope -> "no-facts-in-scope"

/// The provenance chain head an answer stands on: SHA-256 over the
/// canonical join of the distinct fact ids it cites.
///
/// Deployment-independent and recomputable — a party holding the cited
/// ids derives the same digest with no access to this deployment, which
/// is what makes it a join key rather than a local handle. `None` for an
/// answer that verified against no fact: a digest over nothing would look
/// like a chain head and name no chain.
let provenanceChainHead (citedFactIds: string list) : string option =
    match citedFactIds with
    | [] -> None
    | ids ->
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n", ids)))
        |> Array.map (sprintf "%02x")
        |> String.concat ""
        |> Some

/// The post-response numeric-fidelity stage, with the Phase 680 provenance
/// join. Verifies the answer against the turn's retrieved facts, emits the
/// SSE verification event, audits every unmatched token to `IEventStore`
/// (GP 6), records verified/unmatched counters (Phase 523.E), records ONE
/// typed `AnswerVerification*` row through `join.AuditLog` naming the
/// provenance the answer stands on, and returns the (possibly
/// `Strict`-withheld / `Annotate`-footnoted) answer plus the verdict to
/// persist on the `ConversationMessage`.
///
/// `Off` mode (or an absent `gate`) returns `(answer, None)` with zero side
/// effects — byte-for-byte the pre-523 path (GP 11 / GP 13). An empty
/// `join` (`AnswerAuditJoin.none`) records no typed row and leaves the
/// `IEventStore` trail exactly as Phase 523 wrote it.
let runVerificationStageWithJoin
    (gate: AnswerGate option)
    (registry: Grounding.IMetricRegistry option)
    (sources: RetrievedSource list)
    (answer: string)
    (metricsSink: IMetricsSink option)
    (eventStore: IEventStore option)
    (join: AnswerAuditJoin)
    (scopeId: string)
    (taskId: Guid)
    (conversationId: Guid)
    (providerName: string)
    (providerModel: string)
    (logger: ILogger)
    : Async<string * AnswerVerification option> =
    async {
        match gate with
        | None
        | Some { Mode = AnswerGateOff } -> return (answer, None)
        | Some g ->
            let modeStr = AnswerGateMode.toString g.Mode
            let facts = scopedFacts sources
            let verification = g.Verifier.Verify(answer, facts, registry, modeStr)

            // Metrics — one increment per token outcome (Phase 523.E).
            match metricsSink with
            | Some sink ->
                let tags =
                    Map.ofList [ "provider", providerName; "model", providerModel; "mode", modeStr ]

                for n in verification.Numbers do
                    match n.Verdict with
                    | NumberVerified -> sink.Increment(VerifiedTotal, tags)
                    | NumberUnmatched -> sink.Increment(UnmatchedTotal, tags)
                    | NoFactsInScope -> ()
            | None -> ()

            // Audit — one durable record per unmatched token (GP 6). Bounded
            // + best-effort: a wedged event store must never crash or block
            // the conversation. Mirrors the citation-audit shape.
            match eventStore with
            | Some store ->
                for n in verification.Numbers do
                    if n.Verdict = NumberUnmatched then
                        let payload: UnverifiedNumberAudit = {
                            TaskId = taskId
                            ConversationId = conversationId
                            Token = n.Token
                            Canonical = n.Canonical |> Option.defaultValue ""
                            Mode = modeStr
                            FactsInScope = List.length facts
                            ProviderName = providerName
                            ProviderModel = providerModel
                        }

                        let evt: ModuleEvent = {
                            Id = Guid.NewGuid()
                            OccurredAt = DateTime.UtcNow
                            ScopeId = scopeId
                            SourceModule = AuditSourceModule
                            EventType = AuditEventType
                            Payload = JsonSerializer.Serialize(payload, auditJsonOptions)
                        }

                        try
                            let writeTask = store.Write evt |> Async.StartAsTask
                            let timeoutTask = Task.Delay 5_000
                            let! winner = Task.WhenAny(writeTask :> Task, timeoutTask) |> Async.AwaitTask

                            if winner = (writeTask :> Task) then
                                do! writeTask |> Async.AwaitTask
                            else
                                logger.Warn
                                    $"AI answer-verification audit write timed out (taskId={taskId}, token={n.Token}); record dropped, conversation unaffected."
                        with ex ->
                            logger.Warn
                                $"AI answer-verification audit write failed (taskId={taskId}, token={n.Token}): {ex.Message}. Record dropped; conversation unaffected."
            | None -> ()

            // Phase 680 — ONE typed row per verified answer, through
            // `IAuditLog`, BESIDE the per-token `IEventStore` trail above.
            // The two are different surfaces answering different questions:
            // the event-store rows are the module-scoped query surface for
            // unverified figures, this row is the chained, sink-replicated
            // statement of the whole verdict plus the provenance it stands
            // on. Recorded on the affirmative verdict too (Phase 657's
            // discipline) — absence of a row must stay a different fact
            // from a clean one.
            match join.AuditLog with
            | Some auditLog ->
                let citedFactIds =
                    verification.Numbers
                    |> List.choose (fun n ->
                        match n.Verdict, n.MatchedFactId with
                        | NumberVerified, Some id when id <> "" -> Some id
                        | _ -> None)
                    |> List.distinct
                    |> List.sort

                let! certificateRef =
                    match join.Anchors with
                    | Some anchors -> anchors.TryCertificateRef(scopeId, conversationId, citedFactIds)
                    | None -> async { return None }

                let payload: AnswerVerificationPayload = {
                    TaskId = taskId
                    ConversationId = conversationId
                    Mode = modeStr
                    Verified = verification.Verified
                    Unmatched = verification.Unmatched
                    Unverifiable = verification.Unverifiable
                    FactsInScope = List.length facts
                    Tokens =
                        verification.Numbers
                        |> List.map (fun n -> {
                            Token = n.Token
                            Canonical = n.Canonical |> Option.defaultValue ""
                            Verdict = verdictLabel n.Verdict
                            MatchedFactId = n.MatchedFactId
                        })
                    CitedFactIds = citedFactIds
                    ProvenanceChainHead = provenanceChainHead citedFactIds
                    CertificateRef = certificateRef
                    CompositionSealId = join.Anchors |> Option.bind _.CompositionSealId
                    ProviderName = providerName
                    ProviderModel = providerModel
                    OccurredAt = DateTimeOffset.UtcNow
                }

                let event =
                    if verification.Unmatched > 0 then
                        AuditEvent.AnswerVerificationFlagged payload
                    else
                        AuditEvent.AnswerVerificationPassed payload

                // Best-effort, exactly as the event-store write above: a
                // wedged audit backend must never crash or block the
                // conversation. `IAuditLog.Record` already swallows its own
                // store failures; this guard covers a composed sink that
                // throws or hangs on the way in.
                try
                    let recordTask = auditLog.Record(scopeId, event) |> Async.StartAsTask
                    let timeoutTask = Task.Delay 5_000
                    let! winner = Task.WhenAny(recordTask :> Task, timeoutTask) |> Async.AwaitTask

                    if winner = (recordTask :> Task) then
                        do! recordTask |> Async.AwaitTask
                    else
                        logger.Warn
                            $"AI answer-verification audit row timed out (taskId={taskId}); record dropped, conversation unaffected."
                with ex ->
                    logger.Warn
                        $"AI answer-verification audit row failed (taskId={taskId}): {ex.Message}. Record dropped; conversation unaffected."
            | None -> ()

            // Text rewrite by mode (Phase 523.C).
            let finalAnswer =
                match g.Mode with
                | AnswerGateOff -> answer
                | AnswerGateAnnotate -> appendUnverifiedFootnote answer verification
                | AnswerGateStrict -> withholdUnverifiedSentences answer verification

            return (finalAnswer, Some verification)
    }

/// The pre-Phase-680 entry point, preserved verbatim: the verification
/// stage with no provenance join. Delegates with `AnswerAuditJoin.none`,
/// so an existing call site records no typed audit row and behaves exactly
/// as it did (GP 11).
let runVerificationStage
    (gate: AnswerGate option)
    (registry: Grounding.IMetricRegistry option)
    (sources: RetrievedSource list)
    (answer: string)
    (metricsSink: IMetricsSink option)
    (eventStore: IEventStore option)
    (scopeId: string)
    (taskId: Guid)
    (conversationId: Guid)
    (providerName: string)
    (providerModel: string)
    (logger: ILogger)
    : Async<string * AnswerVerification option> =
    runVerificationStageWithJoin
        gate
        registry
        sources
        answer
        metricsSink
        eventStore
        AnswerAuditJoin.none
        scopeId
        taskId
        conversationId
        providerName
        providerModel
        logger
// ─── Phase 686 — the deployment verification report's join source ────
//
// The report lives in `ToolUp.Platform.Server`, upstream of this
// assembly, so it cannot reach `provenanceChainHead` by reference
// without inverting the dependency graph (GP 1). Its seam is a thunk,
// and this is the adapter that fills it.
//
// **What the check actually establishes, stated precisely because the
// section's verdict is only worth its precision.** Phase 680 records, on
// one row, both the distinct fact ids an answer's verified figures cite
// AND the provenance head derived from exactly those ids. So the head
// RECOMPUTES from the ids on its own row, by the same function that
// derived it — and a row where it does not is a row whose join was
// written by something other than this code path.
//
// It is an internal-consistency check over recorded evidence, and it is
// not a claim that the answer was correct, that the cited facts were
// true, or that no answer went unrecorded. The report's own not-proved
// statements carry those bounds; this adapter's job is to be exactly as
// strong as the join it re-derives, and no stronger.

/// The wire `EventType` discriminators Phase 680 records answer
/// verifications under. BOTH are read: a flagged answer is still an
/// answer whose join must hold, and reading only the passing rows would
/// skip precisely the turns an assessor cares most about.
let private answerVerificationEventTypes = [ "AnswerVerificationPassed"; "AnswerVerificationFlagged" ]

/// Re-join one recorded answer-verification payload against the
/// provenance it names. `Ok ()` when the head recomputes (including the
/// honest `None`-for-no-facts case); `Error` describing the row when it
/// does not.
///
/// Pure and total — no I/O, no clock — so an auditor holding the exported
/// rows reaches the same verdict this process does.
let rejoinAnswerVerification (payload: AnswerVerificationPayload) : Result<unit, string> =
    // A row persisted before a field existed deserialises its list as
    // `null` on the STJ path, and a null F# list faults on every list
    // operation. Coerce before touching it.
    let citedFactIds =
        if isNull (box payload.CitedFactIds) then
            []
        else
            payload.CitedFactIds

    let recomputed = provenanceChainHead citedFactIds

    match recomputed, payload.ProvenanceChainHead with
    | Some computed, Some recorded when computed = recorded -> Ok()
    | None, None -> Ok()
    | Some computed, Some recorded ->
        Error(
            sprintf
                "task %O: the row records provenance head '%s' and its %d cited fact id(s) recompute to '%s'"
                payload.TaskId
                recorded
                citedFactIds.Length
                computed
        )
    | Some computed, None ->
        Error(
            sprintf
                "task %O: the row cites %d fact id(s) recomputing to '%s' and records no provenance head"
                payload.TaskId
                citedFactIds.Length
                computed
        )
    | None, Some recorded ->
        Error(sprintf "task %O: the row records provenance head '%s' and cites no fact id" payload.TaskId recorded)

/// The deployment verification report's answer-join source, over the
/// composed audit log.
///
/// Reads the recorded answer-verification rows for `scopeId` and
/// re-derives each row's provenance head from its own cited fact ids.
/// A deployment that composed the Phase 680 join but has served no
/// verified answer yet reports zero rows, which the report's section
/// renders as `Observed` rather than as a pass.
let deploymentVerificationSource
    (auditLog: IAuditLog)
    (scopeId: string)
    : unit -> Async<Result<AnswerJoinIntegrity, string>> =
    fun () -> async {
        let! rowSets =
            answerVerificationEventTypes
            |> List.map (fun eventType -> auditLog.GetAuditTrail(scopeId, None, Some eventType))
            |> Async.Parallel

        let payloads =
            rowSets
            |> Array.toList
            |> List.collect id
            |> List.choose (function
                | AnswerVerificationPassed p
                | AnswerVerificationFlagged p -> Some p
                | _ -> None)

        let results = payloads |> List.map rejoinAnswerVerification

        let mismatched =
            results
            |> List.choose (function
                | Error detail -> Some detail
                | Ok() -> None)

        let unanchored =
            payloads |> List.filter (fun p -> p.ProvenanceChainHead.IsNone) |> List.length

        return
            Ok {
                Rows = payloads.Length
                Rejoined = payloads.Length - mismatched.Length
                Mismatched = mismatched
                Unanchored = unanchored
            }
    }