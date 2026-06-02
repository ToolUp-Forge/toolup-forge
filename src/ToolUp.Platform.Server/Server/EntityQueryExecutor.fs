module ToolUp.Platform.EntityQueryExecutor

open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.SecondaryIndex
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes

// ─── Phase 19a — Entity query executor ──────────────────────────────
//
// Walks the `Predicate` tree, intersects/unions the index-lookup
// results, applies sort + paging. Each leaf is one
// `BlobIndex.Lookup` call.
//
// The executor takes a "lookup function" `(indexName, value) ->
// EntityId list` injected by the caller — keeps the executor pure
// (no I/O dependencies) and the unit tests trivial. The
// `BlobEntityStore` consumer wires the lookup function to its
// per-index `BlobIndex.Lookup` calls.
//
// `Ne` and `Not` predicates are handled by **complement against
// `ListAll`** — `Ne(field, value) = ListAll - Eq(field, value)`. This
// works for small scopes; on a hot path against a large scope the
// caller should prefer narrower predicates that can be answered from
// indexes directly.
//
// `Gt`/`Gte`/`Lt`/`Lte` are handled by enumerating the field's full
// index space (via `BlobIndex.AllKeys`) and filtering the keys by
// string ordering. Same caveat — large scopes are slow on
// range-style predicates. A future commit may add range-aware
// indexes; v1 trades query cost for implementation simplicity.

let private fableJsonOptions = FableConverters.create ()

let private deserialise<'T> (json: string) : 'T =
    JsonSerializer.Deserialize<'T>(json, fableJsonOptions)

/// I/O surface the executor needs. Injected by the caller; tests
/// supply in-memory implementations.
type ExecutorContext = {
    /// Look up entity ids by an exact-match value on a declared index.
    LookupByIndex: string -> string -> Async<EntityId list>
    /// Enumerate every key for an index. Used by range predicates
    /// (`Gt`/`Gte`/`Lt`/`Lte`) and complement predicates
    /// (`Ne`/`Not`). Returns the full key space; empty when the
    /// index has no entries.
    AllIndexKeys: string -> Async<string list>
    /// List every entity id in the scope (regardless of index
    /// values). Used for complement predicates that need to subtract
    /// matched ids from the universe.
    AllEntityIds: unit -> Async<EntityId list>
    /// Fetch a single entity by id and deserialise to 'T. Returns
    /// `None` for missing entities (drift).
    LoadEntity: EntityId -> Async<string option>
    /// Field-name-keyed extractor used for `OrderBy` — given the
    /// loaded entity JSON, return the value of the named field as a
    /// string. Returns `None` when the field isn't present.
    ReadFieldString: string -> string -> string option
}

/// Resolve a predicate to the set of matching entity ids. Used as
/// the executor's inner loop; `Query` wraps this with sort + paging
/// + entity load + deserialisation.
let rec private resolveIds (ctx: ExecutorContext) (predicate: Predicate) : Async<Set<EntityId>> = async {
    match predicate with
    | Eq(field, value) ->
        let! ids = ctx.LookupByIndex field value
        return Set.ofList ids

    | Ne(field, value) ->
        let! matchedIds = ctx.LookupByIndex field value
        let! allIds = ctx.AllEntityIds()
        return Set.difference (Set.ofList allIds) (Set.ofList matchedIds)

    | Gt(field, value) ->
        let! keys = ctx.AllIndexKeys field
        let matchingKeys = keys |> List.filter (fun k -> k > value)

        let! sets =
            matchingKeys
            |> List.map (fun k -> async {
                let! ids = ctx.LookupByIndex field k
                return Set.ofList ids
            })
            |> Async.Parallel

        return sets |> Array.fold Set.union Set.empty

    | Gte(field, value) ->
        let! keys = ctx.AllIndexKeys field
        let matchingKeys = keys |> List.filter (fun k -> k >= value)

        let! sets =
            matchingKeys
            |> List.map (fun k -> async {
                let! ids = ctx.LookupByIndex field k
                return Set.ofList ids
            })
            |> Async.Parallel

        return sets |> Array.fold Set.union Set.empty

    | Lt(field, value) ->
        let! keys = ctx.AllIndexKeys field
        let matchingKeys = keys |> List.filter (fun k -> k < value)

        let! sets =
            matchingKeys
            |> List.map (fun k -> async {
                let! ids = ctx.LookupByIndex field k
                return Set.ofList ids
            })
            |> Async.Parallel

        return sets |> Array.fold Set.union Set.empty

    | Lte(field, value) ->
        let! keys = ctx.AllIndexKeys field
        let matchingKeys = keys |> List.filter (fun k -> k <= value)

        let! sets =
            matchingKeys
            |> List.map (fun k -> async {
                let! ids = ctx.LookupByIndex field k
                return Set.ofList ids
            })
            |> Async.Parallel

        return sets |> Array.fold Set.union Set.empty

    | In(field, values) ->
        let! sets =
            values
            |> List.map (fun v -> async {
                let! ids = ctx.LookupByIndex field v
                return Set.ofList ids
            })
            |> Async.Parallel

        return sets |> Array.fold Set.union Set.empty

    | And(a, b) ->
        let! left = resolveIds ctx a
        let! right = resolveIds ctx b
        return Set.intersect left right

    | Or(a, b) ->
        let! left = resolveIds ctx a
        let! right = resolveIds ctx b
        return Set.union left right

    | Not inner ->
        let! matched = resolveIds ctx inner
        let! allIds = ctx.AllEntityIds()
        return Set.difference (Set.ofList allIds) matched
}

/// Execute a query: resolve predicate → load entities → sort → page.
/// Returns the materialised typed entity records.
let execute<'T> (ctx: ExecutorContext) (query: EntityQuery<'T>) : Async<'T list> = async {
    // 1. Resolve matching entity ids. None predicate = all ids.
    let! ids =
        match query.Where with
        | Some p -> resolveIds ctx p
        | None -> async {
            let! all = ctx.AllEntityIds()
            return Set.ofList all
          }

    // 2. Load each entity's JSON. Drift-resilient: missing entities
    //    drop silently.
    let! loaded =
        ids
        |> Set.toList
        |> List.map (fun id -> async {
            let! json = ctx.LoadEntity id

            return
                match json with
                | None -> None
                | Some j -> Some(id, j)
        })
        |> Async.Parallel

    let resolved = loaded |> Array.choose id |> Array.toList

    // 3. Apply sort. When OrderBy is set, use the field reader to
    //    pull the sort key from each entity's JSON. Stable secondary
    //    sort on EntityId for deterministic paging.
    let sorted =
        match query.OrderBy with
        | None -> resolved |> List.sortBy fst
        | Some(field, direction) ->
            let withKeys =
                resolved
                |> List.map (fun (id, json) -> (ctx.ReadFieldString field json |> Option.defaultValue ""), id, json)

            let primarySort =
                match direction with
                | Ascending -> List.sortBy (fun (k, id, _) -> k, id) withKeys
                | Descending -> List.sortByDescending (fun (k, id, _) -> k, id) withKeys

            primarySort |> List.map (fun (_, id, json) -> id, json)

    // 4. Apply paging.
    let paged =
        sorted |> List.skip (min query.Skip sorted.Length) |> List.truncate query.Take

    // 5. Deserialise.
    return paged |> List.map (fun (_, json) -> deserialise<'T> json)
}