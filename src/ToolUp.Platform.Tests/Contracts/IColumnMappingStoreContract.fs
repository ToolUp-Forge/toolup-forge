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

let private mk fingerprint typeId pairs : Conversion = {
    Fingerprint = fingerprint
    TargetTypeId = typeId
    Mapping = Map.ofList pairs
    Remediation = Map.empty
    SourceHeaders = pairs |> List.map snd
    CreatedBy = "tester"
    CreatedAt = DateTime.UtcNow
}

let private newStore () : IConversionStore =
    ColumnMappingStore.create (InMemoryObjectStore())

let tests =
    testList "IConversionStore contract" [
        testCaseAsync "Save then GetByFingerprint round-trips the mapping"
        <| async {
            let s = newStore ()
            let m = mk "region|sales" "SalesData" [ "Region", "Area"; "Sales", "Turnover" ]
            let! saved = s.Save("scope-a", m)
            Expect.isOk saved "save ok"
            let! got = s.GetByFingerprint("scope-a", "region|sales")
            Expect.equal got [ m ] "round-trips"
        }

        testCaseAsync "GetByFingerprint of an unknown fingerprint is empty"
        <| async {
            let s = newStore ()
            let! got = s.GetByFingerprint("scope-a", "nope")
            Expect.isEmpty got "absent"
        }

        testCaseAsync "one fingerprint holds several mappings — one per target type"
        <| async {
            let s = newStore ()
            do! s.Save("scope-a", mk "fp" "SalesData" [ "Amount", "Turnover" ]) |> Async.Ignore
            do! s.Save("scope-a", mk "fp" "RegionData" [ "Region", "Area" ]) |> Async.Ignore
            let! got = s.GetByFingerprint("scope-a", "fp")
            let types = got |> List.map _.TargetTypeId |> List.sort
            Expect.equal types [ "RegionData"; "SalesData" ] "both targets kept (additive)"
        }

        testCaseAsync "Save overwrites only the same (fingerprint, target type)"
        <| async {
            let s = newStore ()
            do! s.Save("scope-a", mk "fp" "T1" [ "A", "a" ]) |> Async.Ignore
            do! s.Save("scope-a", mk "fp" "T1" [ "A", "b" ]) |> Async.Ignore // re-save same key
            do! s.Save("scope-a", mk "fp" "T2" [ "A", "c" ]) |> Async.Ignore
            let! got = s.GetByFingerprint("scope-a", "fp")
            Expect.equal (List.length got) 2 "T1 overwritten in place, T2 added"

            let t1 = got |> List.find (fun m -> m.TargetTypeId = "T1")
            Expect.equal (t1.Mapping |> Map.find "A") "b" "T1 is the latest save"
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

        testCaseAsync "Delete removes one (fingerprint, type) and is idempotent"
        <| async {
            let s = newStore ()
            do! s.Save("scope-a", mk "fp" "T1" [ "A", "a" ]) |> Async.Ignore
            do! s.Save("scope-a", mk "fp" "T2" [ "A", "a" ]) |> Async.Ignore
            let! d1 = s.Delete("scope-a", "fp", "T1")
            Expect.isOk d1 "delete ok"
            let! got = s.GetByFingerprint("scope-a", "fp")
            Expect.equal (got |> List.map _.TargetTypeId) [ "T2" ] "only T1 gone"
            let! d2 = s.Delete("scope-a", "fp", "T1")
            Expect.isOk d2 "second delete idempotent"
        }

        testCaseAsync "mappings are isolated by scope"
        <| async {
            let s = newStore ()
            do! s.Save("scope-a", mk "fp" "T" [ "A", "a" ]) |> Async.Ignore
            let! crossGet = s.GetByFingerprint("scope-b", "fp")
            Expect.isEmpty crossGet "no cross-scope read"
            let! bList = s.List "scope-b"
            Expect.isEmpty bList "no cross-scope list"
        }

        testCaseAsync "provenance records save + list, separate from recipes"
        <| async {
            let s = newStore ()

            let record: ConversionRecord = {
                ProducedFile = "data__SalesData.csv"
                SourceFile = "data.csv"
                Fingerprint = "fp"
                TargetTypeId = "SalesData"
                Mapping = Map.ofList [ "Revenue", "Amount" ]
                RemediationSteps = [ "Amount: stripped $, removed thousands separators" ]
                ConvertedBy = "tester"
                ConvertedAt = DateTime.UtcNow
            }

            // a recipe in the same scope must not pollute the record list
            do! s.Save("scope-a", mk "fp" "SalesData" [ "Revenue", "Amount" ]) |> Async.Ignore
            let! saved = s.SaveRecord("scope-a", record)
            Expect.isOk saved "record saved"

            let! records = s.ListRecords "scope-a"
            Expect.equal records [ record ] "one record, recipes excluded"

            let! recipes = s.List "scope-a"
            Expect.equal (List.length recipes) 1 "recipe still listed separately"

            let! crossScope = s.ListRecords "scope-b"
            Expect.isEmpty crossScope "records scope-isolated"
        }
    ]