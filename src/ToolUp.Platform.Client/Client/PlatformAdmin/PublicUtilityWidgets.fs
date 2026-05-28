// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PlatformAdmin.PublicUtilityWidgets

open System
open Fable.Core
open Fable.SimpleHttp
open Fable.SimpleJson
open Feliz
open ToolUp.Platform

// ─── Phase 61 — public-utility PlatformAdmin widgets ──────────────
//
// Four widgets surfaced inside `PlatformAdminUI` when
// `ClientConfig.PlatformAdminProfile = PublicUtilityPlatformAdminProfile`.
// Each widget fetches from the matching Phase 61 server endpoint
// (`/api/_platform/admin/{rate-limits,premium-users,ad-units}`) and
// renders a live data view. Per the phase body each widget
// "gracefully degrades when its dependency is not wired": a 503
// from the server (substrate not configured) or an empty result
// renders a widget-specific empty state rather than a hard error.
//
// Identity + CSRF headers are attached automatically by
// `CsrfClient.installRequestGuard`'s XHR/fetch wrappers — the widgets
// just call `Http.request` and the request-guard splices in the
// per-request dynamic headers at send time.

// ─── Shared style helpers ──────────────────────────────────────────

let private widgetCard (title: string) (subtitle: string) (body: ReactElement) : ReactElement =
    Html.div [
        prop.className "border border-slate-200 rounded-md p-4 mb-4 bg-white"
        prop.children [
            Html.div [
                prop.className "mb-3"
                prop.children [
                    Html.h3 [ prop.className "text-base font-semibold text-slate-900"; prop.text title ]
                    Html.p [ prop.className "text-xs text-slate-500"; prop.text subtitle ]
                ]
            ]
            body
        ]
    ]

let private substrateStub (reason: string) : ReactElement =
    Html.div [ prop.className "text-sm text-slate-600 italic"; prop.text reason ]

let private errorBanner (msg: string) : ReactElement =
    Html.div [
        prop.className "p-2 bg-red-50 border border-red-200 rounded text-red-700 text-xs"
        prop.text msg
    ]

let private smallButton (label: string) (onClick: unit -> unit) (disabled: bool) : ReactElement =
    Html.button [
        prop.className [
            "px-2 py-1 text-xs font-medium rounded border transition-colors"
            if disabled then
                "bg-gray-100 text-gray-400 border-gray-200 cursor-not-allowed"
            else
                "bg-white text-gray-700 border-slate-300 hover:bg-gray-50"
        ]
        prop.disabled disabled
        prop.text label
        prop.onClick (fun _ -> onClick ())
    ]

type private LoadState<'T> =
    | Loading
    | Loaded of 'T
    | LoadError of string

[<Emit("URL.createObjectURL($0)")>]
let private createObjectUrl (blob: obj) : string = jsNative

[<Emit("URL.revokeObjectURL($0)")>]
let private revokeObjectUrl (url: string) : unit = jsNative

[<Emit("new Blob([$0], { type: $1 })")>]
let private newBlob (text: string) (mime: string) : obj = jsNative

[<Emit("(function(url, name){ var a = document.createElement('a'); a.href = url; a.download = name; document.body.appendChild(a); a.click(); document.body.removeChild(a); })($0, $1)")>]
let private triggerDownload (url: string) (filename: string) : unit = jsNative

let private downloadCsv (filename: string) (csv: string) : unit =
    let blob = newBlob csv "text/csv;charset=utf-8"
    let url = createObjectUrl blob
    triggerDownload url filename
    revokeObjectUrl url

let private csvEscape (s: string) : string =
    if s.Contains(',') || s.Contains('"') || s.Contains('\n') then
        "\"" + s.Replace("\"", "\"\"") + "\""
    else
        s

// ─── Generic fetch helper ──────────────────────────────────────────
// Returns `Loaded payload` on 2xx, `Loaded fallback` on documented
// substrate-missing statuses (503), and `LoadError` on anything else
// or on parse failure. Callers pass a `fallbackOn503` so each widget
// chooses between "empty-state render" and "explicit
// substrate-disabled banner".

