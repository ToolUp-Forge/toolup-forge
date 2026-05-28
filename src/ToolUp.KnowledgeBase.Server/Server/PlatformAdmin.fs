module KnowledgeBase.ServerPlatformAdmin

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open SharedTypes
open KnowledgeBase.ServerJsonHelpers
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments

// ─── Phase 4b — Platform KB write surface ─────────────────────────────
//
// Server-side implementation of the `IPlatformKnowledgeApi` Fable.Remoting
// record (defined in `Core/Shared/PlatformKnowledgeApi.fs`). Sister
// handler to `KnowledgeBase.Server.knowledgeApi`; shares the underlying
// `uploadDocument` / `deleteDocument` / `getDocuments` machinery via a
// platform-scoped `KnowledgeApiDeps` override.
//
// **Storage layout.** The Platform KB content lives at:
//   _platform/knowledge/{docId}/{filename}   ← raw blobs
//   _platform/knowledge/index.json           ← document list
//   (vector chunks under VectorScope.Platform via the configured store)
//
// Mirrors the team-side layout exactly except the container is
// `_platform` instead of `team-{teamId}` / `user-{userId}`.
//
// **Scope override.** The base `KnowledgeApiDeps.resolve` builds deps
// pinned to the request's resolved `StorageScope`. For platform-admin
// writes the scope is fixed to `_platform`, so we rebuild deps with:
//   Scope          = _platform
//   VectorScope    = Platform
//   PublishInventory = no-op   (Platform KB writes don't update any
//                                team's inventory badge — different
//                                semantics; v1 suppresses the
//                                team-inventory notification rather
//                                than introducing a new platform-
//                                inventory channel)
//   MarkIngestionFailed = rebuilt to target `_platform/knowledge/
//                          index.json` instead of the caller's team
//                          index
//
// `UserId` stays the actual caller's user-id so the
// `IngestionStatusNotificationKey` flow publishes terminal-status
// notifications to the admin's user scope (the admin watches the
// upload progress in their AI side panel just like a team upload).
//
// **Gating.** Each write method checks
// `AccessContext.canModifyPlatformConfig` BEFORE running. Non-admin
// callers receive `Error "platform admin role required"`. Reads
// (`ListPlatformDocuments`) are unconditionally available — the
// `ServerConfig.PlatformKnowledgeBase` toggle (Phase 4b commit 5)
// adds the read-side gate; in commit 4e the substrate is
// pre-populatable by admins.

[<Literal>]
let private platformContainer = "_platform"

let private platformScope: StorageScope = {
    ScopeId = platformContainer
    Container = platformContainer
    Persist = true
}

/// Build a platform-scoped `KnowledgeApiDeps` from the base deps. The
/// rebuild rewires scope-dependent closures so reads / writes target
/// `_platform` even though the request's resolved scope is the caller's
/// team / user scope.
let private platformDeps (baseDeps: KnowledgeApiDeps) : KnowledgeApiDeps =
    let storage = baseDeps.Storage
    let logger = baseDeps.Logger
    let notifications = baseDeps.Notifications
    let userId = baseDeps.UserId

    let markIngestionFailed (docId: string) (fileName: string) (reason: string) = async {
        let status = IngestionStatus.Failed reason

        statusCache.AddOrUpdate(docId, status, fun _ _ -> status) |> ignore

        let! existing = loadIndex storage platformContainer

        let updated =
            existing
            |> List.map (fun d -> if d.Id = docId then { d with Status = status } else d)

        do! saveIndex storage platformContainer updated

        if not (isNull (box notifications)) then
            try
                let payload: IngestionStatusUpdate = {
                    DocumentId = docId
                    FileName = fileName
                    Outcome = "Failed"
                    ChunkCount = 0
                    ErrorReason = reason
                    UploadedBy = userId
                }

                let payloadJson = toJson payload
                let notification = CustomNotification(IngestionStatusNotificationKey, payloadJson)
                do! notifications.Publish(userId, notification)
            with ex ->
                logger.Error(
                    sprintf "[KnowledgeBase] Failed to publish IngestionStatus notification for %s" docId,
                    Some ex
                )
    }

    {
        baseDeps with
            Scope = platformScope
            VectorScope = VectorScope.Platform
            PublishInventory = fun () -> async { return () }
            MarkIngestionFailed = markIngestionFailed
    }

