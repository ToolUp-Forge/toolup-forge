// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 577 — the `_platform.reporting` AI tool family (list templates, render a report).
module ToolUp.Reporting.ReportingAITools

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Reporting
open ToolUp.Reporting.IReportTemplateStore
open ToolUp.Reporting.RendererRegistry

// ─── Phase 577 — the `_platform.reporting` AI tool family ────────────
//
// The assistant could READ narratives (`list_narratives` /
// `get_narrative`) but could not PRODUCE a document: "export this as a
// Word report" dead-ended. Two server-resident tools close that:
//
//   * `_platform.reporting.list_report_templates` — the caller's
//     templates, each with its placeholder schema, so the model can fill
//     values correctly.
//   * `_platform.reporting.render_report` — renders one through the SAME
//     `ReportApiHandler` render path a UI Export button calls. There is
//     no second renderer and no second disclosure door: the tool builds
//     the per-caller `IReportApi` the handler factory builds, with the
//     fact-disclosure gate engaged whenever the deployment composed one,
//     so the Phase 564 door sits pre-render exactly as it does for the
//     button. Each Office sub-companion a deployment registers widens the
//     format list with zero change here.
//
// **Why this lives in the companion, not in the AI server package.** A
// companion's AI tools ship with the companion and reach the agent loop
// through `ServerModule.withAITools` — the `_algorithms` and `_facts`
// families are the precedents. The alternative (the AI server package
// referencing this one) would make every AI deployment carry the
// reporting packages, and would still need a runtime signal for "is
// Reporting composed", which does not exist: the handler factory is
// wired by each composition root by hand. Here the gate is structural
// (GP 13): the tools exist exactly when a deployment calls
// `withReportingAITools`, and an app that never composes Reporting
// cannot surface them because it has nothing to call.
//
// **RBAC.** `IReportApi` gates every method with `[<RequiresClaim
// "scope">]` — any authenticated caller at the resolved scope, never an
// anonymous one. Tool executors run on the agent loop's BACKGROUND
// context, where the DI `AccessContext` factory is unreliable, so the
// caller is re-resolved from the middleware items the background context
// copies forward (the `_platform.ai.*` pattern) and the same rule is
// applied: an anonymous subject is refused. The `_platform.reporting`
// source is SDK-reserved, so the per-module tool filter does not hide it;
// a deployment that names `_platform.reporting` in its permission map is
// honoured here too (no `Read` ⇒ refused), as defence in depth.
//
// **Long renders (577.C).** Above the deployment's `LongRenderPolicy`
// the tool does not render in the turn: it applies the disclosure door
// (the same public `ReportApiHandler.applyExportDisclosure` every second
// egress surface calls), enqueues a `Manual` job carrying the already-
// disclosed values, and returns the job handle at once. The job renders
// to blob through the same handler factory, reports Phase 321 progress,
// and tells the user when the file is ready.
//
// **How the user is told.** Every stored render raises a `SystemMessage`
// (the kind the shell's built-in toast centre renders — "Report ready").
// The job path adds a `JobCompleted` carrying the download link as its
// `resultLink` (the platform's typed "a result is available"
// notification, keyed on the real job id); the in-turn path does not,
// because there is no job, and its link is already in the tool result the
// user is reading. It is NOT a
// `ModuleAction`: the client router drops a module action whose module is
// not registered in the client, and `_platform.reporting` is not a client
// module, so a module action would reach nobody.
//
// **Audit (577.D).** Every successful render writes a `ReportRendered`
// event to `IEventStore` under the reserved source `_platform.reporting`
// (the `_facts` / `_platform.lineage` pattern — no core `AuditEvent`
// case), carrying the requesting conversation id. The deployment's own
// `AuditOnRender` callback still runs first, unchanged.

// ─── Names ───────────────────────────────────────────────────────────

/// The SDK-reserved tool source (and `ModuleEvent.SourceModule`) for the
/// family.
[<Literal>]
let SourceModule = "_platform.reporting"

/// Tool name: enumerate templates.
[<Literal>]
let ListTemplatesToolName = "_platform.reporting.list_report_templates"

/// Tool name: render one template.
[<Literal>]
let RenderReportToolName = "_platform.reporting.render_report"

/// `IJobScheduler` handler name for the long-render path.
[<Literal>]
let RenderJobHandlerName = "_platform.reporting.render"

/// `ModuleEvent.EventType` of the per-render audit row.
[<Literal>]
let ReportRenderedEventType = "ReportRendered"

/// Background-context item keys this family reads. They are stamped by
/// the AI server package, which this companion does not reference, so
/// they are spelled here and pinned equal to the stamping side's
/// constants by a test that references both packages.
module ItemsKeys =
    /// `Guid` — the conversation the current turn belongs to
    /// (`AIConsentDispatch.ItemsKeys.ConversationId`).
    [<Literal>]
    let ConversationId = "ToolUp.AI.ConversationId"

    /// `string` — the module the user was on when they asked
    /// (`AICrossModuleAudit.ItemsKeys.ActiveModule`).
    [<Literal>]
    let ActiveModule = "ToolUp.AI.ActiveModule"

// ─── Configuration ───────────────────────────────────────────────────

