module ToolUp.Platform.Tests.InProcess.DataProtectionBackendTests

open System.Xml.Linq
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ConfigValidatorAggregator
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 329 — fail-loud DataProtection key-ring backend ───────────
//
// Two halves of the same swallow:
//
//   1. `DataProtectionBackendValidator` — a misconfigured / unreachable
//      key-ring backend fails preflight with a message naming the
//      container, prefix, and underlying error, instead of booting
//      with a silent ephemeral key. Security-class: `SkipPreflight`
//      cannot bypass it.
//   2. `BlobXmlRepository` — a key-ring read failure emits a `Warn`
//      (naming container + prefix), so an empty result on a transient
//      read is distinguishable from a genuinely-empty first-boot ring
//      (which stays silent — GP 11).

/// Captures `Warn` lines so the tests can assert on the read-failure
/// diagnostic (and its absence on the healthy / first-boot paths).
type private CapturingLogger() =
    let warns = System.Collections.Generic.List<string>()
    member _.Warns = List.ofSeq warns

    interface ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(m: string) = warns.Add m
        member _.Error(_: string, _: exn option) = ()

/// An unreachable backend — every operation raises, the way a
/// misconfigured container / dead endpoint surfaces through a real
/// companion (S3 / Azure / GCS SDKs throw on connectivity failure).
type private UnreachableBlobStorage() =
    let boom () : 'a =
        failwith "connection refused (simulated backend outage)"

    interface IBlobStorage with
        member _.Upload(_, _, _) = async { return boom () }
        member _.Download(_, _) = async { return boom () }
        member _.DownloadRange(_, _, _, _) = async { return boom () }
        member _.Delete(_, _) = async { return boom () }
        member _.List(_, _) = async { return boom () }
        member _.Exists(_, _) = async { return boom () }
        member _.GetMetadata(_, _) = async { return boom () }
        member _.Erase(_, _, _, _) = async { return boom () }

/// A backend where the prefix lists but individual key blobs fail to
/// download (permission gap / partial outage) — the per-key catch site.
type private DownloadFailingBlobStorage(names: string list) =
    interface IBlobStorage with
        member _.Upload(_, blobName, _) = async { return Result.Ok blobName }
        member _.Download(_, _) = async { return Result.Error "access denied (simulated)" }
        member _.DownloadRange(_, _, _, _) = async { return Result.Error "access denied (simulated)" }
        member _.Delete(_, _) = async { return Result.Ok() }
        member _.List(_, _) = async { return names }
        member _.Exists(_, _) = async { return false }
        member _.GetMetadata(_, _) = async { return Result.Error "access denied (simulated)" }
        member _.Erase(_, _, _, _) = async { return failwith "not exercised" }

/// A read-only backend — `List` works but writes are refused, so key
/// creation / rotation could never persist.
type private WriteDeniedBlobStorage() =
    interface IBlobStorage with
        member _.Upload(_, _, _) = async { return Result.Error "write denied (simulated read-only credentials)" }
        member _.Download(_, _) = async { return Result.Error "not found" }
        member _.DownloadRange(_, _, _, _) = async { return Result.Error "not found" }
        member _.Delete(_, _) = async { return Result.Ok() }
        member _.List(_, _) = async { return [] }
        member _.Exists(_, _) = async { return false }
        member _.GetMetadata(_, _) = async { return Result.Error "not found" }
        member _.Erase(_, _, _, _) = async { return failwith "not exercised" }

