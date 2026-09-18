// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 262 — the per-release CHANGELOG, GENERATED from the api-baseline
/// diffs rather than typed by hand.
///
/// **What this produces.** One `## [version]` section per release tag,
/// newest first, each carrying the public-surface movement between that
/// release and the one before it as three lists — Added / Changed /
/// Removed — plus a link to every migration note that belongs to the
/// release. The `Changelog` target (root `Build.fs`) does the READING
/// (git tags, the baselines at each tag, the migration docs each interval
/// introduced) and hands the data in; everything below is a pure function
/// over it, so a fixture can render a section without a checkout and the
/// idempotence the phase asks for is a property of strings.
///
/// **The diff is the approval gate's own.** Every per-package movement
/// arrives as a `SemVerBump.PackageChange` — Phase 260's classification of
/// `SurfaceDiff.compareSurface`, the set difference the Phase 175/618 gate
/// decides a commit with. This module adds ONE reading on top: a member
/// whose IDENTITY (`Type.member`, the token up to its parameter list or
/// type annotation) is both removed and added in the same interval is a
/// member whose SIGNATURE changed, and it is reported once under
/// **Changed** as `before → after` instead of once under Removed and once
/// under Added. Nothing else is inferred; a reader who wants the raw
/// tokens has the baseline diff itself, and every section says how to get
/// it.
///
/// **Why the lists are capped, and why Added is capped hardest.** A 0.x
/// release routinely adds thousands of public tokens (0.10.0 added 6,700;
/// 0.7.0, the release that introduced the baselines, 21,000), and a
/// packaging move can remove a thousand from one package in one release
/// (0.23.0's promotion of the grid and chart bindings). The counts are
/// always complete; the member LISTS are the part a reader scans, and a
/// consumer scanning a release scans it for what will stop compiling. So
/// each list is capped PER PACKAGE — Removed and Changed generously
/// (`Limits.RemovedPerPackage` / `ChangedPerPackage`), Added tightly
/// (`AddedPerPackage`) — and every truncated list ends with the exact
/// `git diff` that shows the rest. A package that is new in a release, or
/// withdrawn by it, is one line with its member count: a list of every
/// member of a new package is the baseline file, not a changelog entry.
///
/// **No timestamp, no tree SHA.** A release's date is its tag's commit
/// date, which does not move; the draft section at the top is headed
/// "unreleased" and carries no date at all. That is what makes
/// regeneration idempotent — the same tags, baselines and docs render the
/// same bytes — and is why `Changelog --check` can refuse a stale file
/// without a false alarm on every commit.
///
/// **Deliberately FAKE-free and BCL-only**, for the reason `SemVerBump.fs`
/// gives: compiled into `Build.fsproj` and source-linked into the Build
/// test pack, so the target and its fixtures run one implementation.
module ToolUp.Forge.Changelog

open System
open System.Text
open ToolUp.Forge.SemVerBump

// ─── Member identity ─────────────────────────────────────────────────

/// The identity of one baseline token, independent of its signature:
/// `Type.member(args) : ret` → `Type.member`; `Type.Prop : T { get }` →
/// `Type.Prop`; `Type (class)` and `Type (class)  (obsolete)` → `Type`.
/// Two tokens with one key in one interval — one removed, one added — are
/// a signature change, and the renderer says so once.
let memberKey (token: string) : string =
    let t = token.Trim()

    let cuts = [ t.IndexOf '('; t.IndexOf " : " ] |> List.filter (fun i -> i >= 0)

    match cuts with
    | [] -> t
    | _ -> t.Substring(0, List.min cuts).Trim()

/// One member whose signature moved within a release.
type MemberChange = {
    Key: string
    Before: string
    After: string
}

/// One package's movement in one release, as the three lists the section
/// renders. `Introduced` / `Withdrawn` are `PackageChange`'s whole-file
/// cases carried through: for those the lists still hold every member
/// (the counts are read off them) but the renderer collapses them to one
/// line.
type PackageEntry = {
    Package: string
    Introduced: bool
    Withdrawn: bool
    Added: string list
    Changed: MemberChange list
    Removed: string list
}

