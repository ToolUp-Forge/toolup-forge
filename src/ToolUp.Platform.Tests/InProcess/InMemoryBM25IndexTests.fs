module ToolUp.Platform.Tests.InProcess.InMemoryBM25IndexTests

open System
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.RAG.InMemoryBM25Index

// ─── InMemoryBM25Index snapshot persistence ───────────────────────
//
// Regression pack for the "Corrupt index … starting empty" defect: the
// index's `DocEntry` / `ScopeSnapshot` types are `private` records marked
// `[<CLIMutable>]`, and `FSharpCliMutableRecordConverter` previously
// (a) enumerated properties with `BindingFlags.Public` only — a private
//     record's getters are assembly-level, so the persisted snapshot
//     serialised as `{}`; and
// (b) constructed via `Activator.CreateInstance(typeof<'T>, args)`,
//     which binds public constructors only — a private record's
//     constructor is assembly-level, so every load threw
//     `MissingMethodException: Constructor on type … not found` and the
//     index misdiagnosed a healthy snapshot as corrupt, re-ingesting the
//     whole corpus on every boot.
//
// The converter-level round-trip below reproduces the shape directly
// (the index's own types are private to its module, by design); the
// index-level tests drive the real save → load path end to end.

[<CLIMutable>]
type private PrivateDocEntry = {
    ChunkId: string
    Length: int
    Tokens: string array
    Content: string
    Metadata: Map<string, string>
}

[<CLIMutable>]
type private PrivateSnapshot = { Docs: PrivateDocEntry array }

let private jsonOptions = FableConverters.create ()

/// ILogger double that records Warn / Error lines for assertion.
type private CapturingLogger() =
    let warnings = ResizeArray<string>()
    let errors = ResizeArray<string>()

    member _.Warnings = List.ofSeq warnings
    member _.Errors = List.ofSeq errors

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Add message
        member _.Error(message, _) = errors.Add message

let private chunk content metadata : TextChunk = {
    Content = content
    Metadata = metadata
}

let private deploymentBlobName = "_rag/deployment/bm25.json"

[<Tests>]
let tests =
    testList "InMemoryBM25Index persistence" [
        test "private CLIMutable record round-trips through FableConverters" {
            let original = {
                Docs = [|
                    {
                        ChunkId = "chunk-1"
                        Length = 3
                        Tokens = [| "alpha"; "beta"; "gamma" |]
                        Content = "alpha beta gamma"
                        Metadata = Map [ "origin", "test" ]
                    }
                |]
            }

            let json = JsonSerializer.Serialize(original, jsonOptions)

            Expect.stringContains
                json
                "chunk-1"
                "serialised snapshot must contain the document payload (a `{}` here means the \
                 converter's property enumeration missed the private record's assembly-level getters)"

            let roundTripped = JsonSerializer.Deserialize<PrivateSnapshot>(json, jsonOptions)

            Expect.equal roundTripped original "round-trip through FableConverters must preserve the snapshot"
        }

        testAsync "snapshot survives restart: save → load round-trip via IBlobStorage" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            // First boot: ingest two chunks, then dispose (final flush persists).
            do
                use index = new InMemoryBM25Index(storage, flushIntervalMs = 60000)
                let sparse = index :> ISparseIndex

                sparse.Upsert Deployment "doc-1" (chunk "the quick brown fox" (Map [ "k", "v" ]))
                |> Async.RunSynchronously

                sparse.Upsert Deployment "doc-2" (chunk "lazy dogs sleep all day" Map.empty)
                |> Async.RunSynchronously

            // The persisted blob must actually carry the corpus.
            let! blob = storage.Download("_rag", deploymentBlobName)

            match blob with
            | Error e -> failtest $"snapshot blob was not persisted: {e}"
            | Ok bytes ->
                let json = Encoding.UTF8.GetString bytes

                Expect.stringContains
                    json
                    "quick brown fox"
                    "persisted snapshot must contain document content, not an empty object"

            // Second boot over the same storage: the snapshot must load —
            // no "Corrupt index" warning, and search hits without re-ingestion.
            let logger = CapturingLogger()

            use index2 =
                new InMemoryBM25Index(storage, logger = logger, flushIntervalMs = 60000)

            let sparse2 = index2 :> ISparseIndex

            let! results = sparse2.Search [ Deployment ] "quick fox" 10

            Expect.isNonEmpty results "restarted index must answer queries from the loaded snapshot"

            Expect.equal
                (results |> List.map _.ChunkId)
                [ "doc-1" ]
                "the matching chunk must come back from the persisted snapshot"

            Expect.isEmpty
                (logger.Warnings |> List.filter (fun w -> w.Contains "Corrupt index"))
                "loading a healthy snapshot must not be misdiagnosed as corrupt"
        }

        testAsync "legacy empty-object blob loads as an empty index without crashing" {
            // Blobs written by the broken Write path contain `{}`. They must
            // fall through to an empty index (one more re-ingestion), not crash.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let! _ = storage.Upload("_rag", deploymentBlobName, Encoding.UTF8.GetBytes "{}")

            use index = new InMemoryBM25Index(storage, flushIntervalMs = 60000)
            let! results = (index :> ISparseIndex).Search [ Deployment ] "anything" 10

            Expect.isEmpty results "legacy empty-object snapshot must yield an empty index"
        }

        testAsync "genuinely corrupt blob degrades to starting-empty with a warning" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let! _ = storage.Upload("_rag", deploymentBlobName, Encoding.UTF8.GetBytes "not json {{{")

            let logger = CapturingLogger()

            use index = new InMemoryBM25Index(storage, logger = logger, flushIntervalMs = 60000)
            let! results = (index :> ISparseIndex).Search [ Deployment ] "anything" 10

            Expect.isEmpty results "corrupt snapshot must yield an empty index"

            Expect.isNonEmpty
                (logger.Warnings |> List.filter (fun w -> w.Contains "Corrupt index"))
                "corrupt snapshot must be surfaced with the starting-empty warning"
        }
    ]