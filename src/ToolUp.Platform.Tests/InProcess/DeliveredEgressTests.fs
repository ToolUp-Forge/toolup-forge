// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DeliveredEgressTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Usage
open ToolUp.MediaLibrary
open ToolUp.MediaLibrary.DeliveredEgress
open ToolUp.Hosts.DeliveredEgress
open ToolUp.Hosts.DeliveredEgress.FieldMappedParser

// ─── Phase 742 — delivered-egress reconciliation ──────────────────────
//
// Five layers, in the order the phase builds them:
//
//   1. path resolution — the pure inverse of `MediaEdgePaths`, including
//      what it deliberately REFUSES;
//   2. the dedup key, which is the whole of the idempotency guarantee;
//   3. ingestion — attribution, scope isolation, the counted drops, and
//      re-ingestion changing no number;
//   4. the rollup fold — delivered beside origin, never instead of it;
//   5. the `ToolUp.Hosts.DeliveredEgress` field-mapped parsers, proving
//      the record shape from OUTSIDE the SDK (GP 12).
//
// Nothing here reaches a network, and no test constructs a vendor's log
// format from memory: the parser cases are driven by field maps declared
// in the test itself, which is exactly the surface a deployment writes.

// ─── Doubles ──────────────────────────────────────────────────────────

type private RecordingUsageLog() =
    let rows = ResizeArray<UsageRecord>()
    let gate = obj ()

    member _.Rows = lock gate (fun () -> List.ofSeq rows)

    /// The ledger's own dedup, reproduced: the blob-backed
    /// implementation merges incoming rows into the existing
    /// per-(scope, day) rollup by `RecordId`. Modelling that here is
    /// what makes "re-ingestion changes no number" a claim about the
    /// COMPOSED system rather than about a bag that happens to grow.
    member _.Merged = lock gate (fun () -> rows |> Seq.distinctBy _.RecordId |> List.ofSeq)

    interface IUsageLog with
        member _.Record record = async { lock gate (fun () -> rows.Add record) }

        member _.Query(_, _, _) = async.Return []
        member _.Aggregate(_, _) = async.Return Map.empty

type private RecordingMetricsSink() =
    let observations = ResizeArray<string * float * Map<string, string>>()
    let increments = ResizeArray<string * Map<string, string>>()
    let gate = obj ()

    member _.Observations = lock gate (fun () -> List.ofSeq observations)
    member _.Increments = lock gate (fun () -> List.ofSeq increments)

    interface IMetricsSink with
        member _.Record(name, value, tags) =
            lock gate (fun () -> observations.Add((name, value, tags)))

        member _.Increment(name, tags) =
            lock gate (fun () -> increments.Add((name, tags)))

        member _.SetGauge(_, _, _) = ()

/// A secret store holding one fixed signing key, so `MediaUrlSigner`
/// mints and verifies against a known value without a blob store.
type private StubSecretStore(value: string) =
    interface ToolUp.Platform.Secrets.ISecretStore with
        member _.GetSecret(_, _) = async { return Some value }
        member _.SetSecret(_, _, _) = async { return Ok() }
        member _.DeleteSecret(_, _) = async { return Ok() }
        member _.ListKeys(_) = async { return [] }

/// A 32-byte key in the base64url form `resolveSigningKey` expects.
let private signingKeyMaterial =
    Convert.ToBase64String(Array.init 32 byte).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private newSigner () =
    SignedUrl.MediaUrlSigner(StubSecretStore signingKeyMaterial)

let private scope (id: string) : StorageScope = {
    ScopeId = id
    Container = "team-" + id
    Persist = true
}

let private record (url: string) (bytes: int64) (outcome: DeliveredCacheOutcome) (requestId: string option) = {
    Url = url
    Bytes = bytes
    At = DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero)
    Status = 200
    Outcome = outcome
    RequestId = requestId
}

// ─── 1. Path resolution ───────────────────────────────────────────────