/// Pair removed and added tokens that share a `memberKey` into `Changed`;
/// what does not pair stays removed or added. Pairing is positional within
/// a key after an ordinal sort, so two overloads that both moved report
/// as two changes rather than four lines, and the result is the same
/// whatever order the inputs arrived in.
let entryOf (c: PackageChange) : PackageEntry =
    let byKey (tokens: string list) =
        tokens
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.groupBy memberKey
        |> Map.ofList

    let removed = byKey c.Removed
    let added = byKey c.Added

    let changed, leftoverRemoved, leftoverAdded =
        removed
        |> Map.fold
            (fun (changed, remR, remA) key removedTokens ->
                match Map.tryFind key added with
                | None -> changed, removedTokens @ remR, remA
                | Some addedTokens ->
                    let n = min removedTokens.Length addedTokens.Length

                    let pairs =
                        List.zip (List.take n removedTokens) (List.take n addedTokens)
                        |> List.map (fun (b, a) -> { Key = key; Before = b; After = a })

                    changed @ pairs, List.skip n removedTokens @ remR, List.skip n addedTokens @ remA)
            ([], [], [])

    let unpairedAdded =
        added
        |> Map.toList
        |> List.filter (fun (k, _) -> not (removed.ContainsKey k))
        |> List.collect snd

    let ordinal (xs: string list) =
        xs |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    {
        Package = c.Package
        Introduced = c.Introduced
        Withdrawn = c.Withdrawn
        Added = ordinal (unpairedAdded @ leftoverAdded)
        Changed = changed |> List.sortWith (fun a b -> String.CompareOrdinal(a.Before, b.Before))
        Removed = ordinal leftoverRemoved
    }

// ─── Releases ────────────────────────────────────────────────────────

/// What the baselines said about a release. `NoBaselines` is the
/// pre-0.7.0 line: the tags exist, the public-API baselines did not yet,
/// so the section says so rather than reporting "no change".
type Surface =
    | NoBaselines
    | Baselines of PackageEntry list

/// One section's worth of data. `Tag = None` is the draft at the top —
/// the declared version's movement since the newest tag, over the
/// WORKING tree's baselines — and carries no date by construction.
type Release = {
    /// `0.23.0` — rendered in the heading.
    Version: string
    /// `v0.23.0`, or `None` for the unreleased draft.
    Tag: string option
    /// The tag's commit date, `yyyy-MM-dd`; `None` for the draft.
    Date: string option
    /// The release this one is diffed against, or `None` for the first.
    Since: string option
    Surface: Surface
    /// Repo-relative, forward-slash paths under `docs/migrations/`.
    MigrationDocs: string list
}

/// The per-package listing caps; see the header for why they differ.
type Limits = {
    AddedPerPackage: int
    ChangedPerPackage: int
    RemovedPerPackage: int
}

let defaultLimits = {
    AddedPerPackage = 20
    ChangedPerPackage = 50
    RemovedPerPackage = 50
}

// ─── Migration-doc matching ──────────────────────────────────────────

/// The version a migration doc's FILE NAME claims, if it claims one:
/// `0.23.0-…` claims exactly that version, `0.3.x-…` claims the 0.3 line.
/// A doc named by phase number (`257-…`) or by subject claims nothing —
/// the target attributes those by the interval that introduced them.
let claimedVersion (path: string) : string option =
    let file = path.Replace('\\', '/').Split('/') |> Array.last

    let prefix =
        match file.IndexOf '-' with
        | -1 -> None
        | i -> Some(file.Substring(0, i))

    match prefix with
    | Some c ->
        match c.Split '.' with
        | [| ma; mi; pa |] when
            [ ma; mi ] |> List.forall (Seq.forall Char.IsDigit)
            && (pa = "x" || pa |> Seq.forall Char.IsDigit)
            ->
            Some c
        | _ -> None
    | None -> None

/// Whether a claim (`0.23.0` or `0.3.x`) names `version`.
let private claimMatches (version: string) (claimed: string) =
    if String.Equals(claimed, version, StringComparison.OrdinalIgnoreCase) then
        true
    else
        match claimed.Split '.', version.Split '.' with
        | [| ma; mi; "x" |], [| vma; vmi; _ |] -> ma = vma && mi = vmi
        | _ -> false

