// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.SurveyDashboardView

open Feliz
open ToolUp.Forms.FormSchema
open ToolUp.Forms.AggregationTypes

// ─── Phase 21b — Creator-side dashboard for a Publishable form ─────
//
// Stateless presentational component. Caller fetches the
// `(schema, summary)` pair via `FormsClient.proxy.GetAggregations`
// and hands them in; this component renders progress + per-question
// aggregations + the recipient response table.
//
// The component is auth-blind: callers (typically inside a Forms
// admin module) gate visibility on Owner/Admin role before
// rendering. The server-side `IFormApi.GetAggregations` is itself
// Owner/Admin-gated so a leak through the client wouldn't expose
// the data anyway.
//
// Sentiment / NLP analyser outputs (`AnalyserOutputs` slot) render
// as a small "Analyser results" section per field; default is
// empty until a deployment registers an `IFormSubmissionAnalyser`
// companion.

let private formatPercent (rate: float option) : string =
    match rate with
    | Some r -> sprintf "%.0f%%" (r * 100.0)
    | None -> "—"

let private formatFloat (v: float option) : string =
    match v with
    | Some n -> sprintf "%.2f" n
    | None -> "—"

let private renderNumeric (a: NumericAggregation) =
    Html.div [
        prop.style [ style.display.flex; style.gap (length.em 1.0); style.flexWrap.wrap ]
        prop.children [
            for label, value in
                [
                    "n", string a.Count
                    "mean", formatFloat a.Mean
                    "median", formatFloat a.Median
                    "stddev", formatFloat a.StdDev
                    "min", formatFloat a.Min
                    "max", formatFloat a.Max
                ] do
                Html.div [
                    prop.style [ style.padding (length.em 0.5); style.backgroundColor "#f3f4f6" ]
                    prop.children [
                        Html.div [
                            prop.style [ style.fontSize (length.em 0.75); style.color "#6b7280" ]
                            prop.text label
                        ]
                        Html.div [ prop.style [ style.fontWeight 600 ]; prop.text value ]
                    ]
                ]
        ]
    ]

let private renderChoice (a: ChoiceAggregation) =
    Html.table [
        prop.style [ style.width (length.percent 100); style.borderCollapse.collapse ]
        prop.children [
            Html.tbody [
                for kvp in a.Counts |> Map.toList |> List.sortByDescending snd do
                    let option' = fst kvp
                    let count = snd kvp

                    let pct =
                        if a.TotalVotes = 0 then
                            0.0
                        else
                            float count / float a.TotalVotes * 100.0

                    Html.tr [
                        Html.td [
                            prop.style [
                                style.padding (length.em 0.25)
                                style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                            ]
                            prop.text option'
                        ]
                        Html.td [
                            prop.style [
                                style.padding (length.em 0.25)
                                style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                style.textAlign.right
                                style.fontFamily "monospace"
                            ]
                            prop.text (sprintf "%d (%.0f%%)" count pct)
                        ]
                    ]
            ]
        ]
    ]

let private renderBool (a: BoolAggregation) =
    let total = a.TrueCount + a.FalseCount

    let pct count =
        if total = 0 then 0.0 else float count / float total * 100.0

    Html.div [
        prop.style [ style.display.flex; style.gap (length.em 1.0) ]
        prop.children [
            Html.div [
                prop.style [ style.padding (length.em 0.5); style.backgroundColor "#dcfce7" ]
                prop.text (sprintf "Yes: %d (%.0f%%)" a.TrueCount (pct a.TrueCount))
            ]
            Html.div [
                prop.style [ style.padding (length.em 0.5); style.backgroundColor "#fee2e2" ]
                prop.text (sprintf "No: %d (%.0f%%)" a.FalseCount (pct a.FalseCount))
            ]
        ]
    ]

let private renderText (a: TextAggregation) =
    Html.div [
        Html.div [
            prop.style [ style.color "#6b7280"; style.marginBottom (length.em 0.5) ]
            prop.text (
                sprintf
                    "%d responses%s"
                    a.ResponseCount
                    (if a.Sample.Length > 0 then
                         sprintf " — first %d shown" a.Sample.Length
                     else
                         "")
            )
        ]
        Html.ul [
            prop.style [ style.paddingLeft (length.em 1.0) ]
            prop.children [
                for sample in a.Sample do
                    Html.li [
                        prop.style [ style.fontSize (length.em 0.875); style.color "#374151" ]
                        prop.text sample
                    ]
            ]
        ]
    ]

let private renderDate (a: DateAggregation) =
    let format (dt: System.DateTimeOffset option) =
        match dt with
        | Some t -> t.ToString("yyyy-MM-dd")
        | None -> "—"

    Html.div [ prop.text (sprintf "n=%d, %s … %s" a.Count (format a.Min) (format a.Max)) ]

