// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.GroundedNarrativeRun

// ─── The grounded narrative run ──────────────────────────
//
// One entry point: a registered run (a prompt, the metrics it is written
// from, a section shape, the deployment's deterministic projections) and a
// scope go in; the agent loop runs with ONLY the tools the composition
// passed in — the fact tools, plus any declared reference tools — and the
// model returns prose in a fixed format whose every figure is a citation
// token. The run assembles a `NarrativeDocument` from that prose and the
// projections, puts it through the fact tier's grounding gate, and either
// publishes it (narrative store, knowledge base, grounding certificate) or
// repairs it (below) and publishes what is grounded — refusing it only when
// the definition's repair policy says so or nothing survives. The outcome is
// one audit row, beside one row per repair round.
//
// **Placement (985.A).** No package references the AI, fact and reporting
// server tiers together, and none should: the gate lives beside the fact
// tier (`FactNarrativeGroundingGate`), this run beside the agent loop, the
// two triggers beside the report producer registry and the reactive
// data-change seam, and they meet only through the seams in
// `ToolUp.Platform.Server` (`GroundedNarrative.fs`). The fact tools are
// passed IN, not resolved by name, so this package takes no dependency on
// the fact companion; the gate and the publisher are resolved from DI.
//
// **Who pays.** A run is a background job: no one is signed in,
// so the model provider is resolved under the scope the run CARRIES — a team
// scope as a member of that team — and the key configured at that scope funds
// the run, under a strict bring-your-own-key policy as under any other. The
// definition's principal stays the actor everything else is done as. A scope
// that holds no usable key is `GroundedNarrativeUnfunded`: a permanent,
// typed outcome, because waiting does not configure a key.
//
// **Its call budget.** No client waits on a run, so it is not
// held to the interactive assistant's 50-second call cap: the agent loop
// runs under `GroundedNarrativeRunOptions.CallBudget` —
// `defaultCallBudget` (300 s a call, a 16,384-token reply) unless the
// composition sets its own with `withCallBudget`. A reply still streaming
// when a call reaches it is `GroundedNarrativeOverBudget`: permanent, since
// another attempt meets the same budget, and its reason says how to raise it.
//
// **The format the model writes.** A JSON object
//
//     {"sections":[{"id":"summary","paragraphs":["Revenue was [[Revenue|1,250|<fact id>]] ..."]}]}
//
// where `[[label|value|reference]]` becomes a `Metric` span. A token with
// no reference (`[[label|value]]`) becomes a span with none, which the gate
// refuses; a token that does not parse stays text, which — carrying its
// digits — the gate refuses too. The model never writes the title, the
// section headings, a table or a chart: it may REQUEST a table, and may mark
// a hypothesis as conjecture (Phase 1136, below).

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Narrative
open ToolUp.AI.AIToolRegistry

/// How a grounded run is composed.
type GroundedNarrativeRunOptions = {
    /// The tools the model may call — and the ONLY ones. Pass the fact
    /// tier's `NarrativeGrounding.factTools` plus a tool per declared
    /// reference kind the narratives should be able to look up.
    Tools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list
    /// The clock the provenance stamp reads.
    Clock: unit -> DateTimeOffset
    /// The budget every model call the run makes runs under.
    CallBudget: AIAgentEngine.AgentCallBudget
}

module GroundedNarrativeRunOptions =
    /// The call budget a run gets unless its composition sets one. 300 s a
    /// call: a report written from a day's facts streams for minutes, not
    /// the seconds a chat reply takes, and 300 s is the instance-wide
    /// timeout of the shipped HTTP connectors (a value up to the 600 s
    /// per-call ceiling is honoured). 16,384 reply tokens: four times the
    /// shipped connectors' 4,096 default, room for a multi-section report,
    /// and at typical streaming rates (~60 tokens/s) inside the 300 s.
    let defaultCallBudget: AIAgentEngine.AgentCallBudget = {
        CallTimeout = TimeSpan.FromSeconds 300.0
        MaxTokens = Some 16_384
        RaiseWith =
            Some
                "compose the run with GroundedNarrativeRunOptions.withCallBudget, raising CallTimeout (up to 600 s) or MaxTokens (up to the model's output limit)"
    }

    let create
        (tools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list)
        : GroundedNarrativeRunOptions =
        {
            Tools = tools
            Clock = fun () -> DateTimeOffset.UtcNow
            CallBudget = defaultCallBudget
        }

    /// Run under `budget` instead of `defaultCallBudget`. A budget with no
    /// `RaiseWith` gets the default's, so an exhausted budget still says how
    /// to raise it.
    /// **Adds** its registrations through `ServerApp.addServiceConfig`; a later `withExtensions` keeps them.
    let withCallBudget
        (budget: AIAgentEngine.AgentCallBudget)
        (options: GroundedNarrativeRunOptions)
        : GroundedNarrativeRunOptions =
        {
            options with
                CallBudget = {
                    budget with
                        RaiseWith = budget.RaiseWith |> Option.orElse defaultCallBudget.RaiseWith
                }
        }

// ── The format ───────────────────────────────────────────────────

let private citationToken =
    Regex(@"\[\[([^\[\]|]*)\|([^\[\]|]*)(?:\|([^\[\]|]*))?\]\]", RegexOptions.CultureInvariant)

/// One paragraph of model prose as spans: text between citation tokens,
/// and a `Metric` span per token.
let parseParagraph (text: string) : InlineSpan list =
    let spans = ResizeArray<InlineSpan>()
    let mutable last = 0

    for m in citationToken.Matches text do
        if m.Index > last then
            spans.Add(Text(text.Substring(last, m.Index - last)))

        let reference =
            if m.Groups[3].Success && m.Groups[3].Value.Trim() <> "" then
                Some(m.Groups[3].Value.Trim())
            else
                None

        spans.Add(Metric(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), reference))
        last <- m.Index + m.Length

    if last < text.Length then
        spans.Add(Text(text.Substring last))

    List.ofSeq spans

// **Conjecture (Phase 1136).** A hypothesis may be written only if it is
// marked as one: a paragraph `{"conjecture":"..."}` in place of a string, or
// a span `{{...}}` inside one. Either renders as a labelled callout after the
// prose it sits in. Its text is kept as plain text — a citation token inside
// it is NOT read as a figure — so any digit in a conjecture reaches the gate
// as an unreferenced number and the narrative is refused: a number is a fact
// or it is not written.
//
// **Requested projections (Phase 1136).** A section may carry
// `"projections":[{...}]`, each a request object naming its `kind`. The run
// builds each through the builder the definition registered for that kind,
// under the run's projection context, and checks it through the grounding
// gate on its own; a request that cannot be built or is not grounded is
// refused by name on the audit row, and the rest of the run goes on.

let private conjectureToken =
    Regex(@"\{\{([^{}]*)\}\}", RegexOptions.CultureInvariant)

/// One block of a section's prose as the model wrote it.
type GroundedProse =
    /// A paragraph of spans, citation tokens read as `Metric` spans.
    | ProseParagraph of InlineSpan list
    /// A marked hypothesis, as plain text.
    | ProseConjecture of string

/// One section of the model's answer.
type GroundedSectionDraft = {
    /// The section id the model wrote; `""` when it wrote none.
    Id: string
    /// The section's prose, in the order written.
    Prose: GroundedProse list
    /// The projections the model requested for the section.
    Projections: GroundedProjectionRequest list
}

/// The heading a conjecture callout carries.
[<Literal>]
let ConjectureLabel = "Conjecture — not a recorded fact"

/// The callout a conjecture renders as.
let conjectureCallout (text: string) : NarrativeElement =
    Callout(Info, [ Strong ConjectureLabel; Text(": " + text) ])

/// One paragraph string as prose blocks: the paragraph with every `{{...}}`
/// span lifted out, then each lifted span as a conjecture.
let private proseOf (text: string) : GroundedProse list =
    let conjectures =
        conjectureToken.Matches text
        |> Seq.map (fun m -> m.Groups[1].Value.Trim())
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> List.ofSeq

    let rest = conjectureToken.Replace(text, "")

    let paragraph =
        if String.IsNullOrWhiteSpace rest then
            []
        else
            [ ProseParagraph(parseParagraph rest) ]

    paragraph @ (conjectures |> List.map ProseConjecture)

let private stringProperty (name: string) (element: JsonElement) : string option =
    match element.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// Read the model's final answer: one draft per section. `Error` names
