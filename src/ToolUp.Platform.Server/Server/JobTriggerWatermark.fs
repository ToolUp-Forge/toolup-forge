module ToolUp.Platform.JobTriggerWatermark

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── JobTriggerWatermark (Phase 598) ─────────────────────────────
//
// Persisted per-scope cursor over `IEventStore` marking the newest
// event whose `OnEvent` job triggers have been dispatched. Closes the
// lost-trigger half of the event→job seam: the live
// `JobNotifyEventStore` hook is in-memory, so an event durably
// written immediately before a process crash never fires its
// triggers even though it sits in the store. With a persisted cursor
// the scheduler can re-read the store on startup (and on a periodic
// sweep) and dispatch exactly the events the notify hook never
// processed — at-least-once, replaying the *actual* missed events
// rather than blind-re-firing every `OnEvent` job (which is what the
// Phase 9b.A drift back-fill does).
//
// Mirrors `AuditReplicatorCursor` (Phase 9g): `(OccurredAt, Id)` is
// the deterministic per-scope total order — timestamp primary,
// event-id `Guid` tie-breaker for identical timestamps. Cursors are
// tiny JSON blobs at `_platform/job-triggers/{scopeId}.cursor`,
// serialised via `FableConverters` like every other `_platform`
// artefact.
//
// **Advance point.** `JobNotifyEventStore` advances the in-memory
// cursor after the scheduler notify returns — it holds the full
// `ModuleEvent`, so the cursor records the event's own `OccurredAt`,
// not a skewed wall-clock read. Advancing is monotonic: concurrent
// writes whose notifies complete out of order keep the maximum.
//
// **Flush cadence.** In-memory advances are flushed to blob once per
// scheduler tick (and on scheduler shutdown), not per event — zero
// hot-path blob writes. The persisted cursor is therefore up to one
// tick stale, which is exactly the at-least-once replay window the
// startup scan re-dispatches.

/// Per-scope trigger cursor. `(LastDispatchedAt, LastDispatchedEventId)`
/// is the same deterministic total order `AuditReplicatorCursor` uses —
/// `OccurredAt` primary, `Id` tie-breaker.
type JobTriggerCursor = {
    /// `OccurredAt` of the newest event whose triggers were dispatched.
    LastDispatchedAt: DateTime
    /// `Id` of that event — tie-breaker for identical timestamps.
    LastDispatchedEventId: Guid
}

module JobTriggerCursor =
    /// Cursor at a wall-clock instant with no specific event — used to
    /// seed a scope on first enable so history written before the
    /// feature existed is NOT replayed (deliberately different from
    /// `AuditReplicatorCursor.empty`, whose `MinValue` replay-all is
    /// correct for archive sinks but would storm `OnEvent` handlers
    /// with triggers that already fired live, pre-feature).
    let at (instant: DateTime) : JobTriggerCursor = {
        LastDispatchedAt = instant
        LastDispatchedEventId = Guid.Empty
    }

    /// Is `evt` strictly after the cursor? `OccurredAt` primary,
    /// `Id` tie-break — mirrors `AuditReplicatorCursor.isAfter`.
    let isAfter (cursor: JobTriggerCursor) (evt: ModuleEvent) : bool =
        if evt.OccurredAt > cursor.LastDispatchedAt then true
        elif evt.OccurredAt < cursor.LastDispatchedAt then false
        else evt.Id.CompareTo(cursor.LastDispatchedEventId) > 0

    /// Fold an event into the cursor, keeping the maximum — advances
    /// stay monotonic under out-of-order notify completion.
    let advance (cursor: JobTriggerCursor) (evt: ModuleEvent) : JobTriggerCursor =
        if isAfter cursor evt then
            {
                LastDispatchedAt = evt.OccurredAt
                LastDispatchedEventId = evt.Id
            }
        else
            cursor

/// Outcome of reading a scope's persisted cursor. `Missing` (no blob)
/// is first-enable — the caller seeds to "now" and skips the scan.
/// `Unreadable` is distinct so a transient storage failure never
/// masquerades as first-enable and silently skips recovery.
type JobTriggerCursorLoad =
    | Loaded of JobTriggerCursor
    | Missing
    | Unreadable of error: string

