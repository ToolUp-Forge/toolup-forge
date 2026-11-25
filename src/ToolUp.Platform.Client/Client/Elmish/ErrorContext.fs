// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

/// Which part of the shell's program raised the exception, so a reporter
/// does not have to infer it from the message text. Carries the offending
/// message as `obj` for `Update` (downcast to the concrete `Msg` type to
/// introspect it).
[<RequireQualifiedAccess>]
type ErrorPhase =
    /// `init` raised, or a boot step was refused: the program never got past startup.
    | Init
    /// `update` raised, or an effect's performer threw. The offending message
    /// is attached when there is one (`box ()` for a performer). Cast
    /// `Message` to your concrete `Msg` DU to introspect.
    | Update of message: obj
    /// `view` raised: the renderer threw during element construction.
    | View
    /// A resource's start or stop threw. Its key is attached as a
    /// slash-joined string.
    | Subscription of subId: string

/// Structured error report passed to `ClientConfig`'s `OnElmishError` sink:
/// enough context that the reporter can route, tag and correlate without
/// parsing message strings.
///
/// - `Phase` says where it was raised.
/// - `ModuleId` is set by a reporter wrapped with `ErrorContext.withModule`.
/// - `CorrelationId` is set by a reporter wrapped with
///   `ErrorContext.withCorrelationId`.
/// - `Message` is the human-readable summary.
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

    /// Format a message for diagnostics without ever crashing the caller. A
    /// message can legitimately carry a large payload (e.g. an uploaded file's
    /// raw contents); `sprintf "%A"` on multi-MB data can blow the JS call
    /// stack ("Maximum call stack size exceeded"). That overflow must not
    /// propagate through the error reporter — which would re-format the same
    /// message and overflow again, recursing through the reporter itself.
    /// Total + bounded: catches the overflow and caps the output length.
    let safeMsgRepr (msg: 'msg) : string =
        try
            let s = sprintf "%A" msg

            if s.Length > 1000 then
                s.Substring(0, 1000) + "…(truncated)"
            else
                s
        with _ ->
            "<message could not be formatted (large or cyclic payload)>"

    /// Scope the reporter to a named module — any subsequent
    /// `ErrorContext` it receives will have `ModuleId = Some moduleId`.
    let withModule (moduleId: string) (reporter: ErrorContext -> unit) : ErrorContext -> unit =
        fun ctx -> reporter { ctx with ModuleId = Some moduleId }

    /// Attach a correlation id to subsequent reports, for a consumer stamping
    /// ids at the dispatch layer.
    let withCorrelationId (corrId: string) (reporter: ErrorContext -> unit) : ErrorContext -> unit =
        fun ctx -> reporter { ctx with CorrelationId = Some corrId }