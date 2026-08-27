// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module TenantLifecycleAdminUI

open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Phase 54e — tenant-lifecycle diagnostics admin module ───────────
//
// SDK-built-in Platform-Admin module surfacing the **durable last
// `LifecycleSummary`** the server already persists (Phase 54e's
// `ILifecycleSummaryStore` + `IPlatformTenantApi.GetLifecycleSummary`).
// Given a tenant/team scope id, it renders the outcome of that scope's
// most recent provision / offboard run — per-hook name, result, elapsed
// — plus the completed / skipped / failed counts and total elapsed, so
// an operator can answer "what did the last offboard of team-X do?"
// without hitting the `/dev/inspect` dev endpoint.
//
// **Scope selection.** `GetLifecycleSummary` takes a `scopeId` and there
// is no "list every scope" method, so the panel takes a scope-id input
// (the admin types / pastes a tenant or team scope id) and fetches the
// last summary on submit. A "list scopes with a recorded run" API is a
// possible follow-on (noted in `TIDY-UP.md`), not this phase.
//
// **The registered hook set** is surfaced by the Phase 54e
// `TenantLifecycleDiagnosticsContributor` at `/dev/inspect` (which already
// satisfies the phase's hook-visibility acceptance criterion). Rather than
// add a new API method here, the panel links operators to that surface.
//
// **Gating (Platform-Admin).** Sidebar group "Platform Management" — the
// shell's role gate hides the whole group from non-admin callers, exactly
// like `_sdk.PlatformAdmin` / `_sdk.HealthMonitor`. `IPlatformTenantApi`
// is itself Platform-Admin gated server-side, so a non-admin who reaches
// the module by direct navigation gets a uniform error banner rather than
// a silent read. Opt-in / zero-cost (GP 13): `NoTenantLifecycle`
// deployments 404 the proxy surface, so the panel simply renders its
// empty state — nothing to show, no failure.

// ─── Model ───────────────────────────────────────────────────────────

type Model = {
    /// The scope id whose summary is currently shown / loading. `None`
    /// before the admin submits a scope.
    QueriedScope: string option
    /// Result of the most recent `GetLifecycleSummary`. `None` means no
    /// lifecycle run is recorded for `QueriedScope` (a valid answer).
    Summary: LifecycleSummary option
    /// True once a fetch has completed for `QueriedScope`, so the view
    /// can distinguish "nothing queried yet" from "queried, no run".
    Loaded: bool
    /// True while a `GetLifecycleSummary` call is in flight.
    Loading: bool
    /// Error banner (non-admin caller, transport failure, disabled
    /// substrate). Cleared by `DismissError` or the next submit.
    Error: string option
}

