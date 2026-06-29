// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.LifecycleJobHandler

open System
open System.Text
open System.Text.Json.Nodes
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PlatformTenantApiHandler

// ─── Phase 54a — background offboard job handler ─────────────────────
//
// `IJobHandler` registered under `TenantLifecycleAggregator.
// LifecycleJobHandlerName`. Runs the offboard sweep on an `IJobScheduler`
// job so a 25-minute multi-store erasure survives a process restart
// (`IJobStore` re-dispatches the persisted job) and never blocks an HTTP
// thread — `DeprovisionTenantAsync` returns a `LifecycleJobHandle`
// promptly while this executes in the background.
//
// **Restart survival.** The sweep runs through
// `TenantLifecycleAggregator.runResumable`, which records each
// terminal-success hook (`Completed` / `Skipped`) to a blob-backed
// progress record as it lands. A process killed mid-sweep re-dispatches
// the same job; this handler re-reads the progress record and resumes
// from the last completed hook without re-running it, reaching the
// `TenantDeprovisioned` marker. The progress record is cleared on a
// clean finish. (Phase 54b generalises this into `ILifecycleLedger`
// with per-hook retry; 54a's blob record is the minimal precursor.)
//
// **Progress streaming.** Each intermediate summary is pushed to the
// process-local `TenantLifecycleSnapshot`, so `GetLifecycleSummary`
// reflects running → N-of-M progress while the job is in flight.
//
// **Stateless between invocations (GP 12 rule 4).** The handler resolves
// its substrate (`seq<ITenantLifecycle>`, `IAuditLog`,
// `TenantLifecycleSnapshot`, `IBlobStorage`) from the injected
// `IServiceProvider` on every `Execute`; no state survives across
// dispatches except the durable progress record.

/// Reserved blob container + name for the per-scope offboard progress
/// record. Stored under the platform-reserved `_platform` container (not
/// the offboarded tenant's own container, so it is never swept by the
/// tenant's data-erasure hook) and cleared on a clean finish.
[<Literal>]
let private ProgressContainer = "_platform"

let private progressBlob (scopeId: string) (phase: TenantLifecyclePhase) : string =
    sprintf "tenant-lifecycle/%s/%s-progress.json" scopeId (TenantLifecyclePhase.name phase)

/// Read the set of hook names already completed on a prior attempt.
/// Absent / unreadable record → empty set (fresh sweep). Never throws —
/// a corrupt record degrades to a full re-run, which is safe because the
/// first-party hooks are idempotent.
let private readCompleted (blob: IBlobStorage) (scopeId: string) (phase: TenantLifecyclePhase) : Async<Set<string>> = async {
    match! blob.Download(ProgressContainer, progressBlob scopeId phase) with
    | Error _ -> return Set.empty
    | Ok bytes ->
        try
            let arr = JsonNode.Parse(Encoding.UTF8.GetString bytes).AsArray()
            return arr |> Seq.map (fun n -> n.GetValue<string>()) |> Set.ofSeq
        with _ ->
            return Set.empty
}

/// Record one hook as completed by re-reading the current record, adding
/// the name, and writing it back. Sequential within a sweep
/// (`runResumable` records one hook at a time), so the read-modify-write
/// does not race itself.
let private recordCompleted
    (blob: IBlobStorage)
    (scopeId: string)
    (phase: TenantLifecyclePhase)
    (hookName: string)
    : Async<unit> =
    async {
        let! current = readCompleted blob scopeId phase
        let updated = Set.add hookName current
        let arr = JsonArray()

        for name in updated do
            arr.Add(JsonValue.Create name)

        let bytes = Encoding.UTF8.GetBytes(arr.ToJsonString())
        let! _ = blob.Upload(ProgressContainer, progressBlob scopeId phase, bytes)
        return ()
    }

