// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module SessionSecurityUI

open System
open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── Phase 528 — session security (client-tier slice) ────────────────
//
// The user-facing half of the session registry: which devices are signed
// in as me, when each was last seen, and a way to cut any of them — or
// all of them — off.
//
// **Why the list is worth rendering at all, rather than just the
// button.** A bare "sign out everywhere" is a blunt instrument nobody
// reaches for casually, so in practice it is used after something has
// already gone wrong. The list is what turns the feature from a panic
// button into a check: a user who can see "Chrome on Windows, last seen
// two minutes ago" beside "Safari on iOS, last seen in March" can answer
// "is anyone else in my account?" without signing themselves out of
// everything to find out.
//
// **Revoked sessions stay in the list.** `ISessionApi.ListMySessions`
// returns them deliberately, and the view renders them struck through
// rather than filtering them away, because the question a user asks
// immediately after clicking revoke is "did that work?" — and an entry
// that simply vanishes is indistinguishable from a request that failed
// and a list that reloaded.
//
// **Gating.** Authenticated-caller visibility
// (`Visibility.visibleToAuthenticated`) — this is a personal page, not an
// admin one, so every signed-in caller sees their own. The team-admin
// force-revoke (`ISessionApi.RevokeAllForUser`) is deliberately NOT
// surfaced here: it acts on another person's account and belongs beside
// the team-member list, where the admin already has that person in front
// of them, rather than on a page about the caller's own devices.
//
// Opt-in and zero-cost (GP 13): a deployment on the default
// `ServerConfig.SessionRegistry = NoSessionRegistry` never sets
// `ClientConfig.SessionSecurity`, and the API's routes 404 anyway.

// ─── Model ───────────────────────────────────────────────────────────

type Model = {
    /// The caller's sessions as last loaded, server-ordered
    /// (most-recently-seen first).
    Sessions: SessionRecord list
    /// True once the first load has answered, so the view can tell
    /// "still fetching" from "resolved to nothing".
    Loaded: bool
    /// A mutation is in flight — disables every action button so a
    /// double-click cannot fire two revokes.
    Busy: bool
    /// Session ids the user has asked to revoke and not yet confirmed.
    /// Two-step rather than a modal: revoking is destructive but not
    /// dangerous (the worst case is signing yourself out), so an
    /// in-place confirm is proportionate where a dialog would not be.
    PendingRevoke: string option
    /// Sign-out-everywhere is the one action that can lock the user out
    /// of the page they are standing on, so it gets its own confirm.
    PendingRevokeAll: bool
    /// Error banner. Cleared by `DismissError` or the next mutation.
    Error: string option
    /// Transient confirmation after a revoke.
    Status: string option
}

type Msg =
    | Load
    | SessionsLoaded of Result<SessionRecord list, string>
    | AskRevoke of string
    | CancelRevoke
    | ConfirmRevoke of string
    | RevokeCompleted of Result<unit, string>
    | AskRevokeAll
    | CancelRevokeAll
    | ConfirmRevokeAll
    | RevokeAllCompleted of Result<int, string>
    | DismissError
    | DismissStatus

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// WebhookAdminUI.fs. `SessionApi.routeBuilder` is the default
// `/api/{type}/{method}` shape, so no override is needed.
let private sessionApi: ISessionApi =
    Api.makeProxy<ISessionApi> (customOptions = UserSession.withRequestHeaders)

// ─── Commands ────────────────────────────────────────────────────────

let private loadCmd () =
    Cmd.OfRemoting.call sessionApi.ListMySessions () SessionsLoaded (fun e -> SessionsLoaded(Error e.Message))

let private revokeCmd (sessionId: string) =
    Cmd.OfRemoting.call sessionApi.RevokeSession sessionId RevokeCompleted (fun e -> RevokeCompleted(Error e.Message))

let private revokeAllCmd () =
    Cmd.OfRemoting.call sessionApi.RevokeAllMySessions () RevokeAllCompleted (fun e ->
        RevokeAllCompleted(Error e.Message))

// ─── Init ────────────────────────────────────────────────────────────

