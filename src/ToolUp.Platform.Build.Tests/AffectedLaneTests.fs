// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.AffectedLaneTests

open System.IO
open Expecto
open ToolUp.Forge
open ToolUp.Forge.TestLane.Selection
open ToolUp.Platform.Tests.Support

// ─── The `affected` lane — the selection and the keep ────────────────
//
// `Select` (Build.fs) and the Platform pack's keep both run code that is
// source-linked here, so every rule below is the rule the lane applies.
// The forcing paths are pinned in both directions; the keep's go-red
// proofs run a synthetic pack's cases through the same filter the
// Platform pack uses, so "a failing list that was not selected cannot
// fail the lane, and the same list selected does" is a measurement, not
// a reading of the code.

/// Each forcing rule with paths that must fire it and near misses that
/// must not. A rule added to `forcingRules` without a row here fails the
/// coverage case below, so a new forcing path is pinned or it is red.
let private forcingTable = [
    "msbuild-import",
    [
        "Directory.Build.props"
        "Directory.Packages.props"
        "src/ToolUp.Sdk/build/ToolUp.Sdk.props"
        "x/a.targets"
    ],
    [
        "docs/props.md"
        "src/ToolUp.Platform.Server/Server/Props.fs"
        "DRAFT-NOTES.md"
    ]
    "solution", [ "ToolUp.Forge.sln"; "tools/x.slnx" ], [ "docs/solution.md" ]
    "build-script",
    [ "Build.fs"; "Build.fsproj"; "SampleGate.fs"; "verify.ps1" ],
    [
        "src/ToolUp.Platform.Build/Build/SDK.Build.fs"
        "dev-scripts/verify.ps1"
        "README.md"
    ]
    "toolchain", [ "global.json"; "nuget.config"; "samples/X/NuGet.Config" ], [ "src/x/global-config.json" ]
    "composition-root",
    [
        "src/ToolUp.Platform.Server/Server/Compose/ComposeStores.fs"
        "src/ToolUp.Platform.Server/Server/ServerApp.fs"
        "src/ToolUp.Platform.Server/Server/SDK.Server.fs"
    ],
    [ "src/ToolUp.Platform.Server/Server/JobScheduler.fs" ]
    "server-client-config",
    [
        "src/ToolUp.Platform.Core/Shared/SDK.Shared.fs"
        "src/ToolUp.Platform.Core/Shared/Config/StoreModes.fs"
        "src/ToolUp.Platform.Client/Client/SDK.ClientTypes.fs"
    ],
    [ "src/ToolUp.Platform.Core/Shared/ModuleIdentity.fs" ]
    "module-registry",
    [
        "src/ToolUp.AI.Server/Server/AICompose.fs"
        "src/ToolUp.Platform.Client/Client/RegisteredModules.fs"
    ],
    [ "src/ToolUp.AI.Server/Server/AIChat.fs"; "docs/AICompose.fs.md" ]
    "test-support",
    [
        "src/ToolUp.Platform.Tests/Support/TestScope.fs"
        "src/ToolUp.Platform.Testing/Fixtures.fs"
        "src/ToolUp.Platform.Tests/fixtures/sample.json"
    ],
    [
        "src/ToolUp.Platform.Tests/InProcess/LocalFileStorageTests.fs"
        "src/ToolUp.Forms.Tests/Support/X.fs"
    ]
    "test-project",
    [
        "src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj"
        "src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj"
    ],
    [ "src/ToolUp.Forms.Server/ToolUp.Forms.Server.fsproj" ]
]

let private rulesFiring (path: string) =
    forcingRules |> List.filter (fun r -> r.Matches path) |> List.map _.Name

/// A synthetic Platform pack: a registration and the files it names.
let private programText =
    """module ToolUp.Platform.Tests.Program

let private registeredTests =
    testList "ToolUp.Platform.Tests" [
        AlphaTests.tests
        BetaTests.tests
        // a comment line is not an entry
        IWidgetContract.tests "Default" IWidgetContract.defaultFactory
        IWidgetContract.tests
            "Decorated"
            IWidgetContract.decoratedFactory
        testSequencedGroup
            "env"
            (testList "env group" [
                GammaTests.tests
                ToolUp.Platform.Tests.Composition.CompositionBaselineTests.tests
            ])
        PublicApiApprovalTests.tests
    ]

let private deliberatelyUnregistered = []
"""

