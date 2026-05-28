module ToolUp.Platform.ErasurePipeline

open System
open ToolUp.Platform
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.DataSubjectRequestApi

// ─── Phase 9h — DSR orchestrator ─────────────────────────────────────
//
// Composes registered IDataExporter / IErasureHandler instances into
// the per-request execution. Two phases per Erase request:
//   1. Preview — every handler reports the count it would affect;
//      no mutation. Returned to the admin for confirmation.
//   2. Execute — every handler runs its Erase; the orchestrator
//      collects per-handler ErasureSummary or ErasureError.
//
// Execution ordering: alphabetical by handler name in the MVP. The
// phase body's "descendants before ancestors per Phase 8a lineage,
// audit last" sequencing is a follow-up — the lineage-aware ordering
// requires a registered ILineageStore-backed dependency walker which
// lands in the lineage-aware sub-PR. For the MVP, deployments that
// need strict ordering register their handlers with names that sort
// in the desired order (e.g. `01-events`, `02-data`, `99-audit`).
//
// Failures: a handler returning `HandlerRefused` records the
// refusal in the run summary and the orchestrator continues with
// the next handler. A handler returning `StoreUnreachable` is
// retried by the IJobScheduler-backed background-run pattern (when
// composed via the standard ServerApp flow); for the in-process
// pipeline (test harness) the run captures the error and returns
// PartialFailure overall.

let private now () = DateTimeOffset.UtcNow

/// Run the preview phase for an Erase request. Returns one summary
/// per registered handler reporting what would be affected if the
/// run executed.
let preview
    (handlers: IErasureHandler list)
    (request: DataSubjectRequest)
    (scopeId: string)
    : Async<Map<string, ErasureSummary>> =
    async {
        let mutable result = Map.empty

        for handler in handlers |> List.sortBy _.Name do
            let! summary = handler.Preview(scopeId, request.SubjectUserId, request.Policy)
            result <- result.Add(handler.Name, summary)

        return result
    }

/// Execute the Erase phase. Iterates every handler in alphabetical
/// order; collects per-handler outcomes. Caller wraps this in
/// IJobScheduler for retry semantics + restart-survival.
let executeErase
    (handlers: IErasureHandler list)
    (request: DataSubjectRequest)
    (scopeId: string)
    : Async<ErasureRunSummary> =
    async {
        let started = now ()
        let mutable perHandler = Map.empty
        let mutable allSucceeded = true

        for handler in handlers |> List.sortBy _.Name do
            let! outcome = handler.Erase(scopeId, request.SubjectUserId, request.Policy)

            match outcome with
            | Result.Error _ -> allSucceeded <- false
            | _ -> ()

            perHandler <- perHandler.Add(handler.Name, outcome)

        return {
            Request = request
            StartedAt = started
            CompletedAt = now ()
            PerHandler = perHandler
            OverallSuccess = allSucceeded
        }
    }

/// Run an Export request. Concatenates every registered exporter's
/// segments alphabetically by Name (deterministic byte output).
/// Returns the segment list for the API handler to bundle into a
/// streamed archive.
let executeExport
    (exporters: IDataExporter list)
    (request: DataSubjectRequest)
    (scopeId: string)
    : Async<ExportSegment list> =
    async {
        let collected = System.Collections.Generic.List<ExportSegment>()

        for exporter in exporters |> List.sortBy _.Name do
            let! segments = exporter.Export(scopeId, request.SubjectUserId)

            for segment in segments do
                collected.Add segment

        return List.ofSeq collected
    }