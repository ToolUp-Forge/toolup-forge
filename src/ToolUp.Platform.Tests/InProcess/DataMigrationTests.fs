module ToolUp.Platform.Tests.InProcess.DataMigrationTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.FileProcessor

// ─── Phase 10a — module data-migration framework ─────────────────
//
// Four things are worth pinning, and they are the four the phase's
// acceptance criteria turn on:
//
//   1. Chain resolution is total and refuses rather than guesses — a
//      gap, a fork, a non-advancing step and an overshoot each produce
//      their own named error.
//   2. A pass upgrades V1 objects to the declared version, stamps them,
//      and preserves history (`Versioned` keeps the pre-migration
//      version readable).
//   3. Re-running is free: the second pass migrates nothing because the
//      stamps say so, which is also what makes an interrupted pass
//      resume rather than redo.
//   4. A migrator that throws on one object leaves THAT object at its
//      old version, records the failure, emits `MigrationFailed`, and
//      the rest of the scope still migrates.

// ─── Fixtures ────────────────────────────────────────────────────

type private SilentLogger() =
    interface ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(_) = ()
        member _.Error(_, _) = ()

/// A migrator built from a plain byte-array function, so a test states
/// only the version pair and the transformation.
type private TestMigrator(dataTypeId: string, fromVersion: int, toVersion: int, transform: byte[] -> byte[]) =
    interface IDataMigrator with
        member _.DataTypeId = dataTypeId
        member _.FromVersion = fromVersion
        member _.ToVersion = toVersion

        member _.Migrate(payload: obj) = async {
            let bytes = payload :?> byte[]
            return box (transform bytes)
        }

/// A migrator that always raises — the failure-policy fixture.
type private ThrowingMigrator(dataTypeId: string, fromVersion: int, toVersion: int, predicate: byte[] -> bool) =
    interface IDataMigrator with
        member _.DataTypeId = dataTypeId
        member _.FromVersion = fromVersion
        member _.ToVersion = toVersion

        member _.Migrate(payload: obj) = async {
            let bytes = payload :?> byte[]

            if predicate bytes then
                failwith "poisoned record"

            return box bytes
        }

/// A migrator whose return is neither `byte[]` nor `string`.
type private BadPayloadMigrator(dataTypeId: string, fromVersion: int, toVersion: int) =
    interface IDataMigrator with
        member _.DataTypeId = dataTypeId
        member _.FromVersion = fromVersion
        member _.ToVersion = toVersion
        member _.Migrate(_: obj) = async { return box 42 }

let private dataType (id: string) (version: int) (migrations: IDataMigrator list) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    SchemaVersion = version
    Migrations = migrations
    Detect = fun _ -> async { return false }
    Process = fun _ -> async { return failwith "stub DataType.Process is never called by a migration pass" }
}

let private text (bytes: byte[]) = Encoding.UTF8.GetString bytes
let private bytes (s: string) = Encoding.UTF8.GetBytes s

let private tempStorage () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-migration-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

/// Full runner harness over real blob-backed stores — the same
/// `DataObjectStore` a deployment runs, not a stub, so the stamp /
/// version-chain behaviour under test is the shipped behaviour.
type private Harness = {
    Runner: MigrationRunner
    Objects: IDataObjectStore
    StatusStore: IMigrationStatusStore
    Events: IEventStore
    Scope: string
}

let private harness (dataTypes: DataType list) =
    let storage = tempStorage ()
    let objects = DataObjectStore(storage) :> IDataObjectStore
    let statusStore = MigrationStatusStore.create storage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let registry = MigrationRegistry(dataTypes, [])
    let logger = SilentLogger() :> ILogger

    {
        Runner = MigrationRunner(registry, statusStore, objects, events, logger)
        Objects = objects
        StatusStore = statusStore
        Events = events
        Scope = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    }

/// Seed one unstamped (i.e. schema V1) object, exactly as a deployment
/// predating this substrate would have written it.
let private seed (h: Harness) (dataTypeId: string) (objectId: string) (content: string) = async {
    let! result =
        h.Objects.Save(h.Scope, objectId, bytes content, dataTypeId, "seed-user", Map.empty, VersioningPolicy.Versioned)

    return Expect.wantOk result "seed save"
}

