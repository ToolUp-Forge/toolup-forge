module ToolUp.Platform.Tests.InProcess.ConfigMigrationTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigMigrationRegistry

// ─── Phase 10b — configuration schema evolution ──────────────────
//
// The phase's acceptance criteria are three sentences, and each is
// pinned below by a test that fails if the sentence stops being true:
//
//   1. "A module ships V2 with one declared V1→V2 migrator. Existing V1
//      documents are read transparently — the module sees only V2 keys;
//      the persisted document is silently upgraded on first read."
//      → `renameMigration`.
//   2. "A failing migration leaves the V1 document in place, emits an
//      event, and logs — the module sees V1-shaped values, never a
//      partial state." → `failurePolicy`.
//   3. "The `_schema_version` reserved key is honoured by all
//      `IConfigStore` implementations." → `reservedKeyContract`, which
//      is written as a CONTRACT pack: it runs the same four assertions
//      against the shipped blob-backed store AND an independent
//      in-memory implementation, so a future cloud variant validates
//      against the same bar rather than against a convention someone
//      has to remember to re-read.
//
// Two more groups cover what the criteria imply but do not say:
// `chainResolution` (registration defects are refusals, not guesses)
// and `driftObservability` (the gap-audit half — drift a deployment has
// NOT yet shipped a migrator for).

// ─── Fixtures ────────────────────────────────────────────────────

type private SilentLogger() =
    interface ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(_) = ()
        member _.Error(_, _) = ()

/// Captures Warn lines so the failure-policy test can assert the
/// operator actually gets told, not merely that an event was written.
type private CapturingLogger() =
    let warnings = ResizeArray<string>()
    member _.Warnings = List.ofSeq warnings

    interface ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(msg) = warnings.Add msg
        member _.Error(_, _) = ()

/// A migrator built from a plain map function, so a test states only
/// the version pair and the transformation.
type private TestConfigMigrator
    (moduleKey: string, fromVersion: int, toVersion: int, transform: Map<string, string> -> Map<string, string>) =
    interface IConfigMigrator with
        member _.ModuleKey = moduleKey
        member _.FromVersion = fromVersion
        member _.ToVersion = toVersion
        member _.Migrate(values) = async { return transform values }

/// A migrator that always raises — the failure-policy fixture.
type private ThrowingConfigMigrator(moduleKey: string, fromVersion: int, toVersion: int) =
    interface IConfigMigrator with
        member _.ModuleKey = moduleKey
        member _.FromVersion = fromVersion
        member _.ToVersion = toVersion
        member _.Migrate(_) = async { return failwith "poisoned config migrator" }

/// Minimal independent `IConfigStore`, so `reservedKeyContract` proves
/// the decorator carries the contract onto an implementation that
/// knows nothing about it — which is the actual claim in "honoured by
/// all implementations".
type private InMemoryConfigStore() =
    let docs =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, Map<string, string>>()

    interface IConfigStore with
        member _.GetRaw(scope, moduleKey) = async {
            match docs.TryGetValue((scope.Container, moduleKey)) with
            | true, v -> return v
            | _ -> return Map.empty
        }

        member this.Get<'T>(scope, moduleKey) : Async<'T option> = async {
            let! raw = (this :> IConfigStore).GetRaw(scope, moduleKey)
            return ConfigStore.tryProjectExact<'T> ignore scope.Container moduleKey raw
        }

        member this.GetEffective<'T>(scope, moduleKey, schema) : Async<'T> = async {
            let! raw = (this :> IConfigStore).GetRaw(scope, moduleKey)
            let values = ConfigMigrationMetadata.stripReserved raw
            return ConfigStore.projectToRecord<'T> ignore moduleKey schema values
        }

        member this.Set<'T>(scope, moduleKey, value: 'T, schema) = async {
            match ConfigStore.toRawMap value with
            | Error msg -> return Error msg
            | Ok asMap -> return! (this :> IConfigStore).SetRaw(scope, moduleKey, asMap, schema)
        }

        member _.SetRaw(scope, moduleKey, values, _schema) = async {
            docs[(scope.Container, moduleKey)] <- values
            return Ok()
        }

        member _.Clear(scope, moduleKey) = async { docs.TryRemove((scope.Container, moduleKey)) |> ignore }

        member _.Erase(_, _, _, _) = async {
            return
                Result.Ok {
                    HandlerName = "in-memory-config"
                    RecordsAffected = 0
                    Note = None
                }
        }

