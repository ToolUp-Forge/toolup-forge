module ToolUp.Platform.IDataExporter

open ToolUp.Platform

// ─── Phase 9h — DSR extension points ─────────────────────────────────
//
// Two interfaces every persistent subsystem opts into to participate
// in data-subject requests. The orchestrator (`ErasurePipeline`)
// composes contributions without core knowing about every store —
// same shape as `IHealthCheck` / `INotificationChannel.Subscribe`.
//
// Why an extension point rather than extending each store interface
// (the phase-body's "add per-store erasure surface" task): the
// adapter pattern lets new behaviour land additively without forcing
// every existing store impl + every contract test to change in one
// commit. The MVP ships this opt-in shape; the per-store interface-
// extension follow-ups can land per-store as the relevant store
// comes under Erase pressure.
//
// Six-rule portability audit (Phase 9c, GP 12):
//   1. Identity by value      — `scopeId`, `subjectUserId`, the
//                               handler's own `Name` field; no live
//                               handles.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry/supervision as data — failures flow through `ErasureError`
//                                  / `Result`. Caller's RetryPolicy
//                                  (Phase 9b) is what re-runs failed
//                                  segments.
//   4. Stateless between calls — handlers read from their backing
//                                store on every invocation; no
//                                in-memory cumulative state.
//   5. No cross-shard ordering — the orchestrator owns ordering
//                                (descendants before ancestors per
//                                lineage); handlers don't promise
//                                anything about within-store order.
//   6. Precision at lower bound — n/a (no time semantics).

/// One persistent subsystem's contribution to a data-subject export.
/// The orchestrator composes every registered exporter's segment
/// into the streamed response (alphabetical-by-Name for deterministic
/// output bytes).
type IDataExporter =
    /// Stable name. Becomes the file path inside the export archive
    /// (e.g. `entities/team-abc.json`) and the alphabetical sort
    /// key. No spaces / control chars.
    abstract Name: string

    /// Produce one or more segments naming the subject within the
    /// given scope. Returns `[]` when the subsystem has no records
    /// for the subject — the orchestrator skips silently.
    abstract Export: scopeId: string * subjectUserId: string -> Async<ExportSegment list>

/// One persistent subsystem's contribution to a data-subject erasure
/// run. Returns `ErasureSummary` on success or a typed `ErasureError`
/// on failure.
type IErasureHandler =
    /// Stable name. Appears in audit + admin UI.
    abstract Name: string

    /// Erase records naming the subject within the given scope per
    /// the supplied policy. Returns the affected-record count + any
    /// human-readable note.
    abstract Erase:
        scopeId: string * subjectUserId: string * policy: ErasurePolicy -> Async<Result<ErasureSummary, ErasureError>>

    /// Preview the erasure without applying it. Returns the count
    /// the corresponding `Erase` would affect — supports the API's
    /// two-phase commit (preview → admin confirm → execute).
    abstract Preview: scopeId: string * subjectUserId: string * policy: ErasurePolicy -> Async<ErasureSummary>