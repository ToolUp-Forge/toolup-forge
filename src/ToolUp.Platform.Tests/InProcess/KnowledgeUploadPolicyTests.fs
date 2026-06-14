module ToolUp.Platform.Tests.InProcess.KnowledgeUploadPolicyTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 119 — KnowledgeUploadPolicy enforcement ─────────────────
//
// Pins the upload-boundary policy gate: size cap + extension allowlist +
// unsupported-type handling (all opt-in), and the always-on filename
// sanitisation that keeps a client-supplied name from escaping the
// server-controlled blob key. A rejected upload persists nothing and
// returns a typed `UploadRejected` status; an unrecognised-but-accepted
// type lands `UnsupportedFormat` ("stored, not searchable") rather than
// the misleading `Complete 0`.

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private noopNotifications =
    { new INotificationChannel with
        member _.Publish(_, _) = async.Return()

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()
    }

let private mkDeps (storage: IBlobStorage) (policy: KnowledgeUploadPolicy) (container: string) : KnowledgeApiDeps = {
    Storage = storage
    Queue = IngestionQueue()
    OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
    TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
    Notifications = noopNotifications
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
    UploadPolicy = policy
}

let private isRejected =
    function
    | UploadRejected _ -> true
    | _ -> false

/// Poll the persisted index until the document reaches a terminal status
/// (extraction runs off the request path via `Async.Start`).
let rec private waitTerminal (deps: KnowledgeApiDeps) (docId: string) (attempts: int) = async {
    let! idx = loadIndex deps.Storage deps.Scope.Container

    match idx |> List.tryFind (fun d -> d.Id = docId) with
    | Some d ->
        match d.Status with
        | Complete _
        | Failed _
        | UnsupportedFormat _
        | UploadRejected _ -> return Some d.Status
        | _ when attempts <= 0 -> return Some d.Status
        | _ ->
            do! Async.Sleep 50
            return! waitTerminal deps docId (attempts - 1)
    | None -> return None
}

let tests =
    testList "Phase 119 — KnowledgeUploadPolicy" [

        testCaseAsync
            "filename sanitisation: '../../index.json' stores under a server-controlled key and never clobbers the index"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let deps = mkDeps storage KnowledgeUploadPolicy.permissive "team-a"

            let! doc = uploadDocument deps (Text.Encoding.UTF8.GetBytes "name,score\na,1") "../../index.json"

            // Sanitised: the directory traversal is stripped to the bare name.
            Expect.equal doc.FileName "index.json" "stored display name is Path.GetFileName of the input"
            Expect.isFalse (isRejected doc.Status) "a path-bearing name sanitises rather than rejecting"

            // The KB index (knowledge/index.json) loads the document — proof
            // the raw upload did NOT overwrite it with file bytes.
            let! idx = loadIndex storage "team-a"

            Expect.equal
                (idx |> List.map _.Id)
                [ doc.Id ]
                "the index still holds exactly the uploaded doc — not clobbered"

            // The raw blob lives under the docId prefix, not at the root.
            let! raw = storage.Download("team-a", sprintf "knowledge/%s/index.json" doc.Id)

            Expect.isTrue
                (raw |> Result.isOk)
                "raw bytes stored under knowledge/{docId}/index.json (server-controlled key)"
        }

        testCaseAsync "size cap: an oversize upload is rejected before any write"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let policy = {
                KnowledgeUploadPolicy.permissive with
                    MaxUploadBytes = Some 8L
            }

            let deps = mkDeps storage policy "team-a"

            let! doc = uploadDocument deps (Text.Encoding.UTF8.GetBytes "way past the eight-byte cap") "big.pdf"

            Expect.isTrue (isRejected doc.Status) "oversize upload yields UploadRejected"

            let! idx = loadIndex storage "team-a"
            Expect.isEmpty idx "nothing is persisted to the index on rejection"

            let! blobs = storage.List("team-a", "knowledge/")
            Expect.isEmpty blobs "no blob is written on rejection"
        }

        testCaseAsync "allowlist: a disallowed extension is rejected before any write"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let policy = {
                KnowledgeUploadPolicy.permissive with
                    AllowedExtensions = Some(Set.ofList [ "pdf" ])
            }

            let deps = mkDeps storage policy "team-a"

            let! doc = uploadDocument deps (Text.Encoding.UTF8.GetBytes "a,b\n1,2") "data.csv"

            Expect.isTrue (isRejected doc.Status) "extension outside the allowlist yields UploadRejected"

            let! blobs = storage.List("team-a", "knowledge/")
            Expect.isEmpty blobs "no blob is written on a disallowed-type rejection"
        }

        testCaseAsync "unsupported type + Reject policy: rejected before any write"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let policy = {
                KnowledgeUploadPolicy.permissive with
                    OnUnsupportedType = Reject
            }

            let deps = mkDeps storage policy "team-a"

            let! doc = uploadDocument deps (Text.Encoding.UTF8.GetBytes "binary-ish") "model.bin"

            Expect.isTrue (isRejected doc.Status) "an unrecognised type is rejected when the policy says Reject"

            let! blobs = storage.List("team-a", "knowledge/")
            Expect.isEmpty blobs "no blob is written"
        }

        testCaseAsync
            "unsupported type + AcceptUnindexed (default): stored but flagged UnsupportedFormat, not Complete 0"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let deps = mkDeps storage KnowledgeUploadPolicy.permissive "team-a"

            let! doc = uploadDocument deps (Text.Encoding.UTF8.GetBytes "some bytes") "notes.xyz"

            Expect.isFalse (isRejected doc.Status) "the permissive default stores rather than rejecting"

            // Extraction runs off the request path; wait for the terminal status.
            let! terminal = waitTerminal deps doc.Id 100

            match terminal with
            | Some(UnsupportedFormat _) -> ()
            | other -> failtest $"expected UnsupportedFormat terminal status, got %A{other}"
        }

        testCase "validator predicate: warns only when Team-scoped, uncapped, and not explicitly accepted"
        <| fun _ ->
            let uncapped = KnowledgeUploadPolicy.permissive

            let capped = {
                KnowledgeUploadPolicy.permissive with
                    MaxUploadBytes = Some 1024L
            }

            let accepted = {
                KnowledgeUploadPolicy.permissive with
                    AcceptUnboundedUploads = true
            }

            Expect.isTrue
                (KnowledgeUploadPolicy.warnsUncappedInTeamMode true uncapped)
                "Team-scoped + uncapped + not accepted → warn"

            Expect.isFalse
                (KnowledgeUploadPolicy.warnsUncappedInTeamMode false uncapped)
                "non-team deployment → no warning"

            Expect.isFalse (KnowledgeUploadPolicy.warnsUncappedInTeamMode true capped) "a cap silences the warning"

            Expect.isFalse
                (KnowledgeUploadPolicy.warnsUncappedInTeamMode true accepted)
                "explicit AcceptUnboundedUploads silences the warning"
    ]