/// what is wrong with the format. A paragraph that is neither a string nor
/// a conjecture object is skipped, as before; a projection that is not an
/// object, or names no kind, is kept with an empty kind so the run refuses
/// it by name rather than dropping it.
let parseOutput (content: string) : Result<GroundedSectionDraft list, string> =
    if String.IsNullOrWhiteSpace content then
        Error "the model returned no narrative"
    else
        let first = content.IndexOf '{'
        let last = content.LastIndexOf '}'

        if first < 0 || last <= first then
            Error "the model's answer is not a JSON object"
        else
            try
                use doc = JsonDocument.Parse(content.Substring(first, last - first + 1))

                match doc.RootElement.TryGetProperty "sections" with
                | true, sections when sections.ValueKind = JsonValueKind.Array ->
                    sections.EnumerateArray()
                    |> Seq.map (fun section ->
                        let prose =
                            match section.TryGetProperty "paragraphs" with
                            | true, ps when ps.ValueKind = JsonValueKind.Array ->
                                ps.EnumerateArray()
                                |> Seq.collect (fun p ->
                                    match p.ValueKind with
                                    | JsonValueKind.String -> proseOf (p.GetString())
                                    | JsonValueKind.Object ->
                                        match stringProperty "conjecture" p with
                                        | Some c when not (String.IsNullOrWhiteSpace c) -> [
                                            ProseConjecture(c.Trim())
                                          ]
                                        | _ -> []
                                    | _ -> [])
                                |> List.ofSeq
                            | _ -> []

                        let projections =
                            match section.TryGetProperty "projections" with
                            | true, rs when rs.ValueKind = JsonValueKind.Array ->
                                rs.EnumerateArray()
                                |> Seq.map (fun r ->
                                    let kind =
                                        if r.ValueKind = JsonValueKind.Object then
                                            stringProperty "kind" r |> Option.defaultValue ""
                                        else
                                            ""

                                    {
                                        Kind = kind.Trim().ToLowerInvariant()
                                        Json = r.GetRawText()
                                    })
                                |> List.ofSeq
                            | _ -> []

                        {
                            Id = stringProperty "id" section |> Option.defaultValue ""
                            Prose = prose
                            Projections = projections
                        })
                    |> List.ofSeq
                    |> Ok
                | _ -> Error "the model's answer has no \"sections\" array"
            with ex ->
                Error(sprintf "the model's answer is not valid JSON: %s" ex.Message)

/// The narrative document a run publishes: the registered shape's title and
/// headings, then per section the model's prose (a conjecture as its
/// labelled callout), the deterministic projections, and the requested
/// projections that were built and grounded. `Error` when the model wrote
/// a section the shape does not declare.
let assemble
    (definition: GroundedNarrativeDefinition)
    (drafts: GroundedSectionDraft list)
    (projections: (string * NarrativeElement list) list)
    (requested: (string * NarrativeElement list) list)
    (provenance: NarrativeProvenance)
    : Result<NarrativeDocument, string> =
    let declared = definition.Sections |> List.map _.Id |> Set.ofList

    match drafts |> List.map _.Id |> List.filter (declared.Contains >> not) with
    | unknown :: _ -> Error(sprintf "the model wrote section '%s', which the run does not declare" unknown)
    | [] ->
        let proseFor id =
            drafts
            |> List.filter (fun d -> d.Id = id)
            |> List.collect _.Prose
            |> List.choose (function
                | ProseParagraph [] -> None
                | ProseParagraph spans -> Some(Paragraph spans)
                | ProseConjecture text -> Some(conjectureCallout text))

        let elementsFor (source: (string * NarrativeElement list) list) id =
            source |> List.filter (fun (sid, _) -> sid = id) |> List.collect snd

        Ok {
            Title = definition.Title
            Subtitle = None
            Sections =
                definition.Sections
                |> List.map (fun shape -> {
                    Id = shape.Id
                    Heading = shape.Heading
                    Subheading = None
                    Elements =
                        proseFor shape.Id
                        @ elementsFor projections shape.Id
                        @ elementsFor requested shape.Id
                })
            Provenance = Some provenance
            Lang = None
            CanonicalUrl = None
        }

/// The most projections one run may request. The rest are refused by name:
/// each request is a fact read and a gate pass, and a report is not a dump.
[<Literal>]
let MaxRequestedProjections = 8

/// The grounding rules every run's instruction ends with, after the wording
/// . A
/// report that breaks one is refused by the gate whatever the wording says.
let private citationRules: string list = [
    "The report is REFUSED, and never published, if any of these is broken:"
    "- Write every figure as a citation token [[label|value|reference]]: the label in words with no digits, the value exactly as the tool rendered it (or that figure rounded), the reference the fact's id (or kind:id for a declared reference)."
    "- Write no other digits anywhere, and no numbers in words."
    "- Cite only figures a tool returned. Do not compute differences, ratios or percentages yourself."
]

let private proseOnlyRule =
    "- Tables and charts are added for you. Write prose only."

let private answerShape =
    "Answer with ONLY a JSON object: {\"sections\":[{\"id\":\"<section id>\",\"paragraphs\":[\"...\"]}]}"

let rules: string list = citationRules @ [ proseOnlyRule; ""; answerShape ]

/// Phase 1136 — the rules a definition's run ends with. A definition that
/// registers no request builder and allows no conjecture gets `rules`,
/// byte for byte. Registered builders replace the prose-only line with the
/// projection request form, naming the registered kinds; an allowed
/// conjecture adds the conjecture form. The answer shape names what the
/// run reads.
let rulesFor (definition: GroundedNarrativeDefinition) : string list =
    let kinds = definition.RequestBuilders |> Map.toList |> List.map fst

    if List.isEmpty kinds && not definition.AllowsConjecture then
        rules
    else
        [
            yield! citationRules
            if List.isEmpty kinds then
                proseOnlyRule
            else
                "- Never write a table or its rows. To show one, add a request to the section's \"projections\": an object naming its \"kind\" ("
                + String.concat ", " kinds
                + "), \"tableId\", \"metrics\", optionally \"subjectPrefix\" and \"subjectLevel\", and \"period\" ({\"day\":\"yyyy-MM-dd\",\"rangeDays\":n}). The table is drawn from the facts for you. Request one only where a finding calls for it."
            if definition.AllowsConjecture then
                "- A hypothesis you cannot cite may be written only as a conjecture: a paragraph {\"conjecture\":\"...\"} in place of a string, or {{...}} inside a paragraph. It is shown labelled as conjecture, and must carry no figure, no number in words and no citation."
            ""
            if List.isEmpty kinds then
                answerShape
            else
                "Answer with ONLY a JSON object: {\"sections\":[{\"id\":\"<section id>\",\"paragraphs\":[\"...\"],\"projections\":[]}]}"
        ]

/// The instruction the model works from: the definition's wording (the
/// declared one, or an administrator's in its place), then `rulesFor` it.
let systemPrompt (definition: GroundedNarrativeDefinition) (toolNames: string list) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.AppendLine s |> ignore

    line "You write the prose of a report from verified facts, and nothing else."
    line ""
    line (sprintf "Report: %s" definition.Title)
    line (sprintf "Purpose: %s" definition.Prompt)

    if not (List.isEmpty definition.DependsOnMetrics) then
        line (sprintf "Metrics it is written from: %s" (String.concat ", " definition.DependsOnMetrics))

    line ""
    line "Sections — write prose for each, by id:"

    for shape in definition.Sections do
        line (sprintf "- %s (%s): %s" shape.Id shape.Heading shape.Guidance)

    line ""

    line (
        sprintf
            "Read the figures with your tools (%s). Each fact has an id and a canonical rendering."
            (String.concat ", " toolNames)
    )

    line ""

    for rule in rulesFor definition do
        line rule

    sb.ToString()

// ── The repair loop ──────────────────────────────────────────────
//
// A draft the gate refuses is repaired, not dropped: the run asks the model,
// in the same conversation, to rewrite the elements that hold the refused
// claims, splices each rewrite in place of its element alone, and puts the
// WHOLE document through the gate again — a rewrite can break a neighbour,
// and the gate stays the only judge. While the rounds make progress the
// loop goes on; when they stop (`GroundedRepairPolicy`), what is still
// refused is cut away at the smallest unit that holds it, a note names each
// withheld claim's place and reason (never its value — the note passes the
// gate itself), and the grounded remainder is published.
//
// The pure parts — progress, stopping, where a refused claim sits, the
// splice, the prune and the note — are below; the runner drives them.

