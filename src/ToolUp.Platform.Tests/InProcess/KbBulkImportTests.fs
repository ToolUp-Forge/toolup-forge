module ToolUp.Platform.Tests.InProcess.KbBulkImportTests

open System
open System.IO
open System.IO.Compression
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerBulkImport
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 511 — bulk / programmatic KB import ───────────────────────
//
// The whole design claim of this phase is ONE sentence: bulk import adds
// no second admission path. Every item goes through `uploadDocumentCore`,
// the same function `UploadDocument` calls, so the Phase 119 policy
// checks, the Phase 515 content scan, the Phase 14x dedup, the Phase 512
// corpus quota and the Phase 510 versioning decision apply per item with
// no duplicate implementation that could drift.
//
// So these tests split cleanly in two:
//
//   * **What is genuinely new** — archive expansion under resource
//     guards, zip-slip classification, and the URL gate. Hostile input
//     handling that did not exist before, tested directly.
//   * **What must still hold per item** — quota, scan and dedup asserted
//     THROUGH the batch surface, because "each item passes the existing
//     checks" is a claim about the batch, not about the upload boundary
//     (which already has its own packs).
//
// Plus the GP 11 arm: the single-file upload path is byte-for-byte its
// pre-511 self, pinned by the one observable difference the phase
// introduces (the suppressed dedup toast).

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Notification channel that records every publish, so "one completion
/// signal per batch, not N toasts" is an assertion rather than a claim.
type private CapturingNotifications() =
    let published = ResizeArray<string * Notification>()
    let gate = obj ()

    member _.Published = lock gate (fun () -> published |> List.ofSeq)

    member this.OfKey(key: string) =
        this.Published
        |> List.filter (fun (_, n) ->
            match n with
            | CustomNotification(k, _) -> k = key
            | _ -> false)

    member this.SystemMessages =
        this.Published
        |> List.choose (fun (_, n) ->
            match n with
            | SystemMessage(_, text) -> Some text
            | _ -> None)

    interface INotificationChannel with
        member _.Publish(scope, notification) = async { lock gate (fun () -> published.Add(scope, notification)) }

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()

/// Scanner double that rejects any file whose name contains `poison`.
type private NameBasedScanner() =
    interface IContentScanner with
        member _.Name = "test-name-scanner"

        member _.Scan(_, fileName) = async {
            if fileName.Contains "poison" then
                return ScanRejected "test-signature"
            else
                return ScanClean
        }

/// URL transport double. Records every URL it was asked for — so "inert
/// by default" can be asserted as *the transport was never consulted*,
/// not merely as *the call returned a refusal*.
type private StubFetcher(responses: Map<string, UrlFetchResponse>) =
    let calls = ResizeArray<string>()
    let gate = obj ()

    member _.Calls = lock gate (fun () -> calls |> List.ofSeq)

    interface IUrlContentFetcher with
        member _.Fetch(uri, _, _) = async {
            lock gate (fun () -> calls.Add(uri.ToString()))

            match responses.TryFind(uri.ToString()) with
            | Some response -> return Ok response
            | None -> return Error "stub fetcher has no response for this URL"
        }

/// Deps with every Phase 511 lever exposed. Defaults mirror what an
/// uncomposed deployment resolves in `KnowledgeApiDeps.resolve`, so a
/// test that overrides nothing is testing the shipped default.
let private mkDeps
    (storage: IBlobStorage)
    (notifications: INotificationChannel)
    (container: string)
    (quota: KnowledgeQuotaPolicy)
    (upload: KnowledgeUploadPolicy)
    (scanner: IContentScanner option)
    (archivePolicy: ArchiveImportPolicy)
    (urlPolicy: UrlIngestionPolicy)
    (fetcher: IUrlContentFetcher option)
    : KnowledgeApiDeps =
    {
        Storage = storage
        Queue = IngestionQueue()
        OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
        TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
        Notifications = notifications
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
        EmbeddingProvider = None
        NarrativeStore = None
        AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
        OriginalResolver = createDefault ()
        AuditLog = None
        RecordEnqueue = ignore
        PublishInventory = fun () -> async.Return()
        MarkIngestionFailed = fun _ _ _ -> async.Return()
        EnsureContextWriteAllowed = fun () -> async { return Ok() }
        ScopeResolvedFromRequest = true
        UploadPolicy = upload
        DedupPolicy = KnowledgeDedupPolicy.enabled
        VersioningPolicy = KnowledgeVersioningPolicy.disabled
        // Phase 105 — retention not composed: the pre-105 convention-blob path.
        DataObjectStore = None
        QuotaPolicy = quota
        RetentionPolicy = KnowledgeRetentionPolicy.retainForever
        ContentScanner = scanner
        ScanPolicy = ContentScanPolicy.defaults
        DisclosureGate = None
        ArchiveImportPolicy = archivePolicy
        UrlIngestionPolicy = urlPolicy
        UrlFetcher = fetcher
    }

/// The common shape: nothing composed beyond storage + notifications, so
/// every Phase 511 lever sits at its uncomposed default.
let private defaultDeps (storage: IBlobStorage) (notifications: INotificationChannel) (container: string) =
    mkDeps
        storage
        notifications
        container
        KnowledgeQuotaPolicy.unlimited
        KnowledgeUploadPolicy.permissive
        None
        ArchiveImportPolicy.defaults
        UrlIngestionPolicy.disabled
        None

let private csv (name: string) : byte[] =
    Text.Encoding.UTF8.GetBytes(sprintf "name,score\n%s,1" name)

/// Build a zip in memory. `entries` is (entryName, content) — entry
/// names are written verbatim so a traversal name can be crafted.
let private zipOf (entries: (string * byte[]) list) : byte[] =
    use buffer = new MemoryStream()

    (use archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen = true)

     for name, content in entries do
         let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
         use stream = entry.Open()
         stream.Write(content, 0, content.Length))

    buffer.ToArray()

/// A highly compressible payload — the raw material of a decompression
/// bomb: `n` bytes that zip down to almost nothing.
let private compressible (n: int) : byte[] = Array.zeroCreate<byte> n

