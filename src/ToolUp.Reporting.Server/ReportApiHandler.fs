module ToolUp.Reporting.ReportApiHandler

open ToolUp.Reporting
open ToolUp.Reporting.IReportTemplateStore
open ToolUp.Reporting.RendererRegistry

// ─── Report API handler factory ──────────────────────────────────────
//
// Builds an `IReportApi` instance scoped to a particular caller
// (scopeId resolved upstream). The returned record satisfies the
// Fable.Remoting contract — consumers wire it via the standard
// `ServerApp.withFableRemotingApi` shape (consumer-side wiring not
// shown here; the handler factory is the SDK's surface).
//
// Render routing:
//   - Resolve template by id → if missing, TemplateNotFound
//   - Resolve renderer by template format → if missing,
//     Renderer (NoRendererForFormat ...)
//   - Run renderer → on success, decide inline-vs-blob by byte budget
//   - Inline: return RenderedInline (bytes, mime)
//   - Blob: write to IDataObjectStore (caller-supplied), return key
//
// Audit emission:
//   - Every successful render emits a `ReportRendered` audit event
//     with template id + scope + caller + format + output-size +
//     inline/blob marker. Audit emission is the deployment's
//     responsibility — this handler accepts an `auditOnRender`
//     callback so the audit-sink wiring stays caller-side.

/// Per-render audit payload. The handler hands this to the
/// caller-supplied audit callback after a successful render — the
/// callback wires it to its IAuditLog substrate.
type ReportRenderedAudit = {
    TemplateId: TemplateId
    ScopeId: string
    Format: TemplateFormat
    OutputSize: int
    Inline: bool
}

/// Side-effect callback invoked on every successful render.
type AuditOnRender = ReportRenderedAudit -> Async<unit>

/// Side-effect callback that persists rendered bytes to whatever
/// blob substrate the deployment runs (typically `IDataObjectStore`).
/// Args: scopeId, bytes, mimeType. Returns the storage key + version
/// on success.
type StoreBlob = string -> byte[] -> string -> Async<Result<string * int, string>>

/// Build a per-scope `IReportApi` from the supplied registry,
/// template store, blob writer, audit callback, and config.
let create
    (templateStore: IReportTemplateStore)
    (registry: RendererRegistry)
    (storeBlob: StoreBlob)
    (auditOnRender: AuditOnRender)
    (config: ReportApiConfig)
    (scopeId: string)
    : IReportApi =
    {
        ListTemplates = fun () -> templateStore.List scopeId

        SaveTemplate =
            fun template -> async {
                let! result = templateStore.Save(scopeId, template)
                return result
            }

        DeleteTemplate =
            fun id -> async {
                let! result = templateStore.Delete(scopeId, id)
                return result
            }

        Render =
            fun (id, values) -> async {
                let! templateOpt = templateStore.Get(scopeId, id)

                match templateOpt with
                | None -> return Result.Error(TemplateNotFound id)
                | Some template ->
                    match registry.TryResolve template.Format with
                    | None -> return Result.Error(Renderer(NoRendererForFormat template.Format))
                    | Some renderer ->
                        let! renderResult = renderer.Render(template, values)

                        match renderResult with
                        | Result.Error e -> return Result.Error(Renderer e)
                        | Result.Ok bytes ->
                            let mime = ReportApiConfig.mimeFor template.Format config
                            let useInline = bytes.Length <= config.InlineByteBudget

                            do!
                                auditOnRender {
                                    TemplateId = id
                                    ScopeId = scopeId
                                    Format = template.Format
                                    OutputSize = bytes.Length
                                    Inline = useInline
                                }

                            if useInline then
                                return Result.Ok(RenderedInline(bytes, mime))
                            else
                                let! blobResult = storeBlob scopeId bytes mime

                                match blobResult with
                                | Result.Ok(key, version) -> return Result.Ok(RenderedToBlob(key, version, mime))
                                | Result.Error e -> return Result.Error(Renderer(RendererFailure(renderer.Name, e)))
            }
    }