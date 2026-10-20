// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ReflectionTools

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 782 — the reflection door ─────────────────────────────────
//
// A deployment already describes itself three ways: the Phase 593
// composition inspector (what is composed, its knobs, rules and
// disclosure envelope), the Phase 686 verification report (what each
// integrity substrate says, and what the report does not prove), and the
// Phase 691/770 seam reach (what each composed component may and did
// reach). Each is an HTTP surface an operator reads. This file opens the
// same three to an agent — as three read-only AI tools — and to a driver
// or CI job without a running server — as the `--describe` startup mode
// `toolup describe --json` drives.
//
// **No fourth opinion about the deployment.** Every document here is one
// the existing projections already produce, serialised by the one
// function the inspector's export uses (`CompositionInspectorHandler.
// canonicalJson`). The composition tool calls the inspector API's own
// `ExportPanel`; the report tool calls the report API's own
// `GetVerificationReport`; the reach tool serialises the seam-authority
// evidence the report's section is gathered from. Nothing is re-rendered.
//
// **The gates are the APIs' own, applied, never copied.**
//   * `describe_composition` — the inspector's Owner/Admin gate
//     (`CompositionInspectorHandler.readGate`), through the API itself.
//   * `verification_report` — that gate AND the report API's Platform-Admin
//     gate (`DeploymentVerificationReport.readAllowed`). The report was
//     already Platform-Admin-only, and the inspector gate alone would WIDEN
//     it to team owners; applying both means the tool exposes nothing either
//     surface would refuse.
//   * `module_reach` — the same two gates: the reach is the report's
//     seam-authority evidence, so it carries the report's audience.
// All three are deployment-scoped and carry no caller data — every
// document describes THE DEPLOYMENT (the Phase 593 rationale), so there is
// nothing in them one tenant could read about another.
//
// **Every call is an audit row (GP 6)**, served or refused: a
// `RemotingMethodAudited` row of kind `Custom:DeploymentDescribed` in the
// platform scope, naming the tool and the outcome. An assistant's
// self-inspection is therefore on the same ledger as an operator's. The
// report tool additionally writes the report's own `DeploymentVerified`
// row, because it runs the report.
//
// **Registration is the platform's own AI-tool path, and opt-in.**
// `tools` is an ordinary `(AIToolDefinition * executor)` list;
// `AIServerApp.withReflectionTools` appends it to the composition's tool
// accumulator, which `composeAI` registers. A composition that composes no
// AI provider has no `AIServerApp` to call it on, and so registers nothing
// (GP 11 / GP 13); an AI composition that does not opt in offers the model
// exactly the tools it offered before.

// ─── Names ────────────────────────────────────────────────────────────

/// The reserved platform source every reflection tool declares. `_`-
/// prefixed, so the composition validator's orphaned-reference rule treats
/// it as a platform source rather than a missing module.
[<Literal>]
let SourceModule = "_platform.reflection"

[<Literal>]
let DescribeCompositionTool = "_platform.reflection.describe_composition"

[<Literal>]
let VerificationReportTool = "_platform.reflection.verification_report"

[<Literal>]
let ModuleReachTool = "_platform.reflection.module_reach"

/// The audit kind every reflection read records, through the open
/// `Custom:` vocabulary rather than a new `AuditEvent` case.
[<Literal>]
let AuditKind = "Custom:DeploymentDescribed"

/// The document slugs the offline door emits, in emission order.
[<Literal>]
let CompositionDocument = "composition"

[<Literal>]
let VerificationReportDocument = "verification-report"

[<Literal>]
let ModuleReachDocument = "module-reach"

/// Every document the offline door can emit, in the order it emits them.
let documents: string list = [ CompositionDocument; VerificationReportDocument; ModuleReachDocument ]

// ─── The seam-reach document ──────────────────────────────────────────

/// The note the reach document carries when the composition root supplied
/// no seam-authority evidence.
[<Literal>]
let SeamAuthorityNotComposedNote =
    "This deployment supplied no seam-authority evidence (DeploymentVerificationEvidence.withSeamAuthority), so there is no per-component reach to report. That is not a statement that the components reach nothing; the derived reach is computed from the module records at composition time and only a composition root that hands it over has it."

/// The note the reach document carries when no component has an observed
/// reach.
[<Literal>]
let ObservedReachAbsentNote =
    "ObservedReach is absent on every component: no runtime seam confinement is composed, so nothing was observed. Absent is not zero; an observed-nothing component would carry an empty list."

/// The per-component seam reach, as the deployment-verification evidence
/// carries it: each composed component's declared grant, the reach its
/// registrations imply (Phase 691) and the reach its code resolved since
/// boot (Phase 770, absent when no runtime confinement is composed).
type ModuleReachView = {
    /// `None` when the composition root supplied no seam-authority
    /// evidence; `Note` then says so.
    SeamAuthority: SeamAuthorityIntegrity option
    /// What a reader needs to read the document honestly, or `None`.
    Note: string option
}

