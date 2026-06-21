module ToolUp.Platform.Tests.InProcess.ArchitectureFitnessTests

open System.IO
open Expecto
open ToolUp.Platform.Tests.Contracts.ArchitectureFitness

// ─── Phase 174 — architecture-fitness dependency-direction gate ───────
//
// Freezes the layer boundaries the Phase 15d structural reorg
// established. The reorg cleaned the tree once; this keeps it clean by
// failing the build the moment a new `ProjectReference` or `open`
// re-introduces a forbidden edge — at CI time, not at a downstream Fable
// consumer's build.
//
// Two surfaces (see Contracts/ArchitectureFitness.fs for the detectors):
//   • reflection over the compiled `ToolUp.Platform.{Core,Server,Client}`
//     assembly graph — the tri-tier direction rule + the AG Grid split;
//   • source-tree string scans — infra opens under `Shared/`, the AG
//     Grid Enterprise shim in the Client tree, cross-module opens in the
//     samples set.
//
// Every "live tree" test has a paired fail-closed fixture proving the
// detector fires on a *planted* violation, so a green run means the gate
// actually checked something rather than finding nothing to look at.

let private coreAsm = "ToolUp.Platform.Core"
let private serverAsm = "ToolUp.Platform.Server"
let private clientAsm = "ToolUp.Platform.Client"
let private enterpriseAsm = "ToolUp.AgGridEnterprise"

// ─── Live: assembly-graph direction ───────────────────────────────────

let private directionTests =
    testList "assembly-graph direction" [

        test "Core references neither Server nor Client" {
            let refs = referencedSimpleNames (loadAssembly coreAsm)

            let edges = forbiddenEdges coreAsm refs (Set.ofList [ serverAsm; clientAsm ])

            Expect.isEmpty
                edges
                (sprintf
                    "ToolUp.Platform.Core must sit at the bottom of the tier graph — it may reference neither the Server nor the Client tier. Offending edge(s):\n%s"
                    (edges |> List.map formatEdge |> String.concat "\n"))
        }

        test "Server does not reference Client" {
            let refs = referencedSimpleNames (loadAssembly serverAsm)
            let edges = forbiddenEdges serverAsm refs (Set.ofList [ clientAsm ])

            Expect.isEmpty
                edges
                (sprintf
                    "ToolUp.Platform.Server must not reference the Client tier (a Server→Client edge leaks Fable client code into the server). Offending edge(s):\n%s"
                    (edges |> List.map formatEdge |> String.concat "\n"))
        }

        test "Client does not reference Server" {
            let refs = referencedSimpleNames (loadAssembly clientAsm)
            let edges = forbiddenEdges clientAsm refs (Set.ofList [ serverAsm ])

            Expect.isEmpty
                edges
                (sprintf
                    "ToolUp.Platform.Client must not reference the Server tier (a Client→Server edge breaks the Fable compile — server-only APIs leak into the client). Offending edge(s):\n%s"
                    (edges |> List.map formatEdge |> String.concat "\n"))
        }

        test "Client does not reference the AG Grid Enterprise companion (GP 2)" {
            let refs = referencedSimpleNames (loadAssembly clientAsm)
            let edges = forbiddenEdges clientAsm refs (Set.ofList [ enterpriseAsm ])

            Expect.isEmpty
                edges
                (sprintf
                    "ToolUp.Platform.Client is the default-composed Client tier — it must not reference the paid-tier AG Grid Enterprise companion (GP 2). The Enterprise init shim lives only in the opt-in AgGridEnterprise package. Offending edge(s):\n%s"
                    (edges |> List.map formatEdge |> String.concat "\n"))
        }
    ]

// ─── Live: source-tree scans ──────────────────────────────────────────

let private liveSourceTests =
    testList "source-tree boundaries" [

        test "no infra/framework opens under any Shared/ folder (GP 10)" {
            let findings =
                sharedTierFiles ()
                |> List.collect (fun path -> scanOpens classifyInfraOpen (relative path) (File.ReadAllText path))

            Expect.isEmpty
                findings
                (sprintf
                    "A cross-tier Shared/ file opened an infra/framework namespace — it must compile on the Fable client too. Offending open(s):\n%s"
                    (findings |> List.map formatSourceFinding |> String.concat "\n"))
        }

        test "no AG Grid Enterprise opens in the ToolUp.Platform.Client tree (GP 2)" {
            let findings =
                clientTierFiles ()
                |> List.collect (fun path -> scanOpens classifyEnterpriseOpen (relative path) (File.ReadAllText path))

            Expect.isEmpty
                findings
                (sprintf
                    "The default-composed Client tier reached for the AG Grid Enterprise shim. Offending open(s):\n%s"
                    (findings |> List.map formatSourceFinding |> String.concat "\n"))
        }

        test "sample modules are self-contained — no cross-module opens (GP 9)" {
            let findings = crossModuleOpenFindings (sampleModuleUnits ())

            Expect.isEmpty
                findings
                (sprintf
                    "A sample module imported another sample module's namespace — modules must be self-contained (GP 9). Offending open(s):\n%s"
                    (findings |> List.map formatSourceFinding |> String.concat "\n"))
        }
    ]

// ─── Fail-closed fixtures (the gate is not vacuously green) ────────────

