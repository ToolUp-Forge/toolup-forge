module ToolUp.Platform.DataSubjectRequestLifecycle

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.TeamManagement

// ─── Phase 54 — first-party data-erasure lifecycle hook ──────────────
//
// On `OnDeprovisioned`, erases the offboarded tenant's subject data by
// driving every registered Phase 9h `IErasureHandler` (the same
// extension point `IDataSubjectRequestApi` composes) under
// `ErasurePolicy.HardDelete`. Resolves `seq<IErasureHandler>` + the
// optional `ITeamStore` from DI per call (GP 12 rule 4).
//
// **Subject resolution.** `IErasureHandler.Erase` is subject-keyed
// (it erases records *naming* a subject within a scope — Phase 9h's
// per-DSR contract). A tenant offboard must clear *every* member's
// data, so the hook resolves the subject set:
//   * team scope (`team-{id}` / a bare team id `ITeamStore` resolves) —
//     every member's `UserId` via `ITeamStore.GetTeamMembers`.
//   * user scope (`user-{id}`) — that one user id.
//   * anything else — the scope id verbatim as the subject.
// It then erases each (subject × handler) pair and sums the affected
// counts.
//
// **Graceful degradation (soft dep on Phase 9h.A).** This runs the
// erasure *synchronously inline*. When the async-erasure substrate
// (Phase 9h.A, `IJobScheduler`-backed background DSR) is present, a
// long multi-store erasure should route through it so the offboard
// returns promptly and the erasure survives a restart; that routing is
// a follow-on. Absent 9h.A, the synchronous path here IS the graceful
// degrade — bounded by the aggregator's 5-minute `OnDeprovisioned`
// per-hook timeout.
//
// **Skipped vs Failed.** No registered `IErasureHandler` → `Skipped`
// (subject-data erasure not wired). A team scope with no resolvable
// `ITeamStore` → `Skipped` (can't enumerate subjects). Any handler
// returning `Error` → `Failed` with the joined diagnostics (data was
// NOT erased — the operator must see it), even when other handlers
// succeeded.
//
// `OnProvisioned` is a no-op `Skipped`: there is nothing to erase when
// a tenant is stood up.

/// Resolve the subject set whose data the offboard must erase.
let private subjectsFor (services: IServiceProvider) (scopeId: string) : Async<Result<string list, string>> = async {
    if scopeId.StartsWith("team-", StringComparison.Ordinal) then
        match services.GetService(typeof<ITeamStore>) with
        | :? ITeamStore as store ->
            let teamId = scopeId.Substring 5
            let! members = store.GetTeamMembers teamId
            return Ok(members |> List.map _.UserId)
        | _ -> return Error "team scope offboard requires an ITeamStore to enumerate member subjects"
    elif scopeId.StartsWith("user-", StringComparison.Ordinal) then
        return Ok [ scopeId.Substring 5 ]
    else
        return Ok [ scopeId ]
}

type DataSubjectRequestLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "data-erasure"

        member _.OnProvisioned(_scopeId, _actorUserId) = async {
            return
                LifecycleHookResult.Skipped
                    "no provisioning action — there is no subject data to erase when a tenant is stood up"
        }

        member _.OnDeprovisioned(scopeId, _actorUserId) = async {
            // MS DI resolves `seq<T>` to an empty enumerable when nothing
            // is registered; guard against a provider that returns null
            // (a bare custom `IServiceProvider`) so the hook degrades to
            // Skipped rather than throwing.
            let handlers =
                match services.GetService(typeof<seq<IErasureHandler>>) with
                | :? seq<IErasureHandler> as hs -> List.ofSeq hs
                | _ -> []

            if List.isEmpty handlers then
                return LifecycleHookResult.Skipped "no IErasureHandler registered (subject-data erasure not wired)"
            else
                let! subjectsResult = subjectsFor services scopeId

                match subjectsResult with
                | Error reason -> return LifecycleHookResult.Skipped reason
                | Ok subjects ->
                    let errors = ResizeArray<string>()

                    for subject in subjects do
                        for handler in handlers do
                            let! result = handler.Erase(scopeId, subject, ErasurePolicy.HardDelete)

                            match result with
                            | Ok _ -> ()
                            | Error err -> errors.Add(sprintf "%s/%s: %A" handler.Name subject err)

                    if errors.Count > 0 then
                        return LifecycleHookResult.Failed(String.Join("; ", errors))
                    else
                        return LifecycleHookResult.Completed
        }

    // Phase 54c — mutation-free preview: sum the affected-record counts
    // every registered IErasureHandler.Preview (NOT Erase) reports across
    // the offboard's subject set. Reuses the same subject resolution the
    // erasure path uses, so the preview counts exactly what the offboard
    // would erase.
    interface ITenantLifecyclePreview with
        member _.OnDeprovisionPreview(scopeId, _actorUserId) = async {
            let handlers =
                match services.GetService(typeof<seq<IErasureHandler>>) with
                | :? seq<IErasureHandler> as hs -> List.ofSeq hs
                | _ -> []

            if List.isEmpty handlers then
                return
                    LifecyclePreviewItem.affecting "data-erasure" 0 "no IErasureHandler registered — nothing to erase"
            else
                match! subjectsFor services scopeId with
                | Error reason ->
                    return
                        LifecyclePreviewItem.affecting "data-erasure" 0 (sprintf "subject set unavailable: %s" reason)
                | Ok subjects ->
                    let mutable total = 0

                    for subject in subjects do
                        for handler in handlers do
                            let! summary = handler.Preview(scopeId, subject, ErasurePolicy.HardDelete)
                            total <- total + summary.RecordsAffected

                    return
                        LifecyclePreviewItem.affecting
                            "data-erasure"
                            total
                            (sprintf
                                "%d record(s) across %d store(s) would be erased for %d subject(s)"
                                total
                                handlers.Length
                                subjects.Length)
        }

