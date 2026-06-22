module ToolUp.Platform.DataSubjectRequestApiHandler

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Platform
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.ErasurePipeline
open ToolUp.Platform.DataSubjectRequestApi

// ─── Phase 9h — IDataSubjectRequestApi handler factory ──────────────
//
// Per-scope handler builder. The composition root resolves the
// caller's scope (per the established `IStorageScopeResolver`
// pattern) and builds an `IDataSubjectRequestApi` instance bound to
// that scope; the orchestrator runs against the registered handler
// list.
//
// In-memory preview cache: previews land in an in-process
// ConcurrentDictionary keyed by `DataSubjectRequestId`. Confirm
// looks up the preview to recover the subject + policy. Process
// restart wipes the cache — admins re-preview after restart. A
// distributed cache (Phase 9c half-2) is a follow-up.
//
// Audit emission: the audit callback receives an event-payload
// record on every state transition (RequestStarted /
// PreviewCompleted / ErasureCompleted / ExportCompleted). The
// caller wires the callback to its `IAuditLog`.

type DsrAuditEventKind =
    | RequestStarted
    | PreviewCompleted
    | ErasureCompleted
    | ErasureFailed
    | ExportCompleted

type DsrAuditEvent = {
    RequestId: DataSubjectRequestId
    Kind: DsrAuditEventKind
    SubjectUserId: string
    ScopeId: string
    Actor: string
    Reason: string
    /// Free-form extension payload — for ErasureCompleted this
    /// carries the per-handler counts; for ExportCompleted it
    /// carries the segment count + total bytes.
    Properties: Map<string, string>
}

type AuditOnDsr = DsrAuditEvent -> Async<unit>

let private noOpAudit: AuditOnDsr = fun _ -> async { return () }

let private newRequestId () =
    Guid.NewGuid().ToString("N").Substring(0, 12)

let private now () = DateTimeOffset.UtcNow

/// Canonical export-envelope serialisation — a JSON document:
///   `{ "segments": [{ "name", "mimeType", "bytes": "<base64>" }, ...] }`
///
/// Module-level + public (Phase 9h.A) so the synchronous `RequestExport`
/// path and the background `DSRExportJobHandler` produce byte-identical
/// envelopes (the acceptance contract: a downloaded async ticket matches
/// the synchronous `RequestExport` shape).
let serialiseSegments (segments: ExportSegment list) : byte[] =
    let payload = {|
        segments =
            segments
            |> List.map (fun s -> {|
                name = s.Name
                mimeType = s.MimeType
                bytes = Convert.ToBase64String s.Body
            |})
    |}

    let json = JsonSerializer.Serialize payload
    Encoding.UTF8.GetBytes json

// ─── Phase 9h.A — DSR background-job names + payload (moved up) ─────────
//
// `DsrJobs` + `DsrJobPayload` live here (rather than in
// `DSRExportJobHandler.fs`) so the async `RequestExportAsync` path below
// can serialise the job payload + name the handler. `DSRExportJobHandler`
// (compiled later) consumes them via `open DataSubjectRequestApiHandler`.

/// Reserved `IJobScheduler` handler names for the DSR background jobs.
module DsrJobs =
    [<Literal>]
    let ExportHandler = "_platform.datasubject.export"

    [<Literal>]
    let ErasureHandler = "_platform.datasubject.erasure"

