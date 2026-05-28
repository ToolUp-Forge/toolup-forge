// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Severity levels for `ILogger`.
///
/// Ordered low-to-high in `compare` order so an `ILogger` implementation can
/// gate emission with `level >= configuredMinimum`. `Trace` is the new
/// addition for category-filtered diagnostic streams (`ai.sse`, `platform.sse`,
/// `auth`, etc.); `Debug`/`Info`/`Warn`/`Error` keep the same semantics they
/// had before this DU existed.
///
/// Implementations interpret levels per-call and per-category. The default
/// `ConsoleLogger` reads `ServerConfig.LogLevel` for the floor and
/// `ServerConfig.TraceCategories` for the per-category whitelist that gates
/// `Trace` calls. Empty `TraceCategories` + `LogLevel.Info` is the default —
/// behaviour identical to the code path before `Trace` was added.
/// `[<RequireQualifiedAccess>]` because `Error` collides with `Result.Error`
/// and `Debug` collides with `System.Diagnostics.Debug` — every reference
/// reads as `LogLevel.Error`, `LogLevel.Trace`, etc.
[<RequireQualifiedAccess>]
type LogLevel =
    | Trace
    | Debug
    | Info
    | Warn
    | Error

[<RequireQualifiedAccess>]
module LogLevel =
    /// Compare-order rank used by `ConsoleLogger` to gate `Debug`/`Info`/`Warn`/
    /// `Error` calls against the configured floor. `Trace` is gated by category
    /// rather than by rank — a category-allowed Trace call always emits even
    /// when the floor is `Warn`.
    let rank =
        function
        | LogLevel.Trace -> 0
        | LogLevel.Debug -> 1
        | LogLevel.Info -> 2
        | LogLevel.Warn -> 3
        | LogLevel.Error -> 4

    /// Parse `TOOLUP_LOG_LEVEL` env-var values. Case-insensitive. Returns
    /// `None` for unrecognised values; callers fall back to the default
    /// (typically `Info`) and log a warning.
    let tryParse (s: string) =
        if isNull s then
            None
        else
            match s.Trim().ToLowerInvariant() with
            | "trace" -> Some LogLevel.Trace
            | "debug" -> Some LogLevel.Debug
            | "info" -> Some LogLevel.Info
            | "warn"
            | "warning" -> Some LogLevel.Warn
            | "error" -> Some LogLevel.Error
            | _ -> None