let private pathTests =
    testList "DeliveredPath.parse" [
        test "a derived path yields the media id and its relative path" {
            match DeliveredPath.parse "/api/media/hls/abc123/v0/seg-0001.ts" with
            | Some parsed ->
                Expect.equal (MediaId.value parsed.Media) "abc123" "media id"
                Expect.equal parsed.Class (DeliveredDerived "v0/seg-0001.ts") "relative path"
                Expect.isNone parsed.SignedToken "a derived path carries no token"
            | None -> failtest "expected the derived path to resolve"
        }

        test "the stream route yields the original with no token" {
            match DeliveredPath.parse "/api/media/stream/abc123" with
            | Some parsed ->
                Expect.equal (MediaId.value parsed.Media) "abc123" "media id"
                Expect.equal parsed.Class DeliveredOriginal "class"
                Expect.isNone parsed.SignedToken "the ambient route carries no token"
            | None -> failtest "expected the stream path to resolve"
        }

        test "the signed route surfaces its token, URL-decoded" {
            match DeliveredPath.parse "/media/signed/abc123?token=a.b%2Bc.d&other=1" with
            | Some parsed ->
                Expect.equal (MediaId.value parsed.Media) "abc123" "media id"
                Expect.equal parsed.SignedToken (Some "a.b+c.d") "token, decoded"
            | None -> failtest "expected the signed path to resolve"
        }

        // A delegated signer emits ABSOLUTE URLs — that is the point of
        // the 472 seam — so an absolute form is the expected input here,
        // not an edge case.
        test "an absolute URL resolves exactly as its path does" {
            let absolute = DeliveredPath.parse "https://cdn.example.com/api/media/stream/abc123"
            let relative = DeliveredPath.parse "/api/media/stream/abc123"
            Expect.equal absolute relative "absolute and relative agree"
        }

        test "a fragment is stripped before the query is read" {
            match DeliveredPath.parse "/media/signed/abc123?token=xyz#t=30" with
            | Some parsed -> Expect.equal parsed.SignedToken (Some "xyz") "token unaffected by the fragment"
            | None -> failtest "expected the signed path to resolve"
        }

        // The serving path refuses traversal (`OpenDerived` answers
        // `NotFound`), so a traversal attempt in a log is a probe that
        // was ALREADY refused. Letting it resolve here would turn a
        // refused probe into an attribution.
        test "a traversal attempt in a derived path is refused" {
            Expect.isNone (DeliveredPath.parse "/api/media/hls/abc123/../../secrets") "traversal refused"
        }

        test "paths this SDK does not mint are refused rather than guessed" {
            for path in
                [
                    "/api/media/hls/abc123"
                    "/api/media/stream/"
                    "/api/media/stream/a/b"
                    "/some/other/path"
                    "/api/media/beacon"
                    ""
                ] do
                Expect.isNone (DeliveredPath.parse path) (sprintf "refused: %s" path)
        }

        // A delivered row and an origin row for the same file must never
        // disagree about its class, so the class comes from Phase 473's
        // own function rather than a second extension test here.
        test "the response class agrees with the origin-side classifier" {
            let cases = [
                "v0/index.m3u8", PlaybackTelemetry.ClassManifest
                "v0/seg-1.ts", PlaybackTelemetry.ClassSegment
                "v0/seg-1.m4s", PlaybackTelemetry.ClassSegment
                "poster.jpg", PlaybackTelemetry.ClassPoster
            ]

            for relativePath, expected in cases do
                Expect.equal
                    (DeliveredPath.responseClass (DeliveredDerived relativePath))
                    expected
                    (sprintf "class of %s" relativePath)

            Expect.equal
                (DeliveredPath.responseClass DeliveredOriginal)
                PlaybackTelemetry.ClassOriginal
                "class of the original"
        }
    ]

// ─── 2. The dedup key ─────────────────────────────────────────────────

let private dedupTests =
    testList "DeliveredEgress.dedupKey" [
        test "the same record yields the same key" {
            let r = record "/api/media/stream/abc" 100L ServedFromEdge (Some "req-1")
            Expect.equal (dedupKey r) (dedupKey r) "stable"
        }

        // Keying on the batch would double-count the same response
        // re-listed under a different object name, which is precisely
        // what redelivery produces.
        test "the key is independent of which batch carried the record" {
            let r = record "/api/media/stream/abc" 100L ServedFromEdge (Some "req-1")
            let batchA = { BatchId = "file-a"; Records = [ r ] }
            let batchB = { BatchId = "file-b"; Records = [ r ] }

            Expect.equal
                (dedupKey (List.head batchA.Records))
                (dedupKey (List.head batchB.Records))
                "batch id is not an input"
        }

        test "a supplied request id alone identifies the record" {
            let a = record "/api/media/stream/abc" 100L ServedFromEdge (Some "req-1")

            let b = {
                a with
                    Url = "/api/media/stream/zzz"
                    Bytes = 999L
            }

            Expect.equal (dedupKey a) (dedupKey b) "the request id is the whole key when present"
        }

        test "without a request id the key follows the record's content" {
            let a = record "/api/media/stream/abc" 100L ServedFromEdge None
            let b = { a with Bytes = 101L }
            Expect.notEqual (dedupKey a) (dedupKey b) "different bytes, different key"
        }

        // Length-prefixed hashing: "ab"+"c" must not collide with
        // "a"+"bc". Asserted rather than asserted-in-a-comment, because
        // a collision here silently DISCARDS a delivered response.
        test "adjacent fields cannot be confused for one another" {
            let a = record "/api/media/stream/ab" 1L ServedFromEdge None
            let b = record "/api/media/stream/a" 1L ServedFromEdge None
            Expect.notEqual (dedupKey a) (dedupKey b) "no field-boundary collision"
        }
    ]

