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
open ToolUp.Platform.Tests.Contracts.SurfaceDiff
open ToolUp.Platform.Tests.Contracts.PublicApiApproval

let private root = repoRoot ()
let private config = activeConfig ()

// Shared MLC dependency pool — computed once, reused across every
// per-assembly render (each render still spins its own MetadataLoadContext).
let private pool = lazy (resolverPool root config)

let private packable = lazy (discoverPackable root)

// Phase 731: an unbuilt tree is ONE fact, so it is reported once — by
// `buildPrecondition` below. The DLL is resolved HERE, at case execution,
// rather than snapshotted during discovery at process start, so a build
// landing mid-run is seen. A case whose assembly is not built defers to
// that single finding instead of restating it; the precondition case
// itself fails, so an unbuilt tree still leaves the pack red.
let private assemblyCase (a: PackableAssembly) = test a.Name {
    match resolveDll config a with
    | None ->
        skiptestf
            "%s: not built in bin/%s/net10.0 — deferring to the 'the solution is built' precondition, which names the whole set once."
            a.Name
            config
    | Some dll ->
        let render = renderSurfaceDetail dll pool.Value
        let rendered = render.Text
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

            // Phase 258 — the deprecation-message policy, on the SAME
            // render (one MetadataLoadContext load, not two).
            //
            // Deliberately AFTER the drift comparison and only on a
            // comparison run:
            //   * drift leads because regenerating the baseline is the
            //     remedy for it, and a run that reported a message defect
            //     first would be asking for a source fix while the caller
            //     is mid-regen;
            //   * a regeneration run never reaches here, because a policy
            //     check that can block `TOOLUP_APPROVE_API` turns a
            //     maintenance operation into a gate.
            // An assembly with both a drift and a bad message therefore
            // reports the drift now and the message on the next run. Both
            // still fail; only the order is fixed.
            match describeObsoleteDefects a.Name (obsoleteDefects render.Obsolete) with
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

        // Phase 731 — THE precondition, failed once. Every per-assembly
        // case above defers to this one when its DLL is absent, so an
        // unbuilt tree produces a single finding naming the set rather
        // than 52 assertion failures that read like a surface break.
        //
        // Scoped to the regeneration scope when one is set: under
        // `TOOLUP_APPROVE_API=ToolUp.Platform.Core` the untargeted
        // baselines are neither rewritten nor consulted, so requiring
        // them built would fail a run for something the operator
        // deliberately excluded. Unscoped — which includes every ordinary
        // comparison run — it covers the whole discovered set.
        test "the solution is built" {
            let inScope =
                match approveScope () with
                | None -> packable.Value
                | Some names ->
                    packable.Value
                    |> List.filter (fun a ->
                        names
                        |> Set.exists (fun n -> n.Equals(a.Name, System.StringComparison.OrdinalIgnoreCase)))

            match describeUnbuilt config inScope.Length (unbuiltAssemblies config inScope) with
            | None -> ()
            | Some report -> failtest report
        }

        yield! packable.Value |> List.map assemblyCase
    ]

