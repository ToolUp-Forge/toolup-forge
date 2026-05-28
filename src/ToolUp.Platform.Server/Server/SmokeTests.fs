module ToolUp.Platform.SmokeTests.Defaults

open System
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.SmokeTests

// ─── First-party smoke tests (Phase 9o) ──────────────────────────────
//
// The six probes the SDK ships when
// `ServerConfig.SmokeTest = EnabledSmokeTest`. Each exercises one
// wired companion path end-to-end against the reserved sentinel
// scope `SmokeTest.SentinelScope` ("_smoke") and cleans up after
// itself inside `try`/`finally` (a failing impl still cleans up —
// the dispatcher does not compensate).

let private nonce () =
    Guid.NewGuid().ToString("N").Substring(0, 12)

/// Cap a smoke test at its `SmokeTest.defaultTimeout` ceiling. On
/// expiry returns `Fail "probe exceeded {ms}ms"` rather than
/// propagating the cancellation — the smoke endpoint is a
/// deploy-time go/no-go gate, and "ran too long" is operationally
/// equivalent to "failed".
let runWithTimeout (name: string) (body: unit -> Async<SmokeResult>) : Async<SmokeResult> = async {
    use cts = new CancellationTokenSource(SmokeTest.defaultTimeout)

    try
        let! result = Async.StartImmediateAsTask(body (), cts.Token) |> Async.AwaitTask
        return result
    with
    | :? OperationCanceledException ->
        let ms = int SmokeTest.defaultTimeout.TotalMilliseconds
        return Fail(sprintf "%s: probe exceeded %dms" name ms)
    | ex -> return Fail(sprintf "%s: probe threw: %s" name ex.Message)
}

// ─── BlobStorageSmoke ────────────────────────────────────────────────
//
// Upload + Download + Delete a sentinel blob under
// `SmokeTest.SentinelScope`. Cleanup: `try`/`finally` deletes the
// blob even when read-back fails.

type BlobStorageSmoke(storage: IBlobStorage) =
    interface ISmokeTest with
        member _.Name = "blob_storage"

        member _.RunOnce() =
            runWithTimeout "blob_storage"
            <| fun () -> async {
                let blobName = sprintf "smoke/probe-%s" (nonce ())
                let payload = System.Text.Encoding.UTF8.GetBytes(blobName)

                try
                    let! uploaded = storage.Upload(SmokeTest.SentinelScope, blobName, payload)

                    match uploaded with
                    | Error msg -> return Fail(sprintf "Upload failed: %s" msg)
                    | Ok _ ->
                        let! downloaded = storage.Download(SmokeTest.SentinelScope, blobName)

                        match downloaded with
                        | Error msg -> return Fail(sprintf "Download failed: %s" msg)
                        | Ok bytes ->
                            if bytes.Length <> payload.Length || bytes <> payload then
                                return
                                    Fail(
                                        sprintf
                                            "Round-trip mismatch: wrote %d bytes, read %d bytes"
                                            payload.Length
                                            bytes.Length
                                    )
                            else
                                return Pass
                finally
                    storage.Delete(SmokeTest.SentinelScope, blobName)
                    |> Async.RunSynchronously
                    |> ignore
            }

// ─── NotificationChannelSmoke ────────────────────────────────────────

type NotificationChannelSmoke(channel: INotificationChannel) =
    interface ISmokeTest with
        member _.Name = "notification_channel"

        member _.RunOnce() =
            runWithTimeout "notification_channel"
            <| fun () -> async {
                let nonceText = nonce ()
                let received = ref false

                let handler (envelope: NotificationEnvelope) =
                    match envelope.Notification with
                    | SystemMessage(_, text) when text.Contains nonceText -> received.Value <- true
                    | _ -> ()

                let! subId = channel.Subscribe(SmokeTest.SentinelScope, handler)

                try
                    let note =
                        SystemMessage(SystemMessageLevel.Info, sprintf "smoke probe %s" nonceText)

                    do! channel.Publish(SmokeTest.SentinelScope, note)

                    let deadline = DateTime.UtcNow.AddSeconds 1.0
                    let mutable elapsed = false

                    while not received.Value && not elapsed do
                        if DateTime.UtcNow >= deadline then
                            elapsed <- true
                        else
                            do! Async.Sleep 25

                    if received.Value then
                        return Pass
                    else
                        return Fail "Subscribed handler did not observe published SystemMessage within 1s"
                finally
                    channel.Unsubscribe subId |> Async.RunSynchronously
            }

// ─── EventStoreSmoke ─────────────────────────────────────────────────
//
// Write a `ModuleEvent` carrying a nonce + read back via
// `ReadBySource`. No cleanup: `IEventStore` has no delete surface;
// the sentinel scope absorbs the cost.

[<Literal>]
let private eventStoreSmokeSource = "_smoke"

