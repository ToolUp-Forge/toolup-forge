// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.WorkflowBadge

open Feliz
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow

// ─── Phase 21 — Workflow state pill ─────────────────────────────────
//
// Tiny presentational component that renders a SubmissionState as a
// coloured pill. Colour scheme is intentionally simple — Draft is
// neutral grey, Submitted is blue, terminal-ish names like "closed" /
// "rejected" / "cancelled" are red, "approved" / "completed" are
// green. Anything else is amber. Apps that need a richer scheme
// pass their own component instead.

let private colourFor (label: string) : string * string =
    let normalised = label.ToLowerInvariant()

    if normalised = "draft" then
        "#e5e7eb", "#374151"
    elif normalised = "submitted" then
        "#dbeafe", "#1e40af"
    elif List.contains normalised [ "closed"; "rejected"; "cancelled"; "denied"; "expired" ] then
        "#fee2e2", "#991b1b"
    elif List.contains normalised [ "approved"; "completed"; "done"; "resolved"; "fulfilled" ] then
        "#d1fae5", "#065f46"
    else
        "#fef3c7", "#92400e"

[<ReactComponent>]
let WorkflowBadge (state: SubmissionState) =
    let label = SubmissionState.toIndexValue state
    let bg, fg = colourFor label

    Html.span [
        prop.style [
            style.display.inlineBlock
            style.padding (length.em 0.15, length.em 0.6)
            style.borderRadius (length.em 1.0)
            style.fontSize (length.em 0.8)
            style.fontWeight 500
            style.backgroundColor bg
            style.color fg
        ]
        prop.text label
    ]