module ToolUp.Platform.AuditFallbackStore

open System
open System.IO
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── AuditFallbackStore (Phase 9t) ───────────────────────────────
//
// Bounded local-disk spill for audit records that failed to write to
// `IEventStore`, used when `ServerConfig.AuditFailurePolicy =
// DegradeToFile`. Deliberately LOCAL disk, not `IBlobStorage` — when
// the blob-backed event store is down, blob storage generally is
// too; the local spill is the one substrate still standing.
//
// Layout: `{root}/{yyyy-MM-dd}/{occurredAtTicks}-{eventId}.json`,
// one file per failed record, the full `ModuleEvent` envelope
// serialised via `FableConverters` so replay reconstructs exactly
// what `Record` tried to write (same `Id` — a replayed event is the
// original event, not a re-minted one).
//
// Bounded by `maxBytes` (directory total): at capacity, `Append`
// refuses and the record is lost with a loud `Error` log — audit
// spill must never eat the disk out from under the application.
// Sizing is recomputed per append; appends only happen while the
// event store is failing, so the O(files) walk is off the hot path
// by construction.

/// Default fallback root when `ServerConfig.AuditFallbackDirectory`
/// is `None`: `audit-fallback/` under the process working directory.
let defaultDirectory () =
    Path.Combine(Directory.GetCurrentDirectory(), "audit-fallback")

/// Default capacity bound — 64 MB of spilled audit records.
[<Literal>]
let DefaultMaxBytes = 67_108_864L

let private jsonOptions = FableConverters.create ()

/// Directory-backed spill store. Stateless between calls (the
/// directory is the state), so compose may construct more than one
/// instance over the same root (the audit log's spill writer and the
/// replay service's drain reader).
type AuditFallbackStore(root: string, maxBytes: int64, logger: ILogger) =

    let fileNameFor (evt: ModuleEvent) =
        Path.Combine(root, evt.OccurredAt.ToString "yyyy-MM-dd", $"{evt.OccurredAt.Ticks:D19}-{evt.Id:N}.json")

    let currentSizeBytes () =
        if Directory.Exists root then
            Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            |> Seq.sumBy (fun f -> FileInfo(f).Length)
        else
            0L

    /// Every spilled file, oldest first — the `{ticks}-{id}` name is
    /// the sort key, so lexical filename order IS chronological order.
    let pendingFiles () =
        if Directory.Exists root then
            Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            |> Seq.sortBy Path.GetFileName
            |> List.ofSeq
        else
            []

    member _.Root = root

    /// Spill one failed audit record. `Error` when the store is at
    /// capacity or the write itself fails — the caller logs the loss;
    /// there is no further fallback behind the fallback.
    member _.Append(evt: ModuleEvent) : Async<Result<unit, string>> = async {
        try
            let json = JsonSerializer.Serialize(evt, jsonOptions)
            let bytes = Encoding.UTF8.GetBytes json

            if currentSizeBytes () + int64 bytes.Length > maxBytes then
                return Error $"fallback store at capacity ({maxBytes} bytes) — record dropped"
            else
                let path = fileNameFor evt
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                do! File.WriteAllBytesAsync(path, bytes) |> Async.AwaitTask
                return Ok()
        with ex ->
            return Error ex.Message
    }

    /// Number of spilled records awaiting replay.
    member _.PendingCount() : int = pendingFiles () |> List.length

    /// Replay up to `batchSize` spilled records into `eventStore`,
    /// oldest first; each file is deleted only after its write
    /// succeeds. A file that cannot be read or decoded is quarantined
    /// (renamed `*.json.poison`, excluded from future passes) so one
    /// corrupt spill can never wedge the drain; a `Write` failure
    /// halts the pass (the store is still down — retrying the rest
    /// this pass would just burn the loop). Returns the number
    /// successfully replayed.
    member _.ReplayOnce(eventStore: IEventStore, batchSize: int) : Async<int> = async {
        let batch = pendingFiles () |> List.truncate batchSize
        let mutable replayed = 0
        let mutable halted = false

        for path in batch do
            if not halted then
                let decoded = async {
                    let! bytes = File.ReadAllBytesAsync path |> Async.AwaitTask
                    return JsonSerializer.Deserialize<ModuleEvent>(Encoding.UTF8.GetString bytes, jsonOptions)
                }

                match! Async.Catch decoded with
                | Choice2Of2 ex ->
                    // File-local failure — quarantine and keep draining.
                    logger.Error(
                        $"[AuditFallback] event=replay_poison file={Path.GetFileName path}: {ex.Message} — quarantined as .poison; the record is NOT replayed",
                        None
                    )

                    try
                        File.Move(path, path + ".poison")
                    with _ ->
                        halted <- true
                | Choice1Of2 evt ->
                    let written = async {
                        do! eventStore.Write evt
                        File.Delete path
                    }

                    match! Async.Catch written with
                    | Choice1Of2() -> replayed <- replayed + 1
                    | Choice2Of2 ex ->
                        halted <- true

                        logger.Warn
                            $"[AuditFallback] event=replay_halted file={Path.GetFileName path}: {ex.Message} — remaining records retry next pass"

        // Prune empty day-directories left behind by a full drain so
        // the layout doesn't accumulate husks across outages.
        if not halted && Directory.Exists root then
            for dir in Directory.EnumerateDirectories root do
                if Directory.EnumerateFileSystemEntries dir |> Seq.isEmpty then
                    try
                        Directory.Delete dir
                    with _ ->
                        ()

        return replayed
    }