module ToolUp.Platform.AuditSinks.GcsArchive

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 16c GcsArchive `IAuditSink` companion. Appends gzipped JSONL
// blobs to a deploying-org-controlled Google Cloud Storage bucket
// (bucket-level Retention Policy enabled for compliance-grade WORM
// archives). Production deployments wire `GoogleCloudStorage`
// (Phase 2) as the `IBlobStorage` implementation; local development
// can wire `LocalFileStorage` against a directory and inspect the
// files directly.
//
// **Why no `Google.Cloud.Storage.V1` dependency.** This companion
// writes through the abstract `IBlobStorage` interface — `Upload`
// is the only method called. Production deployments inject
// `GoogleCloudStorage` for real GCS, which IS the home of the
// `Google.Cloud.Storage.V1` dependency; companies running on S3 /
// Azure swap in the matching `IBlobStorage` companion and the audit
// archive flows there instead. The retention policy is configured
// at the bucket level via `gcloud storage buckets update --retention-period`
// (or Terraform / Pulumi), not at the SDK level, so the per-blob
// upload path is identical to a non-WORM bucket.
//
// **Blob naming.** `{prefix}/{yyyy-MM-dd}/{HH-mm-ss-fffffff}-{sinkName}-{batchUuid}.jsonl.gz`.
// The leading date bucket gives O(1) listing for date-range queries
// (auditors typically want "everything from 2026-04-01 to
// 2026-04-30"). The trailing UUID guarantees uniqueness when two
// batches land in the same wall-clock second.
//
// **JSONL format.** One `AuditEvent` per line, JSON-serialised via
// `FableJsonConverter` (the SDK's canonical converter for non-
// Remoting JSON crossing the server/Fable boundary). Auditors
// reading the archive parse line-by-line; gzip-compression is
// transparent to most tooling (GNU `zcat`, `gcloud storage cp ... -
// | gunzip -c`, BigQuery's GZIP-aware JSON parsers).

/// Settings for the GCS archive sink. Production deployments override
/// `Container` per environment (one bucket per dev / staging / prod);
/// the optional `PathPrefix` lets a single bucket host audit archives
/// from multiple SDK consumers without leaf collisions.
type GcsArchiveSettings = {
    /// Bucket name (the `Container` field is named to match the
    /// `IBlobStorage` contract; in GCS this is the bucket name —
    /// globally unique, lowercase, dns-compliant, 3–63 chars). With
    /// `LocalFileStorage`, this is a subdirectory under the storage
    /// root. Operators are responsible for ensuring the bucket has a
    /// Retention Policy enabled (GCS) or appropriate filesystem ACLs
    /// (local) — the sink writes the blob; the destination owns the
    /// immutability contract.
    Container: string
    /// Optional path prefix within the bucket. `Some "audit"` →
    /// objects land at
    /// `audit/2026-05-05/14-23-45-{sinkName}-{uuid}.jsonl.gz`.
    /// `None` → objects land at `2026-05-05/...` (bucket root).
    /// Useful when one bucket hosts archives from multiple
    /// deployments / environments / SDK tenants.
    PathPrefix: string option
}

module GcsArchiveSettings =
    /// Default settings — use the canonical bucket name
    /// `audit-archive` with no path prefix. Most deployments override
    /// this; the default is provided so smoke tests / dev rigs can
    /// boot without per-environment config.
    let defaults: GcsArchiveSettings = {
        Container = "audit-archive"
        PathPrefix = None
    }

let private archiveJsonOptions = FableConverters.create ()

/// Wire-format wrapper for one persisted JSONL line. Each line of the
/// archive is one of these records — `Subject`, `OccurredAt`,
/// `ScopeId` alongside the event payload. Auditors reading the
/// archive parse a single envelope per line and route by
/// `SchemaVersion` / `SubjectKind` without needing to decode the
/// inner DU.
type private GcsArchiveLine = {
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
let private serializeBatch (batch: AuditEnvelope list) : byte[] =
    let lines =
        batch
        |> List.map (fun envelope ->
            let line: GcsArchiveLine = {
                SchemaVersion = AuditSchemaVersion.current
                OccurredAt = envelope.OccurredAt
                ScopeId = envelope.ScopeId
                SubjectKind = AuditEnvelope.subjectKindString envelope
                Subject = envelope.Subject
                EventType = AuditEvent.eventTypeName envelope.Event
                Event = envelope.Event
            }

            JsonSerializer.Serialize(line, archiveJsonOptions))

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
let buildBlobName (settings: GcsArchiveSettings) (sinkName: string) (now: DateTime) (batchUuid: Guid) : string =
    let dateBucket = now.ToString "yyyy-MM-dd"
    let timestamp = now.ToString "HH-mm-ss-fffffff"
    let leaf = sprintf "%s-%s-%s.jsonl.gz" timestamp sinkName (batchUuid.ToString "N")

    match settings.PathPrefix with
    | Some prefix when not (String.IsNullOrWhiteSpace prefix) -> sprintf "%s/%s/%s" (prefix.Trim '/') dateBucket leaf
    | _ -> sprintf "%s/%s" dateBucket leaf

/// SDK-default GCS archive `IAuditSink`. One blob per delivered batch.
/// Empty batches succeed without writing.
type GcsArchiveAuditSink(name: string, settings: GcsArchiveSettings, blobStorage: IBlobStorage) =

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
                    | Error msg -> return Error(sprintf "GcsArchive object upload failed: %s" msg)
                with ex ->
                    return Error(sprintf "GcsArchive sink threw: %s" ex.Message)
        }

/// Construct a sink configured for the given bucket + storage
/// implementation. The sink's `Name` doubles as the cursor-key suffix
/// in the replicator and the middle segment of every archived blob
/// name, so choose something stable and deployment-unique
/// (`"gcs-prod-audit"` rather than `"audit"`).
let create (name: string) (settings: GcsArchiveSettings) (blobStorage: IBlobStorage) : IAuditSink =
    GcsArchiveAuditSink(name, settings, blobStorage) :> _