// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.ConversationPanel

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open Feliz
open ToolUp.Platform
open ToolUp.Platform.DataProp
open ToolUp.Platform.AI
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI

// ─── Tool-call pill ─────────────────────────────────────────────
//
// Phase 6h: collapse/expand UI for tool calls. Replaces the
// previous debug-mode-only synthetic `[debug] Tool result: …`
// messages with a real production component — every user sees a
// pill per tool call with status icon + name; clicking expands to
// the JSON args and result.
//
// Self-contained React state — `expanded: bool` lives in the
// component, not the Elmish model. The conversation history's
// `ToolCallRecord` already carries args / result / status from
// the agent loop's SSE events; the pill just renders them.

[<Emit("JSON.stringify(JSON.parse($0), null, 2)")>]
let private prettyJson (raw: string) : string = jsNative

let private safePrettyJson (raw: string) : string =
    if String.IsNullOrWhiteSpace raw then
        ""
    else
        try
            prettyJson raw
        with _ ->
            // Not valid JSON — keep the raw form so the user can still
            // see what the AI sent / received.
            raw

[<ReactComponent>]
let ToolCallPill (tc: ToolCallRecord) =
    let expanded, setExpanded = React.useState false

    // Phase 6j.A: tool calls dispatched by the client-side fast-path
    // resolver use a `_fastpath.*` ToolName prefix and render with a
    // ⚡ icon instead of the standard ✓ so users can distinguish
    // resolver-generated turns from real LLM tool calls.
    let isFastPath = tc.ToolName.StartsWith("_fastpath.")

    let statusIcon =
        if isFastPath && tc.Status = Completed then
            "⚡"
        else
            match tc.Status with
            | Pending -> "⏳"
            | Running -> "▶"
            | Completed -> "✓"
            | Failed _ -> "✗"

    let statusColour =
        if isFastPath && tc.Status = Completed then
            "text-violet-500"
        else
            match tc.Status with
            | Failed _ -> "text-red-500"
            | Completed -> "text-green-600"
            | Running -> "text-blue-500"
            | Pending -> "text-gray-400"

    let resultText =
        match tc.Status, tc.Result with
        | Failed reason, _ -> Some reason
        | _, Some r -> Some r
        | _, None -> None

    Html.div [
        prop.className "rounded border border-gray-200 bg-white text-xs"
        prop.children [
            Html.button [
                prop.className [
                    "w-full flex items-center gap-2 px-2 py-1"
                    "text-left hover:bg-gray-50"
                    "focus:outline-none"
                ]
                prop.onClick (fun _ -> setExpanded (not expanded))
                prop.children [
                    Html.span [ prop.className statusColour; prop.text statusIcon ]
                    Html.span [ prop.className "font-mono text-gray-700"; prop.text tc.ToolName ]
                    Html.span [
                        prop.className "ml-auto text-gray-400 text-[10px]"
                        prop.text (if expanded then "▲" else "▼")
                    ]
                ]
            ]
            if expanded then
                Html.div [
                    prop.className "border-t border-gray-100 px-2 py-1.5 space-y-2"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.div [
                                    prop.className "text-[10px] uppercase text-gray-400 font-semibold mb-0.5"
                                    prop.text "Arguments"
                                ]
                                Html.pre [
                                    prop.className
                                        "bg-gray-50 rounded p-1.5 overflow-x-auto whitespace-pre-wrap font-mono text-[11px] text-gray-700"
                                    prop.text (safePrettyJson tc.Arguments)
                                ]
                            ]
                        ]
                        match resultText with
                        | None ->
                            Html.div [
                                prop.className "text-[10px] uppercase text-gray-400 font-semibold"
                                prop.text "Result pending…"
                            ]
                        | Some r ->
                            Html.div [
                                prop.children [
                                    Html.div [
                                        prop.className "text-[10px] uppercase text-gray-400 font-semibold mb-0.5"
                                        prop.text (
                                            match tc.Status with
                                            | Failed _ -> "Error"
                                            | _ -> "Result"
                                        )
                                    ]
                                    Html.pre [
                                        prop.className [
                                            "bg-gray-50 rounded p-1.5 overflow-x-auto whitespace-pre-wrap font-mono text-[11px]"
                                            match tc.Status with
                                            | Failed _ -> "text-red-700"
                                            | _ -> "text-gray-700"
                                        ]
                                        prop.text (safePrettyJson r)
                                    ]
                                ]
                            ]
                    ]
                ]
        ]
    ]

// ─── Conversation export ────────────────────────────────────────
//
// Phase 6h: lets the user download the active conversation as a
// Markdown or JSON file. Pure client-side — formats the in-memory
// message list, builds a Blob, triggers a browser download.
// No server round-trip; the conversation is already in the
// client's state.

[<Emit("new Blob([$0], { type: $1 })")>]
let private makeBlob (content: string) (mimeType: string) : obj = jsNative

[<Emit("URL.createObjectURL($0)")>]
let private createObjectUrl (blob: obj) : string = jsNative

[<Emit("URL.revokeObjectURL($0)")>]
let private revokeObjectUrl (url: string) : unit = jsNative

let private triggerDownload (filename: string) (mimeType: string) (content: string) : unit =
    let blob = makeBlob content mimeType
    let url = createObjectUrl blob
    let a = Browser.Dom.document.createElement "a" :?> Browser.Types.HTMLAnchorElement
    a.href <- url
    a?download <- filename
    Browser.Dom.document.body.appendChild a |> ignore
    a.click ()
    Browser.Dom.document.body.removeChild a |> ignore
    revokeObjectUrl url

