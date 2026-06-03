// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module WebhookAdminUI

open System
open ToolUp.Elmish
open Feliz
open Fable.Core
open Fable.Core.JsInterop
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Model ───────────────────────────────────────────────────────────

/// Status line rendered alongside per-subscription actions (pause /
/// resume / delete / test-fire). Cleared by `DismissPerSubStatus` or
/// implicitly on the next refresh.
type FormStatus =
    | Idle
    | Working
    | Done of string
    | Failed of string

type Model = {
    /// Subscriptions in the caller's scope, sorted client-side by
    /// `CreatedAt` desc. Empty until `LoadSubscriptions` completes.
    Subscriptions: WebhookSubscription list
    /// Detail-pane focus. Auto-set to the first subscription on load.
    SelectedId: Guid option
    /// True once the initial fetch has completed, regardless of payload.
    Loaded: bool
    /// List-level error banner.
    Error: string option
    /// Per-subscription action status. Keyed by `SubscriptionId`.
    Status: Map<Guid, FormStatus>
    /// Recent deliveries per subscription. Lazy — populated on detail
    /// open. Cleared on subscription delete.
    Deliveries: Map<Guid, WebhookDelivery list>
    /// Most recent test-fire result per subscription. Surfaced under the
    /// Test fire button.
    TestResults: Map<Guid, WebhookTestResult>
    /// Newly created subscription's *unmasked* secret. Shown once in
    /// the detail pane behind a "I have copied this" dismiss button —
    /// after dismiss, the list refetches with the masked representation
    /// and the secret is gone forever (rotation = delete + recreate).
    NewlyCreatedSecret: (Guid * string) option
}

type Msg =
    | LoadSubscriptions
    | SubscriptionsLoaded of Result<WebhookSubscription list, string>
    | SelectSubscription of Guid
    | CreateSubmit of CreateWebhookRequest
    | CreateCompleted of Result<WebhookSubscription, string>
    | UpdateStatusSubmit of Guid * WebhookStatus
    | StatusUpdateCompleted of Guid * Result<unit, string>
    | DeleteSubmit of Guid
    | DeleteCompleted of Guid * Result<unit, string>
    | TestFireSubmit of Guid
    | TestFireCompleted of Guid * Result<WebhookTestResult, string>
    | LoadDeliveries of Guid
    | DeliveriesLoaded of Guid * Result<WebhookDelivery list, string>
    | DismissCreatedSecret
    | DismissError
    | DismissPerSubStatus of Guid

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
let private webhookApi: IWebhookApi =
    Api.makeProxy<IWebhookApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init ────────────────────────────────────────────────────────────

let private loadSubscriptionsCmd () =
    Cmd.OfRemoting.call webhookApi.ListSubscriptions () SubscriptionsLoaded (fun e ->
        SubscriptionsLoaded(Error e.Message))

let init () =
    let model = {
        Subscriptions = []
        SelectedId = None
        Loaded = false
        Error = None
        Status = Map.empty
        Deliveries = Map.empty
        TestResults = Map.empty
        NewlyCreatedSecret = None
    }

    model, loadSubscriptionsCmd ()

// ─── Helpers ─────────────────────────────────────────────────────────

let private loadDeliveriesCmd (id: Guid) =
    Cmd.OfRemoting.call webhookApi.ListDeliveries id (fun result -> DeliveriesLoaded(id, result)) (fun e ->
        DeliveriesLoaded(id, Error e.Message))