let inline private fetchJson<'T> (url: string) (fallbackOn503: 'T option) : Async<LoadState<'T>> = async {
    try
        let! response = Http.request url |> Http.method GET |> Http.send

        match response.statusCode, fallbackOn503 with
        | 200, _ ->
            try
                return Loaded(Json.parseAs<'T> response.responseText)
            with ex ->
                return LoadError(sprintf "Could not parse response: %s" ex.Message)
        | 503, Some fallback -> return Loaded fallback
        | 403, _ -> return LoadError "Access denied — platform-admin role required."
        | code, _ -> return LoadError(sprintf "Request failed (HTTP %d)" code)
    with ex ->
        return LoadError(sprintf "Network error: %s" ex.Message)
}

// ─── Widget 1 — Traffic dashboard ─────────────────────────────────
// The server-side `/api/_platform/admin/traffic` endpoint is the
// Phase 61 out-of-scope follow-up (Vision-tier metrics history
// aggregation is deferred). Until that endpoint lands the widget
// renders the substrate stub the original body shipped — opt-in
// surface, no broken fetches.

let trafficDashboard (config: ClientConfig) : ReactElement =
    let body =
        substrateStub
            "Traffic counters require the server-side /api/_platform/admin/traffic surface. Widget renders when that endpoint lands."

    widgetCard "Traffic" "Request volume + error-rate + latency per route" body

// ─── Widget 2 — Rate-limit event log ──────────────────────────────
// GET /api/_platform/admin/rate-limits?count=100 → RateLimitDecisionEvent list
// (server returns newest-first). Empty list → "Rate-limiting not
// configured" empty state. CSV export emits one row per event.

let private rateLimitsUrl = "/api/_platform/admin/rate-limits?count=100"

let private formatRateLimitKey (key: InboundRateLimitKey) : string =
    match key with
    | IpAddressKey ip -> sprintf "ip:%s" ip
    | UserIdKey uid -> sprintf "user:%s" uid
    | InboundComposite c -> sprintf "composite:%s" c

let private formatWindow (window: RateLimitWindow) : string =
    match window with
    | PerSecond -> "1s"
    | PerMinute -> "1m"
    | PerHour -> "1h"
    | PerDay -> "1d"
    | SlidingWindow(duration, buckets) -> sprintf "sliding %.0fs/%d" duration.TotalSeconds buckets

let private formatDecision (decision: InboundRateLimitDecision) : string =
    match decision with
    | AllowWithRemaining remaining -> sprintf "Allow (rem %d)" remaining
    | DenyWithError _ -> "Deny"

let private rateLimitToCsvRow (event: RateLimitDecisionEvent) : string =
    [
        event.OccurredAt.ToString("o")
        formatRateLimitKey event.Key
        event.Route
        formatWindow event.Window
        string event.Threshold
        formatDecision event.Decision
    ]
    |> List.map csvEscape
    |> String.concat ","

let private rateLimitsToCsv (events: RateLimitDecisionEvent list) : string =
    let header = "OccurredAt,Key,Route,Window,Threshold,Decision"
    let rows = events |> List.map rateLimitToCsvRow
    String.Join("\n", header :: rows)

let private rateLimitRow (event: RateLimitDecisionEvent) : ReactElement =
    let decisionClass =
        match event.Decision with
        | AllowWithRemaining _ -> "text-slate-700"
        | DenyWithError _ -> "text-red-700 font-medium"

    Html.tr [
        prop.className "border-t border-slate-200"
        prop.children [
            Html.td [
                prop.className "px-3 py-1.5 text-xs text-slate-500 font-mono whitespace-nowrap"
                prop.text (event.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss"))
            ]
            Html.td [
                prop.className "px-3 py-1.5 text-xs font-mono"
                prop.text (formatRateLimitKey event.Key)
            ]
            Html.td [ prop.className "px-3 py-1.5 text-xs text-slate-700"; prop.text event.Route ]
            Html.td [
                prop.className "px-3 py-1.5 text-xs text-slate-500"
                prop.text (formatWindow event.Window)
            ]
            Html.td [
                prop.className "px-3 py-1.5 text-xs text-slate-500 font-mono"
                prop.text (string event.Threshold)
            ]
            Html.td [
                prop.className (sprintf "px-3 py-1.5 text-xs %s" decisionClass)
                prop.text (formatDecision event.Decision)
            ]
        ]
    ]

let private rateLimitsTable (events: RateLimitDecisionEvent list) : ReactElement =
    Html.div [
        prop.className "border border-slate-200 rounded overflow-hidden"
        prop.children [
            Html.table [
                prop.className "w-full text-xs"
                prop.children [
                    Html.thead [
                        prop.className "bg-slate-50"
                        prop.children [
                            Html.tr [
                                prop.children [
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "Occurred"
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "Key"
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "Route"
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "Window"
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "Threshold"
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "Decision"
                                    ]
                                ]
                            ]
                        ]
                    ]
                    Html.tbody [ prop.children (events |> List.map rateLimitRow) ]
                ]
            ]
        ]
    ]

