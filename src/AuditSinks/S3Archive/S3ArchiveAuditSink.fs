module ToolUp.Platform.AuditSinks.S3Archive

open System
open System.IO
open System.IO.Compression
open System.Text
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 9g S3Archive `IAuditSink` companion. Appends gzipped JSONL
// blobs to a deploying-org-controlled S3 bucket (Object Lock enabled
// at the bucket level for compliance-grade WORM archives).
// Production deployments wire `AwsS3Storage` (Phase 2) as the
// `IBlobStorage` implementation; local development can wire
// `LocalFileStorage` against a directory and inspect the files
// directly.
//
// **Why no `AWSSDK.S3` dependency.** This companion writes through
// the abstract `IBlobStorage` interface — `Upload` is the only
// method called. Production deployments inject `AwsS3Storage` for
// real S3, which IS the home of the AWS SDK dependency; companies
// running on Azure / GCS swap in the matching `IBlobStorage`
// companion and the audit archive flows there instead. Object Lock
// is configured at the bucket level via the AWS console / IaC, not
// at the SDK level, so the per-blob upload path is identical to a
// non-WORM bucket.
//
// **Blob naming.** `{prefix}/{yyyy-MM-dd}/{HH-mm-ss}-{sinkName}-{batchUuid}.jsonl.gz`.
// The leading date bucket gives O(1) listing for date-range queries
// (operators and auditors typically want "everything from
// 2026-04-01 to 2026-04-30"). The trailing UUID guarantees
// uniqueness when two batches land in the same wall-clock second
// — bounded-channel drain + millisecond-resolution batching means
// this happens routinely under load.
//
// **JSONL format.** One `AuditEvent` per line, JSON-serialised via
// `FableJsonConverter` (the SDK's canonical converter for non-
// Remoting JSON crossing the server/Fable boundary). Auditors
// reading the archive parse line-by-line; gzip-compression is
// transparent to most tooling (GNU `zcat`, `aws s3 cp ... -` plus
// `gzip -d -`, Athena's GZIP support).

/// Settings for the S3 archive sink. Production deployments override
/// `Container` per environment (one bucket per dev / staging / prod);
/// the optional `PathPrefix` lets a single bucket host audit archives
/// from multiple SDK consumers without leaf collisions.
type S3ArchiveSettings = {
    /// Container name. With `AwsS3Storage`, this is the S3 bucket name
    /// (must be globally unique, lowercase, dns-compliant). With
    /// `LocalFileStorage`, this is a subdirectory under the storage
    /// root. Operators are responsible for ensuring the bucket /
    /// directory has Object Lock enabled (S3) or appropriate
    /// filesystem ACLs (local) — the sink writes the blob; the
    /// destination owns the immutability contract.
    Container: string
    /// Optional path prefix within the container. `Some "audit"` →
    /// blobs land at `audit/2026-05-05/14-23-45-{sinkName}-{uuid}.jsonl.gz`.
    /// `None` → blobs land at `2026-05-05/...` (container root).
    /// Useful when one bucket hosts archives from multiple
    /// deployments / environments / SDK tenants.
    PathPrefix: string option
}

module S3ArchiveSettings =
    /// Default settings — use the canonical container `audit-archive`
    /// with no path prefix. Most deployments override this; the
    /// default is provided so smoke tests / dev rigs can boot without
    /// per-environment config.
    let defaults: S3ArchiveSettings = {
        Container = "audit-archive"
        PathPrefix = None
    }

let private archiveJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

/// Wire-format wrapper for one persisted JSONL line. Each line of the
/// archive is one of these records — `Subject`, `OccurredAt`, `ScopeId`
/// alongside the event payload. Auditors reading the archive parse a
/// single envelope per line and route by `SchemaVersion` /
/// `SubjectKind` without needing to decode the inner DU.
///
/// The shape is deliberately flat-and-stable: any change to it bumps
/// `AuditSchemaVersion.current`. The `Subject` field carries the
/// `AuditSubject` discriminated union which serialises as a tagged
/// object via `FableJsonConverter` (e.g.
/// `{"Case":"TeamAudit","Fields":["u-1","t-1"]}`).
type private S3ArchiveLine = {
    SchemaVersion: int
    OccurredAt: DateTime
    ScopeId: string
    SubjectKind: string
    Subject: AuditSubject
    EventType: string
    Event: AuditEvent
}

