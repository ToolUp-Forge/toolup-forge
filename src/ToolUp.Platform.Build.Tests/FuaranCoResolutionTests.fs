// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The Fuaran co-resolution rule, proved offline in both directions.
///
/// The fixtures reconstruct the 0.35.0 release: the fact browse's view slice pinned
/// `Fuaran.Core.Column` 0.40.2 and `Fuaran.Compute.DataFrame` 0.42.0 while every
/// published Fuaran.UI (0.97.0 and older) was built on Core 0.38.1, and a consumer
/// composing both failed with FS0193. The 0.97.0 and 0.98.1 floors below are the
/// published nuspecs' own. Every passing case has a twin that goes red, because a
/// rule that returns no refusals at all satisfies every green case on its own.
module ToolUp.Platform.Build.Tests.FuaranCoResolutionTests

open System
open System.IO
open Expecto
open ToolUp.Forge.FuaranCoResolution

let private coreIds = [
    "Fuaran.Core.Column"
    "Fuaran.Core.Ops"
    "Fuaran.Core.Propagation"
    "Fuaran.Core.Tree"
    "Fuaran.Core.Unit"
    "Fuaran.Core.Validator"
    "Fuaran.Core.Wire"
]

let private floors (core: string) (dataFrame: string) =
    (coreIds |> List.map (fun id -> id, core))
    @ [ "Fuaran.Compute.DataFrame", dataFrame ]
    |> Map.ofList

/// A Fuaran.UI release whose tier packages all declare the same floors, as the
/// published ones do.
let private uiRelease (release: string) (core: string) (dataFrame: string) : UiSnapshot = {
    Release = release
    Packages =
        [ "Fuaran.UI"; "Fuaran.UI.Renderer"; "Fuaran.UI.Renderer.Server" ]
        |> List.map (fun p -> p, floors core dataFrame)
        |> Map.ofList
}

let private ui097 = uiRelease "0.97.0" "0.38.1" "0.40.0"
let private ui0981 = uiRelease "0.98.1" "0.40.2" "0.41.0"

/// The pins the 0.35.0 release packed.
let private forge035 = [
    {
        Id = "Fuaran.Core.Column"
        Version = "0.40.2"
    }
    {
        Id = "Fuaran.Compute.DataFrame"
        Version = "0.42.0"
    }
]

let private openLead family forge release ui = {
    Family = family
    ForgeFloor = forge
    UiRelease = release
    UiFloor = ui
    ClosedBy = None
    BrokenShapes = "a consumer composing the fact tier beside Fuaran-UI pages"
}

let private refusalsOf pins snapshot leads =
    evaluate pins snapshot leads |> refusals

let private family (v: Verdict) =
    match v with
    | Aligned(f, _, _)
    | AheadCompatible(f, _, _)
    | AheadBreaking(f, _, _, _, _)
    | AheadDeclared(f, _, _, _)
    | Behind(f, _, _) -> Some f
    | LeadNotNeeded(l, _) -> Some l.Family
    | SnapshotBehindPin _
    | Unreadable _ -> None

let private verdictFor fam verdicts =
    verdicts
    |> List.filter (fun v -> family v = Some fam)
    |> function
        | [ v ] -> v
        | other -> failtestf "expected one verdict for %s, got %A" fam other

let private fixtureRoot =
    Path.Combine(AppContext.BaseDirectory, "fixtures", "fuaran")

/// The 0.97.0 nuspec's dependency block, as nuget.org serves it.
let private nuspec097 =
    """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Fuaran.UI</id>
    <version>0.97.0</version>
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="FSharp.Core" version="10.1.300" exclude="Build,Analyzers" />
        <dependency id="Fuaran.Compute.DataFrame" version="0.40.0" exclude="Build,Analyzers" />
        <dependency id="Fuaran.Core.Column" version="0.38.1" exclude="Build,Analyzers" />
        <dependency id="Fuaran.Core.Wire" version="[0.38.1, )" exclude="Build,Analyzers" />
        <dependency id="Fable.Core" version="5.0.0" exclude="Build,Analyzers" />
      </group>
    </dependencies>
  </metadata>
</package>"""