let private tempStorage () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-configmig-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

let private scope: StorageScope = {
    ScopeId = "team-acme"
    Container = "team-acme"
    Persist = true
}

let private stringField (key: string) (defaultJson: string) : ConfigFieldSchema = {
    Key = key
    DisplayName = key
    Description = None
    Kind = ConfigFieldKind.String None
    Required = false
    DefaultJson = defaultJson
}

let private entry (moduleKey: string) (version: int) (fields: ConfigFieldSchema list) : ModuleConfigEntry = {
    ModuleKey = moduleKey
    DisplayName = moduleKey
    Schema =
        ModuleConfigSchema.ofFields fields
        |> ModuleConfigSchema.withSchemaVersion version
}

/// A decorated store over a real blob-backed inner store, plus the
/// handles a test needs to see behind the decorator.
type private Harness = {
    Store: IConfigStore
    Inner: IConfigStore
    Events: IEventStore
    Support: ConfigMigrationSupport
}

let private harnessWith
    (inner: IConfigStore)
    (logger: ILogger)
    (entries: ModuleConfigEntry list)
    (migrators: IConfigMigrator list)
    : Harness =

    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let support = {
        Registry = ConfigMigrationRegistry(entries, migrators)
        Drift = ConfigDriftTracker()
        EventStore = Some events
        Logger = logger
    }

    {
        Store = decorate support inner
        Inner = inner
        Events = events
        Support = support
    }

let private harness (entries: ModuleConfigEntry list) (migrators: IConfigMigrator list) : Harness =
    harnessWith (ConfigStore.create (tempStorage ())) (SilentLogger() :> ILogger) entries migrators

/// Seed a document straight into the inner store, bypassing the
/// decorator — the "written by an older release" starting state every
/// migration test needs.
let private seed (h: Harness) (moduleKey: string) (schema: ModuleConfigSchema) (values: Map<string, string>) = async {
    let! result = h.Inner.SetRaw(scope, moduleKey, values, schema)
    Expect.isOk result "seeding the pre-migration document should succeed"
}

let private eventsOfType (h: Harness) (eventType: string) = async {
    let! evts = h.Events.ReadByType(scope.ScopeId, eventType)
    return evts
}

// ─── 1. The rename migration is transparent ──────────────────────