// ─── 1. Chain resolution ─────────────────────────────────────────

let private chainTests =
    testList "MigrationChain" [
        test "resolves a multi-step chain in ascending order" {
            let one = TestMigrator("T", 1, 2, id) :> IDataMigrator
            let two = TestMigrator("T", 2, 3, id) :> IDataMigrator

            // Registered out of order on purpose: resolution follows
            // the version links, never the list order.
            let chain = MigrationChain.resolve "T" [ two; one ] 1 3

            match chain with
            | Ok steps ->
                Expect.equal (steps |> List.map _.FromVersion) [ 1; 2 ] "steps ascend from the object's version"
            | Error e -> failtestf "expected a resolved chain, got %A" e
        }

        test "an already-current object resolves to an empty chain" {
            let one = TestMigrator("T", 1, 2, id) :> IDataMigrator
            Expect.equal (MigrationChain.resolve "T" [ one ] 2 2) (Ok []) "nothing to do"
        }

        test "an object stamped past the target is left alone, not refused" {
            // What a rollback looks like from the runner's side.
            let one = TestMigrator("T", 1, 2, id) :> IDataMigrator
            Expect.equal (MigrationChain.resolve "T" [ one ] 5 2) (Ok []) "no downgrade attempted"
        }

        test "a gap in the chain is NoMigrationPath naming the version it stalled at" {
            let one = TestMigrator("T", 1, 2, id) :> IDataMigrator

            match MigrationChain.resolve "T" [ one ] 1 3 with
            | Error(NoMigrationPath(dataTypeId, atVersion)) ->
                Expect.equal dataTypeId "T" "names the data type"
                Expect.equal atVersion 2 "names the version with no migrator"
            | other -> failtestf "expected NoMigrationPath, got %A" other
        }

        test "two migrators reading one version are refused, never picked between" {
            let a = TestMigrator("T", 1, 2, id) :> IDataMigrator
            let b = TestMigrator("T", 1, 3, id) :> IDataMigrator

            match MigrationChain.resolve "T" [ a; b ] 1 3 with
            | Error(AmbiguousMigrationStep(_, atVersion, candidates)) ->
                Expect.equal atVersion 1 "names the contested version"
                Expect.equal candidates 2 "counts the candidates"
            | other -> failtestf "expected AmbiguousMigrationStep, got %A" other
        }

        test "a non-advancing migrator is refused before it can loop" {
            let stuck = TestMigrator("T", 2, 2, id) :> IDataMigrator

            match MigrationChain.resolve "T" [ stuck ] 1 3 with
            | Error(NonAdvancingMigrator(_, fromVersion, toVersion)) ->
                Expect.equal (fromVersion, toVersion) (2, 2) "names the offending pair"
            | other -> failtestf "expected NonAdvancingMigrator, got %A" other
        }

        test "a chain that overshoots the declared version is refused, not truncated" {
            // Writing an object at a version the module does not claim
            // to read is exactly the mixed state this substrate exists
            // to prevent.
            let jump = TestMigrator("T", 1, 3, id) :> IDataMigrator

            match MigrationChain.resolve "T" [ jump ] 1 2 with
            | Error(ChainOvershootsTarget(_, reached, target)) ->
                Expect.equal (reached, target) (3, 2) "names both versions"
            | other -> failtestf "expected ChainOvershootsTarget, got %A" other
        }

        test "validateSet passes a well-formed set and fails a forked one" {
            let a = TestMigrator("T", 1, 2, id) :> IDataMigrator
            let b = TestMigrator("T", 2, 3, id) :> IDataMigrator
            Expect.equal (MigrationChain.validateSet "T" [ a; b ]) (Ok()) "well-formed"

            let forked = TestMigrator("T", 1, 4, id) :> IDataMigrator
            Expect.isError (MigrationChain.validateSet "T" [ a; forked ]) "forked"
        }
    ]

// ─── 2. Registry ─────────────────────────────────────────────────

