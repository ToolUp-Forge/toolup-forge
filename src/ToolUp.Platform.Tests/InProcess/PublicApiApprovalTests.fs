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
open System.Reflection
open Expecto
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

            // Phase 261 — the doc-coverage floor rides the SAME switch, so
            // a reviewer accepting this assembly's current shape accepts
            // its current coverage in one pass. Only this assembly's line
            // is rewritten; the rest of the file is byte-identical.
            match docFileFor dll with
            | None -> () // no sidecar: the precondition case names it once
            | Some xmlPath ->
                let documented = documentedIdsIn (File.ReadAllText xmlPath)

                writeDocCoverageEntry (docCoveragePath root) (docCoverageOf a.Name render.DocSubjects documented)
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

            // Phase 261 — the XML-doc coverage ratchet, on the SAME render
            // and for the same reason the message policy is here: the
            // subject set must be exactly the surface the two arms above
            // just graded, and a second walk could drift from it.
            //
            // Ordered LAST deliberately. Drift leads (regenerating is its
            // remedy), then the deprecation wording, then coverage — a
            // reader triaging a red run wants the surface break first and
            // a documentation shortfall last, and an assembly with all
            // three reports them one run at a time in that order.
            //
            // A missing sidecar defers to the `XML documentation files are
            // emitted` precondition rather than reporting 0% here, exactly
            // as an unbuilt assembly defers to Phase 731's.
            match docFileFor dll with
            | None -> ()
            | Some xmlPath ->
                let documented = documentedIdsIn (File.ReadAllText xmlPath)
                let coverage = docCoverageOf a.Name render.DocSubjects documented
                let floorPath = docCoveragePath root

                let recorded =
                    if File.Exists floorPath then
                        parseDocCoverage (File.ReadAllText floorPath)
                    else
                        Map.empty

                match recorded.TryFind a.Name with
                | None -> failtest (describeMissingDocCoverageFloor a.Name coverage)
                | Some floor ->
                    match
                        describeDocCoverageRegression
                            floor
                            coverage
                            (undocumentedSubjects render.DocSubjects documented)
                    with
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

        // Phase 261 — the OTHER precondition, and it is a distinct fact
        // from the one above: an assembly can be built and still carry no
        // `<name>.xml`, which measures as 0% documented for a reason that
        // has nothing to do with documentation. Same shape as Phase 731's
        // for the same reason — named once, deferred to by the per-
        // assembly cases.
        test "XML documentation files are emitted" {
            // Scoped to assemblies that have a tracked public surface at
            // all: a content-only package ships no code to document, and
            // requiring a sidecar there would make the precondition
            // permanently red for a package behaving exactly as intended.
            // Derived from the committed baselines rather than named —
            // see `hasTrackedSurface`.
            let built =
                packable.Value
                |> List.filter (fun a -> (resolveDll config a).IsSome && hasTrackedSurface root a.Name)

            match describeMissingDocFiles config built.Length (missingDocFiles config built) with
            | None -> ()
            | Some report -> failtest report
        }

        // Phase 261 — a floor recorded for an assembly the packable walk
        // no longer discovers is a line nothing can ever grade again. It
        // costs nothing to leave, which is precisely why a ratchet file
        // accumulates them until it is decoration. Filter-independent:
        // discovery is not affected by which test cases run.
        test "the doc-coverage floor names only discovered assemblies" {
            let floorPath = docCoveragePath root

            if File.Exists floorPath then
                let discovered = packable.Value |> List.map _.Name |> Set.ofList

                let stale =
                    staleDocCoverageFloors discovered (parseDocCoverage (File.ReadAllText floorPath))

                Expect.isEmpty
                    stale
                    "api-baselines/doc-coverage.approved.txt records a doc-coverage floor for assemblies the packable walk does not discover — they were renamed or deleted. Remove those lines: a floor for a package that no longer exists can never be graded, and a ratchet file that accumulates them stops being read."
        }

        // Phase 261 — the vacuity guard. Every arm of the coverage gate
        // passes trivially if the doc-comment ids this pack computes match
        // NOTHING in the emitted XML: every assembly measures 0/N, the
        // floor records 0/N, and the ratchet holds forever at zero while
        // reporting green. That failure is silent by construction — it
        // looks exactly like an SDK that documents nothing — so the one
        // thing worth asserting about the committed floor is that it is
        // not all zeroes.
        //
        // Reads the committed file rather than the live tree, so it holds
        // under a filtered run and needs no build.
        test "the committed doc-coverage floor is not vacuous" {
            let floorPath = docCoveragePath root

            if File.Exists floorPath then
                let recorded = parseDocCoverage (File.ReadAllText floorPath)
                let documented = recorded |> Map.toList |> List.sumBy (fun (_, c) -> c.Documented)

                Expect.isGreaterThan
                    documented
                    0
                    "every recorded doc-coverage floor is 0 documented. Either the SDK genuinely documents nothing on its public surface, or the doc-comment ids this pack computes match nothing in the emitted XML — and the second reads exactly like the first while the gate reports green. Check `docTypeRef` / `docMethodName` against an actual <name>.xml before regenerating."
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


// ── Phase 261 — the XML-doc coverage gate. Two kinds of fixture, and
//    both are needed: the grading is pure and is driven from synthetic
//    data (so every arm is proven to fail as well as to pass), while the
//    DOC-ID COMPUTATION can only be proven against XML the real F#
//    compiler emitted — a synthetic id set would prove that the fixture
//    author and the gate agree, which is not the question. ──

/// Phase 261 — the fixture the id computation is proven against. Its
/// shape is the point: `Alpha` carries a doc comment and `Beta` does not,
/// so one assembly's real emitted XML answers both directions.
///
/// It lives in the test assembly deliberately. `ToolUp.Platform.Tests`
/// ends in `.Tests`, so `isPackableProject` excludes it and nothing here
/// reaches a committed api-baseline or the coverage floor — but it is
/// still compiled by the same compiler, with the same
/// `GenerateDocumentationFile`, into the same `<name>.xml` shape as every
/// shipped package.
module DocCoverageFixture =

    /// A documented type. This very comment is what the fixture asserts
    /// the gate can see.
    type Documented() =
        /// A documented method.
        member _.Alpha(n: int) : string = string n

        // Deliberately undocumented — the other direction of the same
        // measurement. Do not add a doc comment to this member.
        member _.Beta(n: int) : string = string n

let private docCoverageFixtures =
    // A minimal documentation file of exactly the shape the compiler
    // emits, with one entry of each id kind plus prose that must NOT be
    // mistaken for one.
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
<doc>
<assembly><name>Demo</name></assembly>
<members>
<member name="T:Demo.Widget">
<summary>A widget. See <see cref="M:Demo.Widget.Spin(System.Int32)" /> — a cref is not a member entry.</summary>
</member>
<member name="M:Demo.Widget.Spin(System.Int32)">
<summary>Spins.</summary>
</member>
<member name="P:Demo.Widget.Name">
<summary>The name.</summary>
</member>
</members>
</doc>"""

    let subject token docId = { Token = token; DocId = docId }

    // A three-member surface, all three documented by the file above.
    let fullyDocumented = [
        subject "Demo.Widget (class)" "T:Demo.Widget"
        subject "Demo.Widget.Spin(System.Int32) : System.Unit" "M:Demo.Widget.Spin(System.Int32)"
        subject "Demo.Widget.Name : System.String { get }" "P:Demo.Widget.Name"
    ]

    testList "Phase 261 doc-coverage gate" [
        test "documentedIdsIn reads member entries and not crefs" {
            let ids = documentedIdsIn xml

            Expect.isTrue (ids.Contains "T:Demo.Widget") "the type entry is a documented id"
            Expect.isTrue (ids.Contains "P:Demo.Widget.Name") "the property entry is a documented id"

            Expect.equal
                ids.Count
                3
                "a <see cref=…> inside a summary is a REFERENCE to a member, not a declaration of one — counting it would inflate coverage with members that carry no doc comment at all"
        }

        // The phase's stated contract, direction one.
        test "a fully-documented surface measures 100% and passes its floor" {
            let coverage = docCoverageOf "Demo" fullyDocumented (documentedIdsIn xml)

            Expect.equal coverage.Documented 3 "all three subjects are keyed by the documentation file"
            Expect.equal (DocCoverage.percent coverage) 100.0 "coverage is total"

            Expect.isNone
                (describeDocCoverageRegression
                    coverage
                    coverage
                    (undocumentedSubjects fullyDocumented (documentedIdsIn xml)))
                "a surface at its recorded floor passes"
        }

        // The phase's stated contract, direction two — the go-red proof.
        test "an undocumented public member drops coverage below the floor and FAILS" {
            let documented = documentedIdsIn xml
            let floor = docCoverageOf "Demo" fullyDocumented documented

            let grown =
                fullyDocumented
                @ [ subject "Demo.Widget.Wobble() : System.Unit" "M:Demo.Widget.Wobble" ]

            let current = docCoverageOf "Demo" grown documented

            Expect.equal current.Documented 3 "the new member is not in the documentation file"
            Expect.equal current.Total 4 "the surface grew by one"

            match describeDocCoverageRegression floor current (undocumentedSubjects grown documented) with
            | None ->
                failtest "an undocumented public member landed and the gate stayed green — the ratchet does not hold"
            | Some report ->
                Expect.stringContains report "Demo" "the report names the assembly"
                Expect.stringContains report "REGRESSED" "the verdict is stated"
                Expect.stringContains report "Demo.Widget.Wobble" "the undocumented member is named"

                Expect.stringContains
                    report
                    "NOTHING IS BROKEN"
                    "a coverage shortfall is not a surface break, and the reader of a red run needs to be told which one they are looking at"

                Expect.stringContains
                    report
                    "doc-coverage.approved.txt"
                    "the report names the file that records the floor"
        }

        // The one place this gate departs from Phase 618's both-directions
        // rule. Pinned so the departure is deliberate rather than an
        // omission someone later "fixes".
        test "IMPROVING coverage never fails" {
            let floor = {
                Assembly = "Demo"
                Documented = 1
                Total = 4
            }

            let better = {
                Assembly = "Demo"
                Documented = 4
                Total = 4
            }

            Expect.isNone
                (describeDocCoverageRegression floor better [])
                "documenting members must not fail the gate — a floor that is beaten is doing its job, not going stale"
        }

        test "removing a documented member does not fire this gate" {
            // total and documented fall together, so the undocumented
            // count is unchanged. A removal is Phase 618's business; this
            // gate reporting it too would double-charge one change.
            let floor = {
                Assembly = "Demo"
                Documented = 3
                Total = 5
            }

            let after = {
                Assembly = "Demo"
                Documented = 2
                Total = 4
            }

            Expect.isNone
                (describeDocCoverageRegression floor after [])
                "a removal leaves the undocumented count where it was; the removal itself is reported by the surface comparer"
        }

        test "a deleted doc comment fires even though the surface is unchanged" {
            let floor = {
                Assembly = "Demo"
                Documented = 3
                Total = 5
            }

            let after = {
                Assembly = "Demo"
                Documented = 2
                Total = 5
            }

            Expect.isSome
                (describeDocCoverageRegression floor after [])
                "the surface did not move but a member lost its documentation — exactly the drift a fraction-only threshold over a large denominator would hide"
        }

        test "the floor file round-trips, and a mangled line is dropped rather than guessed" {
            let entries = [
                {
                    Assembly = "Demo.B"
                    Documented = 1
                    Total = 2
                }
                {
                    Assembly = "Demo.A"
                    Documented = 3
                    Total = 4
                }
            ]

            let text = renderDocCoverage entries
            let parsed = parseDocCoverage text

            Expect.equal parsed.Count 2 "both entries survive the round trip"
            Expect.equal (parsed.["Demo.A"]).Documented 3 "counts survive the round trip"

            let lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')
            let body = lines |> Array.filter (fun l -> not (l.StartsWith "#"))

            Expect.equal
                body[0]
                "Demo.A 3/4"
                "assemblies render in ordinal order, so a re-run produces no reordering diff"

            let mangled = parseDocCoverage (text + "Demo.C 7\n")

            Expect.isFalse
                (mangled.ContainsKey "Demo.C")
                "an unparseable line reads as 'no floor recorded', which fails loudly at that assembly, rather than as a silently wrong number"
        }

        test "an upsert rewrites one line and leaves the rest byte-identical" {
            let before =
                renderDocCoverage [
                    {
                        Assembly = "Demo.A"
                        Documented = 3
                        Total = 4
                    }
                    {
                        Assembly = "Demo.B"
                        Documented = 1
                        Total = 2
                    }
                ]

            let after =
                upsertDocCoverage before {
                    Assembly = "Demo.B"
                    Documented = 2
                    Total = 2
                }

            let parsed = parseDocCoverage after

            Expect.equal (parsed.["Demo.A"]).Documented 3 "the untouched assembly's floor is unchanged"
            Expect.equal (parsed.["Demo.B"]).Documented 2 "the named assembly's floor moved"

            Expect.stringContains
                after
                "Demo.A 3/4"
                "a scoped regeneration must not rewrite another session's line — the whole point of TOOLUP_APPROVE_API taking names"
        }

        test "a floor for an undiscovered assembly is reported" {
            let recorded =
                parseDocCoverage (
                    renderDocCoverage [
                        {
                            Assembly = "Demo.A"
                            Documented = 1
                            Total = 1
                        }
                        {
                            Assembly = "Demo.Renamed"
                            Documented = 1
                            Total = 1
                        }
                    ]
                )

            let stale = staleDocCoverageFloors (Set.ofList [ "Demo.A" ]) recorded

            Expect.equal
                stale
                [ "Demo.Renamed" ]
                "a floor nothing can grade again is named, so the file cannot rot into decoration"

            Expect.isEmpty
                (staleDocCoverageFloors (Set.ofList [ "Demo.A"; "Demo.Renamed" ]) recorded)
                "and stays quiet otherwise"
        }

        test "a new package with no recorded floor fails, naming the remedy" {
            let report =
                describeMissingDocCoverageFloor "Demo.New" {
                    Assembly = "Demo.New"
                    Documented = 0
                    Total = 7
                }

            Expect.stringContains report "Demo.New" "the report names the package"
            Expect.stringContains report "TOOLUP_APPROVE_API" "and the switch that records the floor"
        }

        test "the missing-documentation-file precondition fires once and stays quiet otherwise" {
            let a name = {
                Name = name
                ProjectPath = name + ".fsproj"
                ProjectDir = "."
            }

            Expect.isNone (describeMissingDocFiles "Debug" 3 []) "a tree with every sidebar present says nothing"

            match describeMissingDocFiles "Debug" 3 [ a "Demo.A"; a "Demo.B" ] with
            | None -> failtest "a built tree with no XML documentation must be reported"
            | Some report ->
                Expect.stringContains
                    report
                    "GenerateDocumentationFile"
                    "the report names the property that emits the file"

                Expect.stringContains
                    report
                    "not a documentation shortfall"
                    "a missing sidecar measures as 0% documented, which would otherwise send the reader off to write doc comments for a build-property problem"
        }

        // The content-only-package exemption, both directions. Pinned
        // because it is the one place the precondition can be softened,
        // and a softening that swallowed a real assembly would take the
        // whole ratchet with it: an assembly wrongly read as
        // surface-less needs no sidecar, measures nothing, and is never
        // heard from again.
        test "an assembly with no tracked surface needs no documentation file" {
            let scratch =
                Path.Combine(Path.GetTempPath(), "toolup-doccoverage-" + System.Guid.NewGuid().ToString("N"))

            try
                Directory.CreateDirectory(Path.Combine(scratch, "api-baselines")) |> ignore

                let write (name: string) (body: string) =
                    File.WriteAllText(Path.Combine(scratch, "api-baselines", name + ".approved.txt"), body)

                // A content-only package: the generated header and nothing else.
                write "Demo.ContentOnly" "# Public API baseline — Demo.ContentOnly\n# Do not edit by hand.\n"
                write "Demo.Real" "# Public API baseline — Demo.Real\nDemo.Widget (class)\n"

                Expect.isFalse
                    (hasTrackedSurface scratch "Demo.ContentOnly")
                    "a baseline of comments records no surface, so there is nothing for a documentation file to document"

                Expect.isTrue (hasTrackedSurface scratch "Demo.Real") "one member line is a tracked surface"

                Expect.isTrue
                    (hasTrackedSurface scratch "Demo.Missing")
                    "a package with no committed baseline is already failing the drift arm; reading it as surface-less here would let the two findings cancel"
            finally
                try
                    Directory.Delete(scratch, true)
                with _ ->
                    ()
        }

        // The end-to-end arm: the doc-comment ids this pack computes are
        // checked against XML the real F# compiler emitted for the fixture
        // above. A synthetic id set cannot prove this — it would only
        // prove the fixture and the gate agree with each other.
        test "the computed doc ids match the compiler's own XML" {
            let selfDll = Assembly.GetExecutingAssembly().Location

            match docFileFor selfDll with
            | None ->
                failtestf
                    "no XML documentation file beside %s — GenerateDocumentationFile is not in effect for the test assembly, so the doc-id computation cannot be proven against real compiler output."
                    (Path.GetFileName selfDll)
            | Some xmlPath ->
                let documented = documentedIdsIn (File.ReadAllText xmlPath)
                let subjects = (renderSurfaceDetail selfDll pool.Value).DocSubjects

                let find needle =
                    subjects
                    |> List.tryFind (fun s -> s.DocId.EndsWith("DocCoverageFixture.Documented." + needle))

                match find "Alpha(System.Int32)", find "Beta(System.Int32)" with
                | Some alpha, Some beta ->
                    Expect.isTrue
                        (documented.Contains alpha.DocId)
                        (sprintf
                            "the fixture's DOCUMENTED member reads as undocumented — the computed id %s appears in no <member name=…> entry, so every assembly's coverage is understated and the ratchet holds at a floor that is not the truth"
                            alpha.DocId)

                    Expect.isFalse
                        (documented.Contains beta.DocId)
                        "the fixture's deliberately-undocumented member reads as documented — the match is too loose to measure anything"
                | _ ->
                    failtest
                        "the DocCoverageFixture members were not found on the rendered surface of the test assembly — the fixture was renamed or made non-public, and with it the only proof that the doc-id computation matches real compiler output."
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
        docCoverageFixtures
        assemblyCases
    ]