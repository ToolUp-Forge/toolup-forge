// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Testing

open ToolUp.Platform

// ─── Loggers ──────────────────────────────────────────────────────────
//
// The two `ILogger` doubles a test needs: one that drops everything
// (`Logger.silent`) and one that records every line for an assertion
// (`Logger.recording ()`). The recording logger writes into a
// `Recording<LogLine>`, so the code under test may log from any thread
// while the test reads a snapshot.

/// The severity of a recorded log line, one case per `ILogger` member.
[<RequireQualifiedAccess>]
type LogSeverity =
    | Debug
    | Info
    | Warn
    | Error

/// One line an `ILogger` was asked to write.
type LogLine = {
    Severity: LogSeverity
    Message: string
    Exception: exn option
}

/// An `ILogger` that records every line it is given.
[<Sealed>]
type TestLogger() =
    let lines = Recording<LogLine>()

    let add severity message ex =
        lines.Add {
            Severity = severity
            Message = message
            Exception = ex
        }

    /// The recording itself, for a wait on a line yet to be logged.
    member _.Recording = lines

    /// Every line logged so far, oldest first.
    member _.Lines = lines.Snapshot()

    /// The messages logged so far at `severity`, oldest first.
    member _.MessagesAt(severity: LogSeverity) =
        lines.Snapshot()
        |> List.filter (fun line -> line.Severity = severity)
        |> List.map _.Message

    /// Whether any line logged so far contains `fragment`.
    member _.Contains(fragment: string) =
        lines.Snapshot() |> List.exists (fun line -> line.Message.Contains fragment)

    interface ILogger with
        member _.Debug message = add LogSeverity.Debug message None
        member _.Info message = add LogSeverity.Info message None
        member _.Warn message = add LogSeverity.Warn message None
        member _.Error(message, ex) = add LogSeverity.Error message ex

/// The `ILogger` doubles the Testing package ships.
[<RequireQualifiedAccess>]
module Logger =
    /// An `ILogger` that drops every line.
    let silent: ILogger =
        { new ILogger with
            member _.Debug _ = ()
            member _.Info _ = ()
            member _.Warn _ = ()
            member _.Error(_, _) = ()
        }

    /// A fresh logger that records every line it is given.
    let recording () = TestLogger()