// ─── 3. Ingestion ─────────────────────────────────────────────────────

let private ingest (ingestor: DeliveredEgressIngestor) (batchId: string) (records: DeliveredRecord list) =
    ingestor.IngestBatch { BatchId = batchId; Records = records }
    |> Async.RunSynchronously
    |> function
        | Ok outcome -> outcome
        | Error e -> failtestf "ingest failed: %s" e

let private ingestionTests =
    testList "DeliveredEgressIngestor" [
        test "a resolvable record lands as a delivered row with its outcome" {
            let usage = RecordingUsageLog()

            let resolver =
                CallbackLogSource.CallbackScopeResolver("t", Map.ofList [ "abc", scope "team-1" ])

            let ingestor =
                DeliveredEgressIngestor(usage, None, None, Some(resolver :> IDeliveredScopeResolver))

            let outcome =
                ingest ingestor "batch-1" [ record "/api/media/hls/abc/v0/seg-1.ts" 4096L ServedFromEdge (Some "r1") ]

            Expect.equal outcome.Attributed 1 "attributed"
            Expect.equal outcome.AttributedBytes 4096L "delivered bytes"
            Expect.equal outcome.EdgeServedBytes 4096L "edge-served bytes"

            let row = usage.Rows |> List.exactlyOne
            Expect.equal row.ResourceKind PlaybackTelemetry.DeliveredEgressKind "a DELIVERED row, not an origin one"
            Expect.equal row.ScopeId "team-1" "scope"
            Expect.equal (row.Metadata.TryFind PlaybackTelemetry.MediaIdKey) (Some "abc") "media id"
            Expect.equal (row.Metadata.TryFind PlaybackTelemetry.ClassKey) (Some PlaybackTelemetry.ClassSegment) "class"

            Expect.equal
                (row.Metadata.TryFind PlaybackTelemetry.OutcomeKey)
                (Some PlaybackTelemetry.OutcomeEdge)
                "outcome"
        }

        // The heart of the phase: a batch an edge redelivers must not
        // double the bill.
        test "re-ingesting a batch changes no number" {
            let usage = RecordingUsageLog()

            let resolver =
                CallbackLogSource.CallbackScopeResolver("t", Map.ofList [ "abc", scope "team-1" ])

            let ingestor =
                DeliveredEgressIngestor(usage, None, None, Some(resolver :> IDeliveredScopeResolver))

            let records = [
                record "/api/media/hls/abc/v0/seg-1.ts" 4096L ServedFromEdge (Some "r1")
                record "/api/media/hls/abc/v0/seg-2.ts" 8192L ServedFromEdge (Some "r2")
            ]

            ingest ingestor "batch-1" records |> ignore
            ingest ingestor "batch-1" records |> ignore
            // ...and again under a DIFFERENT batch name, which is what a
            // re-listed object looks like.
            ingest ingestor "batch-1-copy" records |> ignore

            Expect.equal (List.length usage.Merged) 2 "two distinct rows survive the merge"
            Expect.equal (usage.Merged |> List.sumBy (fun r -> int64 r.Quantity)) 12288L "bytes are not multiplied"
        }

        // GOES RED IF THE CONTROL IS REMOVED: without the assertion that
        // two genuinely different responses stay distinct, a dedup key
        // that collapsed everything would pass the test above.
        test "distinct responses stay distinct" {
            let usage = RecordingUsageLog()

            let resolver =
                CallbackLogSource.CallbackScopeResolver("t", Map.ofList [ "abc", scope "team-1" ])

            let ingestor =
                DeliveredEgressIngestor(usage, None, None, Some(resolver :> IDeliveredScopeResolver))

            ingest ingestor "batch-1" [
                record "/api/media/hls/abc/v0/seg-1.ts" 4096L ServedFromEdge (Some "r1")
                record "/api/media/hls/abc/v0/seg-2.ts" 4096L ServedFromEdge (Some "r2")
            ]
            |> ignore

            Expect.equal (List.length usage.Merged) 2 "two rows, not one"
        }

        test "unattributable records are DROPPED and COUNTED, never raised" {
            let usage = RecordingUsageLog()
            let metrics = RecordingMetricsSink()

            let ingestor =
                DeliveredEgressIngestor(usage, Some(metrics :> IMetricsSink), None, None)

            let outcome =
                ingest ingestor "batch-1" [
                    record "/not/a/media/path" 100L ServedFromEdge (Some "r1")
                    record "/api/media/stream/abc" 100L ServedFromEdge (Some "r2") // no scope resolver
                    {
                        record "/api/media/stream/abc" 100L ServedFromEdge (Some "r3") with
                            Status = 404
                    }
                    {
                        record "/api/media/stream/abc" 0L ServedFromEdge (Some "r4") with
                            Bytes = 0L
                    }
                ]

            Expect.equal outcome.Attributed 0 "nothing attributed"
            Expect.isEmpty usage.Rows "no ledger rows"

            let dropped = Map.ofList outcome.Dropped
            Expect.equal (dropped.TryFind UnrecognisedPath) (Some 1) "unrecognised path"
            Expect.equal (dropped.TryFind ScopeUnresolved) (Some 1) "scope unresolved"
            Expect.equal (dropped.TryFind NonSuccessStatus) (Some 1) "non-success status"
            Expect.equal (dropped.TryFind NonPositiveBytes) (Some 1) "non-positive bytes"
            Expect.equal (DeliveredIngestOutcome.droppedTotal outcome) 4 "total"

            let dropReasons =
                metrics.Increments
                |> List.filter (fun (name, _) -> name = PlaybackTelemetry.DeliveredDroppedMetric)
                |> List.choose (fun (_, tags) -> tags.TryFind "reason")
                |> List.sort

            Expect.equal
                dropReasons
                [
                    "non-positive-bytes"
                    "non-success-status"
                    "scope-unresolved"
                    "unrecognised-path"
                ]
                "every drop is counted on the metric too"
        }

        // Scope isolation on attribution (GP 4). Anything a viewer can
        // put in a query string reaches a log line, so an UNVERIFIED
        // token payload would let anyone attribute bytes to any scope.
        test "a signed token attributes to the scope it was minted for" {
            let signer = newSigner ()
            let usage = RecordingUsageLog()
            let ingestor = DeliveredEgressIngestor(usage, None, Some signer, None)

            let token =
                signer.SignAsync(MediaId "abc", scope "team-1", TimeSpan.FromMinutes 5.0, DateTimeOffset.UtcNow)
                |> Async.RunSynchronously
                |> function
                    | Ok t -> t
                    | Error e -> failtestf "mint failed: %A" e

            let url = sprintf "/media/signed/abc?token=%s" (Uri.EscapeDataString token)

            let outcome =
                ingest ingestor "batch-1" [ record url 1024L ServedFromEdge (Some "r1") ]

            Expect.equal outcome.Attributed 1 "attributed with no scope resolver composed"
            Expect.equal (usage.Rows |> List.exactlyOne).ScopeId "team-1" "the minting scope"
        }

        test "a FORGED token attributes nothing" {
            let signer = newSigner ()
            let usage = RecordingUsageLog()
            let ingestor = DeliveredEgressIngestor(usage, None, Some signer, None)

            // A well-formed token shape whose signature is nonsense —
            // exactly what a prober can put in a query string.
            let forged =
                "abc.eyJNZWRpYUlkIjoiYWJjIiwiU2NvcGVJZCI6InZpY3RpbSJ9.bm90LWEtc2lnbmF0dXJl"

            let url = sprintf "/media/signed/abc?token=%s" (Uri.EscapeDataString forged)

            let outcome =
                ingest ingestor "batch-1" [ record url 1024L ServedFromEdge (Some "r1") ]

            Expect.equal outcome.Attributed 0 "a forged signature attributes nothing"
            Expect.equal (Map.ofList outcome.Dropped |> Map.tryFind ScopeUnresolved) (Some 1) "counted as unresolved"
            Expect.isEmpty usage.Rows "and writes no row"
        }

        // The log arrives HOURS after the response — one surveyed edge
        // typically delivers within an hour and can lag a day — so a
        // validly-signed token is expired far more often than not.
        // Refusing it would discard the normal case.
        test "an EXPIRED but validly-signed token still attributes" {
            let signer = newSigner ()
            let usage = RecordingUsageLog()
            let ingestor = DeliveredEgressIngestor(usage, None, Some signer, None)

            let token =
                signer.SignAsync(
                    MediaId "abc",
                    scope "team-1",
                    TimeSpan.FromMinutes 5.0,
                    DateTimeOffset.UtcNow.AddDays -2.0
                )
                |> Async.RunSynchronously
                |> function
                    | Ok t -> t
                    | Error e -> failtestf "mint failed: %A" e

            // Control: the SERVING path still refuses it.
            match signer.VerifyAsync(token, DateTimeOffset.UtcNow) |> Async.RunSynchronously with
            | Error SignedUrlError.Expired -> ()
            | other -> failtestf "expected the serving path to refuse an expired token, got %A" other

            let url = sprintf "/media/signed/abc?token=%s" (Uri.EscapeDataString token)

            let outcome =
                ingest ingestor "batch-1" [ record url 1024L ServedFromEdge (Some "r1") ]

            Expect.equal outcome.Attributed 1 "attribution survives expiry"
            Expect.equal (usage.Rows |> List.exactlyOne).ScopeId "team-1" "still the minting scope"
        }

        // A token minted for one item must not report bytes against
        // another — the same property Phase 473 pins on the beacon.
        test "a token minted for another item does not attribute this one" {
            let signer = newSigner ()
            let usage = RecordingUsageLog()
            let ingestor = DeliveredEgressIngestor(usage, None, Some signer, None)

            let token =
                signer.SignAsync(MediaId "other", scope "team-1", TimeSpan.FromMinutes 5.0, DateTimeOffset.UtcNow)
                |> Async.RunSynchronously
                |> function
                    | Ok t -> t
                    | Error e -> failtestf "mint failed: %A" e

            let url = sprintf "/media/signed/abc?token=%s" (Uri.EscapeDataString token)

            let outcome =
                ingest ingestor "batch-1" [ record url 1024L ServedFromEdge (Some "r1") ]

            Expect.equal outcome.Attributed 0 "a mismatched token attributes nothing"
        }

        test "an origin-served record is attributed but not counted as an edge hit" {
            let usage = RecordingUsageLog()

            let resolver =
                CallbackLogSource.CallbackScopeResolver("t", Map.ofList [ "abc", scope "team-1" ])

            let ingestor =
                DeliveredEgressIngestor(usage, None, None, Some(resolver :> IDeliveredScopeResolver))

            let outcome =
                ingest ingestor "b" [
                    record "/api/media/hls/abc/v0/a.ts" 100L ServedFromEdge (Some "r1")
                    record "/api/media/hls/abc/v0/b.ts" 400L ServedFromOrigin (Some "r2")
                    record "/api/media/hls/abc/v0/c.ts" 500L DeliveredOutcomeUnknown (Some "r3")
                ]

            Expect.equal outcome.AttributedBytes 1000L "every delivered byte counts as delivered"
            Expect.equal outcome.EdgeServedBytes 100L "only the edge-served subset counts as a hit"
        }

        test "the resolver is consulted only when the URL carried no usable token" {
            let signer = newSigner ()
            let usage = RecordingUsageLog()
            let mutable consulted = 0

            let resolver =
                CallbackLogSource.CallbackScopeResolver(
                    "t",
                    fun _ ->
                        consulted <- consulted + 1
                        async.Return(Some(scope "fallback"))
                )

            let ingestor =
                DeliveredEgressIngestor(usage, None, Some signer, Some(resolver :> IDeliveredScopeResolver))

            let token =
                signer.SignAsync(MediaId "abc", scope "team-1", TimeSpan.FromMinutes 5.0, DateTimeOffset.UtcNow)
                |> Async.RunSynchronously
                |> function
                    | Ok t -> t
                    | Error e -> failtestf "mint failed: %A" e

            ingest ingestor "b" [
                record (sprintf "/media/signed/abc?token=%s" (Uri.EscapeDataString token)) 1L ServedFromEdge (Some "r1")
            ]
            |> ignore

            Expect.equal consulted 0 "a verified token short-circuits the resolver"

            ingest ingestor "b2" [ record "/api/media/stream/abc" 1L ServedFromEdge (Some "r2") ]
            |> ignore

            Expect.equal consulted 1 "an ambient route falls through to it"
        }

        test "a throwing resolver costs one record, never the batch" {
            let usage = RecordingUsageLog()

            let resolver =
                CallbackLogSource.CallbackScopeResolver(
                    "t",
                    fun id ->
                        if MediaId.value id = "boom" then
                            failwith "lookup exploded"
                        else
                            async.Return(Some(scope "team-1"))
                )

            let ingestor =
                DeliveredEgressIngestor(usage, None, None, Some(resolver :> IDeliveredScopeResolver))

            let outcome =
                ingest ingestor "b" [
                    record "/api/media/stream/boom" 100L ServedFromEdge (Some "r1")
                    record "/api/media/stream/fine" 200L ServedFromEdge (Some "r2")
                ]

            Expect.equal outcome.Attributed 1 "the other record still lands"
            Expect.equal (Map.ofList outcome.Dropped |> Map.tryFind ScopeUnresolved) (Some 1) "the failure is counted"
        }

        // GP 13. A deployment that composes no metrics sink pays nothing
        // and, more importantly, still works.
        test "ingestion works with no metrics sink composed" {
            let usage = RecordingUsageLog()

            let resolver =
                CallbackLogSource.CallbackScopeResolver("t", Map.ofList [ "abc", scope "team-1" ])

            let ingestor =
                DeliveredEgressIngestor(usage, None, None, Some(resolver :> IDeliveredScopeResolver))

            let outcome =
                ingest ingestor "b" [ record "/api/media/stream/abc" 10L ServedFromEdge (Some "r") ]

            Expect.equal outcome.Attributed 1 "attributed"
        }

        // The acceptance criterion: a deployment that ingests nothing is
        // byte-identical to Phase 473.
        test "a deployment that ingests nothing writes nothing" {
            let usage = RecordingUsageLog()
            let ingestor = DeliveredEgressIngestor(usage, None, None, None)
            let outcome = ingest ingestor "empty" []

            Expect.equal outcome.Attributed 0 "nothing attributed"
            Expect.isEmpty outcome.Dropped "nothing dropped"
            Expect.isEmpty usage.Rows "no rows at all"
        }
    ]

