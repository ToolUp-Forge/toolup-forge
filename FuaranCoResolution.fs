// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Whether this repository's Fuaran substrate floors resolve beside the newest
/// PUBLISHED Fuaran.UI release.
///
/// **The defect this decides.** The ToolUp packages depend on published Fuaran
/// substrate packages (`Fuaran.Core.*`, `Fuaran.Compute.*`). So does the Fuaran-UI
/// tier a consumer renders its pages with. NuGet resolves one version of each id
/// for the whole graph, the highest floor any package declares, so when this
/// repository's floor on a substrate package is AHEAD of the floor the published
/// UI tier was built against, every consumer composing both gets the newer
/// substrate underneath a UI tier compiled against the older one. Pre-1.0, a
/// minor release of the substrate may remove what the UI tier calls, and the
/// consumer fails to build: on .NET with FS0193 (`… did not contain 'Row'`), and in
/// its Fable client, which compiles the UI tier's sources against the substrate it
/// resolved. This repository's own build can never see it, because it compiles its
/// Fuaran consumption only against itself.
///
/// **The rule.** Fuaran repositories release their packages in lockstep, so a floor
/// moves per producing repository (a FAMILY: the first two dotted segments of the
/// id, `Fuaran.Core.Column` -> `Fuaran.Core`). For each family this repository
/// pins, its floor is compared with the LOWEST floor any package of the snapshotted
/// UI release declares in that family (the one built against the oldest substrate):
///
/// - ahead across a breaking boundary (a minor below 1.0, a major from 1.0) is a
///   REFUSAL, unless an open lead is declared for exactly that family and floor;
/// - ahead within one compatibility range is a WARNING;
/// - the UI tier ahead across a breaking boundary is a WARNING (this repository was
///   compiled against the older substrate; the co-build probe is the arbiter);
/// - a family the UI tier does not depend on is out of scope.
///
/// Two more refusals keep the inputs honest: an open lead no verdict needed (the UI
/// tier caught up, or the floor it was declared for moved), and a pinned Fuaran.UI
/// newer than the snapshotted release (the snapshot is stale).
///
/// Pure and BCL-only, compiled into the build driver and source-linked into
/// `ToolUp.Platform.Build.Tests`, so the gate and its go-red proofs run one
/// implementation. The snapshot is read from a committed file and refreshed by a
/// named target; nothing here fetches.
module ToolUp.Forge.FuaranCoResolution

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq

/// A package version's numeric core and its prerelease label.
type SemVer = {
    Major: int
    Minor: int
    Patch: int
    /// The prerelease label without its `-`, or "" for a release.
    Label: string
}

module SemVer =

    /// `1.2.3`, `1.2` (patch 0) or `1.2.3-label`; anything else is None.
    let tryParse (text: string) : SemVer option =
        if String.IsNullOrWhiteSpace text then
            None
        else
            let text = text.Trim()

            let core, label =
                match text.IndexOf '-' with
                | -1 -> text, ""
                | i -> text.Substring(0, i), text.Substring(i + 1)

            let parts = core.Split '.'

            let num (s: string) =
                match Int32.TryParse s with
                | true, n when n >= 0 -> Some n
                | _ -> None

            match parts |> Array.map num with
            | [| Some a; Some b |] ->
                Some {
                    Major = a
                    Minor = b
                    Patch = 0
                    Label = label
                }
            | [| Some a; Some b; Some c |] ->
                Some {
                    Major = a
                    Minor = b
                    Patch = c
                    Label = label
                }
            | _ -> None

    let text (v: SemVer) =
        let core = sprintf "%d.%d.%d" v.Major v.Minor v.Patch
        if v.Label = "" then core else core + "-" + v.Label

    /// Numeric order; a prerelease sorts below the release of the same core.
    let compare (a: SemVer) (b: SemVer) : int =
        match Operators.compare (a.Major, a.Minor, a.Patch) (b.Major, b.Minor, b.Patch) with
        | 0 ->
            match a.Label, b.Label with
            | "", "" -> 0
            | "", _ -> 1
            | _, "" -> -1
            | x, y -> String.CompareOrdinal(x, y) |> sign
        | c -> c

    /// Whether moving from `older` to `newer` crosses a boundary SemVer allows to
    /// break: a major, or below 1.0 a minor.
    let crossesBreakingBoundary (newer: SemVer) (older: SemVer) : bool =
        newer.Major <> older.Major || (newer.Major = 0 && newer.Minor <> older.Minor)