let private participantHeading (p: ParticipantType) =
    match p with
    | User -> "User"
    | AIAssistant -> "Assistant"
    | System -> "System"

// Phase 6h.A — `includeToolDetails` gates raw `ToolCalls` (arguments +
// results) out of the export by default. Tool payloads regularly carry
// user ids, internal keys, and RAG/KB chunks that may include PII; the
// sanitised default writes only Participant / Content / Timestamp.
let exportAsMarkdown
    (conversationName: string)
    (messages: ConversationMessage list)
    (includeToolDetails: bool)
    : string =
    let now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
    let lines = ResizeArray<string>()

    lines.Add($"# {conversationName}")
    lines.Add("")
    lines.Add($"**Exported:** {now}")
    lines.Add("")
    lines.Add("---")

    for msg in messages do
        lines.Add("")
        lines.Add($"## {participantHeading msg.Participant}")
        lines.Add("")

        // Phase 6o follow-up B: multipart turns render via
        // `AIContentPart.exportPlaceholder` so embedded image bytes
        // never reach paste-able markdown. Text parts render verbatim;
        // image parts render as `![image #N]` placeholders. Plain-text
        // turns (empty `Parts`) fall through to the existing `Content`
        // path so non-multimodal deployments are byte-for-byte
        // unchanged.
        if msg.Parts.IsEmpty then
            lines.Add(msg.Content)
        else
            msg.Parts
            |> List.iteri (fun i part -> lines.Add(AIContentPart.exportPlaceholder (i + 1) part))

        if includeToolDetails && not msg.ToolCalls.IsEmpty then
            lines.Add("")
            lines.Add("**Tool calls:**")
            lines.Add("")

            for tc in msg.ToolCalls do
                let icon =
                    match tc.Status with
                    | Pending -> "⏳"
                    | Running -> "▶"
                    | Completed -> "✓"
                    | Failed _ -> "✗"

                lines.Add($"- {icon} `{tc.ToolName}`")

                if not (String.IsNullOrWhiteSpace tc.Arguments) then
                    lines.Add("  ```json")
                    lines.Add($"  {tc.Arguments}")
                    lines.Add("  ```")

                match tc.Result with
                | Some r when not (String.IsNullOrWhiteSpace r) ->
                    lines.Add("  Result:")
                    lines.Add("  ```")
                    lines.Add($"  {r}")
                    lines.Add("  ```")
                | _ -> ()

        lines.Add("")

    String.Join("\n", lines)

let exportAsJson (conversationName: string) (messages: ConversationMessage list) (includeToolDetails: bool) : string =
    let raw =
        if includeToolDetails then
            Json.serialize {|
                ConversationName = conversationName
                ExportedAt = DateTime.UtcNow
                Messages = messages
            |}
        else
            // Sanitised default — project away ToolCalls (and anything
            // else that could carry PII); only the three safe fields
            // survive into the downloaded file.
            Json.serialize {|
                ConversationName = conversationName
                ExportedAt = DateTime.UtcNow
                Messages =
                    messages
                    |> List.map (fun m -> {|
                        Participant = m.Participant
                        Content = m.Content
                        Timestamp = m.Timestamp
                    |})
            |}

    try
        prettyJson raw
    with _ ->
        raw

let private safeFilename (name: string) =
    let invalidChars = [| '/'; '\\'; ':'; '*'; '?'; '"'; '<'; '>'; '|'; '\n'; '\r'; '\t' |]
    let sb = System.Text.StringBuilder()

    for c in name do
        if Array.contains c invalidChars then
            sb.Append '_' |> ignore
        else
            sb.Append c |> ignore

    let trimmed = sb.ToString().Trim()
    if trimmed = "" then "conversation" else trimmed

