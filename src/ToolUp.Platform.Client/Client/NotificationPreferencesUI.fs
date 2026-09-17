// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module NotificationPreferencesUI

open System
open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Phase 441 — notification preference centre ─────────────────────
//
// The built-in settings module over `INotificationPreferenceApi`: a
// category × channel matrix (rows = the categories the deployment's
// modules declared, columns = the channel families it can deliver on),
// a delivery picker per cell (immediately / hourly · daily · weekly
// digest / muted), and a quiet-hours window. Mirrors the
// `ServiceAccountUI` conventions: one API proxy, a `Loaded` /
// `Busy` / `Error` model, catalog-driven text, `ClientModule.create`
// registration.
//
// **The matrix is data the server declares, never a list the client
// knows.** The SDK ships no category (GP 9), so a deployment with none
// declared renders the empty state rather than an invented row; a
// non-suppressible category renders as a locked row, so the user can
// see what will always reach them.
//
// **Gating.** No `withNavRole` filter — every signed-in person owns
// their own preferences. The server refuses anonymous and machine callers.

// ─── Model ───────────────────────────────────────────────────────────

/// Elmish model for the preference centre.
type Model = {
    /// The declared matrix + the record as last loaded from the server.
    View: NotificationPreferenceView option
    /// The record being edited; saved wholesale on `Save`.
    Draft: UserNotificationPreferences
    /// The first load has completed (with a view or an error).
    Loaded: bool
    /// A load or save is in flight.
    Busy: bool
    /// Set after a successful save; cleared on the next edit.
    Saved: bool
    /// The last load / save error, until dismissed.
    Error: string option
}

/// Elmish messages for the preference centre.
type Msg =
    /// (Re)load the matrix and the caller's record.
    | Load
    /// The load completed.
    | ViewLoaded of Result<NotificationPreferenceView, string>
    /// Change one cell of the draft.
    | SetCell of category: string * channel: PreferenceChannel * delivery: DeliveryPreference
    /// Replace the draft's quiet-hours window (None disables it).
    | SetQuietHours of QuietHours option
    /// Validate and save the draft wholesale.
    | Save
    /// The save completed.
    | SaveCompleted of Result<unit, string>
    /// Clear the error banner.
    | DismissError

// ─── API proxy ───────────────────────────────────────────────────────