/// The job payload carried in `JobContext.Payload`: the export/erasure
/// ticket plus the originating `DataSubjectRequest`. Serialised as a
/// flat JSON object (DU cases → stable case-name strings) so it survives
/// `IJobStore` round-trips without a converter dependency.
module DsrJobPayload =

    let private kindName =
        function
        | DataSubjectRequestKind.Export -> "Export"
        | DataSubjectRequestKind.Erase -> "Erase"
        | DataSubjectRequestKind.Rectify -> "Rectify"

    let private parseKind =
        function
        | "Export" -> DataSubjectRequestKind.Export
        | "Erase" -> DataSubjectRequestKind.Erase
        | _ -> DataSubjectRequestKind.Rectify

    let private policyName =
        function
        | ErasurePolicy.HardDelete -> "HardDelete"
        | ErasurePolicy.Tombstone -> "Tombstone"
        | ErasurePolicy.RetainPerCompliance -> "RetainPerCompliance"

    let private parsePolicy =
        function
        | "HardDelete" -> ErasurePolicy.HardDelete
        | "RetainPerCompliance" -> ErasurePolicy.RetainPerCompliance
        | _ -> ErasurePolicy.Tombstone

    /// Serialise `(ticket, request)` to the job payload string. `ticket`
    /// is empty for the erasure path (no downloadable artefact).
    let serialise (ticket: string) (request: DataSubjectRequest) : string =
        let o = JsonObject()
        o["ticket"] <- JsonValue.Create ticket
        o["id"] <- JsonValue.Create request.Id
        o["kind"] <- JsonValue.Create(kindName request.Kind)
        o["subjectUserId"] <- JsonValue.Create request.SubjectUserId

        o["teamId"] <-
            (request.TeamId
             |> Option.map JsonValue.Create
             |> Option.map (fun v -> v :> JsonNode)
             |> Option.toObj)

        o["requestedBy"] <- JsonValue.Create request.RequestedBy
        o["requestedAt"] <- JsonValue.Create(request.RequestedAt.ToString("O"))
        o["reason"] <- JsonValue.Create request.Reason
        o["policy"] <- JsonValue.Create(policyName request.Policy)
        o.ToJsonString()

    /// Parse the job payload back to `(ticket, request)`.
    let parse (payload: string) : Result<string * DataSubjectRequest, string> =
        try
            let node = JsonNode.Parse payload
            let ticket = node["ticket"].GetValue<string>()

            let teamId =
                match node["teamId"] with
                | null -> None
                | n -> Some(n.GetValue<string>())

            let request = {
                Id = node["id"].GetValue<string>()
                Kind = parseKind (node["kind"].GetValue<string>())
                SubjectUserId = node["subjectUserId"].GetValue<string>()
                TeamId = teamId
                RequestedBy = node["requestedBy"].GetValue<string>()
                RequestedAt = DateTimeOffset.Parse(node["requestedAt"].GetValue<string>())
                Reason = node["reason"].GetValue<string>()
                Policy = parsePolicy (node["policy"].GetValue<string>())
            }

            Ok(ticket, request)
        with ex ->
            Error ex.Message

/// Map an `AuditOnDsr` event onto an `IAuditLog.Record` call. Shared by
/// the request-time API mount (`BuildRouteHandlers`) and the background
/// job-handler registration (`ComposeJobs`) so both write the same
/// `AuditEvent.DataSubjectRequest` shape under the event's scope.
let auditToLog (auditLog: IAuditLog) : AuditOnDsr =
    fun ev ->
        let payload: DataSubjectRequestAuditPayload = {
            RequestId = ev.RequestId
            Kind =
                match ev.Kind with
                | RequestStarted -> "RequestStarted"
                | PreviewCompleted -> "PreviewCompleted"
                | ErasureCompleted -> "ErasureCompleted"
                | ErasureFailed -> "ErasureFailed"
                | ExportCompleted -> "ExportCompleted"
            SubjectUserId = ev.SubjectUserId
            Actor = ev.Actor
            Reason = ev.Reason
            Properties = ev.Properties
        }

        auditLog.Record(ev.ScopeId, AuditEvent.DataSubjectRequest payload)

/// Phase 9h.A — substrate the async export methods route through. Built
/// per-request by the compose mount when
/// `DataSubjectRequestConfig.Async = true` and both an
/// `IBackgroundExportStore` and `IJobScheduler` are composed. `Notify`
/// publishes a progress signal (no-op when no `INotificationChannel`).
type DsrAsyncDeps = {
    Store: IBackgroundExportStore
    Scheduler: IJobScheduler
    Notify: ExportTicket -> ExportStatus -> Async<unit>
}

