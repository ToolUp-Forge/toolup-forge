module ToolUp.Platform.LineageStore

open System
open System.Collections.Generic
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform

// ─── EventStoreLineageStore ──────────────────────────────────────
//
// Implementation of `ILineageStore` (Phase 8a) that stores nothing
// of its own. Every link is a `ModuleEvent` written to
// `IEventStore` with `EventType = LineageEventType.Link` and
// `SourceModule = LineageSourceModule.Value`. Queries fan out from
// `ReadByType` and walk the resulting edge list in memory.
//
// **Stateless between calls** (Phase 9c Rule 4): every method
// derives its result from `IEventStore` reads. No in-memory cache,
// no shared mutable state. An Orleans grain or Akka actor running
// this could deactivate / restart between any two operations and
// behave identically.
//
// **Within-shard ordering only** (Rule 5): query results are
// bounded by `scopeId` at the underlying read path. Cross-scope
// leakage is structurally impossible — `IEventStore.ReadByType`
// is per-scope.

// ─── JSON serialisation ──────────────────────────────────────────

let private settings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private serialize (link: LineageLink) : string =
    JsonConvert.SerializeObject(link, settings)

let private tryDeserialize (payload: string) : LineageLink option =
    try
        Some(JsonConvert.DeserializeObject<LineageLink>(payload, settings))
    with _ ->
        None

// ─── Graph construction ──────────────────────────────────────────

/// Build a lineage graph by walking the edge list outwards from
/// `root`. `directedFrom` selects the edge orientation:
///   - `true`  → walk descendants (follow `FromObjectId -> ToObjectId`)
///   - `false` → walk ancestors (follow `ToObjectId -> FromObjectId`)
let private walkGraph (root: string) (links: LineageLink list) (directedFrom: bool) : LineageGraph =
    // Build adjacency on the requested orientation. Each key is a
    // visited node id; each value is the list of links that lead
    // *out* of that node in the chosen direction.
    let adjacency =
        let dict = Dictionary<string, LineageLink list>()

        for link in links do
            let key = if directedFrom then link.FromObjectId else link.ToObjectId

            let existing =
                match dict.TryGetValue key with
                | true, xs -> xs
                | false, _ -> []

            dict[key] <- link :: existing

        dict

    // BFS from root collecting every reachable edge.
    let visitedNodes = HashSet<string>()
    let visitedLinks = HashSet<Guid>()
    let collectedEdges = ResizeArray<LineageLink>()
    let queue = Queue<string>()

    queue.Enqueue root
    visitedNodes.Add root |> ignore

    while queue.Count > 0 do
        let current = queue.Dequeue()

        match adjacency.TryGetValue current with
        | true, outgoing ->
            for link in outgoing do
                if visitedLinks.Add link.LinkId then
                    collectedEdges.Add link

                    let next = if directedFrom then link.ToObjectId else link.FromObjectId

                    if visitedNodes.Add next then
                        queue.Enqueue next
        | false, _ -> ()

    // Build node list. ModuleName is recorded against the node that
    // *produced* it — i.e., the link's `ToObjectId` carries the
    // producer for that node. Nodes that appear only as upstream
    // sources (never on any link's ToObjectId) get `ModuleName =
    // None` because the lineage history does not record their
    // producer.
    let producerByObjectId =
        collectedEdges
        |> Seq.map (fun l -> l.ToObjectId, l.ModuleName)
        |> Seq.distinctBy fst
        |> Map.ofSeq

    let nodes =
        visitedNodes
        |> Seq.map (fun id -> {
            ObjectId = id
            ModuleName = Map.tryFind id producerByObjectId
        })
        |> Seq.toList

    {
        Root = root
        Nodes = nodes
        Edges = collectedEdges |> Seq.toList
    }

// ─── BFS path search ─────────────────────────────────────────────

let private findPath (links: LineageLink list) (fromId: string) (toId: string) : LineageLink list option =
    if fromId = toId then
        Some []
    else
        // Outgoing adjacency from FromObjectId -> ToObjectId.
        let adjacency =
            let dict = Dictionary<string, LineageLink list>()

            for link in links do
                let existing =
                    match dict.TryGetValue link.FromObjectId with
                    | true, xs -> xs
                    | false, _ -> []

                dict[link.FromObjectId] <- link :: existing

            dict

        let queue = Queue<string * LineageLink list>()
        let visited = HashSet<string>()
        queue.Enqueue(fromId, [])
        visited.Add fromId |> ignore

        let mutable result: LineageLink list option = None

        while queue.Count > 0 && result.IsNone do
            let current, pathSoFar = queue.Dequeue()

            match adjacency.TryGetValue current with
            | true, outgoing ->
                for link in outgoing do
                    if result.IsNone && visited.Add link.ToObjectId then
                        let newPath = pathSoFar @ [ link ]

                        if link.ToObjectId = toId then
                            result <- Some newPath
                        else
                            queue.Enqueue(link.ToObjectId, newPath)
            | false, _ -> ()

        result

// ─── Store ───────────────────────────────────────────────────────

type EventStoreLineageStore(eventStore: IEventStore) =

    /// Pull every recorded `LineageLink` for a scope. Failed
    /// deserialisations are silently dropped — matches the
    /// `PersistentEventStore` policy of "partial read better than
    /// total failure" for advisory data.
    let readLinks (scopeId: string) : Async<LineageLink list> = async {
        let! events = eventStore.ReadByType(scopeId, LineageEventType.Link)
        return events |> List.choose (fun e -> tryDeserialize e.Payload)
    }

    interface ILineageStore with
        member _.Record(scopeId, link) = async {
            try
                do!
                    eventStore.Write {
                        Id = link.LinkId
                        OccurredAt = link.Timestamp
                        ScopeId = scopeId
                        SourceModule = LineageSourceModule.Value
                        EventType = LineageEventType.Link
                        Payload = serialize link
                    }

                return Ok()
            with ex ->
                return Error ex.Message
        }

        member _.GetAncestors(scopeId, objectId) = async {
            let! links = readLinks scopeId
            return walkGraph objectId links false
        }

        member _.GetDescendants(scopeId, objectId) = async {
            let! links = readLinks scopeId
            return walkGraph objectId links true
        }

        member _.GetPath(scopeId, fromId, toId) = async {
            let! links = readLinks scopeId
            return findPath links fromId toId
        }

        member _.Erase(scopeId, subjectUserId, policy, _dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "lineage"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op"
                    }
            else
                match policy with
                | ErasurePolicy.RetainPerCompliance ->
                    return
                        Result.Error(
                            HandlerRefused(
                                "lineage",
                                "lineage is part of the provenance/audit fabric — retained under RetainPerCompliance"
                            )
                        )
                | _ ->
                    // Lineage has no own persistence; the link events
                    // are byte-erased by the event-store handler under
                    // the same policy. This handler is scope-isolated
                    // and lineage-typed: it reports impact, it does not
                    // double-mutate the shared event store.
                    let! events = eventStore.ReadByType(scopeId, LineageEventType.Link)

                    let matched = events |> List.filter (fun e -> e.Payload.Contains subjectUserId)

                    let verb =
                        match policy with
                        | ErasurePolicy.HardDelete -> "removed by the event-store handler"
                        | _ -> "tombstoned by the event-store handler"

                    return
                        Result.Ok {
                            HandlerName = "lineage"
                            RecordsAffected = matched.Length
                            Note =
                                Some(
                                    sprintf
                                        "%d lineage link(s) name the subject in scope %s; link events are %s (lineage has no independent persistence)"
                                        matched.Length
                                        scopeId
                                        verb
                                )
                        }
        }