/// The migration docs that belong to one release: every doc in the tree
/// whose name claims the version, plus every doc the release's interval
/// INTRODUCED that claims no version at all. A doc that claims a
/// different version is left to that version's section even when this
/// interval is the one that added it — `0.6.1-…` landing in the 0.7.0
/// interval is 0.6.1's note, not 0.7.0's. The exception is a claim no
/// release in `released` answers to (`0.4.0-…` where the first 0.4 tag
/// was 0.4.3): a doc nobody would otherwise link is attributed by the
/// interval that introduced it, like a version-less one. Ordinal-sorted
/// and distinct, so the section renders the same whatever order the
/// inputs arrived in.
let docsForRelease
    (version: string)
    (released: string seq)
    (docsInTree: string seq)
    (docsIntroduced: string seq)
    : string list =
    let normalise (p: string) = p.Replace('\\', '/')

    let answered (claim: string) =
        released |> Seq.exists (fun v -> claimMatches v claim)

    let claimed =
        docsInTree
        |> Seq.filter (fun p ->
            match claimedVersion p with
            | Some c -> claimMatches version c
            | None -> false)

    let unclaimed =
        docsIntroduced
        |> Seq.filter (fun p ->
            match claimedVersion p with
            | None -> true
            | Some c -> not (answered c))

    Seq.append claimed unclaimed
    |> Seq.map normalise
    |> Seq.distinct
    |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> List.ofSeq

// ─── Rendering ───────────────────────────────────────────────────────

let private code (s: string) = "`" + s + "`"

let private plural n (noun: string) =
    if n = 1 then
        sprintf "%d %s" n noun
    else
        sprintf "%d %ss" n noun

/// The lockstep class of a release, read the way `VerifySemVerBump` reads
/// it: the highest class any package reached.
let private classOf (entries: PackageEntry list) =
    entries
    |> List.map (fun e ->
        if not (List.isEmpty e.Removed) || not (List.isEmpty e.Changed) || e.Withdrawn then
            Breaking
        elif not (List.isEmpty e.Added) || e.Introduced then
            Additive
        else
            Unchanged)
    |> List.fold SurfaceClass.max Unchanged

let private docLink (path: string) =
    let file = path.Split('/') |> Array.last

    let name =
        if file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) then
            file.Substring(0, file.Length - 3)
        else
            file

    sprintf "- [%s](%s)" name path

let private baselineDiffHint (r: Release) (package: string) =
    match r.Since, r.Tag with
    | Some since, Some tag -> sprintf "git diff %s %s -- api-baselines/%s.approved.txt" since tag package
    | Some since, None -> sprintf "git diff %s -- api-baselines/%s.approved.txt" since package
    | None, _ -> sprintf "api-baselines/%s.approved.txt" package

let private renderSection (sb: StringBuilder) (title: string) (items: string list) =
    if not (List.isEmpty items) then
        sb.Append("### ").Append(title).Append("\n\n") |> ignore

        for line in items do
            sb.Append(line).Append('\n') |> ignore

        sb.Append('\n') |> ignore