let private renameMigration =
    testList "V1 -> V2 rename is transparent" [

        testCase "the module sees only V2 keys and the document is upgraded on first read"
        <| fun _ ->
            async {
                // V2 of the schema renames `model_id` to `model`. The
                // canonical example from the phase's acceptance criteria.
                let v2 = entry "Assistant" 2 [ stringField "model" "\"default\"" ]

                let renamer =
                    TestConfigMigrator(
                        "Assistant",
                        1,
                        2,
                        fun values ->
                            match values.TryFind "model_id" with
                            | Some v -> values |> Map.remove "model_id" |> Map.add "model" v
                            | None -> values
                    )

                let h = harness [ v2 ] [ renamer :> IConfigMigrator ]

                // A V1 document: the old key, and no version stamp at
                // all — which is what every document written before this
                // substrate existed looks like.
                let v1Schema = ModuleConfigSchema.ofFields [ stringField "model_id" "\"default\"" ]
                do! seed h "Assistant" v1Schema (Map.ofList [ "model_id", "\"claude\"" ])

                let! seen = h.Store.GetRaw(scope, "Assistant")

                Expect.equal
                    (seen.TryFind "model")
                    (Some "\"claude\"")
                    "the module sees the V2 key carrying the V1 value"

                Expect.isNone (seen.TryFind "model_id") "the module does not see the retired V1 key"

                Expect.isNone
                    (seen.TryFind ConfigMigrationMetadata.SchemaVersionKey)
                    "the reserved stamp is stripped before the caller sees the document"

                // ...and the upgrade was PERSISTED, not merely applied
                // in flight: read the inner store directly.
                let! persisted = h.Inner.GetRaw(scope, "Assistant")

                Expect.equal (persisted.TryFind "model") (Some "\"claude\"") "the persisted document carries the V2 key"

                Expect.equal
                    (ConfigMigrationMetadata.readVersion persisted)
                    2
                    "the persisted document is stamped at the version it was migrated to"

                // A second read is a no-op — the stamp says so. This is
                // what makes the lazy strategy affordable.
                let! again = h.Store.GetRaw(scope, "Assistant")
                Expect.equal (again.TryFind "model") (Some "\"claude\"") "the second read is stable"

                let! failures = eventsOfType h ConfigMigrationEvents.ConfigMigrationFailedEventType
                Expect.isEmpty failures "a successful migration emits no failure event"
            }
            |> Async.RunSynchronously

        testCase "a multi-step chain runs every hop in order"
        <| fun _ ->
            async {
                let v3 = entry "Assistant" 3 [ stringField "model" "\"default\"" ]

                let oneToTwo =
                    TestConfigMigrator(
                        "Assistant",
                        1,
                        2,
                        fun v ->
                            v
                            |> Map.remove "model_id"
                            |> Map.add "tmp" (v.TryFind "model_id" |> Option.defaultValue "\"?\"")
                    )

                let twoToThree =
                    TestConfigMigrator(
                        "Assistant",
                        2,
                        3,
                        fun v ->
                            v
                            |> Map.remove "tmp"
                            |> Map.add "model" (v.TryFind "tmp" |> Option.defaultValue "\"?\"")
                    )

                let h =
                    harness [ v3 ] [ oneToTwo :> IConfigMigrator; twoToThree :> IConfigMigrator ]

                let v1Schema = ModuleConfigSchema.ofFields [ stringField "model_id" "\"default\"" ]
                do! seed h "Assistant" v1Schema (Map.ofList [ "model_id", "\"opus\"" ])

                let! seen = h.Store.GetRaw(scope, "Assistant")
                Expect.equal (seen.TryFind "model") (Some "\"opus\"") "the value survives both hops"

                let! persisted = h.Inner.GetRaw(scope, "Assistant")

                Expect.equal
                    (ConfigMigrationMetadata.readVersion persisted)
                    3
                    "the document is stamped at the chain's end"
            }
            |> Async.RunSynchronously

        testCase "an already-current document is not re-migrated"
        <| fun _ ->
            async {
                let v2 = entry "Assistant" 2 [ stringField "model" "\"default\"" ]

                // A migrator that would corrupt the value if it ran.
                let poison =
                    TestConfigMigrator("Assistant", 1, 2, fun v -> v |> Map.add "model" "\"CORRUPTED\"")

                let h = harness [ v2 ] [ poison :> IConfigMigrator ]

                do!
                    seed
                        h
                        "Assistant"
                        v2.Schema
                        (Map.ofList [ "model", "\"sonnet\""; ConfigMigrationMetadata.SchemaVersionKey, "2" ])

                let! seen = h.Store.GetRaw(scope, "Assistant")

                Expect.equal
                    (seen.TryFind "model")
                    (Some "\"sonnet\"")
                    "a document already at the target version skips the chain entirely"
            }
            |> Async.RunSynchronously
    ]

// ─── 2. Failure policy ───────────────────────────────────────────