/// Resolve `IAuditLog` from DI for audit emission on Platform KB
/// mutations. Returns `None` when the audit-log substrate isn't
/// registered (test bypass, `NoAuditLog` deployment) — emission is
/// fire-and-forget and silent when the substrate is absent.
let private resolveAuditLog (ctx: HttpContext) : IAuditLog option =
    match ctx.RequestServices.GetService(typeof<IAuditLog>) with
    | :? IAuditLog as a -> Some a
    | _ -> None

/// Emit a Platform KB audit event under `_platform` scope. Fire-and-
/// forget — `IAuditLog.Record` is contractually best-effort and
/// swallows its own failures, so audit gaps never cascade into a
/// failed primary write.
let private recordAudit (auditLog: IAuditLog option) (event: AuditEvent) =
    match auditLog with
    | Some a -> a.Record(platformContainer, event) |> Async.Start
    | None -> ()

/// Construct the Fable.Remoting `IPlatformKnowledgeApi` for the current
/// request. Mirrors `KnowledgeBase.Server.knowledgeApi`'s shape exactly
/// (build deps once, bind methods to handlers) so the wire-level
/// behaviour is consistent between the team-side and platform-admin
/// surfaces.
let platformKnowledgeApi (ctx: HttpContext) : PlatformKnowledgeApi.IPlatformKnowledgeApi =
    let baseDeps = KnowledgeApiDeps.resolve ctx
    let accessContext = baseDeps.AccessContext
    let auditLog = resolveAuditLog ctx

    let isAdmin () =
        AccessContext.canModifyPlatformConfig accessContext

    {
        UploadPlatformDocument =
            fun bytes fileName -> async {
                if not (isAdmin ()) then
                    return Error "platform admin role required"
                else
                    let pdeps = platformDeps baseDeps
                    let! doc = uploadDocument pdeps bytes fileName

                    recordAudit
                        auditLog
                        (PlatformDocumentUploaded {
                            Actor = accessContext.UserId
                            DocumentId = doc.Id
                            FileName = fileName
                            SizeBytes = int64 bytes.Length
                        })

                    return Ok doc
            }

        DeletePlatformDocument =
            fun docId -> async {
                if not (isAdmin ()) then
                    return Error "platform admin role required"
                else
                    let pdeps = platformDeps baseDeps

                    // Resolve the doc's filename before delete so the
                    // audit payload can carry it. Idempotent deletes
                    // (unknown id) suppress the audit emission — the
                    // trail records material state changes only.
                    let! existing = getDocuments pdeps
                    let prior = existing |> List.tryFind (fun d -> d.Id = docId)

                    let! result = deleteDocument pdeps docId

                    match result, prior with
                    | Ok(), Some doc ->
                        recordAudit
                            auditLog
                            (PlatformDocumentDeleted {
                                Actor = accessContext.UserId
                                DocumentId = doc.Id
                                FileName = doc.FileName
                            })
                    | _ -> ()

                    return result
            }

        ListPlatformDocuments =
            fun () -> async {
                let pdeps = platformDeps baseDeps
                return! getDocuments pdeps
            }

        PromoteTeamDocumentToPlatform =
            fun docId -> async {
                if not (isAdmin ()) then
                    return Error "platform admin role required"
                elif baseDeps.Scope.Container = platformContainer then
                    // The caller's resolved scope IS the platform
                    // container — there's no team-side document to
                    // promote. Returns Error rather than silently
                    // doing nothing.
                    return Error "no team scope resolved"
                else
                    // Step 1: locate the source document in the
                    // caller's team scope and load its blob.
                    let! teamDocs = getDocuments baseDeps
                    let source = teamDocs |> List.tryFind (fun d -> d.Id = docId)

                    match source with
                    | None -> return Error(sprintf "document %s not found in team KB" docId)
                    | Some doc ->
                        let rawBlobName = sprintf "knowledge/%s/%s" doc.Id doc.FileName
                        let! blobResult = baseDeps.Storage.Download(baseDeps.Scope.Container, rawBlobName)

                        match blobResult with
                        | Error e -> return Error(sprintf "failed to read source blob: %s" e)
                        | Ok bytes ->
                            // Step 2: upload through the standard
                            // platform-write path. New docId is minted
                            // by `uploadDocument`; vectorisation runs
                            // asynchronously after the call returns.
                            let pdeps = platformDeps baseDeps
                            let! newDoc = uploadDocument pdeps bytes doc.FileName

                            recordAudit
                                auditLog
                                (PlatformDocumentUploaded {
                                    Actor = accessContext.UserId
                                    DocumentId = newDoc.Id
                                    FileName = doc.FileName
                                    SizeBytes = int64 bytes.Length
                                })

                            return Ok newDoc
            }
    }