let private sortByCreatedDesc (subs: WebhookSubscription list) =
    subs |> List.sortByDescending _.CreatedAt

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) =
    match msg with
    | LoadSubscriptions -> model, loadSubscriptionsCmd ()

    | SubscriptionsLoaded(Ok subs) ->
        let sorted = sortByCreatedDesc subs

        // Preserve the existing selection when possible; fall back to
        // the first row so admins land on a populated detail pane.
        let selected =
            match model.SelectedId with
            | Some id when sorted |> List.exists (fun s -> s.SubscriptionId = id) -> Some id
            | _ -> sorted |> List.tryHead |> Option.map _.SubscriptionId

        let cmd =
            match selected with
            | Some id when not (model.Deliveries |> Map.containsKey id) -> loadDeliveriesCmd id
            | _ -> Cmd.none

        {
            model with
                Subscriptions = sorted
                SelectedId = selected
                Loaded = true
                Error = None
        },
        cmd

    | SubscriptionsLoaded(Error err) ->
        {
            model with
                Loaded = true
                Error = Some err
        },
        Cmd.none

    | SelectSubscription id ->
        let cmd =
            if model.Deliveries |> Map.containsKey id then
                Cmd.none
            else
                loadDeliveriesCmd id

        { model with SelectedId = Some id }, cmd

    | CreateSubmit req ->
        let cmd =
            Cmd.OfRemoting.call webhookApi.CreateSubscription req CreateCompleted (fun e ->
                CreateCompleted(Error e.Message))

        model, cmd

    | CreateCompleted(Ok sub) ->
        // Hold the unmasked secret in the model so the detail pane can
        // surface it once. The server-side list refetch will return the
        // masked representation, so the secret only exists in this
        // session until `DismissCreatedSecret` clears it.
        {
            model with
                NewlyCreatedSecret = Some(sub.SubscriptionId, sub.Secret)
                SelectedId = Some sub.SubscriptionId
        },
        loadSubscriptionsCmd ()

    | CreateCompleted(Error err) -> { model with Error = Some err }, Cmd.none

    | UpdateStatusSubmit(id, status) ->
        let cmd =
            Cmd.OfRemoting.call
                webhookApi.UpdateStatus
                (id, status)
                (fun result -> StatusUpdateCompleted(id, result))
                (fun e -> StatusUpdateCompleted(id, Error e.Message))

        {
            model with
                Status = model.Status |> Map.add id Working
        },
        cmd

    | StatusUpdateCompleted(id, Ok()) ->
        {
            model with
                Status = model.Status |> Map.add id (Done "Status updated.")
        },
        loadSubscriptionsCmd ()

    | StatusUpdateCompleted(id, Error err) ->
        {
            model with
                Status = model.Status |> Map.add id (Failed err)
        },
        Cmd.none

    | DeleteSubmit id ->
        let cmd =
            Cmd.OfRemoting.call webhookApi.DeleteSubscription id (fun result -> DeleteCompleted(id, result)) (fun e ->
                DeleteCompleted(id, Error e.Message))

        {
            model with
                Status = model.Status |> Map.add id Working
        },
        cmd

    | DeleteCompleted(id, Ok()) ->
        // Drop cached deliveries / test results / status for the removed
        // subscription so a future create with the same id (vanishingly
        // unlikely — Guids — but cheap) starts clean.
        let nextSelected =
            if model.SelectedId = Some id then
                None
            else
                model.SelectedId

        {
            model with
                Deliveries = model.Deliveries |> Map.remove id
                TestResults = model.TestResults |> Map.remove id
                Status = model.Status |> Map.remove id
                SelectedId = nextSelected
        },
        loadSubscriptionsCmd ()

    | DeleteCompleted(id, Error err) ->
        {
            model with
                Status = model.Status |> Map.add id (Failed err)
        },
        Cmd.none

    | TestFireSubmit id ->
        let cmd =
            Cmd.OfRemoting.call webhookApi.TestFire id (fun result -> TestFireCompleted(id, result)) (fun e ->
                TestFireCompleted(id, Error e.Message))

        {
            model with
                Status = model.Status |> Map.add id Working
        },
        cmd

    | TestFireCompleted(id, Ok result) ->
        let label =
            match result.StatusCode with
            | Some code -> $"Test fired — HTTP {code} in {result.LatencyMs} ms."
            | None ->
                match result.Error with
                | Some e -> $"Test fired — failed: {e}"
                | None -> "Test fired."

        {
            model with
                TestResults = model.TestResults |> Map.add id result
                Status = model.Status |> Map.add id (Done label)
        },
        // Reload deliveries so the row appears in the log immediately.
        loadDeliveriesCmd id

    | TestFireCompleted(id, Error err) ->
        {
            model with
                Status = model.Status |> Map.add id (Failed err)
        },
        Cmd.none

    | LoadDeliveries id -> model, loadDeliveriesCmd id

    | DeliveriesLoaded(id, Ok rows) ->
        {
            model with
                Deliveries = model.Deliveries |> Map.add id rows
        },
        Cmd.none

    | DeliveriesLoaded(_, Error _) ->
        // Silently swallow — the detail pane shows "no deliveries yet"
        // when the map is empty; a banner here would be noisy.
        model, Cmd.none

    | DismissCreatedSecret -> { model with NewlyCreatedSecret = None }, Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

    | DismissPerSubStatus id ->
        {
            model with
                Status = model.Status |> Map.remove id
        },
        Cmd.none

