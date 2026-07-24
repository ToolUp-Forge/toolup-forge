module ToolUp.Platform.Tests.Graph.EntityGraphProjectionTests

open System
open System.IO
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Graph
open ToolUp.Graph.Projection
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.AuditLog
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore

// ─── Phase 68d — Entity↔Graph projection bridge test pack ───────────
//
// Covers: the pure projection function (node + edges, incl. the
// precision-floor property mapping); mutation-driven incremental sync
// through the real entity-lifecycle signal (create/update/delete
// propagate; idempotent re-apply); one-shot rebuild (orphan removal,
// idempotent second run, ProjectionReport counts); missed-signal recovery;
// tenant isolation; the not-composed byte-identity guarantee (DI
// inspection); and the six-rule portability audit on the projector.

// ── Fixture entity types (with a Phase-19c declared relationship) ──

type Author = {
    Id: string
    Type: string
    Version: int
    Name: string
    FoundedYear: int
    Royalty: decimal
}

type Book = {
    Id: string
    Type: string
    Version: int
    Title: string
    AuthorId: string
    Pages: int
    Published: bool
}

let private authorReg: EntityRegistration<Author> =
    EntityRegistration.create<Author> "Author"

let private bookReg: EntityRegistration<Book> =
    EntityRegistration.create<Book> "Book"
    |> EntityRegistration.withRelationship {
        Name = "writtenBy"
        Target = "Author"
        Cardinality = Cardinality.ManyToOne
        ForeignKeyField = "AuthorId"
        Direction = RelationshipDirection.Outgoing
        JoinEntity = None
    }

let private enrollments = [
    ProjectedEntityType.ofRegistration authorReg
    ProjectedEntityType.ofRegistration bookReg
]

let private mkAuthor id name year royalty : Author = {
    Id = id
    Type = "Author"
    Version = 0
    Name = name
    FoundedYear = year
    Royalty = royalty
}

let private mkBook id title authorId pages published : Book = {
    Id = id
    Type = "Book"
    Version = 0
    Title = title
    AuthorId = authorId
    Pages = pages
    Published = published
}

// ── Store / projector construction ──
//
// `autoProject = true` wraps the entity store's audit log with the
// `ProjectingAuditLog` decorator, so a `Save` / `Delete` drives the
// projector through the genuine lifecycle signal (end-to-end). The
// projector references the store, the store's projecting audit log
// references the projector — the DI cycle the production wiring breaks with
// a lazy thunk is broken here with a `ref` cell.

type private Env = {
    Store: IEntityStore
    Graph: IGraphStore
    Projector: IEntityGraphProjection
}

let private makeEnv (autoProject: bool) : Env =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-egp-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register authorReg
    registry.Register bookReg
    let graph = ToolUp.Graph.InMemory.InMemoryGraphStore() :> IGraphStore
    let projectorCell = ref Unchecked.defaultof<IEntityGraphProjection>

    let auditLog: IAuditLog option =
        if autoProject then
            Some(ProjectingAuditLog(NoOpAuditLog(), (fun () -> projectorCell.Value)) :> IAuditLog)
        else
            None

    let store = BlobEntityStore(dos, blob, registry, auditLog) :> IEntityStore

    let projector =
        EntityGraphProjector(store, graph, enrollments) :> IEntityGraphProjection

    projectorCell.Value <- projector

    {
        Store = store
        Graph = graph
        Projector = projector
    }

let private nodeIdOf entityType entityId =
    EntityProjection.nodeIdFor entityType entityId

let private okOrFail label result =
    match result with
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %A" label e