module ModuleReachView =
    /// Project the reach document off the resolved evidence.
    let ofEvidence (evidence: IDeploymentVerificationEvidence) : ModuleReachView =
        match DeploymentVerificationEvidence.seamAuthorityOf evidence with
        | None -> {
            SeamAuthority = None
            Note = Some SeamAuthorityNotComposedNote
          }
        | Some integrity ->
            let observedAbsent =
                not (List.isEmpty integrity.Components)
                && integrity.Components |> List.forall _.ObservedReach.IsNone

            {
                SeamAuthority = Some integrity
                Note =
                    if observedAbsent then
                        Some ObservedReachAbsentNote
                    else
                        None
            }

// ─── The documents ────────────────────────────────────────────────────

/// Parse an inspector panel slug.
let private panelOf (slug: string) : Result<InspectorPanel, string> =
    match InspectorPanel.all |> List.tryFind (fun p -> InspectorPanel.slug p = slug) with
    | Some panel -> Ok panel
    | None ->
        Error(
            sprintf
                "unknown panel '%s'; expected one of: %s"
                slug
                (InspectorPanel.all |> List.map InspectorPanel.slug |> String.concat ", ")
        )

/// The report narrowed to the requested section ids, serialised. An empty
/// request is the whole report.
///
/// A narrowed report keeps `Outcome`, `VerdictDigest` and `NotProved`
/// whole: they describe the full report, and recomputing them over the
/// narrowed list would produce a digest no audited read ever recorded.
let verificationReportJson
    (report: DeploymentVerification.DeploymentVerificationReport)
    (sections: string list)
    : Result<string, string> =
    let known = report.Sections |> List.map _.Id

    match sections |> List.filter (fun s -> not (List.contains s known)) with
    | [] ->
        let narrowed =
            if List.isEmpty sections then
                report
            else
                {
                    report with
                        Sections = report.Sections |> List.filter (fun s -> List.contains s.Id sections)
                }

        Ok(CompositionInspectorHandler.canonicalJson narrowed)
    | unknown ->
        Error(
            sprintf
                "unknown report section(s) %s; expected any of: %s"
                (unknown |> String.concat ", ")
                (known |> String.concat ", ")
        )

/// The seam-reach document over a provider — the evidence the report's
/// seam-authority section is gathered from, observed reach overlaid.
let moduleReachJson (services: IServiceProvider) : string =
    DeploymentVerificationReport.resolveEvidence services
    |> ModuleReachView.ofEvidence
    |> CompositionInspectorHandler.canonicalJson

// ─── Arguments, refusals, audit ───────────────────────────────────────

/// The refusal / error document a tool returns instead of its document.
let private errorJson (code: string) (message: string) : string =
    CompositionInspectorHandler.canonicalJson {| error = code; message = message |}

/// The tool's argument object, or none when the model sent none.
let private argsOf (argsJson: string) : Result<JsonElement option, string> =
    if String.IsNullOrWhiteSpace argsJson then
        Ok None
    else
        try
            use doc = JsonDocument.Parse argsJson

            match doc.RootElement.ValueKind with
            | JsonValueKind.Object -> Ok(Some(doc.RootElement.Clone()))
            | JsonValueKind.Null -> Ok None
            | kind -> Error(sprintf "arguments must be a JSON object, not %O" kind)
        with :? JsonException as ex ->
            Error(sprintf "arguments are not valid JSON: %s" ex.Message)

let private optionalString (name: string) (args: JsonElement option) : Result<string option, string> =
    match args with
    | None -> Ok None
    | Some a ->
        match a.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Ok(Some(v.GetString()))
        | true, v when v.ValueKind = JsonValueKind.Null -> Ok None
        | true, _ -> Error(sprintf "'%s' must be a string" name)
        | _ -> Ok None

let private optionalStrings (name: string) (args: JsonElement option) : Result<string list, string> =
    match args with
    | None -> Ok []
    | Some a ->
        match a.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            let items = v.EnumerateArray() |> List.ofSeq

            if items |> List.forall (fun i -> i.ValueKind = JsonValueKind.String) then
                Ok(items |> List.map _.GetString())
            else
                Error(sprintf "'%s' must be an array of strings" name)
        | true, v when v.ValueKind = JsonValueKind.Null -> Ok []
        | true, _ -> Error(sprintf "'%s' must be an array of strings" name)
        | _ -> Ok []

