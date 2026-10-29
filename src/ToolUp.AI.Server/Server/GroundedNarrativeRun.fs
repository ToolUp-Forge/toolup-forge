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
// refuses it. Either way the outcome is one audit row.
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
}

module internal GroundedNarrativeAuditRow =
    /// A row read back from the trail; `None` for a payload that is not one.
    let tryParse (payload: string) : GroundedNarrativeAuditRow option =
        try
            let row =
                JsonSerializer.Deserialize<GroundedNarrativeAuditRow>(payload, jsonOptions)

            if isNull (box row) || String.IsNullOrEmpty row.RunKey then
                None
            elif isNull (box row.ProjectionRefusals) then
                Some { row with ProjectionRefusals = [] }
            else
                Some row
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

    let record (scopeId: string) (eventType: string) (row: GroundedNarrativeAuditRow) : Async<unit> = async {
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
                        Payload = JsonSerializer.Serialize(row, jsonOptions)
                    }
            with _ ->
                ()
    }

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

                            match!
                                gate.Check(context.Scope.ScopeId, definition.Principal, definition.Surface, probe)
                            with
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

    let generate
        (definition: GroundedNarrativeDefinition)
        (resolved: ResolvedScope)
        (scope: StorageScope)
        (provider: IAIProvider)
        : Async<Result<GroundedSectionDraft list, GenerationShortfall>> =
        async {
            let tools = AIToolRegistry()
            tools.RegisterAll(options.Tools |> List.map (fun (def, exec) -> createTool def exec))
            let toolNames = options.Tools |> List.map (fun (def, _) -> def.Name)

            let messages: AIProviderMessage list = [
                {
                    Role = "user"
                    Content = "Write the report now, citing every figure."
                    ToolCalls = []
                    ToolResults = []
                    Parts = []
                }
            ]

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
                    | Some message -> return parseOutput message.Content |> Result.mapError GenerationFailed
                    | None -> return Error(GenerationFailed "the model returned no narrative")
            finally
                diScope.Dispose()
        }

    let publish
        (request: GroundedNarrativeRequest)
        (wording: ResolvedWording)
        (gate: INarrativeGroundingGate)
        (scope: StorageScope)
        (document: NarrativeDocument)
        (marks: RunMarks)
        (citations: NarrativeCitation list)
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
                        let! outcome = ingestor.Ingest(scope, definition.Principal, document)
                        return Some outcome
                }

                let! certificate =
                    gate.Certify(
                        request.Scope.ScopeId,
                        definition.Principal,
                        string narrativeId,
                        NarrativeCitation.factIds citations
                    )

                do!
                    record request.Scope.ScopeId GroundedNarrativeEvents.PublishedType {
                        RunKey = request.RunKey
                        Trigger = request.Trigger
                        Outcome = "published"
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
                    }

                return GroundedNarrativePublished(narrativeId, document, citations, indexed, certificate)
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
                        match! withProjectionContext definition request.Scope scope definition.Project with
                        | Error reason -> return! failed request (sprintf "the run's projections failed: %s" reason)
                        | Ok projections ->
                            match! generate definition request.Scope scope provider with
                            | Error(GenerationFailed reason) -> return! failed request reason
                            | Error(GenerationOverBudget reason) -> return! overBudget (Some wording) request reason
                            | Ok drafts ->
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
                                        gate.Check(
                                            request.Scope.ScopeId,
                                            definition.Principal,
                                            definition.Surface,
                                            document
                                        )
                                    with
                                    | Grounded citations ->
                                        return! publish request wording gate scope document marks citations
                                    | Ungrounded offences ->
                                        do!
                                            record request.Scope.ScopeId GroundedNarrativeEvents.RefusedType {
                                                RunKey = request.RunKey
                                                Trigger = request.Trigger
                                                Outcome = "refused"
                                                NarrativeId = None
                                                Citations = []
                                                Offences = offences |> List.map GroundingOffence.describe
                                                CertificateDigest = None
                                                Detail = None
                                                PromptVersion = wording.Version
                                                PromptFallback = wording.Fallback
                                                Conjectures = marks.Conjectures
                                                Projections = marks.Projections
                                                ProjectionRefusals = marks.ProjectionRefusals
                                            }

                                        return GroundedNarrativeRefused offences
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

        let serviceConfig =
            match app.Extensions.ServiceConfig with
            | None -> Some register
            | Some existing -> Some(fun s -> register (existing s))

        {
            app with
                Extensions = {
                    app.Extensions with
                        ServiceConfig = serviceConfig
                }
        }