/// Clear the progress record after a clean finish so a later re-offboard
/// of the same scope starts fresh. Idempotent (`IBlobStorage.Delete`
/// returns `Ok` for a missing blob).
let private clearProgress (blob: IBlobStorage) (scopeId: string) (phase: TenantLifecyclePhase) : Async<unit> = async {
    let! _ = blob.Delete(ProgressContainer, progressBlob scopeId phase)
    return ()
}

type LifecycleJobHandler(services: IServiceProvider) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match TenantLifecycleAggregator.LifecycleJobPayload.parse ctx.Payload with
            | Error e ->
                // A malformed payload will not recover on retry.
                return PermanentFailure(sprintf "malformed tenant-lifecycle payload: %s" e)
            | Ok(phase, scopeId, actorUserId) ->
                let hooks =
                    match services.GetService(typeof<seq<ITenantLifecycle>>) with
                    | :? seq<ITenantLifecycle> as hs -> List.ofSeq hs
                    | _ -> []

                let auditLog =
                    match services.GetService(typeof<IAuditLog>) with
                    | :? IAuditLog as a -> Some a
                    | _ -> None

                let snapshot =
                    match services.GetService(typeof<TenantLifecycleSnapshot>) with
                    | :? TenantLifecycleSnapshot as s -> s
                    | _ -> TenantLifecycleSnapshot()

                // Phase 54e — durable backing so a `GetLifecycleSummary`
                // on a fresh replica / post-restart cold cache still sees
                // the last run. `None` in minimal/test wiring.
                let summaryStore =
                    match services.GetService(typeof<ILifecycleSummaryStore>) with
                    | :? ILifecycleSummaryStore as s -> Some s
                    | _ -> None

                // Best-effort by contract — a sink failure cannot fail
                // an offboard (mirrors the inline handler's seam).
                let emitAudit (scope: string) (event: AuditEvent) =
                    match auditLog with
                    | Some a -> a.Record(scope, event)
                    | None -> async { return () }

                // Persist the terminal summary durably (best-effort) +
                // refresh the process-local cache — mirrors
                // `PlatformTenantApiHandler.persist`. A durable-store
                // outage degrades to a stale admin read, never a failed
                // offboard (the hooks ran, the audit trail recorded it).
                let persistFinal (summary: LifecycleSummary) = async {
                    match summaryStore with
                    | Some store ->
                        try
                            do! store.SetLast(scopeId, summary)
                        with _ ->
                            ()
                    | None -> ()

                    snapshot.Set(scopeId, summary)
                }

                match services.GetService(typeof<IBlobStorage>) with
                | :? IBlobStorage as blob ->
                    try
                        let! summary =
                            TenantLifecycleAggregator.runResumable
                                emitAudit
                                (fun () -> readCompleted blob scopeId phase)
                                (fun hookName -> recordCompleted blob scopeId phase hookName)
                                (fun partial -> async { snapshot.Set(scopeId, partial) })
                                (TenantLifecycleAggregator.defaultTimeout phase)
                                hooks
                                phase
                                scopeId
                                actorUserId

                        do! persistFinal summary
                        do! clearProgress blob scopeId phase
                        return Success
                    with ex ->
                        // Transient — the job re-dispatches; the
                        // persisted progress lets the retry resume from
                        // the last completed hook.
                        return TransientFailure ex.Message
                | _ ->
                    // No durable blob store — fall back to a
                    // non-resumable inline sweep (still off the HTTP
                    // thread, still survives at the job level via
                    // IJobStore re-dispatch, but re-runs every hook on
                    // restart). The first-party hooks are idempotent,
                    // so a full re-run is safe.
                    let! summary = TenantLifecycleAggregator.runWithDefaults emitAudit hooks phase scopeId actorUserId

                    do! persistFinal summary
                    return Success
        }

/// Construct the background offboard job handler. Resolves its substrate
/// from `services` on every `Execute` (stateless between invocations).
let create (services: IServiceProvider) : IJobHandler =
    LifecycleJobHandler(services) :> IJobHandler