[<ReactComponent>]
let RateLimitEventLogBody () : ReactElement =
    let state, setState =
        React.useState (Loading: LoadState<RateLimitDecisionEvent list>)

    let load () =
        setState Loading

        async {
            let! result = fetchJson<RateLimitDecisionEvent list> rateLimitsUrl (Some [])
            setState result
        }
        |> Async.StartImmediate

    React.useEffectOnce (fun () -> load ())

    let isLoading =
        match state with
        | Loading -> true
        | _ -> false

    let controls =
        Html.div [
            prop.className "flex items-center gap-2 mb-3"
            prop.children [
                smallButton (if isLoading then "Refreshing..." else "Refresh") load isLoading
                match state with
                | Loaded events when not (List.isEmpty events) ->
                    smallButton
                        "Export CSV"
                        (fun () -> downloadCsv "rate-limit-events.csv" (rateLimitsToCsv events))
                        false
                | _ -> Html.none
            ]
        ]

    let body =
        match state with
        | Loading -> Html.p [ prop.className "text-xs text-slate-500"; prop.text "Loading..." ]
        | LoadError msg -> errorBanner msg
        | Loaded [] -> substrateStub "Rate-limiting not configured for this deployment, or no decisions recorded yet."
        | Loaded events -> rateLimitsTable events

    Html.div [ prop.children [ controls; body ] ]

let rateLimitEventLog (config: ClientConfig) : ReactElement =
    widgetCard "Rate-limit events" "Recent decisions by key + route (newest first)" (RateLimitEventLogBody())

// ─── Widget 3 — Ad-unit configuration ─────────────────────────────
// CRUD over /api/_platform/admin/ad-units. Skips entirely when
// ClientConfig.AdPanel = NoAdPanel (the server endpoint is also
// 503-mounted when EntityStore is unwired — the widget renders an
// empty state in that case too).

let private adUnitsUrl = "/api/_platform/admin/ad-units"

let private adFormatString (format: AdFormat) : string =
    match format with
    | AdAuto -> "auto"
    | AdRectangle -> "rectangle"
    | AdVertical -> "vertical"
    | AdHorizontal -> "horizontal"
    | AdFluid layoutKey -> sprintf "fluid (%s)" layoutKey

let private parseAdFormat (raw: string) : AdFormat =
    match raw with
    | "rectangle" -> AdRectangle
    | "vertical" -> AdVertical
    | "horizontal" -> AdHorizontal
    | "fluid" -> AdFluid ""
    | _ -> AdAuto

type private AdUnitDraft = {
    AdClientId: string
    SlotId: string
    Format: string
    StyleCss: string
}

let private emptyDraft (defaultAdClientId: string) : AdUnitDraft = {
    AdClientId = defaultAdClientId
    SlotId = ""
    Format = "auto"
    StyleCss = ""
}

let private draftFromConfig (config: AdSlotConfig) : AdUnitDraft = {
    AdClientId = config.AdClientId
    SlotId = config.SlotId
    Format =
        match config.Format with
        | AdAuto -> "auto"
        | AdRectangle -> "rectangle"
        | AdVertical -> "vertical"
        | AdHorizontal -> "horizontal"
        | AdFluid _ -> "fluid"
    StyleCss = config.Style |> Option.map _.CssStyle |> Option.defaultValue ""
}

let private draftToConfig (draft: AdUnitDraft) : AdSlotConfig = {
    AdClientId = draft.AdClientId
    SlotId = draft.SlotId
    Format = parseAdFormat draft.Format
    Style =
        if String.IsNullOrWhiteSpace draft.StyleCss then
            None
        else
            Some { CssStyle = draft.StyleCss }
}