// ── Phase 731: the precondition report is the load-bearing logic, and a
//    guard whose only evidence is that it passed on a built tree has not
//    been shown able to fire. Pin both directions and the wording that
//    keeps it distinguishable from a surface break. ──
let private preconditionFixtures =
    let fake name = {
        Name = name
        ProjectPath = sprintf "src/%s/%s.fsproj" name name
        ProjectDir = sprintf "src/%s" name
    }

    testList "build precondition" [
        test "a fully built set reports nothing" {
            Expect.isNone (describeUnbuilt "Debug" 3 []) "every assembly built must not fail the gate"
        }

        test "an unbuilt assembly is named, with the remedy and the not-a-break wording" {
            let report =
                Expect.wantSome
                    (describeUnbuilt "Debug" 3 [ fake "ToolUp.RAG.StaticCorpus.Core" ])
                    "an unbuilt assembly must fail the gate"

            Expect.stringContains report "ToolUp.RAG.StaticCorpus.Core" "the unbuilt assembly must be named"
            Expect.stringContains report "dotnet build ToolUp.Forge.sln" "the remedy must be named"
            Expect.stringContains report "PRECONDITION" "a missing build must be named as a precondition"

            Expect.isFalse
                (report.Contains "BREAKING")
                "an unbuilt tree is not a public-surface break and must never be reported as one"
        }

        test "a large missing set is bounded and says how much it elided" {
            let missing = [ for i in 1..52 -> fake (sprintf "ToolUp.Companion%02d" i) ]

            let report =
                Expect.wantSome (describeUnbuilt "Debug" 52 missing) "an unbuilt tree must fail the gate"

            Expect.stringContains report "52 of 52" "the count must be named"
            Expect.stringContains report "ToolUp.Companion01" "the sample must start at the first missing assembly"
            Expect.stringContains report "and 47 more" "the elided remainder must be counted, not silently dropped"

            Expect.isFalse
                (report.Contains "ToolUp.Companion50")
                "the sample must be bounded — printing 52 names is the wall of text this replaces"
        }

        test "resolveDll reads the filesystem at call time, not at discovery" {
            // The whole point of Phase 731.A: an assembly whose bin/ does
            // not exist resolves to None NOW, and would resolve to Some
            // the moment a build put a DLL there — no snapshot involved.
            let absent = {
                Name = "ToolUp.NotOnDisk"
                ProjectPath = "src/ToolUp.NotOnDisk/ToolUp.NotOnDisk.fsproj"
                ProjectDir = Path.Combine(Path.GetTempPath(), "toolup-731-no-such-dir")
            }

            Expect.isNone (resolveDll "Debug" absent) "an unbuilt project must resolve to None"

            Expect.equal
                (unbuiltAssemblies "Debug" [ absent ] |> List.map _.Name)
                [ "ToolUp.NotOnDisk" ]
                "the unbuilt filter must report exactly the assemblies with no DLL"
        }
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

// ── Phase 258 seam (DECIDED by Phase 618 in advance, SHIPPED by 258 —
//    see the header note in Contracts/PublicApiApproval.fs). These two
//    fixtures pin which rendering the marker may use, so the decision
//    survives as an executable contract rather than prose. ──
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

// ── Phase 258 — the deprecation-MESSAGE policy. The comparer fixtures
//    above pin what a deprecation does to the BASELINE; these pin what
//    the notice itself has to SAY. Pure over `ObsoleteMember`, so the
//    policy is falsifiable without a build. ──
let private deprecationPolicyFixtures =
    let marking token message = { Token = token; Message = message }

    testList "Phase 258 deprecation message policy" [
        test "a conforming notice passes" {
            Expect.isNone
                (obsoleteDefect (marking "Demo.T.Alpha() : System.Int32" "Use Demo.T.Beta instead. Removed in 1.0."))
                "a notice naming a replacement and a removal target must pass"
        }

        test "a bare [<Obsolete>] fails, and the report says the message is empty" {
            let defect =
                Expect.wantSome
                    (obsoleteDefect (marking "Demo.T.Alpha() : System.Int32" ""))
                    "a bare [<Obsolete>] must fail the policy"

            Expect.stringContains defect "no message" "the defect must name the missing message"

            let report =
                Expect.wantSome
                    (describeObsoleteDefects "Demo" (obsoleteDefects [ marking "Demo.T.Alpha() : System.Int32" "" ]))
                    "the assembly report must fail"

            Expect.stringContains report "Demo.T.Alpha() : System.Int32" "the offending member must be named"
            Expect.stringContains report "(empty)" "an empty message must be shown as empty, not as blank space"

            Expect.stringContains
                report
                "docs/platform/deprecation-policy.md"
                "the report must point at the policy it is enforcing"
        }

        test "whitespace is not a message" {
            Expect.isSome
                (obsoleteDefect (marking "Demo.T.Alpha() : System.Int32" "   "))
                "a whitespace-only message must fail exactly as an empty one does"
        }

        test "a replacement with no removal target fails" {
            let defect =
                Expect.wantSome
                    (obsoleteDefect (
                        marking "Demo.T.Alpha() : System.Int32" "Prefer the Theming API: Demo.T.theme (...)"
                    ))
                    "a notice with no removal target must fail"

            Expect.stringContains defect "removal target" "the defect must name the missing half"
        }

        test "a removal target with no replacement fails" {
            let defect =
                Expect.wantSome
                    (obsoleteDefect (marking "Demo.T.Alpha() : System.Int32" "Removed in 1.0."))
                    "a notice with no replacement must fail"

            Expect.stringContains defect "replacement" "the defect must name the missing half"
        }

        test "a notice naming neither half says so" {
            let defect =
                Expect.wantSome
                    (obsoleteDefect (marking "Demo.T.Alpha() : System.Int32" "Deprecated."))
                    "a notice naming neither half must fail"

            Expect.stringContains defect "neither" "the defect must say both halves are missing"
        }

        test "a policy failure is not reported as a surface break" {
            let report =
                Expect.wantSome
                    (describeObsoleteDefects "Demo" (obsoleteDefects [ marking "Demo.T.Alpha() : System.Int32" "" ]))
                    "the assembly report must fail"

            Expect.stringContains
                report
                "NOTHING IS BROKEN"
                "a wording defect must not read like a removal — the reader of a red run needs to know no baseline drifted"

            Expect.isFalse (report.Contains "BREAKING") "a message defect is not a breaking change"
        }

        test "the phrasings the SDK's own live deprecations already use are accepted" {
            // Every one of these is a notice shipped in `src/` at the time
            // Phase 258 landed. A policy that rejected compliant prose
            // would teach authors to satisfy the parser, not the reader —
            // so the recogniser is measured against real notices rather
            // than against a template invented alongside it.
            let live = [
                "Use Program.withErrorReporter + an update interceptor for structured tracing. withConsoleTrace will be removed in a future major."
                "The AG Grid binding moved to the standalone Feliz.AgGrid package — `open Feliz.AgGrid` instead. This compat module is retired in a future minor."
                "Vendor-named case — use ProviderAuthUI (\"clerk\", box clerkUIConfig) instead. See docs/migrations/494-vendor-neutral-auth-ui.md. ClerkAuthUI will be removed in a future major version."
            ]

            for message in live do
                Expect.isNone
                    (obsoleteDefect (marking "Demo.T.Alpha() : System.Int32" message))
                    (sprintf "a live SDK deprecation notice must pass the policy: %s" message)
        }

        test "clean markings report nothing" {
            Expect.isNone
                (describeObsoleteDefects
                    "Demo"
                    (obsoleteDefects [
                        marking "Demo.T.Alpha() : System.Int32" "Use Demo.T.Beta instead. Removed in 1.0."
                    ]))
                "an assembly whose deprecations all conform must not fail the gate"
        }

        // ── Anti-dormancy. The two seam fixtures above would pass with
        //    the renderer emitting no attributes at all — which is
        //    exactly the state Phase 618 left behind and Phase 258
        //    fixed. This asserts the wiring from the committed
        //    baselines: at least one `(obsolete)` marker is folded in,
        //    so the renderer demonstrably emits them. Deliberately not
        //    pinned to a specific member — a deprecation that reaches
        //    its removal must not redden an unrelated gate. ──
        test "the committed baselines carry obsolete markers — the renderer emits them" {
            let markers =
                Directory.EnumerateFiles(baselineDir root, "*.approved.txt")
                |> Seq.sumBy (fun f ->
                    File.ReadLines f
                    |> Seq.filter (fun l -> l.EndsWith "  (obsolete)")
                    |> Seq.length)

            Expect.isGreaterThan
                markers
                0
                "no committed baseline carries an obsolete marker, yet src/ ships [<Obsolete>] members — the Phase 258 rendering is not wired, so a deprecation is invisible to this gate"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 175 — Public-API approval baseline" [
        comparerFixtures
        messageFixtures
        obsoleteSeamFixtures
        deprecationPolicyFixtures
        preconditionFixtures
        assemblyCases
    ]