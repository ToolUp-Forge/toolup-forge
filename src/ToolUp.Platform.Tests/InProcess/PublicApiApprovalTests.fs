module ToolUp.Platform.Tests.InProcess.PublicApiApprovalTests

// ─── Phase 175 — Public-API approval / baseline (SemVer guard) ───────
//
// One Expecto case per packable `ToolUp.*` assembly: render its live
// public surface (MetadataLoadContext, metadata-only) and diff against
// `toolup-forge/api-baselines/<assembly>.approved.txt`. A removed /
// renamed / retyped public member fails the case and names every lost
// token; additive surface growth passes silently. Plus two synthetic
// fixtures proving the comparer fails-closed on a removal and does NOT
// false-positive on an addition.
//
// Mechanism + additive-vs-breaking policy + the TOOLUP_APPROVE_API
// regeneration path are documented in Contracts/PublicApiApproval.fs.

open System.IO
open Expecto
open ToolUp.Platform.Tests.Contracts.PublicApiApproval

let private root = repoRoot ()
let private config = activeConfig ()

// Shared MLC dependency pool — computed once, reused across every
// per-assembly render (each render still spins its own MetadataLoadContext).
let private pool = lazy (resolverPool root config)

let private packable = lazy (discoverPackable root config)

let private assemblyCase (a: PackableAssembly) = test a.Name {
    match a.DllPath with
    | None ->
        failtestf
            "%s: no DLL in bin/%s/net10.0 — run `dotnet build ToolUp.Forge.sln` before the Public-API gate (the canonical VerifyAll gate builds the solution first, so every Debug DLL is present)."
            a.Name
            config
    | Some dll ->
        let rendered = renderSurface dll pool.Value
        let baselinePath = Path.Combine(baselineDir root, a.Name + ".approved.txt")

        if approveModeOn () then
            Directory.CreateDirectory(baselineDir root) |> ignore
            File.WriteAllText(baselinePath, rendered)
        elif not (File.Exists baselinePath) then
            failtestf
                "%s: no committed baseline at api-baselines/%s.approved.txt. This is a NEW public package — generate its baseline with `TOOLUP_APPROVE_API=1` and commit it in the same PR."
                a.Name
                a.Name
        else
            let baseline = File.ReadAllText baselinePath
            let removed = removedMembers baseline rendered

            if not (List.isEmpty removed) then
                let listing = removed |> List.map (sprintf "  - %s") |> String.concat "\n"

                failtestf
                    "%s: %d public member(s) removed/renamed/retyped vs the committed baseline — a BREAKING change under the SemVer-on-0.x policy (GP 11):\n%s\n\nIf this break is intentional, regenerate the baseline (TOOLUP_APPROVE_API=1) and commit the api-baselines/%s.approved.txt edit in the same PR so the removal is reviewed."
                    a.Name
                    removed.Length
                    listing
                    a.Name
}

let private assemblyCases =
    testList "per-assembly surface" [
        // A sanity floor: discovery must find the packable set. Zero
        // would mean the glob silently matched nothing (a broken gate
        // reading green).
        test "discovery finds packable assemblies" {
            Expect.isGreaterThan
                packable.Value.Length
                0
                "discoverPackable found no packable assemblies under src/ — the Pack-set glob is broken."
        }

        yield! packable.Value |> List.map assemblyCase
    ]

// ── Synthetic fixtures: the comparer is the load-bearing logic; pin
//    both directions (fails-closed on removal, no false-positive on add). ──
let private comparerFixtures =
    testList "comparer" [
        test "removed member is detected (fails-closed)" {
            let baseline =
                "Demo.T (class)\nDemo.T.Alpha() : System.Int32\nDemo.T.Beta() : System.String\n"

            let current = "Demo.T (class)\nDemo.T.Alpha() : System.Int32\n"

            let removed = removedMembers baseline current
            Expect.contains removed "Demo.T.Beta() : System.String" "the removed member must be reported"
        }

        test "added member does not false-positive" {
            let baseline = "Demo.T (class)\nDemo.T.Alpha() : System.Int32\n"

            let current =
                "Demo.T (class)\nDemo.T.Alpha() : System.Int32\nDemo.T.Gamma() : System.Boolean\n"

            Expect.isEmpty (removedMembers baseline current) "a purely additive surface must not be flagged"
        }

        test "retyped member reads as a removal" {
            let baseline = "Demo.T.Bar() : System.Int32\n"
            let current = "Demo.T.Bar() : System.Int64\n"

            let removed = removedMembers baseline current
            Expect.contains removed "Demo.T.Bar() : System.Int32" "a changed return type must surface as a lost token"
        }

        test "comment + blank lines are ignored" {
            let baseline = "# header comment\n\nDemo.T.Alpha() : System.Int32\n"
            let current = "# different header\nDemo.T.Alpha() : System.Int32\n\n"

            Expect.isEmpty (removedMembers baseline current) "header/blank noise must not register as a diff"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 175 — Public-API approval baseline" [ comparerFixtures; assemblyCases ]