let private sendAdUnitRequest
    (httpMethod: HttpMethod)
    (url: string)
    (body: string option)
    : Async<Result<string, string>> =
    async {
        try
            let req =
                Http.request url
                |> Http.method httpMethod
                |> Http.header (Headers.contentType "application/json")

            let req =
                match body with
                | Some text -> req |> Http.content (BodyContent.Text text)
                | None -> req

            let! response = req |> Http.send

            if response.statusCode >= 200 && response.statusCode < 300 then
                return Ok response.responseText
            else
                let reason =
                    if String.IsNullOrWhiteSpace response.responseText then
                        sprintf "HTTP %d" response.statusCode
                    else
                        response.responseText

                return Error reason
        with ex ->
            return Error ex.Message
    }

[<ReactComponent>]
let AdUnitConfigBody (defaultAdClientId: string) : ReactElement =
    let state, setState = React.useState (Loading: LoadState<AdSlotConfig list>)
    let draft, setDraft = React.useState (emptyDraft defaultAdClientId)
    let editingSlot, setEditingSlot = React.useState (None: string option)
    let writeError, setWriteError = React.useState (None: string option)
    let saving, setSaving = React.useState false

    let load () =
        setState Loading

        async {
            let! result = fetchJson<AdSlotConfig list> adUnitsUrl (Some [])
            setState result
        }
        |> Async.StartImmediate

    React.useEffectOnce (fun () -> load ())

    let beginEdit (config: AdSlotConfig) =
        setDraft (draftFromConfig config)
        setEditingSlot (Some config.SlotId)
        setWriteError None

    let cancelEdit () =
        setDraft (emptyDraft defaultAdClientId)
        setEditingSlot None
        setWriteError None

    let save () =
        if String.IsNullOrWhiteSpace draft.SlotId then
            setWriteError (Some "Slot id is required.")
        else
            setSaving true
            setWriteError None
            let config = draftToConfig draft
            let payload = Json.serialize config

            let httpMethod, url =
                match editingSlot with
                | Some _slotId -> PUT, sprintf "%s/%s" adUnitsUrl draft.SlotId
                | None -> POST, adUnitsUrl

            async {
                let! result = sendAdUnitRequest httpMethod url (Some payload)
                setSaving false

                match result with
                | Ok _ ->
                    cancelEdit ()
                    load ()
                | Error reason -> setWriteError (Some(sprintf "Save failed: %s" reason))
            }
            |> Async.StartImmediate

    let delete (slotId: string) =
        setSaving true
        setWriteError None

        async {
            let! result = sendAdUnitRequest DELETE (sprintf "%s/%s" adUnitsUrl slotId) None
            setSaving false

            match result with
            | Ok _ -> load ()
            | Error reason -> setWriteError (Some(sprintf "Delete failed: %s" reason))
        }
        |> Async.StartImmediate

    let listSection =
        match state with
        | Loading -> Html.p [ prop.className "text-xs text-slate-500"; prop.text "Loading..." ]
        | LoadError msg -> errorBanner msg
        | Loaded [] -> substrateStub "No ad units configured yet. Use the form below to create one."
        | Loaded configs ->
            Html.div [
                prop.className "border border-slate-200 rounded overflow-hidden mb-3"
                prop.children [
                    Html.table [
                        prop.className "w-full text-xs"
                        prop.children [
                            Html.thead [
                                prop.className "bg-slate-50"
                                prop.children [
                                    Html.tr [
                                        prop.children [
                                            Html.th [
                                                prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                                prop.text "Slot id"
                                            ]
                                            Html.th [
                                                prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                                prop.text "Ad-client id"
                                            ]
                                            Html.th [
                                                prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                                prop.text "Format"
                                            ]
                                            Html.th [
                                                prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                                prop.text "Style"
                                            ]
                                            Html.th [
                                                prop.className "text-right px-3 py-2 font-medium text-slate-600"
                                                prop.text "Actions"
                                            ]
                                        ]
                                    ]
                                ]
                            ]
                            Html.tbody [
                                prop.children (
                                    configs
                                    |> List.map (fun config ->
                                        Html.tr [
                                            prop.className "border-t border-slate-200"
                                            prop.children [
                                                Html.td [
                                                    prop.className "px-3 py-1.5 font-mono"
                                                    prop.text config.SlotId
                                                ]
                                                Html.td [
                                                    prop.className "px-3 py-1.5 font-mono text-slate-500"
                                                    prop.text config.AdClientId
                                                ]
                                                Html.td [
                                                    prop.className "px-3 py-1.5 text-slate-700"
                                                    prop.text (adFormatString config.Format)
                                                ]
                                                Html.td [
                                                    prop.className "px-3 py-1.5 text-slate-500 font-mono"
                                                    prop.text (
                                                        config.Style
                                                        |> Option.map _.CssStyle
                                                        |> Option.defaultValue "—"
                                                    )
                                                ]
                                                Html.td [
                                                    prop.className "px-3 py-1.5 text-right"
                                                    prop.children [
                                                        Html.div [
                                                            prop.className "flex items-center gap-1 justify-end"
                                                            prop.children [
                                                                smallButton "Edit" (fun () -> beginEdit config) saving
                                                                smallButton
                                                                    "Delete"
                                                                    (fun () -> delete config.SlotId)
                                                                    saving
                                                            ]
                                                        ]
                                                    ]
                                                ]
                                            ]
                                        ])
                                )
                            ]
                        ]
                    ]
                ]
            ]

    let labelledInput (label: string) (value: string) (placeholder: string) (onChange: string -> unit) =
        Html.label [
            prop.className "flex flex-col gap-1 text-xs"
            prop.children [
                Html.span [ prop.className "font-medium text-slate-700"; prop.text label ]
                Html.input [
                    prop.className "border border-slate-300 rounded px-2 py-1 text-xs font-mono"
                    prop.placeholder placeholder
                    prop.value value
                    prop.onChange onChange
                ]
            ]
        ]

    let formatSelect =
        Html.label [
            prop.className "flex flex-col gap-1 text-xs"
            prop.children [
                Html.span [ prop.className "font-medium text-slate-700"; prop.text "Format" ]
                Html.select [
                    prop.className "border border-slate-300 rounded px-2 py-1 text-xs"
                    prop.value draft.Format
                    prop.onChange (fun (v: string) -> setDraft { draft with Format = v })
                    prop.children [
                        Html.option [ prop.value "auto"; prop.text "auto" ]
                        Html.option [ prop.value "rectangle"; prop.text "rectangle" ]
                        Html.option [ prop.value "vertical"; prop.text "vertical" ]
                        Html.option [ prop.value "horizontal"; prop.text "horizontal" ]
                        Html.option [ prop.value "fluid"; prop.text "fluid" ]
                    ]
                ]
            ]
        ]

    let formSection =
        Html.div [
            prop.className "border border-slate-200 rounded p-3"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between mb-2"
                    prop.children [
                        Html.h4 [
                            prop.className "text-xs font-semibold text-slate-700"
                            prop.text (
                                match editingSlot with
                                | Some slotId -> sprintf "Edit slot %s" slotId
                                | None -> "Create slot"
                            )
                        ]
                        if editingSlot.IsSome then
                            smallButton "Cancel" cancelEdit saving
                        else
                            Html.none
                    ]
                ]
                Html.div [
                    prop.className "grid grid-cols-2 gap-3"
                    prop.children [
                        labelledInput "Slot id" draft.SlotId "1234567890" (fun v -> setDraft { draft with SlotId = v })
                        labelledInput "Ad-client id" draft.AdClientId "ca-pub-..." (fun v ->
                            setDraft { draft with AdClientId = v })
                        formatSelect
                        labelledInput
                            "Style CSS (optional)"
                            draft.StyleCss
                            "display:block; width:300px; height:250px;"
                            (fun v -> setDraft { draft with StyleCss = v })
                    ]
                ]
                Html.div [
                    prop.className "flex items-center gap-2 mt-3"
                    prop.children [
                        smallButton
                            (if saving then
                                 "Saving..."
                             else
                                 (if editingSlot.IsSome then "Update" else "Create"))
                            save
                            saving
                        smallButton
                            "Refresh"
                            load
                            (saving
                             || (match state with
                                 | Loading -> true
                                 | _ -> false))
                    ]
                ]
                match writeError with
                | Some msg -> Html.div [ prop.className "mt-2"; prop.children [ errorBanner msg ] ]
                | None -> Html.none
            ]
        ]

    Html.div [ prop.children [ listSection; formSection ] ]