let private preferenceApi: INotificationPreferenceApi =
    Api.makeProxy<INotificationPreferenceApi> (
        routeBuilder = NotificationPreferenceApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

let private loadCmd () =
    Cmd.OfRemoting.call preferenceApi.GetMyPreferences () ViewLoaded (fun e -> ViewLoaded(Error e.Message))

let private saveCmd (draft: UserNotificationPreferences) =
    Cmd.OfRemoting.call preferenceApi.SaveMyPreferences draft SaveCompleted (fun e -> SaveCompleted(Error e.Message))

// ─── Init / update ───────────────────────────────────────────────────

/// Initial model + the first load.
let init () : Model * Cmd<Msg> =
    {
        View = None
        Draft = UserNotificationPreferences.empty
        Loaded = false
        Busy = true
        Saved = false
        Error = None
    },
    loadCmd ()

/// Pure state transitions; every side effect is a `Cmd`.
let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Load -> { model with Busy = true; Error = None }, loadCmd ()

    | ViewLoaded(Ok view) ->
        {
            model with
                View = Some view
                Draft = view.Preferences
                Loaded = true
                Busy = false
                Error = None
        },
        Cmd.none

    | ViewLoaded(Error err) ->
        {
            model with
                Loaded = true
                Busy = false
                Error = Some err
        },
        Cmd.none

    | SetCell(category, channel, delivery) ->
        {
            model with
                Draft = UserNotificationPreferences.setDelivery category channel delivery model.Draft
                Saved = false
        },
        Cmd.none

    | SetQuietHours quiet ->
        {
            model with
                Draft = { model.Draft with QuietHours = quiet }
                Saved = false
        },
        Cmd.none

    | Save ->
        match UserNotificationPreferences.validate model.Draft with
        | Error err -> { model with Error = Some err }, Cmd.none
        | Ok draft -> { model with Busy = true; Error = None }, saveCmd draft

    | SaveCompleted(Ok()) ->
        {
            model with
                Busy = false
                Saved = true
                View = model.View |> Option.map (fun v -> { v with Preferences = model.Draft })
        },
        Cmd.none

    | SaveCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

// ─── Wire tokens for the cell picker ─────────────────────────────────

/// `<option>` values are wire-shaped tokens, never display text, so a
/// localised label cannot change what is stored.
module private DeliveryToken =
    let ofDelivery (delivery: DeliveryPreference) : string =
        match delivery with
        | Immediate -> "immediate"
        | Muted -> "muted"
        | Digest frequency -> "digest:" + DigestFrequency.toWireString frequency

    let toDelivery (token: string) : DeliveryPreference =
        match token with
        | "muted" -> Muted
        | t when t.StartsWith "digest:" ->
            match DigestFrequency.ofWireString (t.Substring 7) with
            | Some frequency -> Digest frequency
            | None -> Immediate
        | _ -> Immediate

/// `"HH:mm"` ⇄ minutes since midnight for the `<input type="time">`
/// controls.
module private ClockText =
    let ofMinutes (minutes: int) : string =
        let h = minutes / 60
        let m = minutes % 60
        sprintf "%02d:%02d" h m

    let toMinutes (text: string) : int option =
        match text.Split ':' with
        | [| h; m |] ->
            match Int32.TryParse h, Int32.TryParse m with
            | (true, hh), (true, mm) when hh >= 0 && hh < 24 && mm >= 0 && mm < 60 -> Some(hh * 60 + mm)
            | _ -> None
        | _ -> None

// ─── View helpers ────────────────────────────────────────────────────

let private channelLabel (msgs: NotificationPreferencesMessages) (channel: PreferenceChannel) =
    match channel with
    | PreferenceChannel.Email -> msgs.ChannelEmail
    | PreferenceChannel.Sms -> msgs.ChannelSms
    | PreferenceChannel.Push -> msgs.ChannelPush

let private errorBanner (msgs: NotificationPreferencesMessages) (model: Model) (dispatch: Msg -> unit) =
    match model.Error with
    | Some msg ->
        Html.div [
            prop.className
                "mb-4 p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
            prop.children [
                Html.span [ prop.text msg ]
                Html.button [
                    prop.className "text-xs text-red-600 hover:underline"
                    prop.text msgs.Dismiss
                    prop.onClick (fun _ -> dispatch DismissError)
                ]
            ]
        ]
    | None -> Html.none

let private cellPicker
    (msgs: NotificationPreferencesMessages)
    (category: NotificationCategory)
    (channel: PreferenceChannel)
    (draft: UserNotificationPreferences)
    (dispatch: Msg -> unit)
    =
    if not category.Suppressible then
        Html.span [
            prop.className "text-xs text-gray-500 italic"
            prop.text msgs.AlwaysDelivered
        ]
    else
        let current = UserNotificationPreferences.resolve draft category.Id channel

        Html.select [
            prop.value (DeliveryToken.ofDelivery current)
            prop.onChange (fun (v: string) -> dispatch (SetCell(category.Id, channel, DeliveryToken.toDelivery v)))
            prop.className "border border-border rounded-lg px-2 py-1 text-xs"
            prop.children [
                Html.option [
                    prop.value (DeliveryToken.ofDelivery Immediate)
                    prop.text msgs.DeliveryImmediate
                ]
                Html.option [
                    prop.value (DeliveryToken.ofDelivery (Digest Hourly))
                    prop.text msgs.DigestHourly
                ]
                Html.option [
                    prop.value (DeliveryToken.ofDelivery (Digest Daily))
                    prop.text msgs.DigestDaily
                ]
                Html.option [
                    prop.value (DeliveryToken.ofDelivery (Digest Weekly))
                    prop.text msgs.DigestWeekly
                ]
                Html.option [ prop.value (DeliveryToken.ofDelivery Muted); prop.text msgs.DeliveryMuted ]
            ]
        ]

let private matrix
    (msgs: NotificationPreferencesMessages)
    (view: NotificationPreferenceView)
    (draft: UserNotificationPreferences)
    (dispatch: Msg -> unit)
    =
    if List.isEmpty view.Categories then
        Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoCategories ]
    elif List.isEmpty view.Channels then
        Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoChannels ]
    else
        Html.table [
            prop.className "w-full text-sm mb-6"
            prop.children [
                Html.thead [
                    Html.tr [
                        prop.children [
                            Html.th [ prop.className "text-left py-2 pr-4"; prop.text msgs.CategoryColumn ]
                            for channel in view.Channels do
                                Html.th [
                                    prop.key (PreferenceChannel.toWireString channel)
                                    prop.className "text-left py-2 pr-4"
                                    prop.text (channelLabel msgs channel)
                                ]
                        ]
                    ]
                ]
                Html.tbody [
                    for category in view.Categories do
                        Html.tr [
                            prop.key category.Id
                            prop.className "border-t border-border"
                            prop.children [
                                Html.td [
                                    prop.className "py-2 pr-4 align-top"
                                    prop.children [
                                        Html.div [ prop.className "font-medium"; prop.text category.DisplayName ]
                                        if not (String.IsNullOrWhiteSpace category.Description) then
                                            Html.div [
                                                prop.className "text-xs text-gray-500"
                                                prop.text category.Description
                                            ]
                                    ]
                                ]
                                for channel in view.Channels do
                                    Html.td [
                                        prop.key (PreferenceChannel.toWireString channel)
                                        prop.className "py-2 pr-4 align-top"
                                        prop.children [ cellPicker msgs category channel draft dispatch ]
                                    ]
                            ]
                        ]
                ]
            ]
        ]

