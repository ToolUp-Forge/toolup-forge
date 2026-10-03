// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Common

open System
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Secrets
open ToolUp.Platform.Transport
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 128 — incremental sync cursors ────────────────────────
//
// Where an incremental read of a third-party resource has got to, kept
// in `IEntityStore` by value and keyed by (tenant scope, connector, data
// source, endpoint), so a sync survives a process restart, a redeploy, or
// a job landing on another instance. Two positions are kept:
//
//   * `Resume`    — the page cursor inside a pass that has not finished
//                   (`None` between passes). A run that stops — a page
//                   cap, a failure, a crash — continues from here.
//   * `Watermark` — the high-water mark of the last COMPLETED pass; an
//                   incremental pass reads what changed since it. The
//                   largest mark seen inside an unfinished pass is held in
//                   `PendingWatermark` and promoted only when the pass
//                   completes, so a pass that dies half-way never skips
//                   the rows it had not reached.
//
// The cursor advances after each page is handed to the sink, through a
// compare-and-set write (`SaveIfVersion`): two runs that race on one
// cursor resolve to one advancing and one stopping, never both ingesting
// the same page. The residual window is a crash between the sink taking a
// page and the cursor write — that page is offered again on the next run
// (at-least-once, at most one page). Sinks that key rows by the
// provider's id make the replay a no-op.
//
// `IncrementalSync.handler` is the `IJobHandler` shape for running a pass
// on the Phase 9b scheduler: stateless — every invocation re-reads the
// cursor (GP 12 rule 4) and carries everything else in its payload.

/// The persisted cursor entity (Phase 19 `IEntity` shape). Read and write
/// it through `SyncCursor`, which works in the typed `SyncCursor` view.
type SyncCursorEntity = {
    Id: EntityId
    Type: string
    Version: int
    Connector: string
    SourceId: string
    Endpoint: string
    /// `PageCursor.encode` of the resume point; `None` between passes.
    Resume: string option
    Watermark: string option
    PendingWatermark: string option
    CompletedPasses: int
}

/// A sync cursor, by value.
type SyncCursor = {
    Connector: string
    SourceId: string
    Endpoint: string
    Resume: PageCursor option
    Watermark: string option
    PendingWatermark: string option
    CompletedPasses: int
    /// The stored version this value was read at — `0` for a cursor never
    /// saved. The next save is conditional on it.
    Version: int
}

/// Why a cursor could not be read or written.
[<RequireQualifiedAccess>]
type SyncCursorError =
    /// Another run advanced the cursor first.
    | Conflict of expected: int * actual: int
    /// The stored resume point is not a cursor `PageCursor.encode` wrote.
    | Corrupt of stored: string
    | Store of error: EntityError