let private registryTests =
    testList "MigrationRegistry" [
        test "unions DataType-declared migrators with DI-registered ones" {
            let declared = TestMigrator("T", 1, 2, id) :> IDataMigrator
            let injected = TestMigrator("T", 2, 3, id) :> IDataMigrator
            let registry = MigrationRegistry([ dataType "T" 3 [ declared ] ], [ injected ])

            Expect.equal (registry.MigratorsFor "T" |> List.length) 2 "both sources contribute"
            Expect.isOk (registry.ResolveChain("T", 1)) "the union resolves a chain neither half could"
        }

        test "a migrator wired both ways counts once" {
            let both = TestMigrator("T", 1, 2, id) :> IDataMigrator
            let registry = MigrationRegistry([ dataType "T" 2 [ both ] ], [ both ])

            Expect.equal (registry.MigratorsFor "T" |> List.length) 1 "collapsed by reference"
            Expect.isOk (registry.ResolveChain("T", 1)) "not read as an ambiguous fork"
        }

        test "DI migrators for another data type do not leak in" {
            let other = TestMigrator("OTHER", 1, 2, id) :> IDataMigrator
            let registry = MigrationRegistry([ dataType "T" 1 [] ], [ other ])
            Expect.isEmpty (registry.MigratorsFor "T") "keyed by data type id"
        }

        test "a data type still at the floor version is not swept" {
            let registry = MigrationRegistry([ dataType "A" 1 []; dataType "B" 2 [] ], [])

            Expect.equal
                (registry.MigratableDataTypes |> List.map _.Id)
                [ "B" ]
                "only versions above the floor can have stale objects"
        }

        test "DescribeDataTypes reports a chain problem, and silence when there is none" {
            let good = TestMigrator("A", 1, 2, id) :> IDataMigrator
            let registry = MigrationRegistry([ dataType "A" 2 [ good ]; dataType "B" 3 [] ], [])
            let described = registry.DescribeDataTypes()

            let byId = described |> List.map (fun d -> d.DataTypeId, d) |> Map.ofList
            Expect.isNone byId["A"].ChainProblem "a complete chain reports nothing"
            Expect.isSome byId["B"].ChainProblem "a missing chain is named"
            Expect.equal byId["B"].CurrentVersion 3 "declared version is projected verbatim"
        }
    ]

// ─── 3. Status store ─────────────────────────────────────────────