let adUnitConfig (config: ClientConfig) : ReactElement =
    match config.AdPanel with
    | NoAdPanel ->
        widgetCard
            "Ad units"
            "AdSense slot configuration"
            (substrateStub "AdPanel is disabled (ClientConfig.AdPanel = NoAdPanel) — no ad units to configure.")
    | EnabledAdPanel panelConfig ->
        widgetCard "Ad units" "AdSense slot configuration" (AdUnitConfigBody(panelConfig.DefaultAdClientId))

// ─── Widget 4 — Premium-user list ─────────────────────────────────
// GET /api/_platform/admin/premium-users → (string * PremiumStatus) list.
// Default NoOpUserClaims returns []; the widget renders an empty
// state so deployments without a wired claim reader degrade cleanly.

let private premiumUsersUrl = "/api/_platform/admin/premium-users"

let private premiumStatusDetails (status: PremiumStatus) : (string * string * string) =
    match status with
    | NotPremium -> "—", "—", "—"
    | Premium(grantedAt, grantedBy, reason) ->
        grantedAt.ToString("yyyy-MM-dd HH:mm:ss"), grantedBy, (reason |> Option.defaultValue "—")

let private premiumUserRow (userId: string, status: PremiumStatus) : ReactElement =
    let grantedAt, grantedBy, reason = premiumStatusDetails status

    Html.tr [
        prop.className "border-t border-slate-200"
        prop.children [
            Html.td [ prop.className "px-3 py-1.5 text-xs font-mono"; prop.text userId ]
            Html.td [
                prop.className "px-3 py-1.5 text-xs text-slate-500 font-mono whitespace-nowrap"
                prop.text grantedAt
            ]
            Html.td [ prop.className "px-3 py-1.5 text-xs text-slate-700"; prop.text grantedBy ]
            Html.td [ prop.className "px-3 py-1.5 text-xs text-slate-500"; prop.text reason ]
        ]
    ]