// ─── 4. The rollup fold ───────────────────────────────────────────────

let private rollupTests =
    testList "PlaybackRollup — delivered beside origin" [
        test "delivered and origin are distinct series in one bucket" {
            let at = DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc)

            let attribution: PlaybackTelemetry.EgressAttribution = {
                Media = MediaId "abc"
                ScopeId = "team-1"
                Class = PlaybackTelemetry.ClassSegment
            }

            let rows = [
                PlaybackTelemetry.egressRecord attribution 1000L (Guid.NewGuid()) at
                PlaybackTelemetry.deliveredRecord attribution PlaybackTelemetry.OutcomeEdge 8000L (Guid.NewGuid()) at
                PlaybackTelemetry.deliveredRecord attribution PlaybackTelemetry.OutcomeOrigin 1100L (Guid.NewGuid()) at
                PlaybackTelemetry.deliveredRecord attribution PlaybackTelemetry.OutcomeUnknown 900L (Guid.NewGuid()) at
            ]

            let rollup = PlaybackTelemetry.PlaybackRollup.ofUsageRecords rows |> List.exactlyOne

            // Phase 473's number is untouched: this phase adds a series,
            // it does not correct one.
            Expect.equal rollup.OriginEgressBytes 1000L "origin egress unchanged"
            Expect.equal rollup.DeliveredEgressBytes 10000L "every delivered byte"
            Expect.equal rollup.EdgeServedBytes 8000L "only edge-served bytes"
            Expect.equal rollup.EdgeHitRateByBytes 0.8 "hit rate by bytes"
        }

        // An unknown disposition must not read as a miss: that would
        // silently deflate the hit rate of any deployment whose
        // vocabulary the field map did not enumerate.
        test "an unknown outcome counts as delivered but not as a hit" {
            let at = DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc)

            let attribution: PlaybackTelemetry.EgressAttribution = {
                Media = MediaId "abc"
                ScopeId = "team-1"
                Class = PlaybackTelemetry.ClassSegment
            }

            let rollup =
                [
                    PlaybackTelemetry.deliveredRecord
                        attribution
                        PlaybackTelemetry.OutcomeUnknown
                        500L
                        (Guid.NewGuid())
                        at
                ]
                |> PlaybackTelemetry.PlaybackRollup.ofUsageRecords
                |> List.exactlyOne

            Expect.equal rollup.DeliveredEgressBytes 500L "delivered"
            Expect.equal rollup.EdgeServedBytes 0L "not a hit"
            Expect.equal rollup.EdgeHitRateByBytes 0.0 "hit rate"
        }

        test "a Phase 473 ledger with no delivered rows reads exactly as before" {
            let at = DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc)

            let attribution: PlaybackTelemetry.EgressAttribution = {
                Media = MediaId "abc"
                ScopeId = "team-1"
                Class = PlaybackTelemetry.ClassOriginal
            }

            let rollup =
                [ PlaybackTelemetry.egressRecord attribution 4242L (Guid.NewGuid()) at ]
                |> PlaybackTelemetry.PlaybackRollup.ofUsageRecords
                |> List.exactlyOne

            Expect.equal rollup.OriginEgressBytes 4242L "origin"
            Expect.equal rollup.DeliveredEgressBytes 0L "no delivered bytes"
            Expect.equal rollup.EdgeServedBytes 0L "no edge bytes"
            Expect.equal rollup.EdgeHitRateByBytes 0.0 "no hit rate to report"
        }

        // Scope isolation survives the fold: two scopes' delivered bytes
        // never merge into one bucket.
        test "delivered bytes stay in their own scope's bucket" {
            let at = DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc)

            let attributionFor scopeId : PlaybackTelemetry.EgressAttribution = {
                Media = MediaId "abc"
                ScopeId = scopeId
                Class = PlaybackTelemetry.ClassSegment
            }

            let rollups =
                [
                    PlaybackTelemetry.deliveredRecord
                        (attributionFor "team-1")
                        PlaybackTelemetry.OutcomeEdge
                        100L
                        (Guid.NewGuid())
                        at
                    PlaybackTelemetry.deliveredRecord
                        (attributionFor "team-2")
                        PlaybackTelemetry.OutcomeEdge
                        900L
                        (Guid.NewGuid())
                        at
                ]
                |> PlaybackTelemetry.PlaybackRollup.ofUsageRecords

            Expect.equal (List.length rollups) 2 "two buckets"

            for rollup in rollups do
                let expected = if rollup.ScopeId = "team-1" then 100L else 900L
                Expect.equal rollup.DeliveredEgressBytes expected (sprintf "%s bytes" rollup.ScopeId)
        }
    ]

