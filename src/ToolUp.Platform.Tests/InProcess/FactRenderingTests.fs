// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactRenderingTests

// ─── Money renders in the metric's declared currency ─────────────────
//
// `FactRendering.renderFor` reads the metric: a metric whose unit is one
// currency and whose display format is the `Currency` style renders with that
// currency's symbol, from the kernel's table rather than from culture data.
// What this pack pins:
//
//   1. GBP / EUR / USD with no and with two fraction digits, with the
//      invariant grouping, decimal separator and negative form (`-£1,235`).
//   2. Every other metric shows the number with no currency symbol: a
//      non-currency metric, a currency unit under a number, percent or
//      verbatim format. A percent has no space before its sign (`12.3%`).
//      With no metric at all, a value is shown verbatim.
//   3. An unknown ISO code renders with the code as its prefix, never `¤`.
//   4. The stored legacy forms forge's metric definitions were written in
//      convert, or are refused rather than rendered with a guessed symbol.
//   5. The same results with no ICU culture data: the cases re-run in a
//      child process started with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
//      (`invariantGlobalizationTests`, in the pack's slow set).

open System
open System.Diagnostics
open System.Globalization
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open Fuaran.Facts

let private ok (result: Result<'a, Refusal>) : 'a =
    match result with
    | Ok value -> value
    | Error refusal -> failwithf "unexpected refusal: %s" (Refusal.describe refusal)

let private format (style: DisplayStyle) (digits: int) (grouping: bool) : DisplayFormat = {
    Style = style
    FractionDigits = Some digits
    Grouping = grouping
}

let private currency digits =
    format DisplayStyle.Currency digits true

let private grouped digits = format DisplayStyle.Number digits true
let private ungrouped digits = format DisplayStyle.Number digits false
let private percent digits = format DisplayStyle.Percent digits true

/// A metric definition over a unit written the way forge's registrations
/// stored it (`GBP`, `count`, `%`), converted by the kernel's legacy table.
let private metric (unitText: string) (dimensionality: string) (displayFormat: DisplayFormat) : MetricDefinition = {
    Id = "revenue"
    Name = "Revenue"
    Unit = ok (Legacy.unitOf unitText)
    Dimensionality = dimensionality
    Direction = HigherIsBetter
    DisplayFormat = displayFormat
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RollUp = None
    Context = None
    RecomputePolicy = None
}

let private money (code: string) (displayFormat: DisplayFormat) =
    Some(metric code "currency" displayFormat)

let private scalar (value: decimal) = Scalar(DecimalText.ofDecimal value)

/// Every expectation the pack holds, as (description, rendered, expected), so
/// the in-process cases and the invariant-globalization child assert the
/// same table.
let private expectations () : (string * string * string) list = [
    "GBP 0 digits", FactRendering.renderFor (money "GBP" (currency 0)) (scalar 21240m), "£21,240"
    "GBP 2 digits", FactRendering.renderFor (money "GBP" (currency 2)) (scalar 21240m), "£21,240.00"
    "EUR 0 digits", FactRendering.renderFor (money "EUR" (currency 0)) (scalar 1299m), "€1,299"
    "EUR 2 digits", FactRendering.renderFor (money "EUR" (currency 2)) (scalar 1299m), "€1,299.00"
    "USD 0 digits", FactRendering.renderFor (money "USD" (currency 0)) (scalar 7729m), "$7,729"
    "USD 2 digits", FactRendering.renderFor (money "USD" (currency 2)) (scalar 7729.5m), "$7,729.50"
    "GBP rounds half away from zero", FactRendering.renderFor (money "GBP" (currency 0)) (scalar 21240.5m), "£21,241"
    "a negative carries a leading minus",
    FactRendering.renderFor (money "GBP" (currency 0)) (scalar -21240m),
    "-£21,240"
    "a negative rounds away from zero", FactRendering.renderFor (money "GBP" (currency 0)) (scalar -1234.5m), "-£1,235"
    "an interval",
    FactRendering.renderFor
        (money "GBP" (currency 0))
        (Interval(DecimalText.ofDecimal 1000m, DecimalText.ofDecimal 2000m)),
    "£1,000 to £2,000"
    "an unknown ISO code is its own prefix",
    FactRendering.renderFor (money "CHF" (currency 0)) (scalar 1299m),
    "CHF 1,299"
    "a non-currency metric, grouped",
    FactRendering.renderFor (Some(metric "count" "count" (grouped 0))) (scalar 21240m),
    "21,240"
    "a currency metric under a grouped number",
    FactRendering.renderFor (money "GBP" (grouped 0)) (scalar 21240m),
    "21,240"
    "a currency metric under an ungrouped number",
    FactRendering.renderFor (money "GBP" (ungrouped 2)) (scalar 21240.456m),
    "21240.46"
    "a currency metric under the verbatim format",
    FactRendering.renderFor (money "GBP" DisplayFormat.verbatim) (scalar 21240.456m),
    "21240.456"
    "a percent has no space before its sign",
    FactRendering.renderFor (Some(metric "%" "ratio" (percent 1))) (scalar 0.123m),
    "12.3%"
    "no metric renders verbatim", FactRendering.renderFor None (scalar 21240.5m), "21240.5"
    "no metric keeps the carried scale", FactRendering.renderFor None (scalar 1.50m), "1.50"
]

let private assertAll () =
    for description, rendered, expected in expectations () do
        Expect.equal rendered expected description

/// Set in the child process, so the child runs the invariant case and
/// never starts a child of its own.
[<Literal>]
let private ChildMarker = "TOOLUP_FACT_RENDERING_INVARIANT_CHILD"

[<Literal>]
let private ChildCaseName =
    "the table holds inside the invariant-globalization child"

let tests =
    testList "FactRendering — money in the declared currency" [
        testCase "every expectation holds" <| fun _ -> assertAll ()

        testCase "a known currency never renders the generic symbol"
        <| fun _ ->
            for code in CurrencySymbols.table |> Map.keys do
                let rendered = FactRendering.renderFor (money code (currency 0)) (scalar 21240m)
                Expect.isFalse (rendered.Contains "¤") (sprintf "%s rendered %s" code rendered)
                Expect.equal rendered (CurrencySymbols.prefix code + "21,240") code

        testCase "only a currency format over a currency unit shows a symbol"
        <| fun _ ->
            for m in
                [
                    metric "count" "count" (grouped 0)
                    metric "%" "ratio" (percent 1)
                    metric "GBP" "currency" (ungrouped 2)
                    metric "GBP" "currency" (grouped 0)
                    metric "GBP" "currency" DisplayFormat.verbatim
                ] do
                let label =
                    sprintf "%s %s %A" (MetricUnit.text m.Unit) m.Dimensionality m.DisplayFormat

                for value in
                    [
                        scalar 21240.456m
                        scalar -3m
                        Interval(DecimalText.ofDecimal 1m, DecimalText.ofDecimal 2m)
                    ] do
                    let rendered = FactRendering.renderFor (Some m) value

                    for symbol in [ "£"; "¤"; "GBP" ] do
                        Expect.isFalse (rendered.Contains symbol) (sprintf "%s rendered %s" label rendered)

                // A value with no magnitude reads the same under any metric.
                for value in [ Categorical "x"; Absent "gap" ] do
                    Expect.equal
                        (FactRendering.renderFor (Some m) value)
                        (FactRendering.renderFor None value)
                        (sprintf "%s %A" label value)

        testCase "a currency format over a unit that is not a currency is refused, never shown with a symbol"
        <| fun _ ->
            let notMoney = metric "count" "count" (currency 0)
            Expect.isError (Definition.checkMetric notMoney) "a currency format needs a currency unit"
            let rendered = FactRendering.renderFor (Some notMoney) (scalar 21240m)
            Expect.isFalse (rendered.Contains "¤") (sprintf "rendered %s" rendered)
            Expect.isFalse (rendered.Contains "£") (sprintf "rendered %s" rendered)

        testCase "the stored legacy forms convert, or are refused rather than guessed"
        <| fun _ ->
            Expect.equal (ok (Legacy.displayFormatOf "C0")) (currency 0) "C0 is a grouped currency"
            Expect.equal (ok (Legacy.displayFormatOf "c0")) (currency 0) "the format letter's case is not significant"
            Expect.equal (ok (Legacy.displayFormatOf "N0")) (grouped 0) "N0 is a grouped number"
            Expect.equal (ok (Legacy.displayFormatOf "F2")) (ungrouped 2) "F2 is an ungrouped number"
            Expect.equal (ok (Legacy.displayFormatOf "P1")) (percent 1) "P1 is a percent"
            Expect.equal (ok (Legacy.displayFormatOf "")) DisplayFormat.verbatim "the empty format is verbatim"

            Expect.equal
                (FactRendering.renderFor (money "GBP" (ok (Legacy.displayFormatOf "c0"))) (scalar 21240m))
                "£21,240"
                "a lowercase legacy format renders as its uppercase form"

            Expect.isError
                (Legacy.displayFormatOf "£#,##0")
                "a custom pattern with a literal symbol is refused: the currency is the unit's"

            Expect.isError (Legacy.unitOf "gbp") "a lowercase currency code is refused, not read as GBP"
            Expect.isError (Legacy.unitOf "£") "a bare symbol is not a unit"

        testCase "the rendering ignores the current culture"
        <| fun _ ->
            let saved = CultureInfo.CurrentCulture

            try
                for name in [ "de-DE"; "fr-FR"; "ja-JP"; "en-GB" ] do
                    CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo name
                    assertAll ()
            finally
                CultureInfo.CurrentCulture <- saved
    ]

/// The same table with no ICU culture data, in a child run of this pack. A
/// list of its own because starting the pack costs seconds: it is in the
/// pack's slow set, so the fast lane keeps the in-process cases above.
let invariantGlobalizationTests =
    testList "FactRendering — money in the declared currency, invariant globalization" [
        testCase ChildCaseName
        <| fun _ ->
            if isNull (Environment.GetEnvironmentVariable ChildMarker) then
                skiptest "runs only inside the child the next case starts"
            else
                // Prove the child really has no culture data: a named
                // culture is either refused outright (the default under
                // invariant globalization) or is the invariant one, whose
                // currency symbol is the generic `¤`.
                let britishSymbol =
                    try
                        Some (CultureInfo.GetCultureInfo "en-GB").NumberFormat.CurrencySymbol
                    with :? CultureNotFoundException ->
                        None

                Expect.notEqual britishSymbol (Some "£") "the child runs with invariant globalization"

                assertAll ()

        testCase "the same results with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"
        <| fun _ ->
            if not (isNull (Environment.GetEnvironmentVariable ChildMarker)) then
                skiptest "the child does not start a child"
            else
                let pack = Assembly.GetEntryAssembly().Location

                let psi =
                    ProcessStartInfo(
                        "dotnet",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    )

                psi.ArgumentList.Add pack
                psi.ArgumentList.Add "--filter-test-case"
                psi.ArgumentList.Add ChildCaseName
                psi.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] <- "1"
                psi.Environment[ChildMarker] <- "1"

                use child = Process.Start psi
                let stdout = child.StandardOutput.ReadToEndAsync()
                let stderr = child.StandardError.ReadToEndAsync()
                child.WaitForExit()
                // The console logger colours its summary; read it plain.
                let output =
                    Regex.Replace(stdout.Result + stderr.Result, "\u001b\\[[0-9;]*[A-Za-z]", "")

                Expect.equal child.ExitCode 0 (sprintf "the child pack run failed:\n%s" output)
                // A filter that matches nothing also exits 0, so the count
                // is the evidence the case ran.
                Expect.stringContains
                    output
                    "1 passed, 0 ignored, 0 failed, 0 errored"
                    (sprintf "the child ran exactly the one case:\n%s" output)
    ]