let private failurePolicy =
    testList "a failing migration degrades rather than raising" [

        testCase "leaves the V1 document, emits the event, logs, and hands back V1 values"
        <| fun _ ->
            async {
                let v2 = entry "Assistant" 2 [ stringField "model" "\"default\"" ]
                let thrower = ThrowingConfigMigrator("Assistant", 1, 2)
                let logger = CapturingLogger()

                let h =
                    harnessWith (ConfigStore.create (tempStorage ())) (logger :> ILogger) [ v2 ] [
                        thrower :> IConfigMigrator
                    ]

                let v1Schema = ModuleConfigSchema.ofFields [ stringField "model_id" "\"default\"" ]
                do! seed h "Assistant" v1Schema (Map.ofList [ "model_id", "\"claude\"" ])

                // The read succeeds — it does NOT raise. A config read
                // that throws takes out `Init` for a module whose only
                // problem is that someone owes it a migrator.
                let! seen = h.Store.GetRaw(scope, "Assistant")

                Expect.equal
                    (seen.TryFind "model_id")
                    (Some "\"claude\"")
                    "the module sees V1-shaped values, stale but readable"

                let! persisted = h.Inner.GetRaw(scope, "Assistant")

                Expect.equal
                    (ConfigMigrationMetadata.readVersion persisted)
                    ConfigMigrationMetadata.InitialVersion
                    "the source document is left at V1, unstamped"

                Expect.equal
                    (persisted.TryFind "model_id")
                    (Some "\"claude\"")
                    "the source document's values are untouched"

                let! failures = eventsOfType h ConfigMigrationEvents.ConfigMigrationFailedEventType
                Expect.isNonEmpty failures "a failed migration emits ConfigMigrationFailed"

                let evt = List.head failures

                Expect.equal
                    evt.SourceModule
                    ConfigMigrationEvents.SourceModule
                    "the event is filed under the reserved _platform.config source"

                Expect.isNonEmpty logger.Warnings "the operator gets a Warn line, not just an event"

                Expect.isTrue
                    (logger.Warnings |> List.exists (fun w -> w.Contains "Assistant"))
                    "the Warn line names the module whose migration failed"
            }
            |> Async.RunSynchronously

        testCase "a chain failing at the second hop returns the last cleanly-resolved version and persists nothing"
        <| fun _ ->
            async {
                let v3 = entry "Assistant" 3 [ stringField "model" "\"default\"" ]

                let oneToTwo =
                    TestConfigMigrator("Assistant", 1, 2, fun v -> v |> Map.add "hop1" "\"ran\"")

                let twoToThree = ThrowingConfigMigrator("Assistant", 2, 3)

                let h =
                    harness [ v3 ] [ oneToTwo :> IConfigMigrator; twoToThree :> IConfigMigrator ]

                let v1Schema = ModuleConfigSchema.ofFields [ stringField "model" "\"default\"" ]
                do! seed h "Assistant" v1Schema (Map.ofList [ "model", "\"claude\"" ])

                let! seen = h.Store.GetRaw(scope, "Assistant")

                Expect.equal
                    (seen.TryFind "hop1")
                    (Some "\"ran\"")
                    "the caller sees the last version that resolved cleanly (V2), which is coherent, not partial"

                let! persisted = h.Inner.GetRaw(scope, "Assistant")

                Expect.isNone
                    (persisted.TryFind "hop1")
                    "the partially-advanced document is NOT written back — a document stamped mid-chain would be indistinguishable from a completed one"

                Expect.equal
                    (ConfigMigrationMetadata.readVersion persisted)
                    ConfigMigrationMetadata.InitialVersion
                    "the source document stays at its original version"
            }
            |> Async.RunSynchronously

        testCase "a migrator producing a schema-invalid document fails rather than persisting it"
        <| fun _ ->
            async {
                let v2 = entry "Assistant" 2 [ stringField "model" "\"default\"" ]

                // Emits a key the V2 schema does not declare, so the
                // inner store's validating SetRaw refuses the write.
                let bad =
                    TestConfigMigrator("Assistant", 1, 2, fun v -> v |> Map.add "not_in_schema" "\"x\"")

                let h = harness [ v2 ] [ bad :> IConfigMigrator ]

                let v1Schema = ModuleConfigSchema.ofFields [ stringField "model" "\"default\"" ]
                do! seed h "Assistant" v1Schema (Map.ofList [ "model", "\"claude\"" ])

                let! _ = h.Store.GetRaw(scope, "Assistant")
                let! persisted = h.Inner.GetRaw(scope, "Assistant")

                Expect.isNone
                    (persisted.TryFind "not_in_schema")
                    "an invalid migration result is caught by the inner store's validation, not persisted"

                let! failures = eventsOfType h ConfigMigrationEvents.ConfigMigrationFailedEventType
                Expect.isNonEmpty failures "the refused write is reported as the migration failure it is"
            }
            |> Async.RunSynchronously

        testCase "an unresolvable chain reports and degrades to the persisted values"
        <| fun _ ->
            async {
                // Declares V2 but registers nothing — the "released the
                // schema, forgot the migrator" case.
                let v2 = entry "Assistant" 2 [ stringField "model" "\"default\"" ]
                let h = harness [ v2 ] []

                let v1Schema = ModuleConfigSchema.ofFields [ stringField "model" "\"default\"" ]
                do! seed h "Assistant" v1Schema (Map.ofList [ "model", "\"claude\"" ])

                let! seen = h.Store.GetRaw(scope, "Assistant")
                Expect.equal (seen.TryFind "model") (Some "\"claude\"") "values are still readable"

                let! failures = eventsOfType h ConfigMigrationEvents.ConfigMigrationFailedEventType
                Expect.isNonEmpty failures "the missing chain is reported"
            }
            |> Async.RunSynchronously
    ]

