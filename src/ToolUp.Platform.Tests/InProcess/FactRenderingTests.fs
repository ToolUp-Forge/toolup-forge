// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactRenderingTests

// ─── Money renders in the metric's declared currency ─────────────────
//
// `FactRendering.renderFor` reads the metric: a `currency` metric whose unit
// is an ISO 4217 code and whose format is a `C` format renders with that
// currency's symbol, from a table in Facts.Core rather than from culture
// data. What this pack pins:
//
//   1. GBP / EUR / USD under `C0` and `C2`, with the invariant grouping,
//      decimal separator and negative form.
//   2. Everything else renders exactly as `render` does: a non-currency
//      metric, a currency metric under an `N` or a custom format, a unit
//      that is not an ISO code, and no metric at all.
//   3. An unknown ISO code renders with the code as its prefix, never `¤`.
//   4. The same results with no ICU culture data: the cases re-run in a
//      child process started with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
//      (`invariantGlobalizationTests`, in the pack's slow set).

open System
open System.Diagnostics
open System.Globalization
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open ToolUp.Facts
open ToolUp.Platform.Grounding

let private metric (unitCode: string) (dimensionality: string) (format: string) : MetricDefinition = {
    Id = "revenue"
    Name = "Revenue"
    Unit = unitCode
    Dimensionality = dimensionality
    Direction = HigherIsBetter
    DisplayFormat = format
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private money (unitCode: string) (format: string) = Some(metric unitCode "currency" format)

/// Every expectation the pack holds, as (description, rendered, expected), so
/// the in-process cases and the invariant-globalization child assert the
/// same table.
let private expectations () : (string * string * string) list = [
    "GBP C0", FactRendering.renderFor (money "GBP" "C0") (Scalar 21240m), "£21,240"
    "GBP C2", FactRendering.renderFor (money "GBP" "C2") (Scalar 21240m), "£21,240.00"
    "EUR C0", FactRendering.renderFor (money "EUR" "C0") (Scalar 1299m), "€1,299"
    "EUR C2", FactRendering.renderFor (money "EUR" "C2") (Scalar 1299m), "€1,299.00"
    "USD C0", FactRendering.renderFor (money "USD" "C0") (Scalar 7729m), "$7,729"
    "USD C2", FactRendering.renderFor (money "USD" "C2") (Scalar 7729.5m), "$7,729.50"
    "GBP C0 rounds as C0 does", FactRendering.renderFor (money "GBP" "C0") (Scalar 21240.5m), "£21,241"
    "a negative keeps the invariant form", FactRendering.renderFor (money "GBP" "C0") (Scalar -21240m), "(£21,240)"
    "an interval", FactRendering.renderFor (money "GBP" "C0") (Interval(1000m, 2000m)), "£1,000 to £2,000"
    "a lowercase code and format", FactRendering.renderFor (money "gbp" "c0") (Scalar 21240m), "£21,240"
    "an unknown ISO code is its own prefix", FactRendering.renderFor (money "CHF" "C0") (Scalar 1299m), "CHF 1,299"
    "a non-currency metric under N0",
    FactRendering.renderFor (Some(metric "count" "count" "N0")) (Scalar 21240m),
    "21,240"
    "a currency metric under N0", FactRendering.renderFor (money "GBP" "N0") (Scalar 21240m), "21,240"
    "a currency metric under a custom format", FactRendering.renderFor (money "GBP" "£#,##0") (Scalar 21240m), "£21,240"
    "no metric renders verbatim", FactRendering.renderFor None (Scalar 21240.5m), "21240.5"
    "render under C0 is unchanged", FactRendering.render "C0" (Scalar 21240m), "¤21,240"
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
                let rendered = FactRendering.renderFor (money code "C0") (Scalar 21240m)
                Expect.isFalse (rendered.Contains "¤") (sprintf "%s rendered %s" code rendered)
                Expect.equal rendered (CurrencySymbols.table[code] + "21,240") code

        testCase "a non-currency metric and a non-ISO unit render as render does"
        <| fun _ ->
            for m in
                [
                    metric "count" "count" "N0"
                    metric "%" "ratio" "P1"
                    metric "£" "currency" "C0"
                    metric "GBP" "currency" "F2"
                    metric "GBP" "currency" ""
                ] do
                for value in
                    [
                        Scalar 21240.456m
                        Scalar -3m
                        Interval(1m, 2m)
                        Categorical "x"
                        Absent "gap"
                    ] do
                    Expect.equal
                        (FactRendering.renderFor (Some m) value)
                        (FactRendering.render m.DisplayFormat value)
                        (sprintf "%s %s %s" m.Unit m.Dimensionality m.DisplayFormat)

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