/// The producing repository an id belongs to: its first two dotted segments, for
/// a `Fuaran.` id only.
let familyOf (id: string) : string option =
    let parts = id.Split '.'

    if
        parts.Length >= 2
        && String.Equals(parts[0], "Fuaran", StringComparison.OrdinalIgnoreCase)
    then
        Some(parts[0] + "." + parts[1])
    else
        None

/// The family of the UI tier itself — compared as a release, never as a substrate.
[<Literal>]
let UiFamily = "Fuaran.UI"

/// The Fuaran.UI tier packages the snapshot records: the node vocabulary, the chart
/// lowering this repository draws with, and the two renderers a consumer's pages
/// run on (browser and server).
let uiTierPackages = [
    "Fuaran.UI"
    "Fuaran.UI.Charts"
    "Fuaran.UI.Renderer"
    "Fuaran.UI.Renderer.Server"
]

/// One `Fuaran.*` version this repository pins.
type Pin = { Id: string; Version: string }

/// The newest published Fuaran.UI release, as the committed snapshot records it.
type UiSnapshot = {
    /// The Fuaran.UI release version every tier package below was read at.
    Release: string
    /// Tier package id -> its `Fuaran.*` dependency floors (id -> version).
    Packages: Map<string, Map<string, string>>
}

/// A deliberate lead, declared in `docs/platform/open-core-boundary.md`.
type Lead = {
    Family: string
    /// The floor this repository declares ahead.
    ForgeFloor: string
    /// The Fuaran.UI release it was declared against, and that release's floor.
    UiRelease: string
    UiFloor: string
    /// `None` while the lead is open; the closing Fuaran.UI release once closed.
    ClosedBy: string option
    /// The consumer shapes that break until it closes.
    BrokenShapes: string
}

/// The verdict vocabulary — one per compared family, plus the input checks.
type Verdict =
    /// At or behind the UI tier's floor, within one compatibility range.
    | Aligned of family: string * forge: string * ui: string
    /// Ahead within one compatibility range: resolves, compatible by SemVer.
    | AheadCompatible of family: string * forge: string * ui: string
    /// Ahead across a breaking boundary, with no lead declared.
    | AheadBreaking of family: string * forge: string * ui: string * release: string * ids: string list
    /// Ahead across a breaking boundary under an open declared lead.
    | AheadDeclared of family: string * forge: string * ui: string * lead: Lead
    /// The UI tier is ahead of this repository across a breaking boundary.
    | Behind of family: string * forge: string * ui: string
    /// An open lead no comparison needed.
    | LeadNotNeeded of lead: Lead * reason: string
    /// A pinned Fuaran.UI is newer than the snapshotted release.
    | SnapshotBehindPin of id: string * pinned: string * release: string
    /// A version that does not parse.
    | Unreadable of what: string * value: string

type Severity =
    | Pass
    | Warning
    | Refusal

let severity (verdict: Verdict) : Severity =
    match verdict with
    | Aligned _
    | AheadDeclared _ -> Pass
    | AheadCompatible _
    | Behind _ -> Warning
    | AheadBreaking _
    | LeadNotNeeded _
    | SnapshotBehindPin _
    | Unreadable _ -> Refusal

