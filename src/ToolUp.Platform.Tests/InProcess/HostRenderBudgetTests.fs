module ToolUp.Platform.Tests.InProcess.HostRenderBudgetTests

open System.IO
open Expecto
open ToolUp.Platform
open Toolup.Samples.ToyTreeBinding.ToyNode

// ─── Phase 278 — hosted-tree render-cost budget gate ───────────────────
//
// Four proofs:
//   1. evaluate — an over-node-count / over-depth / over-render-time tree
//      trips the budget with a readable report; an in-budget tree passes;
//      NOT configured = no measurement (WithinBudget).
//   2. measureTree — counts nodes + depth over a stranger tree language
//      (the Phase 202 ToyNode witness).
//   3. runtime — an over-budget render emits a Phase 268 render fault
//      through the sink (non-fatal); in-budget emits nothing; enforce is
//      the opt-in hard-fail.
//   4. OSS grep-guard.

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// A recording Phase 268 sink — captures every fault the budget reports.
let private recordingSink (captured: ResizeArray<HostRenderFault>) : IHostRenderTelemetrySink =
    { new IHostRenderTelemetrySink with
        member _.Capture fault = captured.Add fault
    }

// The toy's children function — measureTree is generic over it (GP 1).
let private toyChildren (node: ToyNode) : ToyNode list =
    match node with
    | Element(_, children) -> children
    | OnClick(_, child) -> [ child ]
    | Text _
    | Bind _ -> []

// ─── 1. evaluate ──────────────────────────────────────────────────────

let private evaluateTests =
    testList "Phase 278 — evaluate" [
        testCase "an over-node-count tree trips NodesExceeded"
        <| fun _ ->
            let budget = HostRenderBudget.ofShape 3 100

            let measure = {
                NodeCount = 10
                Depth = 2
                RenderMillis = None
            }

            match HostRenderBudget.evaluate budget measure with
            | OverBudget [ NodesExceeded(10, 3) ] -> ()
            | other -> failtestf "expected NodesExceeded(10,3), got %A" other

        testCase "an over-depth tree trips DepthExceeded"
        <| fun _ ->
            let budget = HostRenderBudget.ofShape 100 3

            let measure = {
                NodeCount = 5
                Depth = 9
                RenderMillis = None
            }

            match HostRenderBudget.evaluate budget measure with
            | OverBudget [ DepthExceeded(9, 3) ] -> ()
            | other -> failtestf "expected DepthExceeded(9,3), got %A" other

        testCase "an over-render-time tree trips RenderTimeExceeded"
        <| fun _ ->
            let budget = {
                HostRenderBudget.unlimited with
                    MaxRenderMillis = Some 16.0
            }

            let measure = {
                NodeCount = 1
                Depth = 1
                RenderMillis = Some 40.0
            }

            match HostRenderBudget.evaluate budget measure with
            | OverBudget [ RenderTimeExceeded(40.0, 16.0) ] -> ()
            | other -> failtestf "expected RenderTimeExceeded(40,16), got %A" other

        testCase "an in-budget tree passes"
        <| fun _ ->
            let budget = HostRenderBudget.ofShape 100 10

            let measure = {
                NodeCount = 12
                Depth = 4
                RenderMillis = None
            }

            Expect.equal (HostRenderBudget.evaluate budget measure) WithinBudget "under all limits"

        testCase "an unconfigured budget performs no measurement (GP 13)"
        <| fun _ ->
            let measure = {
                NodeCount = 1_000_000
                Depth = 9_999
                RenderMillis = Some 5000.0
            }

            Expect.equal
                (HostRenderBudget.evaluate HostRenderBudget.unlimited measure)
                WithinBudget
                "not configured ⇒ no measurement, always within budget"

        testCase "multiple breaches are all reported"
        <| fun _ ->
            let budget = HostRenderBudget.ofShape 3 2

            let measure = {
                NodeCount = 10
                Depth = 5
                RenderMillis = None
            }

            match HostRenderBudget.evaluate budget measure with
            | OverBudget breaches -> Expect.equal (List.length breaches) 2 "both node + depth breaches reported"
            | WithinBudget -> failtest "expected two breaches"
    ]