[<Literal>]
let private CursorContainer = "_platform"

let private cursorBlobName (scopeId: string) = $"job-triggers/{scopeId}.cursor"

let private jsonOptions = FableConverters.create ()

/// In-memory per-scope cursor map with blob-backed persistence.
/// One instance per deployment, shared by `JobNotifyEventStore`
/// (advance on live notify) and `InProcessJobScheduler` (startup
/// scan, periodic sweep, per-tick flush). All members are safe for
/// concurrent use.
type JobTriggerWatermark(blobStorage: IBlobStorage, logger: ILogger) =
    let cursors = ConcurrentDictionary<string, JobTriggerCursor>()
    let dirty = ConcurrentDictionary<string, byte>()

    /// Advance the scope's in-memory cursor past `evt` (monotonic —
    /// keeps the max under out-of-order completion) and mark it dirty
    /// for the next flush. Called by `JobNotifyEventStore` after the
    /// scheduler notify returns, and by the catch-up scan after each
    /// replayed dispatch.
    member _.Advance(evt: ModuleEvent) : unit =
        cursors.AddOrUpdate(
            evt.ScopeId,
            (fun _ -> JobTriggerCursor.advance (JobTriggerCursor.at DateTime.MinValue) evt),
            (fun _ existing -> JobTriggerCursor.advance existing evt)
        )
        |> ignore

        dirty[evt.ScopeId] <- 0uy

    /// The scope's current in-memory cursor, if any.
    member _.TryGet(scopeId: string) : JobTriggerCursor option =
        match cursors.TryGetValue scopeId with
        | true, c -> Some c
        | false, _ -> None

    /// Seed the scope's in-memory cursor if absent (startup path).
    /// `markDirty = true` persists the seed at the next flush — used
    /// on first enable so the next restart sees a cursor rather than
    /// re-detecting first-enable forever.
    member _.Seed(scopeId: string, cursor: JobTriggerCursor, markDirty: bool) : unit =
        if cursors.TryAdd(scopeId, cursor) && markDirty then
            dirty[scopeId] <- 0uy

    /// Read the scope's persisted cursor. `Missing` when no blob
    /// exists (first enable); `Unreadable` on storage failure or a
    /// corrupt payload — the two are deliberately distinct (see
    /// `JobTriggerCursorLoad`).
    member _.LoadPersisted(scopeId: string) : Async<JobTriggerCursorLoad> = async {
        let blobName = cursorBlobName scopeId

        try
            let! exists = blobStorage.Exists(CursorContainer, blobName)

            if not exists then
                return Missing
            else
                let! result = blobStorage.Download(CursorContainer, blobName)

                match result with
                | Ok bytes ->
                    let json = Encoding.UTF8.GetString bytes
                    return Loaded(JsonSerializer.Deserialize<JobTriggerCursor>(json, jsonOptions))
                | Error err -> return Unreadable err
        with ex ->
            return Unreadable ex.Message
    }

    /// Persist every dirty cursor. Failures log and re-mark dirty so
    /// the next flush retries — a failed cursor write costs replay
    /// breadth on the next restart, never lost triggers.
    member _.FlushDirty() : Async<unit> = async {
        for KeyValue(scopeId, _) in dirty do
            match dirty.TryRemove scopeId with
            | false, _ -> ()
            | true, _ ->
                match cursors.TryGetValue scopeId with
                | false, _ -> ()
                | true, cursor ->
                    let json = JsonSerializer.Serialize(cursor, jsonOptions)
                    let bytes = Encoding.UTF8.GetBytes json

                    try
                        let! result = blobStorage.Upload(CursorContainer, cursorBlobName scopeId, bytes)

                        match result with
                        | Ok _ -> ()
                        | Error err ->
                            dirty[scopeId] <- 0uy

                            logger.Warn
                                $"[JobTriggerWatermark] event=cursor_flush_failed scope=%s{scopeId}: {err} — next flush retries"
                    with ex ->
                        dirty[scopeId] <- 0uy

                        logger.Warn
                            $"[JobTriggerWatermark] event=cursor_flush_failed scope=%s{scopeId}: {ex.Message} — next flush retries"
    }