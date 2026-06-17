module ToolUp.Platform.Tests.Contracts.IColumnMappingStoreContract

open System
open Expecto
open ToolUp.Platform
open ColumnMappingTypes

/// Minimal in-memory `IDataObjectStore` for store-wrapper tests.
/// `Save` / `Get` / `ListObjects` / `Delete` are faithful (keyed by
/// `(scopeId, objectId)`, so scope isolation is exercised); the rest
/// return stubs the column-mapping store never calls.
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

        member _.Delete(scopeId, objectId) = async {
            store.TryRemove((scopeId, objectId)) |> ignore
            return Ok()
        }

        member _.Evict(scopeId, objectId) = async {
            store.TryRemove((scopeId, objectId)) |> ignore
            return Ok()
        }

        member _.Purge(_) = async { return Ok() }

        member _.Erase(_, _, _, _) = async {
            return
                Result.Ok {
                    HandlerName = "data-objects"
                    RecordsAffected = 0
                    Note = None
                }
        }

let private mk fingerprint typeId pairs : ColumnMapping = {
    Fingerprint = fingerprint
    TargetTypeId = typeId
    FieldToColumn = Map.ofList pairs
    SourceHeaders = pairs |> List.map snd
    CreatedBy = "tester"
    CreatedAt = DateTime.UtcNow
}

let private newStore () : IColumnMappingStore =
    ColumnMappingStore.create (InMemoryObjectStore())

let tests =
    testList "IColumnMappingStore contract" [
        testCaseAsync "Save then Get round-trips the mapping"
        <| async {
            let s = newStore ()
            let m = mk "region|sales" "SalesData" [ "Region", "Area"; "Sales", "Turnover" ]
            let! saved = s.Save("scope-a", m)
            Expect.isOk saved "save ok"
            let! got = s.Get("scope-a", "region|sales")
            Expect.equal got (Some m) "round-trips"
        }

        testCaseAsync "Get of an unknown fingerprint returns None"
        <| async {
            let s = newStore ()
            let! got = s.Get("scope-a", "nope")
            Expect.isNone got "absent"
        }

        testCaseAsync "Save overwrites an existing fingerprint"
        <| async {
            let s = newStore ()
            do! s.Save("scope-a", mk "fp" "T1" [ "A", "a" ]) |> Async.Ignore
            do! s.Save("scope-a", mk "fp" "T2" [ "A", "b" ]) |> Async.Ignore
            let! got = s.Get("scope-a", "fp")
            Expect.equal (got |> Option.map _.TargetTypeId) (Some "T2") "latest wins"
        }

        testCaseAsync "List returns every mapping saved in the scope"
        <| async {
            let s = newStore ()
            do! s.Save("scope-a", mk "fp1" "T" [ "A", "a" ]) |> Async.Ignore
            do! s.Save("scope-a", mk "fp2" "T" [ "B", "b" ]) |> Async.Ignore
            let! all = s.List "scope-a"
            let fingerprints = all |> List.map _.Fingerprint |> List.sort
            Expect.equal fingerprints [ "fp1"; "fp2" ] "both listed"
        }

        testCaseAsync "Delete removes the mapping and is idempotent"
        <| async {
            let s = newStore ()
            do! s.Save("scope-a", mk "fp" "T" [ "A", "a" ]) |> Async.Ignore
            let! d1 = s.Delete("scope-a", "fp")
            Expect.isOk d1 "delete ok"
            let! got = s.Get("scope-a", "fp")
            Expect.isNone got "gone"
            let! d2 = s.Delete("scope-a", "fp")
            Expect.isOk d2 "second delete idempotent"
        }

        testCaseAsync "mappings are isolated by scope"
        <| async {
            let s = newStore ()
            do! s.Save("scope-a", mk "fp" "T" [ "A", "a" ]) |> Async.Ignore
            let! crossGet = s.Get("scope-b", "fp")
            Expect.isNone crossGet "no cross-scope read"
            let! bList = s.List "scope-b"
            Expect.isEmpty bList "no cross-scope list"
        }
    ]