// ─── Helpers (display) ───────────────────────────────────────────────

let private statusBadge (status: WebhookStatus) =
    let cls, label =
        match status with
        | WebhookStatus.Active -> "bg-green-100 text-green-700", "Active"
        | WebhookStatus.Paused -> "bg-yellow-100 text-yellow-800", "Paused"
        | WebhookStatus.Disabled -> "bg-red-100 text-red-700", "Disabled"

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded {cls}"
        prop.text label
    ]

let private outcomeLabel (outcome: WebhookDeliveryOutcome) =
    match outcome with
    | WebhookDeliveryOutcome.Success(code, ms) -> $"OK {code} ({ms} ms)", "text-green-700"
    | WebhookDeliveryOutcome.Failure(Some code, err, ms) -> $"HTTP {code}: {err} ({ms} ms)", "text-yellow-700"
    | WebhookDeliveryOutcome.Failure(None, err, ms) -> $"failed: {err} ({ms} ms)", "text-yellow-700"
    | WebhookDeliveryOutcome.DeadLettered err -> $"dead-lettered: {err}", "text-red-700"

let private statusBanner (status: FormStatus) (onDismiss: unit -> unit) =
    match status with
    | Idle -> Html.none
    | Working -> Html.div [ prop.className "mt-2 text-sm text-gray-500"; prop.text "Working..." ]
    | Done msg ->
        Html.div [
            prop.className
                "mt-2 p-2 bg-green-50 border border-green-200 rounded text-green-700 text-sm flex items-center justify-between"
            prop.children [
                Html.span [ prop.text msg ]
                Html.button [
                    prop.className "text-xs text-green-700 hover:underline"
                    prop.text "dismiss"
                    prop.onClick (fun _ -> onDismiss ())
                ]
            ]
        ]
    | Failed err ->
        Html.div [
            prop.className
                "mt-2 p-2 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
            prop.children [
                Html.span [ prop.text err ]
                Html.button [
                    prop.className "text-xs text-red-700 hover:underline"
                    prop.text "dismiss"
                    prop.onClick (fun _ -> onDismiss ())
                ]
            ]
        ]

// ─── Create form ─────────────────────────────────────────────────────

