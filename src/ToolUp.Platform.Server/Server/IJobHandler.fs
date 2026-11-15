namespace ToolUp.Platform.Jobs

open System
open ToolUp.Platform

// ─── IJobHandler ─────────────────────────────────────────────────
//
// Per-handler business-logic interface for the background-job
// substrate (Phase 9b). Modules implement this once per logical job
// type they want the platform to schedule on their behalf, then
// register it with the scheduler at compose time via the module's
// `Server.fs`.
//
// **Stateless between invocations** (Phase 9c Rule 4). Handlers
// receive every piece of state they need through `JobContext +
// payload` — no in-memory state survives across calls. Orleans can
// deactivate the grain hosting the handler; Akka.Persistence can
// restart its actor; the in-process scheduler can recycle worker
// threads. Implementations that cache state in module-level mutables
// will silently lose it on any of those events.
//
// **Async at every boundary** (Phase 9c Rule 2). `Execute` returns
// `Async<JobResult>` — never `unit` and never `Task<unit>`. Long-
// running jobs that need progress reporting publish `JobProgress`
// notifications via `INotificationChannel`; the handler return
// value is the terminal outcome.
//
// **Pre-deserialised payload not in the contract.** `JobContext`
// carries `Payload: string` (the persisted JSON form). Handlers
// deserialise into their own DTO inside `Execute` and treat
// deserialisation failure as `PermanentFailure` (a malformed payload
// will not recover by retrying).

type IJobHandler =
    /// Execute one attempt of the job. Implementations should:
    ///   1. Deserialise `ctx.Payload` into the handler's expected
    ///      DTO. Return `PermanentFailure` on parse error.
    ///   2. Perform the unit of work, honouring `ctx.AccessContext`
    ///      for any team-scoped reads / writes.
    ///   3. Return `Success` on completion, `TransientFailure` for
    ///      retryable errors (DB lock, rate-limited upstream),
    ///      `PermanentFailure` for terminal errors (malformed
    ///      payload, missing prerequisite the next attempt won't
    ///      fix).
    /// Exceptions thrown out of `Execute` are caught by the
    /// scheduler, logged via `ILogger`, and treated as
    /// `TransientFailure` — handlers that want explicit terminal
    /// failure must return `PermanentFailure` rather than throwing.
    abstract Execute: ctx: JobContext -> Async<JobResult>

// ─── A payload never names another job's scope ───────────────────
//
// A job runs under the scope it was registered under (`ctx.ScopeId`). A
// handler whose payload ALSO names a scope (a target, a container, a
// descriptor) acts on the payload's scope, so a payload that names a scope
// other than the job's own reaches a tenant nobody scheduled it for. Every
// platform path that schedules such a handler registers the job under the
// very scope its payload names, so the comparison costs a legitimate job
// nothing. The one exception is a job registered under the platform
// container (`_platform`), which acts on a tenant by design (the OAuth token
// refresh): the job API refuses a caller whose resolved scope is the
// platform container, so that scope is a registration field the wire
// cannot set.

/// The comparison a handler makes before it acts on a scope its
/// payload names. Each check answers `Error` with an operator-readable
/// reason; the handler returns it as `PermanentFailure`, since no retry
/// makes a payload name a different scope.
[<RequireQualifiedAccess>]
module JobPayloadScope =

    let private sameScope (left: string) (right: string) =
        String.Equals(left, right, StringComparison.Ordinal)

    let private refusal (ctx: JobContext) (named: string) =
        $"the job's payload names scope '{named}', but job {ctx.JobId} runs under scope '{ctx.ScopeId}'"

    /// `Ok` when `payloadScopeId` is the scope the job runs under.
    let requireOwn (ctx: JobContext) (payloadScopeId: string) : Result<unit, string> =
        if sameScope payloadScopeId ctx.ScopeId then
            Ok()
        else
            Error(refusal ctx payloadScopeId)

    /// `Ok` when `payloadScopeId` is the job's own scope, or when the job runs
    /// under the platform container, which only the platform's own paths can
    /// register.
    let requireOwnOrPlatform (ctx: JobContext) (payloadScopeId: string) : Result<unit, string> =
        if sameScope ctx.ScopeId ScopeKey.PlatformContainer then
            Ok()
        else
            requireOwn ctx payloadScopeId

    /// `Ok` when the storage scope a payload carries field by field is the
    /// job's own: its id is the job's scope, and its container is the one
    /// that id renders to (`StorageScope.tryKey`), or the id itself for a
    /// claim scope. A container naming a different principal from its id is
    /// refused even when the id matches.
    let requireOwnStorage (ctx: JobContext) (scope: StorageScope) : Result<unit, string> =
        match requireOwn ctx scope.ScopeId with
        | Error reason -> Error reason
        | Ok() ->
            match StorageScope.tryKey scope with
            | Some _ -> Ok()
            | None when sameScope scope.Container scope.ScopeId -> Ok()
            | None ->
                Error
                    $"the job's payload names container '{scope.Container}', which is not the container of scope '{scope.ScopeId}'"