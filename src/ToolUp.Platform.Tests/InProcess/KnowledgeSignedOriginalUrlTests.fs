// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.KnowledgeSignedOriginalUrlTests

open System
open System.Collections.Concurrent
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerOriginalSourceResolver
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// Abbreviated rather than `open`ed: the preview-seam module exports a
// `createDefault` that would shadow the Phase 104 resolver's.
module PreviewSeam = KnowledgeBase.ServerOriginalPreviewSeam

// ─── Phase 108 — time-bound direct-download URLs for originals ───────
//
// What this pack pins, in order of how much it would cost to get wrong:
//
//   1. **The scope check precedes the mint** (GP 4). A signed URL is a
//      bearer token: once minted, holding it IS the authorisation. So
//      the ordering is not tidiness, it is the access control — an
//      out-of-scope fetch must be refused with no mint having happened
//      at all, not refused after one was quietly issued. Pinned by
//      counting mints, because "returned NotInScope" alone would still
//      pass if the URL had already been created.
//   2. **A backend that cannot sign falls back to proxying, silently.**
//      Local filesystem, in-memory, encrypted-at-rest. This is what
//      makes the opt-in safe to compose everywhere.
//   3. **A backend that FAILS to sign does not fall back.** Reinstating
//      megabytes a deployment deliberately excluded, on a transient
//      storage fault, is the one outcome nobody asked for.
//   4. **Unset is unchanged.** No seam composed ⇒ `GetOriginalDelivery`
//      is `Inline` carrying exactly `GetOriginalDocument`'s bytes.
//   5. **Signed delivery does not read the original.** The Phase 200
//      seam had to download the whole thing just to prove it existed;
//      `ResolveMetadata` is why this one does not. Pinned by counting
//      `Download` calls, since a byte-light path that quietly downloads
//      looks identical from the outside.

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private mkDoc (docId: string) (fileName: string) (fileType: string) (source: KnowledgeSource) : KnowledgeDocument = {
    Id = docId
    FileName = fileName
    FileType = fileType
    UploadedAt = DateTimeOffset.UtcNow
    UploadedBy = "user-1"
    Status = Complete 1
    SizeBytes = 0L
    ChunkCount = 1
    Source = source
    ContentHash = None
    Version = 1
    // Phase 502.C — untagged fixture.
    Tags = []
}

let private mkDeps (storage: IBlobStorage) (container: string) : KnowledgeApiDeps = {
    Storage = storage
    Queue = IngestionQueue()
    OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
    TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
    Notifications = Unchecked.defaultof<INotificationChannel>
    Logger = noopLogger
    Scope = {
        ScopeId = container
        Container = container
        Persist = true
    }
    UserId = "user-1"
    VectorScope = Deployment
    VectorStore = None
    IndexLifecycle = None
    EventStore = None
    NarrativeStore = None
    AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
    OriginalResolver = createDefault ()
    AuditLog = None
    RecordEnqueue = ignore
    PublishInventory = fun () -> async.Return()
    MarkIngestionFailed = fun _ _ _ -> async.Return()
    EnsureContextWriteAllowed = fun () -> async { return Ok() }
    ScopeResolvedFromRequest = true
    UploadPolicy = KnowledgeUploadPolicy.permissive
    DedupPolicy = KnowledgeDedupPolicy.enabled
    VersioningPolicy = KnowledgeVersioningPolicy.disabled
    // Phase 105 — retention not composed: the pre-105 convention-blob path.
    DataObjectStore = None
    QuotaPolicy = KnowledgeQuotaPolicy.unlimited
    RetentionPolicy = KnowledgeRetentionPolicy.retainForever
    // Phase 515 — no scanner composed: the pre-515 upload path.
    ContentScanner = None
    ScanPolicy = ContentScanPolicy.defaults
    DisclosureGate = None
    // Phase 511 — bulk import is not exercised here: archive guards at
    // their shipped defaults, URL ingestion inert, no transport.
    ArchiveImportPolicy = ArchiveImportPolicy.defaults
    UrlIngestionPolicy = UrlIngestionPolicy.disabled
    UrlFetcher = None
}

// ─── Storage doubles ─────────────────────────────────────────────────