/// When a render leaves the chat turn for a background job.
///
/// A render's duration cannot be known before it runs, so the threshold
/// is on what CAN be known: the size of the input the renderer will
/// consume (template body + supplied values, a narrative measured as its
/// markdown projection). `DeferredFormats` lets a deployment send a
/// format it knows to be slow (a PDF engine, say) to the job whatever
/// its size.
type LongRenderPolicy = {
    /// Estimated input bytes above which the render is deferred.
    /// `None` never defers on size.
    InputBytesThreshold: int64 option
    /// Formats always deferred.
    DeferredFormats: TemplateFormat list
}

/// Constructors and the predicate for `LongRenderPolicy`.
module LongRenderPolicy =
    /// 256 KiB of input, no always-deferred formats.
    let defaults: LongRenderPolicy = {
        InputBytesThreshold = Some(256L * 1024L)
        DeferredFormats = []
    }

    /// Never defer — every render happens in the turn.
    let never: LongRenderPolicy = {
        InputBytesThreshold = None
        DeferredFormats = []
    }

    /// Does a render of this size and format leave the turn?
    let defers (policy: LongRenderPolicy) (format: TemplateFormat) (estimatedInputBytes: int64) : bool =
        List.contains format policy.DeferredFormats
        || (match policy.InputBytesThreshold with
            | Some threshold -> estimatedInputBytes > threshold
            | None -> false)

/// Everything the tool family needs — the same building blocks a
/// composition root hands `ReportApiHandler.create` for its Export
/// button, so the two paths cannot be configured apart.
type ReportingAIToolDeps = {
    /// The template store the Export button's handler reads.
    Templates: IReportTemplateStore
    /// The renderer registry the Export button's handler routes through.
    Renderers: RendererRegistry
    /// Where a blob-routed render is written (typically `IDataObjectStore`).
    StoreBlob: ReportApiHandler.StoreBlob
    /// The deployment's own per-render audit callback. Runs first on
    /// every render, unchanged; the family's `ReportRendered` event is
    /// written after it.
    AuditOnRender: ReportApiHandler.AuditOnRender
    /// The handler config (MIME types, inline budget). The tool family
    /// overrides only the inline budget, per the two fields below.
    Config: ReportApiConfig
    /// Largest Markdown / Html output returned to the model as text.
    /// Binary formats (Pdf, Docx, Xlsx, Pptx) always go to blob — a chat
    /// turn can carry a link to a Word file, not the file.
    InlineTextBudget: int
    /// The download URL for a stored render — (data-object key,
    /// version). The deployment owns the route that serves its blobs.
    DownloadLink: string -> int -> string
    /// When a render leaves the turn for the background job.
    LongRender: LongRenderPolicy
}

/// Constructors for `ReportingAIToolDeps`.
module ReportingAIToolDeps =
    /// Deps with the SDK defaults for the knobs: 16 KiB of inline text,
    /// `LongRenderPolicy.defaults`, and a no-op deployment audit callback.
    let create
        (templates: IReportTemplateStore)
        (renderers: RendererRegistry)
        (storeBlob: ReportApiHandler.StoreBlob)
        (downloadLink: string -> int -> string)
        : ReportingAIToolDeps =
        {
            Templates = templates
            Renderers = renderers
            StoreBlob = storeBlob
            AuditOnRender = fun _ -> async.Return()
            Config = ReportApiConfig.defaults
            InlineTextBudget = 16 * 1024
            DownloadLink = downloadLink
            LongRender = LongRenderPolicy.defaults
        }

// ─── Serialisation ───────────────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

let private serialise (value: obj) : string =
    JsonSerializer.Serialize(value, jsonOptions)

let private errorPayload (code: string) (message: string) : string =
    serialise {| error = code; message = message |}

// ─── Caller resolution (from the background context's items) ─────────

/// The caller as the background context carries it.
type private Caller = {
    UserId: string
    ScopeId: string
    Anonymous: bool
    Permissions: Map<string, ModulePermission list>
    ConversationId: Guid option
}

let private callerOf (ctx: HttpContext) : Caller =
    let userId =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) when not (String.IsNullOrWhiteSpace id) -> Some id
        | _ -> None

    let scopeId =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as s) -> Some s.ScopeId
        | _ -> None

    let anonymousSubject =
        match ctx.Items.TryGetValue "ToolUp.Subject" with
        | true, (:? Subject as s) ->
            match s with
            | AnonymousSession _ -> true
            | _ -> false
        | _ -> false

    let permissions =
        match ctx.Items.TryGetValue "ToolUp.ModulePermissions" with
        | true, (:? Map<string, ModulePermission list> as perms) -> perms
        | _ -> Map.empty

    let conversationId =
        match ctx.Items.TryGetValue ItemsKeys.ConversationId with
        | true, (:? Guid as id) -> Some id
        | _ -> None

    let resolvedUser = userId |> Option.defaultValue "anonymous"

    {
        UserId = resolvedUser
        ScopeId = scopeId |> Option.defaultValue resolvedUser
        Anonymous = anonymousSubject || resolvedUser = "anonymous"
        Permissions = permissions
        ConversationId = conversationId
    }

