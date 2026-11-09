// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Audit

open ToolUp.Platform
open ToolUp.Platform.Jobs

open System
open System.Text
open System.Text.Json.Nodes
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

// ─── Phase 9h.A — BlobBackedBackgroundExportStore ───────────────────────
//
// Default `IBackgroundExportStore` over `IBlobStorage`. Per ticket: a
// `.status` sidecar (JSON: status + sizes + timestamps) and, once the
// export job completes, a `.envelope` blob (the serialised segments).
// The ticket encodes its `scopeId`, so every method resolves the blob
// paths from the ticket alone — the store holds no in-memory state and
// any instance over the same `IBlobStorage` serves any ticket
// (restart-survival + horizontal scale, GP 12 rule 4).
//
// Blob layout (container `_platform`):
//   data-subject-requests/{scopeId}/{guid}.status
//   data-subject-requests/{scopeId}/{guid}.envelope
//
// TTL is recorded as `expiresAt` in the status; `GetStatus` reports
// `Expired` past it (and `Download` refuses). Physical GC of expired
// blobs is a deployment sweep (a `PurgeExpired` follow-up); the status
// contract makes a stale envelope un-downloadable regardless.
//
// Both blobs read and write through `BlobDocumentStore`: the status
// sidecar with a thin codec (its bytes predate `PlatformJson`), the
// envelope as opaque bytes. An ABSENT status is an unknown ticket and an
// absent envelope an expired one; a status or envelope that cannot be
// READ refuses (`DocumentUnavailableException`) rather than reporting the
// ticket unknown or the export expired, and a status transition is one
// guarded update.

/// The status sidecar's fields, as stored.
type private ExportStatusRecord = {
    Status: ExportStatus
    SubjectUserId: string
    RequestId: string
    CreatedAt: DateTimeOffset
    ExpiresAt: DateTimeOffset
}

/// The sidecar's JSON, written field by field exactly as it always has been.
module private ExportStatusRecord =

    let codec: BlobCodec<ExportStatusRecord> = {
        Encode =
            fun record ->
                let o = JsonObject()
                o["status"] <- JsonValue.Create(ExportStatus.name record.Status)

                match record.Status with
                | ExportStatus.Ready bytes -> o["sizeBytes"] <- JsonValue.Create bytes
                | ExportStatus.Failed reason -> o["reason"] <- JsonValue.Create reason
                | _ -> ()

                o["subjectUserId"] <- JsonValue.Create record.SubjectUserId
                o["requestId"] <- JsonValue.Create record.RequestId
                o["createdAt"] <- JsonValue.Create(record.CreatedAt.ToString("O"))
                o["expiresAt"] <- JsonValue.Create(record.ExpiresAt.ToString("O"))
                Encoding.UTF8.GetBytes(o.ToJsonString())
        Decode =
            fun bytes ->
                try
                    let node = JsonNode.Parse(Encoding.UTF8.GetString bytes)

                    Ok {
                        Status =
                            match node["status"].GetValue<string>() with
                            | "Preparing" -> ExportStatus.Preparing
                            | "Ready" -> ExportStatus.Ready(node["sizeBytes"].GetValue<int64>())
                            | "Failed" -> ExportStatus.Failed(node["reason"].GetValue<string>())
                            | "Cancelled" -> ExportStatus.Cancelled
                            | "Expired" -> ExportStatus.Expired
                            | _ -> ExportStatus.Unknown
                        SubjectUserId = node["subjectUserId"].GetValue<string>()
                        RequestId = node["requestId"].GetValue<string>()
                        CreatedAt = DateTimeOffset.Parse(node["createdAt"].GetValue<string>())
                        ExpiresAt = DateTimeOffset.Parse(node["expiresAt"].GetValue<string>())
                    }
                with ex ->
                    Error ex.Message
    }

    /// Opaque envelope bytes, carried unchanged.
    let envelopeCodec: BlobCodec<byte[]> = { Encode = id; Decode = Ok }

