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
/// **Majors only (operator decision 2026-09-22).** Release candidates exist
/// for major releases from 1.0 on and for nothing else: `vX.0.0-rc.N` with
/// X >= 1. There is nothing to soak before 1.0, and a minor or patch
/// publishes stable directly.
///
/// **The soak gate.** A major's candidate is promotion-eligible only when
/// it has been served by nuget.org for at least the soak window (14 days
/// unless `release-channel.json` records a per-release override with its
/// reason), no BREAKING surface change has landed since it (additive
/// growth does not block), the commit being promoted descends from it, and
/// the Phase 257 scorecard is all-green.
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

/// The ONLY two tag shapes the channel accepts (a candidate is further
/// restricted to a major, below). Strict on purpose: the
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

/// A major release from 1.0 on — `X.0.0`, X >= 1 — the only kind that has
/// release candidates and the only kind promoted through the soak gate.
let isMajorRelease (core: Version) =
    core.Major >= 1 && core.Minor = 0 && core.Patch = 0

/// `v1.0.0` → stable, `v1.0.0-rc.3` → candidate 3; `v0.23.0-rc.1` and
/// `v1.1.0-rc.1` are refused (candidates are majors only). A leading
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
                "`%s` is neither a stable release tag (`vX.Y.Z`) nor a release-candidate tag of a major (`vX.0.0-rc.N`, X ≥ 1, N ≥ 1, no leading zero). Nothing publishes from it: delete the tag (`git push --delete origin %s`) and tag one of those two shapes."
                bare
                bare
        )
    else
        let core = {
            Major = int' m.Groups[1].Value
            Minor = int' m.Groups[2].Value
            Patch = int' m.Groups[3].Value
        }

        if m.Groups[4].Success && not (isMajorRelease core) then
            Error(
                sprintf
                    "`%s` is a release candidate for %s, which is not a major release. Release candidates exist only for majors from 1.0 on (`vX.0.0-rc.N`, X >= 1): before 1.0 there is nothing to soak, and a minor or patch publishes stable directly. Nothing publishes from it: delete the tag (`git push --delete origin %s`) and tag `v%s`."
                    bare
                    (Version.render core)
                    bare
                    (Version.render core)
            )
        elif m.Groups[4].Success then
            Ok {
                Core = core
                Channel = Candidate(int' m.Groups[4].Value)
            }
        else
            Ok { Core = core; Channel = Stable }

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

/// The soak window when nothing overrides it.
let defaultSoakDays = 14

/// The committed file that configures the soak window, at the repo root
/// beside `v1-readiness.json`. Committed rather than an environment knob,
/// because the release workflow — which re-checks the window on the stable
/// tag — sees only what the tagged commit carries: a window a run could
/// override from its environment would be one the tag push cannot see.
///
///     { "soakDays": 14,
///       "overrides": [ { "version": "1.0.0", "soakDays": 7,
///                        "reason": "why this release soaks differently" } ] }
///
/// `soakDays` is the default (absent: 14). An override applies to one major
/// and MUST carry a non-blank reason; the targets print the window in force
/// and where it came from, so the reason is in every run's log as well as
/// in the commit that introduced it.
let releaseChannelFileName = "release-channel.json"

/// The window in force for one release, and where it came from.
type SoakWindow = { Window: TimeSpan; Source: string }

let private soakDaysOf (where: string) (el: JsonElement) : Result<int, string> =
    match el.ValueKind with
    | JsonValueKind.Number ->
        match el.TryGetInt32() with
        | true, n when n >= 1 -> Ok n
        | _ -> Error(sprintf "%s must be a whole number of days, at least 1" where)
    | _ -> Error(sprintf "%s must be a whole number of days, at least 1" where)

let private unknownKeysOf (allowed: string list) (el: JsonElement) =
    el.EnumerateObject()
    |> Seq.map _.Name
    |> Seq.filter (fun k -> not (List.contains k allowed))
    |> List.ofSeq

/// One override entry: (version, days, reason).
let private parseOverride (index: int) (o: JsonElement) : Result<Version * int * string, string> =
    let at = sprintf "overrides[%d]" index

    let str (name: string) =
        match o.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""

    if o.ValueKind <> JsonValueKind.Object then
        Error(sprintf "%s must be an object" at)
    else
        match unknownKeysOf [ "version"; "soakDays"; "reason" ] o with
        | _ :: _ as unknown -> Error(sprintf "%s carries unknown key(s) %s" at (String.Join(", ", unknown)))
        | [] ->
            let version = str "version"
            let reason = str "reason"

            let parsed =
                if bareVersionPattern.IsMatch version then
                    parseVersion version
                else
                    Error "not bare"

            match parsed with
            | Error _ -> Error(sprintf "%s.version must be a bare X.Y.Z version" at)
            | Ok v when not (isMajorRelease v) ->
                Error(
                    sprintf
                        "%s names %s, which is not a major release — only a major (X.0.0, X >= 1) has a candidate to soak"
                        at
                        version
                )
            | Ok _ when String.IsNullOrWhiteSpace reason ->
                Error(sprintf "%s (%s) has no reason — an override of the soak window must say why" at version)
            | Ok v ->
                match o.TryGetProperty "soakDays" with
                | true, d -> soakDaysOf (at + ".soakDays") d |> Result.map (fun n -> v, n, reason.Trim())
                | _ -> Error(sprintf "%s.soakDays is missing" at)

/// The soak window for `core`, from the committed file's text (`None` when
/// the file is absent). Refuses what it cannot read rather than guessing:
/// unknown keys, a non-positive or non-integer day count, an override
/// without a reason, for a version that is not a major, or twice for one.
let soakWindowFor (fileText: string option) (core: Version) : Result<SoakWindow, string> =
    let fail (msg: string) =
        Error(sprintf "%s: %s" releaseChannelFileName msg)

    match fileText with
    | None ->
        Ok {
            Window = TimeSpan.FromDays(float defaultSoakDays)
            Source = sprintf "the default (%d days; %s is absent)" defaultSoakDays releaseChannelFileName
        }
    | Some text ->
        try
            use doc = JsonDocument.Parse text
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                fail "must be a JSON object"
            else
                match unknownKeysOf [ "soakDays"; "overrides" ] root with
                | _ :: _ as unknown ->
                    fail (
                        sprintf
                            "unknown key(s) %s — the known keys are soakDays, overrides"
                            (String.Join(", ", unknown))
                    )
                | [] ->
                    let baseDays =
                        match root.TryGetProperty "soakDays" with
                        | true, el ->
                            soakDaysOf "soakDays" el
                            |> Result.map (fun n -> n, sprintf "%s soakDays (%d days)" releaseChannelFileName n)
                        | _ -> Ok(defaultSoakDays, sprintf "the default (%d days)" defaultSoakDays)

                    let overrides =
                        match root.TryGetProperty "overrides" with
                        | true, el when el.ValueKind = JsonValueKind.Array ->
                            let results = el.EnumerateArray() |> Seq.mapi parseOverride |> List.ofSeq

                            match
                                results
                                |> List.tryPick (function
                                    | Error e -> Some e
                                    | Ok _ -> None)
                            with
                            | Some e -> Error e
                            | None ->
                                results
                                |> List.choose (function
                                    | Ok x -> Some x
                                    | Error _ -> None)
                                |> Ok
                        | true, _ -> Error "overrides must be an array"
                        | _ -> Ok []

                    match baseDays, overrides with
                    | Error e, _
                    | _, Error e -> fail e
                    | Ok(n, source), Ok os ->
                        match os |> List.filter (fun (v, _, _) -> v = core) with
                        | [] ->
                            Ok {
                                Window = TimeSpan.FromDays(float n)
                                Source = source
                            }
                        | [ (_, d, reason) ] ->
                            Ok {
                                Window = TimeSpan.FromDays(float d)
                                Source =
                                    sprintf
                                        "the %s override for %s (%d days) — reason: %s"
                                        releaseChannelFileName
                                        (Version.render core)
                                        d
                                        reason
                            }
                        | _ -> fail (sprintf "more than one override names %s" (Version.render core))
        with :? JsonException as e ->
            fail (sprintf "is not JSON (%s)" e.Message)

/// Whether a stable release must be promoted from a soaked candidate: a
/// major release from 1.0 on (`X.0.0`, X >= 1), and nothing else. A major
/// cannot be tagged without a soaked candidate — that is the one-way
/// commitment this channel exists for, and one tagged directly would skip
/// the gate by the simple act of not opting in. Every 0.x release and every
/// minor or patch publishes stable with no soak check: candidates exist
/// only for majors (operator decision 2026-09-22), so there is nothing else
/// that could have soaked.
let promotionRequired (v: ReleaseVersion) : bool =
    match v.Channel with
    | Candidate _ -> false
    | Stable -> isMajorRelease v.Core

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

        // Additive growth does not block (operator decision 2026-09-22):
        // a consumer who validated the candidate loses nothing to it. Only
        // a break re-rolls, by SemVerBump's classification, the same one
        // the release bump check applies.
        match e.SurfaceSinceCandidate |> List.filter (fun c -> c.Class = Breaking) with
        | [] -> ()
        | broken ->
            sprintf
                "a BREAKING public-surface change landed since `%s` (%s) — consumers validated a surface this release no longer has. Re-roll: tag `%s` from this commit and restart the soak."
                candidate
                (broken |> List.map _.Package |> String.concat ", ")
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