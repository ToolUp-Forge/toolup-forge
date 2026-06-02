// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Visualisation

// ─── Common display + visualisation primitives ───────────────────
//
// Lightweight, domain-neutral types reused across the SDK and any
// module that renders charts or money values. Lives in the SDK rather
// than per-module so multiple modules don't duplicate them and don't
// have to import each other to share them. Originally lived in the
// `Toolup-SharedTypes` cross-module project; that project was retired
// when each type was reassigned to its rightful owner — domain-neutral
// pieces (these) landed here, sales-domain pieces moved into
// `SalesAnalysis`, SoV-SoM pieces moved into the `SOVSM` module.

/// Per-deployment display defaults that need to vary per team without
/// requiring a code change. Resolved from the `_platform` config map
/// via `PlatformDefaults.fromConfig`. Modules read the resolved record
/// at `Init` from `ClientModuleContext.PlatformConfig`, store the
/// fields they care about on their own `Model`, and thread them
/// through to view code. Server-side render paths use
/// `PlatformDefaultsResolver.resolve` to read from `IConfigStore`.
type PlatformDefaults = { CurrencySymbol: string }

module PlatformDefaults =
    /// Hardcoded fallback used when no `_platform` config has been
    /// loaded yet (pre-fetch seed) or when a field is missing /
    /// malformed in the persisted map. Deployments override per-scope
    /// through the team-config admin UI; a deployment that wants a
    /// different fallback registers a `_platform` entry in
    /// `ServerConfig.ModuleConfigs` whose `currencySymbol` field has a
    /// different `DefaultJson`.
    let defaults: PlatformDefaults = { CurrencySymbol = "£" }

    /// Strip surrounding double quotes from a JSON-encoded string
    /// literal. The `_platform` schema's `String` field kind persists
    /// values as JSON-quoted strings (e.g. `"£"`), so the raw map
    /// entry is `"\"£\""` — this strips the outer quotes back to the
    /// underlying value. Falls through unchanged when the value
    /// doesn't look quoted (defensive — older / hand-edited blobs).
    let private unquote (s: string) : string =
        let trimmed = s.Trim()

        if trimmed.Length >= 2 && trimmed.StartsWith "\"" && trimmed.EndsWith "\"" then
            trimmed.Substring(1, trimmed.Length - 2)
        else
            trimmed

    /// Parse a `_platform` config map (raw JSON-per-field, the same
    /// shape `IConfigStore.GetValues` returns and the shell hands to
    /// `ClientModuleContext.PlatformConfig`) into a typed
    /// `PlatformDefaults`. Falls back to `defaults` for any missing
    /// or malformed field — never throws. Compiles for both server
    /// and Fable: pure string handling, no JSON converter
    /// dependency.
    let fromConfig (platformConfig: Map<string, string>) : PlatformDefaults =
        let currency =
            platformConfig.TryFind "currencySymbol"
            |> Option.map unquote
            |> Option.filter (fun s -> s.Length > 0 && s.Length <= 4)
            |> Option.defaultValue defaults.CurrencySymbol

        { CurrencySymbol = currency }

/// One categorical (bar / pie) data point — a label plus a numeric
/// value. Generic; consumed by any module rendering a categorical
/// chart against `AgChart` bindings.
type ChartDataPointCategorical = { Label: string; Value: float }

/// One XY scatter / line / curve data point. Generic; consumed by any
/// module rendering an XY series against `AgChart` bindings.
type ChartDataPointXY = { X: float; Y: float }

module ChartDataPointXY =
    /// Pair two parallel float lists into a list of XY points. Both
    /// lists must be the same length; mismatched lengths fail at the
    /// `List.map2` call.
    let inline convertListsToPoints (xValues: float list) (yValues: float list) : ChartDataPointXY list =
        List.map2 (fun x y -> { X = x; Y = y }) xValues yValues

/// A line in slope-intercept form (`y = slope * x + intercept`).
/// Domain-neutral — consumed wherever a regression / line-fit result
/// has to be rendered as a curve over a chosen X range.
type LinearEquation = {
    Slope: float
    Intercept: float
} with

    /// Sample the line at `numPoints` evenly spaced X values across
    /// `[xMin, xMax]` and return the resulting XY series. Useful for
    /// drawing a fitted line on top of a scatter.
    member this.makePointsForLine (xMin: float) (xMax: float) (numPoints: int) : ChartDataPointXY list =
        let step = (xMax - xMin) / float numPoints

        [ xMin..step..xMax ]
        |> List.map (fun x -> {
            X = x
            Y = this.Slope * x + this.Intercept
        })