/// Construct the first-party data-erasure lifecycle hook. Resolves the
/// registered erasure handlers (+ optional team store) from `services`
/// on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    DataSubjectRequestLifecycle(services) :> ITenantLifecycle

/// Phase 54j — produce the durable export archive for `scopeId` as the
/// pre-step of an export-then-erase offboard. Resolves every registered
/// `IDataExporter` + `IBlobStorage` from `services`, runs each exporter
/// for the offboard's subject set (the same subjects the erasure hook
/// clears), bundles the segments — alphabetical by `Name` for
/// deterministic bytes — into one content-addressable JSON blob under
/// `_platform`, and returns its reference. Fail-closed: any failure (no
/// exporters, no blob store, subject-resolution failure, a write error)
/// returns `Error`, so `exportThenDeprovision` aborts the offboard before
/// erasing anything.
let exportArchive (services: IServiceProvider) (scopeId: string) : Async<Result<LifecycleExportArchive, string>> = async {
    let exporters =
        match services.GetService(typeof<seq<IDataExporter>>) with
        | :? seq<IDataExporter> as es -> List.ofSeq es
        | _ -> []

    if List.isEmpty exporters then
        return Error "no IDataExporter registered — export-then-erase requires at least one exporter"
    else
        match services.GetService(typeof<IBlobStorage>) with
        | :? IBlobStorage as blob ->
            match! subjectsFor services scopeId with
            | Error reason -> return Error(sprintf "subject set unavailable: %s" reason)
            | Ok subjects ->
                let segments = ResizeArray<ExportSegment>()

                for subject in subjects do
                    for exporter in exporters do
                        let! segs = exporter.Export(scopeId, subject)
                        segments.AddRange segs

                // Alphabetical by Name → deterministic archive bytes
                // (so the same data hashes the same across runs).
                let ordered = segments |> Seq.sortBy _.Name |> List.ofSeq

                let arr = JsonArray()

                for seg in ordered do
                    let o = JsonObject()
                    o["name"] <- JsonValue.Create seg.Name
                    o["mimeType"] <- JsonValue.Create seg.MimeType
                    o["body"] <- JsonValue.Create(Convert.ToBase64String seg.Body)
                    arr.Add o

                let root = JsonObject()
                root["scopeId"] <- JsonValue.Create scopeId
                root["segmentCount"] <- JsonValue.Create ordered.Length
                root["segments"] <- arr

                let bytes = Encoding.UTF8.GetBytes(root.ToJsonString())
                let hash = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
                let path = sprintf "tenant-export/%s/export-%s.json" scopeId hash

                match! blob.Upload("_platform", path, bytes) with
                | Ok _ ->
                    return
                        Ok {
                            Container = "_platform"
                            BlobPath = path
                            ContentHash = hash
                            SegmentCount = ordered.Length
                        }
                | Error err -> return Error(sprintf "failed to write export archive: %s" err)
        | _ -> return Error "no IBlobStorage registered — cannot durably write the export archive"
}