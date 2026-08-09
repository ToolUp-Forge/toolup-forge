module ToolUp.Reporting.ReportApiHandler

open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
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
//   - Route the template format through the registry → a deck-tier
//     format refuses Renderer (FormatServedByDeckTier ...); anything
//     else with nothing registered refuses Renderer
//     (NoRendererForFormat ...)
//   - Resolve `NarrativeValue` placeholders through the disclosure
//     export door (below), projecting each document to format-
//     appropriate text
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
//
// ─── Disclosure export door (Phase 564.B) ────────────────────────────
//
// A rendered report is a fact egress surface: narrative content whose
// `Metric` spans carry fact refs (Phase 521) can ride a render request
// as a `NarrativeValue` placeholder. When the fact companion's
// disclosure gate is supplied (`createWithDisclosureGate`), every fact
// ref in every narrative value is checked at the `FactExport` surface
// *before* rendering — the one `IFactDisclosureGate` is consulted,
// never a per-surface re-implementation. A denied fact's value is
// redacted to the policy-naming marker (the Phase 525 redaction walk,
// `NarrativeFacts.redactDeniedFacts`, reused verbatim) and the report
// gains a "Withheld values" section noting each withheld ref (fact id
// + policy — never the value). Every deny is audited by the gate
// itself (GP 6). No gate (`create`) ⇒ pass-through (GP 11/13): a
// deployment without classified facts renders byte-identically.

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

/// Project a (disclosure-resolved) narrative document to the text a
/// template of the given format embeds. `Html` templates receive an
/// HTML fragment (substitute via `{{key_raw}}` to bypass the
/// renderer's escaping); every other format receives markdown.
let private projectNarrative (format: TemplateFormat) (document: NarrativeDocument) : string =
    match format with
    | Html -> NarrativeHtml.render document
    | Markdown
    | Pdf
    | Docx
    | Xlsx
    | Pptx -> NarrativeMarkdown.render document

/// The "Withheld values" section appended to a redacted document —
/// the report output notes each withheld ref (fact id + policy, with
/// the canonical refusal wording) without ever carrying the value.
let private withheldNoteSection (withheld: (string * string) list) : NarrativeSection = {
    Id = "withheld-values"
    Heading = "Withheld values"
    Subheading = None
    Elements =
        withheld
        |> List.map (fun (factId, policyRef) ->
            Callout(
                Notice,
                [
                    InlineSpan.Text(sprintf "Fact %s — %s." factId (FactDisclosureVerdict.refusalText policyRef))
                ]
            ))
}

/// The export door itself: check every fact ref in `document` at the
/// `FactExport` surface, redact denied values to the policy-naming
/// marker (the Phase 525 walk reused), and append the withheld-values
/// note. A document citing no facts never consults the gate; a
/// document whose facts are all disclosable returns unchanged.
///
/// Public since Phase 650, and deliberately so: the chart export bundle
/// is a second document-egress surface, and a second surface with its
/// own copy of this walk is how one door becomes two doors that agree
/// until they do not. `NarrativeExportBundle` calls THIS function — the
/// bundle is an export surface, not a side door.
let applyExportDisclosure
    (gate: IFactDisclosureGate)
    (principal: string)
    (scopeId: string)
    (document: NarrativeDocument)
    : Async<NarrativeDocument> =
    async {
        match NarrativeFacts.factRefs document |> Set.toList with
        | [] -> return document
        | refs ->
            let! verdicts = gate.Check(scopeId, principal, FactExport, refs)

            let denied =
                verdicts
                |> Map.toList
                |> List.choose (fun (factId, verdict) ->
                    match verdict with
                    | FactNotDisclosable policyRef -> Some(factId, policyRef)
                    | FactDisclosable -> None)

            match denied with
            | [] -> return document
            | denied ->
                let redacted = NarrativeFacts.redactDeniedFacts (Map.ofList denied) document

                return {
                    redacted with
                        Sections = redacted.Sections @ [ withheldNoteSection denied ]
                }
    }

/// Resolve every top-level `NarrativeValue` in the supplied values —
/// through the export door when a gate is present, pass-through
/// projection otherwise — into the `TextValue` the renderer consumes.
/// A values map with no narrative content is returned as-is, so the
/// pre-existing render path is untouched (GP 11).
let private resolveNarrativeValues
    (disclosure: (IFactDisclosureGate * string) option)
    (scopeId: string)
    (format: TemplateFormat)
    (values: Map<string, PlaceholderValue>)
    : Async<Map<string, PlaceholderValue>> =
    async {
        let hasNarrative =
            values
            |> Map.exists (fun _ value ->
                match value with
                | NarrativeValue _ -> true
                | _ -> false)

        if not hasNarrative then
            return values
        else
            let! resolved =
                values
                |> Map.toList
                |> List.map (fun (key, value) -> async {
                    match value with
                    | NarrativeValue document ->
                        let! disclosed =
                            match disclosure with
                            | Some(gate, principal) -> applyExportDisclosure gate principal scopeId document
                            | None -> async.Return document

                        return key, TextValue(projectNarrative format disclosed)
                    | other -> return key, other
                })
                |> Async.Sequential

            return Map.ofArray resolved
    }

