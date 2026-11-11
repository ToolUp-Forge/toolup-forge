// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform

/// Number and currency formatting under the CALLER's locale.
///
/// Every function takes the BCP 47 tag the caller already holds — a view
/// reads it from its `RenderContext.Locale`, the same value
/// `MessageCatalogProvider.formatNumber` takes — so a deployment's `Locale`
/// governs every number the toolkit renders. The toolkit names no locale of
/// its own.
///
/// Under Fable each function is the browser's `Intl` (GP 13: no CLDR data in
/// the bundle); on .NET it is the runtime's culture data, so the rendering is
/// testable where the toolkit's tests run.
module OutputFormatting =

    let phiChar = string (char 0x03C6) // lowercase φ

#if !FABLE_COMPILER
    let private culture (locale: string) =
        System.Globalization.CultureInfo.GetCultureInfo locale
#endif

    /// A currency amount, e.g. `formatCurrency "de-DE" "EUR" 1234.5m`
    /// renders `1.234,50 €`. `currencyCode` is an ISO 4217 code.
    let formatCurrency (locale: string) (currencyCode: string) (value: decimal) : string =
#if FABLE_COMPILER
        (float value)?toLocaleString (
            locale,
            {|
                style = "currency"
                currency = currencyCode
            |}
        )
#else
        let numbers =
            (culture locale).NumberFormat.Clone() :?> System.Globalization.NumberFormatInfo

        // The culture's own symbol only for its own currency; any other
        // currency is shown by its code, as `Intl` does for an unfamiliar one.
        if System.Globalization.RegionInfo(locale).ISOCurrencySymbol <> currencyCode then
            numbers.CurrencySymbol <- currencyCode

        value.ToString("C2", numbers)
#endif

    /// A number with grouping and at most `maximumFractionDigits` (and at
    /// least `minimumFractionDigits`) fraction digits.
    let formatNumber
        (locale: string)
        (minimumFractionDigits: int)
        (maximumFractionDigits: int)
        (value: decimal)
        : string =
#if FABLE_COMPILER
        (float value)?toLocaleString (
            locale,
            {|
                minimumFractionDigits = minimumFractionDigits
                maximumFractionDigits = maximumFractionDigits
            |}
        )
#else
        let pattern =
            "#,##0"
            + (if maximumFractionDigits > 0 then
                   "."
                   + String.replicate minimumFractionDigits "0"
                   + String.replicate (maximumFractionDigits - minimumFractionDigits) "#"
               else
                   "")

        System.Math.Round(value, maximumFractionDigits).ToString(pattern, culture locale)
#endif

    /// A decimal with grouping and up to two fraction digits.
    let formatDecimal (locale: string) (value: decimal) : string = formatNumber locale 0 2 value

    /// An integer with grouping.
    let formatInt (locale: string) (value: int) : string = formatNumber locale 0 0 (decimal value)

    /// The locale's grouping and decimal separators, as `group * decimal`.
    let separators (locale: string) : string * string =
#if FABLE_COMPILER
        // `1234.5` renders with exactly one grouping and one decimal mark.
        let probe = 1234.5
        let sample: string = probe?toLocaleString locale
        let digits = set [ '0' .. '9' ]

        let marks =
            sample
            |> Seq.filter (fun c -> not (digits.Contains c))
            |> Seq.map string
            |> List.ofSeq

        match marks with
        | [ group; decimalMark ] -> group, decimalMark
        | [ decimalMark ] -> "", decimalMark
        | _ -> ",", "."
#else
        let numbers = (culture locale).NumberFormat
        numbers.NumberGroupSeparator, numbers.NumberDecimalSeparator
#endif

    /// Read back a number a person typed in `locale`'s form: grouping marks
    /// and spaces dropped, the locale's decimal mark read as the point.
    /// `None` when the text is not a number.
    let parseDecimal (locale: string) (text: string) : decimal option =
        let group, decimalMark = separators locale

        let withoutGroups =
            text.Replace(" ", "").Replace(" ", "").Replace(" ", "")
            |> fun s -> if group = "" then s else s.Replace(group, "")

        let normalised = withoutGroups.Replace(decimalMark, ".")

        match
            System.Decimal.TryParse(
                normalised,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture
            )
        with
        | true, n -> Some n
        | false, _ -> None

    let formatDate (date: System.DateTime) = date.ToString("dd MMM yyyy")

    let formatPercentage (value: decimal) =
        match value with
        | v when v > 0m -> $"+%.1f{value}%%"
        | v when v < 0m -> $"%.1f{value}%%"
        | _ -> "0.0%"