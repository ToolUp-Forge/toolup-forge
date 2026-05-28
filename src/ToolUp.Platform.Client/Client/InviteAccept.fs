// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.InviteAccept

open Browser.Dom
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Phase 3d — team-invitation accept page ──────────────────────────
//
// Standalone Feliz component for the `/invite/{token}` URL — what
// recipients see when they click an invitation link. Renders behind
// the deployment's normal AuthUI gate: the user must be signed in
// before `AcceptInvite` is called against the substrate-authenticated
// `ITeamInviteApi` surface.
//
// State machine (React.useState; transient input state stays in
// React per the SDK convention):
//
//   Checking   → on mount, look up the access token and the token
//                segment of the URL
//   NotSignedIn → access token missing; user is shown a "please sign
//                 in first" panel that returns to the deployment's
//                 home page (the shell's AuthUI flow signs them in).
//                 The invitation token is stashed in sessionStorage,
//                 and a `UserSession.onAuthTokenChange` observer is
//                 installed (Phase 3d.A) that drains the stash and
//                 re-fires `AcceptInvite` when the visitor returns
//                 signed-in — no second click required.
//   Accepting  → AcceptInvite is in flight
//   Accepted r → success — the team name + role are echoed back
//   Failed msg → terminal error — expired / revoked / already used

[<RequireQualifiedAccess>]
type private AcceptState =
    | Accepting
    | Accepted of TeamInviteAcceptResult
    | Failed of message: string
    | NotSignedIn

let private pendingInviteSessionKey = "toolup.pendingInviteToken"

// Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
let private inviteApi: ITeamInviteApi =
    Api.makeProxy<ITeamInviteApi> (
        routeBuilder = TeamInviteApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

/// Extract the invitation token from `/invite/{token}`. Mirrors the
/// pattern used by `ToolUp.Forms.PublicEmbed.extractToken`.
let extractToken (path: string) : string option =
    let trimmed = path.TrimStart('/')

    if trimmed.StartsWith("invite/") then
        let rest = trimmed.Substring 7

        if rest.Length > 0 then
            Some(
                match rest.IndexOf('/') with
                | -1 -> rest
                | i -> rest.Substring(0, i)
            )
        else
            None
    else
        None

/// Predicate the consumer passes to `ClientConfig.PublicEntryDispatchers`
/// to short-circuit the shell on `/invite/{token}`. Returns `true`
/// when the current path matches; the dispatcher renders the accept
/// component in place of the shell.
let isInviteUrl (_config: ClientConfig) : bool =
    extractToken window.location.pathname |> Option.isSome

let private stashToken (token: string) : unit =
    Browser.WebStorage.sessionStorage.setItem (pendingInviteSessionKey, token)

let private readStashedToken () : string option =
    match Browser.WebStorage.sessionStorage.getItem pendingInviteSessionKey with
    | null
    | "" -> None
    | t -> Some t

let private clearStashedToken () : unit =
    Browser.WebStorage.sessionStorage.removeItem pendingInviteSessionKey

/// Pure resume-trigger predicate — exposed for unit-test coverage
/// of the auto-resume decision. Fires when the auth-token transition
/// reaches `Some _` AND a stashed token from the URL is still present.
/// A `None` transition (sign-out) or an empty stash both produce
/// `false`. Centralised here so the resume gate is testable without
/// JSDOM.
let shouldResumeAccept (currentToken: string option) (stashed: string option) : bool =
    match currentToken, stashed with
    | Some _, Some _ -> true
    | _ -> false

[<ReactComponent>]
let InviteAccept () : ReactElement =
    let state, setState = React.useState AcceptState.Accepting

    // Shared accept driver — used both by the initial mount and by
    // the auto-resume observer when the visitor returns signed-in.
    // Pure function of the token; resolves to a terminal state via
    // `setState`. Stash cleanup on every terminal outcome so a
    // subsequent sign-in for a different invite can't replay a stale
    // entry.
    let runAccept (token: string) = async {
        try
            match! inviteApi.AcceptInvite token with
            | Ok result ->
                clearStashedToken ()
                setState (AcceptState.Accepted result)
            | Error msg ->
                // The handler emits this exact string for the
                // unauthenticated path — pattern-match on it
                // explicitly rather than fuzzy-string-matching
                // HTTP-status digits in exception messages
                // (which conflate 401 with CSRF 403 and any
                // other 4xx whose text happens to contain
                // "401" / "403" / "Unauthorized"). Every other
                // typed Error path renders the actual handler
                // message so users see the real cause.
                if msg.StartsWith "You must sign in" then
                    // Stash preserved — the auto-resume observer
                    // drains it on the signed-in transition.
                    setState AcceptState.NotSignedIn
                else
                    clearStashedToken ()
                    setState (AcceptState.Failed msg)
        with ex ->
            // Network-layer failure (transport refused, CORS,
            // offline) — surface the actual exception text.
            // Don't try to infer "sign in needed" from the
            // exception message; the typed path above is the
            // canonical sign-in signal. Stash kept so a
            // subsequent retry (manual or auto) can drain it.
            setState (AcceptState.Failed(sprintf "Network error: %s" ex.Message))
    }

    React.useEffectOnce (fun () ->
        match extractToken window.location.pathname with
        | None -> setState (AcceptState.Failed "No invitation token in URL.")
        | Some token ->
            stashToken token
            runAccept token |> Async.StartImmediate)

    // Phase 3d.A — auto-resume after sign-in. When the page was
    // first opened unauthenticated, the runAccept call above lands
    // in `NotSignedIn` and the stashed token survives in
    // sessionStorage. The observer below fires once the visitor
    // returns signed-in and replays the accept call — no second
    // click on the invitation link required.
    React.useEffect (
        (fun () ->
            let dispose =
                UserSession.onAuthTokenChange (fun token ->
                    if shouldResumeAccept token (readStashedToken ()) then
                        match readStashedToken () with
                        | Some stashed ->
                            // Re-enter the accept flow. Mark
                            // Accepting so the page replaces the
                            // "Sign in to accept" panel with the
                            // loading indicator while the call is
                            // in flight.
                            setState AcceptState.Accepting
                            runAccept stashed |> Async.StartImmediate
                        | None -> ())

            FsReact.createDisposable dispose),
        [||]
    )

    let pageFrame children =
        Html.div [
            prop.className "w-screen min-h-screen bg-bg-light font-sans flex items-center justify-center"
            prop.children [
                Html.div [
                    prop.className
                        "bg-white rounded-lg shadow-md px-10 py-8 max-w-md w-full flex flex-col items-center gap-4"
                    prop.children (children: ReactElement list)
                ]
            ]
        ]

    match state with
    | AcceptState.NotSignedIn ->
        pageFrame [
            Html.h1 [
                prop.className "text-2xl font-semibold text-brand font-['Umami']"
                prop.text "Sign in to accept"
            ]
            Html.p [
                prop.className $"{Tokens.Text.secondary} text-center"
                prop.text
                    "You'll need to sign in before you can join the team. Sign in at the home page, then re-open this invitation link."
            ]
            Html.a [
                prop.className $"{Tokens.Button.primary} w-full text-center"
                prop.text "Go to sign in"
                prop.href "/"
            ]
        ]
    | AcceptState.Accepting ->
        pageFrame [
            Html.div [
                prop.className $"{Tokens.Text.secondary} text-sm"
                prop.text "Joining the team…"
            ]
        ]
    | AcceptState.Accepted result ->
        pageFrame [
            Html.h1 [
                prop.className "text-2xl font-semibold text-brand font-['Umami']"
                prop.text $"Welcome to {result.TeamName}"
            ]
            Html.p [
                prop.className $"{Tokens.Text.secondary} text-center"
                prop.text $"You've joined as {TeamRoles.displayName result.Role}."
            ]
            Html.a [
                prop.className $"{Tokens.Button.primary} w-full text-center"
                prop.text "Continue to the app"
                prop.href "/"
            ]
        ]
    | AcceptState.Failed message ->
        pageFrame [
            Html.h1 [
                prop.className "text-xl font-semibold text-brand font-['Umami']"
                prop.text "Could not accept invitation"
            ]
            Html.p [
                prop.className $"{Tokens.Colours.error} text-center text-sm"
                prop.text message
            ]
            Html.a [
                prop.className $"{Tokens.Button.secondary} w-full text-center"
                prop.text "Go to home"
                prop.href "/"
            ]
        ]

/// Mount the invitation-accept page against the `"elmish-app"` root.
/// Called from a `PublicEntryDispatchers` entry — when `isInviteUrl`
/// returns `true`, the consumer can render `InviteAccept ()` directly
/// to `"elmish-app"` to bypass the normal shell bootstrap.
///
/// Phase 3d / Cluster D3 — also kicks off `CsrfClient.prefetch ()` so
/// the first `AcceptInvite` POST carries a valid CSRF token under
/// `DefaultSecurityHardening` deployments. Without this, the standalone
/// mount skips the normal shell boot path (which prefetches via
/// `installRequestGuard`), the AcceptInvite POST 403s on CSRF, and the
/// client-side error UX rendered a misleading "Sign in to accept"
/// fallback (D2 narrowed that fallback but the underlying race still
/// produced a 403 the user couldn't fix). Prefetch is async + best-
/// effort; subsequent state-changing requests serialise on it via
/// `CsrfClient.ensure`.
let render () : unit =
    let root = document.getElementById "elmish-app"

    if not (isNull root) then
        CsrfClient.prefetch ()
        ReactDOM.createRoot(root).render (InviteAccept())