/// Build a scoped `IDataSubjectRequestApi`. Caller supplies the
/// registered exporter / erasure-handler lists, the deployment
/// policy default, the scope id resolved upstream, the admin actor
/// id, and the audit callback.
let create
    (exporters: IDataExporter list)
    (handlers: IErasureHandler list)
    (defaultPolicy: ErasurePolicy)
    (scopeId: string)
    (actorUserId: string)
    (accessContext: AccessContext)
    (audit: AuditOnDsr)
    (asyncDeps: DsrAsyncDeps option)
    : IDataSubjectRequestApi =
    // Phase 229 — in-handler Platform-Admin gate, in addition to the
    // `[<RequiresRole "PlatformAdmin">]` classifier attribute on the
    // contract (defence in depth — the gate must not depend solely on the
    // deployment's auth middleware). Same wording as PlatformAdminApiHandler
    // / PlatformAIKeysHandler so the client error banner renders uniformly.
    let denyNonAdmin () =
        not (AccessContext.canModifyPlatformConfig accessContext)
    // Per-handler-instance preview cache. Process-local; admin re-
    // previews after process restart. Acceptable for MVP since
    // erasure is a low-frequency admin operation, not a hot-path
    // request flow.
    let previewCache =
        ConcurrentDictionary<DataSubjectRequestId, DataSubjectRequest * ErasurePreview>()

    let mkRequest
        (kind: DataSubjectRequestKind)
        (subject: string)
        (team: string option)
        (reason: string)
        (policy: ErasurePolicy)
        =
        {
            Id = newRequestId ()
            Kind = kind
            SubjectUserId = subject
            TeamId = team
            RequestedBy = actorUserId
            RequestedAt = now ()
            Reason = reason
            Policy = policy
        }

    let emitAudit (kind: DsrAuditEventKind) (request: DataSubjectRequest) (props: Map<string, string>) =
        audit {
            RequestId = request.Id
            Kind = kind
            SubjectUserId = request.SubjectUserId
            ScopeId = scopeId
            Actor = request.RequestedBy
            Reason = request.Reason
            Properties = props
        }

    {
        RequestExport =
            fun input -> async {
                if denyNonAdmin () then
                    return Result.Error "platform admin role required"
                else

                    let request =
                        mkRequest
                            DataSubjectRequestKind.Export
                            input.SubjectUserId
                            input.TeamId
                            input.Reason
                            defaultPolicy

                    do! emitAudit RequestStarted request Map.empty

                    try
                        let! segments = executeExport exporters request scopeId

                        let totalBytes = segments |> List.sumBy (fun s -> s.Body.Length)

                        do!
                            emitAudit
                                ExportCompleted
                                request
                                (Map [ "segmentCount", string segments.Length; "totalBytes", string totalBytes ])

                        return Result.Ok(serialiseSegments segments)
                    with ex ->
                        return Result.Error ex.Message
            }

        PreviewErasure =
            fun input -> async {
                if denyNonAdmin () then
                    return Result.Error "platform admin role required"
                else

                    let policy = input.OverridePolicy |> Option.defaultValue defaultPolicy

                    let request =
                        mkRequest DataSubjectRequestKind.Erase input.SubjectUserId input.TeamId input.Reason policy

                    do! emitAudit RequestStarted request Map.empty

                    try
                        let! perHandler = preview handlers request scopeId

                        let preview = {
                            Request = request
                            PerHandlerCounts = perHandler
                        }

                        previewCache[request.Id] <- (request, preview)

                        let totalAffected = perHandler.Values |> Seq.sumBy _.RecordsAffected

                        do!
                            emitAudit
                                PreviewCompleted
                                request
                                (Map [
                                    "handlerCount", string perHandler.Count
                                    "totalAffected", string totalAffected
                                ])

                        return Result.Ok preview
                    with ex ->
                        return Result.Error ex.Message
            }

        ConfirmErasure =
            fun requestId -> async {
                if denyNonAdmin () then
                    return Result.Error "platform admin role required"
                else

                    match previewCache.TryGetValue requestId with
                    | false, _ ->
                        return
                            Result.Ok(Refused $"No preview found for request {requestId}. Re-run PreviewErasure first.")
                    | true, (request, _preview) ->
                        try
                            let! outcome = executeErase handlers request scopeId

                            let kind =
                                if outcome.OverallSuccess then
                                    ErasureCompleted
                                else
                                    ErasureFailed

                            let totalAffected =
                                outcome.PerHandler.Values
                                |> Seq.sumBy (fun r ->
                                    match r with
                                    | Result.Ok s -> s.RecordsAffected
                                    | _ -> 0)

                            do!
                                emitAudit
                                    kind
                                    request
                                    (Map [
                                        "totalAffected", string totalAffected
                                        "handlerCount", string outcome.PerHandler.Count
                                    ])

                            // One-shot — drop the preview from the cache so a
                            // second confirm doesn't re-run.
                            previewCache.TryRemove requestId |> ignore
                            return Result.Ok(Completed outcome)
                        with ex ->
                            return Result.Error ex.Message
            }

        // ─── Phase 9h.A — async export surface ─────────────────────────
        RequestExportAsync =
            fun input -> async {
                if denyNonAdmin () then
                    return Result.Error "platform admin role required"
                else

                    match asyncDeps with
                    | None ->
                        return
                            Result.Error
                                "Async DSR export is not enabled — set DataSubjectRequests = Enabled { Async = true } and compose an IJobScheduler."
                    | Some deps ->
                        let request =
                            mkRequest
                                DataSubjectRequestKind.Export
                                input.SubjectUserId
                                input.TeamId
                                input.Reason
                                defaultPolicy

                        do! emitAudit RequestStarted request Map.empty

                        try
                            let! ticket = deps.Store.BeginExport(scopeId, request)
                            do! deps.Notify ticket ExportStatus.Preparing

                            let registration: JobRegistration = {
                                ScopeId = scopeId
                                Handler = DsrJobs.ExportHandler
                                Payload = DsrJobPayload.serialise ticket request
                                Trigger = Manual
                                Idempotency =
                                    Some {
                                        Key = $"dsr-export-{request.Id}"
                                        TtlSeconds = 60 * 60 * 24
                                    }
                                RetryPolicy = JobRetryPolicy.defaults
                                ShardKey = None
                                Precision = Minute
                                CreatedBy = actorUserId
                                Tags = Map [ "source", "dsr-export"; "ticket", ticket ]
                            }

                            match! deps.Scheduler.Schedule registration with
                            | Result.Error err ->
                                do! deps.Store.Fail(ticket, $"schedule failed: {err}")
                                return Result.Error $"Failed to schedule export job: {err}"
                            | Result.Ok jobId ->
                                // Fire immediately (Manual trigger) so the
                                // export does not wait for the next scheduler
                                // tick. TriggerOnce enqueues the attempt now.
                                let! _ = deps.Scheduler.TriggerOnce(scopeId, jobId, actorUserId)
                                return Result.Ok ticket
                        with ex ->
                            return Result.Error ex.Message
            }

        GetExportStatus =
            fun ticket -> async {
                if denyNonAdmin () then
                    return Result.Error "platform admin role required"
                else

                    match asyncDeps with
                    | None -> return Result.Error "Async DSR export is not enabled."
                    | Some deps ->
                        let! status = deps.Store.GetStatus ticket
                        return Result.Ok status
            }

        DownloadExport =
            fun ticket -> async {
                if denyNonAdmin () then
                    return Result.Error "platform admin role required"
                else

                    match asyncDeps with
                    | None -> return Result.Error "Async DSR export is not enabled."
                    | Some deps ->
                        match! deps.Store.Download ticket with
                        | Result.Ok bytes -> return Result.Ok bytes
                        | Result.Error status ->
                            return Result.Error $"Export not downloadable: {ExportStatus.name status}"
            }

        CancelExport =
            fun ticket -> async {
                if denyNonAdmin () then
                    return Result.Error "platform admin role required"
                else

                    match asyncDeps with
                    | None -> return Result.Error "Async DSR export is not enabled."
                    | Some deps ->
                        do! deps.Store.Cancel ticket
                        do! deps.Notify ticket ExportStatus.Cancelled
                        return Result.Ok()
            }
    }

/// No-op audit callback — useful in tests + dev where the consumer
/// hasn't wired audit yet.
let noOpAuditCallback: AuditOnDsr = noOpAudit