let private statusStoreTests =
    testList "MigrationStatusStore" [
        test "the blob path is the documented _platform/migrations layout" {
            Expect.equal
                (MigrationStatusStore.statusBlob "team-a" "SalesData")
                "migrations/team-a/SalesData.json"
                "layout is part of the operator-facing contract"
        }

        test "a segment that could climb out of its prefix is rejected" {
            Expect.isFalse (MigrationStatusStore.isSafeSegment "..") "parent"
            Expect.isFalse (MigrationStatusStore.isSafeSegment "a/b") "separator"
            Expect.isFalse (MigrationStatusStore.isSafeSegment "") "blank"
            Expect.isTrue (MigrationStatusStore.isSafeSegment "team-a") "ordinary id"
        }

        testAsync "round-trips a status and keeps teams apart" {
            let store = MigrationStatusStore.create (tempStorage ())

            let mine = {
                MigrationStatus.idle "team-a" "SalesData" 3 with
                    TotalObjects = 120
                    MigratedObjects = 47
                    State = MigrationInProgress
                    StartedAt = Some(DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            }

            let! _ = store.Write mine
            let! _ = store.Write(MigrationStatus.idle "team-b" "SalesData" 3)

            let! readBack = store.Read("team-a", "SalesData")
            Expect.equal (readBack |> Option.map _.MigratedObjects) (Some 47) "counters survive the round-trip"

            let! forTeam = store.ListForTeam "team-a"
            Expect.equal (forTeam |> List.map _.TeamId) [ "team-a" ] "a team read never widens to another team"

            let! all = store.ListAll()
            Expect.equal (List.length all) 2 "the operator view spans teams"
        }

        testAsync "an unsafe key is refused rather than written outside the prefix" {
            let store = MigrationStatusStore.create (tempStorage ())

            let! result =
                store.Write {
                    MigrationStatus.idle "../escape" "SalesData" 2 with
                        TotalObjects = 1
                }

            Expect.isError result "refused"
        }
    ]

// ─── 4. Payload coercion ─────────────────────────────────────────

let private payloadTests =
    testList "MigrationExecution.interpretPayload" [
        test "byte[] passes through" {
            Expect.equal (MigrationExecution.interpretPayload (box (bytes "x"))) (Ok(bytes "x")) "bytes"
        }

        test "a string is encoded as UTF-8" {
            Expect.equal (MigrationExecution.interpretPayload (box "hello")) (Ok(bytes "hello")) "string"
        }

        test "null is refused rather than written as an empty object" {
            Expect.isError (MigrationExecution.interpretPayload null) "null"
        }

        test "any other type is refused, naming what came back" {
            match MigrationExecution.interpretPayload (box 42) with
            | Error message -> Expect.stringContains message "Int32" "names the offending type"
            | Ok _ -> failtest "expected a refusal"
        }
    ]

// ─── 5. Runner ───────────────────────────────────────────────────

let private runnerTests =
    testList "MigrationRunner" [
        testAsync "upgrades lagging objects, stamps them, and preserves history" {
            let upgrade =
                TestMigrator("T", 1, 2, (fun b -> bytes (text b + "-v2"))) :> IDataMigrator

            let dt = dataType "T" 2 [ upgrade ]
            let h = harness [ dt ]

            let! _ = seed h "T" "obj-1" "payload"
            let! status = h.Runner.RunForTeam(h.Scope, dt)

            Expect.equal status.State MigrationComplete "pass completed"
            Expect.equal status.MigratedObjects 1 "one object upgraded"
            Expect.equal status.TotalObjects 1 "one object in scope"

            let! current = h.Objects.Get(h.Scope, "obj-1")
            let meta, content = Expect.wantOk current "read back"
            Expect.equal (text content) "payload-v2" "content upgraded"
            Expect.equal (MigrationMetadata.readVersion meta.Metadata) 2 "stamped at the declared version"

            Expect.equal
                meta.CreatedBy
                MigrationMetadata.MigrationPrincipal
                "the migration write is attributable in the version history"

            let! versions = h.Objects.ListVersions(h.Scope, "obj-1")
            Expect.equal (List.length versions) 2 "the pre-migration version is preserved (Phase 7 history)"

            let! original = h.Objects.GetVersion(h.Scope, "obj-1", 1)
            let _, originalContent = Expect.wantOk original "v1 still readable"
            Expect.equal (text originalContent) "payload" "history is intact, not rewritten"
        }

        testAsync "runs a multi-step chain end to end" {
            let one = TestMigrator("T", 1, 2, (fun b -> bytes (text b + "-a"))) :> IDataMigrator
            let two = TestMigrator("T", 2, 3, (fun b -> bytes (text b + "-b"))) :> IDataMigrator
            let dt = dataType "T" 3 [ one; two ]
            let h = harness [ dt ]

            let! _ = seed h "T" "obj-1" "x"
            let! status = h.Runner.RunForTeam(h.Scope, dt)
            Expect.equal status.MigratedObjects 1 "upgraded"

            let! current = h.Objects.Get(h.Scope, "obj-1")
            let meta, content = Expect.wantOk current "read back"
            Expect.equal (text content) "x-a-b" "both steps applied, in order"
            Expect.equal (MigrationMetadata.readVersion meta.Metadata) 3 "landed at the declared version"
        }

        testAsync "a second pass is a no-op — which is what makes an interrupted pass resume" {
            let upgrade =
                TestMigrator("T", 1, 2, (fun b -> bytes (text b + "!"))) :> IDataMigrator

            let dt = dataType "T" 2 [ upgrade ]
            let h = harness [ dt ]

            let! _ = seed h "T" "obj-1" "a"
            let! _ = seed h "T" "obj-2" "b"

            let! first = h.Runner.RunForTeam(h.Scope, dt)
            Expect.equal first.MigratedObjects 2 "both upgraded on the first pass"

            let! second = h.Runner.RunForTeam(h.Scope, dt)
            Expect.equal second.MigratedObjects 0 "nothing re-migrated"
            Expect.equal second.AlreadyCurrentObjects 2 "both recognised as current from their stamps"
            Expect.equal second.State MigrationComplete "still complete"

            let! versions = h.Objects.ListVersions(h.Scope, "obj-1")
            Expect.equal (List.length versions) 2 "the second pass wrote no new version"
        }

        testAsync "objects of another data type are not touched" {
            let upgrade =
                TestMigrator("T", 1, 2, (fun b -> bytes (text b + "!"))) :> IDataMigrator

            let dt = dataType "T" 2 [ upgrade ]
            let other = dataType "OTHER" 1 []
            let h = harness [ dt; other ]

            let! _ = seed h "T" "mine" "a"
            let! _ = seed h "OTHER" "theirs" "b"

            let! status = h.Runner.RunForTeam(h.Scope, dt)
            Expect.equal status.TotalObjects 1 "the pass only counts its own data type"

            let! untouched = h.Objects.Get(h.Scope, "theirs")
            let meta, content = Expect.wantOk untouched "other data type read back"
            Expect.equal (text content) "b" "content untouched"
            Expect.equal meta.Version 1 "no new version written"
        }

        testAsync "one throwing object is left behind and the rest still migrate" {
            let poison = ThrowingMigrator("T", 1, 2, (fun b -> text b = "bad")) :> IDataMigrator

            let dt = dataType "T" 2 [ poison ]
            let h = harness [ dt ]

            let! _ = seed h "T" "good-1" "fine"
            let! _ = seed h "T" "bad-1" "bad"
            let! _ = seed h "T" "good-2" "also-fine"

            let! status = h.Runner.RunForTeam(h.Scope, dt)

            Expect.equal status.State MigrationCompleteWithFailures "the pass finished, and says so"
            Expect.equal status.MigratedObjects 2 "the healthy objects migrated"
            Expect.equal status.FailedObjects 1 "the poisoned one did not"
            Expect.equal (status.Failures |> List.map _.ObjectId) [ "bad-1" ] "the failure names the object"

            Expect.stringContains
                (status.Failures.Head.Error)
                "v1→v2"
                "the failure names the step that raised, not just the exception"

            // The whole point of the policy: the source object is
            // untouched, so a fixed migrator can retry exactly this one.
            let! stillOld = h.Objects.Get(h.Scope, "bad-1")
            let meta, content = Expect.wantOk stillOld "read back the failed object"
            Expect.equal (text content) "bad" "content untouched"
            Expect.equal (MigrationMetadata.readVersion meta.Metadata) 1 "still at its old version"
            Expect.equal meta.Version 1 "no version was written for it"

            let! events = h.Events.ReadByType(h.Scope, MigrationEvents.MigrationFailedEventType)
            Expect.equal (List.length events) 1 "one MigrationFailed event per failed object"
            Expect.equal events.Head.SourceModule MigrationEvents.SourceModule "attributed to the migration substrate"
            Expect.stringContains events.Head.Payload "bad-1" "the payload names the object"
        }

        testAsync "a retry after fixing the migrator picks up only the object left behind" {
            let dt1 =
                dataType "T" 2 [ ThrowingMigrator("T", 1, 2, (fun b -> text b = "bad")) :> IDataMigrator ]

            let h1 = harness [ dt1 ]
            let! _ = seed h1 "T" "good-1" "fine"
            let! _ = seed h1 "T" "bad-1" "bad"
            let! _ = h1.Runner.RunForTeam(h1.Scope, dt1)

            // Same stores, a repaired migrator set.
            let dt2 = dataType "T" 2 [ TestMigrator("T", 1, 2, id) :> IDataMigrator ]
            let registry = MigrationRegistry([ dt2 ], [])

            let repaired =
                MigrationRunner(registry, h1.StatusStore, h1.Objects, h1.Events, SilentLogger() :> ILogger)

            let! second = repaired.RunForTeam(h1.Scope, dt2)
            Expect.equal second.MigratedObjects 1 "only the previously-failed object had work left"
            Expect.equal second.AlreadyCurrentObjects 1 "the already-upgraded one was skipped"
            Expect.equal second.FailedObjects 0 "clean"
            Expect.equal second.State MigrationComplete "and the state reflects it"
        }

        testAsync "a migrator returning an unusable payload fails that object rather than writing it" {
            let dt = dataType "T" 2 [ BadPayloadMigrator("T", 1, 2) :> IDataMigrator ]
            let h = harness [ dt ]

            let! _ = seed h "T" "obj-1" "a"
            let! status = h.Runner.RunForTeam(h.Scope, dt)

            Expect.equal status.FailedObjects 1 "refused"

            let! current = h.Objects.Get(h.Scope, "obj-1")
            let _, content = Expect.wantOk current "read back"
            Expect.equal (text content) "a" "the original bytes are still there"
        }

        testAsync "an object with no migrator for its version fails alone, without blocking the pass" {
            // `obj-1` sits at V1 with only a 2→3 step registered.
            let dt = dataType "T" 3 [ TestMigrator("T", 2, 3, id) :> IDataMigrator ]
            let h = harness [ dt ]

            let! _ = seed h "T" "obj-1" "a"
            let! status = h.Runner.RunForTeam(h.Scope, dt)

            Expect.equal status.State MigrationCompleteWithFailures "a reachability gap is a per-object failure"
            Expect.equal status.FailedObjects 1 "the object is named"
            Expect.stringContains status.Failures.Head.Error "No migrator" "and the reason is the chain gap"
        }

        testAsync "a structurally broken migrator set blocks the pass and touches nothing" {
            let a = TestMigrator("T", 1, 2, (fun b -> bytes (text b + "!"))) :> IDataMigrator
            let b = TestMigrator("T", 1, 3, (fun b -> bytes (text b + "?"))) :> IDataMigrator
            let dt = dataType "T" 3 [ a; b ]
            let h = harness [ dt ]

            let! _ = seed h "T" "obj-1" "a"
            let! status = h.Runner.RunForTeam(h.Scope, dt)

            match status.State with
            | MigrationChainBlocked reason -> Expect.stringContains reason "exactly one is required" "names the defect"
            | other -> failtestf "expected MigrationChainBlocked, got %A" other

            Expect.equal status.MigratedObjects 0 "nothing migrated"
            Expect.equal status.TotalObjects 0 "the scope was never even listed"

            let! current = h.Objects.Get(h.Scope, "obj-1")
            let _, content = Expect.wantOk current "read back"
            Expect.equal (text content) "a" "the object is exactly as it was"
        }

        testAsync "the status blob records the pass, so the admin table reads it back" {
            let dt = dataType "T" 2 [ TestMigrator("T", 1, 2, id) :> IDataMigrator ]
            let h = harness [ dt ]

            let! _ = seed h "T" "obj-1" "a"
            let! _ = h.Runner.RunForTeam(h.Scope, dt)

            let! persisted = h.StatusStore.Read(h.Scope, "T")
            let status = Expect.wantSome persisted "the pass persisted its status"
            Expect.equal status.TargetVersion 2 "target version recorded"
            Expect.equal status.MigratedObjects 1 "count recorded"
            Expect.isSome status.CompletedAt "completion recorded"
            Expect.equal (MigrationStatus.outstanding status) 0 "nothing outstanding"
        }

        testAsync "a scope with nothing to migrate completes without writing anything" {
            let dt = dataType "T" 2 [ TestMigrator("T", 1, 2, id) :> IDataMigrator ]
            let h = harness [ dt ]

            let! status = h.Runner.RunForTeam(h.Scope, dt)
            Expect.equal status.TotalObjects 0 "empty scope"
            Expect.equal status.State MigrationComplete "still a clean completion"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 10a — data migrations" [ chainTests; registryTests; statusStoreTests; payloadTests; runnerTests ]