/// Counts `Download` calls so the byte-light claim is measurable, and
/// otherwise delegates. Implements `IBlobStorage` ONLY — the shape of a
/// local-filesystem / in-memory / encrypted-at-rest backend, where the
/// Phase 108 type test must fail and delivery must fall back to inline.
type private CountingBlobStorage(inner: IBlobStorage) =
    let mutable downloads = 0

    member _.Downloads = downloads

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = async {
            System.Threading.Interlocked.Increment(&downloads) |> ignore
            return! inner.Download(container, blobName)
        }

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// Delegating store that ALSO implements the Phase 108 signing
/// capability. `mint` decides the outcome per call so one double covers
/// success, `NotConfigured` and `SigningFailed`; every call is recorded
/// so a test can assert a mint did NOT happen.
type private SigningBlobStorage
    (inner: IBlobStorage, mint: string * string * TimeSpan -> Result<string, SignedUrlRefusal>) =
    let calls = ConcurrentQueue<string * string * TimeSpan>()
    let mutable downloads = 0

    member _.Mints = calls |> List.ofSeq
    member _.Downloads = downloads

    interface ISignedUrlBlobStorage with
        member _.SignedUrl(container, blobName, ttl) = async {
            calls.Enqueue(container, blobName, ttl)
            return mint (container, blobName, ttl)
        }

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = async {
            System.Threading.Interlocked.Increment(&downloads) |> ignore
            return! inner.Download(container, blobName)
        }

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

// ─── Fixtures ────────────────────────────────────────────────────────

let private fixedNow = DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero)

let private options: PreviewSeam.PreviewSignedUrlOptions = {
    Ttl = TimeSpan.FromMinutes 15.0
    Now = fun () -> fixedNow
}

let private alwaysMint (_, blobName: string, _) =
    Ok $"https://cdn.example/{blobName}?sig=abc"

/// Seed one uploaded original into `container` and return its document.
let private seedUpload (storage: IBlobStorage) (container: string) = async {
    let doc = mkDoc "doc-1" "report.pdf" "pdf" UploadedFile
    do! saveIndex storage container [ doc ]
    let! _ = storage.Upload(container, "knowledge/doc-1/report.pdf", Encoding.UTF8.GetBytes "PDF-BYTES")
    return doc
}

// ─── The capability probe (Platform.Core) ────────────────────────────

let private probeTests =
    testList "Phase 108 — BlobStorage.trySignedUrl capability probe" [
        testCaseAsync "a store without the capability answers Ok None — the local-filesystem answer, not an error"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let! result =
                BlobStorage.trySignedUrl storage "team-a" "knowledge/doc-1/report.pdf" (TimeSpan.FromMinutes 5.0)

            Expect.equal result (Ok None) "no capability ⇒ Ok None (fall back to proxying)"
        }

        testCaseAsync "a signing store answers Ok (Some url)"
        <| async {
            let storage = SigningBlobStorage(InMemoryBlobStorage(), alwaysMint) :> IBlobStorage

            let! result =
                BlobStorage.trySignedUrl storage "team-a" "knowledge/doc-1/report.pdf" (TimeSpan.FromMinutes 5.0)

            match result with
            | Ok(Some url) -> Expect.stringContains url "knowledge/doc-1/report.pdf" "the minted URL names the blob"
            | other -> failtest $"expected Ok (Some url), got %A{other}"
        }

        testCaseAsync "NotConfigured collapses to Ok None — same branch as no capability at all"
        <| async {
            let declining =
                SigningBlobStorage(InMemoryBlobStorage(), fun _ -> Error(SignedUrlRefusal.NotConfigured "no key"))
                :> IBlobStorage

            let! result = BlobStorage.trySignedUrl declining "team-a" "blob" (TimeSpan.FromMinutes 5.0)
            Expect.equal result (Ok None) "an unconfigured signer is a proxy fallback, not a failure"
        }

        testCaseAsync "SigningFailed surfaces as Error — never silently a fallback"
        <| async {
            let broken =
                SigningBlobStorage(
                    InMemoryBlobStorage(),
                    fun _ -> Error(SignedUrlRefusal.SigningFailed "kms unreachable")
                )
                :> IBlobStorage

            let! result = BlobStorage.trySignedUrl broken "team-a" "blob" (TimeSpan.FromMinutes 5.0)

            match result with
            | Error message -> Expect.stringContains message "kms unreachable" "diagnostic preserved"
            | other -> failtest $"expected Error, got %A{other}"
        }

        testCaseAsync "a non-positive TTL is refused before any backend is reached"
        <| async {
            let signer = SigningBlobStorage(InMemoryBlobStorage(), alwaysMint)
            let! result = BlobStorage.trySignedUrl (signer :> IBlobStorage) "team-a" "blob" TimeSpan.Zero

            match result with
            | Error message -> Expect.stringContains message "strictly positive" "names the caller defect"
            | other -> failtest $"expected Error, got %A{other}"

            Expect.isEmpty signer.Mints "a zero TTL must not reach the backend at all"
        }
    ]

