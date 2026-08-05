module ToolUp.Platform.Tests.InProcess.ArchitectureFitnessTests

open System
open System.Diagnostics
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
            Expect.stringContains findings[0].Detail "AgGridEnterprise" "finding names the Enterprise module"
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

            Expect.stringContains findings[0].Detail "Beta.Module" "finding names the sibling module that was imported"
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

// ─── Phase 624 — FS0025 stays a build error ───────────────────────────
//
// `Directory.Build.props` promotes FS0025 to an error tree-wide. These
// tests keep that promotion meaningful by pinning at zero the two
// suppressions that would silence it for a whole FILE or a whole
// PROJECT, and by asserting the policy itself is still declared — a gate
// that can be deleted to make a build go green is not a gate.
//
// The `| _ ->` wildcard is deliberately out of scope; see the rationale
// block in Contracts/ArchitectureFitness.fs for why no census over
// compiler output can see one, and what to use instead.

let private fs0025Tests =
    testList "Phase 624 — incomplete-match gate" [

        test "the tree-wide FS0025-as-error policy is still declared" {
            Expect.isTrue
                (policyDeclaresFs0025AsError ())
                "Directory.Build.props must keep FS0025 in <WarningsAsErrors>. If this failed, the incomplete-match gate was removed — restore it rather than deleting this test."
        }

        test "no source file silences FS0025 for its whole file" {
            let findings =
                fs0025ScannableFiles ()
                |> List.filter (fun p -> p.EndsWith ".fs" || p.EndsWith ".fsx")
                |> List.collect (fun path -> scanNowarnSuppressions (relative path) (File.ReadAllText path))

            Expect.isEmpty
                findings
                (sprintf
                    "`#nowarn \"25\"` disables the incomplete-match gate for an entire file. Offending file(s):\n%s"
                    (findings |> List.map formatSourceFinding |> String.concat "\n"))
        }

        test "no project exempts itself from FS0025" {
            let findings =
                fs0025ScannableFiles ()
                |> List.filter (fun p -> p.EndsWith ".fsproj" || p.EndsWith ".props")
                |> List.collect (fun path -> scanProjectSuppressions (relative path) (File.ReadAllText path))

            Expect.isEmpty
                findings
                (sprintf
                    "A project named FS0025 in <NoWarn> / <WarningsNotAsErrors>, exempting itself from the incomplete-match gate. Offending project(s):\n%s"
                    (findings |> List.map formatSourceFinding |> String.concat "\n"))
        }

        test "the sweep still walks a non-trivial slice of the tree, and no foreign worktree" {
            let files = fs0025ScannableFiles ()

            let sources = files |> List.filter (fun p -> p.EndsWith ".fs" || p.EndsWith ".fsx")

            let projects =
                files |> List.filter (fun p -> p.EndsWith ".fsproj" || p.EndsWith ".props")

            // Floors, deliberately far below the ~2,000 / ~220 the tree
            // carries today, so adding or moving files never edits this
            // test. Both assertions the gate actually makes are
            // `Expect.isEmpty` — which a sweep that matches NOTHING passes
            // perfectly. An over-broad prune or a broken root walk would
            // therefore turn the gate off and report green; these floors
            // make that failure loud instead.
            Expect.isGreaterThan
                (List.length sources)
                1000
                "the FS0025 sweep found almost no F# sources — the gate is only as good as its root set, and an empty one passes vacuously. Check the prune list in ArchitectureFitness.fs rather than lowering this floor."

            Expect.isGreaterThan
                (List.length projects)
                100
                "the FS0025 sweep found almost no project/props files — same reasoning as the source floor above."

            // A concurrent agent session's isolated git worktree is a full
            // second checkout of this tree living inside it. It is not our
            // source, its copies of the exempt subtrees do not match the
            // root-anchored exemption prefixes, and it appears and vanishes
            // with no change to this repo — so its files must never reach
            // the gate.
            // Measured relative to the repo root, never on the absolute
            // path: a session verifying in an isolated worktree runs this
            // very test from inside `.claude/worktrees/<name>/`, where
            // every absolute path contains `/.claude/`. An absolute test
            // here calls the checkout under test foreign — the second of
            // the two failures this gate has actually produced.
            Expect.isEmpty
                (files |> List.filter (fun p -> (pathUnder (repoRoot ()) p).Contains "/.claude/"))
                "the sweep walked into .claude/ — a foreign worktree there fails the gate on files that are neither ours nor in scope"
        }

        test "every exemption is declared where authors read the policy" {
            let propsText = File.ReadAllText(Path.Combine(repoRoot (), "Directory.Build.props"))

            for exemption in fs0025ExemptPrefixes do
                Expect.stringContains
                    propsText
                    exemption.Prefix
                    (sprintf
                        "`%s` is exempt from the FS0025 gate but is not named in Directory.Build.props. An exemption the policy comment does not mention makes the policy read as stronger than it is — document it there (reason on file: %s)."
                        exemption.Prefix
                        exemption.Reason)

                Expect.isNotEmpty exemption.Reason (sprintf "exemption `%s` must carry a reason" exemption.Prefix)
        }

        // ── fail-closed: the detectors fire on planted violations ──────

        test "a planted #nowarn \"25\" is detected" {
            let source =
                "module Planted\n\n#nowarn \"25\"\n\nlet f x = match x with | Some v -> v\n"

            let findings = scanNowarnSuppressions "planted.fs" source

            Expect.hasLength findings 1 "the file-scoped suppression must be detected"
            Expect.equal findings[0].Line 3 "the finding names the directive's line"
        }

        test "a planted FS0025 NoWarn is detected in both property spellings" {
            let noWarn =
                scanProjectSuppressions "planted.fsproj" "<NoWarn>$(NoWarn);FS0025</NoWarn>"

            let notAsErrors =
                scanProjectSuppressions "planted.fsproj" "<WarningsNotAsErrors>FS0025</WarningsNotAsErrors>"

            Expect.hasLength noWarn 1 "a NoWarn naming FS0025 must be detected"
            Expect.hasLength notAsErrors 1 "a WarningsNotAsErrors naming FS0025 must be detected"
        }

        test "the bare `25` spelling is detected but `NU1525` is not" {
            Expect.hasLength (scanProjectSuppressions "p.fsproj" "<NoWarn>25</NoWarn>") 1 "bare `25` is warning 25"

            Expect.isEmpty
                (scanProjectSuppressions "p.fsproj" "<NoWarn>NU1525;FS0250</NoWarn>")
                "ids that merely CONTAIN 25 must not match — a detector that fires on everything proves nothing"
        }

        test "an unrelated #nowarn is not flagged" {
            let source = "module Planted\n\n#nowarn \"44\"\n#nowarn \"51\"\n"

            Expect.isEmpty
                (scanNowarnSuppressions "planted.fs" source)
                "only warning 25 is in scope; the deprecation/pointer suppressions the tree legitimately uses must pass"
        }
    ]

