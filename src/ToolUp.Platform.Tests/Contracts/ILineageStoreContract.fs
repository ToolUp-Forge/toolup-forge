module ToolUp.Platform.Tests.Contracts.ILineageStoreContract

open System
open Expecto
open ToolUp.Platform

/// Contract test list for any `ILineageStore` implementation. The
/// factory returns `(store, scopeA, scopeB)` — scopes are
/// GUID-suffixed to keep cross-test isolation clean even when the
/// substrate (an `IEventStore`) is shared per factory call.
let tests (name: string) (factory: unit -> ILineageStore * string * string) =

    let okOrFail label result =
        match result with
        | Ok v -> v
        | Error err -> failtestf "%s: expected Ok, got %A" label err

    let recordOrFail (store: ILineageStore) (scopeId: string) (link: LineageLink) = async {
        let! r = store.Record(scopeId, link)
        return okOrFail "Record" r
    }

    let mkLink (fromId, toId, moduleName) = {
        LinkId = Guid.NewGuid()
        FromObjectId = fromId
        ToObjectId = toId
        ModuleName = moduleName
        LinkType = Derived
        Timestamp = DateTime.UtcNow
    }

    testList $"{name} — ILineageStore contract" [

        testCaseAsync "Record then GetAncestors returns the source"
        <| async {
            let store, scopeA, _ = factory ()
            let link = mkLink ("file-1", "result-1", "SalesAnalysis")
            do! recordOrFail store scopeA link

            let! graph = store.GetAncestors(scopeA, "result-1")
            Expect.equal graph.Root "result-1" "graph rooted at result"
            Expect.equal graph.Edges.Length 1 "single edge"
            Expect.equal graph.Edges[0].FromObjectId "file-1" "edge points back to source"

            let nodeIds = graph.Nodes |> List.map _.ObjectId |> Set.ofList
            Expect.equal nodeIds (Set.ofList [ "file-1"; "result-1" ]) "both nodes present"
        }

        testCaseAsync "GetDescendants traverses forward"
        <| async {
            let store, scopeA, _ = factory ()
            do! recordOrFail store scopeA (mkLink ("file-1", "result-1", "M"))
            do! recordOrFail store scopeA (mkLink ("result-1", "report-1", "M"))

            let! graph = store.GetDescendants(scopeA, "file-1")
            Expect.equal graph.Edges.Length 2 "two edges reachable forward"

            let nodeIds = graph.Nodes |> List.map _.ObjectId |> Set.ofList
            Expect.equal nodeIds (Set.ofList [ "file-1"; "result-1"; "report-1" ]) "all three nodes reachable"
        }

        testCaseAsync "GetAncestors of an object with no recorded lineage returns empty graph"
        <| async {
            let store, scopeA, _ = factory ()
            let! graph = store.GetAncestors(scopeA, "unknown-result")
            Expect.equal graph.Edges.Length 0 "no edges"
            Expect.equal graph.Nodes.Length 1 "only the root node"
            Expect.equal graph.Nodes[0].ObjectId "unknown-result" "root preserved even with no edges"
        }

        testCaseAsync "GetPath returns shortest path between linked objects"
        <| async {
            let store, scopeA, _ = factory ()
            do! recordOrFail store scopeA (mkLink ("a", "b", "M"))
            do! recordOrFail store scopeA (mkLink ("b", "c", "M"))

            let! pathOpt = store.GetPath(scopeA, "a", "c")

            match pathOpt with
            | Some path ->
                Expect.equal path.Length 2 "two-edge path"
                Expect.equal path[0].FromObjectId "a" "first hop"
                Expect.equal path[1].ToObjectId "c" "second hop"
            | None -> failtest "Expected Some path; got None"
        }

        testCaseAsync "GetPath returns None when no path exists"
        <| async {
            let store, scopeA, _ = factory ()
            do! recordOrFail store scopeA (mkLink ("a", "b", "M"))

            let! pathOpt = store.GetPath(scopeA, "a", "unrelated")
            Expect.isNone pathOpt "no path => None"
        }

        testCaseAsync "Cross-scope isolation: scopeB cannot see scopeA's lineage"
        <| async {
            let store, scopeA, scopeB = factory ()
            do! recordOrFail store scopeA (mkLink ("file-a", "result-a", "M"))

            let! graphInB = store.GetAncestors(scopeB, "result-a")
            Expect.equal graphInB.Edges.Length 0 "scopeB sees no edges"
        }

        testCaseAsync "Multiple inputs to one result: all ancestors discoverable"
        <| async {
            let store, scopeA, _ = factory ()
            do! recordOrFail store scopeA (mkLink ("file-1", "result-1", "M"))
            do! recordOrFail store scopeA (mkLink ("file-2", "result-1", "M"))

            let! graph = store.GetAncestors(scopeA, "result-1")
            Expect.equal graph.Edges.Length 2 "two upstream edges"

            let sourceIds = graph.Edges |> List.map _.FromObjectId |> Set.ofList

            Expect.equal sourceIds (Set.ofList [ "file-1"; "file-2" ]) "both sources discovered"
        }

        testCaseAsync "Producer ModuleName recorded against the derived node"
        <| async {
            let store, scopeA, _ = factory ()
            do! recordOrFail store scopeA (mkLink ("file-1", "result-1", "SalesAnalysis"))

            let! graph = store.GetAncestors(scopeA, "result-1")

            let resultNode = graph.Nodes |> List.find (fun n -> n.ObjectId = "result-1")
            Expect.equal resultNode.ModuleName (Some "SalesAnalysis") "producer recorded on derived node"

            let fileNode = graph.Nodes |> List.find (fun n -> n.ObjectId = "file-1")
            Expect.isNone fileNode.ModuleName "upstream-only node has None producer"
        }
    ]