/// The `[<RequiresClaim "scope">]` rule `IReportApi` applies, plus an
/// explicit permission-map entry for the family when a deployment wrote
/// one. `Ok ()` admits.
let private authorise (caller: Caller) : Result<unit, string> =
    if caller.Anonymous then
        Error
            "Report templates and rendering are available to signed-in users only. Ask the user to sign in, then try again."
    else
        match caller.Permissions |> Map.tryFind SourceModule with
        | Some grants when
            not (
                grants
                |> List.exists (fun p -> p = ModulePermission.Read || p = ModulePermission.Admin)
            )
            ->
            Error "This deployment does not grant the current user access to report rendering."
        | _ -> Ok()

// ─── Projection helpers ──────────────────────────────────────────────

let private formatName (format: TemplateFormat) : string =
    match format with
    | Markdown -> "markdown"
    | Html -> "html"
    | Pdf -> "pdf"
    | Docx -> "docx"
    | Xlsx -> "xlsx"
    | Pptx -> "pptx"

let private isTextual (format: TemplateFormat) =
    match format with
    | Markdown
    | Html -> true
    | Pdf
    | Docx
    | Xlsx
    | Pptx -> false

let rec private kindProjection (kind: PlaceholderKind) : obj =
    match kind with
    | Text -> box {| kind = "text" |}
    | Number hint -> box {| kind = "number"; formatHint = hint |}
    | Date hint -> box {| kind = "date"; formatHint = hint |}
    | Image mime -> box {| kind = "image"; mimeType = mime |}
    | Table columns ->
        box {|
            kind = "table"
            columns =
                columns
                |> List.map (fun c -> {|
                    key = c.Key
                    displayName = c.DisplayName
                    cell = kindProjection c.Kind
                |})
        |}

let private placeholderProjection (p: PlaceholderSchema) = {|
    key = p.Key
    displayName = p.DisplayName
    required = p.Required
    schema = kindProjection p.Kind
|}

// ─── list_report_templates (577.A) ───────────────────────────────────

let private listTemplatesDefinition: AIToolDefinition = {
    Name = ListTemplatesToolName
    Description =
        "List the report templates the current user can render, each with its id, name, output format (markdown, html, pdf, docx, xlsx, pptx), whether this deployment can render that format, and its placeholder schema: every placeholder's key, whether it is required, and the value kind it takes (text, number, date, table with typed columns, image). Call this before _platform.reporting.render_report so the values you supply match the schema. Scoped to the current user/team."
    Parameters = []
    SourceModule = SourceModule
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readContent
}

let private executeListTemplates (deps: ReportingAIToolDeps) (ctx: HttpContext) (_argsJson: string) : Async<string> = async {
    let caller = callerOf ctx

    match authorise caller with
    | Error reason -> return errorPayload "NotAuthorised" reason
    | Ok() ->
        let! templates = deps.Templates.List caller.ScopeId

        let entries =
            templates
            |> List.sortBy _.Id
            |> List.map (fun t -> {|
                id = t.Id
                name = t.DisplayName
                format = formatName t.Format
                renderable = Result.isOk (deps.Renderers.Route t.Format)
                placeholders = t.Placeholders |> List.map placeholderProjection
            |})

        return serialise {| templates = entries |}
}

// ─── Value parsing (model JSON → typed placeholder values) ───────────

let private jsonScalarText (el: JsonElement) : string option =
    match el.ValueKind with
    | JsonValueKind.String -> Some(el.GetString())
    | JsonValueKind.Number
    | JsonValueKind.True
    | JsonValueKind.False -> Some(el.GetRawText())
    | _ -> None

let rec private parseValue (key: string) (kind: PlaceholderKind) (el: JsonElement) : Result<PlaceholderValue, string> =
    match kind with
    | Text ->
        match jsonScalarText el with
        | Some s -> Ok(TextValue s)
        | None -> Error $"'{key}' takes text"
    | Number _ ->
        match el.ValueKind with
        | JsonValueKind.Number -> Ok(NumberValue(el.GetDouble()))
        | JsonValueKind.String ->
            match
                Double.TryParse(
                    el.GetString(),
                    Globalization.NumberStyles.Float,
                    Globalization.CultureInfo.InvariantCulture
                )
            with
            | true, d -> Ok(NumberValue d)
            | _ -> Error $"'{key}' takes a number"
        | _ -> Error $"'{key}' takes a number"
    | Date _ ->
        match el.ValueKind with
        | JsonValueKind.String ->
            match
                DateTimeOffset.TryParse(
                    el.GetString(),
                    Globalization.CultureInfo.InvariantCulture,
                    Globalization.DateTimeStyles.AssumeUniversal
                )
            with
            | true, d -> Ok(DateValue d)
            | _ -> Error $"'{key}' takes an ISO-8601 date"
        | _ -> Error $"'{key}' takes an ISO-8601 date"
    | Image _ -> Error $"'{key}' is an image placeholder; images cannot be supplied from the assistant"
    | Table columns ->
        match el.ValueKind with
        | JsonValueKind.Array ->
            let rows =
                el.EnumerateArray()
                |> Seq.toList
                |> List.mapi (fun i row ->
                    match row.ValueKind with
                    | JsonValueKind.Object ->
                        columns
                        |> List.choose (fun c ->
                            match row.TryGetProperty c.Key with
                            | true, cell when cell.ValueKind <> JsonValueKind.Null ->
                                Some(
                                    match c.Kind with
                                    | Table _ -> Error $"'{key}' row {i}: nested tables are not supported"
                                    | cellKind ->
                                        parseValue $"{key}[{i}].{c.Key}" cellKind cell
                                        |> Result.map (fun v -> c.Key, v)
                                )
                            | _ -> None)
                        |> List.fold
                            (fun acc cell ->
                                match acc, cell with
                                | Ok cells, Ok c -> Ok(c :: cells)
                                | Error e, _ -> Error e
                                | _, Error e -> Error e)
                            (Ok [])
                        |> Result.map Map.ofList
                    | _ -> Error $"'{key}' row {i} must be an object keyed by column")

            match
                rows
                |> List.tryPick (fun r ->
                    match r with
                    | Error e -> Some e
                    | Ok _ -> None)
            with
            | Some e -> Error e
            | None ->
                Ok(
                    TableValue(
                        rows
                        |> List.choose (fun r ->
                            match r with
                            | Ok m -> Some m
                            | Error _ -> None)
                    )
                )
        | _ -> Error $"'{key}' takes an array of row objects"

