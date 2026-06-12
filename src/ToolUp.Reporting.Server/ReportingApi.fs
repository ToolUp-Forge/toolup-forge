namespace ToolUp.Reporting

open ToolUp.Platform // 0.5.0 — forge-native auth attributes

// ─── Phase 23 — ReportApi Fable.Remoting contract ────────────────────
//
// Typed RPC the client (Reporting admin UI / module-side render
// callers) consumes. Returns a `RenderOutcome` that distinguishes
// "rendered inline" (small-byte-budget responses) from "stored as a
// blob" (large outputs that the deployment routes through
// IDataObjectStore for download via a separate URL).

/// Outcome of a render request. Inline responses include the bytes
/// directly; blob responses carry the IDataObjectStore key the
/// renderer wrote to.
type RenderOutcome =
    /// Rendered bytes returned directly. Bound by the API's
    /// inline-byte-budget (default 256 KiB; deployments override
    /// via ReportApiConfig).
    | RenderedInline of bytes: byte[] * mimeType: string
    /// Rendered bytes stored as a versioned blob; client fetches
    /// via the returned key + version.
    | RenderedToBlob of dataObjectKey: string * version: int * mimeType: string

/// Render-call failure surface. `Renderer` errors carry the typed
/// `RenderError`; `NotAuthorised` separates auth refusals from
/// renderer issues.
type RenderRpcError =
    | TemplateNotFound of TemplateId
    | NotAuthorised of reason: string
    | Renderer of RenderError

/// Fable.Remoting contract surface — every method returns
/// `Async<Result<_, _>>` per the SDK convention. The handler is
/// scope-resolved per request; callers don't pass scopeId.
type IReportApi = {
    // `ReportApiHandler.create` itself applies no role/claim gate —
    // the scopeId is resolved caller-side and scope isolation is the
    // gating layer (the "Owner / Admin gated" doc lines below
    // describe the intended deployment wiring, not an in-handler
    // enforcement). `AllowAnonymous` documents today's behaviour.
    /// List every template the caller can see at the resolved
    /// scope. Owner / Admin see CRUD-managed templates; non-admins
    /// see render-allowed templates only.
    [<AllowAnonymous>]
    ListTemplates: unit -> Async<ReportTemplate list>
    /// Save (create or update) a template. Owner / Admin gated.
    [<AllowAnonymous>]
    SaveTemplate: ReportTemplate -> Async<Result<ReportTemplate, string>>
    /// Delete a template. Owner / Admin gated.
    [<AllowAnonymous>]
    DeleteTemplate: TemplateId -> Async<Result<unit, string>>
    /// Render the named template against the supplied placeholder
    /// values. Inline-vs-blob routing decided by the handler based
    /// on output size + ReportApiConfig.
    [<AllowAnonymous>]
    Render: TemplateId * Map<string, PlaceholderValue> -> Async<Result<RenderOutcome, RenderRpcError>>
}

/// Per-deployment knobs surfaced by the handler.
type ReportApiConfig = {
    /// Bytes above which a render result is stored in
    /// IDataObjectStore rather than returned inline.
    InlineByteBudget: int
    /// MIME type the renderer should advertise per format. Defaults
    /// fill in standard types when a format is missing.
    MimeTypes: Map<TemplateFormat, string>
}

module ReportApiConfig =
    let defaultMimeTypes =
        [
            Markdown, "text/markdown; charset=utf-8"
            Html, "text/html; charset=utf-8"
            Pdf, "application/pdf"
            Docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            Xlsx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            Pptx, "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        ]
        |> Map.ofList

    let defaults = {
        InlineByteBudget = 256 * 1024 // 256 KiB
        MimeTypes = defaultMimeTypes
    }

    let mimeFor (format: TemplateFormat) (config: ReportApiConfig) =
        config.MimeTypes
        |> Map.tryFind format
        |> Option.orElseWith (fun () -> defaultMimeTypes |> Map.tryFind format)
        |> Option.defaultValue "application/octet-stream"