let private files =
    Map [
        PlatformProgram, programText
        "src/ToolUp.Platform.Tests/InProcess/AlphaTests.fs",
        "module ToolUp.Platform.Tests.InProcess.AlphaTests\n\n[<Tests>]\nlet tests = testList \"Alpha\" []\n"
        "src/ToolUp.Platform.Tests/InProcess/BetaTests.fs",
        "namespace ToolUp.Platform.Tests.InProcess\n\nmodule BetaTests =\n    let tests = testList \"Beta\" []\n"
        "src/ToolUp.Platform.Tests/InProcess/GammaTests.fs",
        "module ToolUp.Platform.Tests.InProcess.GammaTests\n\nlet tests = testList \"Gamma\" []\n"
        "src/ToolUp.Platform.Tests/Contracts/IWidgetContract.fs",
        "module ToolUp.Platform.Tests.Contracts.IWidgetContract\n\nlet tests name factory = testList name []\n"
        "src/ToolUp.Platform.Tests/InProcess/SharedFixtures.fs",
        "module ToolUp.Platform.Tests.InProcess.SharedFixtures\n\nlet store () = ()\n"
        "src/ToolUp.KnowledgeBase.Server/Server/Notes.fs", "module ToolUp.KnowledgeBase.Notes\n"
    ]

let private selectOver (paths: string list) (declared: string list) =
    select {
        Base = Some "base0"
        Head = Some "head0"
        Paths = paths
        Declared = declared
        Packs = [ PlatformPack; "Forms"; "Voice" ]
        Family = []
        Graph = None
        Read = fun p -> Map.tryFind p files
        ListsFile = "out/selection.lists"
        Command = "dotnet run --project Build.fsproj -- Select --base base0 --head head0 --out out/selection.json"
    }

/// The Platform family on the real tree, read off the gate manifest's rows.
let private realFamily () =
    familyOf (fst (GateReconcileTests.declared ()) |> List.map (fun p -> p.Name, p.Project))

/// Every pack row of the real manifest with its project.
let private realPackProjects () =
    fst (GateReconcileTests.declared ())
    |> List.map (fun p -> p.Name, normalise p.Project)

let private realRead (p: string) =
    let full = Path.Combine(GateReconcileTests.repoRoot, p)

    if File.Exists full then
        Some(File.ReadAllText full)
    else
        None

/// A synthetic project graph: Core; part A over Core and part B over A, both
/// compiled from the umbrella's folder; the umbrella over B; one pack per
/// part, a residue over the umbrella, and an unrelated pack over Core.
let private graphFiles =
    let proj (items: string list) (refs: string list) =
        "<Project>\n<ItemGroup>\n"
        + String.concat "" [ for i in items -> sprintf "<Compile Include=\"%s\" />\n" i ]
        + String.concat "" [ for r in refs -> sprintf "<ProjectReference Include=\"%s\" />\n" r ]
        + "</ItemGroup>\n</Project>\n"

    Map [
        "src/Core/Core.fsproj", proj [ @"Shared\C.fs" ] []
        "src/PartA/PartA.fsproj", proj [ @"..\Umbrella\Server\A1.fs" ] [ @"..\Core\Core.fsproj" ]
        "src/PartB/PartB.fsproj", proj [ @"..\Umbrella\Server\B1.fs" ] [ @"..\PartA\PartA.fsproj" ]
        "src/Umbrella/Umbrella.fsproj", proj [ @"Server\U1.fs" ] [ @"..\PartB\PartB.fsproj" ]
        "src/Tests.A/Tests.A.fsproj",
        proj [ @"..\Tests\InProcess\ATests.fs"; "Program.fs" ] [ @"..\PartA\PartA.fsproj" ]
        "src/Tests.B/Tests.B.fsproj",
        proj [ @"..\Tests\InProcess\BTests.fs"; "Program.fs" ] [ @"..\PartB\PartB.fsproj" ]
        "src/Tests/Tests.fsproj", proj [ @"InProcess\UTests.fs"; "Program.fs" ] [ @"..\Umbrella\Umbrella.fsproj" ]
        "src/Other.Tests/Other.Tests.fsproj", proj [ "OtherTests.fs" ] [ @"..\Core\Core.fsproj" ]
    ]