// ─── 3. The reserved-key contract, over two implementations ──────

/// The contract pack. Written once, run against every implementation —
/// the shape `IJobSchedulerContract` and its siblings established, and
/// the only shape under which "honoured by ALL implementations" is a
/// checked claim rather than a convention.
let private reservedKeyContractFor (name: string) (makeInner: unit -> IConfigStore) =
    testList name [

        testCase "a version-1 schema writes no stamp (adoption is free)"
        <| fun _ ->
            async {
                let v1 = entry "Plain" 1 [ stringField "a" "\"\"" ]
                let h = harnessWith (makeInner ()) (SilentLogger() :> ILogger) [ v1 ] []

                let! result = h.Store.SetRaw(scope, "Plain", Map.ofList [ "a", "\"x\"" ], v1.Schema)
                Expect.isOk result "the write succeeds"

                let! persisted = h.Inner.GetRaw(scope, "Plain")

                Expect.isNone
                    (persisted.TryFind ConfigMigrationMetadata.SchemaVersionKey)
                    "a deployment that never declares a version writes byte-for-byte what it wrote before (GP 11)"
            }
            |> Async.RunSynchronously

        testCase "a versioned schema stamps on write and the stamp round-trips"
        <| fun _ ->
            async {
                let v2 = entry "Versioned" 2 [ stringField "a" "\"\"" ]
                let h = harnessWith (makeInner ()) (SilentLogger() :> ILogger) [ v2 ] []

                let! result = h.Store.SetRaw(scope, "Versioned", Map.ofList [ "a", "\"x\"" ], v2.Schema)
                Expect.isOk result "the write succeeds despite the reserved key not being a schema field"

                let! persisted = h.Inner.GetRaw(scope, "Versioned")

                Expect.equal
                    (ConfigMigrationMetadata.readVersion persisted)
                    2
                    "the stamp survives the store's schema validation and round-trips"
            }
            |> Async.RunSynchronously

        testCase "the stamp never reaches the caller"
        <| fun _ ->
            async {
                let v2 = entry "Versioned" 2 [ stringField "a" "\"\"" ]
                let h = harnessWith (makeInner ()) (SilentLogger() :> ILogger) [ v2 ] []

                do!
                    seed
                        h
                        "Versioned"
                        v2.Schema
                        (Map.ofList [ "a", "\"x\""; ConfigMigrationMetadata.SchemaVersionKey, "2" ])

                let! seen = h.Store.GetRaw(scope, "Versioned")

                Expect.isNone
                    (seen.TryFind ConfigMigrationMetadata.SchemaVersionKey)
                    "GetRaw through the decorator strips the reserved key"

                Expect.equal (seen.TryFind "a") (Some "\"x\"") "the declared field is unaffected"
            }
            |> Async.RunSynchronously

        testCase "a caller cannot forge a version stamp"
        <| fun _ ->
            async {
                let v2 = entry "Versioned" 2 [ stringField "a" "\"\"" ]
                let h = harnessWith (makeInner ()) (SilentLogger() :> ILogger) [ v2 ] []

                // Someone hands the store a document claiming version 99.
                let! result =
                    h.Store.SetRaw(
                        scope,
                        "Versioned",
                        Map.ofList [ "a", "\"x\""; ConfigMigrationMetadata.SchemaVersionKey, "99" ],
                        v2.Schema
                    )

                Expect.isOk result "the write is accepted"

                let! persisted = h.Inner.GetRaw(scope, "Versioned")

                Expect.equal
                    (ConfigMigrationMetadata.readVersion persisted)
                    2
                    "the forged stamp is discarded and replaced with the schema's declared version"
            }
            |> Async.RunSynchronously
    ]