/// The repair loop's pure decisions and document edits.
module GroundedRepair =

    /// Why the repair loop stopped.
    type Stop =
        /// No refused claim is left.
        | Settled
        /// `MaxRoundsWithoutProgress` rounds in a row made no progress.
        | NoProgress
        /// `MaxRepairRounds` rounds were taken.
        | Ceiling
        /// The run's model calls reached `MaxRunTokens`.
        | TokenBudget
        /// No refused claim sits in prose the model wrote, so there is
        /// nothing to ask it to rewrite.
        | Unrepairable
        /// A repair call produced no answer; the reason.
        | ModelFailed of reason: string

    module Stop =
        let describe (stop: Stop) : string =
            match stop with
            | Settled -> "every claim is grounded"
            | NoProgress -> "the repair stopped making progress"
            | Ceiling -> "the repair-round ceiling was reached"
            | TokenBudget -> "the run's token budget was reached"
            | Unrepairable -> "no refused claim is in prose the model can rewrite"
            | ModelFailed reason -> sprintf "a repair call failed: %s" reason

    /// Where the loop stands between rounds.
    type Progress = {
        /// Rounds taken.
        Rounds: int
        /// Rounds in a row, ending with the last, that made no progress.
        RoundsWithoutProgress: int
        /// Tokens the run's model calls have spent.
        Tokens: int64
    }

    /// Before the first round.
    let start: Progress = {
        Rounds = 0
        RoundsWithoutProgress = 0
        Tokens = 0L
    }

    /// Whether `next` is progress on `previous`: fewer refused claims, or as
    /// many with different members (the model is working on them, not
    /// repeating itself).
    let isProgress (previous: GroundingOffence list) (next: GroundingOffence list) : bool =
        let members = List.map GroundingOffence.describe >> Set.ofList

        List.length next < List.length previous
        || (List.length next = List.length previous && members next <> members previous)

    /// Whether the loop stops before another round, and why.
    let shouldStop (policy: GroundedRepairPolicy) (progress: Progress) : Stop option =
        if progress.Rounds >= policy.MaxRepairRounds then
            Some Ceiling
        elif progress.RoundsWithoutProgress >= policy.MaxRoundsWithoutProgress then
            Some NoProgress
        else
            match policy.MaxRunTokens with
            | Some budget when progress.Tokens >= budget -> Some TokenBudget
            | _ -> None

    /// The progress after one more round, which `made` progress or not, with
    /// the run's token count now at `tokens`.
    let advance (made: bool) (tokens: int64) (progress: Progress) : Progress = {
        Rounds = progress.Rounds + 1
        RoundsWithoutProgress = if made then 0 else progress.RoundsWithoutProgress + 1
        Tokens = tokens
    }

    // ── Where a refused claim sits ──────────────────────────────

    /// The smallest unit of an element a refused claim can be cut at.
    type Part =
        /// The element as a whole.
        | WholeElement
        /// A top-level span of a paragraph, heading, callout or quote.
        | Span of int
        /// A list item.
        | Item of int
        /// A table row.
        | Row of int
        /// A key-value entry.
        | Entry of int

    /// The section, the element (numbered from one) and the part of it a
    /// refused claim sits in.
    type Place = {
        Section: string
        Element: int
        Part: Part
    }

    let private located =
        Regex(@"^section '(?<section>.*?)' › element (?<element>\d+)(?<rest>.*)$", RegexOptions.CultureInvariant)

    let private firstStep =
        Regex(@"^ › (?<step>span|item|row|entry) (?<n>\d+)", RegexOptions.CultureInvariant)

    /// Read the place out of a gate offence's `Location`
    /// (`section 'summary' › element 2 › span 1`, `… › row 3 › column 2`).
    /// `None` when it is not a place in a section's element list.
    let tryPlace (location: string) : Place option =
        let m = located.Match location

        if not m.Success then
            None
        else
            let step = firstStep.Match m.Groups["rest"].Value

            let part =
                if not step.Success then
                    WholeElement
                else
                    let n = int step.Groups["n"].Value

                    match step.Groups["step"].Value with
                    | "span" -> Span n
                    | "item" -> Item n
                    | "row" -> Row n
                    | _ -> Entry n

            Some {
                Section = m.Groups["section"].Value
                Element = int m.Groups["element"].Value
                Part = part
            }

    // ── The splice ──────────────────────────────────────────────

    /// One element the model rewrote.
    type Repair = {
        Section: string
        /// The element's number in its section, from one.
        Element: int
        /// The rewrite, in the format the model writes a paragraph in.
        Text: string
    }

    let private isConjecture (element: NarrativeElement) =
        match element with
        | Callout(_, Strong label :: _) -> label = ConjectureLabel
        | _ -> false

    /// Whether `element` is prose the model wrote, and so the model's to
    /// rewrite: a paragraph, or a conjecture callout.
    let isProse (element: NarrativeElement) : bool =
        match element with
        | Paragraph _ -> true
        | other -> isConjecture other

    /// The elements one paragraph string of the model's becomes, as
    /// `assemble` builds them.
    let elementsOf (text: string) : NarrativeElement list =
        proseOf text
        |> List.choose (function
            | ProseParagraph [] -> None
            | ProseParagraph spans -> Some(Paragraph spans)
            | ProseConjecture conjecture -> Some(conjectureCallout conjecture))

    /// A prose element in the format the model writes it in: each `Metric`
    /// span back as its citation token, a conjecture as `{{...}}`.
    let tokensOf (element: NarrativeElement) : string =
        let rec spans (xs: InlineSpan list) =
            xs
            |> List.map (function
                | Text s
                | Emphasis s
                | Strong s
                | Code s -> s
                | Metric(label, value, Some reference) -> sprintf "[[%s|%s|%s]]" label value reference
                | Metric(label, value, None) -> sprintf "[[%s|%s]]" label value
                | Link(_, inner) -> spans inner
                | Image(_, alt, _) -> alt
                | Br -> " ")
            |> String.concat ""

        match element with
        | Paragraph xs -> spans xs
        | Callout(_, _ :: rest) when isConjecture element -> "{{" + (spans rest).TrimStart(':', ' ') + "}}"
        | _ -> ""

    /// The document with each repaired element replaced by its rewrite and
    /// nothing else moved. A repair naming an element that is not the
    /// model's prose is ignored; a later repair of one element replaces an
    /// earlier.
    let splice (repairs: Repair list) (document: NarrativeDocument) : NarrativeDocument =
        let rewrites =
            repairs |> List.map (fun r -> (r.Section, r.Element), r.Text) |> Map.ofList

        {
            document with
                Sections =
                    document.Sections
                    |> List.map (fun section -> {
                        section with
                            Elements =
                                section.Elements
                                |> List.mapi (fun i element ->
                                    match rewrites.TryFind(section.Id, i + 1) with
                                    | Some text when isProse element -> elementsOf text
                                    | _ -> [ element ])
                                |> List.concat
                    })
        }

    // ── The repair instruction and its answer ────────────────────

    /// The instruction for one repair round: each prose element holding a
    /// refused claim, as written, with every claim refused in it and why.
    /// `None` when no refused claim sits in the model's prose.
    let instruction (document: NarrativeDocument) (offences: GroundingOffence list) : string option =
        let elementAt (place: Place) =
            document.Sections
            |> List.tryFind (fun s -> s.Id = place.Section)
            |> Option.bind (fun s -> List.tryItem (place.Element - 1) s.Elements)

        let targets =
            offences
            |> List.choose (fun o ->
                tryPlace o.Location
                |> Option.bind (fun place ->
                    match elementAt place with
                    | Some element when isProse element -> Some((place.Section, place.Element), (element, o))
                    | _ -> None))
            |> List.groupBy fst
            |> List.map (fun (key, xs) -> key, fst (snd xs.Head), xs |> List.map (snd >> snd))

        if List.isEmpty targets then
            None
        else
            let sb = StringBuilder()
            let line (s: string) = sb.AppendLine s |> ignore

            line
                "The report was refused, and nothing was published: some claims are not grounded. Rewrite ONLY the elements below and leave every other element as it is. The rules still hold: every figure a citation token, no other digits, no numbers in words. Where a value is misstated, the reason gives the recorded value; cite that, with its reference. A claim you cannot ground, leave out."

            for ((section, element), written, refused) in targets do
                line ""
                line (sprintf "Section '%s', element %d, as written: %s" section element (tokensOf written))

                for o in refused do
                    line (sprintf "- refused: \"%s\" — %s" o.Claim (GroundingOffenceReason.describe o.Reason))

            line ""

            line
                "Answer with ONLY a JSON object: {\"repairs\":[{\"section\":\"<section id>\",\"element\":<element number>,\"text\":\"<the rewritten element>\"}]}"

            Some(sb.ToString())

    /// Read a repair answer. `Error` names what is wrong with its format.
    let parseRepairs (content: string) : Result<Repair list, string> =
        let first = if isNull content then -1 else content.IndexOf '{'
        let last = if isNull content then -1 else content.LastIndexOf '}'

        if first < 0 || last <= first then
            Error "the repair answer is not a JSON object"
        else
            try
                use doc = JsonDocument.Parse(content.Substring(first, last - first + 1))

                match doc.RootElement.TryGetProperty "repairs" with
                | true, repairs when repairs.ValueKind = JsonValueKind.Array ->
                    repairs.EnumerateArray()
                    |> Seq.choose (fun r ->
                        if r.ValueKind <> JsonValueKind.Object then
                            None
                        else
                            match stringProperty "section" r, r.TryGetProperty "element", stringProperty "text" r with
                            | Some section, (true, element), Some text when element.ValueKind = JsonValueKind.Number ->
                                match element.TryGetInt32() with
                                | true, n ->
                                    Some {
                                        Section = section
                                        Element = n
                                        Text = text
                                    }
                                | _ -> None
                            | _ -> None)
                    |> List.ofSeq
                    |> Ok
                | _ -> Error "the repair answer has no \"repairs\" array"
            with ex ->
                Error(sprintf "the repair answer is not valid JSON: %s" ex.Message)

    // ── The prune and the note ───────────────────────────────────

    let private meaningful (spans: InlineSpan list) =
        spans
        |> List.exists (function
            | Text s
            | Emphasis s
            | Strong s
            | Code s -> s |> Seq.exists Char.IsLetterOrDigit
            | Metric _
            | Link _
            | Image _ -> true
            | Br -> false)

    let private without (numbers: Set<int>) (xs: 'a list) : 'a list =
        xs
        |> List.indexed
        |> List.filter (fun (i, _) -> not (numbers.Contains(i + 1)))
        |> List.map snd

    /// `element` with `parts` cut away; `None` when nothing of it survives —
    /// a paragraph with no meaningful span left, a list with no item, a
    /// table with no row. A part that does not fit the element (or any
    /// `WholeElement`) takes the whole element.
    let cut (parts: Part list) (element: NarrativeElement) : NarrativeElement option =
        let numbers pick =
            let picked = parts |> List.map pick

            if List.forall Option.isSome picked then
                Some(picked |> List.choose id |> Set.ofList)
            else
                None

        let spans =
            numbers (function
                | Span n -> Some n
                | _ -> None)

        let items =
            numbers (function
                | Item n -> Some n
                | _ -> None)

        let rows =
            numbers (function
                | Row n -> Some n
                | _ -> None)

        let entries =
            numbers (function
                | Entry n -> Some n
                | _ -> None)

        let prose (rebuild: InlineSpan list -> NarrativeElement) (xs: InlineSpan list) =
            match spans with
            | Some cutAt ->
                let kept = without cutAt xs
                if meaningful kept then Some(rebuild kept) else None
            | None -> None

        let nonEmpty (rebuild: 'a list -> NarrativeElement) (cutAt: Set<int> option) (xs: 'a list) =
            match cutAt with
            | Some cutAt ->
                match without cutAt xs with
                | [] -> None
                | kept -> Some(rebuild kept)
            | None -> None

        match element with
        | _ when isConjecture element -> None
        | Paragraph xs -> prose Paragraph xs
        | Heading(level, xs) -> prose (fun kept -> Heading(level, kept)) xs
        | Callout(severity, xs) -> prose (fun kept -> Callout(severity, kept)) xs
        | Blockquote(citation, xs) -> prose (fun kept -> Blockquote(citation, kept)) xs
        | BulletList xs -> nonEmpty BulletList items xs
        | OrderedList xs -> nonEmpty OrderedList items xs
        | Table(columns, xs) -> nonEmpty (fun kept -> Table(columns, kept)) rows xs
        | KeyValueGrid xs -> nonEmpty KeyValueGrid entries xs
        | _ -> None

    /// The document with every refused claim cut away at the smallest unit
    /// that holds it: a span, else a list item, table row or entry, else the
    /// element. `Error` names a claim whose place cannot be read.
    let prune (offences: GroundingOffence list) (document: NarrativeDocument) : Result<NarrativeDocument, string> =
        let placed = offences |> List.map (fun o -> o, tryPlace o.Location)

        match placed |> List.tryFind (snd >> Option.isNone) with
        | Some(o, _) -> Error(sprintf "the place of a refused claim cannot be read: %s" o.Location)
        | None ->
            let partsAt =
                placed
                |> List.choose snd
                |> List.groupBy (fun p -> p.Section, p.Element)
                |> List.map (fun (key, places) -> key, places |> List.map _.Part)
                |> Map.ofList

            Ok {
                document with
                    Sections =
                        document.Sections
                        |> List.map (fun section -> {
                            section with
                                Elements =
                                    section.Elements
                                    |> List.mapi (fun i element ->
                                        match partsAt.TryFind(section.Id, i + 1) with
                                        | Some parts -> cut parts element
                                        | None -> Some element)
                                    |> List.choose id
                        })
            }

    /// The id of the section the withheld-claims note is published as.
    [<Literal>]
    let WithheldSectionId = "withheld-claims"

    /// The heading the withheld-claims note carries.
    [<Literal>]
    let WithheldHeading = "Withheld claims"

    /// Why a claim was withheld, in words a reader of the report can take
    /// in — no reference, no value, no digit.
    let withheldReason (reason: GroundingOffenceReason) : string =
        match reason with
        | UnreferencedNumber -> "a number that is not a recorded fact"
        | MetricWithoutReference -> "a figure that cites no recorded fact"
        | UnresolvedReference _ -> "a figure whose reference does not resolve"
        | SupersededReference _ -> "a figure whose recorded fact has since been revised"
        | UndisclosableReference _ -> "a figure that may not be disclosed here"
        | MisstatedValue _ -> "a figure that does not match its recorded value"
        | UnregisteredReferenceKind _ -> "a figure citing an unknown kind of reference"

    /// The note a report published with withheld claims carries: each
    /// withheld claim's place (the section by its heading, the kind of unit
    /// cut) and its reason, and never the value it stated. A heading that
    /// carries a digit is named as "a section", so the note states no number
    /// and passes the gate on its own.
    let withheldNote (document: NarrativeDocument) (offences: GroundingOffence list) : NarrativeSection =
        let sectionName (id: string) =
            match document.Sections |> List.tryFind (fun s -> s.Id = id) with
            | Some s when
                not (String.IsNullOrWhiteSpace s.Heading)
                && not (s.Heading |> Seq.exists Char.IsDigit)
                ->
                sprintf "the section \"%s\"" s.Heading
            | _ -> "a section"

        let unitName (part: Part) =
            match part with
            | WholeElement -> "an element"
            | Span _ -> "part of a paragraph"
            | Item _ -> "a list item"
            | Row _ -> "a table row"
            | Entry _ -> "an entry"

        let items =
            offences
            |> List.map (fun o ->
                let where =
                    match tryPlace o.Location with
                    | Some place -> sprintf "%s in %s" (unitName place.Part) (sectionName place.Section)
                    | None -> "a claim"

                sprintf "%s: %s" where (withheldReason o.Reason))
            |> List.distinct
            |> List.map (fun s -> [ Text s ])

        {
            Id = WithheldSectionId
            Heading = WithheldHeading
            Subheading = None
            Elements = [
                Paragraph [
                    Text "Some claims in this report could not be grounded in a recorded fact, so they were withheld:"
                ]
                BulletList items
            ]
        }

    /// Whether anything of the report itself survives in `document` — a
    /// section other than the note with an element left.
    let survives (document: NarrativeDocument) : bool =
        document.Sections
        |> List.exists (fun s -> s.Id <> WithheldSectionId && not (List.isEmpty s.Elements))

/// The run's model calls, counted: every call goes to `inner`, and the
/// tokens each reply reports (prompt plus output) are summed, so the repair
/// loop can hold the run to its token budget. Forwards the per-call options
/// capability, so the run's call budget reaches the provider unchanged.
type internal TokenTally(inner: IAIProvider) =
    let mutable tokens = 0L

    let count (sent: Result<AIProviderResponse, AIProviderError>) =
        match sent with
        | Ok response ->
            match response.Usage with
            | Some usage ->
                Interlocked.Add(&tokens, int64 usage.PromptTokens + int64 usage.OutputTokens)
                |> ignore
            | None -> ()
        | Error _ -> ()

        sent

    /// The tokens the calls so far reported.
    member _.Tokens: int64 = Interlocked.Read(&tokens)

    interface IAIProvider with
        member _.Capabilities = inner.Capabilities

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            let! sent = inner.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy)
            return count sent
        }

        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) = async {
            let! sent = inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)
            return count sent
        }

    interface IAIProviderModelOverride with
        member _.SendMessageWith(options, messages, tools, systemPrompt, onStream, retryPolicy) = async {
            let! sent = inner.SendMessageWith(options, messages, tools, systemPrompt, onStream, retryPolicy)

            return
                sent
                |> Result.map (fun call ->
                    count (Ok call.Response) |> ignore
                    call)
        }

        member _.SendStructuredMessageWith(options, messages, tools, systemPrompt, schema, retryPolicy) = async {
            let! sent = inner.SendStructuredMessageWith(options, messages, tools, systemPrompt, schema, retryPolicy)

            return
                sent
                |> Result.map (fun call ->
                    count (Ok call.Response) |> ignore
                    call)
        }