/// One line per verdict, naming every version it compared.
let describe (verdict: Verdict) : string =
    match verdict with
    | Aligned(f, forge, ui) -> sprintf "%s: floor %s, published Fuaran.UI tier floor %s — aligned." f forge ui
    | AheadCompatible(f, forge, ui) ->
        sprintf
            "%s: floor %s is ahead of the published Fuaran.UI tier's %s within one compatibility range — resolves, but the UI tier was built on the older release."
            f
            forge
            ui
    | AheadBreaking(f, forge, ui, release, ids) ->
        sprintf
            "%s: floor %s is ahead of %s, the floor the newest published Fuaran.UI (%s) was built against, across a breaking boundary. NuGet lifts %s to %s for every consumer composing these packages beside Fuaran.UI %s, so a consumer that renders with Fuaran.UI.Renderer.Server (server) or Fuaran.UI.Renderer (Fable client) fails to build against a substrate its UI tier was not compiled for (the FS0193 class). Lower the floor, wait for a Fuaran.UI release built on %s, or declare the lead in docs/platform/open-core-boundary.md. Ids: %s."
            f
            forge
            ui
            release
            f
            forge
            release
            forge
            (String.concat ", " ids)
    | AheadDeclared(f, forge, ui, lead) ->
        sprintf
            "%s: floor %s is ahead of the published Fuaran.UI tier's %s under the lead declared in docs/platform/open-core-boundary.md. Broken until it closes: %s"
            f
            forge
            ui
            lead.BrokenShapes
    | Behind(f, forge, ui) ->
        sprintf
            "%s: the published Fuaran.UI tier is built on %s, ahead of this repository's floor %s across a breaking boundary; a consumer on that tier lifts %s under packages compiled against %s. The co-build probe decides whether it builds."
            f
            ui
            forge
            f
            forge
    | LeadNotNeeded(lead, reason) ->
        sprintf
            "the open lead for %s at %s (declared against Fuaran.UI %s) is not needed: %s. Record it closed in docs/platform/open-core-boundary.md."
            lead.Family
            lead.ForgeFloor
            lead.UiRelease
            reason
    | SnapshotBehindPin(id, pinned, release) ->
        sprintf
            "%s is pinned at %s, newer than the snapshotted Fuaran.UI release %s. Refresh the snapshot: dotnet run --project Build.fsproj -- RefreshFuaranUiFloors"
            id
            pinned
            release
    | Unreadable(what, value) -> sprintf "%s: `%s` is not a version this rule can read." what value