let private validate (storage: #IBlobStorage) : ValidationResult =
    let v =
        DataProtectionBackendValidator.DataProtectionBackendValidator(storage) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private repository (storage: #IBlobStorage) (logger: CapturingLogger) =
    BlobXmlRepository(storage, logger :> ILogger) :> Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository

[<Tests>]
let tests =
    testList "Phase 329 — DataProtection key-ring backend fail-loud" [

        // ── DataProtectionBackendValidator ──

        test "healthy backend with an empty first-boot ring → Ok" {
            Expect.equal (validate (InMemoryBlobStorage())) Ok "an empty prefix is a legitimate first boot, not a fault"
        }

        test "healthy backend passes and leaves no sentinel behind" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            storage.Upload(BlobDpKeyRing.Container, BlobDpKeyRing.Prefix + "key-1.xml", [| 1uy |])
            |> Async.RunSynchronously
            |> ignore

            Expect.equal (validate storage) Ok "existing keys + healthy backend → silent"

            let remaining =
                storage.List(BlobDpKeyRing.Container, BlobDpKeyRing.Prefix)
                |> Async.RunSynchronously

            Expect.equal remaining [ BlobDpKeyRing.Prefix + "key-1.xml" ] "the probe sentinel was deleted"
        }

        test "unreachable backend → Error naming container, prefix, and underlying error" {
            match validate (UnreachableBlobStorage()) with
            | Error msg ->
                Expect.stringContains msg BlobDpKeyRing.Container "names the container"
                Expect.stringContains msg BlobDpKeyRing.Prefix "names the key-ring prefix"
                Expect.stringContains msg "connection refused (simulated backend outage)" "carries the underlying error"
                Expect.stringContains msg "ephemeral" "explains the silent-ephemeral-key consequence"
            | other -> failtestf "expected Error, got %A" other
        }

        test "write-denied backend → Error (key creation/rotation could not persist)" {
            match validate (WriteDeniedBlobStorage()) with
            | Error msg ->
                Expect.stringContains msg "sentinel upload" "names the failing probe step"

                Expect.stringContains
                    msg
                    "write denied (simulated read-only credentials)"
                    "carries the underlying error"
            | other -> failtestf "expected Error, got %A" other
        }

        test "security-class: implements ISecurityClassValidator (SkipPreflight cannot bypass)" {
            let v =
                DataProtectionBackendValidator.DataProtectionBackendValidator(InMemoryBlobStorage()) :> IConfigValidator

            match box v with
            | :? ISecurityClassValidator -> ()
            | _ -> failtest "DataProtectionBackendValidator must carry the ISecurityClassValidator marker"
        }

        test "SkipPreflight = true still runs the probe and aborts on an unreachable backend" {
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                DataProtectionBackendValidator.DataProtectionBackendValidator(UnreachableBlobStorage())
                :> IConfigValidator
            )
            |> ignore

            try
                ConfigValidatorAggregator.validate services None true |> ignore
                failtest "expected ConfigPreflightFailedException despite SkipPreflight"
            with :? ConfigPreflightFailedException as ex ->
                Expect.stringContains ex.Message "dataprotection-keyring-backend" "the abort names the validator"
        }

        // ── BlobXmlRepository Warn on read failure ──

        test "key-ring List failure → empty ring + a Warn naming container and prefix" {
            let logger = CapturingLogger()
            let repo = repository (UnreachableBlobStorage()) logger

            let elements = repo.GetAllElements()

            Expect.equal elements.Count 0 "an unreadable backend degrades to an empty ring (behaviour unchanged)"
            Expect.hasLength logger.Warns 1 "exactly one Warn for the List failure"
            Expect.stringContains logger.Warns[0] BlobDpKeyRing.Container "the Warn names the container"
            Expect.stringContains logger.Warns[0] BlobDpKeyRing.Prefix "the Warn names the key-ring prefix"

            Expect.stringContains
                logger.Warns[0]
                "connection refused (simulated backend outage)"
                "the Warn carries the underlying error"
        }

        test "genuinely-empty first-boot ring → empty + NO Warn (distinguishable from a read failure)" {
            let logger = CapturingLogger()
            let repo = repository (InMemoryBlobStorage()) logger

            Expect.equal (repo.GetAllElements()).Count 0 "first boot: no keys yet"
            Expect.isEmpty logger.Warns "a legitimately-empty ring is silent — only a read failure warns"
        }

        test "per-key Download failure → key skipped + a Warn naming the blob" {
            let keyBlob = BlobDpKeyRing.Prefix + "key-abc.xml"
            let logger = CapturingLogger()
            let repo = repository (DownloadFailingBlobStorage [ keyBlob ]) logger

            Expect.equal (repo.GetAllElements()).Count 0 "the undownloadable key is skipped"
            Expect.hasLength logger.Warns 1 "exactly one Warn for the failed key"
            Expect.stringContains logger.Warns[0] keyBlob "the Warn names the failing blob"
            Expect.stringContains logger.Warns[0] "access denied (simulated)" "the Warn carries the underlying error"
        }

        test "healthy round-trip: StoreElement → GetAllElements returns the element, no Warn (GP 11)" {
            let logger = CapturingLogger()
            let repo = repository (InMemoryBlobStorage()) logger

            repo.StoreElement(XElement.Parse "<key id=\"k1\" />", "key-k1")
            let elements = repo.GetAllElements() |> List.ofSeq

            Expect.hasLength elements 1 "the persisted key reads back"
            Expect.equal (elements[0].Attribute(XName.Get "id").Value) "k1" "byte-for-byte round-trip"
            Expect.isEmpty logger.Warns "a healthy backend is silent"
        }
    ]