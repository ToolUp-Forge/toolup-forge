// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.SurveyListView

open Feliz
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormApi

// ─── Phase 21b (slice 7) — Multi-survey overview list ───────────────
//
// Stateless presentational component for the operator's "all my
// surveys" pane. Caller fetches the rows via
// `FormsClient.proxy.ListSchemasOverview ()` and passes them in;
// `onOpen` fires when the operator clicks a survey row (host wires
// it to whatever navigation primitive they use — sidebar entry,
// drawer, route change).
//
// Companion to `SurveyDashboardView` (per-survey roll-up) — apps
// typically wire SurveyListView as the entry point and
// SurveyDashboardView as the per-survey detail view.
//
// Auth-blind by design (matches `SurveyDashboardView`); server-side
// `IFormApi.ListSchemasOverview` is filtered to scope by
// `IFormStore.ListSchemas`, so cross-scope leakage is structurally
// impossible regardless of where this component is mounted.

let private formatPercent (rate: float option) : string =
    match rate with
    | Some r -> sprintf "%.0f%%" (r * 100.0)
    | None -> "—"

let private statusPill (status: SurveyStatus) =
    let bg, fg, label =
        match status with
        | Draft -> "#e5e7eb", "#374151", "Draft"
        | Active -> "#dcfce7", "#166534", "Active"
        | Closed -> "#fee2e2", "#991b1b", "Closed"

    Html.span [
        prop.style [
            style.padding (length.em 0.25, length.em 0.5)
            style.backgroundColor bg
            style.color fg
            style.borderRadius 4
            style.fontSize (length.em 0.75)
            style.fontWeight 600
        ]
        prop.text label
    ]

[<ReactComponent>]
let SurveyListView (overview: SurveyOverviewRow list) (onOpen: FormSchemaId -> unit) =
    if List.isEmpty overview then
        Html.div [
            prop.style [ style.padding (length.em 2.0); style.color "#6b7280"; style.textAlign.center ]
            prop.text "No surveys yet."
        ]
    else
        Html.table [
            prop.style [ style.width (length.percent 100); style.borderCollapse.collapse ]
            prop.children [
                Html.thead [
                    Html.tr [
                        for label in [ "Survey"; "Status"; "Responses"; "Invited"; "Rate" ] do
                            Html.th [
                                prop.style [
                                    style.padding (length.em 0.5)
                                    style.borderBottom (length.px 2, borderStyle.solid, "#d1d5db")
                                    style.textAlign.left
                                    style.fontSize (length.em 0.875)
                                ]
                                prop.text label
                            ]
                    ]
                ]
                Html.tbody [
                    for row in overview |> List.sortBy _.Schema.DisplayName do
                        Html.tr [
                            prop.style [ style.cursor.pointer ]
                            prop.onClick (fun _ -> onOpen row.Schema.Id)
                            prop.children [
                                Html.td [
                                    prop.style [
                                        style.padding (length.em 0.5)
                                        style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                        style.fontWeight 500
                                    ]
                                    prop.text row.Schema.DisplayName
                                ]
                                Html.td [
                                    prop.style [
                                        style.padding (length.em 0.5)
                                        style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                    ]
                                    prop.children [ statusPill row.Status ]
                                ]
                                Html.td [
                                    prop.style [
                                        style.padding (length.em 0.5)
                                        style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                        style.fontFamily "monospace"
                                    ]
                                    prop.text (string row.SubmissionCount)
                                ]
                                Html.td [
                                    prop.style [
                                        style.padding (length.em 0.5)
                                        style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                        style.fontFamily "monospace"
                                        style.color "#6b7280"
                                    ]
                                    prop.text (string row.InvitedCount)
                                ]
                                Html.td [
                                    prop.style [
                                        style.padding (length.em 0.5)
                                        style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                        style.fontFamily "monospace"
                                    ]
                                    prop.text (formatPercent row.ResponseRate)
                                ]
                            ]
                        ]
                ]
            ]
        ]