namespace ToolUp.Remoting.Server

open System.Runtime.CompilerServices
open System.Threading

// Grant adapter assemblies access to CallContext's internal setter so
// they can establish per-request ambient state without exposing the
// setter on the public surface (where call-site misuse could break
// AsyncLocal flow). Public consumers read via `CallContext.correlationId()`.
//
// 0.1.15 — InternalsVisibleTo grants for ToolUp.Remoting.AwsLambda and
// ToolUp.Remoting.AzureFunctions.Worker were removed alongside their
// scaffold demotion (IsPackable=false, sln-excluded). When per-platform
// parity ships for either, re-add the grant + the refuseUnsupportedSeams
// guard.
[<assembly: InternalsVisibleTo("ToolUp.Remoting.Giraffe")>]
[<assembly: InternalsVisibleTo("ToolUp.Remoting.Suave")>]
[<assembly: InternalsVisibleTo("ToolUp.Remoting.Falco")>]
[<assembly: InternalsVisibleTo("ToolUp.Remoting.AspNetCore")>]
do ()

/// Phase 69b.D — ambient per-request context.
///
/// `AsyncLocal`-backed primitives the dispatcher writes at request entry
/// and any handler can read. Currently exposes:
///
/// * `CorrelationId` — the request's correlation id. The Giraffe adapter
///   sets it from the `x-correlation-id` request header if present, or
///   generates a fresh GUID otherwise. The value flows through `Async`
///   continuations transparently (per `AsyncLocal` semantics), so handler
///   code reads it without threading the value through every signature
///   (honours GP 7 — correlation rides the async chain).
///
/// The dispatcher also stamps the chosen value back onto the response as
/// the `x-correlation-id` header so clients can correlate end-to-end.
///
/// Future seams (Phase 69b.B's per-request context resolver, Phase 66's
/// `Subject`) layer on the same AsyncLocal pattern.
module CallContext =

    let private correlationIdLocal = AsyncLocal<string>()

    /// Read the current correlation id for the in-flight request.
    /// Returns `None` when called outside a Remoting dispatch (e.g.
    /// in test code that hasn't booted the dispatcher), or for routes
    /// that aren't going through the Remoting handler.
    let correlationId () : string option =
        match correlationIdLocal.Value with
        | null -> None
        | value -> Some value

    /// Internal: dispatcher-only. Sets the correlation id for the
    /// in-flight `AsyncLocal` context. Called by the Giraffe adapter
    /// once per request.
    let internal setCorrelationId (value: string) : unit = correlationIdLocal.Value <- value

    /// Internal: dispatcher-only. Establish a per-request correlation
    /// id and return an `IDisposable` that restores the prior value
    /// on dispose. Use within `use _ = CallContext.beginRequest cid`
    /// so the AsyncLocal value can't leak into the next unrelated
    /// task that runs on the same thread-pool worker after the
    /// request completes.
    let internal beginRequest (value: string) : System.IDisposable =
        let priorValue = correlationIdLocal.Value
        correlationIdLocal.Value <- value

        { new System.IDisposable with
            member _.Dispose() = correlationIdLocal.Value <- priorValue
        }