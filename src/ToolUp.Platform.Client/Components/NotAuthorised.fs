// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.NotAuthorised

open Feliz
open ToolUp.Platform

// Phase 569 — the shell's typed "not authorised" surface.
//
// Hiding a sidebar entry never blocked navigation. Before this phase a
// caller who deep-linked a module they could not see got the module's own
// view, mounted, initialised, and backed only by server-side 403s: empty
// panes, spinners that never resolve, and whatever raw error state the
// module happened to render. The shell now consults the SAME predicate
// that filters the sidebar (`SidebarVisibility.canNavigateTo`) before it
// dispatches a page, and renders this instead.
//
// **Reason-aware on purpose.** "Not authorised" alone is the least useful
// thing a denial can say — it does not tell the caller whether to sign
// in, pick a team, or ask an administrator, which are three completely
// different next actions. `NavigationDenial` carries which gate refused,
// so each case gets the sentence that names the actual remedy, and every
// case gets a route home: a refused deep-link is otherwise a dead end
// whose only exit is the browser's back button.
//
// **Not the security boundary (GP 4 / GP 12).** The server's per-route
// guards are the enforcement; this is UX coherence over the same
// decision. Nothing here is load-bearing for access control, which is
// also why a deployment may replace it wholesale
// (`ClientConfig.NotAuthorisedView = CustomNotAuthorised …`).
//
// Theme-aware by construction: it composes `UIToolkit.StateViews`
// `emptyState`, so it inherits the toolkit's `var(--text-strong)` /
// `var(--muted)` / `var(--radius)` tokens and re-themes with the rest of
// the shell rather than hard-coding a palette.

/// The heading for a denial. Deliberately plain — the sentence below
/// carries the actionable part, and a shouty title on what is often a
/// mis-pasted URL reads as an accusation.
let private title (denial: SidebarVisibility.NavigationDenial) : string =
    match denial with
    | SidebarVisibility.NavigationDenial.NotSignedIn -> "Sign in to continue"
    | SidebarVisibility.NavigationDenial.NoActiveTeam -> "Pick a team first"
    // Phase 637 — a curated-out module is not an access refusal, and
    // titling it "you don't have access" would send the caller to ask an
    // admin for a permission nobody withheld. The page is not part of
    // this deployment's surface; say that.
    | SidebarVisibility.NavigationDenial.NotInVisibilityProfile -> "This page isn't part of this workspace"
    | SidebarVisibility.NavigationDenial.RequiresPlatformAdmin
    | SidebarVisibility.NavigationDenial.RequiresTeamOwnerAdmin
    | SidebarVisibility.NavigationDenial.NotExposedToTeam
    | SidebarVisibility.NavigationDenial.NotAvailableToSubject -> "You don't have access to this page"

/// The sentence that names the remedy. Each case answers "so what do I
/// do now?" — the question a bare denial leaves open.
let private hint (moduleName: string) (denial: SidebarVisibility.NavigationDenial) : string =
    match denial with
    | SidebarVisibility.NavigationDenial.NotSignedIn ->
        sprintf "\"%s\" isn't available to signed-out visitors. Sign in and try the link again." moduleName
    | SidebarVisibility.NavigationDenial.RequiresPlatformAdmin ->
        sprintf "\"%s\" is a platform administration page. Ask a Platform Admin if you need access." moduleName
    | SidebarVisibility.NavigationDenial.RequiresTeamOwnerAdmin ->
        sprintf "\"%s\" is for team owners and admins. Ask an owner of this team if you need access." moduleName
    | SidebarVisibility.NavigationDenial.NotExposedToTeam ->
        sprintf "\"%s\" isn't switched on for this team. A Platform Admin can enable it." moduleName
    | SidebarVisibility.NavigationDenial.NotAvailableToSubject ->
        sprintf "\"%s\" isn't available in your current workspace. Switching team or scope may reach it." moduleName
    | SidebarVisibility.NavigationDenial.NoActiveTeam ->
        sprintf "\"%s\" is scoped to a team, and you haven't picked one yet. Choose a team to continue." moduleName
    | SidebarVisibility.NavigationDenial.NotInVisibilityProfile ->
        sprintf
            "\"%s\" isn't one of the modules this workspace uses. An owner can add it to the workspace's module selection."
            moduleName

/// The SDK built-in denial surface. Rendered by the shell in the content
/// area, so the sidebar, header and team switcher stay live around it —
/// the caller keeps every affordance they'd need to get somewhere they
/// *can* reach, which is why this is an in-place surface rather than a
/// redirect.
[<ReactComponent>]
let NotAuthorisedView (ctx: NotAuthorisedContext) =
    Toolup.UIToolkit.StateViews.emptyState
        Icons.lock
        (title ctx.Denial)
        (hint ctx.ModuleName ctx.Denial)
        (Some {
            Label = "Go to home"
            OnClick = ctx.GoHome
        })

/// Resolve the configured mode to an element. One call site in the shell;
/// the `Custom` arm hands the deployment's renderer the same context the
/// default gets, so a bespoke view can match on the reason too.
let render (mode: NotAuthorisedMode) (ctx: NotAuthorisedContext) : ReactElement =
    match mode with
    | DefaultNotAuthorised -> NotAuthorisedView ctx
    | CustomNotAuthorised renderer -> renderer ctx