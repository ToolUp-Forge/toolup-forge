// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 263 — the release-candidate channel and its soak gate: the rules
/// behind the `VerifyReleaseChannel` and `VerifyRcPromotion` targets.
///
/// **What it de-risks.** `1.0.0` is a one-way commitment: nuget.org
/// versions are immutable, and a stable major is the promise every later
/// compatibility decision is measured against. A release candidate
/// (`1.0.0-rc.N`, a SemVer-2 prerelease) lets consumers validate against
/// the frozen surface first. A consumer that did not ask for prereleases
/// never resolves one, so the stable channel is untouched by construction.
///
/// **The shape.** The tree declares the bare version it is heading for
/// (`<Version>1.0.0</Version>`); the candidate label lives only on the
/// tag (`v1.0.0-rc.1`). Promotion is a new tag, `v1.0.0`, on a descendant
/// of the soaked candidate — never a version edit, so there is no commit
/// in which the tree claims to be a candidate.
///
/// **The soak gate.** A candidate is promotion-eligible only when it has
/// been served by nuget.org for at least `soakWindow`, the public surface
/// has not moved since it (any movement — not only a break — means what
/// would be promoted is not what soaked), the commit being promoted
/// descends from it, and the Phase 257 scorecard is all-green.
///
/// **Pure and BCL-only**, for the reason `PublishedSmoke.fs` gives: the
/// targets do the reading (git, HTTP, files) and hand data in, so every
/// rule here is provable offline, in both directions, from the Build test
/// pack — which matters because the gate itself only ever runs at a
/// release. Compiled into `Build.fsproj` and source-linked into
/// `ToolUp.Platform.Build.Tests`.
module ToolUp.Forge.ReleaseChannel

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions
open ToolUp.Forge.SemVerBump

// ─── Versions on the channel ─────────────────────────────────────────

/// Which channel a release publishes to.
type Channel =
    | Stable
    /// `-rc.N`, N ≥ 1.
    | Candidate of int

type ReleaseVersion = { Core: Version; Channel: Channel }

[<RequireQualifiedAccess>]
module ReleaseVersion =
    let render (v: ReleaseVersion) =
        match v.Channel with
        | Stable -> Version.render v.Core
        | Candidate n -> sprintf "%s-rc.%d" (Version.render v.Core) n

    let tagName (v: ReleaseVersion) = "v" + render v

    /// The SemVer-2 prerelease label the pack appends, `rc.N`.
    let prereleaseLabel (v: ReleaseVersion) =
        match v.Channel with
        | Stable -> None
        | Candidate n -> Some(sprintf "rc.%d" n)

/// The ONLY two tag shapes the channel accepts. Strict on purpose: the
/// `v*.*.*` workflow trigger also matches `v1.0.0-beta`, `v1.0.0-rc1` and
/// `v1.0.0-rc.01`, and a tag this cannot classify must refuse to publish
/// rather than publish as whatever the tree happens to declare — which is
/// what every such tag did before this phase.
let private tagPattern =
    Regex(
        @"^v(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(?:-rc\.([1-9]\d{0,8}))?$",
        RegexOptions.CultureInvariant
    )

let private bareVersionPattern =
    Regex(@"^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})$", RegexOptions.CultureInvariant)

let private int' (s: string) =
    Int32.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture)