// ─── Phase 635 — the sweeps are scoped to THIS checkout ───────────────
//
// The gates above are rooted at the repo root, so their verdict is only
// as good as their notion of "this checkout". Twice, a concurrent agent
// session's git worktree under `.claude/worktrees/` was walked into and
// failed the gate for every session on the machine — once on a stale
// worktree's `docs-snippets/ToolUp.DocSnippets.fsproj` (exempt at
// `docs-snippets/`, not at `.claude/worktrees/x/docs-snippets/`), once on
// the source-count floor. Neither had anything to do with this repo.
//
// The enumeration therefore asks git rather than walking directories.
// These tests exercise that on a purpose-built throwaway tree, in both
// directions — an in-scope violation must still be caught, an
// out-of-scope copy of the same violation must not be — because an
// exclusion that is never proved to still catch anything is
// indistinguishable from switching the gate off.

/// The violation the gate exists to catch, as a project file.
let private fs0025ViolationProject =
    "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <NoWarn>$(NoWarn);FS0025</NoWarn>\n  </PropertyGroup>\n</Project>\n"

/// The same shape with an unrelated suppression — the detector must stay
/// quiet on it, or "caught the violation" proves nothing.
let private cleanProject =
    "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <NoWarn>$(NoWarn);NU1701</NoWarn>\n  </PropertyGroup>\n</Project>\n"

