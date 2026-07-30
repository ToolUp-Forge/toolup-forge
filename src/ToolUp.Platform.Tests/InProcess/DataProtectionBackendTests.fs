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

/// Captures `Warn` and `Error` lines so the tests can assert on the
/// read-failure diagnostic (and its absence on the healthy / first-boot
/// paths) and on the write-failure diagnostic.
type private CapturingLogger() =
    let warns = System.Collections.Generic.List<string>()
    let errors = System.Collections.Generic.List<string * exn option>()
    member _.Warns = List.ofSeq warns
    member _.Errors = List.ofSeq errors

    interface ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(m: string) = warns.Add m
        member _.Error(m: string, ex: exn option) = errors.Add((m, ex))

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
            Expect.isEmpty logger.Errors "a healthy store emits no write-failure diagnostic (GP 11)"
        }

        // ── BlobXmlRepository Error on WRITE failure ──
        //
        // Phase 329 made the read path fail-loud but left `StoreElement`
        // `|> ignore`-ing its `Upload` result, so a transient write
        // failure at key creation / rotation silently dropped the new
        // key and this process went on sealing with a key no other
        // replica can read. These pin the diagnostic — and the
        // deliberate decision NOT to raise (see the type doc comment:
        // ASP.NET surfaces a raise from `StoreElement` as a
        // `CryptographicException` out of `Protect`, so raising would
        // turn a transient blob hiccup into a live request failure).

        test "key-ring StoreElement write refusal → an Error naming container and blob" {
            let logger = CapturingLogger()
            let repo = repository (WriteDeniedBlobStorage()) logger

            repo.StoreElement(XElement.Parse "<key id=\"k1\" />", "key-k1")

            Expect.hasLength logger.Errors 1 "exactly one Error for the refused write"
            let message, ex = logger.Errors[0]
            Expect.stringContains message BlobDpKeyRing.Container "the Error names the container"
            Expect.stringContains message (BlobDpKeyRing.Prefix + "key-k1.xml") "the Error names the blob"

            Expect.stringContains
                message
                "write denied (simulated read-only credentials)"
                "the Error carries the underlying reason"

            Expect.stringContains message "NOT persisted" "the Error states the consequence"
            Expect.isNone ex "a refused `Result.Error` carries no exception"
            Expect.isEmpty logger.Warns "the write failure is Error-class, not Warn-class"
        }

        test "key-ring StoreElement throw → an Error carrying the exception (not re-raised)" {
            let logger = CapturingLogger()
            let repo = repository (UnreachableBlobStorage()) logger

            // Must not raise: ASP.NET propagates a `StoreElement` throw
            // out of `Protect` as a `CryptographicException`, so raising
            // here would fail live requests at the rotation moment.
            repo.StoreElement(XElement.Parse "<key id=\"k1\" />", "key-k1")

            Expect.hasLength logger.Errors 1 "exactly one Error for the throwing backend"
            let message, ex = logger.Errors[0]

            Expect.stringContains
                message
                "connection refused (simulated backend outage)"
                "the Error carries the underlying error"

            Expect.isSome ex "the originating exception is passed to the logger for its stack trace"
        }

        test "StoreElement with no logger configured still does not raise on a write failure" {
            let repo =
                BlobXmlRepository(UnreachableBlobStorage())
                :> Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository

            repo.StoreElement(XElement.Parse "<key id=\"k1\" />", "key-k1")
        }
    ]