let private graphPacks = [
    "PA", "src/Tests.A/Tests.A.fsproj"
    "PB", "src/Tests.B/Tests.B.fsproj"
    "R", "src/Tests/Tests.fsproj"
    "Other", "src/Other.Tests/Other.Tests.fsproj"
]

let private graphOver (paths: string list) =
    select {
        Base = Some "base0"
        Head = Some "head0"
        Paths = paths
        Declared = []
        Packs = graphPacks |> List.map fst
        Family = []
        Graph =
            Some {
                PackProjects = graphPacks
                Cases = Map [ "PB", 7 ]
            }
        Read = fun p -> Map.tryFind p graphFiles
        ListsFile = "out/selection.lists"
        Command = "Select --source graph"
    }

let private selectedPacks (m: Manifest) =
    m.Graph
    |> Option.map (fun g -> g.Selected |> List.map fst)
    |> Option.defaultValue []

let private skippedPacks (m: Manifest) =
    m.Graph
    |> Option.map (fun g -> g.Skipped |> List.map fst)
    |> Option.defaultValue []

let private unitsFrom (source: Source) (m: Manifest) =
    m.Units |> List.filter (fun u -> u.Source = source) |> List.map _.Unit

/// Run every case of `tree` the way a runner would, returning the names of
/// the ones that failed. Enough of a runner for a go-red proof: it is the
/// filtered tree's own test code that runs.
let private failingCases (tree: Test) =
    Test.toTestCodeList tree
    |> List.choose (fun flat ->
        let run () =
            match flat.test with
            | Sync f -> f ()
            | SyncWithCancel f -> f System.Threading.CancellationToken.None
            | Async a -> Async.RunSynchronously a
            | AsyncFsCheck(_, _, f) -> Async.RunSynchronously(f FsCheckConfig.defaultConfig)

        try
            run ()
            None
        with _ ->
            Some(String.concat "/" flat.name))

/// A synthetic registered pack: one healthy list, one list holding a
/// deliberately failing case, one sequenced group.
let private healthy = testList "Healthy" [ testCase "passes" ignore ]

let private injected =
    testList "Injected" [ testCase "fails on purpose" (fun () -> failwith "injected failure") ]

let private grouped = testList "Grouped inner" [ testCase "passes" ignore ]

let private registered =
    testList "Synthetic.Tests" [ healthy; injected; testSequencedGroup "g" (testList "Group" [ grouped ]) ]

let private resolver (name: string) =
    match name with
    | "Synthetic.Tests.Inner.grouped" -> Some grouped
    | _ -> None

let private noDeclaration: TestLaneFilter.LaneDeclaration = { Slow = []; Pure = [] }

let private lists (names: string list) =
    TestLane.Lists.parse (String.concat "\n" names)

let private runLane (names: string list) =
    match TestLaneFilter.resolveKeep resolver (lists names) registered with
    | Ok keep ->
        let tree =
            TestLaneFilter.forLaneIn TestLane.Lane.Affected noDeclaration (Some keep) registered registered

        Ok(keep, failingCases tree)
    | Error why -> Error why

