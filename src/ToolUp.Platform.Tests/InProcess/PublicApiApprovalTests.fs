module ToolUp.Platform.Tests.InProcess.PublicApiApprovalTests

// ─── Phase 175 — Public-API approval / baseline (SemVer guard) ───────
// ─── Phase 618 — the guard fails in BOTH directions ──────────────────
//
// One Expecto case per packable `ToolUp.*` assembly: render its live
// public surface (MetadataLoadContext, metadata-only) and diff against
// `toolup-forge/api-baselines/<assembly>.approved.txt`. A removed /
// renamed / retyped public member fails the case as BREAKING and names
// every lost token; an ADDED member fails it too — with a different
// message saying the growth is fine and only needs folding into the
// baseline (Phase 618; Phase 175 passed additions silently, and the
// drift that bought is the reason this arm exists).
//
// Mechanism, drift policy, the TOOLUP_APPROVE_API regeneration path, and
// the decided Phase 258 (`[<Obsolete>]`) interaction are documented in
// Contracts/PublicApiApproval.fs.

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

        if approveModeFor a.Name then
            Directory.CreateDirectory(baselineDir root) |> ignore
            File.WriteAllText(baselinePath, rendered)
        elif approveModeOn () then
            // A scoped regeneration run that does not cover this
            // assembly. Comparing here would fail the run for drift the
            // operator has deliberately excluded, so the case is inert —
            // the point of the scope is that untargeted baselines are
            // neither rewritten nor consulted.
            ()
        elif not (File.Exists baselinePath) then
            failtestf
                "%s: no committed baseline at api-baselines/%s.approved.txt. This is a NEW public package — generate its baseline with `TOOLUP_APPROVE_API=1` and commit it in the same PR."
                a.Name
                a.Name
        else
            let baseline = File.ReadAllText baselinePath

            match describeDrift a.Name (compareSurface baseline rendered) with
            | None -> ()
            | Some report -> failtest report
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

        // A scoped regeneration that matches nothing rewrites nothing and
        // reports success — the same vacuous-green shape a filter that
        // selects zero tests produces. Loud, in the only place that can
        // see both the scope and the discovered set.
        test "a scoped regeneration names assemblies that exist" {
            match approveScope () with
            | None -> () // unscoped, or not regenerating at all
            | Some names ->
                let discovered = packable.Value |> List.map _.Name |> Set.ofList

                let unknown =
                    names
                    |> Set.filter (fun n ->
                        not (
                            discovered
                            |> Set.exists (fun d -> d.Equals(n, System.StringComparison.OrdinalIgnoreCase))
                        ))

                Expect.isEmpty
                    (Set.toList unknown)
                    "TOOLUP_APPROVE_API names assemblies that are not in the discovered packable set — nothing would have been regenerated for them. Check the spelling, or build the solution first."
        }

        yield! packable.Value |> List.map assemblyCase
    ]

// ── Synthetic fixtures: the comparer is the load-bearing logic; pin
//    both directions (fails-closed on removal, fails-open-but-loud on an
//    unfolded addition) and the wording that tells them apart. ──
let private comparerFixtures =
    testList "comparer" [
        test "removed member is detected (fails-closed)" {
            let baseline =
                "Demo.T (class)\nDemo.T.Alpha() : System.Int32\nDemo.T.Beta() : System.String\n"

            let current = "Demo.T (class)\nDemo.T.Alpha() : System.Int32\n"

            let removed = removedMembers baseline current
            Expect.contains removed "Demo.T.Beta() : System.String" "the removed member must be reported"
        }

        test "added member is detected as an addition, not a removal" {
            let baseline = "Demo.T (class)\nDemo.T.Alpha() : System.Int32\n"

            let current =
                "Demo.T (class)\nDemo.T.Alpha() : System.Int32\nDemo.T.Gamma() : System.Boolean\n"

            let drift = compareSurface baseline current
            Expect.isEmpty drift.Removed "a purely additive surface must not register as breaking"
            Expect.contains drift.Added "Demo.T.Gamma() : System.Boolean" "the added member must be reported"
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

            Expect.isTrue
                (SurfaceDrift.isClean (compareSurface baseline current))
                "header/blank noise must not register as a diff in either direction"
        }
    ]

