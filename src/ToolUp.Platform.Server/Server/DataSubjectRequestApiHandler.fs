module ToolUp.Platform.DataSubjectRequestApiHandler

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
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
    (audit: AuditOnDsr)
    : IDataSubjectRequestApi =
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

    let serialiseSegments (segments: ExportSegment list) =
        // JSON envelope shape:
        //   { "segments": [{ "name": "...", "mimeType": "...",
        //                    "bytes": "<base64>" }, ...] }
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

    {
        RequestExport =
            fun input -> async {
                let request =
                    mkRequest DataSubjectRequestKind.Export input.SubjectUserId input.TeamId input.Reason defaultPolicy

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
                match previewCache.TryGetValue requestId with
                | false, _ ->
                    return Result.Ok(Refused $"No preview found for request {requestId}. Re-run PreviewErasure first.")
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
    }

/// No-op audit callback — useful in tests + dev where the consumer
/// hasn't wired audit yet.
let noOpAuditCallback: AuditOnDsr = noOpAudit