/// `v1.0.0` → stable, `v1.0.0-rc.3` → candidate 3. A leading
/// `refs/tags/` is stripped, so `GITHUB_REF` can be passed as-is.
let parseTag (name: string) : Result<ReleaseVersion, string> =
    let trimmed = if isNull name then "" else name.Trim()

    let bare =
        if trimmed.StartsWith("refs/tags/", StringComparison.Ordinal) then
            trimmed.Substring "refs/tags/".Length
        else
            trimmed

    let m = tagPattern.Match bare

    if not m.Success then
        Error(
            sprintf
                "`%s` is neither a stable release tag (`vX.Y.Z`) nor a release-candidate tag (`vX.Y.Z-rc.N`, N ≥ 1, no leading zero). Nothing publishes from it: delete the tag (`git push --delete origin %s`) and tag one of those two shapes."
                bare
                bare
        )
    else
        let core = {
            Major = int' m.Groups[1].Value
            Minor = int' m.Groups[2].Value
            Patch = int' m.Groups[3].Value
        }

        let channel =
            if m.Groups[4].Success then
                Candidate(int' m.Groups[4].Value)
            else
                Stable

        Ok { Core = core; Channel = channel }

/// The version a publish run releases.
///
/// `tagRef` is the release tag that triggered the run, or `None` for a
/// run that is not on a tag (a `workflow_dispatch` from a branch), which
/// releases the tree's declared version on the stable channel — what such
/// a dispatch has always packed.
///
/// Refused: a declared `<Version>` carrying any suffix (the label belongs
/// on the tag), a tag of neither accepted shape, and a tag whose version
/// is not the one the tree declares — before this phase such a tag
/// published the TREE's version, so `v1.0.0-rc.1` pushed on a tree
/// declaring `1.0.0` would have published a stable `1.0.0`.
let resolve (propsText: string) (tagRef: string option) : Result<ReleaseVersion, string> =
    match declaredVersionTextIn propsText with
    | Error e -> Error(sprintf "cannot read the declared SDK version — %s." e)
    | Ok raw when not (bareVersionPattern.IsMatch raw) ->
        Error(
            sprintf
                "Directory.Build.props declares <Version>%s</Version>. The lockstep version must be a bare `X.Y.Z`: a release-candidate label belongs on the release TAG (`vX.Y.Z-rc.N`), never in the tree, so that promotion is a tag and not a version edit."
                raw
        )
    | Ok raw ->
        let declared =
            match parseVersion raw with
            | Ok v -> v
            | Error e -> failwith e // unreachable: bareVersionPattern matched

        match tagRef with
        | None -> Ok { Core = declared; Channel = Stable }
        | Some ref ->
            match parseTag ref with
            | Error e -> Error e
            | Ok v when v.Core <> declared ->
                Error(
                    sprintf
                        "the tag `%s` names %s, but this tree declares <Version>%s</Version>. A release publishes what the tree declares, so the two must agree — tag the commit that declares %s, or move the tag."
                        (ReleaseVersion.tagName v)
                        (Version.render v.Core)
                        raw
                        (Version.render v.Core)
                )
            | Ok v -> Ok v

/// The environment variables through which `VerifyReleaseChannel` hands
/// a candidate's version to the pack, and which `Directory.Build.targets`
/// reads (MSBuild exposes an environment variable as a property of the
/// same name). Named once, here, and the Build test pack proves the
/// targets file reads exactly these.
let coreEnvVar = "TOOLUP_RELEASE_CORE"

let prereleaseEnvVar = "TOOLUP_RELEASE_PRERELEASE"

// ─── The tag namespace ───────────────────────────────────────────────

/// Every candidate tag for `core`, ascending by candidate number.
let candidatesOf (core: Version) (tags: string seq) : (string * int) list =
    tags
    |> Seq.choose (fun t ->
        match parseTag t with
        | Ok { Core = c; Channel = Candidate n } when c = core -> Some(t, n)
        | _ -> None)
    |> Seq.distinct
    |> Seq.sortBy snd
    |> List.ofSeq

/// The stable tag for `core`, if one exists.
let stableTagOf (core: Version) (tags: string seq) : string option =
    tags
    |> Seq.tryFind (fun t ->
        match parseTag t with
        | Ok { Core = c; Channel = Stable } -> c = core
        | _ -> false)

/// The tag the next candidate for `core` takes.
let nextCandidateTag (core: Version) (tags: string seq) : string =
    let next =
        match candidatesOf core tags with
        | [] -> 1
        | cs -> (cs |> List.map snd |> List.max) + 1

    ReleaseVersion.tagName {
        Core = core
        Channel = Candidate next
    }

/// What refuses a CANDIDATE publish, as findings — empty means it may
/// publish. Stable runs have no candidate findings.
let candidateFindings (v: ReleaseVersion) (tags: string seq) : string list =
    match v.Channel with
    | Stable -> []
    | Candidate n -> [
        match stableTagOf v.Core tags with
        | Some stable ->
            sprintf
                "%s is already released (`%s`). A release candidate precedes the version it is a candidate for; raise <Version> and tag a candidate of the next version instead."
                (Version.render v.Core)
                stable
        | None -> ()

        match candidatesOf v.Core tags |> List.filter (fun (_, m) -> m > n) with
        | [] -> ()
        | later ->
            sprintf
                "`%s` is behind %s. Candidate numbers only move forward — nuget.org would list this one as older than what consumers are already validating."
                (ReleaseVersion.tagName v)
                (later |> List.map (fst >> sprintf "`%s`") |> String.concat ", ")
      ]

// ─── The soak gate ───────────────────────────────────────────────────

/// How long a candidate must have been served by nuget.org before it may
/// be promoted. A committed constant rather than an environment knob: a
/// release gate whose threshold a run can override is not a gate. It
/// moves by a reviewed commit, like every other threshold here.
let soakWindow = TimeSpan.FromDays 14.0

/// Whether a stable release must be promoted from a soaked candidate.
///
/// Two cases. A version that HAS candidates is always promoted through
/// the gate — once consumers were asked to validate a candidate, the
/// stable release answers to what they validated. And a new major at or
/// past 1.0 (`X.0.0`, X ≥ 1) must have one: that is the one-way
/// commitment this channel exists for, and a major tagged without a
/// candidate would skip the gate by the simple act of not opting in.
/// Every other stable release (every 0.x, every 1.x minor or patch with
/// no candidate) publishes exactly as it did before this phase.
let promotionRequired (v: ReleaseVersion) (tags: string seq) : bool =
    match v.Channel with
    | Candidate _ -> false
    | Stable ->
        not (List.isEmpty (candidatesOf v.Core tags))
        || (v.Core.Major >= 1 && v.Core.Minor = 0 && v.Core.Patch = 0)

/// What nuget.org says about a candidate.
type Publication =
    | Published of DateTimeOffset
    /// Listed = false: no consumer resolving normally can pin it.
    | Unlisted
    /// nuget.org serves no such version.
    | NotPublished
    /// The read failed; the soak cannot be established either way.
    | Unreadable of string

/// The registration leaf nuget.org serves for one package version. The
/// SemVer-2 hive, because a `-rc.N` with a dotted label is SemVer-2 and
/// the legacy hive does not carry it.
let registrationLeafUrl (packageId: string) (version: string) =
    sprintf
        "https://api.nuget.org/v3/registration5-gz-semver2/%s/%s.json"
        (packageId.ToLowerInvariant())
        (version.ToLowerInvariant())

/// A registration leaf's `published` / `listed`. An absent `listed` is
/// listed (the leaf omits it for the common case in some hives).
let publicationOf (leafBody: string) : Publication =
    try
        use doc = JsonDocument.Parse leafBody
        let root = doc.RootElement

        let listed =
            match root.TryGetProperty "listed" with
            | true, v when v.ValueKind = JsonValueKind.False -> false
            | _ -> true

        match root.TryGetProperty "published" with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match
                DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            with
            | true, at when listed -> Published at
            | true, _ -> Unlisted
            | _ -> Unreadable(sprintf "the leaf's `published` value `%s` is not a timestamp" (v.GetString()))
        | _ -> Unreadable "the registration leaf carries no `published` field"
    with :? JsonException as e ->
        Unreadable(sprintf "the registration leaf is not JSON (%s)" e.Message)

/// One verdict over the candidate's packages. The SLOWEST to land sets the
/// soak start — a candidate is available when every package is — and any
/// package nuget.org does not serve, has unlisted, or could not be read
/// for, decides the whole.
let combinePublications (publications: Publication list) : Publication =
    let firstOf pick = publications |> List.tryPick pick

    match publications with
    | [] -> Unreadable "no package was read"
    | _ ->
        match
            firstOf (function
                | NotPublished -> Some NotPublished
                | _ -> None)
        with
        | Some p -> p
        | None ->
            match
                firstOf (function
                    | Unlisted -> Some Unlisted
                    | _ -> None)
            with
            | Some p -> p
            | None ->
                match
                    firstOf (function
                        | Unreadable why -> Some(Unreadable why)
                        | _ -> None)
                with
                | Some p -> p
                | None ->
                    publications
                    |> List.choose (function
                        | Published at -> Some at
                        | _ -> None)
                    |> List.max
                    |> Published

/// Everything the soak gate decides from, as data.
type PromotionEvidence = {
    /// The stable version being promoted.
    Version: ReleaseVersion
    /// The newest candidate tag for that version, if any.
    Candidate: string option
    /// Whether the commit being promoted descends from the candidate.
    CandidateIsAncestor: bool
    /// What nuget.org says about the candidate.
    Publication: Publication
    /// Per-package surface classification, candidate → this tree. The
    /// doc-coverage sidecar is excluded by the reader: it moves with
    /// documentation, not with the surface.
    SurfaceSinceCandidate: PackageChange list
    Now: DateTimeOffset
}

let private days (t: TimeSpan) =
    t.TotalDays.ToString("0.#", CultureInfo.InvariantCulture)

/// The mechanical half of the soak gate — window, surface, lineage — as
/// findings; empty means the candidate may be promoted as far as these
/// three go. The scorecard is `scorecardFindings`, kept apart because the
/// release workflow cannot compute it (see the targets in Build.fs).
let promotionFindings (window: TimeSpan) (tags: string seq) (e: PromotionEvidence) : string list =
    let version = Version.render e.Version.Core

    match e.Candidate with
    | None -> [
        sprintf
            "%s has no release candidate. A new major is promoted from a soaked candidate, never tagged directly: tag `%s` from this commit, let it soak %s day(s) on nuget.org, then promote."
            version
            (nextCandidateTag e.Version.Core tags)
            (days window)
      ]
    | Some candidate -> [
        if not e.CandidateIsAncestor then
            sprintf
                "the commit being promoted does not descend from `%s` — what soaked is a different line. Tag `%s` from this commit and soak it."
                candidate
                (nextCandidateTag e.Version.Core tags)

        match e.Publication with
        | Published at ->
            let age = e.Now - at

            if age < window then
                sprintf
                    "`%s` has soaked %s of the %s-day window (published %s UTC); it is promotion-eligible from %s UTC, not before."
                    candidate
                    (days age)
                    (days window)
                    (at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                    ((at + window).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
        | Unlisted ->
            sprintf
                "`%s` is unlisted on nuget.org, so no consumer could have validated it — it has not soaked."
                candidate
        | NotPublished ->
            sprintf
                "nuget.org does not serve `%s` — a candidate that never published has not soaked. Check its publish-nuget run."
                candidate
        | Unreadable why ->
            sprintf
                "nuget.org's record of `%s` could not be read (%s), so the soak cannot be established. Retry; the gate does not pass on an unknown."
                candidate
                why

        match e.SurfaceSinceCandidate |> List.filter (fun c -> c.Class <> Unchanged) with
        | [] -> ()
        | moved ->
            sprintf
                "the public surface moved since `%s` (%s) — what would be promoted is not what soaked. Re-roll: tag `%s` from this commit and restart the soak."
                candidate
                (moved
                 |> List.map (fun c -> sprintf "%s: %s" c.Package (SurfaceClass.name c.Class))
                 |> String.concat ", ")
                (nextCandidateTag e.Version.Core tags)
      ]

/// The Phase 257 half: the readiness scorecard must be all-green.
let scorecardFindings (ready: bool) (failingIds: string list) : string list =
    if ready then
        []
    else
        [
            sprintf
                "the Phase 257 readiness scorecard is not all-green (%s). `dotnet run --project Build.fsproj -- V1Readiness` writes docs/reference/v1-readiness.md, which says what each row needs."
                (if List.isEmpty failingIds then
                     "no row reported"
                 else
                     String.concat ", " failingIds)
        ]

let findingsReport (heading: string) (findings: string list) =
    sprintf
        "%s (%d finding(s)):%s%s"
        heading
        (List.length findings)
        Environment.NewLine
        (findings |> List.map (sprintf "  - %s") |> String.concat Environment.NewLine)

// ─── The workflow, linted ────────────────────────────────────────────

let publishJobId = "publish"

let channelTargetName = "VerifyReleaseChannel"

let private topLevelKeyPattern =
    Regex(@"^[A-Za-z_][A-Za-z0-9_-]*:", RegexOptions.Compiled)

/// The lines of the workflow's top-level `on:` block.
let triggerBlock (workflowText: string) : string list =
    workflowText.Replace("\r\n", "\n").Split('\n')
    |> Array.skipWhile (fun l -> not (l.StartsWith "on:"))
    |> fun lines ->
        if Array.isEmpty lines then
            []
        else
            (lines[0]
             :: (lines
                 |> Array.skip 1
                 |> Array.takeWhile (fun l -> not (topLevelKeyPattern.IsMatch l))
                 |> List.ofArray))

/// What is wrong with `publish-nuget.yml`'s release-channel wiring —
/// empty means well-formed. Run on every push by the Build test pack,
/// because the workflow itself only runs at a release.
///
/// Two properties. The TRIGGER stays tag-only (plus manual dispatch): a
/// branch-push, pull-request, schedule or workflow_run trigger would make
/// an ordinary push to `main` publish to nuget.org. And the publish job
/// runs the channel target BEFORE it mints a credential, so a tag the
/// channel refuses — a malformed one, one that disagrees with the tree, a
/// promotion that has not soaked — fails without a key ever existing.
let publishWorkflowFindings (workflowText: string) : string list = [
    let trigger = triggerBlock workflowText

    if List.isEmpty trigger then
        "publish-nuget.yml declares no top-level `on:` block."
    else
        let text = String.concat "\n" trigger

        if not (Regex.IsMatch(text, @"^\s+tags:", RegexOptions.Multiline)) then
            "publish-nuget.yml's `on:` block declares no `tags:` filter — a release is published from a tag push."

        for forbidden in
            [
                "branches"
                "branches-ignore"
                "pull_request"
                "pull_request_target"
                "schedule"
                "workflow_run"
                "release"
            ] do
            if Regex.IsMatch(text, sprintf @"^\s+%s\s*:" (Regex.Escape forbidden), RegexOptions.Multiline) then
                sprintf
                    "publish-nuget.yml's `on:` block declares `%s:`. Publishing is tag-triggered only (plus manual dispatch); any other trigger turns an ordinary push into a nuget.org release."
                    forbidden

    match PublishedSmoke.jobBlock publishJobId workflowText with
    | None -> sprintf "publish-nuget.yml declares no `%s` job." publishJobId
    | Some block ->
        let indexOf (needle: string) =
            block |> List.tryFindIndex (fun (l: string) -> l.Contains needle)

        match indexOf channelTargetName, indexOf "NuGet/login" with
        | None, _ ->
            sprintf
                "the `%s` job does not invoke the `%s` target. Without it a tag publishes whatever the tree declares, whatever the tag says — an `-rc.N` tag would publish a STABLE version."
                publishJobId
                channelTargetName
        | Some channel, Some login when channel > login ->
            sprintf
                "the `%s` job runs `%s` after `NuGet/login`. A release the channel refuses must fail before a credential is minted."
                publishJobId
                channelTargetName
        | Some _, _ -> ()
]