/// One release's section. Packages sort ordinally; members within a
/// package are already ordinal from `entryOf`. Nothing here reads a clock.
let renderRelease (limits: Limits) (r: Release) : string =
    let sb = StringBuilder()

    match r.Date with
    | Some d -> sb.AppendFormat("## [{0}] — {1}\n\n", r.Version, d) |> ignore
    | None -> sb.AppendFormat("## [{0}] — unreleased\n\n", r.Version) |> ignore

    if not (List.isEmpty r.MigrationDocs) then
        sb.Append("Migration notes:\n\n") |> ignore

        for d in r.MigrationDocs |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)) do
            sb.Append(docLink d).Append('\n') |> ignore

        sb.Append('\n') |> ignore

    match r.Surface with
    | NoBaselines ->
        sb.Append(
            "_No public-API baselines were recorded at this release; the surface diff starts at the release that introduced them._\n\n"
        )
        |> ignore
    | Baselines entries ->
        let entries =
            entries
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.Package, b.Package))

        let since =
            match r.Since with
            | Some s -> sprintf "since %s" (code s)
            | None -> "at this release"

        if List.isEmpty entries then
            sb.AppendFormat("_No public-surface change {0}._\n\n", since) |> ignore
        else
            let added =
                entries |> List.sumBy (fun e -> if e.Introduced then 0 else e.Added.Length)

            let changed = entries |> List.sumBy (fun e -> e.Changed.Length)

            let removed =
                entries |> List.sumBy (fun e -> if e.Withdrawn then 0 else e.Removed.Length)

            let introduced = entries |> List.filter _.Introduced |> List.length
            let withdrawn = entries |> List.filter _.Withdrawn |> List.length

            let packages =
                [
                    if introduced > 0 then
                        sprintf "%s new" (plural introduced "package")
                    if withdrawn > 0 then
                        sprintf "%s withdrawn" (plural withdrawn "package")
                ]
                |> String.concat ", "

            sb.AppendFormat(
                "_Surface {0}: **{1}** — {2} moved; {3} added, {4} changed, {5} removed{6}._\n\n",
                since,
                SurfaceClass.name (classOf entries),
                plural entries.Length "package",
                plural added "member",
                plural changed "member",
                plural removed "member",
                (if packages = "" then "" else "; " + packages)
            )
            |> ignore

            // One package's list under a heading: the count in full, the
            // members up to the cap, and the diff that shows the rest.
            let listed (package: string) (count: string) (cap: int) (items: 'a list) (line: 'a -> string) =
                let shown = items |> List.truncate cap
                let rest = items.Length - shown.Length

                [
                    sprintf "- %s — %s:" (code package) count
                    for m in shown do
                        "  - " + line m
                    if rest > 0 then
                        sprintf "  - … and %d more — %s" rest (code (baselineDiffHint r package))
                ]

            let addedLines =
                entries
                |> List.collect (fun e ->
                    if e.Introduced then
                        [
                            sprintf "- %s — new package (%s)" (code e.Package) (plural e.Added.Length "public member")
                        ]
                    elif List.isEmpty e.Added then
                        []
                    else
                        listed e.Package (plural e.Added.Length "member") limits.AddedPerPackage e.Added code)

            let changedLines =
                entries
                |> List.collect (fun e ->
                    if List.isEmpty e.Changed then
                        []
                    else
                        listed
                            e.Package
                            (plural e.Changed.Length "member")
                            limits.ChangedPerPackage
                            e.Changed
                            (fun m -> sprintf "%s — %s → %s" (code m.Key) (code m.Before) (code m.After)))

            let removedLines =
                entries
                |> List.collect (fun e ->
                    if e.Withdrawn then
                        [
                            sprintf
                                "- %s — package withdrawn (%s); a consumer must drop the `PackageReference` before raising its pin"
                                (code e.Package)
                                (plural e.Removed.Length "public member")
                        ]
                    elif List.isEmpty e.Removed then
                        []
                    else
                        listed e.Package (plural e.Removed.Length "member") limits.RemovedPerPackage e.Removed code)

            renderSection sb "Added" addedLines
            renderSection sb "Changed" changedLines
            renderSection sb "Removed" removedLines

    sb.ToString()

/// The preamble every generated file opens with. Pinned as a value so the
/// `--check` arm and the test that reads the committed file agree on what
/// "generated by this module" looks like.
let header =
    "# Changelog\n\n"
    + "_Generated by `dotnet run --project Build.fsproj -- Changelog` (Phase 262) from the committed public-API\n"
    + "baselines under `api-baselines/`. Do not edit by hand: regenerate, and `-- Changelog --check` refuses a stale\n"
    + "file._\n\n"
    + "Each release lists the public-surface movement since the release before it, read from the baseline diff the\n"
    + "approval gate (Phase 175 / 618) decides every commit with. Counts are complete; each list is capped per\n"
    + "package — **Removed** and **Changed** generously, because they are what stops compiling, **Added** tightly —\n"
    + "and a truncated list ends with the `git diff` that shows the rest. A new or withdrawn package is one line\n"
    + "with its member count. A member both removed and added with the\n"
    + "same name in one release is reported once, as a signature change. Under this repository's SemVer-on-0.x\n"
    + "policy a `breaking` release is a MINOR bump and an `additive` one a PATCH; from 1.0.0 the ordinary table\n"
    + "applies. Where a release ships a migration note under `docs/migrations/`, it is linked from the section.\n\n"

/// The whole file: header, then every release newest first. The order is
/// taken from the input, which the target sorts by version.
let render (limits: Limits) (releases: Release list) : string =
    let sb = StringBuilder(header)

    for r in releases do
        sb.Append(renderRelease limits r) |> ignore

    sb.ToString().TrimEnd('\n') + "\n"