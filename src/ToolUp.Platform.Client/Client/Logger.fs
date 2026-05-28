// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Logger

open Fable.Core

/// Default client-tier `ILogger` implementation — routes severity-tagged
/// messages to severity-appropriate `Fable.Core.JS.console` methods so
/// devtools render them with the matching icon and stack-trace
/// affordance:
///
///   `Debug` → `console.log`
///   `Info`  → `console.info`
///   `Warn`  → `console.warn`
///   `Error` → `console.error`
///
/// Each message is prefixed `[<category>] ` when the logger was built
/// with `Some category`, and unprefixed when built with `None`. Errors
/// pass the exception object as a second argument to `console.error`
/// when supplied; Fable lifts `Message` + `StackTrace` into the JS
/// error shape so devtools render the stack one click away.
///
/// Downstream client-tier modules that need a non-default sink (a
/// structured-log forwarder, a level-filter decorator) receive their
/// `ILogger` through init parameters — the same shape server-tier
/// modules already use. No global swap-point is provided; module-level
/// `mutable` would need explicit justification per the SDK's existing
/// conventions, and `Logger.defaultLogger` / `Logger.forCategory` cover
/// the common case without it.
type ConsoleLogger(category: string option) =
    let prefix =
        match category with
        | Some c -> $"[{c}] "
        | None -> ""

    interface ILogger with
        member _.Debug message = JS.console.log $"{prefix}{message}"
        member _.Info message = JS.console.info $"{prefix}{message}"
        member _.Warn message = JS.console.warn $"{prefix}{message}"

        member _.Error(message, ex) =
            match ex with
            | Some e -> JS.console.error ($"{prefix}{message}", e)
            | None -> JS.console.error $"{prefix}{message}"

/// Uncategorised singleton. `console.*` writes the bare message with
/// no `[category]` prefix. Use when there is no meaningful category to
/// assign — most call sites should prefer `forCategory` so the prefix
/// gives a grep-handle in the devtools output.
let defaultLogger: ILogger = ConsoleLogger(None) :> ILogger

/// Build a categorised `ILogger`. Categories follow a dotted-namespace
/// convention matching the server-side `ITraceLogger` vocabulary
/// (`ai.sse`, `ai.agent`, `auth`, `client.bootstrap`, etc.) so client
/// and server diagnostics share a category space when correlated.
///
/// Module-level placement is the convention — declare one
/// `let log = Logger.forCategory "<category>"` value at the top of
/// each file, immediately after `open` declarations, and let call
/// sites read `log.Warn "..."` directly. See
/// `docs/platform/client-logging.md` for the convention in full.
let forCategory (category: string) : ILogger = ConsoleLogger(Some category) :> ILogger