type EventStoreSmoke(eventStore: IEventStore) =
    interface ISmokeTest with
        member _.Name = "event_store"

        member _.RunOnce() =
            runWithTimeout "event_store"
            <| fun () -> async {
                let nonceText = nonce ()

                let evt: ModuleEvent = {
                    Id = Guid.NewGuid()
                    OccurredAt = DateTime.UtcNow
                    ScopeId = SmokeTest.SentinelScope
                    SourceModule = eventStoreSmokeSource
                    EventType = "EventStoreSmokeProbe"
                    Payload = nonceText
                }

                do! eventStore.Write evt

                let! readBack = eventStore.ReadBySource(SmokeTest.SentinelScope, eventStoreSmokeSource)

                if readBack |> List.exists (fun e -> e.Payload = nonceText) then
                    return Pass
                else
                    return
                        Fail
                            "Wrote sentinel event via IEventStore.Write but ReadBySource did not find it. Underlying store is misconfigured or non-durable."
            }

// ─── DataObjectStoreSmoke ────────────────────────────────────────────

type DataObjectStoreSmoke(dataObjectStore: IDataObjectStore) =
    interface ISmokeTest with
        member _.Name = "data_object_store"

        member _.RunOnce() =
            runWithTimeout "data_object_store"
            <| fun () -> async {
                let objectId = sprintf "smoke-probe-%s" (nonce ())
                let payload = System.Text.Encoding.UTF8.GetBytes objectId

                try
                    let! saved =
                        dataObjectStore.Save(
                            SmokeTest.SentinelScope,
                            objectId,
                            payload,
                            "smoke_probe",
                            "_smoke",
                            Map.empty,
                            VersioningPolicy.Versioned
                        )

                    match saved with
                    | Error err -> return Fail(sprintf "Save failed: %A" err)
                    | Ok _ ->
                        let! fetched = dataObjectStore.Get(SmokeTest.SentinelScope, objectId)

                        match fetched with
                        | Error err -> return Fail(sprintf "Get failed: %A" err)
                        | Ok(_, bytes) ->
                            if bytes <> payload then
                                return
                                    Fail(
                                        sprintf
                                            "Round-trip mismatch: wrote %d bytes, read %d bytes"
                                            payload.Length
                                            bytes.Length
                                    )
                            else
                                return Pass
                finally
                    dataObjectStore.Delete(SmokeTest.SentinelScope, objectId)
                    |> Async.RunSynchronously
                    |> ignore
            }

// ─── JobSchedulerSmoke ───────────────────────────────────────────────
//
// Schedule a `Manual`-triggered sentinel job + verify the
// `JobScheduled` lifecycle event landed in `IEventStore` + Cancel
// the job.

[<Literal>]
let private smokeJobHandlerName = "_smoke"

type SmokeJobHandler() =
    interface IJobHandler with
        member _.Execute(_ctx: JobContext) = async { return JobResult.Success }

type JobSchedulerSmoke(scheduler: IJobScheduler, eventStore: IEventStore) =
    interface ISmokeTest with
        member _.Name = "job_scheduler"

        member _.RunOnce() =
            runWithTimeout "job_scheduler"
            <| fun () -> async {
                let nonceText = nonce ()

                let registration: JobRegistration = {
                    ScopeId = SmokeTest.SentinelScope
                    Handler = smokeJobHandlerName
                    Payload = nonceText
                    Trigger = Trigger.Manual
                    Idempotency = None
                    RetryPolicy = JobRetryPolicy.defaults
                    ShardKey = None
                    Precision = JobPrecision.Minute
                    CreatedBy = "_smoke"
                    Tags = Map.ofList [ "smoke", nonceText ]
                }

                let mutable scheduledJobId: JobId option = None

                try
                    let! scheduleOutcome = scheduler.Schedule registration

                    match scheduleOutcome with
                    | Error err -> return Fail(sprintf "Schedule failed: %A" err)
                    | Ok jobId ->
                        scheduledJobId <- Some jobId

                        let! events = eventStore.ReadByType(SmokeTest.SentinelScope, "JobScheduled")

                        if events |> List.exists (fun e -> e.Payload.Contains nonceText) then
                            return Pass
                        else
                            return
                                Fail
                                    "Schedule returned Ok but no JobScheduled lifecycle event was observed in IEventStore."
                finally
                    match scheduledJobId with
                    | Some jobId -> scheduler.Cancel(SmokeTest.SentinelScope, jobId) |> Async.RunSynchronously
                    | None -> ()
            }

// ─── AuditLogSmoke ───────────────────────────────────────────────────

type AuditLogSmoke(auditLog: IAuditLog) =
    interface ISmokeTest with
        member _.Name = "audit_log"

        member _.RunOnce() =
            runWithTimeout "audit_log"
            <| fun () -> async {
                let isNoOp = auditLog.GetType().Name.Contains "NoOp"

                if isNoOp then
                    return Pass
                else
                    let nonceText = nonce ()

                    let probeEvent =
                        AuditEvent.FileUploaded {
                            UserId = "_smoke"
                            FileName = sprintf "smoke-probe-%s" nonceText
                            DataType = "smoke_probe"
                            SizeBytes = 0L
                        }

                    do! auditLog.Record(SmokeTest.SentinelScope, probeEvent)

                    let! trail = auditLog.GetAuditTrail(SmokeTest.SentinelScope, None, Some "FileUploaded")

                    if
                        trail
                        |> List.exists (fun e ->
                            match e with
                            | AuditEvent.FileUploaded p -> p.FileName.Contains nonceText
                            | _ -> false)
                    then
                        return Pass
                    else
                        return
                            Fail
                                "Recorded sentinel FileUploaded via IAuditLog but GetAuditTrail did not return it. Underlying audit chain is broken."
            }