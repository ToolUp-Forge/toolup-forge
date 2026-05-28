namespace ToolUp.Platform

/// Phase 6h follow-up — Workstream B. Server-side helpers for callers
/// that want to emit at a specific level without caring whether the
/// underlying logger implements the optional capabilities. The helpers
/// do the cap-check + no-op on each call so call sites stay terse.
///
/// Lives in `Server/` rather than `Shared/` because Fable can't reliably
/// type-test F# interfaces — the only consumer of this helper is
/// server-side F# anyway.
module Logger =
    /// Emit a category-gated trace line if the logger supports it.
    /// No-op on plain `ILogger` instances. Cheap when uninterested
    /// (one type test + one branch).
    let trace (logger: ILogger) (category: string) (message: string) =
        match logger with
        | :? ITraceLogger as t -> t.Trace(category, message)
        | _ -> ()