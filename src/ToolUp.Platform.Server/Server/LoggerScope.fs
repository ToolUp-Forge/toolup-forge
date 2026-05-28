module ToolUp.Platform.LoggerScope

open System.Threading

// ─── LoggerScope — Phase 9e.1 ambient correlation context ───────────
//
// Ambient correlation-id context for structured logging. Handlers push
// a request-id / scope-id / trace-id once at the top of a unit of work;
// every `ILogger` call underneath picks the ids up automatically
// instead of threading keys through every call site.
//
// Backed by `AsyncLocal` (NOT thread-static): the context flows across
// `async` / `task` continuations and thread-pool hops, so an id pushed
// before an `await` is still ambient after it (GP 7 — the value rides
// the async chain). Pure BCL, no framework handles — the keys/values
// are plain strings (GP 12 rule 1: identity by value), so Phase 9l's
// `IActivitySink` can map `trace_id` straight onto the W3C trace-id and
// the same correlation surface serves both logs and spans.

/// Per-request correlation id. The top-level identifier tying every log
/// line, span, and audit event for one inbound request together.
[<Literal>]
let RequestId = "request_id"

/// Sub-scope id within a request — a job dispatch, a webhook delivery,
/// a module-query hop. Distinguishes nested units of work that share a
/// `RequestId`.
[<Literal>]
let ScopeId = "scope_id"

/// Distributed-trace id. Phase 9l populates this from the W3C
/// `traceparent`; until then a handler may set it explicitly. Kept as
/// a string so no tracing-framework type leaks onto the surface.
[<Literal>]
let TraceId = "trace_id"

let private ambient = AsyncLocal<Map<string, string>>()

/// The correlation context currently in scope. Empty when nothing has
/// been pushed (the `AsyncLocal` default is an unset reference).
let current () : Map<string, string> =
    match box ambient.Value with
    | null -> Map.empty
    | _ -> ambient.Value

/// Merge `pairs` onto the ambient context for the lifetime of the
/// returned scope; disposing restores the exact prior context (so
/// nested pushes pop in LIFO order). Later keys overwrite earlier ones.
let push (pairs: (string * string) list) : System.IDisposable =
    let prior = current ()
    let next = (prior, pairs) ||> List.fold (fun acc (k, v) -> Map.add k v acc)
    ambient.Value <- next

    { new System.IDisposable with
        member _.Dispose() = ambient.Value <- prior
    }

/// Run `f` with `pairs` merged onto the ambient context, restoring the
/// prior context afterward even if `f` throws.
let withScope (pairs: (string * string) list) (f: unit -> 'a) : 'a =
    use _ = push pairs
    f ()