[<ReactComponent>]
let ExportMenu (conversationName: string) (messages: ConversationMessage list) =
    let isOpen, setIsOpen = React.useState false
    // Phase 6h.A — defaults OFF: the export is sanitised unless the
    // user explicitly opts into (possibly-PII) tool payloads.
    let includeToolDetails, setIncludeToolDetails = React.useState false

    // Fire-and-forget audit beacon — mirrors FastPathHook.sendBeacon.
    // Records only metadata (which conversation, the opt-in flag);
    // the server stamps `ExportedBy` from the request identity.
    let recordExportAudit () =
        try
            match messages with
            | first :: _ ->
                let body =
                    Json.serialize {|
                        ConversationId = first.ConversationId
                        IncludeToolDetails = includeToolDetails
                    |}

                let opts =
                    createObj [
                        "method" ==> "POST"
                        "headers"
                        ==> createObj [
                            "Content-Type" ==> "application/json"
                            "X-User-Id" ==> UserSession.getUserId ()
                        ]
                        "body" ==> body
                    ]

                Browser.Dom.window?fetch ("/api/ai/conversation/export-audit", opts) |> ignore
            | [] -> ()
        with _ ->
            // Audit-beacon failure must never break the user's download.
            ()

    let handleExport (format: string) =
        let stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
        let baseName = safeFilename conversationName

        match format with
        | "markdown" ->
            triggerDownload
                $"{baseName}-{stamp}.md"
                "text/markdown"
                (exportAsMarkdown conversationName messages includeToolDetails)
        | "json" ->
            triggerDownload
                $"{baseName}-{stamp}.json"
                "application/json"
                (exportAsJson conversationName messages includeToolDetails)
        | _ -> ()

        recordExportAudit ()
        setIsOpen false

    let disabled = messages.IsEmpty

    Html.div [
        prop.className "relative"
        prop.children [
            Html.button [
                prop.className [
                    "px-3 py-1.5 rounded text-xs"
                    "border border-gray-300"
                    "bg-white"
                    if disabled then
                        "text-gray-400 cursor-not-allowed"
                    else
                        "text-gray-700 hover:bg-gray-50 cursor-pointer"
                ]
                prop.disabled disabled
                prop.title (
                    if disabled then
                        "Nothing to export — start a conversation first."
                    else
                        "Download the conversation"
                )
                prop.onClick (fun _ ->
                    if not disabled then
                        setIsOpen (not isOpen))
                prop.text "Export ▾"
            ]
            if isOpen then
                Html.div [
                    prop.className [
                        "absolute right-0 mt-1 z-10"
                        "bg-white border border-gray-200 rounded shadow-md"
                        "min-w-[160px]"
                        "text-xs"
                    ]
                    prop.children [
                        Html.label [
                            prop.className
                                "flex items-start gap-2 px-3 py-2 border-b border-gray-100 cursor-pointer select-none"
                            prop.children [
                                Html.input [
                                    prop.type' "checkbox"
                                    prop.className "mt-0.5"
                                    prop.isChecked includeToolDetails
                                    prop.onChange (fun (v: bool) -> setIncludeToolDetails v)
                                ]
                                Html.span [
                                    prop.className "text-gray-600 leading-snug"
                                    prop.text "Include tool details (⚠ may contain PII)"
                                ]
                            ]
                        ]
                        Html.button [
                            prop.className "w-full text-left px-3 py-2 hover:bg-gray-50"
                            prop.onClick (fun _ -> handleExport "markdown")
                            prop.text "Markdown (.md)"
                        ]
                        Html.button [
                            prop.className "w-full text-left px-3 py-2 hover:bg-gray-50"
                            prop.onClick (fun _ -> handleExport "json")
                            prop.text "JSON (.json)"
                        ]
                    ]
                ]
        ]
    ]

// ─── Sources panel ───────────────────────────────────────────────
//
// Renders the `RetrievedSource list` attached to an assistant message
// as a collapsed "Sources (N)" footer; clicking expands it to a list
// of cards showing document name, score, origin, location hint, and a
// snippet preview. Used by both the side-panel (`View` below) and the
// full AI module page (`AIAssistantUI.messageView`) so the grounding
// affordance is consistent across surfaces.
//
// Click-to-open-source navigation is intentionally deferred — visible
// sourcing is the primary value of this WS1.5 increment; deep-linking
// into the KB document panel is a follow-up.

let private originLabel (origin: ChunkOrigin) =
    match origin with
    | Document -> "Document"
    | Note -> "Note"
    | Narrative -> "Narrative"
    | AIContext -> "AI context"
    | Conversation -> "Conversation"
    | Other label -> label

/// Superscript marker glyphs that pair with `RAGPromptBuilder.formatMatch`'s
/// citation prefix. The model is asked to append these to grounded sentences
/// and we render them as clickable affordances that open the Sources panel
/// and highlight the matching entry.
let private citationLabel (i: int) =
    let supers = [| "¹"; "²"; "³"; "⁴"; "⁵"; "⁶"; "⁷"; "⁸"; "⁹" |]

    if i >= 0 && i < supers.Length then
        $"[{supers[i]}]"
    else
        $"[{i + 1}]"

/// Detect which citation indices appear in the assistant's reply so the
/// citation strip can render only the markers the model actually used. We
/// scan once per message and emit a list of indices for the corresponding
/// sources; sources never cited by the model are still reachable via the
/// expanded Sources panel.
let private citedIndices (sources: RetrievedSource list) (content: string) : int list =
    if isNull content || content = "" || sources.IsEmpty then
        []
    else
        sources
        |> List.mapi (fun i _ -> i)
        |> List.filter (fun i -> content.Contains(citationLabel i))

/// Trim a source's document name to fit alongside the citation marker
/// without wrapping the citation strip past the chat-bubble width. Ellipsis
/// uses the single-character horizontal ellipsis to save a column.
let private truncateDocName (maxLen: int) (s: string) =
    if isNull s || s.Length <= maxLen then
        s
    else
        s.Substring(0, max 0 (maxLen - 1)) + "…"

// Phase 6q follow-up B — render `[unverified]` inline as a muted-red
// badge with a tooltip explaining why the model couldn't ground the
// surrounding claim. The post-stream normaliser substitutes
// `[unverified]` for phantom citations under the `Strict` policy
// (see Phase 6q's `CitationNormaliser`); the user reads the model's
// prose with the badge in place of the would-be citation marker.
//
// Markdown is block-rendered, so splitting at the tag breaks the
// containing paragraph into two blocks with a badge between — a
// minor visual quirk vs. the value of making "the model couldn't
// ground this" structurally legible. Acceptable trade-off for the
// substrate-wiring follow-up; a true inline-markdown variant would
// require Markdown.fs to expose an inline-only renderer.
[<Literal>]
let private UnverifiedTag = "[unverified]"