let private reservedKeyContract =
    testList "the _schema_version reserved key is honoured by every implementation" [
        reservedKeyContractFor "blob-backed (the shipped default)" (fun () -> ConfigStore.create (tempStorage ()))
        reservedKeyContractFor "in-memory (an implementation that knows nothing about the substrate)" (fun () ->
            InMemoryConfigStore() :> IConfigStore)
    ]

// ─── 4. Chain resolution refuses rather than guessing ────────────

let private chainResolution =
    testList "chain resolution" [

        testCase "a gap in the chain is NoConfigMigrationPath"
        <| fun _ ->
            let m = TestConfigMigrator("M", 2, 3, id) :> IConfigMigrator
            let result = ConfigMigrationChain.resolve "M" [ m ] 1 3

            match result with
            | Error(NoConfigMigrationPath("M", 1)) -> ()
            | other -> failtestf "expected NoConfigMigrationPath at version 1, got %A" other

        testCase "two migrators reading the same version is AmbiguousConfigMigrationStep"
        <| fun _ ->
            let a = TestConfigMigrator("M", 1, 2, id) :> IConfigMigrator
            let b = TestConfigMigrator("M", 1, 3, id) :> IConfigMigrator

            match ConfigMigrationChain.resolve "M" [ a; b ] 1 3 with
            | Error(AmbiguousConfigMigrationStep("M", 1, 2)) -> ()
            | other -> failtestf "expected AmbiguousConfigMigrationStep, got %A" other

        testCase "a non-advancing migrator is refused before it can loop"
        <| fun _ ->
            let m = TestConfigMigrator("M", 2, 2, id) :> IConfigMigrator

            match ConfigMigrationChain.resolve "M" [ m ] 1 3 with
            | Error(NonAdvancingConfigMigrator("M", 2, 2)) -> ()
            | other -> failtestf "expected NonAdvancingConfigMigrator, got %A" other

        testCase "a chain overshooting the declared version is refused"
        <| fun _ ->
            let m = TestConfigMigrator("M", 1, 5, id) :> IConfigMigrator

            match ConfigMigrationChain.resolve "M" [ m ] 1 2 with
            | Error(ConfigChainOvershootsTarget("M", 5, 2)) -> ()
            | other -> failtestf "expected ConfigChainOvershootsTarget, got %A" other

        testCase "an already-current document resolves to no steps"
        <| fun _ ->
            match ConfigMigrationChain.resolve "M" [] 2 2 with
            | Ok [] -> ()
            | other -> failtestf "expected an empty chain, got %A" other

        testCase "a document ahead of this release resolves to no steps rather than erroring"
        <| fun _ ->
            // An ordinary rolling-deploy state: an older node reads a
            // document a newer node already upgraded.
            match ConfigMigrationChain.resolve "M" [] 5 2 with
            | Ok [] -> ()
            | other -> failtestf "expected an empty chain for a document ahead of the target, got %A" other

        testCase "ValidateAll surfaces registration defects with no storage involved"
        <| fun _ ->
            let registry =
                ConfigMigrationRegistry(
                    [ entry "M" 3 [ stringField "a" "\"\"" ] ],
                    [ TestConfigMigrator("M", 1, 2, id) :> IConfigMigrator ]
                )

            let errors = registry.ValidateAll()
            Expect.isNonEmpty errors "a chain that cannot reach the declared version is a registration defect"

            Expect.isTrue
                (errors
                 |> List.forall (fun e -> (ConfigMigrationChainError.describe e).Contains "M"))
                "every description names the module it is about"

        testCase "a deployment declaring nothing is inert"
        <| fun _ ->
            let registry =
                ConfigMigrationRegistry([ entry "M" 1 [ stringField "a" "\"\"" ] ], [])

            Expect.isTrue registry.IsInert "no declared version and no migrator means the substrate costs nothing"
    ]

