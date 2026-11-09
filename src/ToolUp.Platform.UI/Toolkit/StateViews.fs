// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform

/// Canonical empty / no-data / error / loading state views.
///
/// Modules previously rolled their own placeholder markup (terse grey strings,
/// bespoke red banners). These helpers give every page a consistent, polished
/// state surface. All colours flow from the design tokens — no hardcoded hex.
/// Icons are caller-supplied so the helpers stay product-neutral, with one
/// deliberate exception: `inlineLoading` renders the ToolUp brand mark
/// (`Icons.dataLoading`) so a recomputing panel shows the same "working"
/// indicator as the `BrandMarkLoader` boot screen rather than a bare circle.
module StateViews =

    /// A call-to-action rendered as a primary button inside an empty state.
    type EmptyStateAction = { Label: string; OnClick: unit -> unit }

    /// Full-panel empty state: a large neutral icon, a title, a guiding hint,
    /// and an optional primary call-to-action. Use when a page has nothing to
    /// show yet (no data source selected) — this is the place to explain what
    /// the page is for, so an empty page never feels dead.
    let emptyState (icon: ReactElement) (title: string) (hint: string) (action: EmptyStateAction option) =
        Html.div [
            prop.className "flex flex-col items-center justify-center text-center h-full min-h-[18rem] gap-3 p-8"
            prop.children [
                Html.div [
                    prop.className "text-[var(--muted)] [&>svg]:w-12 [&>svg]:h-12"
                    prop.children [ icon ]
                ]
                Html.h3 [
                    prop.className "text-lg font-semibold text-[var(--text-strong)]"
                    prop.text title
                ]
                Html.p [ prop.className "text-base text-[var(--muted)] max-w-md"; prop.text hint ]
                match action with
                | Some a ->
                    Html.div [
                        prop.className "mt-2"
                        prop.children [ Forms.Button.primary a.Label a.OnClick ]
                    ]
                | None -> Html.none
            ]
        ]

    /// Compact "no rows after filtering" placeholder for a results pane that has
    /// a data source but the current selection produced nothing.
    let noData (message: string) =
        Html.div [
            prop.className "flex items-center justify-center text-center h-full min-h-[12rem] p-6"
            prop.children [
                Html.p [ prop.className "text-base text-[var(--muted)] max-w-md"; prop.text message ]
            ]
        ]

    let private noticeClasses (notice: Notice) =
        match notice with
        | Notice.Info _ -> "border-[var(--pos)] text-[var(--pos)]", "status"
        | Notice.Problem _ -> "border-[var(--neg)] text-[var(--neg)]", "alert"

    let private noticeText (notice: Notice) =
        match notice with
        | Notice.Info text
        | Notice.Problem text -> text

    let private noticeBox (notice: Notice) (dismiss: (string * (unit -> unit)) option) =
        let tone, role = noticeClasses notice

        Html.div [
            prop.role role
            prop.className (
                "flex items-start gap-3 rounded-[var(--radius)] border bg-[var(--surface)] px-4 py-3 "
                + tone
            )
            prop.children [
                Html.span [ prop.className "text-base flex-1"; prop.text (noticeText notice) ]
                match dismiss with
                | Some(label, onDismiss) ->
                    Html.button [
                        prop.className "hover:opacity-80 transition-opacity flex-shrink-0 text-lg leading-none"
                        prop.title label
                        prop.ariaLabel label
                        prop.onClick (fun _ -> onDismiss ())
                        prop.text "×"
                    ]
                | None -> Html.none
            ]
        ]

    /// A notice that stays until the state behind it changes — a failed
    /// load's description, a standing warning. Its polarity is its case:
    /// `Info` reads in the positive token, `Problem` in the negative one.
    let noticeStatic (notice: Notice) : ReactElement = noticeBox notice None

    /// The user-dismissible notice of a page: nothing for `None`, otherwise
    /// the notice with a dismiss button that calls `onDismiss`. The text is
    /// the caller's, already localised; `msgs` carries the dismiss label.
    let notice (msgs: NoticeMessages) (notice: Notice option) (onDismiss: unit -> unit) : ReactElement =
        match notice with
        | Some shown -> noticeBox shown (Some(msgs.Dismiss, onDismiss))
        | None -> Html.none

    /// Inline "working" mark + label, for "recomputing while showing stale
    /// results". Renders the ToolUp brand mark (`Icons.dataLoading` — the
    /// spinning, colour-cycling chevron-and-dot, self-animated via SMIL so it ignores
    /// the surrounding `currentColor` cascade) rather than a neutral spinner
    /// ring, so a panel's inline loading state matches the `BrandMarkLoader`
    /// boot indicator instead of a bare circle. This is the one StateViews
    /// helper that is intentionally brand-specific (the others take a
    /// caller-supplied icon) — see the module header.
    let inlineLoading (label: string) =
        Html.div [
            prop.className "flex items-center gap-2 text-[var(--muted)] text-sm"
            prop.children [
                Html.div [
                    prop.className "[&>svg]:w-5 [&>svg]:h-5"
                    prop.children [ Icons.dataLoading ]
                ]
                Html.span [ prop.text label ]
            ]
        ]

    /// One read's state, rendered: nothing while it is not asked for, the
    /// inline loading mark while it is in flight (above the previous answer
    /// on a reload), the answer through `render`, and a failure as a
    /// `Problem` notice described by the caller's `describe`.
    let remote
        (state: Remote<'T, 'E>)
        (describe: 'E -> string)
        (loading: string)
        (render: 'T -> ReactElement)
        : ReactElement =
        match state with
        | Remote.NotAsked -> Html.none
        | Remote.Loading -> inlineLoading loading
        | Remote.Reloading stale ->
            Html.div [
                prop.className "flex flex-col gap-2"
                prop.children [ inlineLoading loading; render stale ]
            ]
        | Remote.Loaded value -> render value
        | Remote.Failed error -> noticeStatic (Notice.Problem(describe error))