let private renderContentWithUnverifiedBadges (content: string) : ReactElement =
    if isNull content || not (content.Contains UnverifiedTag) then
        Toolup.Markdown.render content
    else
        let segments = content.Split([| UnverifiedTag |], StringSplitOptions.None)

        Html.div [
            prop.className "space-y-2"
            prop.children [
                for i, segment in Array.indexed segments do
                    if i > 0 then
                        Html.span [
                            prop.className
                                "inline-block px-1.5 py-0.5 rounded bg-red-50 text-red-700 text-[10px] font-medium border border-red-200 align-middle mx-1"
                            prop.title "The model couldn't ground this claim against the retrieved knowledge base."
                            prop.text "unverified"
                        ]

                    if segment <> "" then
                        Toolup.Markdown.render segment
            ]
        ]

[<ReactComponent>]
let MessageSources (sources: RetrievedSource list) (content: string) =
    let isExpanded, setExpanded = React.useState false
    let highlightIdx, setHighlightIdx = React.useState (None: int option)
    let containerRef = React.useRef<Browser.Types.HTMLDivElement option> None

    let focusSource (idx: int) =
        setExpanded true
        setHighlightIdx (Some idx)

    React.useEffect (
        (fun () ->
            match highlightIdx, containerRef.current with
            | Some idx, Some root ->
                let target = root?querySelector ($"[data-cite-idx='{idx}']")

                if not (isNull target) then
                    target?scrollIntoView (
                        {|
                            behavior = "smooth"
                            block = "nearest"
                        |}
                    )
            | _ -> ()),
        [| box highlightIdx |]
    )

    let cited = citedIndices sources content

    if sources.IsEmpty then
        Html.none
    else
        Html.div [
            prop.className "mt-2 pt-2 border-t border-gray-200 text-xs"
            prop.children [
                // Inline citation strip — clickable markers above the
                // collapsed Sources footer. Only shows markers the model
                // actually used; uncited sources are still listed in the
                // expanded panel.
                //
                // Each chip pairs the marker glyph (`[¹]`, `[²]`, …) with
                // the document name so a user who sees `[¹]` in the reply
                // text can match it to a source at a glance — without
                // that, the markers read as decorative footnote glyphs of
                // unclear provenance.
                if not cited.IsEmpty then
                    Html.div [
                        prop.className "mb-1"
                        prop.children [
                            Html.div [
                                prop.className "text-[10px] uppercase text-gray-400 font-semibold mb-1"
                                prop.text (
                                    if cited.Length = 1 then
                                        "Source cited in this reply"
                                    else
                                        "Sources cited in this reply"
                                )
                            ]
                            Html.div [
                                prop.className "flex flex-wrap gap-1"
                                prop.children [
                                    for idx in cited do
                                        let docName =
                                            sources
                                            |> List.tryItem idx
                                            |> Option.map _.DocumentName
                                            |> Option.defaultValue ""

                                        Html.button [
                                            prop.className
                                                "inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-violet-50 hover:bg-violet-100 text-violet-700 font-medium text-[11px] border border-violet-100 transition-colors"
                                            prop.title docName
                                            prop.onClick (fun _ -> focusSource idx)
                                            prop.children [
                                                Html.span [ prop.text (citationLabel idx) ]
                                                if docName <> "" then
                                                    Html.span [
                                                        prop.className "font-normal text-violet-900"
                                                        prop.text (truncateDocName 36 docName)
                                                    ]
                                            ]
                                        ]
                                ]
                            ]
                        ]
                    ]

                Html.button [
                    prop.className "text-gray-500 hover:text-gray-700 flex items-center gap-1 font-medium"
                    prop.onClick (fun _ ->
                        setExpanded (not isExpanded)

                        if isExpanded then
                            setHighlightIdx None)
                    prop.children [
                        Html.span [ prop.text (if isExpanded then "▼" else "▶") ]
                        Html.span [ prop.text $"Sources ({sources.Length})" ]
                    ]
                ]

                if isExpanded then
                    Html.div [
                        prop.ref containerRef
                        prop.className "mt-2 space-y-2"
                        prop.children [
                            for i, src in List.indexed sources do
                                // Phase 6q follow-up B — uncited-but-
                                // retrieved sources styled distinctly so
                                // operators can tell "the system found
                                // this but the model didn't actually use
                                // it" from "the model cited this."
                                let wasCited = List.contains i cited

                                Html.div [
                                    dataProp.citeIdx i
                                    prop.className [
                                        "rounded-md border p-2 transition-colors"
                                        if highlightIdx = Some i then
                                            "border-violet-400 ring-2 ring-violet-200 bg-white"
                                        elif wasCited then
                                            "border-gray-200 bg-white"
                                        else
                                            "border-gray-200 bg-gray-50 opacity-75"
                                    ]
                                    prop.children [
                                        Html.div [
                                            prop.className "flex items-center justify-between gap-2"
                                            prop.children [
                                                Html.div [
                                                    prop.className "flex items-center gap-1.5 min-w-0"
                                                    prop.children [
                                                        Html.span [
                                                            prop.className [
                                                                "shrink-0 font-medium text-[11px]"
                                                                if wasCited then "text-violet-600" else "text-gray-400"
                                                            ]
                                                            prop.text (citationLabel i)
                                                        ]
                                                        Html.span [
                                                            prop.className [
                                                                "font-medium truncate"
                                                                if wasCited then "text-gray-800" else "text-gray-500"
                                                            ]
                                                            prop.text src.DocumentName
                                                        ]
                                                        if not wasCited then
                                                            Html.span [
                                                                prop.className
                                                                    "shrink-0 px-1.5 py-0.5 rounded bg-gray-100 text-gray-500 text-[9px] font-medium uppercase tracking-wide"
                                                                prop.title
                                                                    "Retrieved by the system but not cited in the model's reply."
                                                                prop.text "uncited"
                                                            ]
                                                    ]
                                                ]
                                                Html.span [
                                                    prop.className [
                                                        "shrink-0 px-1.5 py-0.5 rounded text-[10px] font-medium"
                                                        if wasCited then
                                                            "bg-violet-50 text-violet-700"
                                                        else
                                                            "bg-gray-100 text-gray-500"
                                                    ]
                                                    prop.text (sprintf "%.2f" src.Score)
                                                ]
                                            ]
                                        ]
                                        Html.div [
                                            prop.className "flex items-center gap-2 text-[10px] text-gray-500 mt-0.5"
                                            prop.children [
                                                Html.span [ prop.text (originLabel src.Origin) ]
                                                match src.LocationHint with
                                                | Some hint when hint <> "" ->
                                                    Html.span [ prop.text "·" ]
                                                    Html.span [ prop.text hint ]
                                                | _ -> Html.none
                                            ]
                                        ]
                                        if src.Snippet <> "" then
                                            Html.div [
                                                prop.className "text-gray-600 mt-1 leading-relaxed"
                                                prop.text src.Snippet
                                            ]
                                    ]
                                ]
                        ]
                    ]
            ]
        ]