type Msg =
    /// Fetch the last `LifecycleSummary` for the submitted scope id.
    | SubmitScope of string
    | SummaryLoaded of Result<LifecycleSummary option, string>
    | DismissError

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// `WebhookAdminUI`. `IPlatformTenantApi` resolves under the reserved
// `/api/_platform/tenants/` prefix, so the proxy carries its own route
// builder rather than the default `/api/{type}/{method}`.
let private tenantApi: IPlatformTenantApi =
    Api.makeProxy<IPlatformTenantApi> (
        routeBuilder = PlatformTenantApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// ─── Init ────────────────────────────────────────────────────────────

let init () : Model * Cmd<Msg> =
    {
        QueriedScope = None
        Summary = None
        Loaded = false
        Loading = false
        Error = None
    },
    Cmd.none

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | SubmitScope scopeId ->
        let scopeId = scopeId.Trim()

        if scopeId = "" then
            model, Cmd.none
        else
            {
                model with
                    QueriedScope = Some scopeId
                    Summary = None
                    Loaded = false
                    Loading = true
                    Error = None
            },
            Cmd.OfRemoting.call tenantApi.GetLifecycleSummary scopeId SummaryLoaded (fun e ->
                SummaryLoaded(Error e.Message))

    | SummaryLoaded(Ok summary) ->
        {
            model with
                Summary = summary
                Loaded = true
                Loading = false
                Error = None
        },
        Cmd.none

    | SummaryLoaded(Error err) ->
        {
            model with
                Summary = None
                Loaded = true
                Loading = false
                Error = Some err
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

// ─── View helpers ──────────────────────────────────────────────────────

let private resultBadge (result: LifecycleHookResult) =
    let cls, label =
        match result with
        | LifecycleHookResult.Completed -> "bg-green-100 text-green-700", "Completed"
        | LifecycleHookResult.Skipped _ -> "bg-yellow-100 text-yellow-800", "Skipped"
        | LifecycleHookResult.Failed _ -> "bg-red-100 text-red-700", "Failed"

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded {cls}"
        prop.text label
    ]

/// Per-hook detail — the skip reason / failure error, blank for a clean
/// completion.
let private resultDetail (result: LifecycleHookResult) =
    match result with
    | LifecycleHookResult.Completed -> ""
    | LifecycleHookResult.Skipped reason -> reason
    | LifecycleHookResult.Failed error -> error

let private countPill (label: string) (count: int) (cls: string) =
    Html.span [
        prop.className $"inline-flex items-baseline gap-1 text-xs px-2 py-1 rounded {cls}"
        prop.children [
            Html.span [ prop.className "font-semibold"; prop.text (string count) ]
            Html.span [ prop.text label ]
        ]
    ]

let private summaryTable (summary: LifecycleSummary) =
    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4"
        prop.children [
            Html.div [
                prop.className "flex items-baseline gap-3 mb-3 flex-wrap"
                prop.children [
                    Html.h3 [
                        prop.className "text-sm font-semibold"
                        prop.text $"Last run — {TenantLifecyclePhase.name summary.Phase}"
                    ]
                    Html.span [
                        prop.className "text-xs text-gray-500 font-mono break-all"
                        prop.text summary.ScopeId
                    ]
                ]
            ]

            Html.div [
                prop.className "flex gap-2 mb-4 flex-wrap"
                prop.children [
                    countPill "completed" (LifecycleSummary.completedCount summary) "bg-green-50 text-green-700"
                    countPill "skipped" (LifecycleSummary.skippedCount summary) "bg-yellow-50 text-yellow-800"
                    countPill "failed" (LifecycleSummary.failedCount summary) "bg-red-50 text-red-700"
                    countPill "ms total" (int summary.TotalElapsedMs) "bg-gray-100 text-gray-600"
                ]
            ]

            if List.isEmpty summary.Outcomes then
                Html.p [
                    prop.className "text-xs text-gray-500"
                    prop.text "The run completed with no registered lifecycle hooks (a valid no-op run)."
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
                                                    prop.text "Hook"
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text "Result"
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text "Detail"
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text "Elapsed"
                                                ]
                                            ]
                                        ]
                                    ]
                                ]
                                Html.tbody [
                                    prop.children [
                                        for outcome in summary.Outcomes ->
                                            Html.tr [
                                                prop.className "border-t border-border"
                                                prop.children [
                                                    Html.td [
                                                        prop.className "px-3 py-2 font-mono"
                                                        prop.text outcome.HookName
                                                    ]
                                                    Html.td [
                                                        prop.className "px-3 py-2"
                                                        prop.children [ resultBadge outcome.Result ]
                                                    ]
                                                    Html.td [
                                                        prop.className "px-3 py-2 text-gray-600 break-all"
                                                        prop.text (resultDetail outcome.Result)
                                                    ]
                                                    Html.td [
                                                        prop.className "px-3 py-2 font-mono text-gray-500"
                                                        prop.text $"{outcome.ElapsedMs} ms"
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

/// Local React state for the scope input so free-typing doesn't
/// round-trip through Elmish on every keystroke (CLAUDE.md MVU rule).
/// `onSubmit` fires only on the button click / Enter.
[<ReactComponent>]
let private ScopeForm (loading: bool) (onSubmit: string -> unit) =
    let scope, setScope = React.useState ""
    let canSubmit = not loading && scope.Trim() <> ""

    let submit () =
        if canSubmit then
            onSubmit (scope.Trim())

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4 mb-4"
        prop.children [
            Html.label [
                prop.className "block text-xs font-medium text-gray-700 mb-1"
                prop.text "Tenant / team scope id"
            ]
            Html.div [
                prop.className "flex gap-2"
                prop.children [
                    Html.input [
                        prop.type' "text"
                        prop.value scope
                        prop.placeholder "e.g. team-abc123"
                        prop.onChange (fun (v: string) -> setScope v)
                        prop.onKeyDown (fun e ->
                            if e.key = "Enter" then
                                submit ())
                        prop.className
                            "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand flex-1 font-mono text-xs"
                    ]
                    Html.button [
                        prop.className [
                            "px-4 py-2 text-sm rounded-lg text-white transition-colors"
                            if canSubmit then
                                "bg-brand hover:bg-brand-dark cursor-pointer"
                            else
                                "bg-gray-300 cursor-not-allowed"
                        ]
                        prop.disabled (not canSubmit)
                        prop.text (if loading then "Loading…" else "Load last run")
                        prop.onClick (fun _ -> submit ())
                    ]
                ]
            ]
            Html.p [
                prop.className "text-xs text-gray-500 mt-2"
                prop.text
                    "Shows the durable summary of the scope's most recent provision / offboard run. The registered lifecycle hook set is listed at the /dev/inspect diagnostics endpoint."
            ]
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let errorBanner =
        match model.Error with
        | Some msg ->
            Html.div [
                prop.className
                    "mb-4 p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
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

    let resultPane =
        match model.Summary with
        | Some summary -> summaryTable summary
        | None when model.Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading…" ]
        | None when model.Loaded ->
            // Queried, but no run recorded for this scope.
            Html.div [
                prop.className "bg-white rounded-lg border border-border p-6 text-center"
                prop.children [
                    Html.p [
                        prop.className "text-sm text-gray-600"
                        prop.text (
                            match model.QueriedScope with
                            | Some scope -> $"No lifecycle run recorded for \"{scope}\"."
                            | None -> "No lifecycle run recorded for this scope."
                        )
                    ]
                    Html.p [
                        prop.className "text-xs text-gray-400 mt-1"
                        prop.text
                            "A provision or offboard run for the scope will appear here once one has executed (the summary is durable across restarts)."
                    ]
                ]
            ]
        | None ->
            // Nothing queried yet.
            Html.p [
                prop.className "text-sm text-gray-500"
                prop.text "Enter a tenant or team scope id above to view its last lifecycle run."
            ]

    Html.div [
        prop.className "p-6 max-w-3xl"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text "Tenant lifecycle" ]
            Html.p [
                prop.className "text-sm text-gray-600 mb-4"
                prop.text "The outcome of a tenant scope's most recent provision / offboard run."
            ]
            errorBanner
            ScopeForm model.Loading (fun scope -> dispatch (SubmitScope scope))
            resultPane
        ]
    ]

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in tenant-lifecycle diagnostics admin as an
/// `ErasedModule`. Registered mode-agnostically alongside the other
/// `_sdk.*` Platform-Admin modules; the shell's sidebar role filter hides
/// the "Platform Management" group from non-admin callers.
let create () : ErasedModule =
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = "Tenant Lifecycle"
        Icon = ToolUp.Platform.Icons.health
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.TenantLifecycleAdmin"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.PlatformAdminOnly
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register