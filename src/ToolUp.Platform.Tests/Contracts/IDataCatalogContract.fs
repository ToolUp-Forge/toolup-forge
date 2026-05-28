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
                    |> Seq.map (fun kvp -> kvp.Key)
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
            let ids = types |> List.map (fun t -> t.Id) |> Set.ofList
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
    ]