// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module SessionSecurityUI

open Fuaran.MVU
open System
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Session security (client-tier slice) ────────────────────────────
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

// ─── Remote calls ────────────────────────────────────────────────────

let private load () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "ISessionApi.ListMySessions"
            sessionApi.ListMySessions
            ()
            (RemoteEffect.flatten >> SessionsLoaded)
    )
]

let private revoke (sessionId: string) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "ISessionApi.RevokeSession"
            sessionApi.RevokeSession
            sessionId
            (RemoteEffect.flatten >> RevokeCompleted)
    )
]

let private revokeAll () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "ISessionApi.RevokeAllMySessions"
            sessionApi.RevokeAllMySessions
            ()
            (RemoteEffect.flatten >> RevokeAllCompleted)
    )
]

// ─── Init ────────────────────────────────────────────────────────────

let init (ctx: ClientModuleContext) : Model * Step<Msg, RemoteEffect<Msg>> list =
    ({
        Sessions = []
        Loaded = false
        Busy = false
        PendingRevoke = None
        PendingRevokeAll = false
        Error = None
        Status = None
        Messages = ctx.Messages.SessionSecurity
     },
     load ())

// ─── Update ──────────────────────────────────────────────────────────

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | Load -> { model with Error = None }, load ()

    | SessionsLoaded(Ok sessions) ->
        // A session the reload did not change stays the record the page
        // already holds, so its row is not built again.
        {
            model with
                Sessions = Memo.keepEqual (fun (s: SessionRecord) -> s.SessionId) model.Sessions sessions
                Loaded = true
                Busy = false
        },
        []

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
        []

    | AskRevoke sessionId ->
        {
            model with
                PendingRevoke = Some sessionId
                PendingRevokeAll = false
        },
        []

    | CancelRevoke -> { model with PendingRevoke = None }, []

    | ConfirmRevoke sessionId ->
        {
            model with
                PendingRevoke = None
                Busy = true
                Error = None
                Status = None
        },
        revoke sessionId

    | RevokeCompleted(Ok()) ->
        // Reload rather than patching the local list: the server is the
        // authority on what is now revoked, and a locally-patched entry
        // would look revoked even if a concurrent change said otherwise.
        {
            model with
                Status = Some(Notice.Info model.Messages.RevokeSuccess)
        },
        load ()

    | RevokeCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        []

    | AskRevokeAll ->
        {
            model with
                PendingRevokeAll = true
                PendingRevoke = None
        },
        []

    | CancelRevokeAll -> { model with PendingRevokeAll = false }, []

    | ConfirmRevokeAll ->
        {
            model with
                PendingRevokeAll = false
                Busy = true
                Error = None
                Status = None
        },
        revokeAll ()

    | RevokeAllCompleted(Ok count) ->
        // Report the server's count rather than "done". Zero is a real
        // and useful answer — it means there was nothing left to sign
        // out, which is different from a revocation having happened.
        // The singular/plural branching lives inside the
        // catalog function itself so a translation owns its own
        // pluralisation rule.
        let message = model.Messages.RevokeAllResult count

        {
            model with
                Status = Some(Notice.Info message)
        },
        load ()

    | RevokeAllCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        []

    | DismissError -> { model with Error = None }, []

    | DismissStatus -> { model with Status = None }, []

// ─── View helpers ────────────────────────────────────────────────────

/// Human-readable "last seen". Deliberately coarse: `LastSeenAt` is
/// advanced at minute granularity or worse by design (GP 12 rule 6), so
/// rendering a precise time would imply an accuracy the substrate does
/// not promise. A time after `now` (clock skew) reads as just now.
let lastSeenLabelWith (msgs: SessionSecurityMessages) (now: DateTimeOffset) (at: DateTimeOffset) : string =
    let elapsed = now - at

    if elapsed < TimeSpan.FromMinutes 2.0 then
        msgs.JustNow
    elif elapsed < TimeSpan.FromHours 1.0 then
        msgs.MinutesAgo(int elapsed.TotalMinutes)
    elif elapsed < TimeSpan.FromDays 1.0 then
        msgs.HoursAgo(int elapsed.TotalHours)
    elif elapsed < TimeSpan.FromDays 30.0 then
        msgs.DaysAgo(int elapsed.TotalDays)
    else
        // A .NET date format specifier, not translatable prose. Baking
        // it into a translated template would make the date FORMAT a
        // property of the language.
        at.ToString "d MMM yyyy"

// ─── View ────────────────────────────────────────────────────────────

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: SessionSecurityMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./SessionSecurityTree.fs")

/// The module's page: its tree, mounted on demand.
let view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "SessionSecurityTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.SessionSecurity
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
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