/// Estimated input the renderer consumes — the long-render predicate's
/// measure. A narrative counts as its markdown projection.
let rec private estimateValueBytes (value: PlaceholderValue) : int64 =
    match value with
    | TextValue s -> int64 (Encoding.UTF8.GetByteCount s)
    | NumberValue _
    | DateValue _ -> 16L
    | ImageValue(bytes, _) -> int64 bytes.Length
    | TableValue rows ->
        rows
        |> List.sumBy (fun row -> row |> Map.toList |> List.sumBy (snd >> estimateValueBytes))
    | NarrativeValue document -> int64 (Encoding.UTF8.GetByteCount(NarrativeMarkdown.render document))

/// Estimated renderer input for one render.
let estimateInputBytes (template: ReportTemplate) (values: Map<string, PlaceholderValue>) : int64 =
    int64 template.Body.Length
    + (values |> Map.toList |> List.sumBy (snd >> estimateValueBytes))

// ─── render_report (577.B / 577.C) ───────────────────────────────────

/// The `ReportRendered` audit payload (JSON in `ModuleEvent.Payload`).
/// Identifiers only — never the rendered content.
type ReportRenderedEvent = {
    /// The rendered template.
    TemplateId: string
    /// Output format, lowercase (`markdown`, `docx`, …).
    Format: string
    /// Rendered size in bytes.
    OutputSize: int
    /// `true` when returned into the turn; `false` when stored as a blob.
    Inline: bool
    /// The user the render was performed for.
    RequestedBy: string
    /// The conversation whose turn asked for the render; `None` when the
    /// tool ran outside a conversation.
    ConversationId: Guid option
    /// Set when the render ran on the long-render job.
    JobId: Guid option
}

/// The long-render job payload. Values have ALREADY passed the
/// disclosure door — the job renders them without a gate so the door
/// runs exactly once per render.
type RenderJobPayload = {
    /// The template to render.
    TemplateId: string
    /// The requester's storage scope.
    ScopeId: string
    /// The requester — the render's principal and the notified user.
    UserId: string
    /// The conversation that asked, for the audit row.
    ConversationId: Guid option
    /// The typed placeholder values, already disclosure-resolved.
    Values: Map<string, PlaceholderValue>
}

/// Encode / decode for `RenderJobPayload` (the job's persisted payload).
module RenderJobPayload =
    /// Serialise with the platform's canonical converter set.
    let encode (payload: RenderJobPayload) : string = serialise payload

    /// Parse a persisted payload; `None` for anything unreadable, never a throw.
    let decode (json: string) : RenderJobPayload option =
        try
            if String.IsNullOrWhiteSpace json then
                None
            else
                Some(JsonSerializer.Deserialize<RenderJobPayload>(json, jsonOptions))
        with _ ->
            None

let private writeRenderedEvent
    (events: IEventStore option)
    (requestedBy: string)
    (conversationId: Guid option)
    (jobId: Guid option)
    (audit: ReportApiHandler.ReportRenderedAudit)
    : Async<unit> =
    async {
        match events with
        | None -> ()
        | Some store ->
            let payload: ReportRenderedEvent = {
                TemplateId = audit.TemplateId
                Format = formatName audit.Format
                OutputSize = audit.OutputSize
                Inline = audit.Inline
                RequestedBy = requestedBy
                ConversationId = conversationId
                JobId = jobId
            }

            try
                do!
                    store.Write {
                        Id = Guid.NewGuid()
                        OccurredAt = DateTime.UtcNow
                        ScopeId = audit.ScopeId
                        SourceModule = SourceModule
                        EventType = ReportRenderedEventType
                        Payload = serialise payload
                    }
            with _ ->
                // Best-effort, like every tool-side audit row: the
                // deployment's own `AuditOnRender` already ran, and a
                // failed audit write must not turn a delivered file into
                // an error the model reports as "export failed".
                ()
    }

