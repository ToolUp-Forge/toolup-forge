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

    /// Phase 1069 — the ONE locale the toolkit formats numbers and
    /// currency in, named rather than scattered as a literal. It is the
    /// fallback until Phase 1075 passes the caller's locale into the
    /// toolkit; a caller that already holds a locale formats through
    /// `MessageCatalogProvider.formatNumber locale` instead.
    [<Literal>]
    let fallbackLocale = "en-GB"

    let formatCurrency value =
        (float value)?toLocaleString (
            fallbackLocale,
            {|
                style = "currency"
                currency = "GBP"
            |}
        )

    let formatDecimal (value: decimal) =
        (float value)?toLocaleString (
            fallbackLocale,
            {|
                minimumFractionDigits = 0
                maximumFractionDigits = 2
            |}
        )


    let formatInt (value: int) = value?toLocaleString fallbackLocale

    let formatDate (date: System.DateTime) = date.ToString("dd MMM yyyy")

    let formatPercentage (value: decimal) =
        match value with
        | v when v > 0m -> $"+%.1f{value}%%"
        | v when v < 0m -> $"%.1f{value}%%"
        | _ -> "0.0%"