/// The rule.
let evaluate (pins: Pin list) (snapshot: UiSnapshot) (leads: Lead list) : Verdict list =
    let parsed what (v: string) =
        match SemVer.tryParse v with
        | Some s -> Ok s
        | None -> Error(Unreadable(what, v))

    let release = parsed "the snapshotted Fuaran.UI release" snapshot.Release

    let uiFloors = [
        for KeyValue(pkg, deps) in snapshot.Packages do
            for KeyValue(id, v) in deps do
                match familyOf id with
                | Some f when f <> UiFamily -> yield f, id, pkg, v
                | _ -> ()
    ]

    let forgePins =
        pins |> List.choose (fun p -> familyOf p.Id |> Option.map (fun f -> f, p))

    let unreadable = ResizeArray<Verdict>()

    let parseAll what (items: (string * 'a * string) list) =
        items
        |> List.choose (fun (key, payload, v) ->
            match parsed what v with
            | Ok s -> Some(key, payload, s)
            | Error u ->
                unreadable.Add u
                None)

    let forgeByFamily =
        forgePins
        |> List.filter (fun (f, _) -> f <> UiFamily)
        |> List.map (fun (f, p) -> f, p.Id, p.Version)
        |> parseAll "a Fuaran pin in Directory.Packages.props"
        |> List.groupBy (fun (f, _, _) -> f)
        |> List.map (fun (f, rows) ->
            let floor =
                rows
                |> List.map (fun (_, _, v) -> v)
                |> List.reduce (fun a b -> if SemVer.compare a b >= 0 then a else b)

            f, floor, rows |> List.map (fun (_, id, _) -> id))

    let uiByFamily =
        uiFloors
        |> List.map (fun (f, id, pkg, v) -> f, (id, pkg), v)
        |> parseAll "a floor in the Fuaran.UI snapshot"
        |> List.groupBy (fun (f, _, _) -> f)
        |> List.map (fun (f, rows) ->
            let floor =
                rows
                |> List.map (fun (_, _, v) -> v)
                |> List.reduce (fun a b -> if SemVer.compare a b <= 0 then a else b)

            f, (floor, rows |> List.map (fun (_, (id, _), _) -> id) |> List.distinct))
        |> Map.ofList

    let openLeads = leads |> List.filter (fun l -> l.ClosedBy.IsNone)
    let usedLeads = ResizeArray<Lead>()

    let sameVersion (a: string) (b: SemVer) =
        match SemVer.tryParse a with
        | Some a -> SemVer.compare a b = 0
        | None -> false

    let compared = [
        for f, forge, forgeIds in forgeByFamily do
            match uiByFamily.TryFind f with
            | None -> ()
            | Some(ui, uiIds) ->
                let fv, uv = SemVer.text forge, SemVer.text ui
                let c = SemVer.compare forge ui

                if c > 0 && SemVer.crossesBreakingBoundary forge ui then
                    match
                        openLeads
                        |> List.tryFind (fun l -> l.Family = f && sameVersion l.ForgeFloor forge)
                    with
                    | Some lead ->
                        usedLeads.Add lead
                        AheadDeclared(f, fv, uv, lead)
                    | None ->
                        let ids = (forgeIds @ uiIds) |> List.distinct |> List.sort
                        AheadBreaking(f, fv, uv, snapshot.Release, ids)
                elif c > 0 then
                    AheadCompatible(f, fv, uv)
                elif c < 0 && SemVer.crossesBreakingBoundary ui forge then
                    Behind(f, fv, uv)
                else
                    Aligned(f, fv, uv)
    ]

    let staleLeads = [
        for lead in openLeads do
            if not (usedLeads.Contains lead) then
                let forgeFloor =
                    forgeByFamily
                    |> List.tryFind (fun (f, _, _) -> f = lead.Family)
                    |> Option.map (fun (_, v, _) -> v)

                let reason =
                    match forgeFloor, uiByFamily.TryFind lead.Family with
                    | None, _ -> sprintf "this repository no longer pins a %s package" lead.Family
                    | _, None -> sprintf "the published Fuaran.UI tier no longer depends on %s" lead.Family
                    | Some fv, Some(uv, _) when not (sameVersion lead.ForgeFloor fv) ->
                        sprintf
                            "the floor is now %s, not the declared %s (a new lead is a new declaration)"
                            (SemVer.text fv)
                            lead.ForgeFloor
                    | Some _, Some(uv, _) ->
                        sprintf "Fuaran.UI %s is built on %s, which closes it" snapshot.Release (SemVer.text uv)

                yield LeadNotNeeded(lead, reason)
    ]

    let snapshotChecks =
        match release with
        | Error u -> [ u ]
        | Ok rel -> [
            for f, p in forgePins do
                if f = UiFamily then
                    match SemVer.tryParse p.Version with
                    | None -> Unreadable("a Fuaran.UI pin", p.Version)
                    | Some v when SemVer.compare v rel > 0 -> SnapshotBehindPin(p.Id, p.Version, snapshot.Release)
                    | Some _ -> ()
          ]

    List.ofSeq unreadable @ snapshotChecks @ compared @ staleLeads

/// The refusals among `verdicts`.
let refusals (verdicts: Verdict list) : Verdict list =
    verdicts |> List.filter (fun v -> severity v = Refusal)

// ─── Inputs ──────────────────────────────────────────────────────────────

/// Every `Fuaran.*` `PackageVersion` in a central-package-management props file,
/// with `$(Property)` versions resolved against the file's own properties. An
/// unresolvable property is an error rather than a skipped pin: a pin the rule
/// cannot read is a pin it cannot decide.
let pinsOfPackagesProps (text: string) : Result<Pin list, string list> =
    let doc = XDocument.Parse text
    let local (e: XElement) = e.Name.LocalName

    let properties =
        doc.Descendants()
        |> Seq.filter (fun e -> not (isNull e.Parent) && local e.Parent = "PropertyGroup" && not e.HasElements)
        |> Seq.map (fun e -> local e, e.Value.Trim())
        |> Map.ofSeq

    let reference = Regex(@"^\$\((?<name>[A-Za-z_][A-Za-z0-9_.-]*)\)$")

    let rows = [
        for e in doc.Descendants() do
            if local e = "PackageVersion" then
                let id = e.Attribute(XName.Get "Include")
                let version = e.Attribute(XName.Get "Version")

                if not (isNull id) && not (isNull version) && (familyOf id.Value).IsSome then
                    let raw = version.Value.Trim()
                    let m = reference.Match raw

                    if m.Success then
                        match properties.TryFind m.Groups["name"].Value with
                        | Some v -> Ok { Id = id.Value; Version = v }
                        | None -> Error(sprintf "%s: version %s names a property the file does not define" id.Value raw)
                    else
                        Ok { Id = id.Value; Version = raw }
    ]

    match
        rows
        |> List.choose (function
            | Error e -> Some e
            | Ok _ -> None)
    with
    | [] ->
        Ok(
            rows
            |> List.choose (function
                | Ok p -> Some p
                | Error _ -> None)
        )
    | errors -> Error errors

/// The `Fuaran.*` dependency floors a nuspec declares, across every target-framework
/// group (the lowest where groups disagree).
let floorsOfNuspec (xml: string) : Map<string, string> =
    let doc = XDocument.Parse xml

    doc.Descendants()
    |> Seq.filter (fun e -> e.Name.LocalName = "dependency")
    |> Seq.choose (fun e ->
        let id = e.Attribute(XName.Get "id")
        let version = e.Attribute(XName.Get "version")

        if isNull id || isNull version || (familyOf id.Value).IsNone then
            None
        else
            // A nuspec range `[a, )` or `[a]` names its floor first.
            let v =
                version.Value.Trim().TrimStart('[', '(').Split(',').[0].TrimEnd(']', ')').Trim()

            Some(id.Value, v))
    |> Seq.groupBy fst
    |> Seq.map (fun (id, vs) ->
        let lowest =
            vs
            |> Seq.map snd
            |> Seq.sortWith (fun a b ->
                match SemVer.tryParse a, SemVer.tryParse b with
                | Some x, Some y -> SemVer.compare x y
                | _ -> String.CompareOrdinal(a, b))
            |> Seq.head

        id, lowest)
    |> Map.ofSeq

/// The newest RELEASE (no prerelease label) in a registry's version list.
let newestRelease (versions: string seq) : string option =
    versions
    |> Seq.choose (fun v -> SemVer.tryParse v |> Option.filter (fun s -> s.Label = ""))
    |> Seq.sortWith (fun a b -> SemVer.compare b a)
    |> Seq.tryHead
    |> Option.map SemVer.text

/// Which release a refresh records: the newest published one, or the one asked
/// for — refused when the registry does not serve it.
let chooseRelease (requested: string option) (published: string list) : Result<string, string> =
    match requested with
    | None ->
        match newestRelease published with
        | Some v -> Ok v
        | None -> Error "the registry lists no released Fuaran.UI version."
    | Some r ->
        let served =
            published
            |> List.exists (fun p ->
                match SemVer.tryParse p, SemVer.tryParse r with
                | Some a, Some b -> SemVer.compare a b = 0
                | _ -> p = r)

        if served then
            Ok r
        else
            Error(
                sprintf "Fuaran.UI %s is not published on the registry; a snapshot records a published release only." r
            )

/// The snapshot's committed JSON form: stable key order, so a refresh that finds
/// nothing new rewrites nothing.
let snapshotToJson (snapshot: UiSnapshot) (source: string) : string =
    use stream = new IO.MemoryStream()

    do
        use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        w.WriteStartObject()
        w.WriteString("release", snapshot.Release)
        w.WriteString("source", source)
        w.WriteStartObject "packages"

        for KeyValue(pkg, deps) in snapshot.Packages do
            w.WriteStartObject pkg

            for KeyValue(id, v) in deps do
                w.WriteString(id, v)

            w.WriteEndObject()

        w.WriteEndObject()
        w.WriteEndObject()

    Text.Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n"

let snapshotOfJson (text: string) : Result<UiSnapshot, string> =
    try
        use doc = JsonDocument.Parse text
        let root = doc.RootElement

        match root.TryGetProperty "release", root.TryGetProperty "packages" with
        | (true, release), (true, packages) when packages.ValueKind = JsonValueKind.Object ->
            let pkgs =
                packages.EnumerateObject()
                |> Seq.map (fun p ->
                    p.Name,
                    (p.Value.EnumerateObject()
                     |> Seq.map (fun d -> d.Name, d.Value.GetString())
                     |> Map.ofSeq))
                |> Map.ofSeq

            if pkgs.IsEmpty then
                Error "the snapshot lists no Fuaran.UI tier package."
            else
                Ok {
                    Release = release.GetString()
                    Packages = pkgs
                }
        | _ -> Error "the snapshot needs a `release` string and a `packages` object."
    with ex ->
        Error(sprintf "the snapshot is not valid JSON: %s" ex.Message)

/// The heading the lead declaration lives under in `open-core-boundary.md`.
[<Literal>]
let LeadsHeading = "### Declared leads"

/// The lead table under `LeadsHeading`: columns `Family`, `Floor here`,
/// `Fuaran.UI release`, `Its floor`, `Status` (`open`, or `closed by Fuaran.UI <v>`)
/// and `Broken until closed`, matched by header text, backticks stripped. A missing
/// section is an error: a deleted declaration must not read as "no leads".
let leadsOfMarkdown (text: string) : Result<Lead list, string> =
    let lines = text.Replace("\r\n", "\n").Split '\n'

    match lines |> Array.tryFindIndex (fun l -> l.Trim() = LeadsHeading) with
    | None -> Error(sprintf "no `%s` section — the lead declaration is missing." LeadsHeading)
    | Some start ->
        let section =
            lines
            |> Array.skip (start + 1)
            |> Array.takeWhile (fun l -> not (l.StartsWith "#"))

        let cells (l: string) =
            l.Trim().Trim('|').Split '|' |> Array.map (fun c -> c.Trim().Replace("`", ""))

        let table = section |> Array.filter (fun l -> l.TrimStart().StartsWith "|")

        if table.Length < 2 then
            Error(sprintf "the `%s` section holds no table." LeadsHeading)
        else
            let header = cells table[0] |> Array.map _.ToLowerInvariant()

            let column name =
                header |> Array.tryFindIndex (fun h -> h = name)

            let wanted = [
                "family"
                "floor here"
                "fuaran.ui release"
                "its floor"
                "status"
                "broken until closed"
            ]

            match wanted |> List.filter (fun w -> (column w).IsNone) with
            | _ :: _ as missing -> Error(sprintf "the lead table lacks column(s): %s." (String.concat ", " missing))
            | [] ->
                let at name (row: string[]) =
                    let i = (column name).Value
                    if i < row.Length then row[i] else ""

                table
                |> Array.skip 2
                |> Array.map cells
                |> Array.fold
                    (fun acc row ->
                        match acc with
                        | Error e -> Error e
                        | Ok leads ->
                            let status = at "status" row
                            let lowered = status.ToLowerInvariant()

                            let closedBy =
                                if lowered = "open" then
                                    Ok None
                                elif lowered.StartsWith "closed by fuaran.ui " then
                                    Ok(Some(status.Substring("closed by Fuaran.UI ".Length).Trim()))
                                else
                                    Error(
                                        sprintf
                                            "lead %s: status `%s` is neither `open` nor `closed by Fuaran.UI <version>`."
                                            (at "family" row)
                                            status
                                    )

                            closedBy
                            |> Result.map (fun c ->
                                leads
                                @ [
                                    {
                                        Family = at "family" row
                                        ForgeFloor = at "floor here" row
                                        UiRelease = at "fuaran.ui release" row
                                        UiFloor = at "its floor" row
                                        ClosedBy = c
                                        BrokenShapes = at "broken until closed" row
                                    }
                                ]))
                    (Ok [])

// ─── The co-build probe's scaffold ───────────────────────────────────────

/// The scratch consumer the co-build probe restores, builds and Fable-compiles:
/// the fact tier beside the published Fuaran-UI renderers, server and client, the
/// shape that failed. Pure text, so the probe's inputs are checked offline.
module CoBuild =

    /// The ToolUp packages the consumer composes, each beside its Fuaran-UI half.
    let serverPackages = [ "ToolUp.Facts.Server"; "Fuaran.UI"; "Fuaran.UI.Renderer.Server" ]
    let clientPackages = [ "ToolUp.Facts.Client"; "Fuaran.UI.Renderer" ]

    /// Only the probe's own feed and nuget.org: `<clear />` drops every source a
    /// parent nuget.config would otherwise merge in, so nothing resolves from a
    /// machine's local feed.
    let nugetConfig (ownFeed: string option) : string =
        let own =
            match ownFeed with
            | Some dir -> sprintf "    <add key=\"own-packs\" value=\"%s\" />\n" (Security.SecurityElement.Escape dir)
            | None -> ""

        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n"
        + own
        + "    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />\n  </packageSources>\n</configuration>\n"

    let private project (sdkComment: string) (packages: (string * string) list) (sources: string list) =
        let refs =
            packages
            |> List.map (fun (id, v) -> sprintf "    <PackageReference Include=\"%s\" Version=\"%s\" />" id v)
            |> String.concat "\n"

        let compiles =
            sources
            |> List.map (sprintf "    <Compile Include=\"%s\" />")
            |> String.concat "\n"

        sprintf
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <!-- %s -->\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>\n    <WarningsAsErrors>NU1603;NU1605</WarningsAsErrors>\n  </PropertyGroup>\n  <ItemGroup>\n%s\n  </ItemGroup>\n  <ItemGroup>\n%s\n  </ItemGroup>\n</Project>\n"
            sdkComment
            compiles
            refs

    /// The server half: the fact tier and a page rendered through the published
    /// server renderer.
    let serverProject (toolUpVersion: string) (uiVersion: string) : string =
        project "co-build probe: the fact tier beside the published Fuaran-UI server renderer" [
            "ToolUp.Facts.Server", toolUpVersion
            "Fuaran.UI", uiVersion
            "Fuaran.UI.Renderer.Server", uiVersion
        ] [ "Program.fs" ]

    let serverSource: string =
        String.concat "\n" [
            "module CoBuild.Server"
            ""
            "open Fuaran.UI"
            "open Fuaran.UI.Types"
            ""
            "[<EntryPoint>]"
            "let main _ ="
            "    let page ="
            "        Fuaran.heading \"co-build\" {"
            "            Defaults.heading with"
            "                Level = 1"
            "                Text = TextSource.Literal \"co-build\""
            "        }"
            ""
            "    let html = Fuaran.UI.Renderer.Server.Render.render Fuaran.UI.Renderer.BindingResolver.empty page"
            "    let wire = Fuaran.UI.Generated.encodeNode page"
            "    let slice = typeof<ToolUp.Facts.FactBrowseSlice.Held>"
            "    printfn \"%d %d %s\" html.Length wire.Length slice.Name"
            "    0"
            ""
        ]

    /// The client half: the fact browse's local refinement and a node drawn
    /// through the published browser renderer, compiled by Fable.
    let clientProject (toolUpVersion: string) (uiVersion: string) : string =
        project "co-build probe: the fact tier beside the published Fuaran-UI browser renderer, Fable-compiled" [
            "ToolUp.Facts.Client", toolUpVersion
            "Fuaran.UI.Renderer", uiVersion
        ] [ "App.fs" ]

    let clientSource: string =
        String.concat "\n" [
            "module CoBuild.Client"
            ""
            "open Fuaran.UI"
            "open Fuaran.UI.Types"
            ""
            "let page: Node<obj> ="
            "    Fuaran.heading \"co-build\" {"
            "        Defaults.heading with"
            "            Level = 1"
            "            Text = TextSource.Literal \"co-build\""
            "    }"
            ""
            "let element = Fuaran.UI.Renderer.Render.renderWithSources Fuaran.UI.Renderer.BindingResolver.empty ignore page"
            "let model = ToolUp.Facts.Client.FactBrowseModel.empty"
            ""
        ]

    /// The probe runs Fable from the consumer's own directory, under its own tool
    /// manifest, pinned to the version the repository's Fable samples use.
    let toolManifest (fableVersion: string) : string =
        sprintf
            "{\n  \"version\": 1,\n  \"isRoot\": true,\n  \"tools\": {\n    \"fable\": {\n      \"version\": \"%s\",\n      \"commands\": [ \"fable\" ],\n      \"rollForward\": false\n    }\n  }\n}\n"
            fableVersion

    /// The server half is run as well as built: .NET binds a member at first call,
    /// so a substrate mismatch the compiler never touched surfaces when the page is
    /// rendered and encoded.
    let serverAssembly = "bin/Debug/net10.0/CoBuild.Server.dll"

    /// Whether a failed step's output is the substrate-mismatch class this probe
    /// exists for, so a red run says so rather than leaving a reader to grep. Three
    /// shapes, one cause: FS0193 on .NET; an error inside a published Fuaran
    /// package's own sources under Fable (it compiles them against the substrate
    /// the consumer resolved); a member missing at run time from a Fuaran assembly.
    let isSubstrateMismatch (output: string) : bool =
        output.Contains "FS0193"
        || Regex.IsMatch(output, @"did not contain the (namespace, module or type|type)")
        || Regex.IsMatch(output, @"fable_modules[\\/]Fuaran\.[^\\/\r\n]+[\\/][^\r\n]*error")
        || Regex.IsMatch(output, @"(MissingMethodException|MissingFieldException|TypeLoadException)[^\r\n]*Fuaran\.")