/// Local React state for the create form so free-typing inputs don't
/// round-trip through Elmish on every keystroke (CLAUDE.md MVU rule).
/// `onSubmit` fires only on the Create button click.
[<ReactComponent>]
let private CreateForm (onSubmit: CreateWebhookRequest -> unit) =
    let target, setTarget = React.useState ""
    let secret, setSecret = React.useState ""
    let eventTypes, setEventTypes = React.useState ""

    let generateSecret () =
        // 32 random bytes, base64-encoded — ≈ 44-char string. Browser
        // crypto so the value never leaves the client until the admin
        // posts the create. Single emitJsExpr to avoid hand-rolling
        // Uint8Array bindings.
        let secret: string =
            emitJsExpr
                ()
                "(()=>{const a=new Uint8Array(32);crypto.getRandomValues(a);let s='';for(const b of a)s+=String.fromCharCode(b);return btoa(s);})()"

        setSecret secret

    let parseEventTypes (raw: string) =
        raw.Split([| ','; ';'; '\n' |])
        |> Array.map _.Trim()
        |> Array.filter (fun s -> s.Length > 0)
        |> Array.toList

    let canSubmit = not (String.IsNullOrWhiteSpace target) && secret.Length > 0

    let submit () =
        if canSubmit then
            onSubmit {
                TargetUrl = target.Trim()
                Secret = secret
                EventTypes = parseEventTypes eventTypes
            }

            setTarget ""
            setSecret ""
            setEventTypes ""

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4 mb-4"
        prop.children [
            Html.h3 [ prop.className "text-sm font-semibold mb-3"; prop.text "Create subscription" ]

            Html.div [
                prop.className "mb-3"
                prop.children [
                    Html.label [
                        prop.className "block text-xs font-medium text-gray-700 mb-1"
                        prop.text "Target URL"
                    ]
                    Html.input [
                        prop.type' "url"
                        prop.value target
                        prop.placeholder "https://hooks.example.com/services/..."
                        prop.onChange (fun (v: string) -> setTarget v)
                        prop.className
                            "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand w-full"
                    ]
                ]
            ]

            Html.div [
                prop.className "mb-3"
                prop.children [
                    Html.label [
                        prop.className "block text-xs font-medium text-gray-700 mb-1"
                        prop.text "Secret (HMAC-SHA256 signing key)"
                    ]
                    Html.div [
                        prop.className "flex gap-2"
                        prop.children [
                            Html.input [
                                prop.type' "text"
                                prop.value secret
                                prop.placeholder "≥ 32 char random string"
                                prop.onChange (fun (v: string) -> setSecret v)
                                prop.className
                                    "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand flex-1 font-mono text-xs"
                            ]
                            Html.button [
                                prop.className
                                    "px-3 py-2 text-xs rounded-lg border border-border hover:bg-gray-50 transition-colors"
                                prop.text "Generate"
                                prop.onClick (fun _ -> generateSecret ())
                            ]
                        ]
                    ]
                    Html.p [
                        prop.className "text-xs text-gray-500 mt-1"
                        prop.text
                            "Copy this value into the receiving service. Rotation requires delete + recreate (no in-place rotation API)."
                    ]
                ]
            ]

            Html.div [
                prop.className "mb-3"
                prop.children [
                    Html.label [
                        prop.className "block text-xs font-medium text-gray-700 mb-1"
                        prop.text "Event types (comma-separated, blank = all)"
                    ]
                    Html.input [
                        prop.type' "text"
                        prop.value eventTypes
                        prop.placeholder "FileUploaded, AnalysisCompleted"
                        prop.onChange (fun (v: string) -> setEventTypes v)
                        prop.className
                            "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand w-full font-mono text-xs"
                    ]
                ]
            ]

            Html.div [
                prop.className "flex gap-2"
                prop.children [
                    Html.button [
                        prop.className [
                            "px-4 py-2 text-sm rounded-lg text-white transition-colors"
                            if canSubmit then
                                "bg-brand hover:bg-brand-dark cursor-pointer"
                            else
                                "bg-gray-300 cursor-not-allowed"
                        ]
                        prop.disabled (not canSubmit)
                        prop.text "Create"
                        prop.onClick (fun _ -> submit ())
                    ]
                ]
            ]
        ]
    ]

// ─── Detail pane ─────────────────────────────────────────────────────

let private newlyCreatedBanner (sub: WebhookSubscription) (secret: string) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "mb-4 p-3 bg-yellow-50 border border-yellow-300 rounded"
        prop.children [
            Html.h4 [
                prop.className "text-sm font-semibold text-yellow-800 mb-1"
                prop.text "Copy this secret now"
            ]
            Html.p [
                prop.className "text-xs text-yellow-700 mb-2"
                prop.text "This is the only time the secret will be shown. After dismiss, you cannot retrieve it."
            ]
            Html.div [
                prop.className "p-2 bg-white border border-yellow-200 rounded font-mono text-xs break-all mb-2"
                prop.text secret
            ]
            Html.button [
                prop.className "px-3 py-1 text-xs bg-yellow-600 text-white rounded hover:bg-yellow-700"
                prop.text "I have copied this"
                prop.onClick (fun _ -> dispatch DismissCreatedSecret)
            ]
        ]
    ]

