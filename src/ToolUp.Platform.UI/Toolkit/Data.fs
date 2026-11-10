// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform

module Data =

    /// How a column's header and cells align.
    type Align =
        | Left
        | Right
        | Center

    /// One column of a `table`: its header text (the caller's string), its
    /// alignment, and how a row renders into its cell.
    type Column<'row> = {
        Header: string
        Align: Align
        Cell: 'row -> ReactElement
    }

    let private alignClass =
        function
        | Left -> "text-left"
        | Right -> "text-right"
        | Center -> "text-center"

    /// The typed table: one header row from the columns, one body row per
    /// value. Since 0.42.0 (Phase 1073) it takes `Column<'row>` values in
    /// place of header strings and pre-rendered element rows.
    let table (columns: Column<'row> list) (rows: 'row list) =
        Html.div [
            prop.className "overflow-x-auto"
            prop.children [
                Html.table [
                    prop.className "w-full text-sm"
                    prop.children [
                        Html.thead [
                            Html.tr [
                                prop.className "border-b border-separator"
                                prop.children [
                                    for column in columns do
                                        Html.th [
                                            prop.className [
                                                alignClass column.Align
                                                "px-3 py-2 text-xs font-semibold uppercase tracking-wide"
                                                Tokens.Text.muted
                                            ]
                                            prop.text column.Header
                                        ]
                                ]
                            ]
                        ]
                        Html.tbody [
                            prop.children [
                                for i, row in List.indexed rows do
                                    Html.tr [
                                        prop.key i
                                        prop.className "border-b border-border last:border-b-0"
                                        prop.children [
                                            for column in columns do
                                                Html.td [
                                                    prop.className [
                                                        alignClass column.Align
                                                        "px-3 py-2 align-top"
                                                        Tokens.Text.primary
                                                    ]
                                                    prop.children [ column.Cell row ]
                                                ]
                                        ]
                                    ]
                            ]
                        ]
                    ]
                ]
            ]
        ]

    /// Column-oriented table component: supply headers and a list of columns (each column is a list of cell elements).
    /// Example usage:
    /// Data.columnTable [ "Item"; "Value"; "Status" ] [
    ///     [ Html.text "Row 1"; Html.text "Row 2"; Html.text "Row 3" ]   // first column
    ///     [ Html.text "100"; Html.text "200"; Html.text "150" ]         // second column
    ///     [ Html.text "Active"; Html.text "Pending"; Html.text "Complete" ] // third column
    /// ]
    let columnTable (headers: string list) (columns: ReactElement list list) =
        // truncate/align columns to header count
        let colCount = headers.Length
        let cols = columns |> List.truncate colCount

        // max number of rows is the longest column
        let maxRows =
            match cols |> List.map List.length with
            | [] -> 0
            | lengths -> List.max lengths

        let cellFor (colIdx: int) (rowIdx: int) =
            match cols |> List.tryItem colIdx with
            | None -> Html.none
            | Some col -> col |> List.tryItem rowIdx |> Option.defaultValue Html.none

        Html.table [
            prop.className "w-auto table-auto"
            prop.children [
                Html.thead [
                    Html.tr [
                        prop.className "border-b border-separator"
                        prop.children [
                            for i, header in List.indexed headers do
                                Html.th [
                                    prop.className [
                                        if i = 0 then "text-left px-6" else "text-right px-6"
                                        "py-3 text-base font-bold text-[var(--text)]"
                                    ]
                                    prop.text header
                                ]
                        ]
                    ]
                ]
                Html.tbody [
                    prop.children [
                        for rowIdx in 0 .. maxRows - 1 do
                            Html.tr [
                                prop.children [
                                    for colIdx in 0 .. colCount - 1 do
                                        Html.td [
                                            prop.className [
                                                if colIdx = 0 then
                                                    "text-left font-bold px-6"
                                                else
                                                    "text-right px-6"
                                                "py-1.5 text-base text-[var(--text-strong)]"
                                            ]
                                            prop.children [ cellFor colIdx rowIdx ]
                                        ]
                                ]
                            ]
                    ]
                ]
            ]
        ]


    type PillClickable =
        | AlwaysReact
        | OnlyWhenActive

    /// A reusable pill control for UI interactions, styled with the site's design tokens.
    let pill (label: string) (isActive: bool) (clickable: PillClickable) (onClick: string -> unit) =
        Html.div [
            prop.className (
                "px-4 py-2 rounded-xl cursor-pointer text-sm font-medium transition-colors "
                + if isActive then
                      $"{Tokens.Colours.brand} {Tokens.Colours.brandText} hover:{Tokens.Colours.brandHover}"
                  else
                      "bg-gray-200 text-[var(--text-strong)] hover:bg-gray-300"
            )
            prop.onClick (fun _ ->
                match isActive, clickable with
                | true, _ -> onClick label
                | false, AlwaysReact -> onClick label
                | false, OnlyWhenActive -> ())
            prop.text label
        ]


module Misc =
    let divider = Html.hr [ prop.className "border-gray-300 my-6" ]