// ─── ResolveMetadata (Phase 108 on the Phase 104 resolver) ───────────

let private metadataTests =
    testList "Phase 108 — IOriginalSourceResolver.ResolveMetadata" [
        testCaseAsync "an uploaded original resolves to its signable blob name + metadata, with no download"
        <| async {
            let counting = CountingBlobStorage(InMemoryBlobStorage())
            let storage = counting :> IBlobStorage
            let! doc = seedUpload storage "team-a"
            let resolver = createDefault ()

            let! located = resolver.ResolveMetadata(storage, "team-a", doc)

            match located with
            | Some location ->
                Expect.equal location.BlobName (Some "knowledge/doc-1/report.pdf") "the Phase 104 naming convention"
                Expect.equal location.ContentType "application/pdf" "extension-derived content type"
                Expect.equal location.SizeBytes 9L "size from the blob's properties"
            | None -> failtest "expected a resolved location"

            Expect.equal counting.Downloads 0 "metadata resolution must not transfer content"
        }

        testCaseAsync "a note resolves to its markdown blob"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let noteSource: NoteSource = {
                Title = "Conventions"
                Author = "user-1"
                CreatedAt = DateTimeOffset.UtcNow
                LastEditedAt = None
            }

            let doc = mkDoc "note-1" "Conventions.md" "note" (Note noteSource)
            let! _ = storage.Upload("team-a", "knowledge/note-1/note.md", Encoding.UTF8.GetBytes "# hi")

            let! located = (createDefault ()).ResolveMetadata(storage, "team-a", doc)

            match located with
            | Some location ->
                Expect.equal location.BlobName (Some "knowledge/note-1/note.md") "note canonical blob"
                Expect.equal location.ContentType "text/markdown" "notes are markdown"
            | None -> failtest "expected a resolved location"
        }

        testCaseAsync "presence agrees with Resolve — narrative absent, missing blob absent"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let resolver = createDefault ()

            let narrativeSource: NarrativeDocSource = {
                ModuleId = "analysis"
                PageRoute = Some "/analysis/overview"
                SettingsKey = "default"
                SettingsDisplay = []
                GeneratedAt = DateTimeOffset.UtcNow
            }

            let narrative = mkDoc "doc-n" "Overview.md" "md" (FromNarrative narrativeSource)
            let! narrativeResolved = resolver.Resolve(storage, "team-a", narrative)
            let! narrativeLocated = resolver.ResolveMetadata(storage, "team-a", narrative)
            Expect.isNone narrativeResolved "narratives have no original"
            Expect.isNone narrativeLocated "…and ResolveMetadata must agree"

            // Indexed but never persisted — the out-of-band-deletion case.
            let ghost = mkDoc "doc-g" "gone.pdf" "pdf" UploadedFile
            let! ghostResolved = resolver.Resolve(storage, "team-a", ghost)
            let! ghostLocated = resolver.ResolveMetadata(storage, "team-a", ghost)
            Expect.isNone ghostResolved "a missing blob is absence, not a throw"
            Expect.isNone ghostLocated "…and ResolveMetadata must agree"
        }

        testCaseAsync "locationViaResolve gives a custom resolver the member for free, with no signable blob"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let custom =
                { new IOriginalSourceResolver with
                    member _.Resolve(_, _, doc) = async {
                        return
                            Some {
                                FileName = doc.FileName
                                ContentType = "text/markdown"
                                SizeBytes = 8L
                                Content = Encoding.UTF8.GetBytes "rendered"
                            }
                    }

                    member this.ResolveMetadata(s, c, d) = locationViaResolve this s c d
                }

            let! located = custom.ResolveMetadata(storage, "team-a", mkDoc "doc-1" "x.md" "md" UploadedFile)

            match located with
            | Some location ->
                Expect.isNone location.BlobName "a synthesised original names no blob — signing must fall back"
                Expect.equal location.SizeBytes 8L "metadata still recovered"
            | None -> failtest "expected a resolved location"
        }
    ]

// ─── The seam: sign, fall back, or fail ──────────────────────────────

