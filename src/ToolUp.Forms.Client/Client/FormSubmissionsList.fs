// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.FormSubmissionsList

open Feliz
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.WorkflowBadge

// ─── Phase 21 — Generic submissions list ────────────────────────────
//
// Tiny presentational component that renders a list of Submissions
// as a HTML table. The caller supplies the already-loaded list (and
// presumably built it from `FormsClient.proxy.ListSubmissions
// query`); this component does not fetch or filter.
//
// Out of scope for v1 (deferred follow-ups):
//   * AG-Grid integration with server-side paging — for now an
//     unstyled `Html.table` keeps the companion's client surface
//     dependency-light.
//   * Per-row actions (view / edit / transition) — caller renders
//     their own action column by passing a `renderActions` callback
//     in a future revision.
//   * Sorting / filtering UI on top of the list.

[<ReactComponent>]
let FormSubmissionsList (submissions: Submission list) =
    Html.table [
        prop.style [ style.width (length.percent 100); style.borderCollapse.collapse ]
        prop.children [
            Html.thead [
                Html.tr [
                    for h in [ "Form"; "Submitted by"; "Submitted at"; "State" ] do
                        Html.th [
                            prop.style [
                                style.textAlign.left
                                style.padding (length.em 0.5)
                                style.borderBottom (1, borderStyle.solid, "#e5e7eb")
                            ]
                            prop.text h
                        ]
                ]
            ]
            Html.tbody [
                if List.isEmpty submissions then
                    Html.tr [
                        Html.td [
                            prop.colSpan 4
                            prop.style [ style.padding (length.em 1); style.color "#6b7280"; style.textAlign.center ]
                            prop.text "No submissions."
                        ]
                    ]
                else
                    for s in submissions do
                        Html.tr [
                            prop.key s.Id
                            prop.children [
                                Html.td [ prop.style [ style.padding (length.em 0.5) ]; prop.text s.FormId ]
                                Html.td [
                                    prop.style [ style.padding (length.em 0.5) ]
                                    prop.text (
                                        match s.Author with
                                        | AuthenticatedUser uid -> uid
                                        | InvitedRespondent(tokenId, Some handle) -> handle
                                        | InvitedRespondent(tokenId, None) -> "(token " + tokenId.Substring(0, 8) + "…)"
                                    )
                                ]
                                Html.td [
                                    prop.style [ style.padding (length.em 0.5) ]
                                    prop.text (s.SubmittedAt.ToString("yyyy-MM-dd HH:mm"))
                                ]
                                Html.td [
                                    prop.style [ style.padding (length.em 0.5) ]
                                    prop.children [ WorkflowBadge s.State ]
                                ]
                            ]
                        ]
            ]
        ]
    ]