let private leadTable (rows: string list) =
    String.concat "\n" [
        "# Doc"
        ""
        LeadsHeading
        ""
        "Prose before the table."
        ""
        "| Family | Floor here | Fuaran.UI release | Its floor | Status | Broken until closed |"
        "|---|---|---|---|---|---|"
        yield! rows
        ""
        "## Next section"
        "| not | a | lead |"
    ]

[<Tests>]
let tests =
    testList "FuaranCoResolution" [

        testList "the 0.35.0 state, pinned red" [
            testCase "Core 0.40.2 against a published tier built on Core 0.38.1 is refused, naming both" (fun () ->
                let verdicts = evaluate forge035 ui097 []

                match verdictFor "Fuaran.Core" verdicts with
                | AheadBreaking(_, forge, ui, release, ids) as v ->
                    Expect.equal (forge, ui, release) ("0.40.2", "0.38.1", "0.97.0") "both floors and the release"
                    Expect.contains ids "Fuaran.Core.Column" "the pinned id is named"
                    let line = describe v
                    Expect.stringContains line "0.40.2" "the message names this repository's floor"
                    Expect.stringContains line "0.38.1" "the message names the UI tier's floor"
                    Expect.stringContains line "FS0193" "the message names the failure class"
                    Expect.equal (severity v) Refusal "a refusal"
                | other -> failtestf "expected AheadBreaking, got %A" other)

            testCase "the DataFrame floor of that release is refused too" (fun () ->
                match verdictFor "Fuaran.Compute" (evaluate forge035 ui097 []) with
                | AheadBreaking(_, "0.42.0", "0.40.0", _, _) -> ()
                | other -> failtestf "expected AheadBreaking 0.42.0 / 0.40.0, got %A" other)

            testCase "the same pins against Fuaran.UI 0.98.1 leave Core aligned" (fun () ->
                match verdictFor "Fuaran.Core" (evaluate forge035 ui0981 []) with
                | Aligned(_, "0.40.2", "0.40.2") -> ()
                | other -> failtestf "expected Aligned, got %A" other)

            testCase "a lead declared for that floor lets the 0.35.0 pins pass, and prints its shapes" (fun () ->
                let leads = [
                    openLead "Fuaran.Core" "0.40.2" "0.97.0" "0.38.1"
                    openLead "Fuaran.Compute" "0.42.0" "0.97.0" "0.40.0"
                ]

                let verdicts = evaluate forge035 ui097 leads
                Expect.isEmpty (refusals verdicts) "both families are declared"

                match verdictFor "Fuaran.Core" verdicts with
                | AheadDeclared(_, _, _, lead) as v ->
                    Expect.stringContains (describe v) lead.BrokenShapes "the shapes are printed"
                | other -> failtestf "expected AheadDeclared, got %A" other)
        ]

        testList "leads" [
            testCase "a closed lead excuses nothing" (fun () ->
                let closed = {
                    openLead "Fuaran.Core" "0.40.2" "0.97.0" "0.38.1" with
                        ClosedBy = Some "0.98.1"
                }

                let refused =
                    refusalsOf forge035 ui097 [ closed; openLead "Fuaran.Compute" "0.42.0" "0.97.0" "0.40.0" ]

                Expect.exists
                    refused
                    (function
                    | AheadBreaking("Fuaran.Core", _, _, _, _) -> true
                    | _ -> false)
                    "Core is refused under a closed lead")

            testCase "an open lead the UI tier has caught up with is refused as not needed" (fun () ->
                let refused =
                    refusalsOf forge035 ui0981 [
                        openLead "Fuaran.Core" "0.40.2" "0.97.0" "0.38.1"
                        openLead "Fuaran.Compute" "0.42.0" "0.98.1" "0.41.0"
                    ]

                match refused with
                | [ LeadNotNeeded(lead, reason) ] ->
                    Expect.equal lead.Family "Fuaran.Core" "the Core lead"
                    Expect.stringContains reason "0.98.1" "names the closing release"
                | other -> failtestf "expected one LeadNotNeeded, got %A" other)

            testCase "a lead for another floor does not excuse a moved floor, and is itself stale" (fun () ->
                let refused =
                    refusalsOf forge035 ui097 [
                        openLead "Fuaran.Core" "0.40.1" "0.97.0" "0.38.1"
                        openLead "Fuaran.Compute" "0.42.0" "0.97.0" "0.40.0"
                    ]

                Expect.exists
                    refused
                    (function
                    | AheadBreaking("Fuaran.Core", "0.40.2", _, _, _) -> true
                    | _ -> false)
                    "the moved floor is refused"

                Expect.exists
                    refused
                    (function
                    | LeadNotNeeded(l, r) -> l.ForgeFloor = "0.40.1" && r.Contains "0.40.2"
                    | _ -> false)
                    "the old lead is named stale")
        ]

        testList "the comparison" [
            testCase "ahead by a patch below 1.0 is a warning, not a refusal" (fun () ->
                let pins = [
                    {
                        Id = "Fuaran.Core.Column"
                        Version = "0.40.3"
                    }
                ]

                let v = verdictFor "Fuaran.Core" (evaluate pins ui0981 [])
                Expect.equal v (AheadCompatible("Fuaran.Core", "0.40.3", "0.40.2")) "patch ahead"
                Expect.equal (severity v) Warning "a warning")

            testCase "from 1.0 a minor ahead is compatible and a major ahead breaks" (fun () ->
                let ui = uiRelease "1.0.0" "1.2.0" "1.0.0"

                let at v = [
                    {
                        Id = "Fuaran.Core.Column"
                        Version = v
                    }
                ]

                Expect.equal
                    (severity (verdictFor "Fuaran.Core" (evaluate (at "1.3.0") ui [])))
                    Warning
                    "minor ahead from 1.0"

                Expect.equal
                    (severity (verdictFor "Fuaran.Core" (evaluate (at "2.0.0") ui [])))
                    Refusal
                    "major ahead from 1.0")

            testCase "the UI tier ahead across a minor is a warning naming both" (fun () ->
                let pins = [
                    {
                        Id = "Fuaran.Core.Column"
                        Version = "0.40.2"
                    }
                ]

                let v =
                    verdictFor "Fuaran.Core" (evaluate pins (uiRelease "1.0.0" "0.41.0" "0.42.0") [])

                Expect.equal v (Behind("Fuaran.Core", "0.40.2", "0.41.0")) "behind"
                Expect.equal (severity v) Warning "a warning")

            testCase "the UI floor is the LOWEST any tier package declares" (fun () ->
                let mixed = {
                    ui0981 with
                        Packages = ui0981.Packages |> Map.add "Fuaran.UI.Renderer" (floors "0.38.1" "0.41.0")
                }

                match verdictFor "Fuaran.Core" (evaluate forge035 mixed []) with
                | AheadBreaking(_, "0.40.2", "0.38.1", _, _) -> ()
                | other -> failtestf "expected the renderer's 0.38.1 to decide, got %A" other)

            testCase "a family the UI tier does not depend on is out of scope" (fun () ->
                let pins = [
                    {
                        Id = "Fuaran.Data.Frames"
                        Version = "9.0.0"
                    }
                ]

                Expect.isEmpty (evaluate pins ui0981 []) "no verdict")

            testCase "a pinned Fuaran.UI newer than the snapshot is refused; an older one is not" (fun () ->
                let pin v = [ { Id = "Fuaran.UI"; Version = v } ]

                match refusalsOf (pin "0.99.1") ui0981 [] with
                | [ SnapshotBehindPin("Fuaran.UI", "0.99.1", "0.98.1") ] -> ()
                | other -> failtestf "expected SnapshotBehindPin, got %A" other

                Expect.isEmpty (evaluate (pin "0.97.0") ui0981 []) "an older pin needs nothing")

            testCase "an unreadable version is a refusal, never a skip" (fun () ->
                let pins = [
                    {
                        Id = "Fuaran.Core.Column"
                        Version = "latest"
                    }
                ]

                match refusalsOf pins ui0981 [] with
                | [ Unreadable(_, "latest") ] -> ()
                | other -> failtestf "expected Unreadable, got %A" other)
        ]

        testList "inputs" [
            testCase "pins resolve a property per producing repository; other ids are ignored" (fun () ->
                let props =
                    """<Project>
  <PropertyGroup><FuaranCoreVersion>0.40.2</FuaranCoreVersion><FuaranUIVersion>0.98.1</FuaranUIVersion></PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Expecto" Version="10.2.3" />
    <PackageVersion Include="Fuaran.Core.Column" Version="$(FuaranCoreVersion)" />
    <PackageVersion Include="Fuaran.UI" Version="$(FuaranUIVersion)" />
    <PackageVersion Include="Fuaran.Compute.DataFrame" Version="0.42.0" />
  </ItemGroup>
</Project>"""

                let expected = [
                    {
                        Id = "Fuaran.Core.Column"
                        Version = "0.40.2"
                    }
                    { Id = "Fuaran.UI"; Version = "0.98.1" }
                    {
                        Id = "Fuaran.Compute.DataFrame"
                        Version = "0.42.0"
                    }
                ]

                Expect.equal (pinsOfPackagesProps props) (Ok expected) "three Fuaran pins")

            testCase "a pin naming an undefined property is an error, not a skipped pin" (fun () ->
                let props =
                    """<Project><ItemGroup><PackageVersion Include="Fuaran.Core.Column" Version="$(Missing)" /></ItemGroup></Project>"""

                match pinsOfPackagesProps props with
                | Error [ e ] -> Expect.stringContains e "Missing" "names the property"
                | other -> failtestf "expected one error, got %A" other)

            testCase "a nuspec yields its Fuaran floors only, a range by its floor" (fun () ->
                Expect.equal
                    (floorsOfNuspec nuspec097)
                    (Map.ofList [
                        "Fuaran.Compute.DataFrame", "0.40.0"
                        "Fuaran.Core.Column", "0.38.1"
                        "Fuaran.Core.Wire", "0.38.1"
                    ])
                    "Fuaran ids only")

            testCase "the newest release skips prereleases; an unpublished release is refused" (fun () ->
                let published = [ "0.97.0"; "0.98.1"; "1.0.0-rc.1"; "0.99.1" ]
                Expect.equal (newestRelease published) (Some "0.99.1") "newest release"
                Expect.equal (chooseRelease None published) (Ok "0.99.1") "default is the newest"
                Expect.equal (chooseRelease (Some "0.98.1") published) (Ok "0.98.1") "a published one"

                match chooseRelease (Some "0.99.2") published with
                | Error e -> Expect.stringContains e "not published" "refused by name"
                | Ok v -> failtestf "an unpublished release was accepted: %s" v)

            testCase "the snapshot round-trips through its committed form" (fun () ->
                let json = snapshotToJson ui0981 "https://api.nuget.org/v3-flatcontainer/"
                Expect.equal (snapshotOfJson json) (Ok ui0981) "round trip"
                Expect.equal (snapshotToJson ui0981 "https://api.nuget.org/v3-flatcontainer/") json "stable bytes")

            testCase "a snapshot with no tier package is refused" (fun () ->
                Expect.isError (snapshotOfJson """{"release":"0.98.1","packages":{}}""") "empty")

            testCase "the lead table parses open and closed rows, and stops at the next heading" (fun () ->
                let text =
                    leadTable [
                        "| `Fuaran.Core` | `0.40.2` | `0.97.0` | `0.38.1` | closed by Fuaran.UI 0.98.1 | FS0193 |"
                        "| Fuaran.Compute | 0.42.0 | 0.99.1 | 0.41.0 | open | none observed |"
                    ]

                match leadsOfMarkdown text with
                | Ok [ core; compute ] ->
                    Expect.equal core.ClosedBy (Some "0.98.1") "closed"
                    Expect.equal (core.Family, core.ForgeFloor) ("Fuaran.Core", "0.40.2") "backticks stripped"
                    Expect.equal compute.ClosedBy None "open"
                    Expect.equal compute.BrokenShapes "none observed" "the shapes"
                | other -> failtestf "expected two leads, got %A" other)

            testCase "a missing section or an unknown status is an error" (fun () ->
                Expect.isError (leadsOfMarkdown "# No leads here") "missing section"

                Expect.isError
                    (leadsOfMarkdown (leadTable [ "| Fuaran.Core | 0.40.2 | 0.97.0 | 0.38.1 | pending | x |" ]))
                    "unknown status")
        ]

        testList "the committed inputs" [
            testCase "the snapshot, the props and the lead table parse" (fun () ->
                let snapshot =
                    File.ReadAllText(Path.Combine(fixtureRoot, "shipped-floors.json"))
                    |> snapshotOfJson

                match snapshot with
                | Ok s ->
                    for pkg in uiTierPackages do
                        Expect.isTrue (s.Packages.ContainsKey pkg) (sprintf "the snapshot records %s" pkg)
                | Error e -> failtest e

                Expect.isOk
                    (File.ReadAllText(Path.Combine(fixtureRoot, "Directory.Packages.props"))
                     |> pinsOfPackagesProps)
                    "the props resolve")

            testCase "the lead table records the worked example, closed by Fuaran.UI 0.98.1" (fun () ->
                match
                    File.ReadAllText(Path.Combine(fixtureRoot, "open-core-boundary.md"))
                    |> leadsOfMarkdown
                with
                | Ok leads ->
                    Expect.exists
                        leads
                        (fun l ->
                            l.Family = "Fuaran.Core"
                            && l.ForgeFloor = "0.40.2"
                            && l.ClosedBy = Some "0.98.1")
                        "the Core lead as it stood"
                | Error e -> failtest e)
        ]

        testList "the co-build probe's scaffold" [
            testCase "the feed is the probe's own packs plus nuget.org, nothing inherited" (fun () ->
                let config = CoBuild.nugetConfig (Some "C:/feed")
                Expect.stringContains config "<clear />" "inherited sources are cleared"
                Expect.stringContains config "C:/feed" "own packs"
                Expect.stringContains config "https://api.nuget.org/v3/index.json" "nuget.org"
                Expect.isFalse ((CoBuild.nugetConfig None).Contains "own-packs") "no own feed when not packed")

            testCase "each half pins the versions it is given and refuses a silent downgrade" (fun () ->
                let server = CoBuild.serverProject "0.0.0-cobuild" "0.97.0"

                for id in CoBuild.serverPackages do
                    Expect.stringContains server (sprintf "Include=\"%s\"" id) id

                Expect.stringContains server "Version=\"0.97.0\"" "the UI version"
                Expect.stringContains server "Version=\"0.0.0-cobuild\"" "the ToolUp version"
                Expect.stringContains server "NU1605" "a downgrade is an error"

                let client = CoBuild.clientProject "0.35.0" "0.98.1"

                for id in CoBuild.clientPackages do
                    Expect.stringContains client (sprintf "Include=\"%s\"" id) id)

            testCase "the FS0193 class is recognised, and an ordinary error is not" (fun () ->
                let fs0193 =
                    "error FS0193: The module/namespace 'Fuaran.Core' from compilation unit 'Fuaran.Core.Column' did not contain the namespace, module or type 'Row'"

                Expect.isTrue (CoBuild.isSubstrateMismatch fs0193) "FS0193"

                Expect.isTrue
                    (CoBuild.isSubstrateMismatch
                        @".\output\fable_modules\Fuaran.UI.0.97.0\Generated.fs(2048,27): (2048,32) error FSHARP: The value, namespace, type or module 'Canon' is not defined.")
                    "a published Fuaran package's own source failing under Fable"

                Expect.isTrue
                    (CoBuild.isSubstrateMismatch
                        "Unhandled exception. System.MissingMethodException: Method not found: 'System.String Fuaran.Core.Wire.Canon.render(JVal)'.")
                    "a member missing at run time"

                Expect.isFalse
                    (CoBuild.isSubstrateMismatch @".\output\fable_modules\Feliz.2.9.0\Html.fs(1,1): error FSHARP: x")
                    "an error in a non-Fuaran package is not this class"

                Expect.isFalse (CoBuild.isSubstrateMismatch "error FS0039: The value 'x' is not defined") "FS0039")
        ]
    ]