let private seamTests =
    testList "Phase 108 — BlobSignedUrlOriginalPreviewSeam" [
        testCaseAsync "a signing backend delivers a URL expiring exactly one TTL from now, reading no bytes"
        <| async {
            let signing = SigningBlobStorage(InMemoryBlobStorage(), alwaysMint)
            let storage = signing :> IBlobStorage
            let! doc = seedUpload storage "team-a"
            let seam = PreviewSeam.createBlobSignedUrl (createDefault ()) options

            let! result = seam.Preview(storage, "team-a", doc, None)

            match result with
            | Ok target ->
                match target.Content with
                | PreviewContent.SignedUrl(url, expiresAt) ->
                    Expect.stringContains url "knowledge/doc-1/report.pdf" "URL names the original's blob"
                    Expect.equal expiresAt (fixedNow.Add options.Ttl) "expiry is now + the configured TTL"
                | other -> failtest $"expected SignedUrl delivery, got %A{other}"

                Expect.equal target.ContentType "application/pdf" "viewer-picking metadata survives byte-light delivery"
                Expect.equal target.SizeBytes 9L "size survives byte-light delivery"
            | Error e -> failtest $"expected Ok, got %A{e}"

            Expect.equal signing.Downloads 0 "signed delivery must not read the original server-side"

            match signing.Mints with
            | [ (container, blobName, ttl) ] ->
                Expect.equal container "team-a" "minted within the caller's scope container"
                Expect.equal blobName "knowledge/doc-1/report.pdf" "minted for the resolved blob"
                Expect.equal ttl options.Ttl "the composed TTL is the one handed to the backend"
            | other -> failtest $"expected exactly one mint, got %A{other}"
        }

        testCaseAsync "a backend with no signing capability falls back to inline delivery, transparently"
        <| async {
            // The local-filesystem / encrypted-at-rest shape: composing
            // the opt-in on such a deployment must be a no-op, not a
            // refusal, or the option is unsafe to compose by default.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let! doc = seedUpload storage "team-a"
            let seam = PreviewSeam.createBlobSignedUrl (createDefault ()) options

            let! result = seam.Preview(storage, "team-a", doc, None)

            match result with
            | Ok target ->
                match target.Content with
                | PreviewContent.Inline original ->
                    Expect.equal (Encoding.UTF8.GetString original.Content) "PDF-BYTES" "the real bytes, proxied"
                | other -> failtest $"expected Inline fallback, got %A{other}"
            | Error e -> failtest $"expected Ok, got %A{e}"
        }

        testCaseAsync "a backend that declines to sign (NotConfigured) also falls back to inline"
        <| async {
            let declining =
                SigningBlobStorage(InMemoryBlobStorage(), fun _ -> Error(SignedUrlRefusal.NotConfigured "ADC only"))

            let storage = declining :> IBlobStorage
            let! _ = seedUpload storage "team-a"
            let doc = mkDoc "doc-1" "report.pdf" "pdf" UploadedFile
            let seam = PreviewSeam.createBlobSignedUrl (createDefault ()) options

            let! result = seam.Preview(storage, "team-a", doc, None)

            match result with
            | Ok target ->
                match target.Content with
                | PreviewContent.Inline _ -> ()
                | other -> failtest $"expected Inline fallback, got %A{other}"
            | Error e -> failtest $"expected Ok, got %A{e}"
        }

        testCaseAsync "a signing FAILURE surfaces — it must not silently reinstate the bytes"
        <| async {
            let broken =
                SigningBlobStorage(
                    InMemoryBlobStorage(),
                    fun _ -> Error(SignedUrlRefusal.SigningFailed "kms unreachable")
                )

            let storage = broken :> IBlobStorage
            let! doc = seedUpload storage "team-a"
            let seam = PreviewSeam.createBlobSignedUrl (createDefault ()) options

            let! result = seam.Preview(storage, "team-a", doc, None)

            match result with
            | Error(OriginalRetrievalFailed reason) ->
                Expect.stringContains reason "kms unreachable" "diagnostic preserved"
            | other -> failtest $"expected OriginalRetrievalFailed, got %A{other}"

            Expect.equal broken.Downloads 0 "a failed mint must not fall through to a download"
        }

        testCaseAsync "a source kind with no original refuses, on a signing backend too"
        <| async {
            let signing = SigningBlobStorage(InMemoryBlobStorage(), alwaysMint)
            let storage = signing :> IBlobStorage

            let narrativeSource: NarrativeDocSource = {
                ModuleId = "analysis"
                PageRoute = Some "/analysis/overview"
                SettingsKey = "default"
                SettingsDisplay = []
                GeneratedAt = DateTimeOffset.UtcNow
            }

            let doc = mkDoc "doc-n" "Overview.md" "md" (FromNarrative narrativeSource)
            let seam = PreviewSeam.createBlobSignedUrl (createDefault ()) options

            let! result = seam.Preview(storage, "team-a", doc, None)

            Expect.equal result (Error NoOriginalAvailable) "no original ⇒ typed absence"
            Expect.isEmpty signing.Mints "nothing to sign, so nothing was signed"
        }

        testCase "a non-positive TTL fails the boot, not a request"
        <| fun () ->
            Expect.throws
                (fun () ->
                    PreviewSeam.createBlobSignedUrl (createDefault ()) { options with Ttl = TimeSpan.Zero }
                    |> ignore)
                "a misconfigured lifetime is a deployment defect"
    ]