// ─── Knowledge-base inventory badge ─────────────────────────────
//
// Subscribes to `NotificationClient` for `CustomNotification` envelopes
// keyed `KnowledgeBase.InventoryUpdated`. Renders a small clickable
// pill summarising the team's KB content ("KB · 4 docs, 2 notes")
// alongside the AI Chat trigger. Click invokes the `onNavigate`
// callback so the caller can route the user to the KnowledgeBase
// module. Stays hidden until the first inventory event arrives —
// avoiding a "0 items" badge when the deployment has no KB.
//
// The wire-format key is duplicated from
// `KnowledgeBase.Shared.SharedTypes.InventoryUpdatedNotificationKey`;
// AI is more foundational than KB and must not import KB types.
// Same convention as `KbStatusBanner` in `AIAssistantUI.fs`.

[<Literal>]
let private kbInventoryUpdatedKey = "KnowledgeBase.InventoryUpdated"

type private KbInventoryPayload = {
    DocumentCount: int
    NoteCount: int
    HasAIContext: bool
    LastUpdated: DateTime
    SuggestedQuestions: string list
}

let private kbInventoryLabel (p: KbInventoryPayload) =
    let parts = [
        if p.DocumentCount = 1 then
            "1 doc"
        elif p.DocumentCount > 1 then
            sprintf "%d docs" p.DocumentCount

        if p.NoteCount = 1 then
            "1 note"
        elif p.NoteCount > 1 then
            sprintf "%d notes" p.NoteCount

        if p.HasAIContext then
            "AI context"
    ]

    match parts with
    | [] -> ""
    | xs -> sprintf "KB \u00B7 %s" (String.concat ", " xs)

