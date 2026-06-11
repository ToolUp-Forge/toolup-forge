// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text.Json.Nodes
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.ErasurePipeline
open ToolUp.Platform.DataSubjectRequestApiHandler

// ─── Phase 9h.A — DSR background-job payload + export handler ───────────
//
// The synchronous Phase 9h `RequestExport` assembles the whole envelope
// on the HTTP thread. For large tenants that crosses the HTTP timeout;
// this handler runs the same `executeExport` on an `IJobScheduler` job
// and writes the result into an `IBackgroundExportStore` ticket the
// client polls.

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

/// `IJobHandler` for `_platform.datasubject.export`. Resolves the
/// registered exporters at construction (DI), runs `executeExport` on
/// the job, assembles the canonical envelope, and `Complete`s the
/// ticket. On failure the ticket flips to `Failed` and the job returns
/// `TransientFailure` so `IJobScheduler` retries per the job's
/// `JobRetryPolicy`.
type DSRExportJobHandler
    (store: IBackgroundExportStore, exporters: IDataExporter list, audit: AuditOnDsr, logger: ILogger) =

    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match DsrJobPayload.parse ctx.Payload with
            | Error e ->
                logger.Error($"DSRExportJobHandler: malformed payload — {e}", None)
                // A malformed payload won't fix itself on retry.
                return PermanentFailure $"malformed DSR export payload: {e}"
            | Ok(ticket, request) ->
                try
                    let! segments = executeExport exporters request ctx.ScopeId
                    let envelope = serialiseSegments segments
                    do! store.Complete(ticket, envelope)

                    do!
                        audit {
                            RequestId = request.Id
                            Kind = ExportCompleted
                            SubjectUserId = request.SubjectUserId
                            ScopeId = ctx.ScopeId
                            Actor = request.RequestedBy
                            Reason = request.Reason
                            Properties =
                                Map [
                                    "ticket", ticket
                                    "segmentCount", string segments.Length
                                    "totalBytes", string envelope.Length
                                ]
                        }

                    return Success
                with ex ->
                    do! store.Fail(ticket, ex.Message)
                    logger.Warn $"DSRExportJobHandler: export failed for {request.Id} — {ex.Message}"
                    return TransientFailure ex.Message
        }

module DSRExportJobHandler =
    /// Construct the export job handler. `exporters` are the registered
    /// `IDataExporter`s resolved from DI at compose time.
    let create
        (store: IBackgroundExportStore)
        (exporters: IDataExporter list)
        (audit: AuditOnDsr)
        (logger: ILogger)
        : IJobHandler =
        DSRExportJobHandler(store, exporters, audit, logger) :> IJobHandler