let private subscriptionDetail (model: Model) (sub: WebhookSubscription) (dispatch: Msg -> unit) =
    let status =
        model.Status |> Map.tryFind sub.SubscriptionId |> Option.defaultValue Idle

    let deliveries =
        model.Deliveries |> Map.tryFind sub.SubscriptionId |> Option.defaultValue []

    let secretBanner =
        match model.NewlyCreatedSecret with
        | Some(id, secret) when id = sub.SubscriptionId -> newlyCreatedBanner sub secret dispatch
        | _ -> Html.none

    let actionButtons =
        let toggleStatusButton =
            match sub.Status with
            | WebhookStatus.Active ->
                Html.button [
                    prop.className "px-3 py-1.5 text-xs rounded-lg border border-border hover:bg-gray-50"
                    prop.text "Pause"
                    prop.onClick (fun _ -> dispatch (UpdateStatusSubmit(sub.SubscriptionId, WebhookStatus.Paused)))
                ]
            | WebhookStatus.Paused ->
                Html.button [
                    prop.className "px-3 py-1.5 text-xs rounded-lg border border-border hover:bg-gray-50"
                    prop.text "Resume"
                    prop.onClick (fun _ -> dispatch (UpdateStatusSubmit(sub.SubscriptionId, WebhookStatus.Active)))
                ]
            | WebhookStatus.Disabled ->
                Html.button [
                    prop.className "px-3 py-1.5 text-xs rounded-lg border border-border hover:bg-gray-50"
                    prop.text "Re-enable"
                    prop.onClick (fun _ -> dispatch (UpdateStatusSubmit(sub.SubscriptionId, WebhookStatus.Active)))
                ]

        Html.div [
            prop.className "flex gap-2 flex-wrap"
            prop.children [
                toggleStatusButton
                Html.button [
                    prop.className "px-3 py-1.5 text-xs rounded-lg border border-border hover:bg-gray-50"
                    prop.text "Test fire"
                    prop.onClick (fun _ -> dispatch (TestFireSubmit sub.SubscriptionId))
                ]
                Html.button [
                    prop.className "px-3 py-1.5 text-xs rounded-lg border border-red-300 text-red-700 hover:bg-red-50"
                    prop.text "Delete"
                    prop.onClick (fun _ ->
                        if Browser.Dom.window.confirm "Delete this subscription? Delivery log will be pruned." then
                            dispatch (DeleteSubmit sub.SubscriptionId))
                ]
            ]
        ]

    let metadataBlock =
        let row (label: string) (value: ReactElement) =
            Html.div [
                prop.className "flex items-baseline gap-2 mb-1"
                prop.children [
                    Html.span [ prop.className "text-xs text-gray-500 w-32 shrink-0"; prop.text label ]
                    value
                ]
            ]

        Html.div [
            prop.className "text-sm"
            prop.children [
                row
                    "Subscription Id"
                    (Html.span [ prop.className "font-mono text-xs"; prop.text (string sub.SubscriptionId) ])
                row "Target" (Html.span [ prop.className "font-mono text-xs break-all"; prop.text sub.TargetUrl ])
                row
                    "Event types"
                    (Html.span [
                        prop.className "text-xs"
                        prop.text (
                            if List.isEmpty sub.EventTypes then
                                "(all)"
                            else
                                String.concat ", " sub.EventTypes
                        )
                    ])
                row "Status" (statusBadge sub.Status)
                row
                    "Consecutive failures"
                    (Html.span [ prop.className "text-xs"; prop.text (string sub.ConsecutiveFailures) ])
                row
                    "Created"
                    (Html.span [
                        prop.className "text-xs"
                        prop.text (sub.CreatedAt.ToString("u") + " by " + sub.CreatedBy)
                    ])
            ]
        ]

    let deliveryTable =
        Html.div [
            prop.className "mt-4"
            prop.children [
                Html.h4 [ prop.className "text-sm font-semibold mb-2"; prop.text "Recent deliveries" ]
                if List.isEmpty deliveries then
                    Html.p [
                        prop.className "text-xs text-gray-500"
                        prop.text "No deliveries yet. Use Test fire to send a synthetic event."
                    ]
                else
                    Html.div [
                        prop.className "border border-border rounded-lg overflow-hidden"
                        prop.children [
                            Html.table [
                                prop.className "w-full text-xs"
                                prop.children [
                                    Html.thead [
                                        prop.className "bg-gray-50"
                                        prop.children [
                                            Html.tr [
                                                prop.children [
                                                    Html.th [
                                                        prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                        prop.text "Attempted"
                                                    ]
                                                    Html.th [
                                                        prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                        prop.text "Attempt"
                                                    ]
                                                    Html.th [
                                                        prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                        prop.text "Outcome"
                                                    ]
                                                    Html.th [
                                                        prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                        prop.text "Event Id"
                                                    ]
                                                ]
                                            ]
                                        ]
                                    ]
                                    Html.tbody [
                                        prop.children [
                                            for row in deliveries ->
                                                let label, cls = outcomeLabel row.Outcome

                                                Html.tr [
                                                    prop.className "border-t border-border"
                                                    prop.children [
                                                        Html.td [
                                                            prop.className "px-3 py-2 font-mono"
                                                            prop.text (row.AttemptedAt.ToString("u"))
                                                        ]
                                                        Html.td [
                                                            prop.className "px-3 py-2"
                                                            prop.text (string row.Attempt)
                                                        ]
                                                        Html.td [ prop.className $"px-3 py-2 {cls}"; prop.text label ]
                                                        Html.td [
                                                            prop.className "px-3 py-2 font-mono text-gray-500"
                                                            prop.text (
                                                                row.EventId
                                                                |> Option.map (fun g -> g.ToString("N").Substring(0, 8))
                                                                |> Option.defaultValue "—"
                                                            )
                                                        ]
                                                    ]
                                                ]
                                        ]
                                    ]
                                ]
                            ]
                        ]
                    ]
            ]
        ]

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4"
        prop.children [
            secretBanner
            metadataBlock
            Html.div [
                prop.className "mt-4"
                prop.children [
                    actionButtons
                    statusBanner status (fun () -> dispatch (DismissPerSubStatus sub.SubscriptionId))
                ]
            ]
            deliveryTable
        ]
    ]

