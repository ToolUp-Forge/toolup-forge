module ToolUp.Platform.Tests.InProcess.S3ArchiveAuditSinkTests

open System
open System.Collections.Concurrent
open System.IO
open System.IO.Compression
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.S3Archive
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

/// Phase 9g — bind `IAuditSinkContract` to the S3Archive companion
/// running against per-test `LocalFileStorage` directories. Production
/// deployments wire `AwsS3Storage` instead; the contract is identical.
///
/// Each `factory ()` call constructs a fresh working directory so
/// tests running in parallel don't accumulate blobs into a shared
/// archive. The directory-per-sink mapping is tracked in a
/// `ConcurrentDictionary` keyed by sink reference identity so the
/// verifier can locate the archive for each sink instance.

let private uniqueDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-s3archive-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private readJsonlGz (path: string) : string list =
    use fs = File.OpenRead path
    use gz = new GZipStream(fs, CompressionMode.Decompress)
    use reader = new StreamReader(gz, Encoding.UTF8)

    let text = reader.ReadToEnd()

    if String.IsNullOrEmpty text then
        []
    else
        text.Split([| '\n' |]) |> Array.toList

/// Per-sink working-directory map. Populated by `factory ()` and read
/// by `verifyDelivered`. Reference-identity keying via
/// `ConcurrentDictionary<obj, string>` so two sinks constructed at the
/// same wall-clock instant don't collide.
let private workingDirs = ConcurrentDictionary<obj, string>()

let private settings: S3ArchiveSettings = {
    Container = "audit-archive"
    PathPrefix = Some "test"
}

let tests =
    let factory () =
        let dir = uniqueDir ()
        let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
        let sink = create "test-sink" settings storage
        workingDirs[box sink] <- dir
        sink

    let verifyDelivered (sink: IAuditSink) (expected: AuditEnvelope list list) =
        let dir = workingDirs[box sink]

        let archiveRoot =
            Path.Combine(dir, settings.Container, "test", DateTime.UtcNow.ToString "yyyy-MM-dd")

        let files =
            if Directory.Exists archiveRoot then
                Directory.GetFiles(archiveRoot, "*.jsonl.gz") |> Array.sort |> Array.toList
            else
                []

        Expect.hasLength files (List.length expected) "one .jsonl.gz blob per delivered batch"

        for blobPath, expectedBatch in List.zip files expected do
            let lines = readJsonlGz blobPath

            let nonEmptyLines =
                lines |> List.filter (fun l -> not (String.IsNullOrWhiteSpace l))

            Expect.hasLength
                nonEmptyLines
                (List.length expectedBatch)
                (sprintf "blob %s must contain one JSON line per envelope in the batch" (Path.GetFileName blobPath))

    IAuditSinkContract.tests "S3ArchiveAuditSink" factory verifyDelivered