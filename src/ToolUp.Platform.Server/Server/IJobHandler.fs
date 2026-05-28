namespace ToolUp.Platform

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