module ToolUp.Platform.Tests.InProcess.BlobStorageAuthAuditTests

open System
open System.Net
open Expecto
open ToolUp.Platform

// ─── Phase 2c — the credential-rejection trail ───────────────────────
//
// Two halves, proven separately because they live in different places
// for a reason (GP 1 — a vendor SDK type never enters
// `ToolUp.Platform.*`):
//
//   * CLASSIFICATION is per-companion, because deciding that an
//     exception is a 401/403 means naming `AmazonS3Exception` /
//     `RequestFailedException` / `GoogleApiException`. Each companion's
//     `authFailureStatus` is exercised here against REAL SDK exception
//     instances — including the `AggregateException`-wrapped shape,
//     which is the one that has silently broken arms in these files
//     twice (Phase 733's dead `416`, and GCS's dead `Delete` 404).
//   * RECORDING is shared, in `BlobStorageAuthAudit`, and is exercised
//     against a recording `IAuditLog`.
//
// What is NOT covered here, deliberately and with the reason stated so
// it is not mistaken for an omission: driving a companion end-to-end
// into a real 401/403. Two of the three companions authenticate at
// CONSTRUCTION (`AzureBlobStorage` calls `CreateIfNotExists`,
// `GoogleCloudStorage` eagerly builds its client), so an in-process
// instance cannot be built at all without a live or emulated backend.
// That arm belongs to the env-gated cloud-parity run — the same place
// the Phase 733 wrapper defects were finally measured — and the unit
// here is what makes it cheap to reason about when it runs.

// ─── Recording sink ──────────────────────────────────────────────────

/// Records every `(scopeId, event)` handed to it. Same shape as the
/// `RecordingAuditLog` several other packs carry; kept local rather than
/// shared because those are each private to their pack.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Recorded = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// A sink that fails on every write. `record`'s contract is that a
/// failing sink is swallowed — audit emission must never turn a storage
/// failure into a different failure.
type private ThrowingAuditLog() =
    interface IAuditLog with
        member _.Record(_, _) = async { return failwith "audit sink is down" }

        member _.GetAuditTrail(_, _, _) = async { return [] }

let private run (work: Async<unit>) = Async.RunSynchronously work

let private authRows (sink: RecordingAuditLog) =
    sink.Recorded
    |> List.choose (fun (scope, evt) ->
        match evt with
        | BlobStorageAuthFailed payload -> Some(scope, payload)
        | _ -> None)

// ─── Real SDK exception instances ────────────────────────────────────
//
// Constructed rather than mocked: the whole point of `authFailureStatus`
// is that it reads the status off the concrete SDK type, so a stand-in
// would prove nothing about the thing that has actually gone wrong here
// before.

let private awsException (status: HttpStatusCode) =
    Amazon.S3.AmazonS3Exception(
        "The AWS Access Key Id you provided does not exist in our records.",
        Amazon.Runtime.ErrorType.Sender,
        "InvalidAccessKeyId",
        "req-1",
        status
    )
    :> exn

let private azureException (status: int) =
    Azure.RequestFailedException(status, "Server failed to authenticate the request.") :> exn

let private gcsException (status: HttpStatusCode) =
    Google.GoogleApiException("storage", "caller does not have storage.objects.list access")
    |> fun ex ->
        ex.HttpStatusCode <- status
        ex :> exn

/// The shape every one of these SDKs actually delivers to a `with`
/// handler when a single awaited Task faults.
let private wrapped (inner: exn) = AggregateException inner :> exn

