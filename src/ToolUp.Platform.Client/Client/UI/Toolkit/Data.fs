// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform

module Data =

    /// Table component
    let table (headers: string list) rows =
        Html.table [
            // keep sizing as desired (w-auto/table-auto or w-full)
            prop.className "w-auto table-auto"
            prop.children [
                Html.thead [
                    Html.tr [
                        prop.className "border-b border-separator"
                        prop.children [
                            for i, header in List.indexed headers do
                                Html.th [
                                    prop.className [
                                        // add horizontal padding (px-6) to increase space between columns
                                        if i = 0 then "text-left px-6" else "text-right px-6"
                                        "py-3 text-base font-bold text-gray-700"
                                    ]
                                    prop.text header
                                ]
                        ]
                    ]
                ]
                //Html.div [ prop.className "py-1" ]
                Html.tbody [
                    prop.children [
                        for row in rows do
                            Html.tr [
                                prop.children [
                                    for i, cell in List.indexed row do
                                        Html.td [
                                            prop.className [
                                                // match header padding for body cells
                                                if i = 0 then
                                                    "text-left font-bold px-6"
                                                else
                                                    "text-right px-6"
                                                "py-1.5 text-base text-gray-900"
                                            ]
                                            prop.children [ cell ]
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
                                        "py-3 text-base font-bold text-gray-700"
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
                                                "py-1.5 text-base text-gray-900"
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
                      "bg-gray-200 text-gray-800 hover:bg-gray-300"
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