/// Chain the deployment's callback, then the family's event.
let private auditChain
    (deps: ReportingAIToolDeps)
    (events: IEventStore option)
    (requestedBy: string)
    (conversationId: Guid option)
    (jobId: Guid option)
    : ReportApiHandler.AuditOnRender =
    fun audit -> async {
        do! deps.AuditOnRender audit
        do! writeRenderedEvent events requestedBy conversationId jobId audit
    }

let private serviceOf<'T> (services: IServiceProvider) : 'T option =
    match services.GetService(typeof<'T>) with
    | :? 'T as s -> Some s
    | _ -> None

/// Tell the user a stored render is ready: the toast the shell renders,
/// plus — for a job — the typed result-available notification carrying
/// the link, keyed on the job.
let private announceReady
    (channel: INotificationChannel option)
    (userId: string)
    (job: (Guid * string) option)
    (templateName: string)
    : Async<unit> =
    async {
        match channel with
        | Some ch when userId <> "anonymous" ->
            try
                do!
                    ch.Publish(
                        userId,
                        Notification.SystemMessage(SystemMessageLevel.Info, $"Report ready: {templateName}")
                    )

                match job with
                | Some(jobId, link) -> do! ch.Publish(userId, Notification.JobCompleted(jobId, "Succeeded", Some link))
                | None -> ()
            with _ ->
                ()
        | _ -> ()
    }

let private announceFailed
    (channel: INotificationChannel option)
    (userId: string)
    (templateName: string)
    (reason: string)
    =
    async {
        match channel with
        | Some ch when userId <> "anonymous" ->
            try
                do!
                    ch.Publish(
                        userId,
                        Notification.SystemMessage(
                            SystemMessageLevel.Warning,
                            $"Report export failed: {templateName} — {reason}"
                        )
                    )
            with _ ->
                ()
        | _ -> ()
    }

let private rpcErrorText (error: RenderRpcError) : string =
    match error with
    | TemplateNotFound id -> $"No template '{id}' is visible at this scope."
    | NotAuthorised reason -> reason
    | Renderer e -> RenderError.toMessage e

let private renderReportDefinition: AIToolDefinition = {
    Name = RenderReportToolName
    Description =
        "Render a report template to a document the user can download — the same export the page's Export button performs. Pass template_id (from _platform.reporting.list_report_templates) and values: an object keyed by placeholder key, each value matching that placeholder's kind (text as a string, number as a number, date as an ISO-8601 string, table as an array of objects keyed by column). To fill a placeholder with a narrative, name it in narrative_placeholder: the narrative is the one given by narrative_id (see list_narratives), or — when narrative_id is omitted — the latest narrative published by the page the user is on. Missing required placeholders return an error naming them; supply them and retry. Small Markdown/HTML results come back as text; every other result is stored and returned as a download link, and the user is notified. Large renders run in the background: the result is a job id and the user is notified when the file is ready."
    Parameters = [
        {
            Name = "template_id"
            Type = "string"
            Description = "Template id from _platform.reporting.list_report_templates."
            Required = true
            Default = None
        }
        {
            Name = "values"
            Type = "object"
            Description =
                "Placeholder values keyed by placeholder key. Omit a placeholder you are filling from a narrative."
            Required = false
            Default = None
        }
        {
            Name = "narrative_placeholder"
            Type = "string"
            Description = "Key of a text placeholder to fill with a narrative (the on-screen one, or narrative_id's)."
            Required = false
            Default = None
        }
        {
            Name = "narrative_id"
            Type = "string"
            Description = "NarrativeId (GUID) from list_narratives. Omit to use the on-screen narrative."
            Required = false
            Default = None
        }
    ]
    SourceModule = SourceModule
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.declare [ ReadContent; WriteState SourceModule ]
}

type private RenderArgs = {
    TemplateId: string
    Values: JsonElement option
    NarrativePlaceholder: string option
    NarrativeId: string option
}

let private parseArgs (argsJson: string) : Result<RenderArgs, string> =
    try
        use doc =
            JsonDocument.Parse(
                if String.IsNullOrWhiteSpace argsJson then
                    "{}"
                else
                    argsJson
            )

        let root = doc.RootElement

        let str name =
            match root.TryGetProperty(name: string) with
            | true, v when
                v.ValueKind = JsonValueKind.String
                && not (String.IsNullOrWhiteSpace(v.GetString()))
                ->
                Some(v.GetString())
            | _ -> None

        match str "template_id" with
        | None ->
            Error "Required argument 'template_id' is missing. Call _platform.reporting.list_report_templates first."
        | Some templateId ->
            let values =
                match root.TryGetProperty "values" with
                | true, v when v.ValueKind = JsonValueKind.Object -> Some(v.Clone())
                | _ -> None

            Ok {
                TemplateId = templateId
                Values = values
                NarrativePlaceholder = str "narrative_placeholder"
                NarrativeId = str "narrative_id"
            }
    with ex ->
        Error $"Arguments are not valid JSON: {ex.Message}"

/// Resolve the narrative the call names, or the active page's newest.
let private resolveNarrative
    (ctx: HttpContext)
    (narrativeId: string option)
    : Async<Result<NarrativeDocument, string>> =
    async {
        match narrativeId with
        | Some idText ->
            match Guid.TryParse idText with
            | false, _ -> return Error $"narrative_id '{idText}' is not a valid GUID."
            | true, id ->
                let! entry = NarrativePublisher.get ctx id

                match entry with
                | Some e -> return Ok e.Document
                | None ->
                    return
                        Error $"No narrative '{idText}' is visible to this scope. Call list_narratives for valid ids."
        | None ->
            let activeModule =
                match ctx.Items.TryGetValue ItemsKeys.ActiveModule with
                | true, (:? string as m) when not (String.IsNullOrWhiteSpace m) -> Some m
                | _ -> None

            match activeModule with
            | None ->
                return
                    Error
                        "The user is not on a page that publishes a narrative. Pass narrative_id (see list_narratives)."
            | Some moduleId ->
                let! recent = NarrativePublisher.list ctx 50

                match
                    recent
                    |> List.filter (fun e -> e.ModuleId = moduleId)
                    |> List.sortByDescending _.PublishedAt
                    |> List.tryHead
                with
                | None ->
                    return
                        Error
                            $"The current page ({moduleId}) has not published a narrative. Pass narrative_id (see list_narratives)."
                | Some info ->
                    let! entry = NarrativePublisher.get ctx info.Id

                    match entry with
                    | Some e -> return Ok e.Document
                    | None ->
                        return
                            Error
                                "The on-screen narrative is no longer available. Pass narrative_id (see list_narratives)."
    }

/// Assemble the typed values: parse the supplied ones against the
/// schema, add the narrative, and name every required placeholder still
/// missing.
let private assembleValues
    (ctx: HttpContext)
    (template: ReportTemplate)
    (args: RenderArgs)
    : Async<Result<Map<string, PlaceholderValue>, string>> =
    async {
        let schema = template.Placeholders |> List.map (fun p -> p.Key, p) |> Map.ofList

        let supplied =
            match args.Values with
            | None -> Ok []
            | Some obj ->
                obj.EnumerateObject()
                |> Seq.toList
                |> List.filter (fun p -> p.Value.ValueKind <> JsonValueKind.Null)
                |> List.map (fun p ->
                    match schema.TryFind p.Name with
                    | None -> Error $"'{p.Name}' is not a placeholder of template '{template.Id}'"
                    | Some s -> parseValue p.Name s.Kind p.Value |> Result.map (fun v -> p.Name, v))
                |> List.fold
                    (fun acc r ->
                        match acc, r with
                        | Ok xs, Ok x -> Ok(x :: xs)
                        | Error e, _ -> Error e
                        | _, Error e -> Error e)
                    (Ok [])

        match supplied with
        | Error e -> return Error(errorPayload "InvalidValues" e)
        | Ok supplied ->
            let values = Map.ofList supplied

            let! withNarrative = async {
                match args.NarrativePlaceholder with
                | None when args.NarrativeId.IsSome ->
                    return
                        Error(
                            errorPayload
                                "InvalidArguments"
                                "narrative_id was given without narrative_placeholder — name the text placeholder the narrative fills."
                        )
                | None -> return Ok values
                | Some key ->
                    match schema.TryFind key with
                    | Some { Kind = Text } ->
                        let! narrative = resolveNarrative ctx args.NarrativeId

                        match narrative with
                        | Ok document -> return Ok(values |> Map.add key (NarrativeValue document))
                        | Error reason -> return Error(errorPayload "NarrativeUnavailable" reason)
                    | Some _ ->
                        return
                            Error(
                                errorPayload
                                    "InvalidArguments"
                                    $"narrative_placeholder '{key}' is not a text placeholder."
                            )
                    | None ->
                        return
                            Error(
                                errorPayload
                                    "InvalidArguments"
                                    $"narrative_placeholder '{key}' is not a placeholder of template '{template.Id}'."
                            )
            }

            match withNarrative with
            | Error e -> return Error e
            | Ok values ->
                let missing =
                    template.Placeholders
                    |> List.filter (fun p -> p.Required && not (values.ContainsKey p.Key))
                    |> List.map _.Key

                if List.isEmpty missing then
                    return Ok values
                else
                    return
                        Error(
                            serialise {|
                                error = "MissingPlaceholders"
                                message =
                                    "Required placeholders were not supplied. Supply them in values (or via narrative_placeholder) and call again."
                                missing = missing
                                placeholders = template.Placeholders |> List.map placeholderProjection
                            |}
                        )
    }

/// The per-caller `IReportApi` — the handler factory the Export button
/// uses, with the disclosure gate engaged when one is composed.
let private apiFor
    (deps: ReportingAIToolDeps)
    (gate: IFactDisclosureGate option)
    (principal: string)
    (config: ReportApiConfig)
    (audit: ReportApiHandler.AuditOnRender)
    (scopeId: string)
    : IReportApi =
    match gate with
    | Some g ->
        ReportApiHandler.createWithDisclosureGate
            g
            principal
            deps.Templates
            deps.Renderers
            deps.StoreBlob
            audit
            config
            scopeId
    | None -> ReportApiHandler.create principal deps.Templates deps.Renderers deps.StoreBlob audit config scopeId

/// The in-turn render config: text formats inline up to the budget,
/// binary formats always to blob.
let private inTurnConfig (deps: ReportingAIToolDeps) (format: TemplateFormat) : ReportApiConfig = {
    deps.Config with
        InlineByteBudget = if isTextual format then deps.InlineTextBudget else -1
}

/// The job's render config: always to blob — the result is announced
/// with a link, never returned into a turn that has already ended.
let private jobConfig (deps: ReportingAIToolDeps) : ReportApiConfig = {
    deps.Config with
        InlineByteBudget = -1
}

let private renderInTurn
    (deps: ReportingAIToolDeps)
    (ctx: HttpContext)
    (caller: Caller)
    (template: ReportTemplate)
    (values: Map<string, PlaceholderValue>)
    (deferral: string option)
    : Async<string> =
    async {
        let services = ctx.RequestServices
        let gate = serviceOf<IFactDisclosureGate> services
        let events = serviceOf<IEventStore> services

        let audit = auditChain deps events caller.UserId caller.ConversationId None

        let api =
            apiFor deps gate caller.UserId (inTurnConfig deps template.Format) audit caller.ScopeId

        let! outcome = api.Render(template.Id, values)

        match outcome with
        | Error error ->
            let code =
                match error with
                | Renderer(MissingRequiredPlaceholder _) -> "MissingPlaceholders"
                | Renderer(PlaceholderTypeMismatch _) -> "InvalidValues"
                | TemplateNotFound _ -> "TemplateNotFound"
                | NotAuthorised _ -> "NotAuthorised"
                | Renderer _ -> "RenderFailed"

            return errorPayload code (rpcErrorText error)
        | Ok(RenderedInline(bytes, mime)) ->
            return
                serialise {|
                    status = "rendered"
                    delivery = "inline"
                    templateId = template.Id
                    format = formatName template.Format
                    mimeType = mime
                    content = Encoding.UTF8.GetString bytes
                    note = deferral
                |}
        | Ok(RenderedToBlob(key, version, mime)) ->
            let link = deps.DownloadLink key version
            let channel = serviceOf<INotificationChannel> services
            do! announceReady channel caller.UserId None template.DisplayName

            return
                serialise {|
                    status = "rendered"
                    delivery = "download"
                    templateId = template.Id
                    format = formatName template.Format
                    mimeType = mime
                    dataObjectKey = key
                    version = version
                    downloadLink = link
                    note = deferral
                |}
    }

/// Apply the disclosure door to every narrative value — the job renders
/// what this returns, ungated, so the door runs once.
let private discloseForJob
    (gate: IFactDisclosureGate option)
    (principal: string)
    (scopeId: string)
    (values: Map<string, PlaceholderValue>)
    : Async<Map<string, PlaceholderValue>> =
    async {
        match gate with
        | None -> return values
        | Some g ->
            let! resolved =
                values
                |> Map.toList
                |> List.map (fun (key, value) -> async {
                    match value with
                    | NarrativeValue document ->
                        let! disclosed = ReportApiHandler.applyExportDisclosure g principal scopeId document
                        return key, NarrativeValue disclosed
                    | other -> return key, other
                })
                |> Async.Sequential

            return Map.ofArray resolved
    }

let private enqueue
    (scheduler: IJobScheduler)
    (ctx: HttpContext)
    (caller: Caller)
    (template: ReportTemplate)
    (values: Map<string, PlaceholderValue>)
    : Async<string> =
    async {
        let gate = serviceOf<IFactDisclosureGate> ctx.RequestServices
        let! disclosed = discloseForJob gate caller.UserId caller.ScopeId values

        let registration: JobRegistration = {
            ScopeId = caller.ScopeId
            Handler = RenderJobHandlerName
            Payload =
                RenderJobPayload.encode {
                    TemplateId = template.Id
                    ScopeId = caller.ScopeId
                    UserId = caller.UserId
                    ConversationId = caller.ConversationId
                    Values = disclosed
                }
            Trigger = Manual
            Idempotency = None
            RetryPolicy = JobRetryPolicy.defaults
            ShardKey = None
            Precision = Minute
            CreatedBy = caller.UserId
            Tags = Map.ofList [ "template-id", template.Id ]
        }

        match! scheduler.Schedule registration with
        | Error err -> return errorPayload "RenderQueueFailed" $"The background render could not be queued: %A{err}"
        | Ok jobId ->
            // A `Manual` job runs on `TriggerOnce`, not on a tick.
            match! scheduler.TriggerOnce(caller.ScopeId, jobId, caller.UserId) with
            | Error reason ->
                return errorPayload "RenderQueueFailed" $"The background render could not be started: {reason}"
            | Ok() ->
                return
                    serialise {|
                        status = "queued"
                        jobId = jobId
                        templateId = template.Id
                        format = formatName template.Format
                        message =
                            "This report is large, so it is rendering in the background. The user will be notified with a download link when it is ready."
                    |}
    }

let private executeRenderReport (deps: ReportingAIToolDeps) (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let caller = callerOf ctx

    match authorise caller with
    | Error reason -> return errorPayload "NotAuthorised" reason
    | Ok() ->
        match parseArgs argsJson with
        | Error reason -> return errorPayload "InvalidArguments" reason
        | Ok args ->
            let! templateOpt = deps.Templates.Get(caller.ScopeId, args.TemplateId)

            match templateOpt with
            | None ->
                return
                    errorPayload
                        "TemplateNotFound"
                        $"No template '{args.TemplateId}' is visible at this scope. Call _platform.reporting.list_report_templates for valid ids."
            | Some template ->
                let! assembled = assembleValues ctx template args

                match assembled with
                | Error payload -> return payload
                | Ok values ->
                    let estimate = estimateInputBytes template values

                    if LongRenderPolicy.defers deps.LongRender template.Format estimate then
                        match serviceOf<IJobScheduler> ctx.RequestServices with
                        | Some scheduler -> return! enqueue scheduler ctx caller template values
                        | None ->
                            // GP 13 — no scheduler composed, no background
                            // path: the render still happens, in the turn.
                            return!
                                renderInTurn
                                    deps
                                    ctx
                                    caller
                                    template
                                    values
                                    (Some
                                        "Rendered in the turn: this deployment has no background job scheduler for large renders.")
                    else
                        return! renderInTurn deps ctx caller template values None
}

// ─── The long-render job handler (577.C) ─────────────────────────────

/// The job handler for `RenderJobHandlerName`. Resolves its services
/// from `services` on every run (nothing captured beyond the deps), so
/// it stays stateless between invocations (GP 12 rule 4).
///
/// Every failure is PERMANENT: a render is deterministic over its
/// payload, so a retry re-runs the same failure and delays the user's
/// failure notice by the whole backoff.
let renderJobHandler (deps: ReportingAIToolDeps) (services: IServiceProvider) : IJobHandler =
    { new IJobHandler with
        member _.Execute(jobCtx: JobContext) = async {
            match RenderJobPayload.decode jobCtx.Payload with
            | None -> return PermanentFailure "unreadable report render payload"
            | Some payload ->
                let channel = serviceOf<INotificationChannel> services
                let events = serviceOf<IEventStore> services

                do! jobCtx.Progress.Report(ProgressCheckpoint.create (Some 0.1) "Rendering report")

                let! templateOpt = deps.Templates.Get(payload.ScopeId, payload.TemplateId)

                let templateName =
                    templateOpt
                    |> Option.map _.DisplayName
                    |> Option.defaultValue payload.TemplateId

                let audit =
                    auditChain deps events payload.UserId payload.ConversationId (Some jobCtx.JobId)

                // No gate: the executor applied the disclosure door before
                // enqueueing, and these are the values it returned.
                let api =
                    ReportApiHandler.create
                        payload.UserId
                        deps.Templates
                        deps.Renderers
                        deps.StoreBlob
                        audit
                        (jobConfig deps)
                        payload.ScopeId

                let! outcome = api.Render(payload.TemplateId, payload.Values)

                match outcome with
                | Ok(RenderedToBlob(key, version, _)) ->
                    let link = deps.DownloadLink key version

                    do!
                        jobCtx.Progress.Report(
                            {
                                ProgressCheckpoint.create (Some 1.0) "Report ready" with
                                    Durable = true
                            }
                        )

                    do! announceReady channel payload.UserId (Some(jobCtx.JobId, link)) templateName
                    return Success
                | Ok(RenderedInline _) ->
                    // Unreachable with `jobConfig`'s budget; refused
                    // rather than silently dropping the bytes.
                    let reason = "the render was returned inline, so there is no file to link"
                    do! announceFailed channel payload.UserId templateName reason
                    return PermanentFailure reason
                | Error error ->
                    let reason = rpcErrorText error
                    do! announceFailed channel payload.UserId templateName reason
                    return PermanentFailure reason
        }
    }

// ─── Composition ─────────────────────────────────────────────────────

/// The two tools, as `ServerModule.withAITools` takes them.
let tools (deps: ReportingAIToolDeps) : (AIToolDefinition * (HttpContext -> string -> Async<string>)) list = [
    listTemplatesDefinition, executeListTemplates deps
    renderReportDefinition, executeRenderReport deps
]

/// Compose the `_platform.reporting` tool family onto a `ServerApp`:
/// the two tools on a `_platform.reporting` module (aggregated by the AI
/// composition like any module's tools), and the long-render job handler
/// registered against the scheduler at startup. A deployment that never
/// calls this has no reporting tools (GP 13); one without a scheduler
/// gets the tools, renders every report in the turn, and the startup
/// diagnostic names the unregistered handler.
let withReportingAITools (deps: ReportingAIToolDeps) (app: ServerApp) : ServerApp =
    let toolModule =
        ServerModule.create SourceModule |> ServerModule.withAITools (tools deps)

    let register (s: IServiceCollection) =
        s.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                DeferredScheduledJobDeclaration.hostedService
                    "Report rendering (AI tools)"
                    [
                        DeferredScheduledJobDeclaration.ofHandler RenderJobHandlerName Manual (renderJobHandler deps)
                    ]
                    sp)
        )

    let withJobs = {
        app with
            Extensions = {
                app.Extensions with
                    ServiceConfig =
                        match app.Extensions.ServiceConfig with
                        | None -> Some register
                        | Some baseFn -> Some(fun s -> register (baseFn s))
            }
    }

    ServerApp.addModule toolModule withJobs