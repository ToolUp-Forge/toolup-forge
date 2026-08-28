// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DataSubject.Tests.Support.Doubles

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.DataSubjectRequestApiHandler

/// In-memory `IBlobStorage` for the background-export-store tests.
type InMemoryBlobStorage() =
    let store = ConcurrentDictionary<string * string, byte[]>()

    interface IBlobStorage with
        // Phase 741 — no bounded multi-part commit primitive here; callers assemble through memory.
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) = async {
            store[(container, blobName)] <- content
            return Ok blobName
        }

        member _.Download(container, blobName) = async {
            match store.TryGetValue((container, blobName)) with
            | true, v -> return Ok v
            | false, _ -> return Error "not found"
        }

        member this.DownloadRange(container, blobName, offset, length) =
            ToolUp.Platform.BlobStorage.downloadRangeViaDownload (this :> IBlobStorage) container blobName offset length

        member _.Delete(container, blobName) = async {
            store.TryRemove((container, blobName)) |> ignore
            return Ok()
        }

        member _.List(container, prefix) = async {
            return
                store.Keys
                |> Seq.filter (fun (c, n) -> c = container && n.StartsWith(prefix, StringComparison.Ordinal))
                |> Seq.map snd
                |> List.ofSeq
        }

        member _.Exists(container, blobName) = async { return store.ContainsKey((container, blobName)) }

        member _.GetMetadata(container, blobName) = async {
            match store.TryGetValue((container, blobName)) with
            | true, v ->
                return
                    Ok {
                        Size = int64 v.Length
                        LastModified = DateTime.UtcNow
                        ContentType = None
                    }
            | false, _ -> return Error "not found"
        }

        member _.Erase(_, _, _, _) = async {
            return
                Ok {
                    HandlerName = "InMemoryBlobStorage"
                    RecordsAffected = 0
                    Note = None
                }
        }

/// No-op `ILogger` for tests.
type NoOpLogger() =
    interface ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()

/// `IDataExporter` returning a fixed segment for the subject.
type FakeExporter(name: string) =
    interface IDataExporter with
        member _.Name = name

        member _.Export(_scopeId: string, subjectUserId: string) = async {
            return [
                {
                    Name = name
                    MimeType = "application/json"
                    Body = Text.Encoding.UTF8.GetBytes $"{name}:{subjectUserId}"
                }
            ]
        }

/// `IErasureHandler` reporting a fixed affected-count.
type FakeErasureHandler(name: string, affected: int) =
    interface IErasureHandler with
        member _.Name = name

        member _.Erase(_scopeId, _subject, _policy) = async {
            return
                Ok {
                    HandlerName = name
                    RecordsAffected = affected
                    Note = None
                }
        }

        member _.Preview(_scopeId, _subject, _policy) = async {
            return {
                HandlerName = name
                RecordsAffected = affected
                Note = None
            }
        }

/// Capturing `AuditOnDsr` — records every event for assertion.
type CapturingAudit() =
    let events = ResizeArray<DsrAuditEvent>()
    member _.Events: DsrAuditEvent list = List.ofSeq events
    member _.Callback: AuditOnDsr = fun e -> async { events.Add e }

/// Build a `JobContext` for handler tests.
let jobContext (scopeId: string) (payload: string) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = scopeId
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "admin")
    Attempt = 1
    Trigger = Manual
    TriggerSource = ScheduledManually "admin"
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = payload
    DeadLetterDestination = None
}

/// A sample DSR export request.
let sampleRequest (subject: string) : DataSubjectRequest = {
    Id = "req-" + subject
    Kind = DataSubjectRequestKind.Export
    SubjectUserId = subject
    TeamId = Some "team-1"
    RequestedBy = "admin"
    RequestedAt = DateTimeOffset.UtcNow
    Reason = "Article 15 export"
    Policy = ErasurePolicy.Tombstone
}