[<Tests>]
let tests =
    testList "Phase 2c — BlobStorageAuthFailed credential-rejection trail" [

        // ─── Sanitisation ────────────────────────────────────────────

        testList "sanitiseReason" [
            test "redacts an Azure connection string echoed back by the SDK" {
                let message =
                    "Server failed to authenticate the request. DefaultEndpointsProtocol=https;AccountName=acme;AccountKey=c2VjcmV0LWtleS12YWx1ZQ==;EndpointSuffix=core.windows.net"

                let sanitised = BlobStorageAuthAudit.sanitiseReason message

                Expect.isFalse
                    (sanitised.Contains "c2VjcmV0LWtleS12YWx1ZQ==")
                    "the account key must not reach the audit row"

                Expect.stringContains sanitised "AccountKey=***" "the redaction is visible, not a silent drop"
                Expect.stringContains sanitised "AccountName=acme" "a non-secret pair survives"
            }

            test "redacts every secret-bearing marker, including an access key id" {
                let message =
                    "denied AccessKeyId=AKIAEXAMPLE token=abc123 password=hunter2 sig=deadbeef credential=svc-account"

                let sanitised = BlobStorageAuthAudit.sanitiseReason message

                for leaked in [ "AKIAEXAMPLE"; "abc123"; "hunter2"; "deadbeef"; "svc-account" ] do
                    Expect.isFalse (sanitised.Contains leaked) $"'{leaked}' must be redacted"

                Expect.stringContains sanitised "AccessKeyId=***" "access key id redacted"
                Expect.stringContains sanitised "token=***" "token redacted"
            }

            test "leaves an ordinary diagnostic message readable" {
                let sanitised =
                    BlobStorageAuthAudit.sanitiseReason "caller does not have storage.objects.list access"

                Expect.equal sanitised "caller does not have storage.objects.list access" "no redaction to do"
            }

            test "flattens control characters rather than carrying them into the row" {
                let message = "denied\r\n\tby policy"
                let sanitised = BlobStorageAuthAudit.sanitiseReason message

                Expect.equal sanitised "denied by policy" "newlines and tabs collapse to single spaces"

                Expect.isFalse
                    (sanitised |> Seq.exists Char.IsControl)
                    "no control character survives into a persisted audit payload"
            }

            test "truncates a runaway message" {
                let message = String.replicate 400 "long "
                let sanitised = BlobStorageAuthAudit.sanitiseReason message

                Expect.isTrue
                    (sanitised.Length <= BlobStorageAuthAudit.maxReasonLength + 3)
                    "bounded by maxReasonLength plus the truncation marker"

                Expect.stringEnds sanitised "..." "truncation is marked, not silent"
            }

            test "an empty or whitespace message becomes the empty string" {
                Expect.equal (BlobStorageAuthAudit.sanitiseReason "") "" "empty in, empty out"
                Expect.equal (BlobStorageAuthAudit.sanitiseReason "   ") "" "whitespace in, empty out"
                Expect.equal (BlobStorageAuthAudit.sanitiseReason null) "" "null in, empty out"
            }
        ]

        // ─── Recording ───────────────────────────────────────────────

        testList "record" [
            test "emits exactly one row, under _platform, carrying every queryable axis" {
                let sink = RecordingAuditLog()

                run (BlobStorageAuthAudit.record (Some(sink :> IAuditLog)) "aws-s3" "acme-bucket" "Upload" 403 "denied")

                match authRows sink with
                | [ scope, payload ] ->
                    Expect.equal scope BlobStorageAuthAudit.scopeId "recorded under the platform scope"
                    Expect.equal scope "_platform" "the platform scope is literally _platform"
                    Expect.equal payload.Companion "aws-s3" "companion id"
                    Expect.equal payload.Container "acme-bucket" "bucket / container"
                    Expect.equal payload.Operation "Upload" "the IBlobStorage member"
                    Expect.equal payload.StatusCode 403 "the rejecting status, unmapped"
                    Expect.equal payload.Reason "denied" "the sanitised SDK message"
                | rows -> failtestf "expected exactly one BlobStorageAuthFailed row, got %d" (List.length rows)
            }

            test "sanitises the reason on the way onto the row" {
                let sink = RecordingAuditLog()

                run (
                    BlobStorageAuthAudit.record
                        (Some(sink :> IAuditLog))
                        "azure"
                        "toolup"
                        "List"
                        403
                        "refused AccountKey=super-secret"
                )

                match authRows sink with
                | [ _, payload ] ->
                    Expect.isFalse (payload.Reason.Contains "super-secret") "the credential never reaches the store"
                    Expect.stringContains payload.Reason "AccountKey=***" "redacted in place"
                | rows -> failtestf "expected one row, got %d" (List.length rows)
            }

            test "a deployment that composed no audit log records nothing and does not throw" {
                // The GP 13 half: `None` is the default every existing
                // consumer gets, and it must cost nothing at all.
                run (BlobStorageAuthAudit.record None "gcs" "acme-bucket" "Download" 401 "denied")
            }

            test "a failing sink is swallowed — emission never becomes the caller's error" {
                let sink = ThrowingAuditLog() :> IAuditLog

                // The load-bearing guarantee: the companions call this on
                // the failure path immediately before returning the error
                // they have always returned. If this could throw, a
                // storage failure would silently become a different one.
                run (BlobStorageAuthAudit.record (Some sink) "aws-s3" "acme-bucket" "Delete" 403 "denied")
            }

            test "payload stamps a UTC timestamp" {
                let before = DateTime.UtcNow.AddSeconds -1.0
                let payload = BlobStorageAuthAudit.payload "gcs" "acme-bucket" "List" 401 "denied"

                Expect.equal payload.At.Kind DateTimeKind.Utc "audit timestamps are UTC"
                Expect.isTrue (payload.At >= before) "stamped at emission time"
            }
        ]

        // ─── Per-companion classification ────────────────────────────

        testList "authFailureStatus" [
            testList "AwsS3Storage" [
                test "401 and 403 classify; 404 does not" {
                    Expect.equal
                        (ToolUp.Storage.AwsS3Storage.authFailureStatus (awsException HttpStatusCode.Unauthorized))
                        (Some 401)
                        "401 is a credential fact"

                    Expect.equal
                        (ToolUp.Storage.AwsS3Storage.authFailureStatus (awsException HttpStatusCode.Forbidden))
                        (Some 403)
                        "403 is a credential fact"

                    Expect.equal
                        (ToolUp.Storage.AwsS3Storage.authFailureStatus (awsException HttpStatusCode.NotFound))
                        None
                        "404 is an absence, not a credential fact"
                }

                test "classifies THROUGH an AggregateException wrapper" {
                    // The shape an awaited Task actually delivers. A
                    // direct type test on the wrapper never fires, which
                    // is how this file's 416 arm sat dead until an armed
                    // run measured it.
                    Expect.equal
                        (ToolUp.Storage.AwsS3Storage.authFailureStatus (wrapped (awsException HttpStatusCode.Forbidden)))
                        (Some 403)
                        "the wrapped 403 must classify identically to the bare one"
                }

                test "an unrelated exception classifies as not-a-credential-fact" {
                    Expect.equal
                        (ToolUp.Storage.AwsS3Storage.authFailureStatus (InvalidOperationException "boom"))
                        None
                        "a non-SDK exception is never an auth failure"
                }
            ]

            testList "AzureBlobStorage" [
                test "401 and 403 classify; 404 and 412 do not" {
                    Expect.equal
                        (ToolUp.Storage.AzureBlobStorage.authFailureStatus (azureException 401))
                        (Some 401)
                        "401 is a credential fact"

                    Expect.equal
                        (ToolUp.Storage.AzureBlobStorage.authFailureStatus (azureException 403))
                        (Some 403)
                        "403 is a credential fact"

                    Expect.equal
                        (ToolUp.Storage.AzureBlobStorage.authFailureStatus (azureException 404))
                        None
                        "404 is an absence"

                    Expect.equal
                        (ToolUp.Storage.AzureBlobStorage.authFailureStatus (azureException 412))
                        None
                        "412 is a refused precondition, and the credential authenticated to get it"
                }

                test "classifies THROUGH an AggregateException wrapper" {
                    Expect.equal
                        (ToolUp.Storage.AzureBlobStorage.authFailureStatus (wrapped (azureException 403)))
                        (Some 403)
                        "the wrapped 403 must classify identically to the bare one"
                }

                test "an unrelated exception classifies as not-a-credential-fact" {
                    Expect.equal
                        (ToolUp.Storage.AzureBlobStorage.authFailureStatus (InvalidOperationException "boom"))
                        None
                        "a non-SDK exception is never an auth failure"
                }
            ]

            testList "GoogleCloudStorage" [
                test "401 and 403 classify; 404 does not" {
                    Expect.equal
                        (ToolUp.Storage.GoogleCloudStorage.authFailureStatus (gcsException HttpStatusCode.Unauthorized))
                        (Some 401)
                        "401 is a credential fact"

                    Expect.equal
                        (ToolUp.Storage.GoogleCloudStorage.authFailureStatus (gcsException HttpStatusCode.Forbidden))
                        (Some 403)
                        "403 is a credential fact"

                    Expect.equal
                        (ToolUp.Storage.GoogleCloudStorage.authFailureStatus (gcsException HttpStatusCode.NotFound))
                        None
                        "404 is an absence"
                }

                test "classifies THROUGH an AggregateException wrapper" {
                    Expect.equal
                        (ToolUp.Storage.GoogleCloudStorage.authFailureStatus (
                            wrapped (gcsException HttpStatusCode.Forbidden)
                        ))
                        (Some 403)
                        "the wrapped 403 must classify identically to the bare one"
                }

                test "an unrelated exception classifies as not-a-credential-fact" {
                    Expect.equal
                        (ToolUp.Storage.GoogleCloudStorage.authFailureStatus (InvalidOperationException "boom"))
                        None
                        "a non-SDK exception is never an auth failure"
                }
            ]

            test "every companion records under the id its health probe uses" {
                // The join key. If either side is renamed without the
                // other, the probe's `Unhealthy` reading and the audit
                // rows stop meeting, and nothing else would fail.
                Expect.equal ToolUp.Storage.AwsS3Storage.companionId "aws-s3" "blob_storage:aws-s3"
                Expect.equal ToolUp.Storage.AzureBlobStorage.companionId "azure" "blob_storage:azure"
                Expect.equal ToolUp.Storage.GoogleCloudStorage.companionId "gcs" "blob_storage:gcs"
            }
        ]

        // ─── Wire ────────────────────────────────────────────────────

        test "wire format — BlobStorageAuthFailed round-trips with structural equality" {
            // The Phase 114 registry pack already asserts coverage
            // reflectively over every case. This pins the new case's
            // actual field values through the codec, the way the other
            // "wire format stable" cases are pinned.
            let original =
                BlobStorageAuthFailed {
                    Companion = "azure"
                    Container = "toolup"
                    Operation = "List"
                    StatusCode = 403
                    Reason = "Server failed to authenticate the request."
                    At = DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc)
                }

            let json = AuditLog.serialiseAuditEvent original

            Expect.equal
                (AuditLog.tryDecodeAuditEvent "BlobStorageAuthFailed" json)
                (Ok original)
                "credential-rejection row round-trip"
        }
    ]