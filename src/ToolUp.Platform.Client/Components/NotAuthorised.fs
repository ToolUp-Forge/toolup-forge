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
let private title (msgs: NotAuthorisedMessages) (denial: SidebarVisibility.NavigationDenial) : string =
    match denial with
    | SidebarVisibility.NavigationDenial.NotSignedIn -> msgs.TitleNotSignedIn
    | SidebarVisibility.NavigationDenial.NoActiveTeam -> msgs.TitleNoActiveTeam
    // Phase 637 — a curated-out module is not an access refusal, and
    // titling it "you don't have access" would send the caller to ask an
    // admin for a permission nobody withheld. The page is not part of
    // this deployment's surface; say that.
    | SidebarVisibility.NavigationDenial.NotInVisibilityProfile -> msgs.TitleNotInVisibilityProfile
    | SidebarVisibility.NavigationDenial.RequiresPlatformAdmin
    | SidebarVisibility.NavigationDenial.RequiresTeamOwnerAdmin
    | SidebarVisibility.NavigationDenial.NotExposedToTeam
    | SidebarVisibility.NavigationDenial.NotAvailableToSubject -> msgs.TitleNoAccess

/// The sentence that names the remedy. Each case answers "so what do I
/// do now?" — the question a bare denial leaves open.
let private hint
    (msgs: NotAuthorisedMessages)
    (moduleName: string)
    (denial: SidebarVisibility.NavigationDenial)
    : string =
    match denial with
    | SidebarVisibility.NavigationDenial.NotSignedIn -> msgs.HintNotSignedIn moduleName
    | SidebarVisibility.NavigationDenial.RequiresPlatformAdmin -> msgs.HintRequiresPlatformAdmin moduleName
    | SidebarVisibility.NavigationDenial.RequiresTeamOwnerAdmin -> msgs.HintRequiresTeamOwnerAdmin moduleName
    | SidebarVisibility.NavigationDenial.NotExposedToTeam -> msgs.HintNotExposedToTeam moduleName
    | SidebarVisibility.NavigationDenial.NotAvailableToSubject -> msgs.HintNotAvailableToSubject moduleName
    | SidebarVisibility.NavigationDenial.NoActiveTeam -> msgs.HintNoActiveTeam moduleName
    | SidebarVisibility.NavigationDenial.NotInVisibilityProfile -> msgs.HintNotInVisibilityProfile moduleName

/// The SDK built-in denial surface. Rendered by the shell in the content
/// area, so the sidebar, header and team switcher stay live around it —
/// the caller keeps every affordance they'd need to get somewhere they
/// *can* reach, which is why this is an in-place surface rather than a
/// redirect.
[<ReactComponent>]
let NotAuthorisedView (ctx: NotAuthorisedContext) =
    // Phase 444 — the denial wording comes from the resolved catalog.
    // The `NotAuthorisedMessages` record carries one field per
    // `NavigationDenial` case, so a denial case added upstream fails to
    // compile until it has been worded here.
    let msgs = (MessageCatalogProvider.useMessages ()).NotAuthorised

    Toolup.UIToolkit.StateViews.emptyState
        Icons.lock
        (title msgs ctx.Denial)
        (hint msgs ctx.ModuleName ctx.Denial)
        (Some {
            Label = msgs.GoHome
            OnClick = ctx.GoHome
        })

/// Resolve the configured mode to an element. One call site in the shell;
/// the `Custom` arm hands the deployment's renderer the same context the
/// default gets, so a bespoke view can match on the reason too.
let render (mode: NotAuthorisedMode) (ctx: NotAuthorisedContext) : ReactElement =
    match mode with
    | DefaultNotAuthorised -> NotAuthorisedView ctx
    | CustomNotAuthorised renderer -> renderer ctx