// ── The run ──────────────────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

let private tryService<'T> (sp: IServiceProvider) : 'T option =
    match sp.GetService(typeof<'T>) with
    | :? 'T as service -> Some service
    | _ -> None

/// The audit row's payload. Names references and policies, never a value
/// the gate withheld. Internal, not private, because the wording admin
/// (`NarrativePromptHandler`) reads a narrative's last run back from it.
type internal GroundedNarrativeAuditRow = {
    RunKey: string
    Trigger: string
    Outcome: string
    NarrativeId: string option
    Citations: string list
    Offences: string list
    CertificateDigest: string option
    Detail: string option
    /// The wording version the run's instruction was built
    /// from; 0 is the declaration. A row written before 1111 reads as 0,
    /// which is what those runs used.
    PromptVersion: int
    /// Set when a saved wording existed and could not be used
    /// (unreadable, or it no longer fits the declaration), so the run fell
    /// back to the declared wording.
    PromptFallback: string option
    /// Phase 1136 — how many conjectures the narrative carried.
    Conjectures: int
    /// Phase 1136 — how many requested projections were drawn.
    Projections: int
    /// Phase 1136 — each requested projection refused, naming the request
    /// and why. A row written before 1136 reads as none.
    ProjectionRefusals: string list
    /// The repair rounds the run took; a row written before the repair loop
    /// existed reads as 0, which is what those runs took.
    RepairRounds: int
    /// Each claim withheld from a narrative published with withheld claims,
    /// as the gate refused it. A row written before the repair loop reads
    /// as none.
    Withheld: string list
}

/// One repair round, on the audit trail beside the run's outcome row.
type internal GroundedNarrativeRepairRow = {
    RunKey: string
    Trigger: string
    /// The round's number, from one.
    Round: int
    /// The refused claims the round was asked to repair.
    OffencesIn: string list
    /// The refused claims after it.
    OffencesOut: string list
    /// Whether it made progress (`GroundedRepair.isProgress`).
    Progress: bool
    /// The tokens the run's model calls had spent when it ended.
    Tokens: int64
    /// What went wrong in it, when something did: an unreadable answer, or
    /// a failed call.
    Detail: string option
}

module internal GroundedNarrativeAuditRow =
    /// A row read back from the trail; `None` for a payload that is not one.
    let tryParse (payload: string) : GroundedNarrativeAuditRow option =
        try
            let row =
                JsonSerializer.Deserialize<GroundedNarrativeAuditRow>(payload, jsonOptions)

            if isNull (box row) || String.IsNullOrEmpty row.RunKey then
                None
            else
                Some {
                    row with
                        ProjectionRefusals =
                            if isNull (box row.ProjectionRefusals) then
                                []
                            else
                                row.ProjectionRefusals
                        Withheld = if isNull (box row.Withheld) then [] else row.Withheld
                }
        with _ ->
            None

// ── The administrator's wording ───────────────────────────────
//
// A scope's saved wording for one narrative is one blob in the scope's own
// container, `narrative-prompts/<run key>`, holding every saved version
// (oldest first, at most `NarrativePromptLimits.MaxVersions`). It is written
// through `BlobMapStore` — the guarded read-modify-write every one-document
// blob store uses — so two admins saving at once cannot lose a version, and
// an unreadable blob is never read as "no wording" and overwritten.
//
// Keyed by the scope's CONTAINER, which only the platform's scope resolution
// produces: a run reads the wording of the scope it runs in, and the admin
// API writes the wording of the scope its request resolved to, so one
// team's wording cannot reach another team's run.

/// Every saved version of one narrative's wording in one scope.
type internal NarrativePromptHistory = {
    Versions: NarrativePromptVersion list
}

/// The wording a run's instruction was built from.
type internal ResolvedWording = {
    Definition: GroundedNarrativeDefinition
    /// 0 for the declaration.
    Version: int
    /// Why a saved wording was not used, when one existed.
    Fallback: string option
}

type internal NarrativePromptStore(storage: IBlobStorage, logger: ILogger) =
    let codec: BlobCodec<NarrativePromptHistory> = {
        Encode = fun history -> Encoding.UTF8.GetBytes(JsonSerializer.Serialize(history, jsonOptions))
        Decode =
            fun bytes ->
                try
                    let history =
                        JsonSerializer.Deserialize<NarrativePromptHistory>(Encoding.UTF8.GetString bytes, jsonOptions)

                    if isNull (box history) then
                        Error "the wording history is empty"
                    elif isNull (box history.Versions) then
                        Ok { Versions = [] }
                    else
                        Ok {
                            Versions =
                                history.Versions
                                |> List.map (fun v ->
                                    if isNull (box v.Guidance) then
                                        { v with Guidance = [] }
                                    else
                                        v)
                        }
                with ex ->
                    Error ex.Message
    }

    let docs = BlobMapStore<NarrativePromptHistory>(storage, codec, logger)

    static member BlobName(runKey: string) =
        "narrative-prompts/" + Uri.EscapeDataString runKey

    /// Every saved version, oldest first. `Ok []` when none was ever saved;
    /// `Error` when a saved history exists and cannot be read.
    member _.Versions(container: string, runKey: string) : Async<Result<NarrativePromptVersion list, string>> = async {
        match! docs.Read(container, NarrativePromptStore.BlobName runKey) with
        | Ok None -> return Ok []
        | Ok(Some history) -> return Ok history.Versions
        | Error error -> return Error(BlobMapStoreError.describe error)
    }

    /// Append the version `make` builds from the stored versions. `make` may
    /// refuse (`Error`) or decide there is nothing to write (`Ok None`);
    /// nothing is written then. Keeps the newest `MaxVersions`. `make` may run
    /// more than once: a lost race replays it over the fresh read.
    member _.Append
        (
            container: string,
            runKey: string,
            make: NarrativePromptVersion list -> Result<NarrativePromptVersion option, string>
        ) : Async<Result<NarrativePromptVersion option, string>> =
        async {
            let! result =
                docs.Update(
                    container,
                    NarrativePromptStore.BlobName runKey,
                    fun stored ->
                        let versions = stored |> Option.map _.Versions |> Option.defaultValue []

                        match make versions with
                        | Error refusal -> BlobUpdate.Keep(Error refusal)
                        | Ok None -> BlobUpdate.Keep(Ok None)
                        | Ok(Some version) ->
                            let kept =
                                versions @ [ version ]
                                |> List.rev
                                |> List.truncate NarrativePromptLimits.MaxVersions
                                |> List.rev

                            BlobUpdate.Write({ Versions = kept }, Ok(Some version))
                )

            return
                match result with
                | Ok outcome -> outcome
                | Error error -> Error(BlobMapStoreError.describe error)
        }

module internal NarrativeWording =
    /// The saved version in force: the latest, unless it is a reset (or
    /// there is none), when the declaration is.
    let current (versions: NarrativePromptVersion list) : NarrativePromptVersion option =
        match List.tryLast versions with
        | Some latest when not latest.IsReset -> Some latest
        | _ -> None

    /// The latest version number saved, resets included; 0 when none.
    let latestNumber (versions: NarrativePromptVersion list) : int =
        versions |> List.tryLast |> Option.map _.Version |> Option.defaultValue 0

    /// `definition` with `version`'s wording in place.
    let apply
        (definition: GroundedNarrativeDefinition)
        (version: NarrativePromptVersion)
        : Result<GroundedNarrativeDefinition, string> =
        GroundedNarrativeDefinition.reword
            version.Prompt
            (version.Guidance |> List.map (fun g -> g.SectionId, g.Guidance))
            definition

    /// The store over `storage`; `None` when no blob storage is composed
    /// (then no wording can have been saved).
    let storeOver (storage: IBlobStorage option) (logger: ILogger option) : NarrativePromptStore option =
        storage
        |> Option.map (fun storage ->
            let logger =
                logger
                |> Option.defaultValue
                    { new ILogger with
                        member _.Debug _ = ()
                        member _.Info _ = ()
                        member _.Warn _ = ()
                        member _.Error(_, _) = ()
                    }

            NarrativePromptStore(storage, logger))

    /// The store over the composed services.
    let storeOf (services: IServiceProvider) : NarrativePromptStore option =
        storeOver (tryService<IBlobStorage> services) (tryService<ILogger> services)

    /// The wording a run in `scope` uses: the scope's saved version in force,
    /// or the declaration — and, when a saved wording existed and could not
    /// be used, the declaration with the reason.
    let resolve
        (store: NarrativePromptStore option)
        (scope: StorageScope)
        (definition: GroundedNarrativeDefinition)
        : Async<ResolvedWording> =
        async {
            let declared fallback = {
                Definition = definition
                Version = 0
                Fallback = fallback
            }

            match store with
            | None -> return declared None
            | Some store ->
                match! store.Versions(scope.Container, definition.Key) with
                | Error reason ->
                    return
                        declared (
                            Some(
                                sprintf "the saved wording could not be read (%s); the declared wording was used" reason
                            )
                        )
                | Ok versions ->
                    match current versions with
                    | None -> return declared None
                    | Some version ->
                        match apply definition version with
                        | Ok reworded ->
                            return {
                                Definition = reworded
                                Version = version.Version
                                Fallback = None
                            }
                        | Error reason ->
                            return
                                declared (
                                    Some(
                                        sprintf
                                            "saved wording version %d no longer fits the declaration (%s); the declared wording was used"
                                            version.Version
                                            reason
                                    )
                                )
        }

/// Why a run has no provider. `ProviderUnfunded`: the factory
/// ANSWERED that the scope holds no usable provider, which another attempt
/// would answer the same way. `ProviderUnavailable`: everything that may pass
/// — nothing composed yet, or the resolution itself threw (a secret store
/// outage reads as a throw, not an answer).
type private ProviderShortfall =
    | ProviderUnfunded of string
    | ProviderUnavailable of string

/// Why the model produced no narrative: a failure that may pass, or a reply
/// cut at the run's call budget, which will not.
type private GenerationShortfall =
    | GenerationFailed of string
    | GenerationOverBudget of string

/// Phase 1136 — what a run's audit row counts of its conjectures and
/// requested projections.
type private RunMarks = {
    Conjectures: int
    Projections: int
    ProjectionRefusals: string list
}

type GroundedNarrativeRunner
    (services: IServiceProvider, registry: GroundedNarrativeRegistry, options: GroundedNarrativeRunOptions) =

    let write (scopeId: string) (eventType: string) (payload: string) : Async<unit> = async {
        match tryService<IEventStore> services with
        | None -> return ()
        | Some events ->
            try
                do!
                    events.Write {
                        Id = Guid.NewGuid()
                        OccurredAt = DateTime.UtcNow
                        ScopeId = scopeId
                        SourceModule = GroundedNarrativeEvents.SourceModule
                        EventType = eventType
                        Payload = payload
                    }
            with _ ->
                ()
    }

    let record (scopeId: string) (eventType: string) (row: GroundedNarrativeAuditRow) : Async<unit> =
        write scopeId eventType (JsonSerializer.Serialize(row, jsonOptions))

    let recordRound (scopeId: string) (row: GroundedNarrativeRepairRow) : Async<unit> =
        write scopeId GroundedNarrativeEvents.RepairRoundType (JsonSerializer.Serialize(row, jsonOptions))

    /// Which wording a row's run used: the version and any
    /// fallback reason. A run that fails before its wording is resolved
    /// used none, and records the declaration's 0.
    let promptOf (wording: ResolvedWording option) =
        match wording with
        | Some w -> w.Version, w.Fallback
        | None -> 0, None

    let failedAs
        (outcome: string)
        (result: string -> GroundedNarrativeOutcome)
        (wording: ResolvedWording option)
        (request: GroundedNarrativeRequest)
        (reason: string)
        : Async<GroundedNarrativeOutcome> =
        async {
            let version, fallback = promptOf wording

            do!
                record request.Scope.ScopeId GroundedNarrativeEvents.FailedType {
                    RunKey = request.RunKey
                    Trigger = request.Trigger
                    Outcome = outcome
                    NarrativeId = None
                    Citations = []
                    Offences = []
                    CertificateDigest = None
                    Detail = Some reason
                    PromptVersion = version
                    PromptFallback = fallback
                    Conjectures = 0
                    Projections = 0
                    ProjectionRefusals = []
                    RepairRounds = 0
                    Withheld = []
                }

            return result reason
        }

    let failed = failedAs "failed" GroundedNarrativeFailed

    /// The scope holds no provider the funding policy allows.
    /// The same audit row as any failure, with its own outcome, so a reader
    /// of the trail can tell "configure a key" from "try again".
    let unfunded = failedAs "unfunded" GroundedNarrativeUnfunded

    /// The reply was cut at the run's call budget.
    let overBudget = failedAs "over-budget" GroundedNarrativeOverBudget

    let resolveProvider (access: AccessContext) : Async<Result<IAIProvider, ProviderShortfall>> = async {
        match tryService<IAIProvider> services with
        | Some provider -> return Ok provider
        | None ->
            match tryService<IAIProviderFactory> services with
            | None ->
                return
                    Error(
                        ProviderUnavailable
                            "no AI provider is composed (neither an IAIProvider nor an IAIProviderFactory)"
                    )
            | Some factory ->
                try
                    match! factory.Resolve access with
                    | Ok provider -> return Ok provider
                    | Error err -> return Error(ProviderUnfunded(ProviderResolutionError.toMessage err))
                with ex ->
                    return Error(ProviderUnavailable ex.Message)
    }

    /// The access context the provider is resolved under: the
    /// request's when it names one; else the scope the run carries, so the
    /// key configured at the team (or user) scope that triggered the run
    /// funds it; else — a scope that names no team or user, and so holds no
    /// AI configuration of its own — the definition's principal.
    let providerAccess
        (request: GroundedNarrativeRequest)
        (definition: GroundedNarrativeDefinition)
        (scope: StorageScope)
        : AccessContext =
        match request.Access with
        | Some access -> access
        | None ->
            AccessContext.forConfigScope definition.Principal scope
            |> Option.defaultWith (fun () -> AccessContext.unrestricted (AuthenticatedUser definition.Principal))

    /// The request context the agent loop and the fact tools read: the run's
    /// resolved scope (carried, never minted) and principal, over a fresh DI
    /// scope.
    let contextFor (resolved: ResolvedScope) (storage: StorageScope) (principal: string) : HttpContext * IServiceScope =
        let diScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope()
        let ctx = DefaultHttpContext()
        ctx.RequestServices <- diScope.ServiceProvider
        ctx.Items["ToolUp.StorageScope"] <- box storage
        StorageScopeResolver.ScopeResolution.carry ctx resolved
        ctx.Items["ToolUp.UserId"] <- box principal
        ctx :> HttpContext, diScope

    /// Phase 1136 — run `project` under the run's projection context: the
    /// same request context the model's fact tools read through, over its
    /// own DI scope, disposed when `project` is done.
    let withProjectionContext
        (definition: GroundedNarrativeDefinition)
        (resolved: ResolvedScope)
        (scope: StorageScope)
        (project: GroundedProjectionContext -> Async<'T>)
        : Async<'T> =
        async {
            let ctx, diScope = contextFor resolved scope definition.Principal

            try
                return!
                    project {
                        Scope = resolved
                        Principal = definition.Principal
                        Surface = definition.Surface
                        Request = ctx
                    }
            finally
                diScope.Dispose()
        }

    /// Phase 1136 — build each requested projection through the builder
    /// registered for its kind, and check what it built through the gate on
    /// its own. The drawn ones by section, and one refusal per request that
    /// was not drawn, naming the request and why — an offence by its
    /// location and reason, never by the value it states.
    let resolveRequested
        (gate: INarrativeGroundingGate)
        (definition: GroundedNarrativeDefinition)
        (context: GroundedProjectionContext)
        (drafts: GroundedSectionDraft list)
        : Async<(string * NarrativeElement list) list * string list> =
        async {
            let requests =
                drafts
                |> List.collect (fun d -> d.Projections |> List.map (fun r -> d.Id, r))
                |> List.indexed

            let registered =
                match definition.RequestBuilders |> Map.toList |> List.map fst with
                | [] -> "none"
                | kinds -> kinds |> List.map (sprintf "'%s'") |> String.concat ", "

            let drawn = ResizeArray<string * NarrativeElement list>()
            let refusals = ResizeArray<string>()

            for (i, (sectionId, request)) in requests do
                let name =
                    sprintf
                        "projection %d (%s) in section '%s'"
                        (i + 1)
                        (if request.Kind = "" then
                             "no kind"
                         else
                             sprintf "kind '%s'" request.Kind)
                        sectionId

                if i >= MaxRequestedProjections then
                    refusals.Add(sprintf "%s: a run may request at most %d projections" name MaxRequestedProjections)
                else
                    match definition.RequestBuilders.TryFind request.Kind with
                    | None ->
                        refusals.Add(
                            sprintf "%s: no builder is registered for that kind; the run registers %s" name registered
                        )
                    | Some build ->
                        let! built = async {
                            try
                                return! build context request
                            with ex ->
                                return Error(sprintf "the builder failed: %s" ex.Message)
                        }

                        match built with
                        | Error why -> refusals.Add(sprintf "%s: %s" name why)
                        | Ok [] -> refusals.Add(sprintf "%s: the builder drew nothing" name)
                        | Ok elements ->
                            let probe: NarrativeDocument = {
                                Title = definition.Title
                                Subtitle = None
                                Sections = [
                                    {
                                        Id = sectionId
                                        Heading = sectionId
                                        Subheading = None
                                        Elements = elements
                                    }
                                ]
                                Provenance = None
                                Lang = None
                                CanonicalUrl = None
                            }

                            match! gate.Check(context.Scope, definition.Principal, definition.Surface, probe) with
                            | Grounded _ -> drawn.Add((sectionId, elements))
                            | Ungrounded offences ->
                                offences
                                |> List.map (fun o ->
                                    sprintf "%s: %s" o.Location (GroundingOffenceReason.describe o.Reason))
                                |> String.concat "; "
                                |> sprintf "%s: not grounded: %s" name
                                |> refusals.Add

            return List.ofSeq drawn, List.ofSeq refusals
        }

    /// Run the agent loop over `messages` with the run's tools: the final
    /// answer's text and the conversation as it then stands, so a repair
    /// round can continue it.
    let converse
        (definition: GroundedNarrativeDefinition)
        (resolved: ResolvedScope)
        (scope: StorageScope)
        (provider: IAIProvider)
        (messages: AIProviderMessage list)
        : Async<Result<string * AIProviderMessage list, GenerationShortfall>> =
        async {
            let tools = AIToolRegistry()
            tools.RegisterAll(options.Tools |> List.map (fun (def, exec) -> createTool def exec))
            let toolNames = options.Tools |> List.map (fun (def, _) -> def.Name)

            let ctx, diScope = contextFor resolved scope definition.Principal

            try
                let turn =
                    AIAgentEngine.TurnContext.create
                        provider
                        tools
                        (ClientToolDispatch.ClientToolDispatchRegistry())
                        ctx
                        (Guid.NewGuid())
                        (Guid.NewGuid())
                        AISurface.FullPage
                        None
                        None
                        (ModelInput.ofSystemPrompt
                            "GroundedNarrativeRun"
                            (Some(systemPrompt definition toolNames))
                            messages)
                        options.CallBudget
                        SystemPromptBuilder.AgentLoopLimits.defaults
                        ignore
                        CancellationToken.None

                match! AIAgentEngine.run turn AIAgentEngine.TurnPlan.RunLoop messages with
                | Error(AIAgentEngine.LoopStop.CallBudgetExceeded _ as stop) ->
                    return Error(GenerationOverBudget(AIAgentEngine.LoopStop.message stop))
                | Error stop -> return Error(GenerationFailed(AIAgentEngine.LoopStop.message stop))
                | Ok { Stopped = Some stop } -> return Error(GenerationFailed(AIAgentEngine.LoopStop.message stop))
                | Ok state ->
                    let answer =
                        state.Messages
                        |> List.rev
                        |> List.tryFind (fun m -> m.Role = "assistant" && not (String.IsNullOrWhiteSpace m.Content))

                    match answer with
                    | Some message -> return Ok(message.Content, state.Messages)
                    | None -> return Error(GenerationFailed "the model returned no narrative")
            finally
                diScope.Dispose()
        }

    let userMessage (content: string) : AIProviderMessage = {
        Role = "user"
        Content = content
        ToolCalls = []
        ToolResults = []
        Parts = []
    }

    /// The first draft: the model's sections, and the conversation that
    /// wrote them.
    let generate
        (definition: GroundedNarrativeDefinition)
        (resolved: ResolvedScope)
        (scope: StorageScope)
        (provider: IAIProvider)
        : Async<Result<GroundedSectionDraft list * AIProviderMessage list, GenerationShortfall>> =
        async {
            match!
                converse definition resolved scope provider [ userMessage "Write the report now, citing every figure." ]
            with
            | Error shortfall -> return Error shortfall
            | Ok(content, conversation) ->
                return
                    parseOutput content
                    |> Result.map (fun drafts -> drafts, conversation)
                    |> Result.mapError GenerationFailed
        }

    /// Repair `document`, which the gate refused for `offences`, while the
    /// repair makes progress (`GroundedRepairPolicy`), recording every
    /// round. Answers the best document reached with the gate's verdict over
    /// it, the rounds taken and why the loop stopped. A round that made no
    /// progress is not kept: the next round starts from the best document,
    /// in the conversation as it now stands.
    let repair
        (request: GroundedNarrativeRequest)
        (gate: INarrativeGroundingGate)
        (definition: GroundedNarrativeDefinition)
        (scope: StorageScope)
        (tally: TokenTally)
        (document: NarrativeDocument)
        (offences: GroundingOffence list)
        (conversation: AIProviderMessage list)
        : Async<NarrativeDocument * NarrativeGroundingVerdict * int * GroundedRepair.Stop> =
        let policy = definition.RepairPolicy

        let rec loop document offences conversation (progress: GroundedRepair.Progress) = async {
            match GroundedRepair.shouldStop policy { progress with Tokens = tally.Tokens } with
            | Some stop -> return document, Ungrounded offences, progress.Rounds, stop
            | None ->
                match GroundedRepair.instruction document offences with
                | None -> return document, Ungrounded offences, progress.Rounds, GroundedRepair.Unrepairable
                | Some instruction ->
                    let round = progress.Rounds + 1
                    let described = offences |> List.map GroundingOffence.describe

                    let roundRow offencesOut made detail = {
                        RunKey = request.RunKey
                        Trigger = request.Trigger
                        Round = round
                        OffencesIn = described
                        OffencesOut = offencesOut
                        Progress = made
                        Tokens = tally.Tokens
                        Detail = detail
                    }

                    match!
                        converse
                            definition
                            request.Scope
                            scope
                            (tally :> IAIProvider)
                            (conversation @ [ userMessage instruction ])
                    with
                    | Error(GenerationFailed reason)
                    | Error(GenerationOverBudget reason) ->
                        do! recordRound request.Scope.ScopeId (roundRow described false (Some reason))
                        return document, Ungrounded offences, round, GroundedRepair.ModelFailed reason
                    | Ok(content, answered) ->
                        let repaired, detail =
                            match GroundedRepair.parseRepairs content with
                            | Ok repairs -> GroundedRepair.splice repairs document, None
                            | Error why -> document, Some why

                        let! verdict = gate.Check(request.Scope, definition.Principal, definition.Surface, repaired)

                        let after =
                            match verdict with
                            | Grounded _ -> []
                            | Ungrounded remaining -> remaining

                        let made = GroundedRepair.isProgress offences after

                        do!
                            recordRound
                                request.Scope.ScopeId
                                (roundRow (after |> List.map GroundingOffence.describe) made detail)

                        let next = GroundedRepair.advance made tally.Tokens progress

                        match verdict with
                        | Grounded _ -> return repaired, verdict, round, GroundedRepair.Settled
                        | Ungrounded _ when made -> return! loop repaired after answered next
                        | Ungrounded _ -> return! loop document offences answered next
        }

        loop document offences conversation GroundedRepair.start

    let publish
        (request: GroundedNarrativeRequest)
        (wording: ResolvedWording)
        (gate: INarrativeGroundingGate)
        (scope: StorageScope)
        (document: NarrativeDocument)
        (marks: RunMarks)
        (citations: NarrativeCitation list)
        (repairSummary: GroundedRepairSummary)
        : Async<GroundedNarrativeOutcome> =
        async {
            let definition = wording.Definition
            let tags = definition.Tags @ [ "grounded" ] |> List.distinct

            match!
                NarrativePublisher.publishForScope
                    services
                    request.Scope.ScopeId
                    definition.ModuleId
                    definition.PageRoute
                    tags
                    document
            with
            | None ->
                return!
                    failed
                        (Some wording)
                        request
                        "no narrative store is composed; the grounded narrative passed the gate and was not published"
            | Some narrativeId ->
                let! indexed = async {
                    match tryService<INarrativeIngestor> services with
                    | None -> return None
                    | Some ingestor ->
                        let! outcome = ingestor.Ingest(request.Scope, definition.Principal, document)
                        return Some outcome
                }

                let! certificate =
                    gate.Certify(
                        request.Scope,
                        definition.Principal,
                        string narrativeId,
                        NarrativeCitation.factIds citations
                    )

                do!
                    record request.Scope.ScopeId GroundedNarrativeEvents.PublishedType {
                        RunKey = request.RunKey
                        Trigger = request.Trigger
                        Outcome =
                            if GroundedRepairSummary.isComplete repairSummary then
                                "published"
                            else
                                "published-with-withheld"
                        NarrativeId = Some(string narrativeId)
                        Citations = citations |> List.map _.Reference
                        Offences = []
                        CertificateDigest =
                            match certificate with
                            | Ok c -> Some c.Digest
                            | Error _ -> None
                        Detail =
                            match certificate with
                            | Ok _ -> None
                            | Error reason -> Some(sprintf "no certificate: %s" reason)
                        PromptVersion = wording.Version
                        PromptFallback = wording.Fallback
                        Conjectures = marks.Conjectures
                        Projections = marks.Projections
                        ProjectionRefusals = marks.ProjectionRefusals
                        RepairRounds = repairSummary.Rounds
                        Withheld = repairSummary.Withheld |> List.map GroundingOffence.describe
                    }

                return GroundedNarrativePublished(narrativeId, document, citations, indexed, certificate, repairSummary)
        }

    /// The refusal: nothing is published, and the trail says why — every
    /// refused claim, and, when the run repaired, why the repair stopped.
    let refuse
        (request: GroundedNarrativeRequest)
        (wording: ResolvedWording)
        (marks: RunMarks)
        (offences: GroundingOffence list)
        (rounds: int)
        (detail: string option)
        : Async<GroundedNarrativeOutcome> =
        async {
            do!
                record request.Scope.ScopeId GroundedNarrativeEvents.RefusedType {
                    RunKey = request.RunKey
                    Trigger = request.Trigger
                    Outcome = "refused"
                    NarrativeId = None
                    Citations = []
                    Offences = offences |> List.map GroundingOffence.describe
                    CertificateDigest = None
                    Detail = detail
                    PromptVersion = wording.Version
                    PromptFallback = wording.Fallback
                    Conjectures = marks.Conjectures
                    Projections = marks.Projections
                    ProjectionRefusals = marks.ProjectionRefusals
                    RepairRounds = rounds
                    Withheld = []
                }

            return GroundedNarrativeRefused offences
        }

    /// After the repair loop: publish the document whole when it passed;
    /// otherwise — when the policy publishes the grounded remainder — cut
    /// the refused claims away, add the withheld-claims note, and publish
    /// what then passes the gate. Refuse when the policy does not publish a
    /// remainder, when a refused claim cannot be placed, when nothing of the
    /// report survives, or when the pruned document still does not pass.
    let settle
        (request: GroundedNarrativeRequest)
        (wording: ResolvedWording)
        (gate: INarrativeGroundingGate)
        (scope: StorageScope)
        (marks: RunMarks)
        (document: NarrativeDocument)
        (verdict: NarrativeGroundingVerdict)
        (rounds: int)
        (stop: GroundedRepair.Stop)
        : Async<GroundedNarrativeOutcome> =
        async {
            let definition = wording.Definition

            let stopped =
                if rounds = 0 && stop = GroundedRepair.Ceiling then
                    None
                else
                    Some(sprintf "repair stopped after %d round(s): %s" rounds (GroundedRepair.Stop.describe stop))

            let check document =
                gate.Check(request.Scope, definition.Principal, definition.Surface, document)

            match verdict with
            | Grounded citations ->
                return!
                    publish request wording gate scope document marks citations (GroundedRepairSummary.complete rounds)
            | Ungrounded offences when not definition.RepairPolicy.PublishGroundedRemainder ->
                return! refuse request wording marks offences rounds stopped
            | Ungrounded offences ->
                match GroundedRepair.prune offences document with
                | Error why -> return! refuse request wording marks offences rounds (Some why)
                | Ok pruned when not (GroundedRepair.survives pruned) ->
                    return!
                        refuse
                            request
                            wording
                            marks
                            offences
                            rounds
                            (Some "nothing of the report survives withholding its refused claims")
                | Ok pruned ->
                    let noted = {
                        pruned with
                            Sections = pruned.Sections @ [ GroundedRepair.withheldNote document offences ]
                    }

                    // A cut removes claims and adds none, so a second pass
                    // only ever meets a line of the note itself (a heading
                    // spelling a number in words): it is cut like any other.
                    let! first = check noted

                    let! final = async {
                        match first with
                        | Grounded citations -> return Ok(noted, citations)
                        | Ungrounded again ->
                            match GroundedRepair.prune again noted with
                            | Error why -> return Error why
                            | Ok recut ->
                                match! check recut with
                                | Grounded citations -> return Ok(recut, citations)
                                | Ungrounded still ->
                                    return
                                        Error(
                                            sprintf
                                                "the pruned report still holds %d refused claim(s)"
                                                (List.length still)
                                        )
                    }

                    match final with
                    | Ok(published, citations) ->
                        return!
                            publish request wording gate scope published marks citations {
                                Rounds = rounds
                                Withheld = offences
                            }
                    | Error why -> return! refuse request wording marks offences rounds (Some why)
        }

    member _.Run(request: GroundedNarrativeRequest) : Async<GroundedNarrativeOutcome> = async {
        match registry.TryResolve request.RunKey with
        | None -> return! failed None request (sprintf "no grounded narrative run '%s' is registered" request.RunKey)
        | Some declared ->
            match tryService<INarrativeGroundingGate> services with
            | None ->
                return!
                    failed
                        None
                        request
                        "no narrative grounding gate is composed (compose the fact tier); a narrative that cannot be checked is never published"
            | Some gate ->
                match request.Scope.Storage with
                | None ->
                    return!
                        failed
                            None
                            request
                            "the run was given the anonymous scope; a grounded run reads facts through the request-path fact tools, which need a scope the platform resolved"
                | Some scope ->
                    // The wording in force in THIS scope: an
                    // administrator's saved version, or the declaration.
                    // Only `Prompt` and section guidance can differ from
                    // `declared`; the rules come after it in `systemPrompt`.
                    let! wording = NarrativeWording.resolve (NarrativeWording.storeOf services) scope declared
                    let definition = wording.Definition
                    let failed = failed (Some wording)

                    match! resolveProvider (providerAccess request definition scope) with
                    | Error(ProviderUnfunded reason) -> return! unfunded (Some wording) request reason
                    | Error(ProviderUnavailable reason) -> return! failed request reason
                    | Ok provider ->
                        let tally = TokenTally provider

                        match! withProjectionContext definition request.Scope scope definition.Project with
                        | Error reason -> return! failed request (sprintf "the run's projections failed: %s" reason)
                        | Ok projections ->
                            match! generate definition request.Scope scope (tally :> IAIProvider) with
                            | Error(GenerationFailed reason) -> return! failed request reason
                            | Error(GenerationOverBudget reason) -> return! overBudget (Some wording) request reason
                            | Ok(drafts, conversation) ->
                                let provenance = {
                                    ModuleId = definition.ModuleId
                                    PageRoute = definition.PageRoute
                                    GeneratedAt = options.Clock()
                                    SettingsKey = sprintf "grounded-narrative:%s" definition.Key
                                    SettingsDisplay = [ "run", definition.Key; "trigger", request.Trigger ]
                                }

                                let! requested, projectionRefusals =
                                    if drafts |> List.forall (fun d -> List.isEmpty d.Projections) then
                                        async.Return([], [])
                                    else
                                        withProjectionContext definition request.Scope scope (fun context ->
                                            resolveRequested gate definition context drafts)

                                let marks = {
                                    Conjectures =
                                        drafts
                                        |> List.sumBy (fun d ->
                                            d.Prose
                                            |> List.filter (function
                                                | ProseConjecture _ -> true
                                                | ProseParagraph _ -> false)
                                            |> List.length)
                                    Projections = requested |> List.length
                                    ProjectionRefusals = projectionRefusals
                                }

                                match assemble definition drafts projections requested provenance with
                                | Error reason -> return! failed request reason
                                | Ok document ->
                                    match!
                                        gate.Check(request.Scope, definition.Principal, definition.Surface, document)
                                    with
                                    | Grounded citations ->
                                        return!
                                            publish
                                                request
                                                wording
                                                gate
                                                scope
                                                document
                                                marks
                                                citations
                                                (GroundedRepairSummary.complete 0)
                                    | Ungrounded offences ->
                                        let! best, verdict, rounds, stop =
                                            repair request gate definition scope tally document offences conversation

                                        return! settle request wording gate scope marks best verdict rounds stop
    }

    interface IGroundedNarrativeRun with
        member this.Run request = this.Run request

/// Construction and composition (seam:GroundedNarrativeRun's first
/// implementation).
module GroundedNarratives =

    /// A run over the composed services.
    let create
        (services: IServiceProvider)
        (registry: GroundedNarrativeRegistry)
        (options: GroundedNarrativeRunOptions)
        : IGroundedNarrativeRun =
        GroundedNarrativeRunner(services, registry, options) :> IGroundedNarrativeRun

    /// Compose grounded narratives onto an app: register the runs and the
    /// run. The data-arrival trigger arms itself where the fact tier is
    /// composed; the scheduled-report trigger is a report producer the
    /// deployment registers (`GroundedNarrativeProducer.create`). A
    /// duplicate run key fails the composition, naming both claimants.
    let compose
        (options: GroundedNarrativeRunOptions)
        (definitions: GroundedNarrativeDefinition list)
        (app: ServerApp)
        : ServerApp =
        let registry = GroundedNarrativeRegistry()

        for definition in definitions do
            match registry.Register definition with
            | Ok() -> ()
            | Error reason -> invalidOp reason

        let register (s: IServiceCollection) =
            s.AddSingleton<GroundedNarrativeRegistry>(registry) |> ignore

            s.AddSingleton<IGroundedNarrativeRun>(
                Func<IServiceProvider, IGroundedNarrativeRun>(fun sp -> create sp registry options)
            )

        app |> ServerApp.addServiceConfig register