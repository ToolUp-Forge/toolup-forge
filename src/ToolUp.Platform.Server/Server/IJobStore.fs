namespace ToolUp.Platform

// ─── IJobStore ────────────────────────────────────────────────────
//
// Server-side persistence interface for the background-job substrate
// (Phase 9b). Stores `JobDefinition`s and per-attempt `JobRun`s,
// scoped strictly per `ScopeId` for team isolation (GP 4).
//
// **Async at every boundary** (Phase 9c Rule 2). Even the in-process
// blob-backed default reads / writes through `IBlobStorage`'s async
// surface so a distributed-store companion (Akka.Persistence journal,
// Orleans grain state, EventGrid + cosmos) drops in without changing
// any caller. No `unit -> 'T` shortcuts.
//
// **Identity by value** (Phase 9c Rule 1). All lookups take `JobId`
// (`Guid`) and `ScopeId` (`string`) — never an in-memory handle. A
// caller that persists a `JobId` to its own store and revives it days
// later gets the same lookup behaviour from any implementation.
//
// **Cross-scope reads are structurally impossible.** Every method
// takes `scopeId` as a required parameter, mirroring the pattern from
// `IDataObjectStore`, `IResultStore`, `IFeatureFlagStore`. The
// blob-backed default keys on this in the path layout, so a
// mis-scoped lookup fails to find rather than leaking another team's
// jobs.
//
// **Precision is carried on the data, not the store** (Phase 9c Rule
// 6). `JobDefinition.Precision : JobPrecision` records each job's
// declared firing precision; the store persists and returns the
// value verbatim and does not validate or enforce it. Precision
// rejection (e.g. `Second` on an in-process scheduler) lives in
// `IJobScheduler.Schedule`, which inspects the definition before
// calling `Save`. A new companion store therefore has no precision
// concern of its own — round-trip the field, and the scheduler that
// wraps it owns the contract.