let init () : Model * Cmd<Msg> =
    {
        Sessions = []
        Loaded = false
        Busy = false
        PendingRevoke = None
        PendingRevokeAll = false
        Error = None
        Status = None
    },
    loadCmd ()

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Load -> { model with Error = None }, loadCmd ()

    | SessionsLoaded(Ok sessions) ->
        {
            model with
                Sessions = sessions
                Loaded = true
                Busy = false
        },
        Cmd.none

    | SessionsLoaded(Error err) ->
        {
            model with
                // `Loaded` stays as it was on a failed refresh: a load
                // error must not flip a populated list into the "you have
                // no sessions" empty state, which is the one reading a
                // security page must never invite.
                Busy = false
                Error = Some err
        },
        Cmd.none

    | AskRevoke sessionId ->
        {
            model with
                PendingRevoke = Some sessionId
                PendingRevokeAll = false
        },
        Cmd.none

    | CancelRevoke -> { model with PendingRevoke = None }, Cmd.none

    | ConfirmRevoke sessionId ->
        {
            model with
                PendingRevoke = None
                Busy = true
                Error = None
                Status = None
        },
        revokeCmd sessionId

    | RevokeCompleted(Ok()) ->
        // Reload rather than patching the local list: the server is the
        // authority on what is now revoked, and a locally-patched entry
        // would look revoked even if a concurrent change said otherwise.
        //
        // A pure reducer has no rendered tree and therefore no `msgs`
        // hook to read — go straight to the English catalog value, per
        // `TeamManagerUI.update`'s recorded pattern (Phase 751).
        {
            model with
                Status = Some MessageCatalog.english.SessionSecurity.RevokeSuccess
        },
        loadCmd ()

    | RevokeCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | AskRevokeAll ->
        {
            model with
                PendingRevokeAll = true
                PendingRevoke = None
        },
        Cmd.none

    | CancelRevokeAll -> { model with PendingRevokeAll = false }, Cmd.none

    | ConfirmRevokeAll ->
        {
            model with
                PendingRevokeAll = false
                Busy = true
                Error = None
                Status = None
        },
        revokeAllCmd ()

    | RevokeAllCompleted(Ok count) ->
        // Report the server's count rather than "done". Zero is a real
        // and useful answer — it means there was nothing left to sign
        // out, which is different from a revocation having happened.
        // Same no-`msgs`-in-a-reducer reasoning as `RevokeCompleted`
        // above; the singular/plural branching lives inside the
        // catalog function itself so a translation owns its own
        // pluralisation rule.
        let message = MessageCatalog.english.SessionSecurity.RevokeAllResult count

        { model with Status = Some message }, loadCmd ()

    | RevokeAllCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

    | DismissStatus -> { model with Status = None }, Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

/// Human-readable "last seen". Deliberately coarse: `LastSeenAt` is
/// advanced at minute granularity or worse by design (GP 12 rule 6), so
/// rendering a precise time would imply an accuracy the substrate does
/// not promise.
///
/// Public non-component render helper (Phase 751) — the arity cannot
/// widen without reading as a removal in the public-API approval
/// baseline, so the message-catalog-aware body is the additive
/// `…With` variant and this delegates with the English catalog,
/// matching `BootDegradation.bannerWith` / `Components.ModuleBoundary.wrapWith`.
let lastSeenLabelWith (msgs: SessionSecurityMessages) (now: DateTimeOffset) (at: DateTimeOffset) : string =
    let elapsed = now - at

    if elapsed < TimeSpan.Zero then
        msgs.JustNow
    elif elapsed < TimeSpan.FromMinutes 2.0 then
        msgs.JustNow
    elif elapsed < TimeSpan.FromHours 1.0 then
        msgs.MinutesAgo(int elapsed.TotalMinutes)
    elif elapsed < TimeSpan.FromDays 1.0 then
        msgs.HoursAgo(int elapsed.TotalHours)
    elif elapsed < TimeSpan.FromDays 30.0 then
        msgs.DaysAgo(int elapsed.TotalDays)
    else
        // A .NET date format specifier, not translatable prose — see
        // the Phase 751 sweep's "date/number format specifiers" scope
        // note. Baking it into a translated template would make the
        // date FORMAT a property of the language.
        at.ToString "d MMM yyyy"

let lastSeenLabel (now: DateTimeOffset) (at: DateTimeOffset) : string =
    lastSeenLabelWith MessageCatalog.english.SessionSecurity now at