[<RequireQualifiedAccess>]
module SyncCursor =

    /// The entity type the cursor is stored under.
    [<Literal>]
    let EntityType = "ExternalApiSyncCursor"

    /// The registration a composition root adds (`ServerApp.withEntity`, or
    /// `EntityRegistry.Register`) before the cursor store is used.
    let registration: EntityRegistration<SyncCursorEntity> =
        EntityRegistration.create<SyncCursorEntity> EntityType

    let private segment (value: string) : string =
        let builder = StringBuilder()

        for ch in value do
            if Char.IsAsciiLetterOrDigit ch || ch = '_' then
                builder.Append ch |> ignore
            else
                builder.Append('-').Append((int ch).ToString "x4") |> ignore

        builder.ToString()

    /// The deterministic entity id for a (connector, data source, endpoint)
    /// key. Each part is escaped to `[A-Za-z0-9_-]`, `-` only ever opening
    /// an escape, so distinct keys never share an id.
    let entityId (connector: string) (sourceId: string) (endpoint: string) : EntityId =
        $"{segment connector}.{segment sourceId}.{segment endpoint}"

    /// The cursor of a sync that has never run.
    let initial (connector: string) (sourceId: string) (endpoint: string) : SyncCursor = {
        Connector = connector
        SourceId = sourceId
        Endpoint = endpoint
        Resume = None
        Watermark = None
        PendingWatermark = None
        CompletedPasses = 0
        Version = 0
    }

    let private ofEntity (entity: SyncCursorEntity) : Result<SyncCursor, SyncCursorError> =
        let resume =
            match entity.Resume with
            | None -> Ok None
            | Some stored ->
                match PageCursor.tryDecode stored with
                | Some cursor -> Ok(Some cursor)
                | None -> Error(SyncCursorError.Corrupt stored)

        resume
        |> Result.map (fun resume -> {
            Connector = entity.Connector
            SourceId = entity.SourceId
            Endpoint = entity.Endpoint
            Resume = resume
            Watermark = entity.Watermark
            PendingWatermark = entity.PendingWatermark
            CompletedPasses = entity.CompletedPasses
            Version = entity.Version
        })

    let private toEntity (cursor: SyncCursor) : SyncCursorEntity = {
        Id = entityId cursor.Connector cursor.SourceId cursor.Endpoint
        Type = EntityType
        Version = cursor.Version
        Connector = cursor.Connector
        SourceId = cursor.SourceId
        Endpoint = cursor.Endpoint
        Resume = cursor.Resume |> Option.map PageCursor.encode
        Watermark = cursor.Watermark
        PendingWatermark = cursor.PendingWatermark
        CompletedPasses = cursor.CompletedPasses
    }

    /// Read the cursor — the `initial` one when none was ever saved.
    let load
        (store: IEntityStore)
        (scopeId: string)
        (connector: string)
        (sourceId: string)
        (endpoint: string)
        : Async<Result<SyncCursor, SyncCursorError>> =
        async {
            let! read = store.Get<SyncCursorEntity>(scopeId, EntityType, entityId connector sourceId endpoint)

            return
                match read with
                | Ok entity -> ofEntity entity
                | Error(NotFound _) -> Ok(initial connector sourceId endpoint)
                | Error error -> Error(SyncCursorError.Store error)
        }

    /// Write `cursor` if, and only if, the stored cursor is still at
    /// `cursor.Version`. Returns the cursor at its new version.
    let save
        (store: IEntityStore)
        (scopeId: string)
        (actor: EntityPrincipal)
        (cursor: SyncCursor)
        : Async<Result<SyncCursor, SyncCursorError>> =
        async {
            let! written = store.SaveIfVersion<SyncCursorEntity>(scopeId, actor, toEntity cursor, cursor.Version)

            return
                match written with
                | Ok entityRef ->
                    Ok {
                        cursor with
                            Version = entityRef.Version
                    }
                | Error(VersionConflict(_, _, expected, actual)) -> Error(SyncCursorError.Conflict(expected, actual))
                | Error error -> Error(SyncCursorError.Store error)
        }

    let private later (a: string option) (b: string option) : string option =
        match a, b with
        | Some x, Some y -> Some(if String.CompareOrdinal(x, y) >= 0 then x else y)
        | Some _, None -> a
        | None, _ -> b

    /// The cursor after one page was taken: the resume point moves to the
    /// page's continuation, the page's largest watermark joins the pending
    /// one, and on the last page the pass completes — pending promoted,
    /// resume cleared.
    let advance (watermarks: string option list) (next: PageCursor option) (cursor: SyncCursor) : SyncCursor =
        let pending = watermarks |> List.fold later cursor.PendingWatermark

        match next with
        | Some continuation -> {
            cursor with
                Resume = Some continuation
                PendingWatermark = pending
          }
        | None -> {
            cursor with
                Resume = None
                Watermark = later cursor.Watermark pending
                PendingWatermark = None
                CompletedPasses = cursor.CompletedPasses + 1
          }

    let toMessage (error: SyncCursorError) : string =
        match error with
        | SyncCursorError.Conflict(expected, actual) ->
            $"the sync cursor moved under this run (expected version {expected}, found {actual})"
        | SyncCursorError.Corrupt stored -> $"the stored resume point '{stored}' is not a page cursor"
        | SyncCursorError.Store error -> $"the cursor store failed: %A{error}"

/// The job payload for one incremental pass — serialised into
/// `JobRegistration.Payload` by `IncrementalSync.payload`.
type IncrementalSyncRequest = {
    /// The data source being synced (its `Id` keys the cursor, its
    /// `ConnectionScope` and `CredentialKey` reach the connector).
    Source: DataSourceConfig
    /// The endpoint to read.
    Endpoint: string
    /// The principal that scheduled the sync — stamped on every cursor
    /// write (a job carries the principal that scheduled it).
    ScheduledBy: string
}

/// Composition-time dependencies of an incremental sync. Holds no per-run
/// state: every run re-reads its cursor.
type IncrementalSyncDeps<'Row> = {
    Connector: IExternalApiConnector<'Row>
    /// The outbound transport — decorate it with `OutboundRateBudget`.
    Transport: IHttpTransport
    Cursors: IEntityStore
    SecretStore: ISecretStore option
    Options: PagedFetchOptions
    /// Where a page's rows go. Called once per page, before the cursor
    /// moves past it; an `Error` stops the run and leaves the page to be
    /// offered again.
    Ingest: ExternalApiCall -> 'Row list -> Async<Result<unit, string>>
}

/// What one pass did.
type IncrementalSyncReport = {
    Pages: int
    Rows: int
    /// The pass reached the end of the resource (the cursor's watermark
    /// moved and its resume point cleared).
    Completed: bool
    Cursor: SyncCursor
    /// The page that stopped the run, if one did.
    Failure: PageError option
}

/// Why a pass could not start.
[<RequireQualifiedAccess>]
type IncrementalSyncError =
    | Cursor of SyncCursorError
    | Prepare of IngestionError

