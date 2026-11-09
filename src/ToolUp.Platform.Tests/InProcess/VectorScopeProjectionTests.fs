module ToolUp.Platform.Tests.InProcess.VectorScopeProjectionTests

// The three renderings of a `VectorScope` (store key, storage container,
// audit stream) and the chunk-id grammar are each written once, in Core.
// These pins hold their bytes to the spellings already at rest: a store
// blob `_rag/{key}/…`, a container `team-{id}` / `user-{id}`, an audit
// stream, and a chunk id `{documentId}:chunk:{ordinal}`.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.ISparseIndex
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private every = [
    Platform, "platform"
    Deployment, "deployment"
    Team "acme", "team:acme"
    VectorScope.User "u1", "user:u1"
]

let private keyTests =
    testList "the store key" [
        test "every scope renders the key its store blobs and scope column are named by" {
            for scope, expected in every do
                Expect.equal (VectorScope.key scope) expected (sprintf "%A" scope)
        }

        test "tryOfKey is the inverse of key" {
            for scope, _ in every do
                Expect.equal (VectorScope.tryOfKey (VectorScope.key scope)) (Some scope) (sprintf "%A" scope)
        }

        test "a string key does not produce reads as no scope" {
            for foreign in [ null; ""; "team-acme"; "Platform"; "org:acme"; "_platform" ] do
                Expect.isNone (VectorScope.tryOfKey foreign) (sprintf "%A is not a store key" foreign)
        }

        testCaseAsync "the in-memory stores persist under the same blob names as before"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let chunk = {
                Content = "quarterly revenue"
                Metadata = Map.empty
            }

            do! async {
                use vectors =
                    new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(storage, flushIntervalMs = 60000)

                use keywords =
                    new ToolUp.RAG.InMemoryBM25Index.InMemoryBM25Index(storage, flushIntervalMs = 60000)

                for scope, _ in every do
                    do! (vectors :> IVectorStore).Upsert scope "doc:chunk:0" [| 1.0f; 0.0f |] chunk
                    do! (keywords :> ISparseIndex).Upsert scope "doc:chunk:0" chunk
            }

            let! names = storage.List("_rag", "_rag/")

            for _, key in every do
                Expect.contains names (sprintf "_rag/%s/index.json" key) "the vector snapshot"
                Expect.contains names (sprintf "_rag/%s/bm25.json" key) "the keyword snapshot"
        }
    ]

let private containerTests =
    testList "the storage container" [
        test "a team and a user render their ScopeKey container; the shared scopes have none" {
            Expect.equal (VectorScope.container (Team "acme")) (Some "team-acme") "a team"
            Expect.equal (VectorScope.container (VectorScope.User "u1")) (Some "user-u1") "a user"
            Expect.isNone (VectorScope.container Platform) "the platform scope belongs to no principal"
            Expect.isNone (VectorScope.container Deployment) "nor does the deployment scope"
        }

        test "ofContainer reads back what container renders, for a team and a user" {
            for key in [ ScopeKey.Team(TeamId "acme"); ScopeKey.User(UserId "u1") ] do
                let storage = StorageScope.ofKey true key
                let scope = VectorScope.ofContainer storage
                Expect.equal (VectorScope.container scope) (Some storage.Container) (sprintf "%A" key)

            Expect.equal
                (VectorScope.ofContainer (StorageScope.ofKey true (ScopeKey.Team(TeamId "acme"))))
                (Team "acme")
                "a team container is the team's"

            Expect.equal
                (VectorScope.ofContainer (StorageScope.ofKey false (ScopeKey.User(UserId "u1"))))
                (VectorScope.User "u1")
                "a user container is that user's own"
        }

        test "the deployment container is Deployment; every other container is one caller's own" {
            let deployment = {
                ScopeId = VectorScope.DeploymentContainer
                Container = VectorScope.DeploymentContainer
                Persist = true
            }

            Expect.equal (VectorScope.ofContainer deployment) Deployment "the deployment container"

            Expect.equal
                (VectorScope.ofContainer (StorageScope.ofKey false (ScopeKey.Session "s1")))
                (VectorScope.User "s1")
                "a session's chunks stay in its own User scope"

            Expect.equal
                (VectorScope.ofContainer (StorageScope.ofKey true ScopeKey.platform))
                (VectorScope.User ScopeKey.PlatformContainer)
                "no storage scope projects to Platform: only the platform knowledge API writes there"
        }
    ]

let private auditTests =
    testList "the audit stream" [
        test "one spelling per scope, the one already at rest" {
            Expect.equal (VectorScope.auditScopeId (Team "acme")) "acme" "a team's bare id"
            Expect.equal (VectorScope.auditScopeId Platform) "_platform" "the platform trail"
            Expect.equal (VectorScope.auditScopeId Deployment) "_deployment" "the deployment container's name"
            Expect.equal (VectorScope.auditScopeId (VectorScope.User "u1")) "user:u1" "a user's store key"
        }
    ]

let private chunkIdTests =
    testList "the chunk-id grammar" [
        test "render spells documentId:chunk:ordinal" {
            Expect.equal (ChunkId.render { DocumentId = "doc-1"; Ordinal = 3 }) "doc-1:chunk:3" "the grammar"

            Expect.equal
                (ChunkId.render {
                    DocumentId = "report.pdf"
                    Ordinal = 0
                })
                "report.pdf:chunk:0"
                "a file-name key renders in the same grammar"
        }

        test "tryParse is the inverse of render" {
            for id in
                [
                    { DocumentId = "doc-1"; Ordinal = 0 }
                    { DocumentId = "doc-1"; Ordinal = 42 }
                    {
                        DocumentId = "a:chunk:b"
                        Ordinal = 2
                    }
                ] do
                Expect.equal (ChunkId.tryParse (ChunkId.render id)) (Some id) (sprintf "%A" id)
        }

        test "a string the grammar does not produce reads as no chunk id" {
            for foreign in [ null; ""; "doc"; "doc:chunk:"; "doc:chunk:x"; ":chunk:1"; "doc:chunk:-1" ] do
                Expect.isNone (ChunkId.tryParse foreign) (sprintf "%A" foreign)
        }

        test "ofDocument lists a document's chunks in order" {
            Expect.equal
                (ChunkId.ofDocument "d" 3 |> List.map ChunkId.render)
                [ "d:chunk:0"; "d:chunk:1"; "d:chunk:2" ]
                "ordinals 0 .. count - 1"

            Expect.isEmpty (ChunkId.ofDocument "d" 0) "no chunks"
        }
    ]

let tests =
    testList "Phase 1083 — VectorScope projections and the chunk-id grammar" [
        keyTests
        containerTests
        auditTests
        chunkIdTests
    ]