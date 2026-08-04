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
    ]