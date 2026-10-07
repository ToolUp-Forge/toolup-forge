// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.GroundedNarrativeRun

// ─── The grounded narrative run (Phase 985) ──────────────────────────
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
// **Who pays (Phase 995).** A run is a background job: no one is signed in,
// so the model provider is resolved under the scope the run CARRIES — a team
// scope as a member of that team — and the key configured at that scope funds
// the run, under a strict bring-your-own-key policy as under any other. The
// definition's principal stays the actor everything else is done as. A scope
// that holds no usable key is `GroundedNarrativeUnfunded`: a permanent,
// typed outcome, because waiting does not configure a key.
//
// **The format the model writes.** A JSON object
//
//     {"sections":[{"id":"summary","paragraphs":["Revenue was [[Revenue|1,250|<fact id>]] ..."]}]}
//
// where `[[label|value|reference]]` becomes a `Metric` span. A token with
// no reference (`[[label|value]]`) becomes a span with none, which the gate
// refuses; a token that does not parse stays text, which — carrying its
// digits — the gate refuses too. The model never writes the title, the
// section headings, a table or a chart.

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
}

module GroundedNarrativeRunOptions =
    let create
        (tools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list)
        : GroundedNarrativeRunOptions =
        {
            Tools = tools
            Clock = fun () -> DateTimeOffset.UtcNow
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

/// Read the model's final answer: section id → paragraphs. `Error` names
/// what is wrong with the format.
let parseOutput (content: string) : Result<(string * InlineSpan list list) list, string> =
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
                        let id =
                            match section.TryGetProperty "id" with
                            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                            | _ -> ""

                        let paragraphs =
                            match section.TryGetProperty "paragraphs" with
                            | true, ps when ps.ValueKind = JsonValueKind.Array ->
                                ps.EnumerateArray()
                                |> Seq.filter (fun p -> p.ValueKind = JsonValueKind.String)
                                |> Seq.map (fun p -> parseParagraph (p.GetString()))
                                |> List.ofSeq
                            | _ -> []

                        id, paragraphs)
                    |> List.ofSeq
                    |> Ok
                | _ -> Error "the model's answer has no \"sections\" array"
            with ex ->
                Error(sprintf "the model's answer is not valid JSON: %s" ex.Message)

/// The narrative document a run publishes: the registered shape's title and
/// headings, the model's prose per section, then that section's projections.
/// `Error` when the model wrote a section the shape does not declare.
let assemble
    (definition: GroundedNarrativeDefinition)
    (prose: (string * InlineSpan list list) list)
    (projections: (string * NarrativeElement list) list)
    (provenance: NarrativeProvenance)
    : Result<NarrativeDocument, string> =
    let declared = definition.Sections |> List.map _.Id |> Set.ofList

    match prose |> List.map fst |> List.filter (declared.Contains >> not) with
    | unknown :: _ -> Error(sprintf "the model wrote section '%s', which the run does not declare" unknown)
    | [] ->
        let proseFor id =
            prose
            |> List.filter (fun (sid, _) -> sid = id)
            |> List.collect snd
            |> List.filter (List.isEmpty >> not)
            |> List.map Paragraph

        let projectedFor id =
            projections |> List.filter (fun (sid, _) -> sid = id) |> List.collect snd

        Ok {
            Title = definition.Title
            Subtitle = None
            Sections =
                definition.Sections
                |> List.map (fun shape -> {
                    Id = shape.Id
                    Heading = shape.Heading
                    Subheading = None
                    Elements = proseFor shape.Id @ projectedFor shape.Id
                })
            Provenance = Some provenance
            Lang = None
            CanonicalUrl = None
        }

/// The instruction the model works from.
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
    line "The report is REFUSED, and never published, if any of these is broken:"

    line
        "- Write every figure as a citation token [[label|value|reference]]: the label in words with no digits, the value exactly as the tool rendered it (or that figure rounded), the reference the fact's id (or kind:id for a declared reference)."

    line "- Write no other digits anywhere, and no numbers in words."
    line "- Cite only figures a tool returned. Do not compute differences, ratios or percentages yourself."
    line "- Tables and charts are added for you. Write prose only."
    line ""
    line "Answer with ONLY a JSON object: {\"sections\":[{\"id\":\"<section id>\",\"paragraphs\":[\"...\"]}]}"
    sb.ToString()

// ── The run ──────────────────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

