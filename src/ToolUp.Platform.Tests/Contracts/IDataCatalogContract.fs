module ToolUp.Platform.Tests.Contracts.IDataCatalogContract

open System
open Expecto
open ToolUp.Platform
open DataManagementTypes
open ToolUp.Platform.FileProcessor
open ProcessedDataTypes

/// Minimal in-memory `IDataObjectStore` for catalog tests. Only the
/// methods the catalog actually exercises (`Save`, `Get`,
/// `ListObjects`) are implemented faithfully; the others return
/// stubs. Keeping the test substrate independent of the blob-backed
/// `DataObjectStore` means catalog assertions don't fail because of
/// blob-layer regressions.
type private InMemoryObjectStore() =
    let store =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, DataObject * byte[]>()

    interface IDataObjectStore with
        member _.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy) = async {
            let obj = {
                ObjectId = objectId
                Version = 1
                CreatedAt = DateTime.UtcNow
                CreatedBy = createdBy
                ScopeId = scopeId
                DataType = dataType
                ContentHash = ""
                Policy = policy
                Metadata = metadata
            }

            store[(scopeId, objectId)] <- obj, content
            return Ok obj
        }

        member _.Get(scopeId, objectId) = async {
            match store.TryGetValue((scopeId, objectId)) with
            | true, (obj, bytes) -> return Ok(obj, bytes)
            | false, _ -> return Error NotFound
        }

        member this.GetVersion(scopeId, objectId, _version) =
            (this :> IDataObjectStore).Get(scopeId, objectId)

        // Stub — the catalog tests don't read content by hash (this
        // double stores by objectId with an empty ContentHash).
        member _.GetContent(_, _) = async { return Error NotFound }

        member _.ListVersions(_, _) = async { return [] }

        member _.ListObjects(scopeId) = async {
            return
                store
                |> Seq.filter (fun kvp -> fst kvp.Key = scopeId)
                |> Seq.map (fun kvp -> fst kvp.Value)
                |> Seq.toList
        }

        member _.Recover(_, _, _, _) = async { return Error NotFound }
        member _.Delete(_, _) = async { return Ok() }

        member _.Evict(scopeId, objectId) = async {
            store.TryRemove((scopeId, objectId)) |> ignore
            return Ok()
        }

        member _.Purge(_) = async { return Ok() }

        member _.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "data-objects"
                        RecordsAffected = 0
                        Note = None
                    }
            else
                let namesSubject (o: DataObject) =
                    o.CreatedBy = subjectUserId
                    || (o.Metadata |> Map.exists (fun _ v -> v.Contains subjectUserId))

                let matchedKeys =
                    store
                    |> Seq.filter (fun kvp -> fst kvp.Key = scopeId && namesSubject (fst kvp.Value))
                    |> Seq.map _.Key
                    |> Seq.toList

                if not dryRun then
                    for key in matchedKeys do
                        match policy with
                        | ErasurePolicy.HardDelete -> store.TryRemove key |> ignore
                        | _ ->
                            match store.TryGetValue key with
                            | true, (o, bytes) ->
                                let redacted = {
                                    o with
                                        CreatedBy =
                                            (if o.CreatedBy = subjectUserId then
                                                 Erasure.TombstoneMarker
                                             else
                                                 o.CreatedBy)
                                        Metadata =
                                            o.Metadata
                                            |> Map.map (fun _ v ->
                                                if v.Contains subjectUserId then
                                                    Erasure.TombstoneMarker
                                                else
                                                    v)
                                }

                                let newBytes =
                                    if policy = ErasurePolicy.Tombstone then
                                        System.Text.Encoding.UTF8.GetBytes Erasure.TombstoneMarker
                                    else
                                        bytes

                                store[key] <- (redacted, newBytes)
                            | false, _ -> ()

                return
                    Result.Ok {
                        HandlerName = "data-objects"
                        RecordsAffected = matchedKeys.Length
                        Note = None
                    }
        }

/// Construct an in-memory object store. Tests share this with the
/// catalog so `ListObjects` can observe pre-saved objects.
let inMemoryObjectStore () : IDataObjectStore =
    InMemoryObjectStore() :> IDataObjectStore