/// Shared handler body — `disclosure` carries the fact-disclosure gate
/// + the calling principal when the deployment composes the fact tier.
let private createCore
    (disclosure: (IFactDisclosureGate * string) option)
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
                    // Phase 647 — `Route`, not `TryResolve`: a deck-tier
                    // format refuses with the error that names the deck
                    // tier, rather than the "missing PackageReference"
                    // one it would otherwise fall through to.
                    match registry.Route template.Format with
                    | Result.Error routing -> return Result.Error(Renderer routing)
                    | Result.Ok renderer ->
                        // Phase 564.B — the disclosure export door runs
                        // before rendering; a values map with no
                        // narrative content passes through untouched.
                        let! values = resolveNarrativeValues disclosure scopeId template.Format values

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

/// Build a per-scope `IReportApi` from the supplied registry,
/// template store, blob writer, audit callback, and config. No
/// disclosure gate: `NarrativeValue` placeholders are projected to
/// text unchecked (a deployment without the fact tier pays nothing —
/// GP 13).
let create
    (templateStore: IReportTemplateStore)
    (registry: RendererRegistry)
    (storeBlob: StoreBlob)
    (auditOnRender: AuditOnRender)
    (config: ReportApiConfig)
    (scopeId: string)
    : IReportApi =
    createCore None templateStore registry storeBlob auditOnRender config scopeId

/// `create` with the fact-disclosure export door engaged (Phase
/// 564.B): every fact ref carried by a `NarrativeValue` placeholder is
/// checked through the supplied gate at the `FactExport` surface
/// before rendering. `principal` is the resolved caller the gate
/// audits denies against — resolve it upstream alongside `scopeId`
/// (both are per-caller). Deployments composing the fact tier wire
/// this variant; the gate arrives from DI wherever the fact store is
/// composed (`FactsCompose` registers `IFactDisclosureGate`
/// inseparably from the store).
let createWithDisclosureGate
    (gate: IFactDisclosureGate)
    (principal: string)
    (templateStore: IReportTemplateStore)
    (registry: RendererRegistry)
    (storeBlob: StoreBlob)
    (auditOnRender: AuditOnRender)
    (config: ReportApiConfig)
    (scopeId: string)
    : IReportApi =
    createCore (Some(gate, principal)) templateStore registry storeBlob auditOnRender config scopeId

// ─── In-handler management gate (Phase 619) ──────────────────────────
//
// `IReportApi`'s per-method attributes (`[<RequiresClaim "scope">]` on
// all four) are the primary gate: the dispatcher's Phase 69d classifier
// refuses an anonymous caller before this handler runs. That closes the
// defect Phase 619 was filed for — every method used to be
// `[<AllowAnonymous>]` while its doc comments claimed "Owner / Admin
// gated", the same tell Phase 229 found on the DSR export/erasure
// endpoints.
//
// 229's lesson was that ONE gate whose enforcement lives entirely in
// deployment wiring is a gate nobody can point at. So, as there, the
// mutating half gets a second, in-handler check that does not depend on
// the deployment's auth middleware being wired the way the contract
// hopes. It is a decorator rather than a constructor parameter because
// the deployment's answer to "may this caller manage templates?" is the
// deployment's own role model — forge cannot express it (the first-party
// providers leave `AuthenticatedUser.Roles` empty, so an
// `[<RequiresRole "Owner">]` would be a Phase 132 dead gate denying
// everyone) — and because a decorator composes with BOTH `create` and
// `createWithDisclosureGate` without a combinatorial set of factories.
//
// Not composing it is a supported posture (GP 13): the attribute gate
// still refuses anonymous callers, which is the breaking default this
// phase ships. Composing it is how a deployment restores the Owner /
// Admin restriction its templates need.

/// Predicate answering "may the current caller manage templates at this
/// scope?". Resolved per call — not snapshotted at construction — so a
/// deployment that revokes a caller's management rights mid-session
/// takes effect on the caller's next write rather than at the next
/// restart. (The build-once / read-per-call seam mismatch is a live
/// defect class in this codebase; this seam is read-per-call by
/// construction.)
type CanManageTemplates = unit -> Async<bool>

/// The refusal a management-gated write returns. A single constant so
/// the two call sites cannot drift, and so a consumer can match on it.
/// Names the requirement, never the caller or the policy internals.
[<Literal>]
let TemplateManagementDenied =
    "report-template management requires an Owner / Admin caller at this scope"

/// Phase 619 — wrap an `IReportApi` so `SaveTemplate` / `DeleteTemplate`
/// additionally consult the deployment's management predicate. Reads and
/// renders are untouched: listing and rendering are the ordinary
/// user-facing operations, and `Render`'s own fact-level `FactExport`
/// gate (Phase 564.B) is what decides which values a principal may
/// egress.
///
/// Composes over either factory:
///
/// ```fsharp
/// ReportApiHandler.createWithDisclosureGate gate principal store registry storeBlob audit config scopeId
/// |> ReportApiHandler.withManagementGate canManage
/// ```
let withManagementGate (canManage: CanManageTemplates) (api: IReportApi) : IReportApi = {
    api with
        SaveTemplate =
            fun template -> async {
                let! permitted = canManage ()

                if permitted then
                    return! api.SaveTemplate template
                else
                    return Result.Error TemplateManagementDenied
            }

        DeleteTemplate =
            fun id -> async {
                let! permitted = canManage ()

                if permitted then
                    return! api.DeleteTemplate id
                else
                    return Result.Error TemplateManagementDenied
            }
}