let tests =
    testList "ToolUp.Graph.Projection — entity↔graph bridge (Phase 68d)" [

        // ── 68d.A — pure projection ──
        testCase "pure: an entity projects to a node (label = Type, id = entity:Type:Id)" (fun () ->
            let node, edges =
                EntityProjection.projectEntity authorReg (mkAuthor "a1" "Ada" 1990 12.5m)

            Expect.equal node.Id (nodeIdOf "Author" "a1") "deterministic node id"
            Expect.equal node.Labels (Set.singleton "Author") "label is the entity Type"
            Expect.equal edges [] "an entity with no outgoing relationship projects no edges"
            Expect.equal (node.Properties.TryFind "Name") (Some(PString "Ada")) "string field → PString")

        testCase "pure: property mapping honours the precision floor (rule 6)" (fun () ->
            let node, _ =
                EntityProjection.projectEntity authorReg (mkAuthor "a1" "Ada" 1990 12.5m)
            // int → PInt (int64); decimal → PFloat (no decimal case in the graph model)
            Expect.equal (node.Properties.TryFind "FoundedYear") (Some(PInt 1990L)) "int field → PInt(int64)"
            Expect.equal (node.Properties.TryFind "Royalty") (Some(PFloat 12.5)) "decimal downcast to PFloat")

        testCase "pure: a declared relationship projects a deterministic edge" (fun () ->
            let node, edges =
                EntityProjection.projectEntity bookReg (mkBook "b1" "Poems" "a1" 120 true)

            Expect.equal node.Id (nodeIdOf "Book" "b1") "book node id"

            match edges with
            | [ edge ] ->
                Expect.equal edge.Label "writtenBy" "edge label = relationship name"
                Expect.equal edge.From (nodeIdOf "Book" "b1") "edge from the declaring entity"
                Expect.equal edge.To (nodeIdOf "Author" "a1") "edge to the FK target"

                Expect.equal
                    edge.Id
                    (EdgeId "entity-edge:entity:Book:b1:writtenBy:entity:Author:a1")
                    "deterministic edge id"
            | other -> failtestf "expected exactly one edge, got %A" other)

        testCase "pure: an empty foreign key projects no edge" (fun () ->
            let _, edges =
                EntityProjection.projectEntity bookReg (mkBook "b1" "Poems" "" 120 true)

            Expect.equal edges [] "a book with no author id emits no writtenBy edge")

        // ── 68d.B — mutation-driven incremental sync (through the signal) ──
        testCaseAsync
            "sync: create propagates node + edge automatically"
            (async {
                let env = makeEnv true
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                let! _ = env.Store.Save("team-a", mkBook "b1" "Poems" "a1" 120 true)

                let! authorNode = env.Graph.GetNode("team-a", nodeIdOf "Author" "a1")
                let! bookNode = env.Graph.GetNode("team-a", nodeIdOf "Book" "b1")
                Expect.isSome authorNode "author node created by the projection"
                Expect.isSome bookNode "book node created by the projection"

                let! neighbours = env.Graph.Neighbours("team-a", nodeIdOf "Book" "b1", Outgoing, Some "writtenBy")

                Expect.equal
                    (neighbours |> List.map _.Id)
                    [ nodeIdOf "Author" "a1" ]
                    "writtenBy edge points at the author"
            })

        testCaseAsync
            "sync: update re-projects the node in place (still one node)"
            (async {
                let env = makeEnv true
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada Lovelace" 1990 20.0m)

                let! node = env.Graph.GetNode("team-a", nodeIdOf "Author" "a1")

                match node with
                | Some n ->
                    Expect.equal (n.Properties.TryFind "Name") (Some(PString "Ada Lovelace")) "node reflects the update"
                | None -> failtest "author node should still exist after update"
            })

        testCaseAsync
            "sync: delete removes the node (+ incident edges)"
            (async {
                let env = makeEnv true
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                let! _ = env.Store.Save("team-a", mkBook "b1" "Poems" "a1" 120 true)
                let! _ = env.Store.Delete("team-a", "Book", "b1")

                let! bookNode = env.Graph.GetNode("team-a", nodeIdOf "Book" "b1")
                Expect.isNone bookNode "book node removed by the delete signal"

                let! neighbours = env.Graph.Neighbours("team-a", nodeIdOf "Author" "a1", Incoming, Some "writtenBy")
                Expect.equal neighbours [] "incident writtenBy edge removed with the node"
            })

        testCaseAsync
            "sync: re-applying the same mutation is idempotent (deterministic ids)"
            (async {
                let env = makeEnv false
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                let! r1 = env.Projector.SyncEntity("team-a", "Author", "a1")
                let! r2 = env.Projector.SyncEntity("team-a", "Author", "a1")
                okOrFail "sync 1" r1
                okOrFail "sync 2" r2
                // A second rebuild sees no change — the re-apply produced the
                // identical node value.
                let! report = env.Projector.RebuildProjection("team-a")
                Expect.isTrue (ProjectionReport.isNoOp report) "re-applied projection left nothing to reconcile"
            })

        testCaseAsync
            "sync: an unknown entity type surfaces retryable data, never a throw (rule 3)"
            (async {
                let env = makeEnv false
                let! result = env.Projector.SyncEntity("team-a", "Ghost", "x")
                Expect.equal result (Error(UnknownProjectedType "Ghost")) "unknown type → typed error, not exception"
            })

        // ── 68d.C — one-shot rebuild + reconciliation ──
        testCaseAsync
            "rebuild: bootstraps a graph over an existing entity store (counts correct)"
            (async {
                let env = makeEnv false // no auto-projection — simulate a pre-existing store
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                let! _ = env.Store.Save("team-a", mkBook "b1" "Poems" "a1" 120 true)

                let! report = env.Projector.RebuildProjection("team-a")
                Expect.equal report.NodesUpserted 2 "two entities → two nodes upserted"
                Expect.equal report.EdgesUpserted 1 "one declared relationship → one edge upserted"
                Expect.equal report.OrphansRemoved 0 "no orphans on a fresh bootstrap"

                let! authorNode = env.Graph.GetNode("team-a", nodeIdOf "Author" "a1")
                Expect.isSome authorNode "author node present after rebuild"
            })

        testCaseAsync
            "rebuild: a second run over an unchanged store is a no-op (idempotent)"
            (async {
                let env = makeEnv false
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                let! _ = env.Store.Save("team-a", mkBook "b1" "Poems" "a1" 120 true)
                let! _ = env.Projector.RebuildProjection("team-a")
                let! second = env.Projector.RebuildProjection("team-a")
                Expect.isTrue (ProjectionReport.isNoOp second) "unchanged store → zero-count report"
            })

        testCaseAsync
            "rebuild: removes an orphan node whose source entity is gone"
            (async {
                let env = makeEnv false
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                let! _ = env.Projector.RebuildProjection("team-a")
                // Inject an orphan directly into the graph (no backing entity).
                let orphan = {
                    Id = nodeIdOf "Author" "ghost"
                    Labels = Set.singleton "Author"
                    Properties = Map.empty
                }

                let! _ = env.Graph.UpsertNode("team-a", orphan)
                let! report = env.Projector.RebuildProjection("team-a")
                Expect.equal report.OrphansRemoved 1 "the orphan node is reconciled away"
                let! gone = env.Graph.GetNode("team-a", nodeIdOf "Author" "ghost")
                Expect.isNone gone "orphan removed from the graph"
            })

        testCaseAsync
            "rebuild: heals a missed delete/mutation signal (drift recovery)"
            (async {
                let env = makeEnv false
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                let! _ = env.Store.Save("team-a", mkBook "b1" "Poems" "a1" 120 true)
                let! _ = env.Projector.RebuildProjection("team-a")
                // Simulate a missed signal: the book node vanishes out-of-band.
                let! _ = env.Graph.DeleteNode("team-a", nodeIdOf "Book" "b1")
                let! afterDrift = env.Graph.GetNode("team-a", nodeIdOf "Book" "b1")
                Expect.isNone afterDrift "precondition: node is gone"

                let! report = env.Projector.RebuildProjection("team-a")
                Expect.isTrue (report.NodesUpserted >= 1) "rebuild re-projects the missing node"
                let! restored = env.Graph.GetNode("team-a", nodeIdOf "Book" "b1")
                Expect.isSome restored "missed-signal drift healed by rebuild"
            })

        // ── 68d.E — tenant isolation ──
        testCaseAsync
            "isolation: a tenant's entities project only into its own graph scope"
            (async {
                let env = makeEnv true
                let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                let! _ = env.Store.Save("team-b", mkAuthor "a2" "Grace" 1906 9.0m)

                let! aInA = env.Graph.GetNode("team-a", nodeIdOf "Author" "a1")
                let! bInA = env.Graph.GetNode("team-a", nodeIdOf "Author" "a2")
                let! bInB = env.Graph.GetNode("team-b", nodeIdOf "Author" "a2")
                let! aInB = env.Graph.GetNode("team-b", nodeIdOf "Author" "a1")

                Expect.isSome aInA "team-a's author is in team-a's graph scope"
                Expect.isNone bInA "team-b's author is NOT visible in team-a's graph scope"
                Expect.isSome bInB "team-b's author is in team-b's graph scope"
                Expect.isNone aInB "team-a's author is NOT visible in team-b's graph scope"
            })

        // ── 68d.D — composition seam: not-composed byte-identity + wired ──
        testCase "compose: not composed leaves the DI container untouched (GP 13)" (fun () ->
            let services = ServiceCollection()
            let baseAudit = NoOpAuditLog() :> IAuditLog
            services.AddSingleton<IAuditLog>(baseAudit) |> ignore
            let before = services.Count

            EntityGraphProjectionCompose.wire
                services
                {
                    ServerConfig.defaults with
                        EntityGraphProjection = NoEntityGraphProjection
                }
                enrollments

            Expect.equal services.Count before "no descriptors added when opted out"

            Expect.isFalse
                (services |> Seq.exists (fun d -> d.ServiceType = typeof<IEntityGraphProjection>))
                "no IEntityGraphProjection registered when opted out"

            let provider = services.BuildServiceProvider()

            Expect.isTrue
                (obj.ReferenceEquals(provider.GetService<IAuditLog>(), baseAudit))
                "the registered IAuditLog is the untouched original")

        testCase "compose: opting in registers the projector + decorates the audit log" (fun () ->
            let tempDir =
                Path.Combine(Path.GetTempPath(), "toolup-egp-wire-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory tempDir |> ignore
            let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
            let dos = DataObjectStore(blob) :> IDataObjectStore
            let registry = EntityRegistry()
            registry.Register authorReg
            let services = ServiceCollection()

            services.AddSingleton<IEntityStore>(BlobEntityStore(dos, blob, registry, None) :> IEntityStore)
            |> ignore

            services.AddSingleton<IGraphStore>(ToolUp.Graph.InMemory.InMemoryGraphStore() :> IGraphStore)
            |> ignore

            services.AddSingleton<IAuditLog>(NoOpAuditLog() :> IAuditLog) |> ignore

            EntityGraphProjectionCompose.wire
                services
                {
                    ServerConfig.defaults with
                        EntityGraphProjection = EnabledEntityGraphProjection
                }
                enrollments

            let provider = services.BuildServiceProvider()
            let projection = provider.GetService<IEntityGraphProjection>()
            Expect.isNotNull (box projection) "IEntityGraphProjection resolvable when opted in"

            let audit = provider.GetService<IAuditLog>()
            Expect.isTrue (audit :? ProjectingAuditLog) "IAuditLog is decorated with the projecting wrapper")

        // ── Six-rule portability audit on the projector ──
        testList "six-rule audit (GP 12)" [
            testCase "rule 1 — identity by value: re-projecting yields the same node" (fun () ->
                let n1, _ =
                    EntityProjection.projectEntity authorReg (mkAuthor "a1" "Ada" 1990 12.5m)

                let n2, _ =
                    EntityProjection.projectEntity authorReg (mkAuthor "a1" "Ada" 1990 12.5m)

                Expect.equal n1 n2 "deterministic projection — identical value, identical id")

            testCase "rule 6 — precision at lower bound: no decimal reaches the graph" (fun () ->
                let node, _ =
                    EntityProjection.projectEntity authorReg (mkAuthor "a1" "Ada" 1990 12.5m)

                match node.Properties.TryFind "Royalty" with
                | Some(PFloat _) -> ()
                | other -> failtestf "decimal must map to PFloat, got %A" other)

            testCaseAsync
                "rule 3 — sync failures are data, not exceptions"
                (async {
                    let env = makeEnv false
                    let! result = env.Projector.SyncEntity("team-a", "NotEnrolled", "x")

                    match result with
                    | Error(UnknownProjectedType _) -> ()
                    | other -> failtestf "expected a typed error, got %A" other
                })

            testCaseAsync
                "rule 5 — no cross-entity ordering: entities project independently"
                (async {
                    // Two unrelated entities, projected in either order, reach the
                    // same graph state — the bridge promises ordering only within
                    // a single entity, never across entities.
                    let env = makeEnv false
                    let! _ = env.Store.Save("team-a", mkAuthor "a1" "Ada" 1990 12.5m)
                    let! _ = env.Store.Save("team-a", mkAuthor "a2" "Grace" 1906 9.0m)
                    let! _ = env.Projector.SyncEntity("team-a", "Author", "a2")
                    let! _ = env.Projector.SyncEntity("team-a", "Author", "a1")
                    let! n1 = env.Graph.GetNode("team-a", nodeIdOf "Author" "a1")
                    let! n2 = env.Graph.GetNode("team-a", nodeIdOf "Author" "a2")
                    Expect.isSome n1 "first author present regardless of sync order"
                    Expect.isSome n2 "second author present regardless of sync order"
                })
        ]
    ]