let private refusalOf (report: BulkImportReport) (sourceFragment: string) : string option =
    report.Items
    |> List.tryFind (fun i -> i.Source.Contains sourceFragment)
    |> Option.bind (fun i ->
        match i.Outcome with
        | BulkItemOutcome.Refused reason -> Some reason
        | BulkItemOutcome.Admitted doc ->
            match doc.Status with
            | UploadRejected reason -> Some reason
            | _ -> None)

// ─── Phase 725 helpers ───────────────────────────────────────────────

/// Counts `PublishInventory` deliveries. Substituted into `deps` so
/// "one snapshot per batch, not two per item" is measured rather than
/// asserted. Locked because background extraction publishes from the
/// thread pool while the request path publishes from the test thread.
type private InventoryCounter() =
    let gate = obj ()
    let mutable count = 0

    member _.Publish() : Async<unit> = async { lock gate (fun () -> count <- count + 1) }

    member _.Count = lock gate (fun () -> count)

/// The docId a report's admitted item landed under, for reading the
/// stored blob back.
let private docIdOf (report: BulkImportReport) (fileName: string) : string =
    report.Items
    |> List.pick (fun i ->
        match i.Outcome with
        | BulkItemOutcome.Admitted doc when doc.FileName = fileName -> Some doc.Id
        | _ -> None)

/// CRC-32 (IEEE), so the hand-assembled fixture below is well-formed in
/// every respect except the one lie it exists to tell.
let private crc32 (data: byte[]) : uint32 =
    let table =
        Array.init 256 (fun n ->
            let mutable c = uint32 n

            for _ in 0..7 do
                c <-
                    if c &&& 1u <> 0u then
                        0xEDB88320u ^^^ (c >>> 1)
                    else
                        c >>> 1

            c)

    let mutable crc = 0xFFFFFFFFu

    for b in data do
        crc <- table[int ((crc ^^^ uint32 b) &&& 0xFFu)] ^^^ (crc >>> 8)

    crc ^^^ 0xFFFFFFFFu

/// Phase 725.C — a zip whose DECLARED uncompressed size understates the
/// content actually stored.
///
/// **This has to be hand-assembled, and that is the finding.** The BCL
/// `ZipArchive` writer computes and declares honest sizes, so no fixture
/// built with it can put `readBounded`'s overrun arm on a path — every
/// oversized entry is caught by a cheap declared-size pre-filter first,
/// and the guard that actually holds against a hostile archive was
/// unreachable from any test in the repo. An unreachable guard on a
/// hostile-input path is indistinguishable from an absent one.
///
/// STORED (method 0) rather than deflate: the BCL bounds a read-mode
/// entry stream by the CENTRAL DIRECTORY'S compressed size and, for a
/// stored entry, hands those bytes straight back — so `compressedSize`
/// stays honest (the bytes really are there), `uncompressedSize` carries
/// the lie, and nothing about the container is malformed. Every other
/// field is correct, including the CRC, so a refusal can only come from
/// the bounded read.
let private lyingZip (entryName: string) (realContent: byte[]) (declaredSize: uint32) : byte[] =
    let nameBytes = Text.Encoding.ASCII.GetBytes entryName
    let realSize = uint32 realContent.Length
    let crc = crc32 realContent

    use buffer = new MemoryStream()
    use writer = new BinaryWriter(buffer)

    // Local file header.
    writer.Write 0x04034b50u // signature
    writer.Write 20us // version needed
    writer.Write 0us // general-purpose flags
    writer.Write 0us // method: stored
    writer.Write 0us // last-mod time
    writer.Write 33us // last-mod date (1980-01-01)
    writer.Write crc
    writer.Write realSize // compressed size — honest
    writer.Write declaredSize // uncompressed size — THE LIE
    writer.Write(uint16 nameBytes.Length)
    writer.Write 0us // extra-field length
    writer.Write nameBytes
    writer.Write realContent

    let centralDirectoryOffset = uint32 buffer.Position

    // Central-directory file header — the record the BCL reader
    // actually consults, so the lie has to appear here too.
    writer.Write 0x02014b50u // signature
    writer.Write 20us // version made by
    writer.Write 20us // version needed
    writer.Write 0us // general-purpose flags
    writer.Write 0us // method: stored
    writer.Write 0us // last-mod time
    writer.Write 33us // last-mod date
    writer.Write crc
    writer.Write realSize
    writer.Write declaredSize
    writer.Write(uint16 nameBytes.Length)
    writer.Write 0us // extra-field length
    writer.Write 0us // file-comment length
    writer.Write 0us // disk number start
    writer.Write 0us // internal attributes
    writer.Write 0u // external attributes
    writer.Write 0u // relative offset of local header
    writer.Write nameBytes

    let centralDirectorySize = uint32 buffer.Position - centralDirectoryOffset

    // End of central directory.
    writer.Write 0x06054b50u // signature
    writer.Write 0us // this disk
    writer.Write 0us // disk with central directory
    writer.Write 1us // entries on this disk
    writer.Write 1us // entries total
    writer.Write centralDirectorySize
    writer.Write centralDirectoryOffset
    writer.Write 0us // comment length

    writer.Flush()
    buffer.ToArray()