let private premiumUsersTable (users: (string * PremiumStatus) list) : ReactElement =
    Html.div [
        prop.className "border border-slate-200 rounded overflow-hidden"
        prop.children [
            Html.table [
                prop.className "w-full text-xs"
                prop.children [
                    Html.thead [
                        prop.className "bg-slate-50"
                        prop.children [
                            Html.tr [
                                prop.children [
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "User id"
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "Granted at"
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "Granted by"
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-slate-600"
                                        prop.text "Reason"
                                    ]
                                ]
                            ]
                        ]
                    ]
                    Html.tbody [ prop.children (users |> List.map premiumUserRow) ]
                ]
            ]
        ]
    ]

[<ReactComponent>]
let PremiumUserListBody () : ReactElement =
    let state, setState =
        React.useState (Loading: LoadState<(string * PremiumStatus) list>)

    let load () =
        setState Loading

        async {
            let! result = fetchJson<(string * PremiumStatus) list> premiumUsersUrl (Some [])
            setState result
        }
        |> Async.StartImmediate

    React.useEffectOnce (fun () -> load ())

    let isLoading =
        match state with
        | Loading -> true
        | _ -> false

    let controls =
        Html.div [
            prop.className "flex items-center gap-2 mb-3"
            prop.children [
                smallButton (if isLoading then "Refreshing..." else "Refresh") load isLoading
            ]
        ]

    let body =
        match state with
        | Loading -> Html.p [ prop.className "text-xs text-slate-500"; prop.text "Loading..." ]
        | LoadError msg -> errorBanner msg
        | Loaded [] ->
            substrateStub
                "No premium users granted yet. Grant via POST /api/_platform/users/{userId}/premium (Phase 62)."
        | Loaded users -> premiumUsersTable users

    Html.div [ prop.children [ controls; body ] ]

let premiumUserList (config: ClientConfig) : ReactElement =
    match config.PremiumModel with
    | AnonymousFirst -> widgetCard "Premium users" "Operator-granted premium claims" (PremiumUserListBody())

// ─── Composition entry ────────────────────────────────────────────
// Render every widget that applies under the current ClientConfig +
// PlatformAdminProfile combination.

let render (config: ClientConfig) : ReactElement =
    match config.PlatformAdminProfile with
    | StandardPlatformAdminProfile -> Html.none
    | PublicUtilityPlatformAdminProfile ->
        Html.div [
            prop.className "p-4"
            prop.children [
                Html.h2 [
                    prop.className "text-lg font-semibold text-slate-900 mb-4"
                    prop.text "Public utility"
                ]
                trafficDashboard config
                rateLimitEventLog config
                adUnitConfig config
                premiumUserList config
            ]
        ]