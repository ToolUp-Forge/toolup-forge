// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform

module OutputFormatting =

    let phiChar = string (char 0x03C6) // lowercase φ

    let formatCurrency value =
        (float value)?toLocaleString (
            "en-GB",
            {|
                style = "currency"
                currency = "GBP"
            |}
        )

    let formatDecimal (value: decimal) =
        (float value)?toLocaleString (
            "en-GB",
            {|
                minimumFractionDigits = 0
                maximumFractionDigits = 2
            |}
        )


    let formatInt (value: int) = value?toLocaleString "en-GB"

    let formatDate (date: System.DateTime) = date.ToString("dd MMM yyyy")

    let formatPercentage (value: decimal) =
        match value with
        | v when v > 0m -> $"+%.1f{value}%%"
        | v when v < 0m -> $"%.1f{value}%%"
        | _ -> "0.0%"