let private writeFixtureFile (root: string) (relPath: string) (contents: string) =
    let full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar))
    Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
    File.WriteAllText(full, contents)

/// `git <args>` in `dir`; true on a zero exit. Used only to *build* the
/// fixture — the code under test runs git itself.
let private tryGit (dir: string) (args: string) : bool =
    try
        let psi = ProcessStartInfo("git", args)
        psi.WorkingDirectory <- dir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start psi
        proc.StandardOutput.ReadToEnd() |> ignore
        proc.StandardError.ReadToEnd() |> ignore
        proc.WaitForExit 60_000 && proc.ExitCode = 0
    with _ ->
        false

/// A throwaway tree shaped like this repo, carrying the same FS0025
/// violation in four places: one in scope, three not. `asGitCheckout`
/// selects which enumeration strategy the tree exercises.
///
/// Returns the root; the caller deletes it.
let private plantFixtureTree (asGitCheckout: bool) : string =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-phase635-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore

    writeFixtureFile root ".gitignore" ".claude/worktrees/\nscratch/\n"

    // In scope: an ordinary project of this checkout.
    writeFixtureFile root "src/Violation.fsproj" fs0025ViolationProject
    writeFixtureFile root "src/Clean.fsproj" cleanProject

    // Out of scope 1 — a concurrent session's worktree, at the exact path
    // and filename that produced the observed false red.
    writeFixtureFile root ".claude/worktrees/sibling/docs-snippets/ToolUp.DocSnippets.fsproj" fs0025ViolationProject

    // Out of scope 2 — an ordinary gitignored directory with nothing to
    // do with `.claude/`. A hardcoded prune list cannot exclude this one;
    // it is the whole reason the enumeration asks git.
    writeFixtureFile root "scratch/Ignored.fsproj" fs0025ViolationProject

    // Out of scope 3 — a second checkout that is NOT gitignored. Git
    // reports an untracked nested repository as a single directory entry
    // and never lists its files, so a worktree parked anywhere at all is
    // excluded without anyone having to predict where.
    writeFixtureFile root "nested-checkout/src/Nested.fsproj" fs0025ViolationProject

    if asGitCheckout then
        tryGit root "init -q ." |> ignore
        tryGit (Path.Combine(root, "nested-checkout")) "init -q ." |> ignore

    root

let private deleteFixtureTree (root: string) =
    try
        Directory.Delete(root, true)
    with _ ->
        () // a leftover temp tree is not worth failing a gate over

/// Segment test against the path *relative to `root`*. Never against the
/// absolute path — the checkout under test may itself live under any of
/// the segments being looked for (see `pathUnder`).
let private containsSegment (root: string) (segment: string) (path: string) = (pathUnder root path).Contains segment

