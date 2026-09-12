// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 260 — the release bump, DERIVED from the public-surface diff
/// rather than typed by hand.
///
/// **What this decides.** Given the api-baselines as they stood at the
/// last release and as they stand now, it classifies each package's
/// surface change (unchanged / additive / breaking), rolls those up to
/// the one lockstep SDK version, and says which bump that demands. The
/// `VerifySemVerBump` target (root `Build.fs`) then checks the
/// `<Version>` in `Directory.Build.props` against it and fails when the
/// declared version implies a SMALLER bump than the surface moved by.
/// It never edits the version: the proposal is advice, the check is a
/// floor.
///
/// **The one thing worth reading before the code.** The phase shard asked
/// for the diff between the *working public surface* and the approved
/// baselines. That was true when it was written and is not true now:
/// Phase 618 made the approval gate fail in BOTH directions, so on any
/// tree that passes `verify.ps1` the working surface and its baselines
/// are IDENTICAL by construction. A bump computed from that difference
/// would classify every green tree as `unchanged` and demand a patch —
/// and since this check only fails on an UNDER-bump, it would never fire
/// at all. The measurement that still means something is the interval
/// between RELEASES: the committed baselines at the last release tag
/// versus the committed baselines now. Phase 618 is precisely what makes
/// that a faithful proxy for the surface diff rather than a weaker one —
/// each side of it is an exact mirror of the surface at that commit,
/// because a commit where it was not would have failed the gate. So the
/// dependency runs the other way from how it looks: this check is
/// trustworthy *because* the approval gate is strict, and relaxing 618
/// would silently cost this one its meaning.
///
/// Reading the baselines rather than the assemblies also means the check
/// needs no build, no `MetadataLoadContext` and no network — it is text
/// and `git`, so it can run before a release packs anything.
///
/// **The 0.x policy is the repo's own, not a new one.**
/// `Directory.Build.props` states it twice: "minor bumps may include
/// breaking changes during 0.x; patch bumps remain non-breaking", and, at
/// the 0.23.0 entry, "MINOR is where this repo's SemVer-on-0.x policy
/// puts a break". So while the released line is pre-1.0, MINOR is the
/// breaking slot and PATCH carries compatible growth; from 1.0.0 the
/// ordinary SemVer table applies (breaking → major, additive → minor).
/// `demandedBump` is those two tables and nothing else, and it selects by
/// the RELEASED version rather than the declared one, because the promise
/// a consumer is holding is the one attached to the version they are on.
///
/// **The release point is the newest tag STRICTLY BELOW the declared
/// version**, not simply the newest tag. The publish workflow runs on the
/// `v*` tag push, so by the time this executes there the tag being
/// released already exists and points at HEAD; measuring against it would
/// compare the release with itself, find nothing, and then fail the same
/// version for not having advanced past itself. Defining the release
/// point this way makes the check give the same answer whether it runs
/// before or after the tag is cut.
///
/// **Deliberately FAKE-free and BCL-only**, for the reason
/// `SdkManifest.fs` states: it is compiled into `Build.fsproj` and
/// source-linked into the Build test pack, and a dependency either side
/// would force the rule to be written twice. It is also free of `git` —
/// the target does the reading and hands the texts in, so every decision
/// below is a pure function over strings and is exercised as one.
module ToolUp.Forge.SemVerBump

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open ToolUp.Platform.Tests.Contracts.SurfaceDiff

// ─── Versions ────────────────────────────────────────────────────────

/// A three-part SemVer core. Pre-release and build metadata are parsed
/// off and discarded: this check reasons about the compatibility slots,
/// and `0.23.0-rc.1` makes the same promise about them as `0.23.0`.
type Version = { Major: int; Minor: int; Patch: int }

[<RequireQualifiedAccess>]
module Version =
    let render (v: Version) =
        sprintf "%d.%d.%d" v.Major v.Minor v.Patch

    /// Total order on the three slots — the comparison every
    /// tag-selection decision below is made with, in one place so the
    /// "newest tag" and the "ahead of declared" arms cannot disagree.
    let compare (a: Version) (b: Version) =
        Operators.compare (a.Major, a.Minor, a.Patch) (b.Major, b.Minor, b.Patch)