let private renderFieldAggregation (agg: FieldAggregation) =
    match agg with
    | NumericFieldAggregation a -> renderNumeric a
    | ChoiceFieldAggregation a -> renderChoice a
    | BoolFieldAggregation a -> renderBool a
    | TextFieldAggregation a -> renderText a
    | DateFieldAggregation a -> renderDate a
    | OpaqueAggregation count -> Html.div [ prop.text (sprintf "n=%d" count) ]

let private renderRecipientTable (recipients: RecipientResponseStatus list) =
    if List.isEmpty recipients then
        Html.div [
            prop.style [ style.padding (length.em 1.0); style.color "#6b7280" ]
            prop.text "No invitations issued for this form."
        ]
    else
        Html.table [
            prop.style [ style.width (length.percent 100); style.borderCollapse.collapse ]
            prop.children [
                Html.thead [
                    Html.tr [
                        for label in [ "Recipient"; "Issued"; "Responded"; "Status" ] do
                            Html.th [
                                prop.style [
                                    style.padding (length.em 0.5)
                                    style.borderBottom (length.px 2, borderStyle.solid, "#d1d5db")
                                    style.textAlign.left
                                ]
                                prop.text label
                            ]
                    ]
                ]
                Html.tbody [
                    for r in recipients do
                        Html.tr [
                            Html.td [
                                prop.style [
                                    style.padding (length.em 0.5)
                                    style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                ]
                                prop.text (
                                    if System.String.IsNullOrEmpty r.Handle then
                                        "(token " + r.TokenId.Substring(0, 8) + "…)"
                                    else
                                        r.Handle
                                )
                            ]
                            Html.td [
                                prop.style [
                                    style.padding (length.em 0.5)
                                    style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                    style.fontSize (length.em 0.875)
                                ]
                                prop.text (r.IssuedAt.ToString("yyyy-MM-dd HH:mm"))
                            ]
                            Html.td [
                                prop.style [
                                    style.padding (length.em 0.5)
                                    style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                    style.fontSize (length.em 0.875)
                                ]
                                prop.text (
                                    match r.RespondedAt with
                                    | Some t -> t.ToString("yyyy-MM-dd HH:mm")
                                    | None -> "—"
                                )
                            ]
                            Html.td [
                                prop.style [
                                    style.padding (length.em 0.5)
                                    style.borderBottom (length.px 1, borderStyle.solid, "#e5e7eb")
                                ]
                                prop.text (
                                    if r.Revoked then
                                        "Revoked"
                                    elif r.RespondedAt.IsSome then
                                        "Responded"
                                    elif r.ExpiresAt < System.DateTimeOffset.UtcNow then
                                        "Expired"
                                    else
                                        "Pending"
                                )
                            ]
                        ]
                ]
            ]
        ]

[<ReactComponent>]
let SurveyDashboardView (schema: FormSchema) (summary: AggregationSummary) =
    Html.div [
        prop.style [ style.padding (length.em 1.0) ]
        prop.children [
            // Headline metrics.
            Html.div [
                prop.style [
                    style.display.flex
                    style.gap (length.em 2.0)
                    style.marginBottom (length.em 1.5)
                ]
                prop.children [
                    for label, value in
                        [
                            "Invited", string summary.InvitedCount
                            "Responded", string summary.SubmissionCount
                            "Response rate", formatPercent summary.ResponseRate
                        ] do
                        Html.div [
                            prop.children [
                                Html.div [
                                    prop.style [ style.color "#6b7280"; style.fontSize (length.em 0.875) ]
                                    prop.text label
                                ]
                                Html.div [
                                    prop.style [ style.fontSize (length.em 1.5); style.fontWeight 600 ]
                                    prop.text value
                                ]
                            ]
                        ]
                ]
            ]

            // Per-question aggregations.
            Html.h3 [ prop.text "Question responses" ]
            for field in schema.Fields do
                Html.div [
                    prop.style [
                        style.padding (length.em 0.75)
                        style.marginBottom (length.em 0.75)
                        style.backgroundColor "#ffffff"
                        style.borderRadius 4
                        style.border (length.px 1, borderStyle.solid, "#e5e7eb")
                    ]
                    prop.children [
                        Html.div [
                            prop.style [ style.fontWeight 600; style.marginBottom (length.em 0.5) ]
                            prop.text field.DisplayName
                        ]
                        match Map.tryFind field.Key summary.FieldAggregations with
                        | Some agg -> renderFieldAggregation agg
                        | None -> Html.div [ prop.style [ style.color "#9ca3af" ]; prop.text "(no data)" ]
                    ]
                ]

            // Recipient progress.
            Html.h3 [ prop.style [ style.marginTop (length.em 1.5) ]; prop.text "Recipients" ]
            renderRecipientTable summary.Recipients
        ]
    ]