// ─── 5. Pre-migration drift observability (gap audit, Gap 7) ─────

let private driftObservability =
    testList "pre-migration drift observability" [

        testCase "a rename with no migrator records both halves and emits the event"
        <| fun _ ->
            async {
                // The schema moved to `model`; nobody wrote a migrator;
                // the version was not even bumped. This is precisely the
                // silent case the gap audit names.
                let v1 = entry "Assistant" 1 [ stringField "model" "\"default\"" ]
                let h = harness [ v1 ] []

                do!
                    seed
                        h
                        "Assistant"
                        (ModuleConfigSchema.ofFields [ stringField "model_id" "\"d\"" ])
                        (Map.ofList [ "model_id", "\"claude\"" ])

                let! _ = h.Store.GetRaw(scope, "Assistant")

                let summaries = h.Support.Drift.Summarise h.Support.Registry
                Expect.hasLength summaries 1 "one module has drift"

                let s = List.head summaries
                Expect.equal s.ModuleKey "Assistant" "the drift is attributed to the module"

                Expect.equal
                    s.MissingFields
                    [ "model" ]
                    "the schema field that silently fell back to its default is named"

                Expect.equal
                    s.OrphanedKeys
                    [ "model_id" ]
                    "the persisted key whose value is being dropped is named — the evidence half"

                Expect.isFalse s.HasMigrators "and the panel says no migrator is registered, which is the headline"

                let! drift = eventsOfType h ConfigMigrationEvents.FieldMigrationNeededEventType
                Expect.hasLength drift 2 "one ModuleConfigFieldMigrationNeeded event per observation"
            }
            |> Async.RunSynchronously

        testCase "an unconfigured module produces no drift"
        <| fun _ ->
            async {
                // Every field falls back to its default here too — but
                // that is the normal case, not drift. Counting it would
                // bury the real signal under one row per module per read.
                let v1 = entry "Assistant" 1 [ stringField "model" "\"default\"" ]
                let h = harness [ v1 ] []

                let! _ = h.Store.GetRaw(scope, "Assistant")

                Expect.equal h.Support.Drift.TotalObservations 0 "an empty document is not drift"
            }
            |> Async.RunSynchronously

        testCase "a document matching its schema produces no drift"
        <| fun _ ->
            async {
                let v1 = entry "Assistant" 1 [ stringField "model" "\"default\"" ]
                let h = harness [ v1 ] []

                do! seed h "Assistant" v1.Schema (Map.ofList [ "model", "\"claude\"" ])
                let! _ = h.Store.GetRaw(scope, "Assistant")

                Expect.equal h.Support.Drift.TotalObservations 0 "a document in agreement with its schema is silent"
            }
            |> Async.RunSynchronously

        testCase "a successful migration leaves no drift behind"
        <| fun _ ->
            async {
                let v2 = entry "Assistant" 2 [ stringField "model" "\"default\"" ]

                let renamer =
                    TestConfigMigrator(
                        "Assistant",
                        1,
                        2,
                        fun v ->
                            match v.TryFind "model_id" with
                            | Some x -> v |> Map.remove "model_id" |> Map.add "model" x
                            | None -> v
                    )

                let h = harness [ v2 ] [ renamer :> IConfigMigrator ]

                do!
                    seed
                        h
                        "Assistant"
                        (ModuleConfigSchema.ofFields [ stringField "model_id" "\"d\"" ])
                        (Map.ofList [ "model_id", "\"claude\"" ])

                let! _ = h.Store.GetRaw(scope, "Assistant")

                Expect.equal
                    h.Support.Drift.TotalObservations
                    0
                    "drift is observed AFTER migration, so a shipped migrator resolves it rather than reporting it"
            }
            |> Async.RunSynchronously

        testCase "repeated reads accumulate observations but not distinct fields"
        <| fun _ ->
            async {
                let v1 = entry "Assistant" 1 [ stringField "model" "\"default\"" ]
                let h = harness [ v1 ] []

                do!
                    seed
                        h
                        "Assistant"
                        (ModuleConfigSchema.ofFields [ stringField "model_id" "\"d\"" ])
                        (Map.ofList [ "model_id", "\"claude\"" ])

                for _ in 1..3 do
                    let! _ = h.Store.GetRaw(scope, "Assistant")
                    ()

                let s = h.Support.Drift.Summarise h.Support.Registry |> List.head
                Expect.equal s.DistinctFields 2 "still two distinct drifting fields"
                Expect.equal s.Observations 6 "but six observations — the prioritisation signal"
            }
            |> Async.RunSynchronously

        testCase "the /dev/inspect panel renders and names the substrate's state"
        <| fun _ ->
            async {
                let v2 = entry "Assistant" 2 [ stringField "model" "\"default\"" ]

                let h =
                    harness [ v2 ] [ TestConfigMigrator("Assistant", 1, 2, id) :> IConfigMigrator ]

                let contributor =
                    ConfigMigrationDevDiagnostics.PendingConfigMigrationsContributor(h.Support)
                    :> IDevDiagnosticsContributor

                let! name, payload = contributor.Contribute()

                Expect.equal name "Pending Config Migrations" "the panel name is stable — operators bookmark it"
                Expect.isNotNull (box payload) "the panel renders a payload"
            }
            |> Async.RunSynchronously
    ]

