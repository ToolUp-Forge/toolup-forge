module ToolUp.Scheduling.Tests.InProcess.InMemoryEntityStore

open System
open System.Collections.Concurrent
open Microsoft.FSharp.Reflection
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.EntityQueryTypes

// ─── Test-only in-memory IEntityStore stub ────────────────────────
//
// The contract pack for `IBookingScheduler` lives in this Tests
// project; the real `BlobEntityStore` lives in `ToolUp.Platform`'s
// Server props injection and would drag many transitive deps into
// the test fsproj. The stub gives the contract pack a working
// `IEntityStore` without that. The Phase 19 acceptance tests live
// in `ToolUp.Platform.Tests` and exercise the real impl directly.

let private setVersion<'T> (entity: 'T) (newVersion: int) : 'T =
    let t = typeof<'T>

    if not (FSharpType.IsRecord t) then
        entity
    else
        let fields = FSharpType.GetRecordFields(t)
        let values = FSharpValue.GetRecordFields(entity)

        let versionIdx = fields |> Array.tryFindIndex (fun f -> f.Name = "Version")

        match versionIdx with
        | Some i ->
            values[i] <- box newVersion
            FSharpValue.MakeRecord(t, values) :?> 'T
        | None -> entity

type InMemoryEntityStore() =
    // (scopeId, entityType) -> (entityId -> (boxed entity, version))
    let store =
        ConcurrentDictionary<string * string, ConcurrentDictionary<string, obj * int>>()

    let bucket scopeId entityType =
        store.GetOrAdd((scopeId, entityType), (fun _ -> ConcurrentDictionary()))

    interface IEntityStore with

        member _.Save<'T>(scopeId: string, entity: 'T) = async {
            match tryGetEntityFields entity with
            | Error msg -> return Error(InvalidEntityShape msg)
            | Ok core ->
                let b = bucket scopeId core.Type

                let newVersion =
                    match b.TryGetValue core.Id with
                    | true, (_, v) -> v + 1
                    | _ -> 1

                let stored = setVersion entity newVersion
                b[core.Id] <- (box stored, newVersion)

                return
                    Ok {
                        Id = core.Id
                        Type = core.Type
                        Version = newVersion
                    }
        }

        member _.Get<'T>(scopeId, entityType, entityId) = async {
            let b = bucket scopeId entityType

            match b.TryGetValue entityId with
            | true, (boxed, _) -> return Ok(boxed :?> 'T)
            | _ -> return Error(NotFound(entityType, entityId))
        }

        member _.GetVersion<'T>(scopeId, entityType, entityId, version) = async {
            let b = bucket scopeId entityType

            match b.TryGetValue entityId with
            | true, (boxed, v) when v = version -> return Ok(boxed :?> 'T)
            | _ -> return Error(NotFound(entityType, entityId))
        }

        member _.ListVersions<'T>(scopeId, entityType, entityId) = async {
            let b = bucket scopeId entityType

            match b.TryGetValue entityId with
            | true, (_, v) ->
                let r: EntityRef<'T> = {
                    Id = entityId
                    Type = entityType
                    Version = v
                }

                return [ r ]
            | _ -> return []
        }

        member _.Delete(scopeId, entityType, entityId) = async {
            let b = bucket scopeId entityType
            b.TryRemove(entityId) |> ignore
            return Ok()
        }

        member _.FindByIndex<'T>(scopeId, entityType, indexName, value) = async {
            // Best-effort: treat indexName + value as a property
            // filter on the entity record. Slow but acceptable for
            // tests — the contract pack doesn't exercise large
            // collections.
            let b = bucket scopeId entityType
            let acc = ResizeArray<EntityRef<'T>>()

            for kvp in b do
                let entity, version = kvp.Value

                match FSharpType.IsRecord(entity.GetType()) with
                | false -> ()
                | true ->
                    let fields = FSharpType.GetRecordFields(entity.GetType())
                    let values = FSharpValue.GetRecordFields(entity)

                    let idx = fields |> Array.tryFindIndex (fun f -> f.Name = indexName)

                    match idx with
                    | Some i ->
                        let v = values[i]
                        let asString = if v = null then "" else string v

                        if asString = value then
                            acc.Add {
                                Id = kvp.Key
                                Type = entityType
                                Version = version
                            }
                    | None -> ()

            return Ok(List.ofSeq acc)
        }

        member _.Count(scopeId, entityType) = async {
            let b = bucket scopeId entityType
            return b.Count
        }

        member _.ListAll<'T>(scopeId, entityType, skip, take) = async {
            let b = bucket scopeId entityType

            return
                b
                |> Seq.sortBy _.Key
                |> Seq.skip (min skip b.Count)
                |> Seq.truncate take
                |> Seq.map (fun kvp ->
                    let _, version = kvp.Value

                    let r: EntityRef<'T> = {
                        Id = kvp.Key
                        Type = entityType
                        Version = version
                    }

                    r)
                |> List.ofSeq
        }

        member _.Query<'T>(scopeId, query) = async {
            // Stub doesn't implement the query layer — contract pack
            // doesn't exercise it. Return empty.
            let empty: 'T list = []
            return Ok empty
        }