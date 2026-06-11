// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Text.Json.Nodes
open ToolUp.Platform.BlobStorage

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

/// Default blob-backed `IBackgroundExportStore`. `ttl` bounds how long a
/// completed envelope stays downloadable (default 7 days).
type BlobBackedBackgroundExportStore(blobs: IBlobStorage, ttl: TimeSpan) =

    [<Literal>]
    static let Container = "_platform"

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

    let statusBlob (scopeId: string) (guid: string) =
        $"data-subject-requests/{scopeId}/{guid}.status"

    let envelopeBlob (scopeId: string) (guid: string) =
        $"data-subject-requests/{scopeId}/{guid}.envelope"

    // ── status sidecar (de)serialisation ───────────────────────────────

    let writeStatus
        (scopeId: string)
        (guid: string)
        (status: ExportStatus)
        (subjectUserId: string)
        (requestId: string)
        (createdAt: DateTimeOffset)
        : Async<unit> =
        async {
            let o = JsonObject()
            o["status"] <- JsonValue.Create(ExportStatus.name status)

            match status with
            | ExportStatus.Ready bytes -> o["sizeBytes"] <- JsonValue.Create bytes
            | ExportStatus.Failed reason -> o["reason"] <- JsonValue.Create reason
            | _ -> ()

            o["subjectUserId"] <- JsonValue.Create subjectUserId
            o["requestId"] <- JsonValue.Create requestId
            o["createdAt"] <- JsonValue.Create(createdAt.ToString("O"))
            o["expiresAt"] <- JsonValue.Create((createdAt + ttl).ToString("O"))
            let bytes = Encoding.UTF8.GetBytes(o.ToJsonString())
            let! _ = blobs.Upload(Container, statusBlob scopeId guid, bytes)
            return ()
        }

    /// Read the persisted status, mapping a lapsed TTL to `Expired`.
    let readStatus (scopeId: string) (guid: string) : Async<ExportStatus * string * string * DateTimeOffset> = async {
        match! blobs.Download(Container, statusBlob scopeId guid) with
        | Error _ -> return ExportStatus.Unknown, "", "", DateTimeOffset.MinValue
        | Ok bytes ->
            try
                let node = JsonNode.Parse(Encoding.UTF8.GetString bytes)
                let subject = node["subjectUserId"].GetValue<string>()
                let requestId = node["requestId"].GetValue<string>()
                let createdAt = DateTimeOffset.Parse(node["createdAt"].GetValue<string>())
                let expiresAt = DateTimeOffset.Parse(node["expiresAt"].GetValue<string>())

                let raw =
                    match node["status"].GetValue<string>() with
                    | "Preparing" -> ExportStatus.Preparing
                    | "Ready" -> ExportStatus.Ready(node["sizeBytes"].GetValue<int64>())
                    | "Failed" -> ExportStatus.Failed(node["reason"].GetValue<string>())
                    | "Cancelled" -> ExportStatus.Cancelled
                    | "Expired" -> ExportStatus.Expired
                    | _ -> ExportStatus.Unknown

                let effective =
                    if DateTimeOffset.UtcNow > expiresAt then
                        ExportStatus.Expired
                    else
                        raw

                return effective, subject, requestId, createdAt
            with _ ->
                return ExportStatus.Unknown, "", "", DateTimeOffset.MinValue
    }

    /// Re-persist a status transition, preserving the original subject /
    /// requestId / createdAt so the TTL window doesn't reset.
    let transition (scopeId: string) (guid: string) (status: ExportStatus) : Async<unit> = async {
        let! _, subject, requestId, createdAt = readStatus scopeId guid

        if createdAt = DateTimeOffset.MinValue then
            // No prior status (Unknown) — nothing to transition.
            return ()
        else
            do! writeStatus scopeId guid status subject requestId createdAt
    }

    interface IBackgroundExportStore with
        member _.BeginExport(scopeId: string, request: DataSubjectRequest) : Async<ExportTicket> = async {
            let ticket = mintTicket scopeId

            match parseTicket ticket with
            | Some(_, guid) ->
                do!
                    writeStatus
                        scopeId
                        guid
                        ExportStatus.Preparing
                        request.SubjectUserId
                        request.Id
                        DateTimeOffset.UtcNow

                return ticket
            | None ->
                // Mint always produces a parseable ticket; defensive only.
                return ticket
        }

        member _.GetStatus(ticket: ExportTicket) : Async<ExportStatus> = async {
            match parseTicket ticket with
            | None -> return ExportStatus.Unknown
            | Some(scopeId, guid) ->
                let! status, _, _, _ = readStatus scopeId guid
                return status
        }

        member _.Complete(ticket: ExportTicket, envelope: byte[]) : Async<unit> = async {
            match parseTicket ticket with
            | None -> return ()
            | Some(scopeId, guid) ->
                let! _ = blobs.Upload(Container, envelopeBlob scopeId guid, envelope)
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
                let! status, _, _, _ = readStatus scopeId guid

                match status with
                | ExportStatus.Ready _ ->
                    match! blobs.Download(Container, envelopeBlob scopeId guid) with
                    | Ok bytes -> return Ok bytes
                    // Envelope GC'd but status not yet swept → treat as Expired.
                    | Error _ -> return Error ExportStatus.Expired
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