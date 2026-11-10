// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module SessionSecurityUI

open System
open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
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
// `ServerConfig.Stores.SessionRegistry = NoSessionRegistry` never sets
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
    /// Failure notice (`Notice.Problem`). Cleared by `DismissError` or the
    /// next mutation.
    Error: Notice option
    /// Transient confirmation after a revoke (`Notice.Info`).
    Status: Notice option
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: SessionSecurityMessages
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

let init (ctx: ClientModuleContext) : Model * Cmd<Msg> =
    {
        Sessions = []
        Loaded = false
        Busy = false
        PendingRevoke = None
        PendingRevokeAll = false
        Error = None
        Status = None
        Messages = ctx.Messages.SessionSecurity
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
                Error = Some(Notice.Problem err)
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
                Status = Some(Notice.Info model.Messages.RevokeSuccess)
        },
        loadCmd ()

    | RevokeCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
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
        let message = model.Messages.RevokeAllResult count

        {
            model with
                Status = Some(Notice.Info message)
        },
        loadCmd ()

    | RevokeAllCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
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
/// matching `BootDegradation.banner` / `Components.ModuleBoundary.wrapWith`.
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
                                ("text-sm font-medium truncate line-through " + Tokens.Text.muted)
                        )
                        prop.text record.DeviceDescriptor
                    ]
                    Html.p [
                        prop.className [ "text-xs mt-0.5"; Tokens.Text.muted ]
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
                        Html.span [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.Revoked ]
                    elif pending then
                        Html.div [
                            prop.className "flex gap-2"
                            prop.children [
                                Forms.Button.dangerWith
                                    (Forms.Button.stateOf model.Busy msgs.Confirm false)
                                    msgs.Confirm
                                    (fun () -> dispatch (ConfirmRevoke record.SessionId))
                                Forms.Button.secondaryWith Forms.Button.Enabled msgs.Cancel (fun () ->
                                    dispatch CancelRevoke)
                            ]
                        ]
                    else
                        Forms.Button.secondaryWith
                            (Forms.Button.stateOf false msgs.SignOut model.Busy)
                            msgs.SignOut
                            (fun () -> dispatch (AskRevoke record.SessionId))
                ]
            ]
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

/// The module's page, over the catalog section the view hands it.
let private body (msgs: SessionSecurityMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let now = DateTimeOffset.UtcNow

    let noticeMsgs = { Dismiss = msgs.Dismiss }

    let body =
        if not model.Loaded then
            Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.Loading ]
        elif List.isEmpty model.Sessions then
            Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.EmptyState ]
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
                            Forms.Button.dangerWith
                                (Forms.Button.stateOf model.Busy msgs.SignOutEverywhere false)
                                msgs.SignOutEverywhere
                                (fun () -> dispatch ConfirmRevokeAll)
                            Forms.Button.secondaryWith Forms.Button.Enabled msgs.Cancel (fun () ->
                                dispatch CancelRevokeAll)
                        ]
                    ]
                ]
            ]
        else
            Html.div [
                prop.className "mt-4"
                prop.children [
                    Forms.Button.secondaryWith
                        (Forms.Button.stateOf false msgs.SignOutEverywhere model.Busy)
                        msgs.SignOutEverywhere
                        (fun () -> dispatch AskRevokeAll)
                ]
            ]

    Html.div [
        prop.className "p-6 max-w-3xl"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
            Html.p [
                prop.className [ "text-sm mb-4"; Tokens.Text.secondary ]
                prop.text msgs.Subheading
            ]
            StateViews.notice noticeMsgs model.Error (fun () -> dispatch DismissError)
            StateViews.notice noticeMsgs model.Status (fun () -> dispatch DismissStatus)
            body
            signOutEverywhere
        ]
    ]

let view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(body ctx.Messages.SessionSecurity model ctx.Dispatch)

// ─── Module creation ─────────────────────────────────────────────────

/// Phase 879 — this built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.SessionSecurity"

/// The built-in session-security page (`ShellSlots.builtIn` builds its module).
/// Injected by the shell's `prepareModules` when the deployment opts in
/// via `ClientConfig.SessionSecurity`.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Session Security"
        Icon = ToolUp.Platform.Icons.settings
    }
    Group = None
    NavRole = None
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = init
    Update = update
    View = BuiltInView.Page view
    Extend = id
}