/// Default blob-backed `IBackgroundExportStore`. `ttl` bounds how long a
/// completed envelope stays downloadable (default 7 days).
type BlobBackedBackgroundExportStore(blobs: IBlobStorage, ttl: TimeSpan) =

    [<Literal>]
    static let TicketPrefix = "dsr."

    static let b64urlEncode (s: string) =
        Convert.ToBase64String(Encoding.UTF8.GetBytes s).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    static let b64urlDecode (s: string) =
        let padded =
            match s.Length % 4 with
            | 2 -> s + "=="
            | 3 -> s + "="
            | _ -> s

        Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')))

    /// `dsr.{base64url(scopeId)}.{guid}`.
    let mintTicket (scopeId: string) : ExportTicket =
        $"{TicketPrefix}{b64urlEncode scopeId}.{Guid.NewGuid():N}"

    /// Parse a ticket back to (scopeId, guid). `None` for a malformed
    /// ticket — surfaces as `ExportStatus.Unknown`.
    let parseTicket (ticket: ExportTicket) : (string * string) option =
        if not (ticket.StartsWith(TicketPrefix, StringComparison.Ordinal)) then
            None
        else
            match ticket.Substring(TicketPrefix.Length).Split('.') with
            | [| encScope; guid |] ->
                try
                    Some(b64urlDecode encScope, guid)
                with _ ->
                    None
            | _ -> None

    let logger = JsonConsoleLogger.JsonConsoleLogger() :> ILogger

    let statuses =
        BlobDocumentStore<ExportStatusRecord>(blobs, ExportStatusRecord.codec, logger)

    let envelopes =
        BlobDocumentStore<byte[]>(blobs, ExportStatusRecord.envelopeCodec, logger)

    let statusPath (scopeId: string) (guid: string) =
        DocPath.ofString $"data-subject-requests/{scopeId}/{guid}.status"

    let envelopePath (scopeId: string) (guid: string) =
        DocPath.ofString $"data-subject-requests/{scopeId}/{guid}.envelope"

    /// Phase 863 — every write here IS the export's record (its status or
    /// its envelope), and the interface carries no failure channel, so a
    /// refused write raises. The export job handler catches it and records
    /// the ticket `Failed`, rather than announcing `Ready` over an envelope
    /// that was never stored.
    let orRaise (path: DocPath) (written: Result<unit, BlobMapStoreError>) =
        match written with
        | Ok() -> ()
        | Error e ->
            failwithf
                "BlobBackedBackgroundExportStore: write of %s failed: %s"
                (DocPath.value path)
                (BlobMapStoreError.describe e)

    /// The persisted status record (`None` = no such ticket); one that
    /// cannot be read or decoded refuses.
    let readStatus (scopeId: string) (guid: string) : Async<ExportStatusRecord option> = async {
        let! read = statuses.Read(ScopeKey.platform, statusPath scopeId guid)
        return DocumentUnavailable.orRaise read
    }

    /// The status a caller sees: a lapsed TTL reads as `Expired`.
    let effective (record: ExportStatusRecord) : ExportStatus =
        if DateTimeOffset.UtcNow > record.ExpiresAt then
            ExportStatus.Expired
        else
            record.Status

    /// Re-persist a status transition, preserving the original subject /
    /// requestId / createdAt so the TTL window doesn't reset. One guarded
    /// update: a ticket with no status has nothing to transition.
    let transition (scopeId: string) (guid: string) (status: ExportStatus) : Async<unit> = async {
        let path = statusPath scopeId guid

        let! written =
            statuses.Update(
                ScopeKey.platform,
                path,
                function
                | None -> BlobUpdate.Keep()
                | Some record ->
                    BlobUpdate.Write(
                        {
                            record with
                                Status = status
                                ExpiresAt = record.CreatedAt + ttl
                        },
                        ()
                    )
            )

        orRaise path written
    }

    interface IBackgroundExportStore with
        member _.BeginExport(scopeId: string, request: DataSubjectRequest) : Async<ExportTicket> = async {
            let ticket = mintTicket scopeId

            match parseTicket ticket with
            | Some(_, guid) ->
                let createdAt = DateTimeOffset.UtcNow
                let path = statusPath scopeId guid

                let! written =
                    statuses.Replace(
                        ScopeKey.platform,
                        path,
                        {
                            Status = ExportStatus.Preparing
                            SubjectUserId = request.SubjectUserId
                            RequestId = request.Id
                            CreatedAt = createdAt
                            ExpiresAt = createdAt + ttl
                        }
                    )

                orRaise path written
                return ticket
            | None ->
                // Mint always produces a parseable ticket; defensive only.
                return ticket
        }

        member _.GetStatus(ticket: ExportTicket) : Async<ExportStatus> = async {
            match parseTicket ticket with
            | None -> return ExportStatus.Unknown
            | Some(scopeId, guid) ->
                let! record = readStatus scopeId guid
                return record |> Option.map effective |> Option.defaultValue ExportStatus.Unknown
        }

        member _.Complete(ticket: ExportTicket, envelope: byte[]) : Async<unit> = async {
            match parseTicket ticket with
            | None -> return ()
            | Some(scopeId, guid) ->
                let path = envelopePath scopeId guid
                let! written = envelopes.Replace(ScopeKey.platform, path, envelope)
                orRaise path written
                do! transition scopeId guid (ExportStatus.Ready(int64 envelope.Length))
        }

        member _.Fail(ticket: ExportTicket, reason: string) : Async<unit> = async {
            match parseTicket ticket with
            | None -> return ()
            | Some(scopeId, guid) -> do! transition scopeId guid (ExportStatus.Failed reason)
        }

        member _.Cancel(ticket: ExportTicket) : Async<unit> = async {
            match parseTicket ticket with
            | None -> return ()
            | Some(scopeId, guid) -> do! transition scopeId guid ExportStatus.Cancelled
        }

        member _.Download(ticket: ExportTicket) : Async<Result<byte[], ExportStatus>> = async {
            match parseTicket ticket with
            | None -> return Error ExportStatus.Unknown
            | Some(scopeId, guid) ->
                let! record = readStatus scopeId guid

                match record |> Option.map effective |> Option.defaultValue ExportStatus.Unknown with
                | ExportStatus.Ready _ ->
                    let! read = envelopes.Read(ScopeKey.platform, envelopePath scopeId guid)

                    match DocumentUnavailable.orRaise read with
                    | Some bytes -> return Ok bytes
                    // Envelope GC'd but status not yet swept → treat as Expired.
                    | None -> return Error ExportStatus.Expired
                | other -> return Error other
        }

module BlobBackedBackgroundExportStore =
    /// Default TTL for a completed export envelope (7 days).
    let DefaultTtl = TimeSpan.FromDays 7.0

    /// Construct the default blob-backed store with the 7-day TTL.
    let create (blobs: IBlobStorage) : IBackgroundExportStore =
        BlobBackedBackgroundExportStore(blobs, DefaultTtl) :> IBackgroundExportStore

    /// Construct with an explicit envelope TTL.
    let createWithTtl (blobs: IBlobStorage) (ttl: TimeSpan) : IBackgroundExportStore =
        BlobBackedBackgroundExportStore(blobs, ttl) :> IBackgroundExportStore