// ─── The handler: opt-in, default, and the ordering guarantee ────────

let private handlerTests =
    testList "Phase 108 — getOriginalDelivery" [
        testCaseAsync "unset is the Phase 102 path: Inline, byte-identical to GetOriginalDocument"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let! _ = seedUpload storage "team-a"
            let deps = mkDeps storage "team-a"

            let! proxied = getOriginalDocument deps "doc-1"
            let! delivered = getOriginalDelivery None deps "doc-1"

            match proxied, delivered with
            | Ok original, Ok(PreviewContent.Inline inlined) ->
                Expect.equal inlined original "the unset arm returns exactly what Phase 102 returns"
            | _ -> failtest $"expected Ok/Inline, got %A{proxied} / %A{delivered}"
        }

        testCaseAsync "opted in on a signing backend, a fetch returns a time-bound URL"
        <| async {
            let signing = SigningBlobStorage(InMemoryBlobStorage(), alwaysMint)
            let storage = signing :> IBlobStorage
            let! _ = seedUpload storage "team-a"
            let deps = mkDeps storage "team-a"
            let seam = PreviewSeam.createBlobSignedUrl (createDefault ()) options

            let! result = getOriginalDelivery (Some seam) deps "doc-1"

            match result with
            | Ok(PreviewContent.SignedUrl(_, expiresAt)) ->
                Expect.equal expiresAt (fixedNow.Add options.Ttl) "expires after the configured TTL"
            | other -> failtest $"expected SignedUrl, got %A{other}"
        }

        testCaseAsync "THE ORDERING GUARANTEE — an out-of-scope id is refused with NO url minted (GP 4)"
        <| async {
            // A signed URL is a bearer token, so a mint that happens
            // before the gate is a leak even when the call still returns
            // NotInScope. Asserting the refusal alone would not catch
            // that; asserting the mint count is what pins the order.
            let signing = SigningBlobStorage(InMemoryBlobStorage(), alwaysMint)
            let storage = signing :> IBlobStorage

            // The document exists — in ANOTHER team's container.
            let! _ = seedUpload storage "team-b"
            let deps = mkDeps storage "team-a"
            let seam = PreviewSeam.createBlobSignedUrl (createDefault ()) options

            let! result = getOriginalDelivery (Some seam) deps "doc-1"

            Expect.equal result (Error NotInScope) "another team's document is not findable (no existence oracle)"
            Expect.isEmpty signing.Mints "the scope check ran BEFORE any URL was minted"
        }

        testCaseAsync "an unknown id refuses identically, and mints nothing"
        <| async {
            let signing = SigningBlobStorage(InMemoryBlobStorage(), alwaysMint)
            let storage = signing :> IBlobStorage
            let! _ = seedUpload storage "team-a"
            let deps = mkDeps storage "team-a"
            let seam = PreviewSeam.createBlobSignedUrl (createDefault ()) options

            let! result = getOriginalDelivery (Some seam) deps "no-such-doc"

            Expect.equal result (Error NotInScope) "unknown and out-of-scope are indistinguishable"
            Expect.isEmpty signing.Mints "no mint for an id that was never in scope"
        }

        testCaseAsync "opted in on a NON-signing backend, delivery is transparently the proxy path"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let! _ = seedUpload storage "team-a"
            let deps = mkDeps storage "team-a"
            let seam = PreviewSeam.createBlobSignedUrl (createDefault ()) options

            let! delivered = getOriginalDelivery (Some seam) deps "doc-1"
            let! proxied = getOriginalDocument deps "doc-1"

            match delivered, proxied with
            | Ok(PreviewContent.Inline inlined), Ok original ->
                Expect.equal inlined original "local-filesystem deployments fall back to Phase 102's bytes"
            | _ -> failtest $"expected Inline, got %A{delivered}"
        }
    ]

let tests =
    testList "KnowledgeBase signed original URLs (Phase 108)" [ probeTests; metadataTests; seamTests; handlerTests ]