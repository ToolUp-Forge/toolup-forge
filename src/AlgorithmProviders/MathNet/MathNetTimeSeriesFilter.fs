// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AlgorithmProviders

open MathNet.Numerics.Statistics
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders.MathNetAlgorithmSupport

// ─── Phase 11.E.3 — ITimeSeriesFilter over Math.NET ─────────────────
//
// The eval's second divergence-3 row, and the one that compiled first
// time. `Statistics.MovingAverage` is TRAILING with an expanding
// warm-up; asked for a centred average it returns the right numbers one
// period late, and the result plots correctly. Nothing objects.
//
// Both halves of that sentence are used here rather than worked around:
//
//   * the expanding warm-up IS the `PartialWindow` policy, so under that
//     policy the library's series is used verbatim;
//   * the "right numbers one period late" IS the centred series, so
//     `CentredMean` is that same array RE-INDEXED, with explicit `None`
//     padding at both ends — which is the only thing the raw path was
//     missing, and the thing no caller can see it is missing.
//
// `SmoothingResult.Alignment` then carries the fact out, so a downstream
// consumer asserts on it instead of eyeballing a chart.

/// Window arithmetic for the two mean kinds, kept separate so the
/// re-indexing — the load-bearing part — is legible.
module private MathNetSmoothingWindows =

    /// The inclusive window bounds a CENTRED average at period `i`
    /// covers, for a window of length `window`.
    ///
    /// `lo = i − window/2`, `hi = i + (window − 1)/2`. For an odd window
    /// this is symmetric. For an EVEN window there is no exact centre,
    /// and this leans one period BACKWARD (a 4-window at period `i`
    /// covers `i−2 … i+1`) — the declared tie-break, stated in the
    /// algorithm's precision contract rather than left to be discovered.
    let centredBounds (window: int) (i: int) = i - window / 2, i + (window - 1) / 2

    /// Mean of `values[lo..hi]`, clamped to the series. Used only for
    /// partial windows at the edges; full windows come from the
    /// library's series.
    let clampedMean (values: float[]) (lo: int) (hi: int) =
        let lo = max 0 lo
        let hi = min (values.Length - 1) hi
        values[lo..hi] |> Array.average

/// `ITimeSeriesFilter` backed by Math.NET Numerics'
/// `Statistics.MovingAverage` for the windowed kinds, plus the standard
/// exponential recursion (which Math.NET does not ship).
type MathNetTimeSeriesFilter() =

    interface ITimeSeriesFilter with

        member _.Smooth request = async {
            match AlgorithmValidation.smoothing MathNetAlgorithmIds.Smooth request with
            | Error e -> return Error e
            | Ok() ->
                let values = request.Values
                let n = values.Length

                let smoothed =
                    match request.Kind with
                    | TrailingMean ->
                        // Math.NET's series verbatim: entries at and
                        // after the first full window are the trailing
                        // means; the earlier entries are the expanding
                        // partial means, which is precisely the
                        // `PartialWindow` policy.
                        let trailing = Statistics.MovingAverage(values, request.Window) |> Seq.toArray

                        Array.init n (fun i ->
                            if i + 1 >= request.Window || request.WarmUp = PartialWindow then
                                Some trailing[i]
                            else
                                None)

                    | CentredMean ->
                        let trailing = Statistics.MovingAverage(values, request.Window) |> Seq.toArray

                        Array.init n (fun i ->
                            let lo, hi = MathNetSmoothingWindows.centredBounds request.Window i

                            if lo >= 0 && hi <= n - 1 then
                                // The re-index: the centred value at `i`
                                // is the trailing value at the window's
                                // LAST period.
                                Some trailing[hi]
                            elif request.WarmUp = PartialWindow then
                                Some(MathNetSmoothingWindows.clampedMean values lo hi)
                            else
                                // The explicit padding at BOTH ends — the
                                // half-window the trailing series has no
                                // entry for, and the half-window at the
                                // head it silently filled.
                                None)

                    | ExponentiallyWeighted ->
                        // Seeded at the first observation, so every
                        // period is defined and no entry is `None` under
                        // either warm-up policy. `Alpha` is guaranteed
                        // present by the validation above.
                        let alpha = request.Alpha |> Option.defaultValue 1.0
                        let level = Array.zeroCreate<float> n

                        for i in 0 .. n - 1 do
                            level[i] <-
                                if i = 0 then
                                    values[0]
                                else
                                    alpha * values[i] + (1.0 - alpha) * level[i - 1]

                        level |> Array.map Some

                return
                    Ok {
                        Values = smoothed
                        Kind = request.Kind
                        Window =
                            if SmoothingKind.usesWindow request.Kind then
                                request.Window
                            else
                                0
                        // The contract (`ITimeSeriesFilter`): the
                        // alignment is derived from the kind, not left
                        // for the caller to infer from a chart that
                        // looks right either way.
                        Alignment = SmoothingAlignment.ofKind request.Kind
                        WarmUp = request.WarmUp
                    }
        }