// ─── 5. The sub-companion (GP 12 — proving the shape from outside) ────

/// A field map in the shape a delimited delivery that splits date and
/// time, and logs the query separately, would need.
let private delimitedMap = {
    FieldMap.required "uri-stem" "bytes-sent" "status" "date" with
        TimestampSecondField = Some "time"
        QueryField = Some "uri-query"
        OutcomeField = Some "result-type"
        RequestIdField = Some "request-id"
        TimestampFormats = [ "yyyy-MM-dd HH:mm:ss" ]
        EdgeOutcomes = Set.ofList [ "Hit"; "RefreshHit" ]
        OriginOutcomes = Set.ofList [ "Miss" ]
}

let private parserTests =
    testList "FieldMappedParser (ToolUp.Hosts.DeliveredEgress)" [
        test "a delimited log with a #Fields: header parses by field NAME, not position" {
            let content =
                String.concat "\n" [
                    "#Version: 1.0"
                    "#Fields: date time uri-stem status bytes-sent uri-query result-type request-id"
                    "2026-08-28\t10:00:00\t/api/media/hls/abc/v0/s1.ts\t200\t4096\t-\tHit\treq-1"
                    "2026-08-28\t10:00:01\t/media/signed/abc\t200\t8192\ttoken=xyz\tMiss\treq-2"
                ]

            let output = parseDelimited delimitedMap '\t' None content

            Expect.isEmpty output.Errors "no parse errors"
            Expect.equal (List.length output.Records) 2 "two records"

            let first = output.Records[0]
            Expect.equal first.Url "/api/media/hls/abc/v0/s1.ts" "path, with an absent query left off"
            Expect.equal first.Bytes 4096L "bytes"
            Expect.equal first.Status 200 "status"
            Expect.equal first.Outcome ServedFromEdge "Hit maps to edge"
            Expect.equal first.RequestId (Some "req-1") "request id"
            Expect.equal (first.At.ToString "yyyy-MM-dd HH:mm:ss") "2026-08-28 10:00:00" "timestamp, joined and UTC"

            let second = output.Records[1]
            Expect.equal second.Url "/media/signed/abc?token=xyz" "query joined onto the path"
            Expect.equal second.Outcome ServedFromOrigin "Miss maps to origin"
        }

        // Every edge surveyed logs UTC. Reading a zone-less timestamp as
        // machine-local would shift whole days of attribution on any
        // host that is not on UTC — and would be invisible to a
        // developer whose machine is.
        test "a zone-less timestamp is read as UTC, not as machine-local" {
            let content =
                "#Fields: date time uri-stem status bytes-sent uri-query result-type request-id\n"
                + "2026-08-28\t23:30:00\t/api/media/stream/abc\t200\t1\t-\tHit\tr"

            let parsed =
                (parseDelimited delimitedMap '\t' None content).Records |> List.exactlyOne

            Expect.equal parsed.At.Offset TimeSpan.Zero "offset is UTC"
            Expect.equal (parsed.At.ToString "yyyy-MM-dd HH:mm:ss") "2026-08-28 23:30:00" "no local-time shift"
        }

        // An unenumerated vocabulary must degrade to "I do not know",
        // never to a silent miss.
        test "an outcome value in neither set reads as unknown" {
            let content =
                "#Fields: date time uri-stem status bytes-sent uri-query result-type request-id\n"
                + "2026-08-28\t10:00:00\t/api/media/stream/abc\t200\t1\t-\tLimitExceeded\tr"

            let parsed =
                (parseDelimited delimitedMap '\t' None content).Records |> List.exactlyOne

            Expect.equal parsed.Outcome DeliveredOutcomeUnknown "unenumerated is unknown"
        }

        test "a malformed line is reported without abandoning the file" {
            let content =
                String.concat "\n" [
                    "#Fields: date time uri-stem status bytes-sent uri-query result-type request-id"
                    "2026-08-28\t10:00:00\t/api/media/stream/abc\t200\t4096\t-\tHit\treq-1"
                    "not\tenough\tfields"
                    "2026-08-28\t10:00:02\t/api/media/stream/def\t200\t100\t-\tHit\treq-3"
                ]

            let output = parseDelimited delimitedMap '\t' None content

            Expect.equal (List.length output.Records) 2 "the good lines survive"
            Expect.equal (List.length output.Errors) 1 "the bad line is reported"
            Expect.equal output.Errors.Head.Line 3 "with its line number"
        }

        test "a caller-supplied column list overrides an absent header" {
            let content =
                "2026-08-28\t10:00:00\t/api/media/stream/abc\t200\t4096\t-\tHit\treq-1"

            let columns = [
                "date"
                "time"
                "uri-stem"
                "status"
                "bytes-sent"
                "uri-query"
                "result-type"
                "request-id"
            ]

            let output = parseDelimited delimitedMap '\t' (Some columns) content
            Expect.isEmpty output.Errors "no errors"
            Expect.equal (output.Records |> List.exactlyOne).Bytes 4096L "parsed by the supplied columns"
        }

        test "headerless input with no supplied columns is an error, not a guess" {
            let output = parseDelimited delimitedMap '\t' None "a\tb\tc"
            Expect.isEmpty output.Records "nothing parsed"
            Expect.equal (List.length output.Errors) 1 "reported"
        }

        // The same logical field arrives typed on one delivery and
        // stringly on another; refusing either would be refusing a
        // configuration choice rather than a malformation.
        test "JSON lines parse whether values are typed or stringly" {
            let map = {
                FieldMap.required "RequestURI" "ResponseBytes" "ResponseStatus" "StartTimestamp" with
                    OutcomeField = Some "CacheStatus"
                    RequestIdField = Some "RayID"
                    EdgeOutcomes = Set.ofList [ "hit" ]
                    OriginOutcomes = Set.ofList [ "miss" ]
            }

            let content =
                String.concat "\n" [
                    """{"RequestURI":"/api/media/hls/abc/v0/s1.ts","ResponseBytes":4096,"ResponseStatus":200,"StartTimestamp":"2026-08-28T10:00:00Z","CacheStatus":"hit","RayID":"ray-1"}"""
                    """{"RequestURI":"/media/signed/abc?token=xyz","ResponseBytes":"8192","ResponseStatus":"200","StartTimestamp":"2026-08-28T10:00:01Z","CacheStatus":"MISS","RayID":"ray-2"}"""
                ]

            let output = parseJsonLines map content

            Expect.isEmpty output.Errors "no errors"
            Expect.equal (List.length output.Records) 2 "two records"
            Expect.equal output.Records[0].Bytes 4096L "numeric bytes"
            Expect.equal output.Records[1].Bytes 8192L "stringly bytes"
            Expect.equal output.Records[0].Outcome ServedFromEdge "hit"
            Expect.equal output.Records[1].Outcome ServedFromOrigin "case-insensitive MISS"
            Expect.equal output.Records[1].Url "/media/signed/abc?token=xyz" "the URI field already carries the query"
        }

        test "a non-JSON line is reported and the rest of the file parses" {
            let map = FieldMap.required "u" "b" "s" "t"

            let content =
                "{\"u\":\"/api/media/stream/a\",\"b\":1,\"s\":200,\"t\":\"2026-08-28T10:00:00Z\"}\nnot json"

            let output = parseJsonLines map content

            Expect.equal (List.length output.Records) 1 "the good line survives"
            Expect.equal (List.length output.Errors) 1 "the bad line is reported"
        }

        test "a parsed batch ingests end to end" {
            let usage = RecordingUsageLog()

            let resolver =
                CallbackLogSource.CallbackScopeResolver("t", Map.ofList [ "abc", scope "team-1" ])

            let ingestor =
                DeliveredEgressIngestor(usage, None, None, Some(resolver :> IDeliveredScopeResolver))

            let content =
                String.concat "\n" [
                    "#Fields: date time uri-stem status bytes-sent uri-query result-type request-id"
                    "2026-08-28\t10:00:00\t/api/media/hls/abc/v0/s1.ts\t200\t4096\t-\tHit\treq-1"
                    "2026-08-28\t10:00:01\t/api/media/hls/abc/v0/s2.ts\t200\t4096\t-\tMiss\treq-2"
                ]

            let output = parseDelimited delimitedMap '\t' None content

            let outcome = ingest ingestor "file-1" output.Records

            Expect.equal outcome.Attributed 2 "both attributed"
            Expect.equal outcome.AttributedBytes 8192L "delivered bytes"
            Expect.equal outcome.EdgeServedBytes 4096L "only the Hit is an edge hit"
        }

        test "a throwing log source becomes a typed error, never an escaped exception" {
            let source =
                CallbackLogSource.CallbackLogSource.ofBatches (
                    "boom",
                    TimeSpan.FromHours 1.0,
                    fun () -> async { return failwith "store unreachable" }
                )

            match (source :> IDeliveredLogSource).FetchBatches() |> Async.RunSynchronously with
            | Error e -> Expect.stringContains e "store unreachable" "the message survives"
            | Ok _ -> failtest "expected a typed error"
        }

        // Rule 6 — declared rather than assumed. The two edge classes
        // surveyed differ by roughly three orders of magnitude here.
        test "a source declares its own delivery lag" {
            let source =
                CallbackLogSource.CallbackLogSource("s", TimeSpan.FromHours 24.0, fun () -> async { return Ok [] })

            Expect.equal (source :> IDeliveredLogSource).DeliveryLag (TimeSpan.FromHours 24.0) "declared lag"
        }
    ]

let tests =
    testList "DeliveredEgress (Phase 742)" [ pathTests; dedupTests; ingestionTests; rollupTests; parserTests ]