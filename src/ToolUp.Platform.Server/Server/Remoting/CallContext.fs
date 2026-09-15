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
    // ── Phase 69i.E — the calling subject, carried the same way ─────────
    //
    // The dispatcher establishes the resolved caller's `IAuthContext.SubjectId`
    // for the duration of a request (and the companion routes do the same
    // for their own caller) so that the long-running substrate can stamp a
    // job with its submitting subject at `Enqueue` and re-establish it
    // INSIDE the job body — `CallContext.subjectId ()` read from within the
    // job answers with the original caller, not with whatever thread-pool
    // context the background work happens to run on. That is the whole of
    // "the job carries the subject id": sub-operations inside the job that
    // key on the caller (per-subject rate-limit buckets, idempotency scopes,
    // tenant-scoped stores resolved by subject) see the submitter.
    //
    // `None` means no subject was resolved for this request: either no
    // `AuthContextResolver` is composed, the method is `[<PublicEndpoint>]`
    // (the resolver is never invoked), or the resolved context is
    // anonymous. A job enqueued under `None` has no owner and is readable by
    // any caller — the pre-69i posture, preserved for consumers who have
    // not composed authentication (GP 11).

    let private subjectIdLocal = AsyncLocal<string>()

    /// Phase 69i.E — the calling subject's stable id (`IAuthContext.SubjectId`),
    /// or `None` when the current request resolved no subject.
    let subjectId () : string option =
        match subjectIdLocal.Value with
        | null -> None
        | value -> Some value

    // Phase 69i.G — whether the current caller may act on jobs it does not
    // own. Set by the dispatcher's companion routes when the resolved
    // `IAuthContext` holds `RemotingOptions.JobAdminRole`; never set for an
    // ordinary method call, so a handler polling the dispatcher from inside a
    // method body is bound by ownership exactly like a remote caller.
    let private jobAdminLocal = AsyncLocal<bool>()

    /// Phase 69i.G — true when the current caller holds the job-admin role
    /// (`RemotingOptions.JobAdminRole`) and may read / cancel any job.
    let isJobAdmin () : bool = jobAdminLocal.Value

    /// Phase 69i.E — establish the calling subject (and job-admin standing)
    /// for the current async flow; restores the prior values on dispose.
    let internal beginSubject (subject: string option) (jobAdmin: bool) : System.IDisposable =
        let priorSubject = subjectIdLocal.Value
        let priorAdmin = jobAdminLocal.Value

        subjectIdLocal.Value <-
            match subject with
            | Some s -> s
            | None -> null

        jobAdminLocal.Value <- jobAdmin

        { new System.IDisposable with
            member _.Dispose() =
                subjectIdLocal.Value <- priorSubject
                jobAdminLocal.Value <- priorAdmin
        }