/// The caller's subject id, in the dispatcher's audit rendering.
let private subjectOf (ctx: HttpContext) : string =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac ->
        match ac.Subject with
        | AnonymousSession sid -> "anonymous:" + sid
        | AuthenticatedUser uid -> "user:" + uid
        | TeamMember(uid, tid) -> "team:" + tid + ":user:" + uid
        | ClaimBearer claim -> "claim:" + claim.ScopeId
        | PublicationReader uid -> "publication-reader:" + uid
    | _ -> "anonymous"

/// Record one reflection read. Awaited rather than fire-and-forget, for
/// `DeploymentVerificationReport.recordRead`'s reason: `IAuditLog.Record`
/// is documented best-effort, and the offline door exits the process
/// straight afterwards.
let private audit
    (services: IServiceProvider)
    (subject: string)
    (method: string)
    (outcome: string)
    (detail: (string * string) list)
    : Async<unit> =
    async {
        match services.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as log ->
            let event =
                AuditEvent.RemotingMethodAudited {
                    Kind = AuditKind
                    MethodName = method
                    SubjectId = subject
                    CorrelationId = None
                    Payload = Map(("outcome", outcome) :: detail)
                }

            do! log.Record(DeploymentVerificationReport.PlatformScopeId, event)
        | _ -> ()
    }

// ─── The tools ────────────────────────────────────────────────────────

let private definition (name: string) (description: string) (parameters: ToolParameterSchema list) : AIToolDefinition = {
    Name = name
    Description = description
    Parameters = parameters
    SourceModule = SourceModule
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readContent
}

let describeCompositionDefinition: AIToolDefinition =
    definition
        DescribeCompositionTool
        "Describe what this deployment composes, from its own boot-time declarations: modules, companion slots, data types, AI tools, metrics, subjects, purposes, the configuration knobs that shaped composition and its canonical methods (panel 'composition', the default); the invariant rules and this deployment's defects against them ('rules'); the grounding and disclosure envelope with its digest ('disclosure'); the captured surface descriptors ('surfaces'); and whether the provenance substrate is composed ('provenance'). Ask this before reading the composition root's source. Team owners and admins only; describes the deployment, never any caller's data."
        [
            ToolSchema.parameter
                "panel"
                "Which inspector panel to return. Defaults to 'composition'."
                false
                (ToolSchema.enumOf (InspectorPanel.all |> List.map InspectorPanel.slug))
        ]

let verificationReportDefinition: AIToolDefinition =
    definition
        VerificationReportTool
        "Run this deployment's verification report: one verdict per integrity section (boot seal, grounding continuity, audit ledger, certificate issuance, answer joins, seam authority, configuration conformance and the rest), each section's findings, the top-line outcome, the verdict digest, and the statements of what the report does NOT prove. A NotComposed section is the report's own boundary, not a pass. Platform admins only; the run is itself recorded on the audit trail."
        [
            ToolSchema.parameter
                "sections"
                "Optional section ids to narrow the report to. Outcome, verdict digest and not-proved statements always describe the whole report."
                false
                (ToolSchema.arrayOf ToolSchema.text)
        ]

let moduleReachDefinition: AIToolDefinition =
    definition
        ModuleReachTool
        "Report, per composed component, the substrate seams it declared it may reach, the seams its registrations imply it reaches (derived), and the seams its code actually resolved since boot (observed; absent when no runtime confinement is composed, which is not the same as observed-nothing). Platform admins only; describes the deployment, never any caller's data."
        []

/// Run a read past the inspector's gate, recording the outcome either way.
let private gated
    (ctx: HttpContext)
    (tool: string)
    (detail: (string * string) list)
    (body: unit -> Async<Result<string, string * string>>)
    : Async<string> =
    async {
        let subject = subjectOf ctx

        let! result = async {
            match! CompositionInspectorHandler.readGate ctx with
            | Error reason -> return Error("Refused", reason)
            | Ok() -> return! body ()
        }

        match result with
        | Ok json ->
            do! audit ctx.RequestServices subject tool "served" detail
            return json
        | Error(code, message) ->
            do! audit ctx.RequestServices subject tool (code.ToLowerInvariant()) detail
            return errorJson code message
    }

let executeDescribeComposition (ctx: HttpContext) (argsJson: string) : Async<string> =
    let panel =
        argsOf argsJson
        |> Result.bind (optionalString "panel")
        |> Result.map (Option.defaultValue (InspectorPanel.slug CompositionPanel))

    let detail =
        match panel with
        | Ok slug -> [ "panel", slug ]
        | Error _ -> []

    gated ctx DescribeCompositionTool detail (fun () -> async {
        match panel |> Result.bind panelOf with
        | Error usage -> return Error("InvalidArgument", usage)
        | Ok requested ->
            match! (CompositionInspectorHandler.compositionInspectorApi ctx).ExportPanel requested with
            | Ok json -> return Ok json
            | Error reason when reason = CompositionInspectorHandler.SnapshotMissingMessage ->
                return Error("Unavailable", reason)
            | Error reason -> return Error("Refused", reason)
    })