let private tryService<'T> (sp: IServiceProvider) : 'T option =
    match sp.GetService(typeof<'T>) with
    | :? 'T as service -> Some service
    | _ -> None

/// The audit row's payload. Names references and policies, never a value
/// the gate withheld.
type private GroundedNarrativeAuditRow = {
    RunKey: string
    Trigger: string
    Outcome: string
    NarrativeId: string option
    Citations: string list
    Offences: string list
    CertificateDigest: string option
    Detail: string option
}

/// Why a run has no provider (Phase 995). `ProviderUnfunded`: the factory
/// ANSWERED that the scope holds no usable provider, which another attempt
/// would answer the same way. `ProviderUnavailable`: everything that may pass
/// — nothing composed yet, or the resolution itself threw (a secret store
/// outage reads as a throw, not an answer).
type private ProviderShortfall =
    | ProviderUnfunded of string
    | ProviderUnavailable of string

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

    let failedAs
        (outcome: string)
        (result: string -> GroundedNarrativeOutcome)
        (request: GroundedNarrativeRequest)
        (reason: string)
        : Async<GroundedNarrativeOutcome> =
        async {
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
                }

            return result reason
        }

    let failed = failedAs "failed" GroundedNarrativeFailed

    /// Phase 995 — the scope holds no provider the funding policy allows.
    /// The same audit row as any failure, with its own outcome, so a reader
    /// of the trail can tell "configure a key" from "try again".
    let unfunded = failedAs "unfunded" GroundedNarrativeUnfunded

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

    /// Phase 995 — the access context the provider is resolved under: the
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

    let generate
        (definition: GroundedNarrativeDefinition)
        (resolved: ResolvedScope)
        (scope: StorageScope)
        (provider: IAIProvider)
        : Async<Result<(string * InlineSpan list list) list, string>> =
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
                let! final =
                    AIAgentEngine.runAgentLoopWithInput
                        provider
                        tools
                        (ClientToolDispatch.ClientToolDispatchRegistry())
                        ctx
                        (Guid.NewGuid())
                        (Guid.NewGuid())
                        AISurface.FullPage
                        None
                        None
                        CancellationToken.None
                        messages
                        (ModelInput.ofSystemPrompt
                            "GroundedNarrativeRun"
                            (Some(systemPrompt definition toolNames))
                            messages)
                        ignore

                let answer =
                    final
                    |> List.rev
                    |> List.tryFind (fun m -> m.Role = "assistant" && not (String.IsNullOrWhiteSpace m.Content))

                match answer with
                | Some message -> return parseOutput message.Content
                | None -> return Error "the model returned no narrative"
            finally
                diScope.Dispose()
        }

    let publish
        (request: GroundedNarrativeRequest)
        (definition: GroundedNarrativeDefinition)
        (gate: INarrativeGroundingGate)
        (scope: StorageScope)
        (document: NarrativeDocument)
        (citations: NarrativeCitation list)
        : Async<GroundedNarrativeOutcome> =
        async {
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
                    }

                return GroundedNarrativePublished(narrativeId, document, citations, indexed, certificate)
        }

    member _.Run(request: GroundedNarrativeRequest) : Async<GroundedNarrativeOutcome> = async {
        match registry.TryResolve request.RunKey with
        | None -> return! failed request (sprintf "no grounded narrative run '%s' is registered" request.RunKey)
        | Some definition ->
            match tryService<INarrativeGroundingGate> services with
            | None ->
                return!
                    failed
                        request
                        "no narrative grounding gate is composed (compose the fact tier); a narrative that cannot be checked is never published"
            | Some gate ->
                match request.Scope.Storage with
                | None ->
                    return!
                        failed
                            request
                            "the run was given the anonymous scope; a grounded run reads facts through the request-path fact tools, which need a scope the platform resolved"
                | Some scope ->
                    match! resolveProvider (providerAccess request definition scope) with
                    | Error(ProviderUnfunded reason) -> return! unfunded request reason
                    | Error(ProviderUnavailable reason) -> return! failed request reason
                    | Ok provider ->
                        match! definition.Project request.Scope.ScopeId with
                        | Error reason -> return! failed request (sprintf "the run's projections failed: %s" reason)
                        | Ok projections ->
                            match! generate definition request.Scope scope provider with
                            | Error reason -> return! failed request reason
                            | Ok prose ->
                                let provenance = {
                                    ModuleId = definition.ModuleId
                                    PageRoute = definition.PageRoute
                                    GeneratedAt = options.Clock()
                                    SettingsKey = sprintf "grounded-narrative:%s" definition.Key
                                    SettingsDisplay = [ "run", definition.Key; "trigger", request.Trigger ]
                                }

                                match assemble definition prose projections provenance with
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
                                        return! publish request definition gate scope document citations
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