// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

/// Which phase of the Elmish loop raised the exception. Populated by the
/// runtime so the reporter doesn't have to infer from the error message
/// string. Carries the offending message as `obj` for `Update` (consumers
/// can downcast to the concrete `Msg` type if they need to introspect).
[<RequireQualifiedAccess>]
type ErrorPhase =
    /// `init` raised — the program never got past startup.
    | Init
    /// `update` raised — the offending message is attached. Cast `Message`
    /// to your concrete `Msg` DU to introspect.
    | Update of message: obj
    /// `view` raised — the renderer threw during element construction.
    | View
    /// A `Sub` subscription's start / stop / dispatch threw. The
    /// originating `SubId` is attached as a slash-joined string.
    | Subscription of subId: string
    /// The termination handler threw during program teardown.
    | Termination

/// Structured error report passed to `Program.withErrorReporter`. Replaces
/// the upstream `(string * exn) -> unit` shape with enough context that
/// the reporter can route, tag, and correlate without parsing message
/// strings.
///
/// - `Phase` is populated by the runtime.
/// - `ModuleId` is populated by the runtime when the program is composed
///   under a parent shell that named the child module (the parent wraps
///   the reporter with `Program.scopeReporterToModule`).
/// - `CorrelationId` is populated by `ToolUp.Elmish.Tracing` middleware
///   when present, otherwise `None`. ToolUp.Remoting-issued requests
///   ambient-propagate the id; consumers can plumb it manually too.
/// - `Message` is the human-readable summary the runtime would have
///   passed to upstream Elmish's `onError`.
/// - `Exception` is the raw exception.
type ErrorContext = {
    Phase: ErrorPhase
    ModuleId: string option
    CorrelationId: string option
    Message: string
    Exception: exn
}

[<RequireQualifiedAccess>]
module ErrorContext =

    /// Construct a context from raw upstream-shape arguments. Used by the
    /// `withErrorHandler` compat shim — most code doesn't need to call this.
    let ofUpstreamShape (text: string) (ex: exn) = {
        Phase = ErrorPhase.Update(box ())
        ModuleId = None
        CorrelationId = None
        Message = text
        Exception = ex
    }

    /// Scope the reporter to a named module — any subsequent
    /// `ErrorContext` it receives will have `ModuleId = Some moduleId`.
    /// Used by parent-shell composition when child programs are
    /// erased under one runtime.
    let withModule (moduleId: string) (reporter: ErrorContext -> unit) : ErrorContext -> unit =
        fun ctx -> reporter { ctx with ModuleId = Some moduleId }

    /// Attach a correlation id to subsequent reports. Used by
    /// `ToolUp.Elmish.Tracing` middleware; consumers can call this
    /// directly if they're stamping ids at the dispatch layer.
    let withCorrelationId (corrId: string) (reporter: ErrorContext -> unit) : ErrorContext -> unit =
        fun ctx -> reporter { ctx with CorrelationId = Some corrId }