let private failClosedTests =
    testList "fail-closed fixtures" [

        test "planted Server→Client reference is detected" {
            // Synthetic reference set: Server's IL gained a forbidden edge
            // to the Client tier. forbiddenEdges must surface exactly it.
            let plantedRefs = [ coreAsm; "FSharp.Core"; "Giraffe"; clientAsm ]

            let edges = forbiddenEdges serverAsm plantedRefs (Set.ofList [ clientAsm ])

            Expect.equal
                edges
                [ { From = serverAsm; To = clientAsm } ]
                "the planted Server→Client edge must be the sole finding"
        }

        test "planted Core→Server and Core→Client references are both detected" {
            let plantedRefs = [ "FSharp.Core"; serverAsm; clientAsm ]

            let edges = forbiddenEdges coreAsm plantedRefs (Set.ofList [ serverAsm; clientAsm ])

            let targets = edges |> List.map _.To |> List.sort
            Expect.equal targets [ clientAsm; serverAsm ] "both planted upward edges from Core must be flagged"
        }

        test "a clean reference set yields no edges" {
            // Defence against a vacuous detector: the real shape (Server →
            // Core + framework, no Client) must produce zero findings.
            let cleanRefs = [ coreAsm; "FSharp.Core"; "Giraffe"; "Microsoft.AspNetCore.Http" ]

            let edges = forbiddenEdges serverAsm cleanRefs (Set.ofList [ clientAsm ])

            Expect.isEmpty edges "a Server that references only Core + framework is clean"
        }

        test "planted `open Giraffe` under a Shared fixture is detected" {
            let fixture =
                "module Some.Shared.Contract\n\nopen System\nopen Giraffe\nopen Microsoft.AspNetCore.Http\n\ntype Dto = { Id: int }\n"

            let findings = scanOpens classifyInfraOpen "Some/Shared/Contract.fs" fixture

            let opened = findings |> List.map _.Detail
            Expect.hasLength findings 2 "both the Giraffe and the Microsoft.AspNetCore opens must flag"
            Expect.isTrue (opened |> List.exists (fun d -> d.Contains "Giraffe")) "Giraffe open flagged"

            Expect.isTrue
                (opened |> List.exists (fun d -> d.Contains "Microsoft.AspNetCore"))
                "Microsoft.AspNetCore open flagged"
        }

        test "a Shared fixture with only client-safe opens is not flagged" {
            let fixture =
                "module Some.Shared.Contract\n\nopen System\nopen FSharp.Core\n\ntype Dto = { Id: int }\n"

            let findings = scanOpens classifyInfraOpen "Some/Shared/Contract.fs" fixture
            Expect.isEmpty findings "client-safe opens under Shared/ must not flag"
        }

        test "planted `open AgGridEnterprise` in a Client fixture is detected" {
            let fixture =
                "module ToolUp.Platform.SomeView\n\nopen Feliz\nopen AgGridEnterprise\n\nlet view () = ()\n"

            let findings =
                scanOpens classifyEnterpriseOpen "src/ToolUp.Platform.Client/Client/SomeView.fs" fixture

            Expect.hasLength findings 1 "the planted Enterprise open must be the sole finding"
            Expect.stringContains findings.[0].Detail "AgGridEnterprise" "finding names the Enterprise module"
        }

        test "planted cross-module open across two sample units is detected" {
            // Two synthetic sample-module units; unit A reaches into
            // unit B's namespace — the GP 9 violation the live scan
            // guards against once a second sample module ships.
            let units = [
                {
                    UnitId = "samples/Alpha.Module"
                    Decls = Set.ofList [ "Alpha.Module.SharedTypes"; "Alpha.Module.ClientModel" ]
                    Files = [
                        "samples/Alpha.Module/ClientModel.fs",
                        "module Alpha.Module.ClientModel\n\nopen Alpha.Module.SharedTypes\nopen Beta.Module.SharedTypes\n"
                    ]
                }
                {
                    UnitId = "samples/Beta.Module"
                    Decls = Set.ofList [ "Beta.Module.SharedTypes" ]
                    Files = [ "samples/Beta.Module/SharedTypes.fs", "module Beta.Module.SharedTypes\n" ]
                }
            ]

            let findings = crossModuleOpenFindings units

            Expect.hasLength
                findings
                1
                "only the cross-unit open (Alpha→Beta) violates; the intra-unit SharedTypes open does not"

            Expect.stringContains findings.[0].Detail "Beta.Module" "finding names the sibling module that was imported"
        }

        test "intra-module opens within one sample unit are not flagged" {
            let units = [
                {
                    UnitId = "samples/Alpha.Module"
                    Decls = Set.ofList [ "Alpha.Module.SharedTypes"; "Alpha.Module.ClientModel" ]
                    Files = [
                        "samples/Alpha.Module/ClientView.fs",
                        "module Alpha.Module.ClientView\n\nopen Alpha.Module.SharedTypes\nopen Alpha.Module.ClientModel\nopen ToolUp.Platform\n"
                    ]
                }
            ]

            Expect.isEmpty
                (crossModuleOpenFindings units)
                "a module opening its own SharedTypes/ClientModel is self-contained, not a violation"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 174 — architecture-fitness gate" [ directionTests; liveSourceTests; failClosedTests ]