// ─── 6. The builder surface ──────────────────────────────────────

let private builderSurface =
    testList "ServerModule.withConfigMigration" [

        testCase "appends, so a module accumulates its chain"
        <| fun _ ->
            let a = TestConfigMigrator("M", 1, 2, id) :> IConfigMigrator
            let b = TestConfigMigrator("M", 2, 3, id) :> IConfigMigrator

            let m =
                ServerModule.create "M"
                |> ServerModule.withConfigMigration a
                |> ServerModule.withConfigMigration b

            Expect.hasLength m.ConfigMigrations 2 "both steps are retained"
            Expect.equal m.ConfigMigrations[0].ToVersion 2 "registration order is preserved"

        testCase "a module declaring none contributes the empty list"
        <| fun _ ->
            let m = ServerModule.create "M"
            Expect.isEmpty m.ConfigMigrations "the pre-10b shape is unchanged (GP 11)"

        testCase "ModuleConfigSchema defaults to the implicit floor"
        <| fun _ ->
            Expect.equal ModuleConfigSchema.empty.SchemaVersion 1 "the empty schema sits at version 1"

            Expect.equal
                (ModuleConfigSchema.ofFields []).SchemaVersion
                1
                "a schema built the pre-10b way sits at version 1"
    ]

let tests =
    testList "Phase 10b — configuration schema evolution" [
        renameMigration
        failurePolicy
        reservedKeyContract
        chainResolution
        driftObservability
        builderSurface
    ]