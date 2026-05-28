module ToolUp.Forms.Tests.InProcess.InMemoryEntityStore

open System.Collections.Concurrent
open Microsoft.FSharp.Reflection
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Forms.FormSubmission

// ─── Test-only in-memory IEntityStore stub ────────────────────────
//
// Modelled on src/ToolUp.Scheduling.Tests/InProcess/InMemoryEntityStore.fs;
// extended with a working `Query<'T>` implementation supporting
// `Eq` / `Ne` / `And` / `Or` / `Not` predicates against record
// fields (matched by name). Phase 19a's full predicate suite (`Gt`
// / `Lt` / `Gte` / `Lte` / `In`) is implemented as well — the
// IFormStoreContract pack and IWorkflowEngineContract pack both
// rely on `Query` working end-to-end.
//
// Reflection-driven; not optimised for large entity collections,
// but adequate for the per-test populations the contract packs use.

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

let private extractField (entity: obj) (fieldName: string) : string option =
    let t = entity.GetType()

    if not (FSharpType.IsRecord t) then
        None
    else
        let fields = FSharpType.GetRecordFields(t)
        let values = FSharpValue.GetRecordFields(entity)
        let idx = fields |> Array.tryFindIndex (fun f -> f.Name = fieldName)

        match idx with
        | Some i ->
            let v = values[i]

            if isNull v then
                Some ""
            else
                // Phase 21b — `Submission.Author` is a typed DU; the
                // production `BlobEntityStore` reads its value via the
                // registered `withIndex` extractor (which calls
                // `SubmissionAuthor.toIndexValue`). The default `string`
                // representation of a DU doesn't match, so this stub
                // routes the conversion through the same helper for
                // queries to behave the same way.
                match v with
                | :? SubmissionAuthor as a -> Some(SubmissionAuthor.toIndexValue a)
                | _ -> Some(string v)
        | None -> None

let rec private evaluatePredicate (entity: obj) (predicate: Predicate) : bool =
    match predicate with
    | Eq(name, value) ->
        match extractField entity name with
        | Some v -> v = value
        | None -> false
    | Ne(name, value) ->
        match extractField entity name with
        | Some v -> v <> value
        | None -> true
    | Gt(name, value) ->
        match extractField entity name with
        | Some v -> v > value
        | None -> false
    | Gte(name, value) ->
        match extractField entity name with
        | Some v -> v >= value
        | None -> false
    | Lt(name, value) ->
        match extractField entity name with
        | Some v -> v < value
        | None -> false
    | Lte(name, value) ->
        match extractField entity name with
        | Some v -> v <= value
        | None -> false
    | In(name, values) ->
        match extractField entity name with
        | Some v -> List.contains v values
        | None -> false
    | And(a, b) -> evaluatePredicate entity a && evaluatePredicate entity b
    | Or(a, b) -> evaluatePredicate entity a || evaluatePredicate entity b
    | Not p -> not (evaluatePredicate entity p)

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
            | _ -> return Error(EntityError.NotFound(entityType, entityId))
        }

        member _.GetVersion<'T>(scopeId, entityType, entityId, version) = async {
            let b = bucket scopeId entityType

            match b.TryGetValue entityId with
            | true, (boxed, v) when v = version -> return Ok(boxed :?> 'T)
            | _ -> return Error(EntityError.NotFound(entityType, entityId))
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

        member _.Delete(_, entityType, entityId) = async {
            let b = store.Values |> Seq.tryFind (fun bk -> bk.ContainsKey entityId)

            match b with
            | Some bk -> bk.TryRemove(entityId) |> ignore
            | None -> ()

            return Ok()
        }

        member _.FindByIndex<'T>(scopeId, entityType, indexName, value) = async {
            let b = bucket scopeId entityType
            let acc = ResizeArray<EntityRef<'T>>()

            for kvp in b do
                let entity, version = kvp.Value

                match extractField entity indexName with
                | Some v when v = value ->
                    acc.Add {
                        Id = kvp.Key
                        Type = entityType
                        Version = version
                    }
                | _ -> ()

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
            let b = bucket scopeId query.EntityType

            let acc = ResizeArray<'T>()

            let predicateMatches entity =
                match query.Where with
                | None -> true
                | Some p -> evaluatePredicate entity p

            for kvp in b do
                let entity, _ = kvp.Value

                if predicateMatches entity then
                    acc.Add(entity :?> 'T)

            let sorted =
                match query.OrderBy with
                | Some(fieldName, dir) ->
                    let key (e: 'T) =
                        extractField (box e) fieldName |> Option.defaultValue ""

                    let asc = acc |> Seq.sortBy key |> List.ofSeq

                    match dir with
                    | Ascending -> asc
                    | Descending -> List.rev asc
                | None ->
                    acc
                    |> Seq.sortBy (fun e -> extractField (box e) "Id" |> Option.defaultValue "")
                    |> List.ofSeq

            return Ok(sorted |> List.skip (min query.Skip sorted.Length) |> List.truncate query.Take)
        }