/// Quiet-hours editor. Local `useState` for the three inputs (MVU
/// text-input discipline: dispatch on apply, not per keystroke).
[<ReactComponent>]
let private QuietHoursForm (current: QuietHours option) (busy: bool) (onApply: QuietHours option -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).NotificationPreferences
    let enabled, setEnabled = React.useState current.IsSome

    let start, setStart =
        React.useState (
            current
            |> Option.map (fun q -> ClockText.ofMinutes q.StartMinute)
            |> Option.defaultValue "22:00"
        )

    let finish, setFinish =
        React.useState (
            current
            |> Option.map (fun q -> ClockText.ofMinutes q.EndMinute)
            |> Option.defaultValue "07:00"
        )

    let zone, setZone =
        React.useState (current |> Option.map _.TimeZoneId |> Option.defaultValue "UTC")

    let apply () =
        if not enabled then
            onApply None
        else
            match ClockText.toMinutes start, ClockText.toMinutes finish with
            | Some s, Some e ->
                onApply (
                    Some {
                        StartMinute = s
                        EndMinute = e
                        TimeZoneId = zone.Trim()
                    }
                )
            | _ -> ()

    Html.div [
        prop.className "border border-border rounded-lg p-4 mb-6"
        prop.children [
            Html.h3 [
                prop.className "text-sm font-semibold mb-1"
                prop.text msgs.QuietHoursHeading
            ]
            Html.p [ prop.className "text-xs text-gray-600 mb-3"; prop.text msgs.QuietHoursBody ]
            Html.label [
                prop.className "flex items-center gap-2 text-sm mb-3"
                prop.children [
                    Html.input [
                        prop.type' "checkbox"
                        prop.isChecked enabled
                        prop.onCheckedChange setEnabled
                    ]
                    Html.span [ prop.text msgs.QuietHoursEnabled ]
                ]
            ]
            Html.div [
                prop.className "flex flex-wrap items-end gap-3"
                prop.children [
                    Html.label [
                        prop.className "text-xs"
                        prop.children [
                            Html.div [ prop.text msgs.QuietHoursStart ]
                            Html.input [
                                prop.type' "time"
                                prop.value start
                                prop.disabled (not enabled)
                                prop.onChange (fun (v: string) -> setStart v)
                                prop.className "border border-border rounded-lg px-2 py-1"
                            ]
                        ]
                    ]
                    Html.label [
                        prop.className "text-xs"
                        prop.children [
                            Html.div [ prop.text msgs.QuietHoursEnd ]
                            Html.input [
                                prop.type' "time"
                                prop.value finish
                                prop.disabled (not enabled)
                                prop.onChange (fun (v: string) -> setFinish v)
                                prop.className "border border-border rounded-lg px-2 py-1"
                            ]
                        ]
                    ]
                    Html.label [
                        prop.className "text-xs"
                        prop.children [
                            Html.div [ prop.text msgs.QuietHoursTimeZone ]
                            Html.input [
                                prop.type' "text"
                                prop.value zone
                                prop.disabled (not enabled)
                                prop.onChange (fun (v: string) -> setZone v)
                                prop.className "border border-border rounded-lg px-2 py-1"
                            ]
                        ]
                    ]
                    Html.button [
                        prop.className "text-xs px-3 py-1 border border-border rounded-lg hover:bg-gray-50"
                        prop.disabled busy
                        prop.text msgs.ApplyQuietHours
                        prop.onClick (fun _ -> apply ())
                    ]
                ]
            ]
        ]
    ]

/// Page body. A `[<ReactComponent>]` so the message-catalog hook has a
/// component boundary of its own.
[<ReactComponent>]
let private NotificationPreferencesBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).NotificationPreferences

    Html.div [
        prop.className "p-6 max-w-4xl"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
            Html.p [ prop.className "text-sm text-gray-600 mb-4"; prop.text msgs.Subheading ]
            errorBanner msgs model dispatch
            (match model.View with
             | None when not model.Loaded -> Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.Loading ]
             | None -> Html.none
             | Some view ->
                 Html.div [
                     prop.children [
                         matrix msgs view model.Draft dispatch
                         QuietHoursForm model.Draft.QuietHours model.Busy (fun q -> dispatch (SetQuietHours q))
                         Html.div [
                             prop.className "flex items-center gap-3"
                             prop.children [
                                 Html.button [
                                     prop.className
                                         "text-sm px-4 py-2 rounded-lg bg-primary text-white hover:opacity-90 disabled:opacity-50"
                                     prop.disabled model.Busy
                                     prop.text (if model.Busy then msgs.Saving else msgs.Save)
                                     prop.onClick (fun _ -> dispatch Save)
                                 ]
                                 if model.Saved then
                                     Html.span [ prop.className "text-xs text-green-700"; prop.text msgs.Saved ]
                             ]
                         ]
                     ]
                 ])
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    NotificationPreferencesBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in preference centre as an `ErasedModule`. Visible
/// to every authenticated person; the server enforces ownership.
let create (config: NotificationPreferencesConfig option) : ErasedModule =
    let name =
        config |> Option.map _.Name |> Option.defaultValue "Notification Preferences"

    let icon =
        config
        |> Option.map _.Icon
        |> Option.defaultValue ToolUp.Platform.Icons.settings

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.NotificationPreferences"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Settings"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register