let executeVerificationReport (ctx: HttpContext) (argsJson: string) : Async<string> =
    let sections = argsOf argsJson |> Result.bind (optionalStrings "sections")

    let detail =
        match sections with
        | Ok [] -> []
        | Ok ids -> [ "sections", String.concat "," ids ]
        | Error _ -> []

    gated ctx VerificationReportTool detail (fun () -> async {
        match sections with
        | Error usage -> return Error("InvalidArgument", usage)
        | Ok ids ->
            match! (DeploymentVerificationReport.deploymentVerificationApi ctx).GetVerificationReport() with
            | Error reason -> return Error("Refused", reason)
            | Ok report ->
                return
                    verificationReportJson report ids
                    |> Result.mapError (fun usage -> "InvalidArgument", usage)
    })

let executeModuleReach (ctx: HttpContext) (_argsJson: string) : Async<string> =
    gated ctx ModuleReachTool [] (fun () -> async {
        return
            match DeploymentVerificationReport.readAllowed ctx with
            | Error reason -> Error("Refused", reason)
            | Ok() -> Ok(moduleReachJson ctx.RequestServices)
    })

/// The three reflection tools, in the platform's `(definition, executor)`
/// registration shape. Appended to a composition by
/// `AIServerApp.withReflectionTools`; registered by `composeAI`.
let tools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list = [
    describeCompositionDefinition, executeDescribeComposition
    verificationReportDefinition, executeVerificationReport
    moduleReachDefinition, executeModuleReach
]

// ─── The offline door ─────────────────────────────────────────────────

/// Render the requested documents for the `--describe` startup mode.
///
/// Ungated, as `--verify-deployment` is: the process holds the
/// deployment's own binary and configuration, which is a strictly larger
/// grant than any document here. The read is still audited, under
/// `StartupModes.CliDescribeActor`.
///
/// One document is emitted as its bytes exactly — the same bytes the
/// corresponding tool returns. Several are emitted as one JSON object keyed
/// by document slug, each value embedded verbatim. The exit code is the
/// verification report's own when the report is among them (non-zero only
/// for a composed section that failed or would not answer), 1 when the
/// composition snapshot is missing, else 0; a usage error is `Error`.
let describe
    (services: IServiceProvider)
    (sections: string list)
    (panel: string option)
    : Async<Result<string * int, string>> =
    async {
        let requested =
            if List.isEmpty sections then
                documents
            else
                List.distinct sections

        match
            requested |> List.filter (fun s -> not (List.contains s documents)),
            panelOf (defaultArg panel (InspectorPanel.slug CompositionPanel))
        with
        | unknown :: _, _ ->
            return Error(sprintf "unknown section '%s'; expected any of: %s" unknown (String.concat ", " documents))
        | [], Error usage -> return Error usage
        | [], Ok inspectorPanel ->
            let render (document: string) : Async<string * int> = async {
                match document with
                | CompositionDocument ->
                    return
                        match
                            CompositionInspectorHandler.panelJson
                                services
                                (CompositionInspectorHandler.resolveSnapshot services)
                                inspectorPanel
                        with
                        | Ok json -> json, 0
                        | Error reason -> errorJson "Unavailable" reason, 1
                | VerificationReportDocument ->
                    let! report = DeploymentVerificationReport.run services StartupModes.CliDescribeActor
                    return CompositionInspectorHandler.canonicalJson report, DeploymentVerification.exitCode report
                | _ -> return moduleReachJson services, 0
            }

            let selected = documents |> List.filter (fun d -> List.contains d requested)

            let! rendered = async {
                let results = ResizeArray<string * (string * int)>()

                for document in selected do
                    let! output = render document
                    results.Add(document, output)

                return List.ofSeq results
            }

            do!
                audit services StartupModes.CliDescribeActor "--describe" "served" [
                    "sections", String.concat "," selected
                    "panel", InspectorPanel.slug inspectorPanel
                ]

            let code = rendered |> List.map (snd >> snd) |> List.fold max 0

            let json =
                match rendered with
                | [ _, (single, _) ] -> single
                | many ->
                    let sb = StringBuilder()
                    sb.Append('{') |> ignore

                    many
                    |> List.iteri (fun i (document, (doc, _)) ->
                        if i > 0 then
                            sb.Append(',') |> ignore

                        sb.Append('"').Append(document).Append("\":").Append(doc) |> ignore)

                    sb.Append('}').ToString()

            return Ok(json, code)
    }

/// The renderer `ServerApp.run` registers for the `--describe` arm.
let describeRenderer: StartupModes.DescribeRenderer =
    StartupModes.DescribeRenderer(describe)