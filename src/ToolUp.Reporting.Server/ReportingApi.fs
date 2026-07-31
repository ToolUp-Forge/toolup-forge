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
///
/// **Phase 619 — secure by default. No method is anonymous-reachable.**
/// Every method carries `[<RequiresClaim "scope">]`: the forge-
/// conventional gate for a scope-owned surface that is neither role-
/// gated nor tenant-only, but *never* anonymous. Against the default
/// `ForgeAuthContext` resolver `HasClaim("scope", None)` resolves to
/// exactly `not isAnonymous`, so the classifier refuses an
/// unauthenticated caller on all four methods before dispatch. The same
/// gate `IConfigApi` / `TeamApi` / `ITeamInviteApi` / `MaintenanceApi` /
/// `IUsageQueryApi` already apply to their scope-owned surfaces.
///
/// **What the gate does NOT do — read this before relying on it.**
/// `[<RequiresClaim "scope">]` is not a per-scope binding. It admits any
/// authenticated subject, including a share-token `ClaimBearer`. That
/// the caller may only reach *their own* scope is enforced structurally
/// by the `StorageScope` resolver upstream (GP 4), which is what this
/// contract's pre-619 comment was gesturing at — except that nothing
/// enforced it against an anonymous caller, because there was no gate at
/// all. There is now.
///
/// **Why not `[<RequiresRole …>]`.** A report template is scope-owned,
/// not platform-owned, so `"PlatformAdmin"` would break per-team
/// template management in every deployment. `"Owner"` / `"Admin"` are
/// worse: the first-party providers leave `AuthenticatedUser.Roles`
/// empty, so any role string other than the server-resolved
/// `"PlatformAdmin"` is a Phase 132 *dead gate* — it denies every
/// caller, admins included. The Owner/Admin intent the pre-619 comments
/// asserted lives in `ReportApiHandler.withManagementGate` instead,
/// where the deployment supplies the predicate that its own role model
/// can actually answer.
///
/// **Why no method stays anonymous.** There is no share-token-bearing
/// public render on this contract — no method takes a token, and the
/// scope is resolved from the caller upstream. forge's token-gated
/// public surface is `IPublicFormApi`, which carries
/// `[<PublicEndpoint>]` and takes the token as a parameter. If a public
/// report-share surface is ever wanted it is a *separate* contract of
/// that shape, not a relaxation here.
///
/// Migration: `docs/migrations/619-report-api-authorization.md`.
type IReportApi = {
    /// List every template the caller can see at the resolved scope.
    /// **Gate: any authenticated caller at the resolved scope.**
    /// Template bodies and placeholder schemas are business content and
    /// describe exactly what the deployment can render, so this is a
    /// read of scope-owned data, not a public catalogue.
    [<RequiresClaim "scope">]
    ListTemplates: unit -> Async<ReportTemplate list>
    /// Save (create or update) a template.
    /// **Gate: any authenticated caller at the resolved scope, PLUS the
    /// deployment's management predicate when
    /// `ReportApiHandler.withManagementGate` is composed** — a template
    /// is executable render content, so writing one is a privileged
    /// scope mutation. Without the wrapper the attribute gate is the
    /// only enforcement (GP 13 — a deployment that composes nothing
    /// pays nothing), and it still refuses anonymous callers.
    [<RequiresClaim "scope">]
    SaveTemplate: ReportTemplate -> Async<Result<ReportTemplate, string>>
    /// Delete a template.
    /// **Gate: identical to `SaveTemplate`** — same dual gate, and
    /// destructive besides.
    [<RequiresClaim "scope">]
    DeleteTemplate: TemplateId -> Async<Result<unit, string>>
    /// Render the named template against the supplied placeholder
    /// values. Inline-vs-blob routing decided by the handler based
    /// on output size + ReportApiConfig.
    /// **Gate: any authenticated caller at the resolved scope.** This is
    /// the [Phase 564] disclosure egress door — narrative placeholders
    /// carrying fact refs leave the deployment through here — so an
    /// anonymous caller must never reach it. It is deliberately NOT
    /// admin-gated: rendering is the ordinary user-facing operation the
    /// whole companion exists for, and the fact-level `FactExport` gate
    /// in `ReportApiHandler` is what decides *which values* a given
    /// principal may egress.
    [<RequiresClaim "scope">]
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