/// Contract test list for any `IDataCatalog` implementation. The
/// factory takes the registrations and the (caller-supplied)
/// `IDataObjectStore` and returns the catalog plus two scope ids
/// (for scope-isolation assertions on `ListObjects`).
let tests (name: string) (factory: (string * DataType) list * IDataObjectStore -> IDataCatalog * string * string) =

    let stubProcess (_: string * string) : Async<ProcessedData * ProcessedFileEntry> = async {
        return
            { TypeName = ""; Payload = "" },
            {
                FileName = ""
                DataType = ""
                ProcessedAt = DateTime.UtcNow
                Info = None
                Error = None
            }
    }

    let mkType (id: string) (displayName: string) (schema: DataTypeSchema option) : DataType = {
        Info = {
            Id = id
            DisplayName = displayName
            Schema = schema
        }
        Id = id
        SchemaVersion = DataTypes.initialSchemaVersion
        Migrations = []
        Detect = fun _ -> async { return false }
        Process = stubProcess
    }

    testList $"{name} — IDataCatalog contract" [

        testCaseAsync "ListTypes returns every registered type"
        <| async {
            let salesType = mkType "Sales" "Sales Data" None
            let mediaType = mkType "Media" "Media Data" None

            let registrations = [ "SalesAnalysis", salesType; "MediaOptimisation", mediaType ]

            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory (registrations, store)

            let! types = catalog.ListTypes()
            let ids = types |> List.map _.Id |> Set.ofList
            Expect.equal ids (Set.ofList [ "Sales"; "Media" ]) "both types listed"
        }

        testCaseAsync "ListTypes deduplicates when two modules declare the same Id"
        <| async {
            let sharedType = mkType "Shared" "Shared Data" None

            let registrations = [ "ModuleA", sharedType; "ModuleB", sharedType ]

            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory (registrations, store)

            let! types = catalog.ListTypes()
            Expect.equal types.Length 1 "single entry per Id"
            Expect.equal types[0].Id "Shared" "shared id surfaced once"
        }

        testCaseAsync "GetSchema returns the schema when published"
        <| async {
            let schema = {
                Description = "Quarterly sales feed"
                Columns = [
                    {
                        Name = "Region"
                        Type = StringColumn
                        Required = true
                        Description = None
                    }
                    {
                        Name = "Revenue"
                        Type = NumberColumn
                        Required = true
                        Description = Some "USD"
                    }
                ]
            }

            let salesType = mkType "Sales" "Sales Data" (Some schema)

            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory ([ "SalesAnalysis", salesType ], store)

            let! result = catalog.GetSchema "Sales"

            match result with
            | Some s ->
                Expect.equal s.Description "Quarterly sales feed" "description preserved"
                Expect.equal s.Columns.Length 2 "two columns"
            | None -> failtest "Expected schema; got None"
        }

        testCaseAsync "GetSchema returns None when no schema published"
        <| async {
            let salesType = mkType "Sales" "Sales Data" None
            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory ([ "SalesAnalysis", salesType ], store)

            let! result = catalog.GetSchema "Sales"
            Expect.isNone result "no schema published"
        }

        testCaseAsync "GetSchema returns None for unknown type"
        <| async {
            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory ([], store)

            let! result = catalog.GetSchema "Nonexistent"
            Expect.isNone result "unknown type returns None"
        }

        testCaseAsync "GetProducers returns the module name for single-producer type"
        <| async {
            let salesType = mkType "Sales" "Sales Data" None
            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory ([ "SalesAnalysis", salesType ], store)

            let! producers = catalog.GetProducers "Sales"
            Expect.equal producers [ "SalesAnalysis" ] "single producer"
        }

        testCaseAsync "GetProducers returns all module names for multi-producer type"
        <| async {
            let sharedType = mkType "Shared" "Shared Data" None

            let registrations = [ "ModuleA", sharedType; "ModuleB", sharedType; "ModuleC", sharedType ]

            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory (registrations, store)

            let! producers = catalog.GetProducers "Shared"
            Expect.equal (Set.ofList producers) (Set.ofList [ "ModuleA"; "ModuleB"; "ModuleC" ]) "all three producers"
        }

        testCaseAsync "GetProducers returns empty list for unknown type"
        <| async {
            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory ([], store)

            let! producers = catalog.GetProducers "Nonexistent"
            Expect.equal producers [] "no producers"
        }

        testCaseAsync "ListObjects returns only objects whose DataType matches"
        <| async {
            let salesType = mkType "Sales" "Sales Data" None
            let mediaType = mkType "Media" "Media Data" None

            let registrations = [ "SalesAnalysis", salesType; "MediaOptimisation", mediaType ]

            let store = inMemoryObjectStore ()
            let catalog, scopeA, _ = factory (registrations, store)

            let bytesOf (s: string) = System.Text.Encoding.UTF8.GetBytes s

            let! _ = store.Save(scopeA, "obj-1", bytesOf "x", "Sales", "u", Map.empty, Unversioned)
            let! _ = store.Save(scopeA, "obj-2", bytesOf "y", "Sales", "u", Map.empty, Unversioned)
            let! _ = store.Save(scopeA, "obj-3", bytesOf "z", "Media", "u", Map.empty, Unversioned)

            let! salesObjects = catalog.ListObjects(scopeA, "Sales")
            Expect.equal salesObjects.Length 2 "two Sales objects"

            let! mediaObjects = catalog.ListObjects(scopeA, "Media")
            Expect.equal mediaObjects.Length 1 "one Media object"

            let! noneOfThese = catalog.ListObjects(scopeA, "Unknown")
            Expect.equal noneOfThese.Length 0 "unknown type returns empty"
        }

        testCaseAsync "ListObjects respects scope isolation"
        <| async {
            let salesType = mkType "Sales" "Sales Data" None
            let store = inMemoryObjectStore ()
            let catalog, scopeA, scopeB = factory ([ "SalesAnalysis", salesType ], store)

            let bytesOf (s: string) = System.Text.Encoding.UTF8.GetBytes s
            let! _ = store.Save(scopeA, "a-only", bytesOf "x", "Sales", "u", Map.empty, Unversioned)

            let! inA = catalog.ListObjects(scopeA, "Sales")
            let! inB = catalog.ListObjects(scopeB, "Sales")
            Expect.equal inA.Length 1 "scopeA sees its object"
            Expect.equal inB.Length 0 "scopeB sees nothing"
        }

        // ─── Phase 30d — GetSyntheticSample reproducibility + adversarial ───

        testCaseAsync "GetSyntheticSample returns byte-identical bytes for identical (typeId, count, seed)"
        <| async {
            let schema = {
                Description = "Test"
                Columns = [
                    {
                        Name = "Region"
                        Type = StringColumn
                        Required = true
                        Description = None
                    }
                    {
                        Name = "Revenue"
                        Type = NumberColumn
                        Required = true
                        Description = None
                    }
                    {
                        Name = "AsOf"
                        Type = DateColumn
                        Required = false
                        Description = None
                    }
                    {
                        Name = "IsActive"
                        Type = BooleanColumn
                        Required = false
                        Description = None
                    }
                ]
            }

            let salesType = mkType "Sales" "Sales Data" (Some schema)
            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory ([ "SalesAnalysis", salesType ], store)

            let! (objA, bytesA) = catalog.GetSyntheticSample("Sales", 10, 42)
            let! (objB, bytesB) = catalog.GetSyntheticSample("Sales", 10, 42)

            Expect.equal bytesA bytesB "byte-identical for same seed"
            Expect.equal objA.ContentHash objB.ContentHash "ContentHash byte-identical for same seed"
            Expect.equal objA.ObjectId objB.ObjectId "ObjectId stable for same seed"
        }

        testCaseAsync "GetSyntheticSample with different seeds returns different bytes"
        <| async {
            let schema = {
                Description = "Test"
                Columns = [
                    {
                        Name = "Region"
                        Type = StringColumn
                        Required = true
                        Description = None
                    }
                ]
            }

            let salesType = mkType "Sales" "Sales Data" (Some schema)
            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory ([ "SalesAnalysis", salesType ], store)

            let! (_, bytesA) = catalog.GetSyntheticSample("Sales", 20, 1)
            let! (_, bytesB) = catalog.GetSyntheticSample("Sales", 20, 2)

            Expect.notEqual bytesA bytesB "different seeds yield different bytes"
        }

        testCaseAsync "GetSyntheticSample on unknown typeId returns well-formed header-only CSV"
        <| async {
            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory ([], store)

            let! (obj, bytes) = catalog.GetSyntheticSample("Nonexistent", 5, 42)
            let body = System.Text.Encoding.UTF8.GetString bytes

            // Unknown type id surfaces no Schema; the generator falls
            // back to a header-only placeholder column so the bytes
            // are still valid CSV (header present + no rows because
            // schema-less + the explicit fallback column).
            Expect.isTrue (body.Contains "_synthetic_empty") "fallback placeholder header present"
            Expect.equal obj.ScopeId "_synthetic" "synthetic sentinel scope"
            Expect.equal obj.CreatedBy "_synthetic" "synthetic sentinel CreatedBy"
        }

        testCaseAsync "GetSyntheticSample DataObject metadata records seed + count"
        <| async {
            let schema = {
                Description = "Test"
                Columns = [
                    {
                        Name = "X"
                        Type = StringColumn
                        Required = false
                        Description = None
                    }
                ]
            }

            let salesType = mkType "Sales" "Sales Data" (Some schema)
            let store = inMemoryObjectStore ()
            let catalog, _, _ = factory ([ "SalesAnalysis", salesType ], store)

            let! (obj, _) = catalog.GetSyntheticSample("Sales", 7, 99)

            Expect.equal (obj.Metadata |> Map.tryFind "synthetic.seed") (Some "99") "seed recorded"
            Expect.equal (obj.Metadata |> Map.tryFind "synthetic.count") (Some "7") "count recorded"
            Expect.equal obj.DataType "Sales" "DataType preserved"
        }

        testCaseAsync "GetSyntheticSample never touches the real object store"
        <| async {
            // Adversarial: if a SchemaOnly caller's GetSyntheticSample
            // somehow returned bytes from the real store, the partner
            // would see real-row data. This asserts the substrate
            // path: a sample call must produce its bytes from the
            // schema-driven generator, never from a real-blob read.
            //
            // We assert this structurally — pre-populate the real
            // store with bytes that include an unmistakable canary
            // string, generate a sample, and verify the canary is
            // absent from the synthetic bytes regardless of seed.
            let schema = {
                Description = "Test"
                Columns = [
                    {
                        Name = "Notes"
                        Type = StringColumn
                        Required = false
                        Description = None
                    }
                ]
            }

            let salesType = mkType "Sales" "Sales Data" (Some schema)
            let store = inMemoryObjectStore ()
            let catalog, scopeA, _ = factory ([ "SalesAnalysis", salesType ], store)

            let canary = "PHASE_30D_REAL_DATA_CANARY_DO_NOT_LEAK"

            let! _ =
                store.Save(
                    scopeA,
                    "real-1",
                    System.Text.Encoding.UTF8.GetBytes canary,
                    "Sales",
                    "u",
                    Map.empty,
                    Unversioned
                )

            for seed in 0..9 do
                let! (_, bytes) = catalog.GetSyntheticSample("Sales", 5, seed)
                let body = System.Text.Encoding.UTF8.GetString bytes
                Expect.isFalse (body.Contains canary) (sprintf "synthetic bytes do not leak real data (seed=%d)" seed)
        }

        testCaseAsync "GetSyntheticSample adversarial typeId — sub-path traversal does not return real bytes"
        <| async {
            // Adversarial: partners might submit typeId values with
            // path-traversal characters, wildcard escapes, JSON-
            // injection sequences, or container-name sentinels. The
            // generator must handle each as an opaque string — never
            // resolve into a real-blob path.
            let schema = {
                Description = "Test"
                Columns = [
                    {
                        Name = "C"
                        Type = StringColumn
                        Required = false
                        Description = None
                    }
                ]
            }

            let salesType = mkType "Sales" "Sales Data" (Some schema)
            let store = inMemoryObjectStore ()
            let catalog, scopeA, _ = factory ([ "SalesAnalysis", salesType ], store)

            let canary = "PHASE_30D_TRAVERSAL_CANARY"

            let! _ =
                store.Save(scopeA, "x", System.Text.Encoding.UTF8.GetBytes canary, "Sales", "u", Map.empty, Unversioned)

            let adversarial = [
                "../_data/sales"
                "../../_platform"
                "Sales/../../_data"
                "*.json"
                "\"; DROP TABLE; --"
                "{\"$gte\":\"\"}"
                "_synthetic"
                "_content"
                System.String('A', 4096)
            ]

            for typeId in adversarial do
                let! (_, bytes) = catalog.GetSyntheticSample(typeId, 3, 1)
                let body = System.Text.Encoding.UTF8.GetString bytes
                Expect.isFalse (body.Contains canary) (sprintf "synthetic bytes opaque on adversarial typeId %A" typeId)
        }

        // ─── Phase 30d — SchemaOnlyGuard contract ──────────────────────

        testCaseAsync "SchemaOnlyGuard: caller with only [SchemaOnly] is gated"
        <| async {
            let ctx = {
                UserId = "partner-x"
                TeamId = Some "team-x"
                Subject = TeamMember("partner-x", "team-x")
                ModulePermissions = Map.ofList [ "Sales", [ ModulePermission.SchemaOnly ] ]
                ModuleExposure = Map.empty
                PlatformRole = None
            }

            Expect.isTrue (SchemaOnlyGuard.isSchemaOnlyCaller ctx "Sales") "SchemaOnly-only caller flagged"
        }

        testCaseAsync "SchemaOnlyGuard: caller with [SchemaOnly; Read] is NOT gated"
        <| async {
            let ctx = {
                UserId = "u"
                TeamId = Some "t"
                Subject = TeamMember("u", "t")
                ModulePermissions = Map.ofList [ "Sales", [ ModulePermission.SchemaOnly; ModulePermission.Read ] ]
                ModuleExposure = Map.empty
                PlatformRole = None
            }

            Expect.isFalse (SchemaOnlyGuard.isSchemaOnlyCaller ctx "Sales") "real-data grant lifts the gate"
        }

        testCaseAsync "SchemaOnlyGuard: caller with only [Read] is NOT gated"
        <| async {
            let ctx = {
                UserId = "u"
                TeamId = Some "t"
                Subject = TeamMember("u", "t")
                ModulePermissions = Map.ofList [ "Sales", [ ModulePermission.Read ] ]
                ModuleExposure = Map.empty
                PlatformRole = None
            }

            Expect.isFalse (SchemaOnlyGuard.isSchemaOnlyCaller ctx "Sales") "Read-only is not gated"
        }

        testCaseAsync "SchemaOnlyGuard: unrestricted (empty map) caller is NOT gated"
        <| async {
            let ctx = AccessContext.unrestricted (TeamMember("u", "t"))
            Expect.isFalse (SchemaOnlyGuard.isSchemaOnlyCaller ctx "Sales") "unrestricted is fail-open"
        }

        testCaseAsync "ModulePermission.implies — Admin / Write / Read all imply SchemaOnly"
        <| async {
            Expect.isTrue
                (ModulePermission.implies ModulePermission.Admin ModulePermission.SchemaOnly)
                "Admin -> SchemaOnly"

            Expect.isTrue
                (ModulePermission.implies ModulePermission.Write ModulePermission.SchemaOnly)
                "Write -> SchemaOnly"

            Expect.isTrue
                (ModulePermission.implies ModulePermission.Read ModulePermission.SchemaOnly)
                "Read -> SchemaOnly"
        }

        testCaseAsync "ModulePermission.implies — SchemaOnly does NOT imply Read / Write / Admin"
        <| async {
            Expect.isFalse
                (ModulePermission.implies ModulePermission.SchemaOnly ModulePermission.Read)
                "SchemaOnly does not imply Read"

            Expect.isFalse
                (ModulePermission.implies ModulePermission.SchemaOnly ModulePermission.Write)
                "SchemaOnly does not imply Write"

            Expect.isFalse
                (ModulePermission.implies ModulePermission.SchemaOnly ModulePermission.Admin)
                "SchemaOnly does not imply Admin"
        }
    ]