// ── Phase 618: the two failure messages must be DISTINGUISHABLE. A gate
//    whose new arm reads like its old one teaches the reader the wrong
//    thing — an addition is not a break, and the text has to say so. ──
let private messageFixtures =
    testList "drift report" [
        test "a matching surface reports nothing" {
            let text = "Demo.T (class)\nDemo.T.Alpha() : System.Int32\n"

            Expect.isNone (describeDrift "Demo" (compareSurface text text)) "a clean surface must not fail the gate"
        }

        test "a removal reports the breaking-change wording" {
            let drift = compareSurface "Demo.T.Beta() : System.String\n" ""

            let report =
                Expect.wantSome (describeDrift "Demo" drift) "a removal must fail the gate"

            Expect.stringContains report "BREAKING" "a removal must be named as breaking"
            Expect.stringContains report "Demo.T.Beta() : System.String" "the lost token must be named"
            Expect.isFalse (report.Contains "NOTHING IS BROKEN") "the removal message must not reassure"
        }

        test "an addition reports the fold-the-baseline wording, not the breaking one" {
            let drift = compareSurface "" "Demo.T.Gamma() : System.Boolean\n"

            let report =
                Expect.wantSome (describeDrift "Demo" drift) "an unfolded addition must fail the gate"

            Expect.stringContains report "ADDED" "an addition must be named as an addition"
            Expect.stringContains report "NOTHING IS BROKEN" "the addition message must say the change is non-breaking"
            Expect.stringContains report "TOOLUP_APPROVE_API" "the addition message must name the regeneration flag"

            Expect.stringContains
                report
                "api-baselines/Demo.approved.txt"
                "the addition message must name the file to commit"

            Expect.isFalse (report.Contains "BREAKING") "an addition must NOT be reported as a breaking change"
        }

        test "when both directions moved, the breaking one leads" {
            let drift =
                compareSurface "Demo.T.Beta() : System.String\n" "Demo.T.Gamma() : System.Boolean\n"

            let report =
                Expect.wantSome (describeDrift "Demo" drift) "a two-directional drift must fail the gate"

            Expect.stringContains report "BREAKING" "the breaking arm must lead when both moved"
            Expect.stringContains report "Demo.T.Beta() : System.String" "the lost token must be named"
            Expect.stringContains report "Demo.T.Gamma() : System.Boolean" "the added token must still be named"
        }
    ]

// ── Phase 258 seam (DECIDED here, in advance — see the header note in
//    Contracts/PublicApiApproval.fs). Phase 258 will render `[<Obsolete>]`
//    markings; these two fixtures pin which rendering it may use, so the
//    decision survives as an executable contract rather than prose. ──
let private obsoleteSeamFixtures =
    testList "Phase 258 seam" [
        test "sanctioned obsolete marker scores additive, never breaking" {
            let token = "Demo.T.Alpha() : System.Int32"
            let baseline = token + "\n"
            let current = sprintf "%s\n%s\n" token (obsoleteMarker token)

            let drift = compareSurface baseline current

            Expect.isEmpty
                drift.Removed
                "marking a member [<Obsolete>] is additive (minor) — it must never read as a removal"

            Expect.equal drift.Added [ obsoleteMarker token ] "the marker itself is the one added token"
        }

        test "rewriting the member token in place would read as breaking (why it is forbidden)" {
            let baseline = "Demo.T.Alpha() : System.Int32\n"
            let current = "Demo.T.Alpha() : System.Int32 [obsolete]\n"

            let drift = compareSurface baseline current

            Expect.contains
                drift.Removed
                "Demo.T.Alpha() : System.Int32"
                "an in-place token rewrite is indistinguishable from a removal — this is why Phase 258 must emit a separate marker line"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 175 — Public-API approval baseline" [
        comparerFixtures
        messageFixtures
        obsoleteSeamFixtures
        assemblyCases
    ]