let private importedNames (report: BulkImportReport) =
    report.Items
    |> List.filter (fun i -> BulkItemOutcome.isImported i.Outcome)
    |> List.map _.FileName
    |> List.sort

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 511 — bulk / programmatic KB import" [

        // ── 511.A — the batch surface itself ──

        testCaseAsync "mixed-validity batch reports every item and one bad item does not fail the batch"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()

            let deps =
                mkDeps
                    storage
                    notifications
                    "team-mixed"
                    KnowledgeQuotaPolicy.unlimited
                    // An allowlist that admits csv only — so the .exe
                    // item is refused by the SHIPPED per-item policy, not
                    // by anything the batch surface invented.
                    {
                        KnowledgeUploadPolicy.permissive with
                            AllowedExtensions = Some(Set.ofList [ "csv" ])
                    }
                    None
                    ArchiveImportPolicy.defaults
                    UrlIngestionPolicy.disabled
                    None

            let! report =
                importBatch deps {
                    Sources = [
                        BulkImportSource.File("good-a.csv", csv "a")
                        BulkImportSource.File("blocked.exe", csv "b")
                        BulkImportSource.File("good-b.csv", csv "c")
                        BulkImportSource.Url "https://not-allowed.test/doc.csv"
                    ]
                }

            Expect.equal (List.length report.Items) 4 "every submitted source produces exactly one report line"
            Expect.equal report.Imported 2 "the two admissible csv files imported"
            Expect.equal report.Refused 2 "the disallowed extension and the URL were refused"
            Expect.equal (importedNames report) [ "good-a.csv"; "good-b.csv" ] "the good items are named in the report"

            Expect.isSome (refusalOf report "blocked.exe") "the refused extension carries a reason"
            Expect.isSome (refusalOf report "not-allowed.test") "the refused URL carries a reason"

            // The batch did not abort at the first refusal: `good-b.csv`
            // sits AFTER `blocked.exe` in the submission order.
            let! index = loadIndex storage "team-mixed"
            Expect.equal (List.length index) 2 "both good documents persisted despite an intervening refusal"
        }

        testCaseAsync "a batch publishes exactly ONE completion signal regardless of item count"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-signal"

            let! report =
                importBatch deps {
                    Sources = [
                        for i in 1..6 -> BulkImportSource.File(sprintf "doc-%d.csv" i, csv (string i))
                    ]
                }

            let signals = notifications.OfKey BulkImportNotificationKey

            Expect.equal (List.length signals) 1 "one batch, one completion notification — not one per file"
            Expect.equal report.Imported 6 "all six imported"

            Expect.isNonEmpty report.BatchId "the report carries a batch id correlating it with the notification"
        }

        testCaseAsync "a batch's dedup hits are silent; the single-file path still announces them (GP 11)"
        <| async {
            // The ONE observable behaviour change Phase 511 makes to the
            // shared upload function. Asserted in both directions,
            // because a suppression that also fired on the interactive
            // path would be a silent regression of the pre-511 UX.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let bulkNotes = CapturingNotifications()
            let bulkDeps = defaultDeps storage bulkNotes "team-quiet"

            let! _ =
                importBatch bulkDeps {
                    Sources = [
                        BulkImportSource.File("same.csv", csv "same")
                        BulkImportSource.File("same-again.csv", csv "same")
                    ]
                }

            Expect.isEmpty bulkNotes.SystemMessages "a bulk dedup hit publishes no per-file toast"

            let interactiveNotes = CapturingNotifications()
            let interactiveDeps = defaultDeps storage interactiveNotes "team-loud"

            let! _ = uploadDocument interactiveDeps (csv "loud") "loud.csv"
            let! _ = uploadDocument interactiveDeps (csv "loud") "loud-again.csv"

            Expect.isNonEmpty
                interactiveNotes.SystemMessages
                "the interactive path still announces a dedup hit exactly as it did before Phase 511"
        }

        // ── 511.B — archive expansion + bomb guards ──

        testCaseAsync "a zip of many documents expands and each entry imports"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-zip"

            let archive =
                zipOf [
                    "alpha.csv", csv "alpha"
                    "docs/beta.csv", csv "beta"
                    "docs/nested/gamma.csv", csv "gamma"
                ]

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.Archive("corpus.zip", archive) ]
                }

            Expect.equal report.Imported 3 "all three entries imported"
            Expect.equal report.Refused 0 "nothing refused"

            // Directory components are neutralised — an entry lands under
            // its leaf name, never a path.
            Expect.equal
                (importedNames report)
                [ "alpha.csv"; "beta.csv"; "gamma.csv" ]
                "each entry imports under its leaf name; the directory component is flattened"

            Expect.isTrue
                (report.Items |> List.forall (fun i -> i.Source.StartsWith "corpus.zip →"))
                "every report line names the archive it came from"
        }

        testCaseAsync "a decompression bomb is refused with a classified reason"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-bomb"

            // 8 MB of zeros compresses to a few KB — a ratio far past the
            // default 100:1, while sitting under the entry-count, the
            // per-entry and the total-bytes caps. This is precisely the
            // bomb shape those three caps do NOT catch, which is why the
            // ratio lever exists.
            let bomb = zipOf [ "bomb.csv", compressible 8_388_608 ]

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.Archive("bomb.zip", bomb) ]
                }

            Expect.equal report.Imported 0 "nothing from a bomb is admitted"
            Expect.equal (List.length report.Items) 1 "the archive is refused whole, not entry by entry"

            let reason = refusalOf report "bomb.zip" |> Option.defaultValue ""

            Expect.stringContains
                reason
                "decompression-ratio"
                "the refusal names the guard that fired, not a generic failure"

            let! index = loadIndex storage "team-bomb"
            Expect.isEmpty index "a refused archive persists nothing"
        }

        testCaseAsync "an archive over the entry-count cap is refused whole"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()

            let deps =
                mkDeps
                    storage
                    notifications
                    "team-many"
                    KnowledgeQuotaPolicy.unlimited
                    KnowledgeUploadPolicy.permissive
                    None
                    {
                        ArchiveImportPolicy.defaults with
                            MaxEntries = Some 2
                    }
                    UrlIngestionPolicy.disabled
                    None

            let archive = zipOf [ for i in 1..5 -> sprintf "f%d.csv" i, csv (string i) ]

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.Archive("many.zip", archive) ]
                }

            Expect.equal report.Imported 0 "an over-count archive admits nothing"
            let reason = refusalOf report "many.zip" |> Option.defaultValue ""
            Expect.stringContains reason "5 entries" "the refusal states what was declared"
            Expect.stringContains reason "2-entry" "and the limit it breached"
        }

        testCaseAsync "an oversized entry is refused alone; its siblings still import"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()

            let deps =
                mkDeps
                    storage
                    notifications
                    "team-bigentry"
                    KnowledgeQuotaPolicy.unlimited
                    KnowledgeUploadPolicy.permissive
                    None
                    {
                        ArchiveImportPolicy.defaults with
                            MaxEntryBytes = Some 64L
                    }
                    UrlIngestionPolicy.disabled
                    None

            let archive = zipOf [ "small.csv", csv "s"; "large.csv", compressible 4096 ]

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.Archive("mixed.zip", archive) ]
                }

            Expect.equal
                report.Imported
                1
                "the small entry still imports — a per-entry refusal is not an archive refusal"

            Expect.equal (importedNames report) [ "small.csv" ] "and it is the small one"
            Expect.isSome (refusalOf report "large.csv") "the oversized entry carries its own reason"
        }

        testCaseAsync "a zip-slip entry name is refused, not silently renamed"
        <| async {
            // The classification, not the byte safety, is what is under
            // test. `uploadDocument` would already reduce
            // `../../../etc/passwd` to `passwd` under a server-controlled
            // key — so the file could never escape. What silent
            // flattening would cost is the SIGNAL: a hostile archive
            // would read in the report as an ordinary success.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-slip"

            let archive =
                zipOf [
                    "ok.csv", csv "ok"
                    "../../../etc/passwd.csv", csv "evil"
                    "..\\..\\windows\\system32\\evil.csv", csv "evil"
                    "/absolute/rooted.csv", csv "evil"
                    "C:/drive/qualified.csv", csv "evil"
                ]

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.Archive("slip.zip", archive) ]
                }

            Expect.equal report.Imported 1 "only the benign entry is admitted"
            Expect.equal (importedNames report) [ "ok.csv" ] "and it is the benign one"
            Expect.equal report.Refused 4 "all four traversal shapes are refused"

            for fragment in [ "passwd"; "system32"; "rooted"; "qualified" ] do
                let reason = refusalOf report fragment |> Option.defaultValue ""

                Expect.stringContains
                    reason
                    "path traversal"
                    (sprintf "'%s' is refused by name with a traversal classification" fragment)

            let! index = loadIndex storage "team-slip"
            Expect.equal (List.length index) 1 "no traversal entry reached storage"
        }

        testCase "the traversal classifier covers rooted, drive-qualified and dot-dot names"
        <| fun _ ->
            // The predicate on its own — the falsification target. Flip
            // `isTraversalEntryName` to `false` and both this and the
            // archive case above go red.
            for hostile in
                [
                    "../x.csv"
                    "a/../../x.csv"
                    "..\\x.csv"
                    "/x.csv"
                    "C:/x.csv"
                    "\\\\server\\share\\x.csv"
                    ""
                ] do
                Expect.isTrue (isTraversalEntryName hostile) (sprintf "'%s' escapes its root" hostile)

            for benign in [ "x.csv"; "docs/x.csv"; "a/b/c/x.csv"; "docs\\x.csv"; "..hidden.csv" ] do
                Expect.isFalse (isTraversalEntryName benign) (sprintf "'%s' is an ordinary relative entry" benign)

        testCaseAsync "a payload that is not a zip is refused, not thrown"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-notzip"

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.Archive("not-really.zip", csv "definitely not a zip") ]
                }

            Expect.equal report.Refused 1 "a malformed archive is one refused line"
            let reason = refusalOf report "not-really.zip" |> Option.defaultValue ""
            Expect.stringContains reason "could not be read as a zip" "with a classified reason"
        }

        // ── 511.C — URL ingestion, inert by default ──

        testCaseAsync "URL ingestion is inert unless allowlisted — the transport is never consulted"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()

            let fetcher =
                StubFetcher(Map.ofList [ "https://docs.example.com/a.csv", Body(csv "a") ])

            // A transport IS composed, and the host WOULD have resolved.
            // Only the empty allowlist stands between the two — which is
            // exactly the property under test.
            let deps =
                mkDeps
                    storage
                    notifications
                    "team-inert"
                    KnowledgeQuotaPolicy.unlimited
                    KnowledgeUploadPolicy.permissive
                    None
                    ArchiveImportPolicy.defaults
                    UrlIngestionPolicy.disabled
                    (Some(fetcher :> IUrlContentFetcher))

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.Url "https://docs.example.com/a.csv" ]
                }

            Expect.equal report.Imported 0 "nothing is fetched by default"

            Expect.isEmpty
                fetcher.Calls
                "the gate refuses BEFORE any transport is reached — no request left the process"

            let reason = refusalOf report "docs.example.com" |> Option.defaultValue ""
            Expect.stringContains reason "not enabled" "the refusal says the deployment has not opted in"

            let! index = loadIndex storage "team-inert"
            Expect.isEmpty index "and nothing was stored"
        }

        testCaseAsync "an allowlisted URL is fetched and imports through the ordinary upload path"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()

            let fetcher =
                StubFetcher(Map.ofList [ "https://docs.example.com/a.csv", Body(csv "a") ])

            let deps =
                mkDeps
                    storage
                    notifications
                    "team-url"
                    KnowledgeQuotaPolicy.unlimited
                    KnowledgeUploadPolicy.permissive
                    None
                    ArchiveImportPolicy.defaults
                    (UrlIngestionPolicy.allowingHosts [ "docs.example.com" ])
                    (Some(fetcher :> IUrlContentFetcher))

            let! report =
                importBatch deps {
                    Sources = [
                        BulkImportSource.Url "https://docs.example.com/a.csv"
                        BulkImportSource.Url "https://other.example.com/b.csv"
                    ]
                }

            Expect.equal report.Imported 1 "only the allowlisted host is fetched"
            Expect.equal (importedNames report) [ "a.csv" ] "the file name comes from the URL's last path segment"

            Expect.equal
                fetcher.Calls
                [ "https://docs.example.com/a.csv" ]
                "the non-allowlisted host never reached the transport"

            let! index = loadIndex storage "team-url"
            Expect.equal (List.length index) 1 "the fetched document persisted through the normal path"
        }

        testCase "the URL gate refuses literal IPs, foreign schemes and embedded credentials"
        <| fun _ ->
            // Blunter than a private-range blocklist on purpose: an
            // allowlist is by hostname, so a literal address has no
            // legitimate reason to appear, and enumerating link-local /
            // RFC1918 / unique-local / every IPv6 equivalent is a list
            // that is wrong the moment it is written.
            let policy =
                UrlIngestionPolicy.allowingHosts [ "docs.example.com"; "169.254.169.254"; "127.0.0.1"; "[::1]" ]

            let expectRefused (url: string) (fragment: string) =
                match classifyUrl policy url with
                | Ok _ -> failtestf "'%s' should have been refused" url
                | Error reason ->
                    Expect.stringContains reason fragment (sprintf "'%s' is refused for the stated reason" url)

            // Even explicitly "allowlisted", a literal address is refused.
            expectRefused "http://169.254.169.254/latest/meta-data/" "literal IP"
            expectRefused "http://127.0.0.1/admin" "literal IP"
            expectRefused "http://[::1]/admin" "literal IP"

            expectRefused "file:///etc/passwd" "scheme"
            expectRefused "gopher://docs.example.com/x" "scheme"
            expectRefused "https://docs.example.com@evil.test/x" "credentials"
            expectRefused "https://evil.test/x" "allowlist"
            expectRefused "not-a-url" "absolute URL"

            // Suffix confusion — the reason exact equality is used.
            expectRefused "https://notdocs.example.com/x" "allowlist"
            expectRefused "https://docs.example.com.evil.test/x" "allowlist"

            Expect.isOk (classifyUrl policy "https://docs.example.com/a.csv") "the allowlisted host itself resolves"

            Expect.isOk
                (classifyUrl policy "https://DOCS.EXAMPLE.COM/a.csv")
                "host matching is case-insensitive, as DNS is"

        testCaseAsync "a redirect out of the allowlist is refused rather than followed"
        <| async {
            // The canonical SSRF bypass: an allowlisted host 302s to the
            // cloud metadata endpoint. Every hop is re-gated, so the
            // allowlist cannot be escaped by redirection.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()

            let fetcher =
                StubFetcher(
                    Map.ofList [
                        "https://docs.example.com/meta", Redirect "http://169.254.169.254/latest/meta-data/"
                        "https://docs.example.com/away", Redirect "https://evil.test/payload.csv"
                        "https://docs.example.com/ok", Redirect "https://docs.example.com/final.csv"
                        "https://docs.example.com/final.csv", Body(csv "final")
                    ]
                )

            let deps =
                mkDeps
                    storage
                    notifications
                    "team-redirect"
                    KnowledgeQuotaPolicy.unlimited
                    KnowledgeUploadPolicy.permissive
                    None
                    ArchiveImportPolicy.defaults
                    (UrlIngestionPolicy.allowingHosts [ "docs.example.com" ])
                    (Some(fetcher :> IUrlContentFetcher))

            let! report =
                importBatch deps {
                    Sources = [
                        BulkImportSource.Url "https://docs.example.com/meta"
                        BulkImportSource.Url "https://docs.example.com/away"
                        BulkImportSource.Url "https://docs.example.com/ok"
                    ]
                }

            Expect.equal report.Imported 1 "only the redirect that stayed inside the allowlist completed"
            Expect.equal (importedNames report) [ "final.csv" ] "and it delivered the final hop's content"

            Expect.stringContains
                (refusalOf report "/meta" |> Option.defaultValue "")
                "literal IP"
                "a redirect to the metadata endpoint is refused by the same literal-IP rule"

            Expect.stringContains
                (refusalOf report "/away" |> Option.defaultValue "")
                "allowlist"
                "a redirect to an unlisted host is refused by the same allowlist"

            Expect.isFalse
                (fetcher.Calls
                 |> List.exists (fun c -> c.Contains "169.254" || c.Contains "evil.test"))
                "neither refused target was ever requested"
        }

        // ── The per-item claim: landed policies still apply ──

        testCaseAsync "the corpus quota is enforced per item, so a batch cannot overshoot it"
        <| async {
            // The reason items are admitted sequentially. The quota is a
            // read-then-persist decision, so N concurrent items would each
            // see the same pre-batch headroom and the batch would overshoot
            // by N-1.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()

            let deps =
                mkDeps
                    storage
                    notifications
                    "team-quota"
                    {
                        MaxDocuments = Some 2
                        MaxBytes = None
                    }
                    KnowledgeUploadPolicy.permissive
                    None
                    ArchiveImportPolicy.defaults
                    UrlIngestionPolicy.disabled
                    None

            let! report =
                importBatch deps {
                    Sources = [ for i in 1..5 -> BulkImportSource.File(sprintf "q%d.csv" i, csv (string i)) ]
                }

            Expect.equal report.Imported 2 "the batch stops admitting at the scope's document cap"
            Expect.equal report.Refused 3 "and the rest are refused, each with the quota reason"

            Expect.stringContains
                (refusalOf report "q5.csv" |> Option.defaultValue "")
                "document quota"
                "the refusal is the shipped Phase 512 message, not a bulk-specific one"

            let! index = loadIndex storage "team-quota"
            Expect.equal (List.length index) 2 "the corpus never exceeds its cap"
        }

        testCaseAsync "the content scanner runs on every item, including archive entries"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()

            let deps =
                mkDeps
                    storage
                    notifications
                    "team-scan"
                    KnowledgeQuotaPolicy.unlimited
                    KnowledgeUploadPolicy.permissive
                    (Some(NameBasedScanner() :> IContentScanner))
                    ArchiveImportPolicy.defaults
                    UrlIngestionPolicy.disabled
                    None

            // Every payload is byte-DISTINCT on purpose. Identical
            // content would dedup, and a deduped item reports the FIRST
            // document's file name — which reads exactly like the scanner
            // having admitted the wrong file. (It is not hypothetical:
            // this test failed that way on its first run.)
            let archive =
                zipOf [ "clean-entry.csv", csv "clean-entry"; "poison-entry.csv", csv "bad-entry" ]

            let! report =
                importBatch deps {
                    Sources = [
                        BulkImportSource.File("clean-direct.csv", csv "clean-direct")
                        BulkImportSource.File("poison-direct.csv", csv "bad-direct")
                        BulkImportSource.Archive("mixed.zip", archive)
                    ]
                }

            Expect.equal
                (importedNames report)
                [ "clean-direct.csv"; "clean-entry.csv" ]
                "only the clean items — from BOTH the direct and the expanded-archive paths — are admitted"

            for fragment in [ "poison-direct.csv"; "poison-entry.csv" ] do
                Expect.stringContains
                    (refusalOf report fragment |> Option.defaultValue "")
                    "test-signature"
                    (sprintf "'%s' carries the scanner's own reason" fragment)

            let! index = loadIndex storage "team-scan"

            Expect.equal
                (List.length index)
                2
                "a scanned-out archive entry never reaches the blob container, exactly as a scanned-out upload does not"
        }

        testCaseAsync "dedup applies across a batch, so a duplicated archive entry costs one document"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-dedup"

            let shared = csv "identical"

            let archive =
                zipOf [ "first.csv", shared; "second-copy.csv", shared; "different.csv", csv "other" ]

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.Archive("dupes.zip", archive) ]
                }

            let ids =
                report.Items
                |> List.choose (fun i ->
                    match i.Outcome with
                    | BulkItemOutcome.Admitted doc -> Some doc.Id
                    | _ -> None)
                |> List.distinct

            Expect.equal (List.length ids) 2 "the two byte-identical entries resolve to the same document id"

            let! index = loadIndex storage "team-dedup"
            Expect.equal (List.length index) 2 "and only two documents are stored"
        }

        testCaseAsync "an empty batch is a valid no-op"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-empty"

            let! report = importBatch deps { Sources = [] }

            Expect.isEmpty report.Items "no items"
            Expect.equal report.Imported 0 "nothing imported"
            Expect.equal report.Refused 0 "nothing refused"

            Expect.equal
                (List.length (notifications.OfKey BulkImportNotificationKey))
                1
                "the completion signal still fires — an empty result is a result, not a silence"
        }

        testCase "report counts are derived from the items and cannot disagree with them"
        <| fun _ ->
            let now = DateTimeOffset.UtcNow

            let doc: KnowledgeDocument = {
                Id = "d1"
                FileName = "a.csv"
                FileType = "csv"
                UploadedAt = now
                UploadedBy = "u"
                Status = Queued
                SizeBytes = 10L
                ChunkCount = 0
                Source = UploadedFile
                ContentHash = None
                Version = 1
                // Phase 502.C — untagged fixture.
                Tags = []
            }

            let report =
                BulkImportReport.ofItems "batch" now now [
                    {
                        Source = "a.csv"
                        FileName = "a.csv"
                        Outcome = BulkItemOutcome.Admitted doc
                    }
                    {
                        Source = "b.csv"
                        FileName = "b.csv"
                        Outcome =
                            BulkItemOutcome.Admitted {
                                doc with
                                    Status = UploadRejected "nope"
                            }
                    }
                    {
                        Source = "c.zip"
                        FileName = "c.zip"
                        Outcome = BulkItemOutcome.Refused "bomb"
                    }
                ]

            Expect.equal report.Imported 1 "an UploadRejected document counts as refused, not imported"
            Expect.equal report.Refused 2 "both refusal shapes are folded together"

            Expect.equal
                (report.Imported + report.Refused)
                (List.length report.Items)
                "the counts partition the items exactly"

        // ══ Phase 725 — bulk-import hardening ══════════════════════
        //
        // Four follow-ons Phase 511 deliberately left outside its lease.
        // None changes an admission decision; each closes a cost or a
        // gap that only a corpus-scale import makes visible.

        // ── 725.A — batch-scoped inventory suppression ──

        testCaseAsync "a batch publishes ONE inventory snapshot, not two per item"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let publishes = InventoryCounter()

            let deps = {
                defaultDeps storage notifications "team-inventory" with
                    PublishInventory = publishes.Publish
            }

            // 25 items. The pre-725 code publishes once per item on the
            // REQUEST path alone — synchronously, before `importBatch`
            // returns — so the old floor here is 25 and the assertion
            // below separates the two behaviours by an order of
            // magnitude rather than by one.
            let! report =
                importBatch deps {
                    Sources = [
                        for i in 1..25 -> BulkImportSource.File(sprintf "doc-%d.csv" i, csv (string i))
                    ]
                }

            // Sampled the instant the batch returns. Extraction runs off
            // the request path (`Async.Start`), so a background publish
            // can land after `Close` has already passed the window —
            // those are DELIBERATELY not suppressed (an extraction
            // finishing long after the import must still refresh the
            // inventory). The bound absorbs a couple of those and still
            // fails hard on the old shape.
            let delivered = publishes.Count

            Expect.equal (List.length (importedNames report)) 25 "every item was admitted"

            Expect.isLessThanOrEqual
                delivered
                5
                (sprintf "a 25-item batch coalesces to ~1 inventory snapshot, not ~50 (delivered %d)" delivered)

            Expect.isGreaterThan delivered 0 "the batch still publishes the one snapshot at its end"
        }

        testCaseAsync "an all-refused batch publishes no inventory snapshot at all"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let publishes = InventoryCounter()

            let deps = {
                mkDeps
                    storage
                    notifications
                    "team-refused"
                    KnowledgeQuotaPolicy.unlimited
                    {
                        KnowledgeUploadPolicy.permissive with
                            AllowedExtensions = Some(Set.ofList [ "csv" ])
                    }
                    None
                    ArchiveImportPolicy.defaults
                    UrlIngestionPolicy.disabled
                    None with
                    PublishInventory = publishes.Publish
            }

            let! report =
                importBatch deps {
                    Sources = [
                        for i in 1..4 -> BulkImportSource.File(sprintf "blocked-%d.exe" i, csv (string i))
                    ]
                }

            Expect.equal (importedNames report) [] "nothing was admitted"

            // Nothing mutated the inventory, so there is nothing to
            // re-announce — the gate publishes on close only if an item
            // actually asked. Fully deterministic (a refusal spawns no
            // background extraction at all), so this arm pins the
            // semantics the timing-tolerant one above can only bound.
            Expect.equal publishes.Count 0 "a batch that persisted nothing publishes nothing"
        }

        testCaseAsync "the interactive single-file upload still publishes on the request path (GP 11)"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let publishes = InventoryCounter()

            let deps = {
                defaultDeps storage notifications "team-interactive" with
                    PublishInventory = publishes.Publish
            }

            // `uploadDocument` takes the caller's deps, never a gated
            // copy — which is the whole reason the suppression is a
            // substituted seam per batch rather than a change to
            // `persistAndIngest`.
            let! _ = uploadDocument deps (csv "solo") "solo.csv"

            Expect.isGreaterThan
                publishes.Count
                0
                "the pre-725 interactive path is unchanged: it publishes as it always did"
        }

        // ── 725.B — a compact transport for nested byte[] ──

        testCaseAsync "the base64 file case admits exactly what the byte[] case does"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-b64"
            let payload = csv "compact"

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.ofFileBytes "compact.csv" payload ]
                }

            Expect.equal (importedNames report) [ "compact.csv" ] "the compact case is admitted"

            // Not merely "a document appeared" — the bytes that reached
            // storage are the bytes submitted. A base64 shape that
            // round-tripped wrong would still produce a green import.
            let! stored =
                storage.Download("team-b64", sprintf "knowledge/%s/compact.csv" (docIdOf report "compact.csv"))

            match stored with
            | Ok bytes -> Expect.equal bytes payload "the decoded payload is byte-identical to the submitted bytes"
            | Error reason -> failtestf "the compact case persisted nothing readable: %s" reason
        }

        testCaseAsync "the base64 archive case expands under the same policy as the byte[] case"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-b64-zip"

            let archive = zipOf [ "docs/a.csv", csv "a"; "docs/b.csv", csv "b" ]

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.ofArchiveBytes "corpus.zip" archive ]
                }

            Expect.equal (importedNames report) [ "a.csv"; "b.csv" ] "both entries expanded and were admitted"
        }

        testCaseAsync "malformed base64 is one classified refusal, not a failed batch"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()
            let deps = defaultDeps storage notifications "team-b64-bad"

            let! report =
                importBatch deps {
                    Sources = [
                        BulkImportSource.ofFileBytes "good.csv" (csv "good")
                        BulkImportSource.FileBase64("broken.csv", "not!valid!base64!")
                        BulkImportSource.ofFileBytes "also-good.csv" (csv "also")
                    ]
                }

            Expect.equal
                (importedNames report)
                [ "also-good.csv"; "good.csv" ]
                "the two well-formed items are unaffected — one bad item never fails the batch (511.A)"

            match refusalOf report "broken.csv" with
            | Some reason -> Expect.stringContains reason "base64" "the refusal names the transport, not a BCL message"
            | None -> failtest "the malformed base64 item produced no classified refusal"
        }

        test "the compact shape is a string for a reason: base64 beats the numeric-array encoding" {
            // The cost 725.B closes is not the .NET host's — server-side
            // `ByteArrayConverter` already writes base64. It is the
            // FABLE host's, where a `byte[]` is emitted as `[n, n, ...]`.
            // A `string` field has no such fork, and this pins the
            // margin rather than restating the claim.
            let payload = Array.init 4096 (fun i -> byte ((i * 37 + 11) % 256))
            let base64Length = (Convert.ToBase64String payload).Length

            let numericArrayLength =
                let body = payload |> Array.map string |> String.concat ","
                body.Length + 2

            Expect.isLessThan
                (base64Length * 2)
                numericArrayLength
                (sprintf
                    "base64 (%d chars) is well under half the numeric-array form (%d chars) for the same %d bytes"
                    base64Length
                    numericArrayLength
                    payload.Length)
        }

        // ── 725.C — the lying-zip fixture readBounded exists for ──

        test "the lying-zip fixture is a well-formed zip whose declared size understates its content" {
            // Verify the PROBE before trusting what it proves. A fixture
            // the BCL could not open would be refused by
            // `expandArchive`'s "could not be read as a zip" arm, and the
            // overrun test below would pass without ever reaching
            // `readBounded` — green, and vacuous. So: the archive opens,
            // the entry declares the lie, and the entry stream really
            // does yield the full content.
            let realContent = compressible 200_000
            let archive = lyingZip "big.bin" realContent 10u

            use stream = new MemoryStream(archive, writable = false)
            use zip = new ZipArchive(stream, ZipArchiveMode.Read)

            let entry = zip.Entries |> Seq.exactlyOne
            Expect.equal entry.Length 10L "the archive DECLARES 10 uncompressed bytes"

            use entryStream = entry.Open()
            use drained = new MemoryStream()
            entryStream.CopyTo drained

            Expect.equal
                drained.Length
                200_000L
                "...and the entry stream nevertheless yields 200,000 — which is the whole hazard"
        }

        testCaseAsync "an entry that streams more than it declares is refused by the bounded read"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let notifications = CapturingNotifications()

            // `readBounded` is the guard that actually holds, and until
            // now nothing in the repo could exercise its overrun arm:
            // the BCL `ZipArchive` WRITER always declares honest sizes,
            // so every in-repo archive made the declared-size pre-filters
            // correct and the bounded read redundant. This fixture is
            // hand-assembled precisely so the pre-filters pass and only
            // the read can refuse it.
            let deps =
                mkDeps
                    storage
                    notifications
                    "team-lying-zip"
                    KnowledgeQuotaPolicy.unlimited
                    KnowledgeUploadPolicy.permissive
                    None
                    {
                        ArchiveImportPolicy.defaults with
                            MaxEntryBytes = Some 1_000L
                            MaxTotalUncompressedBytes = Some 5_000L
                            // Off deliberately: the ratio lever reads the
                            // DECLARED total against the archive length,
                            // so leaving it on refuses this archive at
                            // the whole-archive stage and the per-entry
                            // read never runs.
                            MaxCompressionRatio = None
                    }
                    UrlIngestionPolicy.disabled
                    None

            let archive = lyingZip "big.bin" (compressible 200_000) 10u

            let! report =
                importBatch deps {
                    Sources = [ BulkImportSource.Archive("liar.zip", archive) ]
                }

            Expect.equal (importedNames report) [] "nothing from a lying archive is admitted"

            match refusalOf report "big.bin" with
            | Some reason ->
                Expect.stringContains
                    reason
                    "streamed more than"
                    "the refusal is the bounded read's, not a declared-size pre-filter's"

                Expect.stringContains reason "declared size understated" "and it names why the pre-filters passed"
            | None -> failtest "the lying entry was not refused — the overrun arm did not fire"
        }

        // ── 725.D — compose-time preflight for the bulk-import surface ──

        test "unguardedLevers names exactly what a policy leaves off" {
            Expect.equal
                (ArchiveImportPolicy.unguardedLevers ArchiveImportPolicy.defaults)
                []
                "the shipped defaults guard every lever"

            Expect.equal
                (ArchiveImportPolicy.unguardedLevers ArchiveImportPolicy.unbounded)
                [
                    "MaxEntries"
                    "MaxEntryBytes"
                    "MaxTotalUncompressedBytes"
                    "MaxCompressionRatio"
                ]
                "unbounded turns all four off, and the validator can name each"

            Expect.isTrue
                (ArchiveImportPolicy.isUnguarded {
                    ArchiveImportPolicy.defaults with
                        MaxCompressionRatio = None
                })
                "clearing only the ratio lever is still unguarded — that is the lever catching a bomb of modest entries"
        }

        testCaseAsync "composing ArchiveImportPolicy.unbounded warns at compose time, naming the posture"
        <| async {
            let app = ServerApp.empty |> withArchiveImportPolicy ArchiveImportPolicy.unbounded

            let validator =
                app.ConfigValidators
                |> List.find (fun v -> v.Name = "knowledge-base:archive-import-policy")

            match! validator.Validate() with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "MaxCompressionRatio" "the warning names the levers that are off"
                Expect.stringContains message "decompression bomb" "and says what the posture admits"
            | other -> failtestf "expected a Warning naming the unguarded posture, got %A" other
        }

        testCaseAsync "composing the shipped archive defaults warns about nothing"
        <| async {
            let app = ServerApp.empty |> withArchiveImportPolicy ArchiveImportPolicy.defaults

            let validator =
                app.ConfigValidators
                |> List.find (fun v -> v.Name = "knowledge-base:archive-import-policy")

            match! validator.Validate() with
            | ConfigValidation.ValidationResult.Ok -> ()
            | other -> failtestf "a guarded policy must be silent, got %A" other
        }

        test "isBroadAllowlist trips at the reviewability threshold, never on an inert policy" {
            let hosts n =
                UrlIngestionPolicy.allowingHosts [ for i in 1..n -> sprintf "host-%d.example.com" i ]

            Expect.isFalse
                (UrlIngestionPolicy.isBroadAllowlist UrlIngestionPolicy.disabled)
                "an inert policy is never broad"

            Expect.isFalse
                (UrlIngestionPolicy.isBroadAllowlist (hosts (UrlIngestionPolicy.BroadAllowlistThreshold - 1)))
                "one below the threshold is not broad"

            Expect.isTrue
                (UrlIngestionPolicy.isBroadAllowlist (hosts UrlIngestionPolicy.BroadAllowlistThreshold))
                "the threshold itself is broad"
        }

        testCaseAsync "a broad URL allowlist warns at compose time, and an inert one does not"
        <| async {
            let validatorFor (policy: UrlIngestionPolicy) =
                (ServerApp.empty |> withUrlIngestion policy).ConfigValidators
                |> List.find (fun v -> v.Name = "knowledge-base:url-ingestion")

            let broad =
                UrlIngestionPolicy.allowingHosts [
                    for i in 1 .. UrlIngestionPolicy.BroadAllowlistThreshold -> sprintf "docs-%d.example.com" i
                ]

            match! (validatorFor broad).Validate() with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "reviewability threshold" "the warning names the posture it found"
                Expect.stringContains message "allowingHosts" "and points at the lever that narrows it"
            | other -> failtestf "expected a Warning for a broad allowlist, got %A" other

            // Composing `withUrlIngestion` with an EMPTY allowlist is a
            // legitimate fail-closed shape (a deployment wiring it from
            // configuration that came back empty) and can fetch nothing,
            // so it is not a posture worth a preflight line.
            match! (validatorFor UrlIngestionPolicy.disabled).Validate() with
            | ConfigValidation.ValidationResult.Ok -> ()
            | other -> failtestf "an inert allowlist must be silent, got %A" other
        }

        testCaseAsync "URL ingestion in a Team deployment warns that the egress surface is shared"
        <| async {
            let teamApp = {
                ServerApp.empty with
                    Config = {
                        ServerApp.empty.Config with
                            Surfaces = [ SurfaceProfile.team ]
                    }
            }

            let app =
                teamApp
                |> withUrlIngestion (UrlIngestionPolicy.allowingHosts [ "docs.example.com" ])

            let validator =
                app.ConfigValidators
                |> List.find (fun v -> v.Name = "knowledge-base:url-ingestion")

            match! validator.Validate() with
            | ConfigValidation.ValidationResult.Warning message ->
                // One allowlisted host is nowhere near "broad", so this
                // can only have fired on the deployment shape — the same
                // Team / MultiTeam gate the upload and quota validators
                // use.
                Expect.stringContains message "Team / MultiTeam" "the warning names the deployment shape"

                Expect.isFalse
                    (message.Contains "reviewability threshold")
                    "and not the breadth finding, which does not apply here"
            | other -> failtestf "expected a Warning for shared-tenant egress, got %A" other
        }
    ]