/// Serialise a batch into gzipped JSONL bytes. One envelope per line;
/// `\n` line separator (LF, not CRLF — most parsers accept either,
/// but LF is the convention for cloud / Linux archive consumers).
/// Phase 66 Stream B.7 — each line is an `S3ArchiveLine` wrapper that
/// carries `SchemaVersion`, the resolved subject, and the recording
/// scope, not just the bare event.
let private serializeBatch (batch: AuditEnvelope list) : byte[] =
    let lines =
        batch
        |> List.map (fun envelope ->
            let line: S3ArchiveLine = {
                SchemaVersion = AuditSchemaVersion.current
                OccurredAt = envelope.OccurredAt
                ScopeId = envelope.ScopeId
                SubjectKind = AuditEnvelope.subjectKindString envelope
                Subject = envelope.Subject
                EventType = AuditEvent.eventTypeName envelope.Event
                Event = envelope.Event
            }

            JsonConvert.SerializeObject(line, archiveJsonSettings))

    let jsonl = String.Join("\n", lines)
    let bytes = Encoding.UTF8.GetBytes jsonl

    use ms = new MemoryStream()

    do
        use gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen = true)
        gz.Write(bytes, 0, bytes.Length)

    ms.ToArray()

/// Compute the blob name for `now`. Public to make the layout
/// inspectable in tests; production code calls it through the sink's
/// `Deliver`. `now` parameterised so tests can assert the exact path.
let buildBlobName (settings: S3ArchiveSettings) (sinkName: string) (now: DateTime) (batchUuid: Guid) : string =
    let dateBucket = now.ToString "yyyy-MM-dd"
    let timestamp = now.ToString "HH-mm-ss-fffffff"
    let leaf = sprintf "%s-%s-%s.jsonl.gz" timestamp sinkName (batchUuid.ToString "N")

    match settings.PathPrefix with
    | Some prefix when not (String.IsNullOrWhiteSpace prefix) -> sprintf "%s/%s/%s" (prefix.Trim '/') dateBucket leaf
    | _ -> sprintf "%s/%s" dateBucket leaf

/// SDK-default S3 archive `IAuditSink`. One blob per delivered batch.
/// Empty batches succeed without writing — the dispatcher only calls
/// `Deliver` with non-empty batches in practice but the contract pack
/// asserts the empty case.
type S3ArchiveAuditSink(name: string, settings: S3ArchiveSettings, blobStorage: IBlobStorage) =

    interface IAuditSink with
        member _.Name = name

        member _.SchemaVersion = AuditSchemaVersion.current

        member _.Deliver(batch) = async {
            if List.isEmpty batch then
                return Ok()
            else
                try
                    let blobName = buildBlobName settings name DateTime.UtcNow (Guid.NewGuid())
                    let bytes = serializeBatch batch
                    let! result = blobStorage.Upload(settings.Container, blobName, bytes)

                    match result with
                    | Ok _ -> return Ok()
                    | Error msg -> return Error(sprintf "S3Archive blob upload failed: %s" msg)
                with ex ->
                    return Error(sprintf "S3Archive sink threw: %s" ex.Message)
        }

/// Construct a sink configured for the given container + storage
/// implementation. The sink's `Name` doubles as the cursor-key suffix
/// in the replicator (`_platform/audit-replicator/{Name}/{scopeId}.cursor`)
/// and the middle segment of every archived blob name, so choose
/// something stable and deployment-unique (`"s3-prod-audit"` rather
/// than `"audit"`).
let create (name: string) (settings: S3ArchiveSettings) (blobStorage: IBlobStorage) : IAuditSink =
    S3ArchiveAuditSink(name, settings, blobStorage) :> _