[<RequireQualifiedAccess>]
module IncrementalSync =

    let private jsonOptions = FableConverters.create ()

    /// The payload string for a `JobRegistration`.
    let payload (request: IncrementalSyncRequest) : string =
        JsonSerializer.Serialize(request, jsonOptions)

    /// Parse a payload `payload` wrote.
    let tryParsePayload (text: string) : Result<IncrementalSyncRequest, string> =
        try
            match box (JsonSerializer.Deserialize<IncrementalSyncRequest>(text, jsonOptions)) with
            | null -> Error "the payload is empty"
            | parsed -> Ok(unbox parsed)
        with ex ->
            Error $"the payload is not an incremental-sync request: {ex.Message}"

    /// Run one pass for `request` in `scopeId`: read the cursor, resolve
    /// the call from the cursor's watermark, page from the cursor's resume
    /// point, hand each page to `Ingest` and advance the cursor after it.
    let run
        (deps: IncrementalSyncDeps<'Row>)
        (scopeId: string)
        (request: IncrementalSyncRequest)
        : Async<Result<IncrementalSyncReport, IncrementalSyncError>> =
        async {
            let actor = EntityPrincipal.ofPrincipal request.ScheduledBy
            let connectorId = deps.Connector.Provider.Id

            let! loaded = SyncCursor.load deps.Cursors scopeId connectorId request.Source.Id request.Endpoint

            match loaded with
            | Error error -> return Error(IncrementalSyncError.Cursor error)
            | Ok cursor ->
                let ctx: DataSourceCallContext = {
                    ScopeId = scopeId
                    Config = request.Source
                    Credential = None
                }

                let! prepared =
                    ExternalApi.prepare deps.Connector deps.SecretStore ctx request.Endpoint cursor.Watermark

                match prepared with
                | Error error -> return Error(IncrementalSyncError.Prepare error)
                | Ok call ->
                    let step (cursor: SyncCursor, rows: int) (page: Page<'Row>) = async {
                        let! ingested = deps.Ingest call page.Rows

                        match ingested with
                        | Error message -> return Error message
                        | Ok() ->
                            let watermarks = page.Rows |> List.map deps.Connector.Watermark

                            let! saved =
                                SyncCursor.save
                                    deps.Cursors
                                    scopeId
                                    actor
                                    (SyncCursor.advance watermarks page.Next cursor)

                            return
                                saved
                                |> Result.map (fun saved -> saved, rows + page.Rows.Length)
                                |> Result.mapError SyncCursor.toMessage
                    }

                    let start = cursor.Resume |> Option.defaultValue PageCursor.First

                    let! outcome =
                        PagedFetch.fold
                            deps.Options
                            deps.Transport
                            (ExternalApi.endpoint deps.Connector call)
                            step
                            (cursor, 0)
                            start

                    let finalCursor, rows = outcome.State

                    return
                        Ok {
                            Pages = outcome.Pages
                            Rows = rows
                            Completed = outcome.Failure.IsNone && outcome.ResumeFrom.IsNone
                            Cursor = finalCursor
                            Failure = outcome.Failure
                        }
        }

    /// The scheduler verdict for a pass: a page cap that left work behind is
    /// a `Success` (the next scheduled run continues); a failure a later run
    /// could get past is `TransientFailure`; one that would repeat is
    /// `PermanentFailure`.
    let toJobResult (result: Result<IncrementalSyncReport, IncrementalSyncError>) : JobResult =
        match result with
        | Ok { Failure = None } -> Success
        | Ok { Failure = Some failure } ->
            let message = $"page {failure.PageIndex}: {PageFailure.toMessage failure.Failure}"

            if PageFailure.isTransient failure.Failure then
                TransientFailure message
            else
                PermanentFailure message
        | Error(IncrementalSyncError.Cursor(SyncCursorError.Conflict _ as error)) ->
            TransientFailure(SyncCursor.toMessage error)
        | Error(IncrementalSyncError.Cursor error) -> PermanentFailure(SyncCursor.toMessage error)
        | Error(IncrementalSyncError.Prepare(CredentialMissing key)) ->
            PermanentFailure $"credential '{key}' is missing"
        | Error(IncrementalSyncError.Prepare error) -> PermanentFailure $"%A{error}"

    /// The `IJobHandler` for incremental passes over `deps`. Register it
    /// once (`IJobScheduler.RegisterHandler`) and schedule a job per
    /// (data source, endpoint) with `payload`.
    let handler (deps: IncrementalSyncDeps<'Row>) : IJobHandler =
        { new IJobHandler with
            member _.Execute(ctx: JobContext) = async {
                match tryParsePayload ctx.Payload with
                | Error message -> return PermanentFailure message
                | Ok request ->
                    let! result = run deps ctx.ScopeId request
                    return toJobResult result
            }
        }