type IJobStore =
    /// Persist a fresh `JobDefinition` keyed by `JobId`. Caller is
    /// responsible for generating `JobId` (`Guid.NewGuid()`) and
    /// computing `NextRunAt` from the `Trigger`. The store does not
    /// validate the trigger or compute `NextRunAt` itself — those
    /// concerns live in `IJobScheduler`.
    abstract Save: definition: JobDefinition -> Async<unit>

    /// Read one job by id. Returns `None` when the job does not exist
    /// in `scopeId` — does not fall back to other scopes (cross-scope
    /// reads are structurally impossible).
    abstract Get: scopeId: string * jobId: JobId -> Async<JobDefinition option>

    /// List every job in the scope. Order is implementation-defined
    /// (the blob-backed default returns in `CreatedAt` ascending
    /// order). Does NOT filter by `Status` — `Cancelled` and
    /// `Disabled` jobs appear. Admin UIs filter as needed.
    abstract ListJobs: scopeId: string -> Async<JobDefinition list>

    /// Mutating overload — replaces the existing record under
    /// `(scopeId, jobId)`. The scheduler uses this on every status
    /// transition (`NextRunAt`, `LastRunAt`, `LastRunStatus`,
    /// `ConsecutiveFailures`). Callers should always read-modify-
    /// write through `Get` then `Update` to avoid clobbering a
    /// concurrent scheduler tick — the in-process default takes a
    /// per-job `lock`, distributed implementations rely on
    /// `IBlobStorage.UploadIfMatch` (Phase 9c follow-up).
    abstract Update: definition: JobDefinition -> Async<unit>

    /// Idempotency lookup — returns the existing `JobId` if any job
    /// in `scopeId` carries `Idempotency.Key = key` and was created
    /// within the supplied TTL. Used by `IJobScheduler.Schedule` to
    /// short-circuit duplicate registration attempts. `None` means
    /// "no live match — caller may register a fresh job".
    abstract FindByIdempotencyKey:
        scopeId: string * key: string * ttlSeconds: int * now: System.DateTime -> Async<JobId option>

    /// Append a per-attempt run row. The store keeps run history
    /// alongside the definition (one blob per run under
    /// `{scopeId}/{jobId}/runs/{runId}.json`). Callers retain the
    /// `JobRun` value; the store does not return it.
    abstract RecordRun: run: JobRun -> Async<unit>

    /// Read the most recent `count` run rows for a job. Newest first.
    /// Used by the admin UI's per-job history panel and by the
    /// scheduler's restart-recovery code (figure out where we left
    /// off). Returns the empty list when no runs exist or when the
    /// job is unknown.
    abstract GetRecentRuns: scopeId: string * jobId: JobId * count: int -> Async<JobRun list>

    /// Fetch every job in `scopeId` whose `Status = Active` AND
    /// whose `NextRunAt <= now`. The in-process scheduler calls this
    /// once per tick to find due cron jobs. Returns the empty list
    /// when nothing is due.
    ///
    /// **Implementations targeting non-trivial scale MUST index on
    /// `(Status, NextRunAt)` so this query completes in O(due-count)
    /// rather than O(jobs-in-scope).** The blob-backed default scans
    /// every job in the scope, which is acceptable only at expected
    /// counts (≤ a few thousand per team) — a distributed companion
    /// (Akka.Persistence query journal, Orleans grain index, cloud-
    /// managed scheduler) without an index will hit visible tick
    /// latency past that threshold. The interface declares the
    /// contract; the implementation chooses the storage shape that
    /// satisfies it.
    abstract DueJobs: scopeId: string * now: System.DateTime -> Async<JobDefinition list>

    /// Phase 319 — every run in `scopeId` currently sitting in
    /// `JobRunStatus.AwaitingExternal`, newest first, capped at `limit`.
    /// The scheduler's reconciliation pass calls this once per scope per
    /// tick, polls each returned run's `ExternalHandle`, and drives it to
    /// a terminal state. Returns the empty list when nothing is awaiting
    /// — which is the case for every deployment that composes no
    /// external-compute backend, so the query must be *cheap when empty*
    /// (GP 13), not merely correct.
    ///
    /// **`limit` bounds the batch, and the cap is a fairness device, not
    /// a correctness one.** A scope with more awaiting runs than `limit`
    /// gets the rest on the next tick; no run is dropped, because the
    /// state lives in the store and the query is re-issued every tick.
    /// The alternative — an unbounded return — lets one saturated scope
    /// monopolise a tick and starve every other scope's reconciliation,
    /// which is the failure the scheduler's per-scope loop already
    /// avoids for due jobs.
    ///
    /// **Implementations MUST NOT satisfy this by scanning run
    /// history.** Run rows are per-attempt and unbounded in the general
    /// case, so a scan is O(all-runs-ever) to find an O(few) answer —
    /// on the tick path, every tick, forever. Index on the status
    /// transition instead: the blob-backed default maintains an
    /// `_awaiting-external` secondary index that `RecordRun` adds to
    /// and removes from as a run enters and leaves the state, so the
    /// query is a prefix list of exactly the awaiting set. This is the
    /// same contract `DueJobs` states for `(Status, NextRunAt)`, and
    /// for the same reason.
    ///
    /// A run whose `Status` is `AwaitingExternal` but whose
    /// `ExternalHandle` is `None` is malformed and MUST still be
    /// returned — the scheduler needs to see it to fail it, and
    /// silently filtering it here would leave the run awaiting forever
    /// with nothing anywhere reporting why.
    abstract AwaitingExternalRuns: scopeId: string * limit: int -> Async<JobRun list>

    /// Enumerate every scope that currently holds at least one job.
    /// The in-process scheduler calls this on each tick to know
    /// which `DueJobs(scope, now)` queries to run — without it the
    /// scheduler would have to track scopes itself, which conflates
    /// scheduler state with persistence. Returns the empty list when
    /// no jobs exist anywhere. Order is implementation-defined.
    abstract ListScopesWithJobs: unit -> Async<string list>