let private phase635Tests =
    testList "Phase 635 — checkout-scoped enumeration" [

        test "git-derived enumeration excludes ignored paths and nested checkouts" {
            let root = plantFixtureTree true

            try
                match gitCheckoutFiles root with
                | None ->
                    skiptest
                        "no usable git CLI — the fallback walk is covered by its own test below, but the git strategy this repo actually takes cannot be exercised here"
                | Some _ ->
                    // Verify the probe, not just the verdict: everything
                    // below is only meaningful because git answered.
                    let files = enumerateCheckoutFiles root

                    let named name =
                        files |> List.exists (fun p -> Path.GetFileName p = name)

                    Expect.isTrue (named "Violation.fsproj") "an ordinary project of the checkout must be enumerated"

                    Expect.isEmpty
                        (files |> List.filter (containsSegment root "/.claude/"))
                        "a concurrent session's worktree under .claude/ must never be enumerated — this is the failure the phase exists to close"

                    Expect.isEmpty
                        (files |> List.filter (containsSegment root "/scratch/"))
                        "an ordinary gitignored directory must be excluded too. If this failed, the enumeration is matching a hardcoded path list rather than asking git, and it will drift the next time anyone edits .gitignore"

                    Expect.isEmpty
                        (files |> List.filter (containsSegment root "/nested-checkout/"))
                        "a second checkout that is not gitignored must still be excluded — git reports an untracked nested repository as a directory, never as its files"
            finally
                deleteFixtureTree root
        }

        test "the same violation is caught in scope and ignored out of scope" {
            let root = plantFixtureTree true

            try
                match gitCheckoutFiles root with
                | None -> skiptest "no usable git CLI — see the enumeration test above"
                | Some _ ->
                    let findings =
                        enumerateCheckoutFiles root
                        |> List.filter (fun p -> p.EndsWith ".fsproj")
                        |> List.collect (fun path -> scanProjectSuppressions path (File.ReadAllText path))

                    // Four copies of the violation are planted; exactly
                    // one is this checkout's. An exclusion that also
                    // swallowed the real one would be a silent gate.
                    Expect.hasLength
                        findings
                        1
                        (sprintf
                            "exactly the in-scope violation must be reported. Findings:\n%s"
                            (findings |> List.map formatSourceFinding |> String.concat "\n"))

                    Expect.equal
                        (Path.GetFileName findings[0].File)
                        "Violation.fsproj"
                        "the surviving finding must be the one under src/, not a copy from a worktree, an ignored directory or a nested checkout"
            finally
                deleteFixtureTree root
        }

        test "the fallback walk still excludes .claude/ when git cannot answer" {
            // Same tree with no git anywhere in it, so `gitCheckoutFiles`
            // returns None and the prune-list walk takes over.
            let root = plantFixtureTree false

            try
                // Verify the probe: if the temp directory happens to sit
                // inside someone's work tree, git answers and this test
                // would silently exercise the git path instead. Skip
                // rather than assert — a machine whose TEMP is inside a
                // repo is unusual, not broken, and a false red here would
                // be the very class of failure this phase closes.
                if (gitCheckoutFiles root).IsSome then
                    skiptest "TEMP is itself inside a git work tree, so the no-git fallback cannot be isolated here"

                let files = enumerateCheckoutFiles root

                Expect.isTrue
                    (files |> List.exists (fun p -> Path.GetFileName p = "Violation.fsproj"))
                    "the fallback must still enumerate the checkout's own sources"

                Expect.isEmpty
                    (files |> List.filter (containsSegment root "/.claude/"))
                    "the prune list must keep .claude/ out even without git — this is the case it was written for"

                // Stated rather than hidden: the fallback cannot read
                // .gitignore, so it does walk trees git would have
                // excluded. That is why it is the fallback and not the
                // strategy.
                Expect.isTrue
                    (files |> List.exists (containsSegment root "/scratch/"))
                    "the fallback is expected to be weaker than git here; if it stopped being so, the prune list grew a second copy of .gitignore and this test should become an isEmpty"
            finally
                deleteFixtureTree root
        }

        test "the live checkout's file set carries no foreign worktree" {
            Expect.isEmpty
                (repoFiles () |> List.filter (containsSegment (repoRoot ()) "/.claude/"))
                "the live enumeration reached into .claude/ — every sweep in this file is rooted at the repo root and would inherit the foreign files"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 174 — architecture-fitness gate" [
        directionTests
        liveSourceTests
        failClosedTests
        fs0025Tests
        phase635Tests
    ]