[<Tests>]
let tests =
    testList "The affected lane — selection, forcing paths and the keep" [
        testList "forcing paths" [
            for rule, fires, misses in forcingTable do
                testCase (sprintf "%s fires on its paths and not on its near misses" rule)
                <| fun () ->
                    for p in fires do
                        Expect.contains (rulesFiring p) rule (sprintf "%s should fire %s" p rule)

                    for p in misses do
                        Expect.isFalse (List.contains rule (rulesFiring p)) (sprintf "%s should not fire %s" p rule)

            testCase "every forcing rule has a row in the table"
            <| fun () ->
                let pinned = forcingTable |> List.map (fun (r, _, _) -> r) |> set
                let declared = forcingRules |> List.map _.Name |> set
                Expect.equal declared pinned "a forcing rule without a pinned row, or a row for a rule that is gone"

            testCase "the Platform pack's Program.fs is deliberately not a forcing path"
            <| fun () ->
                Expect.isEmpty (rulesFiring PlatformProgram) "Program.fs fires a rule"
                let m = selectOver [ PlatformProgram ] []
                Expect.isFalse m.Forced "a registration change alone forced the full lane"
                Expect.isEmpty (unitsFrom Source.Diffed m) "Program.fs selected lists of its own"

            testCase "a diff touching Directory.Build.props runs the full lane and names the rule"
            <| fun () ->
                let m =
                    selectOver [ "src/ToolUp.Platform.Tests/InProcess/AlphaTests.fs"; "Directory.Build.props" ] []

                Expect.isTrue m.Forced "not forced"

                Expect.equal
                    m.Fired
                    [
                        {
                            Rule = "msbuild-import"
                            Path = "Directory.Build.props"
                        }
                    ]
                    "the fired rule is not named"

                Expect.isEmpty m.Skipped "a full lane skipped lists"
                Expect.isTrue m.ClientTierMoved "a full lane skipped a leg"

                Expect.stringContains
                    (TestLane.Lists.render (Some "m.json") m.Forced m.Lists)
                    "\n*\n"
                    "the lists file does not keep everything"

                Expect.stringContains
                    (String.concat "\n" (summaryLines m))
                    "msbuild-import: Directory.Build.props"
                    "the summary does not say which rule fired"
        ]

        testList "the registration and the modules a file defines" [
            testCase "entries are read at each list's item level; argument lines are not entries"
            <| fun () ->
                let regs = registrations programText

                Expect.equal
                    (regs |> List.map (fun r -> r.Text, r.Applied))
                    [
                        "AlphaTests.tests", false
                        "BetaTests.tests", false
                        "IWidgetContract.tests", true
                        "IWidgetContract.tests", true
                        "GammaTests.tests", false
                        "ToolUp.Platform.Tests.Composition.CompositionBaselineTests.tests", false
                        "PublicApiApprovalTests.tests", false
                    ]
                    "the registration was misread"

            testCase "a registration the formatter kept on one line is read item by item"
            <| fun () ->
                Expect.equal
                    (registrations "let private registeredTests =\n    testList \"P\" [ ATests.tests; BTests.more ]\n"
                     |> List.map _.Text)
                    [ "ATests.tests"; "BTests.more" ]
                    "an inline list was misread"

            testCase "top-level, namespaced and nested modules are qualified"
            <| fun () ->
                Expect.equal
                    (modulesDefinedIn
                        "namespace A.B\n\nmodule C =\n    let x = 1\n    module D =\n        let y = 2\nmodule E =\n    let z = 3\n")
                    [ "A.B.C"; "A.B.C.D"; "A.B.E" ]
                    "namespaced modules"

                Expect.equal
                    (modulesDefinedIn "module A.B.C\n\nmodule Inner =\n    ()\n")
                    [ "A.B.C"; "A.B.C.Inner" ]
                    "nested in a top-level module"

            testCase "the real family registers hundreds of entries, PublicApiApprovalTests in exactly one member"
            <| fun () ->
                let family = realFamily ()

                Expect.isGreaterThanOrEqual
                    family.Length
                    8
                    "the family is the residue, six part packs and the approval pack"

                let regs =
                    family
                    |> List.collect (fun (row, program) ->
                        registrations (File.ReadAllText(Path.Combine(GateReconcileTests.repoRoot, program)))
                        |> List.map (fun r -> row, r))

                Expect.isGreaterThan regs.Length 900 "the registration parser read almost nothing"

                Expect.equal
                    (regs
                     |> List.filter (fun (_, r) -> r.Text = "PublicApiApprovalTests.tests")
                     |> List.map fst)
                    [ "Platform.PublicApi" ]
                    "the always-on list is registered once, in the approval pack"

                Expect.exists regs (fun (_, r) -> r.Applied) "no applied (contract) entry was recognised"
        ]

        testList "the selection" [
            testCase "a diffed test file selects its lists by binding; the rest are skipped by name"
            <| fun () ->
                let m =
                    selectOver [
                        "src/ToolUp.KnowledgeBase.Server/Server/Notes.fs"
                        "src/ToolUp.Platform.Tests/InProcess/BetaTests.fs"
                    ] []

                Expect.isFalse m.Forced "forced"

                Expect.equal
                    (unitsFrom Source.Diffed m)
                    [ "Platform/ToolUp.Platform.Tests.InProcess.BetaTests.tests" ]
                    "the diffed list"

                Expect.equal
                    m.Skipped
                    [
                        "AlphaTests.tests"
                        "IWidgetContract.tests …"
                        "IWidgetContract.tests …"
                        "GammaTests.tests"
                        "ToolUp.Platform.Tests.Composition.CompositionBaselineTests.tests"
                    ]
                    "the skipped lists are not each named"

                Expect.containsAll
                    (unitsFrom Source.Always m)
                    [
                        "Platform/ToolUp.Platform.Tests.InProcess.PublicApiApprovalTests.tests"
                        "Platform/" + RegistrationGuard
                        "Forms/*"
                        "Voice/*"
                        "leg/Analyzers"
                    ]
                    "the always-on set"

                Expect.isFalse m.ClientTierMoved "a server-only diff moved the client tier"

            testCase "declared lists are units as given; a label is resolved by the pack, not here"
            <| fun () ->
                let m =
                    selectOver [ "src/ToolUp.KnowledgeBase.Server/Server/Notes.fs" ] [
                        "Platform/ToolUp.Platform.Tests.InProcess.GammaTests.tests"
                        "AlphaTests.tests"
                        "Forms/Some list"
                        "A label"
                    ]

                Expect.equal
                    (unitsFrom Source.Declared m)
                    [
                        "Platform/ToolUp.Platform.Tests.InProcess.GammaTests.tests"
                        "Platform/AlphaTests.tests"
                        "Forms/Some list"
                        "Platform/A label"
                    ]
                    "the declared units"

                Expect.equal m.UnresolvedDeclared [ "A label" ] "only the label is unresolved here"
                Expect.isFalse (List.contains "GammaTests.tests" m.Skipped) "a declared binding was reported skipped"

                Expect.isFalse
                    (List.contains "AlphaTests.tests" m.Skipped)
                    "a declared short binding was reported skipped"

                Expect.equal
                    m.Lists
                    [
                        "ToolUp.Platform.Tests.InProcess.GammaTests.tests"
                        "AlphaTests.tests"
                        "A label"
                        "ToolUp.Platform.Tests.InProcess.PublicApiApprovalTests.tests"
                    ]
                    "the lists file names the Platform declarations and the always-on list, and nothing for another pack"

            testCase "a test file whose lists cannot be named forces the full lane"
            <| fun () ->
                let helper =
                    selectOver [ "src/ToolUp.Platform.Tests/InProcess/SharedFixtures.fs" ] []

                let contract =
                    selectOver [ "src/ToolUp.Platform.Tests/Contracts/IWidgetContract.fs" ] []

                for m, path in
                    [
                        helper, "src/ToolUp.Platform.Tests/InProcess/SharedFixtures.fs"
                        contract, "src/ToolUp.Platform.Tests/Contracts/IWidgetContract.fs"
                    ] do
                    Expect.equal m.Fired [ { Rule = UnpinnedRule; Path = path } ] (sprintf "%s did not force" path)

            testCase "a client-tier path runs the Fable leg and the sample transpiles"
            <| fun () ->
                Expect.isTrue (selectOver [ "src/ToolUp.Platform.Client/Client/Shell.fs" ] []).ClientTierMoved "client"
                Expect.isTrue (selectOver [ "samples/HelloWorld/x.fs" ] []).ClientTierMoved "sample"
                Expect.isTrue (selectOver [ "src/ToolUp.AI.Wire.Fable.Tests/x.fs" ] []).ClientTierMoved "fable tests"
                Expect.isFalse (selectOver [ "src/ToolUp.Forms.Server/x.fs"; "docs/a.md" ] []).ClientTierMoved "server"

            testCase "the manifest carries map: none and reads back to itself"
            <| fun () ->
                let m =
                    selectOver [ "src/ToolUp.Platform.Tests/InProcess/BetaTests.fs" ] [
                        "A label"
                        "Platform/AlphaTests.tests"
                    ]

                let json = toJson m
                Expect.stringContains json "\"map\": \"none\"" "map"
                Expect.stringContains json "\"derived\": []" "derived"
                Expect.stringContains json "\"kind\": \"selectionManifest\"" "kind"
                Expect.stringContains json "\"skippedExact\": false" "skippedExact"
                Expect.equal (ofJson json) (Ok m) "the manifest does not round-trip"

            testCase "acceptance: a KnowledgeBase server file and its test file, over the real tree"
            <| fun () ->
                let root = GateReconcileTests.repoRoot

                let read (p: string) =
                    let full = Path.Combine(root, p)

                    if File.Exists full then
                        Some(File.ReadAllText full)
                    else
                        None

                let family = realFamily ()

                let over paths declared =
                    select {
                        Base = None
                        Head = None
                        Paths = paths
                        Declared = declared
                        Packs = (family |> List.map fst) @ [ "Forms" ]
                        Family = family
                        Graph = None
                        Read = read
                        ListsFile = "selection.lists"
                        Command = "Select --paths p"
                    }

                let testFile = "src/ToolUp.Platform.Tests/AI/IngestionStatusStreamTests.fs"
                let binding = "ToolUp.Platform.Tests.AI.IngestionStatusStreamTests.tests"

                let m =
                    over [ "src/ToolUp.KnowledgeBase.Server/Server/IngestionStatusStream.fs"; testFile ] [ binding ]

                Expect.isFalse m.Forced (sprintf "forced: %A" m.Fired)
                // A unit carries one source, the first that admits it: declared.
                Expect.equal (unitsFrom Source.Declared m) [ "Platform/" + binding ] "the declared list"
                Expect.isEmpty (unitsFrom Source.Diffed m) "a declared unit was listed twice"

                Expect.equal
                    (unitsFrom Source.Diffed (over [ testFile ] []))
                    [ "Platform/" + binding ]
                    "undeclared, the test file's list is diffed"

                Expect.containsAll
                    (unitsFrom Source.Always m)
                    [
                        "Platform.PublicApi/ToolUp.Platform.Tests.InProcess.PublicApiApprovalTests.tests"
                        "Platform.Audit/" + RegistrationGuard
                        "Forms/*"
                    ]
                    "the always-on list in its own pack, every member's guard, the other packs whole"

                Expect.isEmpty m.UnresolvedDeclared "a declared binding was unresolved"
                Expect.isGreaterThan m.Skipped.Length 900 "the skipped lists are not named"

                Expect.isFalse
                    (List.contains "IngestionStatusStreamTests.tests" m.Skipped)
                    "the selected list is skipped"

                Expect.isFalse m.ClientTierMoved "a server change ran the Fable leg"
                Expect.stringContains (toJson m) "\"map\": \"none\"" "map"

                let full = over [ testFile; "Directory.Build.props" ] [ binding ]
                Expect.isTrue full.Forced "Directory.Build.props did not force the full lane"
                Expect.exists full.Fired (fun f -> f.Rule = "msbuild-import") "the rule is not named"
        ]

        testList "the graph source — whole packs by the project graph" [
            testCase "a source compiled from the umbrella's folder belongs to the part that compiles it"
            <| fun () ->
                let graph =
                    projectGraph (fun p -> Map.tryFind p graphFiles) (graphPacks |> List.map snd)

                Expect.equal
                    (ownersOf graph "src/Umbrella/Server/B1.fs")
                    (set [ "src/PartB/PartB.fsproj" ])
                    "an item outranks the folder it sits in"

                Expect.equal
                    (ownersOf graph "src/Umbrella/README.md")
                    (set [ "src/Umbrella/Umbrella.fsproj" ])
                    "a file no item names is its folder's"

                Expect.equal
                    (ownersOf graph "src/Tests/InProcess/ATests.fs")
                    (set [ "src/Tests.A/Tests.A.fsproj" ])
                    "a test file is the pack's that compiles it, not the residue folder's"

                Expect.isEmpty (ownersOf graph "docs/x.md") "a path outside every project is owned by none"

                Expect.equal
                    (closureOf graph "src/Tests.B/Tests.B.fsproj")
                    (set [
                        "src/Tests.B/Tests.B.fsproj"
                        "src/PartB/PartB.fsproj"
                        "src/PartA/PartA.fsproj"
                        "src/Core/Core.fsproj"
                    ])
                    "the closure is the pack and everything it references"

            testCase "a diff confined to one part selects that part's pack and the packs above it, and skips the rest"
            <| fun () ->
                let m = graphOver [ "src/Umbrella/Server/B1.fs" ]
                Expect.isFalse m.Forced "forced"
                Expect.equal (selectedPacks m) [ "PB"; "R" ] "part B reaches its pack and the residue over the umbrella"
                Expect.equal (skippedPacks m) [ "PA"; "Other" ] "the pack below it and the unrelated pack run nothing"
                Expect.equal (unitsFrom Source.Graph m) [ "PB/*"; "R/*" ] "the selected packs run whole"
                Expect.equal m.Skipped [ "PA/*"; "Other/*" ] "the skipped packs are named"

                Expect.equal
                    m.Graph
                    (Some {
                        Selected = [ "PB", Some 7; "R", None ]
                        Skipped = [ "PA", None; "Other", None ]
                        Unowned = []
                    })
                    "the recorded case count rides beside each pack"

                let earlier = graphOver [ "src/Umbrella/Server/A1.fs" ]
                Expect.equal (selectedPacks earlier) [ "PA"; "PB"; "R" ] "part A is in every pack built over it"

            testCase "a test file selects only the pack that compiles it; an unowned path selects nothing"
            <| fun () ->
                let m = graphOver [ "src/Tests/InProcess/ATests.fs"; "docs/x.md" ]
                Expect.equal (selectedPacks m) [ "PA" ] "the test file's pack"
                Expect.equal (m.Graph |> Option.map _.Unowned) (Some [ "docs/x.md" ]) "the unowned path is named"

                Expect.stringContains
                    (String.concat "\n" (summaryLines m))
                    "unowned  docs/x.md"
                    "the summary does not name the unowned path"

            testCase "only the graph-blind forcing rules apply; the graph places the composition root itself"
            <| fun () ->
                Expect.isTrue (graphOver [ "Directory.Build.props" ]).Forced "an import did not force"
                let root = graphOver [ "src/ToolUp.Platform.Server/Server/ServerApp.fs" ]

                Expect.isEmpty
                    (rulesFiring "src/ToolUp.Platform.Server/Server/ServerApp.fs"
                     |> List.filter graphBlindRules.Contains)
                    "a blind rule"

                Expect.isFalse root.Forced "the composition root forced a graph selection"

            testCase "a draft note is not an import: appending one forces nothing and the graph selects"
            <| fun () ->
                let code = graphOver [ "src/Umbrella/Server/B1.fs" ]
                let noted = graphOver [ "src/Umbrella/Server/B1.fs"; "DRAFT-NOTES.md" ]
                Expect.isFalse noted.Forced (sprintf "a draft note forced the full lane: %A" noted.Fired)
                Expect.isEmpty noted.Fired "a forcing rule fired on the draft note"
                Expect.equal (selectedPacks noted) (selectedPacks code) "the note changed what the graph selects"
                Expect.equal (skippedPacks noted) (skippedPacks code) "the note changed what the graph skips"

                Expect.equal
                    (noted.Graph |> Option.map _.Unowned)
                    (Some [ "DRAFT-NOTES.md" ])
                    "the note is an unowned path, which selects nothing"

                Expect.isTrue
                    (graphOver [ "src/Umbrella/Server/B1.fs"; "Directory.Build.props" ]).Forced
                    "falsifier: the same change with its note in the props forces the full lane"

            testCase "the graph manifest says its source and reads back to itself"
            <| fun () ->
                let m = graphOver [ "src/Umbrella/Server/B1.fs"; "docs/x.md" ]
                let json = toJson m
                Expect.stringContains json "\"source\": \"graph\"" "source"
                Expect.stringContains json "\"map\": \"none\"" "map"
                Expect.equal (ofJson json) (Ok m) "the manifest does not round-trip"

            testCase
                "acceptance: over the real tree, an Audit-part diff selects the Audit pack and skips the earlier parts' packs"
            <| fun () ->
                let over paths =
                    select {
                        Base = None
                        Head = None
                        Paths = paths
                        Declared = []
                        Packs = realPackProjects () |> List.map fst
                        Family = realFamily ()
                        Graph =
                            Some {
                                PackProjects = realPackProjects ()
                                Cases = Map.empty
                            }
                        Read = realRead
                        ListsFile = "selection.lists"
                        Command = "Select --source graph"
                    }

                let parts = [ "Remoting"; "Infra"; "Config"; "Scope"; "Jobs"; "Audit" ]
                let pack (part: string) = "Platform." + part

                // One source per part, as its project compiles it.
                for i, path in
                    [
                        0, "src/ToolUp.Platform.Server/Server/Remoting/MsgPack/TypeShape.fs"
                        4, "src/ToolUp.Platform.Server/Server/SseTraceContributor.fs"
                        5, "src/ToolUp.Platform.Server/Server/AuditFallbackStore.fs"
                    ] do
                    let m = over [ path ]
                    Expect.isFalse m.Forced (sprintf "%s forced: %A" path m.Fired)
                    let selected = selectedPacks m

                    for j, part in List.indexed parts do
                        if j < i then
                            Expect.contains
                                (skippedPacks m)
                                (pack part)
                                (sprintf "%s: %s should be skipped" path (pack part))
                        else
                            Expect.contains selected (pack part) (sprintf "%s: %s should be selected" path (pack part))

                    Expect.contains selected PlatformPack (sprintf "%s: the residue is built over every part" path)

                    Expect.contains
                        (unitsFrom Source.Always m)
                        "Platform.PublicApi/ToolUp.Platform.Tests.InProcess.PublicApiApprovalTests.tests"
                        "the approval gate is always on"

                // The composition root is the umbrella's: the residue, never a part's pack.
                let root = over [ "src/ToolUp.Platform.Server/Server/ServerApp.fs" ]
                Expect.isFalse root.Forced "the composition root forced a graph selection"
                Expect.contains (selectedPacks root) PlatformPack "the residue"

                for part in parts do
                    Expect.contains (skippedPacks root) (pack part) (sprintf "ServerApp.fs selected %s" (pack part))
        ]

        testList "the keep — go-red proofs" [
            testCase "a failing case in a list NOT selected does not fail the lane, and is named as skipped"
            <| fun () ->
                match runLane [ "Healthy" ] with
                | Ok(keep, failing) ->
                    Expect.isEmpty failing "the unselected failing list ran"

                    Expect.equal
                        (keep.Skipped |> List.choose TestLaneFilter.labelOf)
                        [ "Injected"; "Group" ]
                        "the skipped lists are not named"
                | Error why -> failtest why

            testCase "the same case in a declared list fails the lane"
            <| fun () ->
                match runLane [ "Healthy"; "Injected" ] with
                | Ok(_, failing) ->
                    Expect.equal
                        failing
                        [ "Synthetic.Tests/Injected/fails on purpose" ]
                        "the selected failure did not fail"
                | Error why -> failtest why

            testCase "a declared name matching no list is red, never an empty run"
            <| fun () ->
                match runLane [ "Healthy"; "No such list" ] with
                | Ok _ -> failtest "an unmatched name was accepted"
                | Error why -> Expect.stringContains why "No such list" "the unmatched name is not named"

            testCase "a lists file naming nothing is refused by its reader"
            <| fun () ->
                let path = Path.GetTempFileName()

                try
                    File.WriteAllText(path, "# only a comment\n")
                    let before = System.Environment.GetEnvironmentVariable TestLane.ListsVariableName
                    System.Environment.SetEnvironmentVariable(TestLane.ListsVariableName, path)

                    try
                        match TestLane.Lists.read () with
                        | Ok _ -> failtest "an empty lists file was accepted"
                        | Error why -> Expect.stringContains why "names no list" "the refusal does not say why"
                    finally
                        System.Environment.SetEnvironmentVariable(TestLane.ListsVariableName, before)
                finally
                    File.Delete path

            testCase "a binding nested in a sequenced group keeps the group"
            <| fun () ->
                match runLane [ "Synthetic.Tests.Inner.grouped" ] with
                | Ok(keep, failing) ->
                    Expect.isEmpty failing "a failure ran"

                    Expect.equal
                        (keep.Kept |> List.choose (snd >> TestLaneFilter.labelOf))
                        [ "Group" ]
                        "the group was not kept"
                | Error why -> failtest why

            testCase "`*` keeps every list, so a forced selection runs whole"
            <| fun () ->
                match runLane [ "*" ] with
                | Ok(keep, failing) ->
                    Expect.isEmpty keep.Skipped "a list was skipped"
                    Expect.equal failing [ "Synthetic.Tests/Injected/fails on purpose" ] "the failing list did not run"
                | Error why -> failtest why

            testCase "the binding resolver reads a real binding by its full and its short name"
            <| fun () ->
                let resolve =
                    TestLaneFilter.bindingResolver (System.Reflection.Assembly.GetExecutingAssembly())

                let expected = PureLaneTests.tests

                for name in [ "ToolUp.Platform.Build.Tests.PureLaneTests.tests"; "PureLaneTests.tests" ] do
                    match resolve name with
                    | Some t ->
                        Expect.isTrue
                            (obj.ReferenceEquals(t, expected))
                            (sprintf "%s is not the registered object" name)
                    | None -> failtestf "%s did not resolve" name

                Expect.isNone
                    (resolve "ToolUp.Platform.Build.Tests.PureLaneTests.noSuchBinding")
                    "a missing member resolved"
        ]
    ]