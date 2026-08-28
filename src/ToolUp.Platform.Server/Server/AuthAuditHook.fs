// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AuthAuditHook

open System
open System.Collections.Concurrent
open ToolUp.Platform

// ─── Phase 120 — default IAuthAuditHook implementation ───────────────
//
// Writes an `AuthorizationDenied` audit event through the registered
// `IAuditLog` for every denial an emission point reports, and coalesces
// probing bursts on the same `(route, subject)` key via a dedup window so
// a scripted enumeration produces bounded audit volume with an accurate
// count rather than one row per probe.
//
// **Coalescing model (per `(route, subject)` key, within `dedupWindow`):**
//   * The FIRST denial of a window emits a leading-edge row immediately
//     (`DedupCount = 1`) — operators see the probe start without delay.
//   * Subsequent denials in the same window are SUPPRESSED and counted.
//   * When a denial arrives after the window has expired, the prior
//     window's suppressed tail (if any) is flushed as ONE summary row
//     (`DedupCount = suppressed`), then a fresh leading-edge row opens the
//     new window. Sum of `DedupCount` across rows = total denials.
//   * `FlushPending` emits the suppressed tail for windows that have gone
//     quiet (called by the `/dev/auth-denials` read path so the rollup
//     reflects accurate counts without waiting for the next probe).
//
// **Single-instance dedup (rule 4).** The window state is in-memory and
// per-process. A second instance dedups independently — acceptable: each
// instance's denial volume is bounded on its own, and the audit rows
// still carry accurate per-instance counts. A distributed `IAuthAuditHook`
// can no-op the coalescing and emit per call.
//
// **Best-effort (never blocks the response).** Every audit write is
// wrapped; a failure is swallowed (matching `IAuditLog.Record`'s contract).

/// Default dedup window. Short relative to the `/dev/auth-denials`
/// 60-minute rollup — it bounds a *burst*, not the rollup horizon.
let DefaultDedupWindow = TimeSpan.FromSeconds 60.0

/// Per-`(route, subject)` coalescing state. `Template` is the most recent
/// denial seen on the key, used to reconstruct a representative summary row
/// when the suppressed tail flushes.
type private FloodEntry = {
    WindowStart: DateTimeOffset
    Suppressed: int
    Template: AuthDenial
}

/// Sanitise a `Subject` to the `(kind, id)` pair the audit row carries —
/// the only subject information that reaches the trail (no PII beyond the
/// id). Mirrors `SurfaceEnforcementMiddleware`'s sanitisation.
///
/// Phase 739 moved the body to `AuditSubject.sanitise` in Core and left
/// this as the delegating alias, so every existing caller reads
/// unchanged. The move was forced by the grant twin: `MediaKeyDelivered`
/// is emitted from a COMPANION assembly, which cannot see an `internal`
/// binding here, and the two halves of one endpoint's trail must name a
/// subject identically or a reviewer cannot union them.
let internal sanitiseSubject (subject: Subject) : string * string option = AuditSubject.sanitise subject

/// Stable dedup key for a denial — `(route, subject-id-or-anon)`. Anonymous
/// subjects share one bucket per route (a flood of anonymous probes on one
/// route coalesces, which is the intent).
let internal dedupKey (denial: AuthDenial) : string * string =
    let _, subjectId = sanitiseSubject denial.Subject
    denial.Route, (subjectId |> Option.defaultValue "anonymous")

/// SDK-default `IAuthAuditHook`. Writes `AuthorizationDenied` rows through
/// `IAuditLog`, coalescing bursts per the module preamble. `dedupWindow`
/// and `now` are injectable for tests; production uses
/// `DefaultDedupWindow` and the wall clock.
type AuthAuditHook(auditLog: IAuditLog, logger: ILogger, ?dedupWindow: TimeSpan, ?now: unit -> DateTimeOffset) =

    let window = defaultArg dedupWindow DefaultDedupWindow
    let clock = defaultArg now (fun () -> DateTimeOffset.UtcNow)
    let entries = ConcurrentDictionary<string * string, FloodEntry>()
    // Serialises the read-modify-write decision on the entry map; the async
    // audit writes happen OUTSIDE this lock.
    let gate = obj ()

    /// Write one `AuthorizationDenied` row representing `dedupCount`
    /// denials, derived from `denial`. Best-effort.
    let emit (denial: AuthDenial) (dedupCount: int) : Async<unit> = async {
        try
            let subjectKind, subjectId = sanitiseSubject denial.Subject
            let scopeId = denial.ScopeId |> Option.defaultValue "_platform"

            do!
                auditLog.Record(
                    scopeId,
                    AuthorizationDenied {
                        Route = denial.Route
                        Requirement = AuthDenialRequirement.toString denial.Requirement
                        SubjectKind = subjectKind
                        SubjectId = subjectId
                        Verdict = denial.Verdict
                        Reason = denial.Reason
                        ScopeId = denial.ScopeId
                        CorrelationId = denial.CorrelationId
                        DedupCount = dedupCount
                        OccurredAt = clock ()
                    }
                )
        with ex ->
            // Best-effort — a denial-audit failure must not affect the
            // response the emission site is about to write.
            logger.Warn(sprintf "[AuthAuditHook] denial-audit write failed route=%s: %s" denial.Route ex.Message)
    }

    interface IAuthAuditHook with
        member _.RecordDenial(denial: AuthDenial) : Async<unit> =
            let key = dedupKey denial
            let nowTs = clock ()

            // Decide what to emit under the lock; perform the async writes
            // after releasing it. `emits` is the list of (denial, count)
            // rows this call produces (0, 1, or 2 entries).
            let emits =
                lock gate (fun () ->
                    match entries.TryGetValue key with
                    | false, _ ->
                        // First denial on this key — open a window, emit leading.
                        entries[key] <- {
                            WindowStart = nowTs
                            Suppressed = 0
                            Template = denial
                        }

                        [ denial, 1 ]
                    | true, entry when nowTs - entry.WindowStart >= window ->
                        // Window rolled over. Flush the prior window's
                        // suppressed tail (if any) as a summary, then open a
                        // fresh window with a leading-edge row.
                        entries[key] <- {
                            WindowStart = nowTs
                            Suppressed = 0
                            Template = denial
                        }

                        if entry.Suppressed > 0 then
                            [ entry.Template, entry.Suppressed; denial, 1 ]
                        else
                            [ denial, 1 ]
                    | true, entry ->
                        // Within the window — suppress + count, emit nothing.
                        entries[key] <- {
                            entry with
                                Suppressed = entry.Suppressed + 1
                                Template = denial
                        }

                        [])

            async {
                for d, count in emits do
                    do! emit d count
            }

    /// Emit the suppressed tail for every key whose window currently holds
    /// suppressed denials, resetting each counter to 0 (the window itself is
    /// left open). Called by the `/dev/auth-denials` read path so the rollup
    /// reflects accurate counts without waiting for the next probe to
    /// trigger a rollover. Idempotent when nothing is pending.
    member _.FlushPending() : Async<unit> =
        let pending =
            lock gate (fun () -> [
                for kvp in entries do
                    let entry = kvp.Value

                    if entry.Suppressed > 0 then
                        entries[kvp.Key] <- { entry with Suppressed = 0 }
                        yield entry.Template, entry.Suppressed
            ])

        async {
            for d, count in pending do
                do! emit d count
        }