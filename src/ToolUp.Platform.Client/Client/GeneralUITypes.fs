// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module GeneralUITypes

open Toolup.UIToolkit

type Toggle =
    | Show
    | Hide

    member this.Toggle() =
        match this with
        | Show -> Hide
        | Hide -> Show

type UpdateApp =
    | UpdateUIOnly
    | UpdateAndRunAnalysis

module CommonComponents =

    let createStringSelect (inputList: string list) (value: string) (dispatch: string -> unit) =
        Forms.dropdown (value) (fun (event: string) -> event |> dispatch) (inputList |> List.map (fun v -> v, v))

    let createIntegerSelect (availableInts: int list) (value: int) (toString: _ -> string) (dispatch: int -> unit) =
        Forms.dropdown
            (toString value)
            (fun (event: string) -> System.Int32.Parse event |> dispatch)
            (availableInts |> List.map (fun target -> toString target, toString target))