// ─── Sidebar (subscriptions list) ────────────────────────────────────

let private sidebar (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "w-72 border-r border-border bg-gray-50 p-4 overflow-y-auto"
        prop.children [
            CreateForm(fun req -> dispatch (CreateSubmit req))

            Html.h3 [
                prop.className "text-xs font-semibold uppercase tracking-wide text-gray-500 mb-2"
                prop.text "Subscriptions"
            ]

            if not model.Loaded then
                Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading..." ]
            elif List.isEmpty model.Subscriptions then
                Html.p [ prop.className "text-sm text-gray-500"; prop.text "No subscriptions yet." ]
            else
                Html.div [
                    prop.className "flex flex-col gap-1"
                    prop.children [
                        for sub in model.Subscriptions ->
                            let isSelected = model.SelectedId = Some sub.SubscriptionId

                            Html.button [
                                prop.className [
                                    "text-left px-3 py-2 rounded border"
                                    if isSelected then
                                        "bg-brand text-white border-brand"
                                    else
                                        "hover:bg-gray-100 text-gray-700 border-transparent"
                                ]
                                prop.onClick (fun _ -> dispatch (SelectSubscription sub.SubscriptionId))
                                prop.children [
                                    Html.div [
                                        prop.className "text-xs font-mono break-all mb-1"
                                        prop.text sub.TargetUrl
                                    ]
                                    Html.div [
                                        prop.className [
                                            "text-xs"
                                            if isSelected then "text-white/80" else "text-gray-500"
                                        ]
                                        prop.text (
                                            match sub.Status with
                                            | WebhookStatus.Active -> "Active"
                                            | WebhookStatus.Paused -> "Paused"
                                            | WebhookStatus.Disabled -> "Disabled"
                                        )
                                    ]
                                ]
                            ]
                    ]
                ]
        ]
    ]

let private detailPane (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            match model.SelectedId with
            | None ->
                Html.p [
                    prop.className "text-sm text-gray-500"
                    prop.text (
                        if model.Loaded then
                            "Create a subscription, or select one to view details."
                        else
                            "Loading subscriptions..."
                    )
                ]
            | Some id ->
                match model.Subscriptions |> List.tryFind (fun s -> s.SubscriptionId = id) with
                | Some sub -> subscriptionDetail model sub dispatch
                | None ->
                    Html.p [
                        prop.className "text-sm text-gray-500"
                        prop.text "Subscription not found in this scope."
                    ]
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let errorBanner =
        match model.Error with
        | Some msg ->
            Html.div [
                prop.className
                    "m-3 p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
                prop.children [
                    Html.span [ prop.text msg ]
                    Html.button [
                        prop.className "text-xs text-red-600 hover:underline"
                        prop.text "dismiss"
                        prop.onClick (fun _ -> dispatch DismissError)
                    ]
                ]
            ]
        | None -> Html.none

    let body =
        Html.div [
            prop.className "flex h-full min-h-0"
            prop.children [ sidebar model dispatch; detailPane model dispatch ]
        ]

    // 0.5.6 — FullWidth render. Error banner stacks above the body.
    Html.div [
        prop.className "flex flex-col gap-3 h-full"
        prop.children [ errorBanner; body ]
    ]

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in webhook admin as an `ErasedModule`. The shell's
/// `prepareModules` injects this in any non-Anonymous mode — Anonymous
/// has no persistent scope and the API short-circuits with an error.
let create (config: WebhookAdminConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Webhooks"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.webhook

    let route = "/" + name.ToLower().Replace(" ", "-")

    // SDK-built-in — reserved under the `_sdk.` Id namespace so it can
    // never collide with an app's RBAC-managed `ServerConfig.ModuleNames`.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.WebhookAdmin"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Admin"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register