let private sessionRow
    (msgs: SessionSecurityMessages)
    (model: Model)
    (dispatch: Msg -> unit)
    (now: DateTimeOffset)
    (record: SessionRecord)
    =
    let active = SessionRecord.isActive record

    let pending = model.PendingRevoke = Some record.SessionId

    Html.div [
        prop.key record.SessionId
        prop.className "flex items-start justify-between gap-4 px-4 py-3 border-b border-border last:border-b-0"
        prop.children [
            Html.div [
                prop.className "min-w-0"
                prop.children [
                    Html.p [
                        prop.className (
                            if active then
                                "text-sm font-medium truncate"
                            else
                                "text-sm font-medium truncate line-through text-gray-400"
                        )
                        prop.text record.DeviceDescriptor
                    ]
                    Html.p [
                        prop.className "text-xs text-gray-500 mt-0.5"
                        prop.text (
                            if active then
                                msgs.DeviceLastSeen record.AuthProvider (lastSeenLabelWith msgs now record.LastSeenAt)
                            else
                                msgs.DeviceSignedOut record.AuthProvider
                        )
                    ]
                ]
            ]
            Html.div [
                prop.className "shrink-0"
                prop.children [
                    if not active then
                        Html.span [ prop.className "text-xs text-gray-400"; prop.text msgs.Revoked ]
                    elif pending then
                        Html.div [
                            prop.className "flex gap-2"
                            prop.children [
                                Html.button [
                                    prop.className "text-xs px-2 py-1 rounded bg-red-600 text-white disabled:opacity-50"
                                    prop.disabled model.Busy
                                    prop.text msgs.Confirm
                                    prop.onClick (fun _ -> dispatch (ConfirmRevoke record.SessionId))
                                ]
                                Html.button [
                                    prop.className "text-xs px-2 py-1 rounded border border-border"
                                    prop.text msgs.Cancel
                                    prop.onClick (fun _ -> dispatch CancelRevoke)
                                ]
                            ]
                        ]
                    else
                        Html.button [
                            prop.className
                                "text-xs px-2 py-1 rounded border border-border hover:bg-gray-50 disabled:opacity-50"
                            prop.disabled model.Busy
                            prop.text msgs.SignOut
                            prop.onClick (fun _ -> dispatch (AskRevoke record.SessionId))
                        ]
                ]
            ]
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

/// Phase 751 — the module body as a React COMPONENT rather than a plain
/// render function, so it has a hook site from which to read the
/// resolved catalog. `view` is invoked inline by the shell's own
/// render, where a hook would join the shell's hook order and break the
/// moment the active module changed; a component of its own has a
/// stable identity and its own. Same distinction `HealthMonitorUI`
/// documents at `HealthMonitorBody`.
[<ReactComponent>]
let private SessionSecurityBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).SessionSecurity
    let now = DateTimeOffset.UtcNow

    let errorBanner =
        match model.Error with
        | None -> Html.none
        | Some err ->
            Html.div [
                prop.className "mb-4 px-3 py-2 rounded bg-red-50 border border-red-200 text-sm text-red-800"
                prop.children [
                    Html.span [ prop.text err ]
                    Html.button [
                        prop.className "ml-3 text-xs underline"
                        prop.text msgs.Dismiss
                        prop.onClick (fun _ -> dispatch DismissError)
                    ]
                ]
            ]

    let statusBanner =
        match model.Status with
        | None -> Html.none
        | Some status ->
            Html.div [
                prop.className "mb-4 px-3 py-2 rounded bg-green-50 border border-green-200 text-sm text-green-800"
                prop.children [
                    Html.span [ prop.text status ]
                    Html.button [
                        prop.className "ml-3 text-xs underline"
                        prop.text msgs.Dismiss
                        prop.onClick (fun _ -> dispatch DismissStatus)
                    ]
                ]
            ]

    let body =
        if not model.Loaded then
            Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.Loading ]
        elif List.isEmpty model.Sessions then
            Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.EmptyState ]
        else
            Html.div [
                prop.className "border border-border rounded-lg overflow-hidden bg-white"
                prop.children (model.Sessions |> List.map (sessionRow msgs model dispatch now))
            ]

    let signOutEverywhere =
        if List.isEmpty (model.Sessions |> List.filter SessionRecord.isActive) then
            Html.none
        elif model.PendingRevokeAll then
            Html.div [
                prop.className "mt-4 px-3 py-3 rounded border border-red-200 bg-red-50"
                prop.children [
                    Html.p [
                        prop.className "text-sm text-red-800 mb-2"
                        prop.text msgs.SignOutEverywhereWarning
                    ]
                    Html.div [
                        prop.className "flex gap-2"
                        prop.children [
                            Html.button [
                                prop.className "text-sm px-3 py-1.5 rounded bg-red-600 text-white disabled:opacity-50"
                                prop.disabled model.Busy
                                prop.text msgs.SignOutEverywhere
                                prop.onClick (fun _ -> dispatch ConfirmRevokeAll)
                            ]
                            Html.button [
                                prop.className "text-sm px-3 py-1.5 rounded border border-border"
                                prop.text msgs.Cancel
                                prop.onClick (fun _ -> dispatch CancelRevokeAll)
                            ]
                        ]
                    ]
                ]
            ]
        else
            Html.button [
                prop.className "mt-4 text-sm px-3 py-1.5 rounded border border-red-300 text-red-700 disabled:opacity-50"
                prop.disabled model.Busy
                prop.text msgs.SignOutEverywhere
                prop.onClick (fun _ -> dispatch AskRevokeAll)
            ]

    Html.div [
        prop.className "p-6 max-w-3xl"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
            Html.p [ prop.className "text-sm text-gray-600 mb-4"; prop.text msgs.Subheading ]
            errorBanner
            statusBanner
            body
            signOutEverywhere
        ]
    ]

let view (model: Model) (dispatch: Msg -> unit) : ReactElement = SessionSecurityBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in session-security page as an `ErasedModule`.
/// Injected by the shell's `prepareModules` when the deployment opts in
/// via `ClientConfig.SessionSecurity`.
let create (config: SessionSecurityConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Session Security"

    let icon =
        config
        |> Option.map _.Icon
        |> Option.defaultValue ToolUp.Platform.Icons.settings

    // SDK built-in — reserved under the `_sdk.` Id namespace so it can
    // never collide with an app's RBAC-managed `ServerConfig.ModuleNames`,
    // and so a module-visibility profile stated over that list can never
    // hide the page a user signs a stolen session out from.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.SessionSecurity"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register