/// `v0.22.0` / `0.22.0` / `0.23.0-rc.1` → a `Version`. A leading `v` and
/// any `-prerelease` / `+build` suffix are tolerated; anything else is an
/// error rather than a guess, because a version this cannot read is a
/// version whose compatibility slots it must not pretend to know.
let parseVersion (text: string) : Result<Version, string> =
    if isNull text then
        Error "no version text"
    else

        let trimmed = text.Trim()

        let core =
            let withoutV =
                if trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) then
                    trimmed.Substring 1
                else
                    trimmed

            match withoutV.IndexOfAny [| '-'; '+' |] with
            | -1 -> withoutV
            | i -> withoutV.Substring(0, i)

        let parts = core.Split '.'

        let numeric (s: string) =
            match Int32.TryParse(s, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None

        match parts |> Array.map numeric with
        | [| Some ma; Some mi; Some pa |] -> Ok { Major = ma; Minor = mi; Patch = pa }
        | _ -> Error(sprintf "'%s' is not a three-part SemVer version" trimmed)

/// The `<Version>` declared in `Directory.Build.props`.
///
/// XML comments are blanked first. The file's own prose mentions
/// `<Version>` while explaining per-package overrides, and a check that
/// read a version out of a comment would be reporting on documentation.
let declaredVersionIn (propsText: string) : Result<Version, string> =
    let uncommented =
        Regex.Replace(propsText, @"<!--.*?-->", "", RegexOptions.Singleline)

    let matches = Regex.Matches(uncommented, @"<Version>\s*([^<]+?)\s*</Version>")

    match matches |> Seq.map (fun m -> m.Groups[1].Value) |> List.ofSeq with
    | [ one ] -> parseVersion one
    | [] -> Error "Directory.Build.props declares no <Version> element outside a comment"
    | many ->
        Error(
            sprintf
                "Directory.Build.props declares %d <Version> elements (%s) — the lockstep SDK version must be a single declaration"
                many.Length
                (String.concat ", " many)
        )

// ─── Bumps ───────────────────────────────────────────────────────────

/// A SemVer bump class. Ordered: `Patch < Minor < Major`.
type Bump =
    | Patch
    | Minor
    | Major

[<RequireQualifiedAccess>]
module Bump =
    let rank =
        function
        | Patch -> 0
        | Minor -> 1
        | Major -> 2

    let name =
        function
        | Patch -> "patch"
        | Minor -> "minor"
        | Major -> "major"

    let atLeast (needed: Bump) (actual: Bump) = rank actual >= rank needed

/// `previous` advanced by `bump` — the version this check PROPOSES.
let applyBump (bump: Bump) (v: Version) =
    match bump with
    | Major -> {
        Major = v.Major + 1
        Minor = 0
        Patch = 0
      }
    | Minor -> {
        Major = v.Major
        Minor = v.Minor + 1
        Patch = 0
      }
    | Patch -> { v with Patch = v.Patch + 1 }

/// What advancing from `released` to `declared` amounts to. `None` means
/// the declared version does not advance at all — it is the released one,
/// or behind it — which is never a legal release whatever the surface
/// did, so it is a distinct answer rather than a `Patch` that happens to
/// be zero-sized.
let advanceOf (released: Version) (declared: Version) : Bump option =
    if declared.Major > released.Major then Some Major
    elif declared.Major < released.Major then None
    elif declared.Minor > released.Minor then Some Minor
    elif declared.Minor < released.Minor then None
    elif declared.Patch > released.Patch then Some Patch
    else None

// ─── Surface classes ─────────────────────────────────────────────────

/// What one package's surface did between the two baselines. Ordered:
/// `Unchanged < Additive < Breaking`, which is the order the roll-up to
/// the lockstep version takes the maximum in.
type SurfaceClass =
    | Unchanged
    | Additive
    | Breaking

[<RequireQualifiedAccess>]
module SurfaceClass =
    let rank =
        function
        | Unchanged -> 0
        | Additive -> 1
        | Breaking -> 2

    let name =
        function
        | Unchanged -> "unchanged"
        | Additive -> "additive"
        | Breaking -> "breaking"

    let max a b = if rank a >= rank b then a else b

/// The bump a surface class demands, under the policy this repo states in
/// `Directory.Build.props`.
///
/// `releasedIsPreOne` selects the table, and it is the RELEASED version's
/// major that decides — see this file's header. The two tables agree on
/// `Unchanged`, and differ by exactly one slot everywhere else, which is
/// what "0.x shifts the compatibility slots down by one" means when it is
/// written out rather than asserted.
let demandedBump (releasedIsPreOne: bool) (cls: SurfaceClass) : Bump =
    if releasedIsPreOne then
        match cls with
        | Breaking -> Minor
        | Additive
        | Unchanged -> Patch
    else
        match cls with
        | Breaking -> Major
        | Additive -> Minor
        | Unchanged -> Patch

// ─── Per-package classification ──────────────────────────────────────

/// One package's surface movement between the release point and now.
///
/// `Withdrawn` / `Introduced` are the whole-file cases: a baseline that
/// existed at the release and does not now is a package that STOPPED
/// being produced, and one that exists now and did not then is a new
/// package. They are reported separately because a withdrawal is the one
/// break a version number cannot express — a consumer that raises its pin
/// without dropping the `PackageReference` fails at RESTORE with
/// `NU1101`, not at compile — and the release notes need to say so. They
/// change no arithmetic: both fall out of the same set difference against
/// an empty surface.
type PackageChange = {
    Package: string
    Class: SurfaceClass
    Removed: string list
    Added: string list
    Withdrawn: bool
    Introduced: bool
}

/// `api-baselines/ToolUp.Graph.Core.approved.txt` → `ToolUp.Graph.Core`.
/// Tolerates either slash, so a path from `git` and one from
/// `Path.Combine` name the same package.
let packageOfBaselinePath (path: string) =
    let file = path.Replace('\\', '/').Split('/') |> Array.last

    if file.EndsWith(".approved.txt", StringComparison.OrdinalIgnoreCase) then
        file.Substring(0, file.Length - ".approved.txt".Length)
    else
        file

/// Classify one package from its baseline text at the release point and
/// now. `None` on a side means the baseline did not exist there.
///
/// Every case is the SAME comparison — `compareSurface`, the approval
/// gate's own set difference (`Contracts/SurfaceDiff.fs`) — against an
/// empty surface where a side is absent. That is deliberate: the
/// tokeniser that decides what counts as a member here has to be the one
/// that decides it in the gate, or the two can disagree about whether a
/// change happened at all.
let classifyPackage (package: string) (released: string option) (current: string option) : PackageChange =
    let releasedText = defaultArg released ""
    let currentText = defaultArg current ""
    let drift = compareSurface releasedText currentText

    let cls =
        if not (List.isEmpty drift.Removed) then Breaking
        elif not (List.isEmpty drift.Added) then Additive
        else Unchanged

    {
        Package = package
        Class = cls
        Removed = drift.Removed
        Added = drift.Added
        Withdrawn = released.IsSome && current.IsNone
        Introduced = released.IsNone && current.IsSome
    }

/// The lockstep roll-up: the highest class any package reached. The SDK
/// versions in lockstep — one `<Version>` for every package — so one
/// breaking package makes the whole release breaking, however many others
/// did not move.
let rollUp (changes: PackageChange list) : SurfaceClass =
    changes |> List.fold (fun acc c -> SurfaceClass.max acc c.Class) Unchanged

// ─── Release-point selection ─────────────────────────────────────────

/// Tag names paired with the versions they parse to, unparseable tags
/// dropped. Dropping rather than failing is deliberate: a repo may carry
/// tags that are not releases at all, and a check that refused to run
/// because of one of them would be measuring the tag namespace instead of
/// the surface.
let releaseTags (tags: string seq) : (string * Version) list =
    tags
    |> Seq.choose (fun t ->
        match parseVersion t with
        | Ok v -> Some(t, v)
        | Error _ -> None)
    |> List.ofSeq

/// The newest release STRICTLY BELOW `declared` — the version a consumer
/// could already be holding, and therefore the one this release's
/// compatibility promise is made against. See the header for why the
/// newest tag outright is the wrong choice on the publish workflow.
let releasePoint (declared: Version) (tags: string seq) : (string * Version) option =
    releaseTags tags
    |> List.filter (fun (_, v) -> Version.compare v declared < 0)
    |> List.sortWith (fun (_, a) (_, b) -> Version.compare a b)
    |> List.tryLast

/// Release tags AHEAD of the declared version. Non-empty means the
/// declared version is behind something already published, which no
/// amount of surface analysis can make releasable — reported as its own
/// finding rather than folded into the bump arithmetic.
let releasesAhead (declared: Version) (tags: string seq) : (string * Version) list =
    releaseTags tags
    |> List.filter (fun (_, v) -> Version.compare v declared > 0)
    |> List.sortWith (fun (_, a) (_, b) -> Version.compare a b)

// ─── The assessment ──────────────────────────────────────────────────

/// The whole answer, as data — so the target prints it, the tests assert
/// on it, and neither has to re-derive any of it from the other's text.
type Assessment = {
    ReleaseTag: string
    Released: Version
    Declared: Version
    Changes: PackageChange list
    Class: SurfaceClass
    Demanded: Bump
    /// The smallest version that satisfies `Demanded` — the PROPOSAL.
    Proposed: Version
    /// What the declared version actually advances by, or `None` when it
    /// does not advance at all.
    Advance: Bump option
}

let assess (releaseTag: string) (released: Version) (declared: Version) (changes: PackageChange list) : Assessment =
    let cls = rollUp changes
    let demanded = demandedBump (released.Major = 0) cls

    {
        ReleaseTag = releaseTag
        Released = released
        Declared = declared
        Changes = changes
        Class = cls
        Demanded = demanded
        Proposed = applyBump demanded released
        Advance = advanceOf released declared
    }

/// Whether the declared version meets the floor the surface demands.
let meetsDemand (a: Assessment) =
    match a.Advance with
    | Some actual -> Bump.atLeast a.Demanded actual
    | None -> false

// ─── Reports (pure — so the wording itself is testable) ──────────────

let private movedPackages (a: Assessment) =
    a.Changes
    |> List.filter (fun c -> c.Class <> Unchanged)
    |> List.sortWith (fun x y ->
        match Operators.compare (SurfaceClass.rank y.Class) (SurfaceClass.rank x.Class) with
        | 0 -> String.CompareOrdinal(x.Package, y.Package)
        | n -> n)

let private packageLine (c: PackageChange) =
    let shape =
        if c.Withdrawn then " — PACKAGE WITHDRAWN"
        elif c.Introduced then " — new package"
        else ""

    sprintf "  %-10s %s%s (+%d / -%d)" (SurfaceClass.name c.Class) c.Package shape c.Added.Length c.Removed.Length

/// The proposal, always printed — a check that only speaks when it is
/// angry teaches nobody what it is measuring.
let describe (a: Assessment) : string =
    let moved = movedPackages a
    let sb = StringBuilder()

    // No denominator: `Changes` carries only the baselines whose blob
    // moved, because an identical blob is unchanged by definition and
    // reading the other ~110 would buy nothing. Claiming "N of N" out of
    // that set would be true and would read as a total.
    sb.AppendLine(
        sprintf
            "Public surface since %s (%s): %s — %d package baseline(s) moved."
            a.ReleaseTag
            (Version.render a.Released)
            (SurfaceClass.name a.Class)
            moved.Length
    )
    |> ignore

    for c in moved |> List.truncate 20 do
        sb.AppendLine(packageLine c) |> ignore

    if moved.Length > 20 then
        sb.AppendLine(sprintf "  … and %d more" (moved.Length - 20)) |> ignore

    sb.AppendLine(
        sprintf
            "Demanded bump: %s (SemVer-on-%s policy) → proposed %s. Declared: %s (%s)."
            (Bump.name a.Demanded)
            (if a.Released.Major = 0 then "0.x" else "1.x+")
            (Version.render a.Proposed)
            (Version.render a.Declared)
            (match a.Advance with
             | Some b -> sprintf "a %s over %s" (Bump.name b) (Version.render a.Released)
             | None -> sprintf "NO advance over %s" (Version.render a.Released))
    )
    |> ignore

    sb.ToString().TrimEnd()

let private evidenceFor (a: Assessment) =
    let breaking = a.Changes |> List.filter (fun c -> c.Class = Breaking)

    match breaking with
    | [] -> ""
    | _ ->
        let sample =
            breaking
            |> List.truncate 3
            |> List.map (fun c ->
                let tokens = c.Removed |> List.truncate 3 |> List.map (sprintf "      - %s")

                let elided =
                    match c.Removed.Length - List.length tokens with
                    | 0 -> ""
                    | n -> sprintf "\n      … and %d more" n

                sprintf
                    "    %s%s\n%s%s"
                    c.Package
                    (if c.Withdrawn then " (package withdrawn entirely)" else "")
                    (String.concat "\n" tokens)
                    elided)

        let more =
            match breaking.Length - List.length sample with
            | 0 -> ""
            | n -> sprintf "\n    … and %d more breaking package(s)" n

        sprintf
            "\n\n  The removed/renamed/retyped members that make this breaking:\n%s%s"
            (String.concat "\n" sample)
            more

/// The FAILURE, or `None` when the declared version meets the floor.
///
/// Deliberately shaped like the approval gate's message: it says what is
/// wrong, what the smallest correct answer is, and — because the reader
/// of a red release check is about to edit a version number under time
/// pressure — that this is a floor and not an instruction. A version
/// ABOVE the demand is always fine.
let defect (a: Assessment) : string option =
    if meetsDemand a then
        None
    else

        let head =
            match a.Advance with
            | None ->
                sprintf
                    "UNDER-BUMP: Directory.Build.props declares %s, which does not advance past the last release %s (%s)."
                    (Version.render a.Declared)
                    a.ReleaseTag
                    (Version.render a.Released)
            | Some actual ->
                sprintf
                    "UNDER-BUMP: the public surface changed by a %s since %s, which demands a %s bump; Directory.Build.props declares %s, a %s."
                    (SurfaceClass.name a.Class)
                    a.ReleaseTag
                    (Bump.name a.Demanded)
                    (Version.render a.Declared)
                    (Bump.name actual)

        Some(
            sprintf
                "%s%s\n\n  Smallest version that satisfies the diff: %s. Any HIGHER version is fine — this is a floor, not an instruction, and a deliberately larger bump is never wrong.\n\n  The classification is derived from api-baselines/ at %s versus the working tree, using the same comparer the public-API approval gate runs (Phase 175/618). If a removal here is intentional, the bump is the answer; if it is not, the surface change is the bug.\n\n%s"
                head
                (evidenceFor a)
                (Version.render a.Proposed)
                a.ReleaseTag
                (describe a)
        )

/// The precondition report for a tree this check cannot measure — no
/// release tag below the declared version is visible.
///
/// It is a FAILURE, not a pass. A shallow CI checkout fetches no tags, so
/// "I can see no release" and "there has been no release" are the same
/// observation from in here, and a release gate that reports green when
/// it could not find the thing it compares against is the vacuous-green
/// shape this repo fails builds over elsewhere. The remedy is named
/// rather than implied.
let noReleasePointReport (declared: Version) (tagsSeen: int) =
    sprintf
        "VerifySemVerBump: no release tag below the declared version %s is visible (%d candidate tag(s) read). The bump is derived by diffing api-baselines/ at the last release against the working tree, so without that release point there is nothing to measure — and a release check cannot report green on a comparison it did not make.\n\nIf this is a shallow or tag-less checkout, fetch the tags first:\n\n  git fetch --tags --force\n\nIf this repository genuinely has no release yet, there is no compatibility promise to keep and this check has nothing to say: set TOOLUP_SEMVER_BASE to the ref to measure from, or run the release without it."
        (Version.render declared)
        tagsSeen

/// The report for a declared version that is BEHIND a tag already cut.
let behindReleaseReport (declared: Version) (ahead: (string * Version) list) =
    sprintf
        "VerifySemVerBump: Directory.Build.props declares %s, but %d release tag(s) are already ahead of it: %s. No surface analysis can make this releasable — the version must advance past everything published, and the bump this check computes is measured from the newest release below the declared version, which is not the newest release."
        (Version.render declared)
        ahead.Length
        (ahead |> List.map fst |> String.concat ", ")

// ─── Filesystem side (still `git`-free) ──────────────────────────────

/// `api-baselines/` under `root` — the same directory the approval gate
/// reads, named here rather than restated by the target.
let baselineDir (root: string) = Path.Combine(root, "api-baselines")

/// The current text of one baseline, or `None` when the file is absent
/// (the package was withdrawn since the release point).
let currentBaseline (root: string) (relativePath: string) : string option =
    let full =
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))

    if File.Exists full then
        Some(File.ReadAllText full)
    else
        None