// ─── 2. measureTree over the ToyNode witness ──────────────────────────

let private measureTests =
    testList "Phase 278 — measureTree (ToyNode witness)" [
        testCase "counts nodes + depth of a stranger tree language"
        <| fun _ ->
            // section > [ p > [Text]; button > [Text] ]  →  5 nodes, depth 3.
            let tree =
                Element("section", [ Element("p", [ Text "hi" ]); Element("button", [ Text "go" ]) ])

            let nodes, depth = HostRenderBudget.measureTree toyChildren tree
            Expect.equal nodes 5 "section + p + text + button + text = 5 nodes"
            Expect.equal depth 3 "section(1) → p(2) → text(3)"

        testCase "measureOf builds a measure the budget evaluates"
        <| fun _ ->
            let tree =
                Element("ul", [ Element("li", [ Text "a" ]); Element("li", [ Text "b" ]) ])

            let measure = HostRenderBudget.measureOf toyChildren None tree
            // 5 nodes (ul + 2 li + 2 text), depth 3.
            match HostRenderBudget.evaluate (HostRenderBudget.ofShape 3 100) measure with
            | OverBudget [ NodesExceeded(5, 3) ] -> ()
            | other -> failtestf "expected a node-count breach, got %A" other
    ]

// ─── 3. Runtime reporting through the Phase 268 sink ──────────────────

let private reportingTests =
    testList "Phase 278 — runtime reporting (Phase 268 sink)" [
        testCase "an over-budget render emits a render fault (non-fatal)"
        <| fun _ ->
            let captured = ResizeArray<HostRenderFault>()
            let sink = recordingSink captured

            let result =
                HostRenderBudget.evaluate (HostRenderBudget.ofShape 2 100) {
                    NodeCount = 5
                    Depth = 2
                    RenderMillis = None
                }

            let wasOver = HostRenderBudget.reportBreaches sink "root-node" result

            Expect.isTrue wasOver "reportBreaches signals the over-budget state"
            Expect.equal captured.Count 1 "one breach captured through the sink"
            Expect.equal captured[0].NodeId "root-node" "the fault carries the node id"
            Expect.stringContains captured[0].Message "node count" "the fault names the exceeded dimension"

        testCase "an in-budget render emits nothing"
        <| fun _ ->
            let captured = ResizeArray<HostRenderFault>()
            let sink = recordingSink captured

            let result =
                HostRenderBudget.evaluate (HostRenderBudget.ofShape 100 100) {
                    NodeCount = 3
                    Depth = 2
                    RenderMillis = None
                }

            let wasOver = HostRenderBudget.reportBreaches sink "root" result
            Expect.isFalse wasOver "not over budget"
            Expect.equal captured.Count 0 "no fault emitted when within budget (GP 13)"

        testCase "enforce is the opt-in hard-fail"
        <| fun _ ->
            let captured = ResizeArray<HostRenderFault>()
            let sink = recordingSink captured

            let over =
                HostRenderBudget.evaluate (HostRenderBudget.ofShape 1 100) {
                    NodeCount = 5
                    Depth = 1
                    RenderMillis = None
                }

            Expect.throws (fun () -> HostRenderBudget.enforce sink "n" over) "enforce raises on an over-budget result"
            Expect.equal captured.Count 1 "enforce still reports through the sink before raising"

            // A within-budget result never raises.
            HostRenderBudget.enforce sink "n" WithinBudget
    ]

// ─── 4. OSS grep-guard ────────────────────────────────────────────────

let private ossTests =
    testList "Phase 278 — OSS boundary" [
        testCase "the budget source carries no banned OSS vocabulary"
        <| fun _ ->
            let path =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "HostRenderBudget.fs")

            Expect.isTrue (File.Exists path) (sprintf "expected the seam file at %s" path)
            NeutralityTokens.assertNoBannedTokens path (File.ReadAllText path)
            NeutralityTokens.skipUnlessExternalSource ()
    ]

let tests =
    testList "HostRenderBudget (Phase 278)" [ evaluateTests; measureTests; reportingTests; ossTests ]