[<ReactComponent>]
let KbInventoryBadge (onNavigate: unit -> unit) =
    let payload, setPayload = React.useState<KbInventoryPayload option> None

    React.useEffectOnce (fun () ->
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.CustomNotification(key, payloadJson) when key = kbInventoryUpdatedKey ->
                    try
                        let p = Json.parseAs<KbInventoryPayload> payloadJson
                        setPayload (Some p)
                    with _ ->
                        ()
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    match payload with
    | None -> Html.none
    | Some p ->
        let label = kbInventoryLabel p

        if label = "" then
            Html.none
        else
            Html.button [
                prop.className
                    "flex items-center gap-1 px-2 py-1 rounded text-[11px] font-medium bg-emerald-50 text-emerald-700 hover:bg-emerald-100 transition-colors"
                prop.title "Open the Knowledge Base"
                prop.onClick (fun _ -> onNavigate ())
                prop.children [ Html.span [ prop.text label ] ]
            ]

// ─── Side panel — width persistence ──────────────────────────────
//
// User-resizable AI side panel. A 4-px handle on the panel's left
// edge drag-resizes the panel; the width persists across reloads
// via localStorage. Clamped to [320, 800] — narrower truncates the
// input affordances, wider crowds the main content area on a
// 1366-wide screen.
//
// The shell's main-content right padding tracks the panel width
// via a CSS custom property (`--toolup-ai-panel-width`) set on
// `document.documentElement`. The shell reads it through a Tailwind
// arbitrary value (`pr-[var(--toolup-ai-panel-width,_380px)]`); the
// 380px default keeps consumers that don't render the side panel
// at all unaffected.

[<Literal>]
let private defaultPanelWidth = 380

[<Literal>]
let private minPanelWidth = 320

[<Literal>]
let private maxPanelWidth = 800

[<Literal>]
let private panelWidthStorageKey = "toolup-ai-panel-width"

[<Literal>]
let private panelWidthCssVar = "--toolup-ai-panel-width"

let private clampPanelWidth (w: int) =
    if w < minPanelWidth then minPanelWidth
    elif w > maxPanelWidth then maxPanelWidth
    else w

let private loadPersistedPanelWidth () : int =
    match Browser.Dom.window.localStorage.getItem panelWidthStorageKey with
    | null
    | "" -> defaultPanelWidth
    | raw ->
        match Int32.TryParse raw with
        | true, n -> clampPanelWidth n
        | _ -> defaultPanelWidth

let private persistPanelWidth (w: int) : unit =
    Browser.Dom.window.localStorage.setItem (panelWidthStorageKey, string w)

// CSS custom properties must be written via `setProperty`; the
// `el.style.--foo` syntax is invalid. Setting on `documentElement`
// (the `<html>` tag) lets every descendant — including the shell's
// main content area — read the variable via `var(...)`.
[<Emit("document.documentElement.style.setProperty($0, $1)")>]
let private setRootCssVar (name: string) (value: string) : unit = jsNative

// ─── Side panel component ────────────────────────────────────────

[<ReactComponent>]
let View
    (isOpen: bool)
    (title: string)
    (messages: ConversationMessage list)
    (streamingContent: string)
    (onSendMessage: string -> unit)
    (onClose: unit -> unit)
    (onClearSession: unit -> unit)
    (onCancel: (unit -> unit) option)
    =
    // Local React state for the input. Per CLAUDE.md: text inputs use
    // React.useState, not the Elmish model — dispatch only on submit,
    // never per-keystroke.
    let inputText, setInputText = React.useState ""

    // History navigation state, modelled on shell history.
    //   historyIndex = 0 → input shows the live draft (whatever the
    //       user has typed; also the initial state).
    //   historyIndex = N (1-indexed) → input shows the Nth-most-recent
    //       user message from `messages`.
    //   savedDraft preserves the user's in-progress text when they
    //       press Up for the first time, so Down through 0 restores it.
    let historyIndex, setHistoryIndex = React.useState 0
    let savedDraft, setSavedDraft = React.useState ""

    // Panel width — persistent across reloads via localStorage; live
    // updated by the left-edge drag handle below. `dragInfoRef` holds
    // the pointer-down anchor (start pointer X + start width) so the
    // pointermove handler can compute the delta without depending on
    // potentially-stale React state captured at handler-bind time.
    let panelWidth, setPanelWidth = React.useState (loadPersistedPanelWidth ())

    let dragInfoRef = React.useRef<{| StartX: float; StartWidth: int |} option> None

    // Mirror the width to the CSS custom property + localStorage on
    // every change. localStorage writes are synchronous and cheap;
    // doing this on every render rather than only on drag-end keeps
    // the persistence logic in one place (no need to differentiate
    // initial-mount + drag-end + external-set).
    React.useEffect (
        (fun () ->
            setRootCssVar panelWidthCssVar (sprintf "%dpx" panelWidth)
            persistPanelWidth panelWidth),
        [| box panelWidth |]
    )

    let onResizeHandlePointerDown (e: Browser.Types.PointerEvent) =
        e.preventDefault ()
        // Pointer capture: pointermove + pointerup continue to fire
        // on this handle even when the pointer leaves the 4-px
        // strip. Without it the drag breaks the moment the user
        // moves outside the handle's bounds.
        let target: obj = e.target
        target?setPointerCapture (e.pointerId)

        dragInfoRef.current <-
            Some {|
                StartX = e.clientX
                StartWidth = panelWidth
            |}

    let onResizeHandlePointerMove (e: Browser.Types.PointerEvent) =
        match dragInfoRef.current with
        | Some info ->
            // The panel is right-anchored; its width grows as the
            // pointer moves leftward. `delta` is positive when the
            // pointer is left of the drag origin.
            let delta = info.StartX - e.clientX
            setPanelWidth (clampPanelWidth (info.StartWidth + int delta))
        | None -> ()

    let onResizeHandlePointerUp (e: Browser.Types.PointerEvent) =
        match dragInfoRef.current with
        | Some _ ->
            dragInfoRef.current <- None
            let target: obj = e.target
            target?releasePointerCapture (e.pointerId)
        | None -> ()

    // Sticky-bottom auto-scroll. The default is to keep the
    // messages area pinned to the latest content as the agent
    // streams. If the user deliberately scrolls up — to re-read a
    // previous response — the pin disengages. Scrolling back to
    // within 64 px of the bottom re-engages it. This is the
    // standard chat-UI hysteresis pattern; the threshold is wide
    // enough to absorb a casual mouse-wheel tick at the bottom
    // without ping-ponging.
    let messagesContainerRef = React.useRef<Browser.Types.HTMLDivElement option> None
    let stickToBottom, setStickToBottom = React.useState true

    React.useEffect (
        (fun () ->
            if stickToBottom then
                match messagesContainerRef.current with
                | Some el -> el.scrollTop <- float el.scrollHeight
                | None -> ()),
        // Dependencies that mean "new content might have arrived":
        // a new finalised message, or a token added to the live
        // stream. `stickToBottom` is also a dep so that re-engaging
        // the pin immediately snaps to bottom.
        [| box messages.Length; box streamingContent; box stickToBottom |]
    )

    let onMessagesScroll (e: Browser.Types.Event) =
        let el = e.currentTarget :?> Browser.Types.HTMLDivElement

        let distanceFromBottom =
            float el.scrollHeight - el.scrollTop - float el.clientHeight

        let nearBottom = distanceFromBottom < 64.0
        // Only flip state when crossing the threshold — avoids a
        // setState call on every scroll event during streaming.
        if nearBottom <> stickToBottom then
            setStickToBottom nearBottom

    // Subscribe to KB inventory updates so the zero-state can render
    // suggested questions tailored to the team's actual KB content.
    // Same wire-format channel as `KbInventoryBadge` — AI client never
    // imports KB types, only the duplicated literal key.
    let suggestedQuestions, setSuggestedQuestions = React.useState<string list> []
    // User-controlled visibility of the zero-state strip. Session-scoped
    // (resets when the page reloads) — a user dismissing once for an
    // experiment shouldn't permanently lose the affordance.
    let suggestionsHidden, setSuggestionsHidden = React.useState false

    React.useEffectOnce (fun () ->
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.CustomNotification(key, payloadJson) when key = kbInventoryUpdatedKey ->
                    try
                        let p = Json.parseAs<KbInventoryPayload> payloadJson
                        setSuggestedQuestions p.SuggestedQuestions
                    with _ ->
                        ()
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    // Auto-resize the textarea to fit its content, up to a cap. Resetting
    // to "auto" first is required so `scrollHeight` reflects content
    // height, not the previously-applied height.
    let textareaRef = React.useRef<Browser.Types.HTMLTextAreaElement option> None

    React.useEffect (
        (fun () ->
            match textareaRef.current with
            | Some el ->
                // Browser.Types.HTMLTextAreaElement doesn't expose `style`
                // in this binding, so reach the DOM style object via
                // dynamic JS interop.
                let style: obj = el?style
                style?height <- "auto"
                let maxPx = 200.0
                let scrollH = float el.scrollHeight
                let desired = min scrollH maxPx
                style?height <- sprintf "%fpx" desired
                style?overflowY <- if scrollH > maxPx then "auto" else "hidden"
            | None -> ()),
        [| box inputText |]
    )

    // User messages in reverse-chronological order (most recent first),
    // so userHistory[0] is the message Up-from-draft should land on.
    let userHistory =
        messages |> List.filter (fun m -> m.Participant = User) |> List.rev

    if not isOpen then
        Html.none
    else
        Html.div [
            prop.className "fixed right-0 top-0 h-full bg-white shadow-xl z-20 flex flex-col border-l border-gray-200"
            prop.style [ style.width panelWidth ]
            prop.children [
                // Resize handle — 4-px strip along the panel's left
                // edge. Wider hit-target than `border-l` alone so the
                // cursor doesn't have to be pixel-perfect.
                Html.div [
                    prop.className
                        "absolute left-0 top-0 bottom-0 w-1 cursor-ew-resize hover:bg-violet-300/60 transition-colors z-10"
                    prop.title "Drag to resize"
                    prop.onPointerDown onResizeHandlePointerDown
                    prop.onPointerMove onResizeHandlePointerMove
                    prop.onPointerUp onResizeHandlePointerUp
                    prop.onPointerCancel onResizeHandlePointerUp
                ]

                // Header
                Html.div [
                    prop.className "flex items-center justify-between px-4 py-3 border-b border-gray-200 bg-gray-50"
                    prop.children [
                        Html.h3 [ prop.className "text-sm font-semibold text-gray-700"; prop.text title ]
                        Html.div [
                            prop.className "flex items-center gap-1"
                            prop.children [
                                // Clear session \u2014 wipes the conversation
                                // and starts a new server-side conversation
                                // on the next send. Disabled when there's
                                // nothing to clear so the user has a
                                // visual cue mid-conversation.
                                Html.button [
                                    prop.className
                                        "text-sm text-gray-600 hover:text-violet-600 px-2 py-1 rounded hover:bg-gray-100 disabled:text-gray-400 disabled:hover:text-gray-400 disabled:hover:bg-transparent"
                                    prop.disabled (messages.IsEmpty && streamingContent = "")
                                    prop.title "Clear this conversation and start a new session"
                                    prop.onClick (fun _ -> onClearSession ())
                                    prop.text "Clear session"
                                ]
                                Html.button [
                                    prop.className "text-gray-400 hover:text-gray-600 p-1"
                                    prop.onClick (fun _ -> onClose ())
                                    prop.text "\u2715"
                                ]
                            ]
                        ]
                    ]
                ]

                // Messages area
                Html.div [
                    prop.ref messagesContainerRef
                    prop.className "flex-1 overflow-y-auto p-4 space-y-3"
                    prop.onScroll onMessagesScroll
                    prop.children [
                        // Zero-state: when there are no messages and no
                        // streaming content, surface KB-grounded
                        // suggested questions (WS4.4) so the input box
                        // isn't a blank starting point. Each click
                        // submits the question as a normal user turn.
                        if
                            messages.IsEmpty
                            && streamingContent = ""
                            && not suggestedQuestions.IsEmpty
                            && not suggestionsHidden
                        then
                            Html.div [
                                prop.className "space-y-2"
                                prop.children [
                                    Html.div [
                                        prop.className "flex items-center justify-between"
                                        prop.children [
                                            Html.div [ prop.className "text-xs text-gray-500"; prop.text "Try asking:" ]
                                            // Dismiss the zero-state suggestions for this
                                            // session. Re-appears on next page load — kept
                                            // session-scoped so a user trying it once
                                            // doesn't permanently lose the affordance.
                                            Html.button [
                                                prop.className
                                                    "text-[11px] text-gray-400 hover:text-gray-600 px-1.5 py-0.5 rounded hover:bg-gray-100"
                                                prop.title "Hide suggestions for this session"
                                                prop.onClick (fun _ -> setSuggestionsHidden true)
                                                prop.text "Hide"
                                            ]
                                        ]
                                    ]
                                    for q in suggestedQuestions do
                                        Html.button [
                                            prop.className
                                                "block w-full text-left px-3 py-2 rounded-lg bg-violet-50 hover:bg-violet-100 text-violet-900 text-sm border border-violet-100 transition-colors"
                                            prop.onClick (fun _ -> onSendMessage q)
                                            prop.text q
                                        ]
                                ]
                            ]

                        for msg in messages do
                            let isUser = msg.Participant = User

                            Html.div [
                                prop.className [
                                    "max-w-[85%] rounded-lg px-3 py-2 text-sm"
                                    if isUser then
                                        "ml-auto bg-violet-500 text-white"
                                    else
                                        "mr-auto bg-gray-100 text-gray-800"
                                ]
                                prop.children [
                                    renderContentWithUnverifiedBadges msg.Content

                                    if not msg.ToolCalls.IsEmpty then
                                        Html.div [
                                            prop.className "mt-2 space-y-1"
                                            prop.children [
                                                for tc in msg.ToolCalls do
                                                    ToolCallPill tc
                                            ]
                                        ]

                                    if not isUser && not msg.RetrievedSources.IsEmpty then
                                        MessageSources msg.RetrievedSources msg.Content
                                ]
                            ]

                        // Streaming content (in-progress AI response)
                        if streamingContent <> "" then
                            Html.div [
                                prop.className
                                    "mr-auto max-w-[85%] rounded-lg px-3 py-2 text-sm bg-gray-100 text-gray-800"
                                prop.children [
                                    Toolup.Markdown.render streamingContent
                                    Html.span [ prop.className "inline-block w-2 h-4 bg-gray-400 animate-pulse ml-1" ]
                                ]
                            ]

                        // Phase 6h: Cancel button rendered while a
                        // task is in flight. Caller passes
                        // `Some cancel` when there's a known taskId;
                        // the click handler is closed over the taskId
                        // so this component doesn't need to know it.
                        //
                        // Companion-supplied streaming actions
                        // (registered via SidePanelExtensions) render
                        // alongside Cancel in the same flex container.
                        let extraActions = SidePanelExtensions.getStreamingActions ()

                        if not (List.isEmpty extraActions) || onCancel.IsSome then
                            Html.div [
                                prop.className "mr-auto flex gap-2"
                                prop.children [
                                    for el in extraActions do
                                        el
                                    match onCancel with
                                    | Some cancel ->
                                        Html.button [
                                            prop.className [
                                                "px-2 py-1 rounded text-xs"
                                                "border border-gray-300 bg-white hover:bg-gray-50"
                                                "text-gray-700"
                                            ]
                                            prop.title
                                                "Cancel the in-flight AI response. Whatever has streamed so far will be kept."
                                            prop.onClick (fun _ -> cancel ())
                                            prop.text "✕ Cancel"
                                        ]
                                    | None -> ()
                                ]
                            ]
                    ]
                ]

                // Input area
                Html.div [
                    prop.className "border-t border-gray-200 p-3"
                    prop.children [
                        Html.div [
                            prop.className "flex gap-2 items-end"
                            prop.children [
                                Html.textarea [
                                    prop.ref textareaRef
                                    prop.className
                                        "flex-1 border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-violet-400 resize-none overflow-hidden leading-5"
                                    prop.rows 1
                                    prop.placeholder "Type a message..."
                                    prop.value inputText
                                    prop.onChange (fun (newText: string) ->
                                        setInputText newText
                                        // User-initiated edits reset history
                                        // navigation. Programmatic setInputText
                                        // from Up/Down below doesn't fire
                                        // onChange (React only fires it on
                                        // actual DOM input events), so this
                                        // runs only when the user actually
                                        // types or pastes.
                                        if historyIndex <> 0 then
                                            setHistoryIndex 0
                                            setSavedDraft "")
                                    prop.onKeyDown (fun e ->
                                        // Enter submits; Shift+Enter inserts
                                        // a newline. History nav via Up/Down
                                        // only fires on a single-line draft so
                                        // multi-line editing isn't hijacked.
                                        let isMultiline = inputText.Contains '\n'

                                        match e.key with
                                        | "Enter" when not e.shiftKey && inputText.Trim() <> "" ->
                                            e.preventDefault ()
                                            onSendMessage (inputText.Trim())
                                            setInputText ""
                                            setHistoryIndex 0
                                            setSavedDraft ""
                                        | "ArrowUp" when
                                            not isMultiline
                                            && not userHistory.IsEmpty
                                            && historyIndex < userHistory.Length
                                            ->
                                            e.preventDefault ()

                                            if historyIndex = 0 then
                                                setSavedDraft inputText

                                            let newIdx = historyIndex + 1
                                            setHistoryIndex newIdx
                                            setInputText userHistory[newIdx - 1].Content
                                        | "ArrowDown" when not isMultiline && historyIndex > 0 ->
                                            e.preventDefault ()
                                            let newIdx = historyIndex - 1
                                            setHistoryIndex newIdx

                                            if newIdx = 0 then
                                                setInputText savedDraft
                                            else
                                                setInputText userHistory[newIdx - 1].Content
                                        | _ -> ())
                                ]
                                Html.button [
                                    prop.className
                                        "bg-violet-500 hover:bg-violet-600 text-white px-4 py-2 rounded-lg text-sm font-medium disabled:opacity-50"
                                    prop.disabled (inputText.Trim() = "")
                                    prop.onClick (fun _ ->
                                        if inputText.Trim() <> "" then
                                            onSendMessage (inputText.Trim())
                                            setInputText ""
                                            setHistoryIndex 0
                                            setSavedDraft "")
                                    prop.text "Send"
                                ]
                            ]
                        ]
                    ]
                ]
            ]
        ]