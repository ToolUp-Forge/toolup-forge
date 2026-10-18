// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 777 — trust boundaries as data.
///
/// What this pins:
///   • the list is exhaustive over the closed union, in one order;
///   • every entry is in the four-part form, and its claim state agrees
///     with the mechanisms it cites;
///   • the list cannot lag the code: every cited page exists, no awaited
///     landing has happened under an entry that still waits for it, and an
///     in-tree runtime attestor beyond the unattested one reddens the
///     hostile-host entry until it is revisited;
///   • the remoting-decoder entry IS the page's trust-boundary statement;
///   • the public page is generated from the list, byte for byte, and
///     carries no private vocabulary;
///   • every report carries the section, a bare deployment still reads
///     nothing-composed, and the decision-point statement narrows only
///     where declared routes are composed.
module ToolUp.Platform.Tests.InProcess.TrustBoundaryTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Platform.DeploymentVerification
open ToolUp.Platform.Tests.Contracts

let private repoRoot = NeutralityTokens.repoRootPath

let private repoPath (relative: string) =
    Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar))

/// The page `renderMarkdown` generates.
let private DocPath = "docs/platform/trust-boundaries.md"

/// Set to `1` to rewrite the page from the list instead of comparing.
let private RegenVariable = "TOOLUP_REGEN_TRUST_BOUNDARIES"

let private referencesOf (boundary: TrustBoundary) =
    boundary.BoundedBy
    |> List.choose (function
        | TrustMechanism.Contains(_, reference)
        | TrustMechanism.Narrows(_, reference)
        | TrustMechanism.Evidences(_, reference) -> Some reference
        | TrustMechanism.Awaits _
        | TrustMechanism.Upstream _
        | TrustMechanism.Stated -> None)

let private collapse (text: string) = Regex.Replace(text.Trim(), @"\s+", " ")

// ── the closed list ───────────────────────────────────────────────────

let private closedList =
    testList "the closed list" [

        test "ids names every TrustBoundaryId case exactly once" {
            let cases =
                FSharpType.GetUnionCases(typeof<TrustBoundaryId>)
                |> Array.map _.Name
                |> Set.ofArray

            let listed = TrustBoundaries.ids |> List.map string

            Expect.equal (List.length listed) (List.distinct listed |> List.length) "no boundary is listed twice"
            Expect.equal (Set.ofList listed) cases "every case of the union is listed"
        }

        test "all is the entries of ids, in order, each under its own id" {
            Expect.equal (TrustBoundaries.all |> List.map _.Id) TrustBoundaries.ids "one entry per id, in order"
        }

        test "labels are distinct and kebab-case" {
            let labels = TrustBoundaries.ids |> List.map TrustBoundaries.label
            Expect.equal (List.distinct labels) labels "labels are distinct"

            for label in labels do
                Expect.isTrue (Regex.IsMatch(label, "^[a-z]+(-[a-z]+)*$")) $"'{label}' is kebab-case"
        }

        test "every entry is in the four-part form and names its mechanism" {
            for boundary in TrustBoundaries.all do
                let name = TrustBoundaries.label boundary.Id

                for field, value in
                    [
                        "Title", boundary.Title
                        "Crosses", boundary.Crosses
                        "Mediates", boundary.Mediates
                        "Statement", boundary.Statement
                        "NotClaimed", boundary.NotClaimed
                    ] do
                    Expect.isFalse (String.IsNullOrWhiteSpace value) $"{name}: {field} is stated"

                Expect.isNonEmpty boundary.BoundedBy $"{name}: names what bounds it"
        }

        test "a claim state agrees with the mechanisms its entry cites" {
            for boundary in TrustBoundaries.all do
                let name = TrustBoundaries.label boundary.Id

                let contains =
                    boundary.BoundedBy
                    |> List.exists (function
                        | TrustMechanism.Contains _ -> true
                        | _ -> false)

                match boundary.Claim with
                | TrustClaim.ContainedUnder _ ->
                    Expect.isTrue contains $"{name}: a contained boundary cites the control that contains it"
                | TrustClaim.NotClaimed
                | TrustClaim.StatedHatch ->
                    Expect.isFalse
                        contains
                        $"{name}: a boundary that cites a containing control must say it is contained — move its claim state"
        }

        test "the containment profile labels are the composition profile's" {
            Expect.equal
                (TrustBoundaries.profileLabel ContainmentProfile.Standard)
                (CompositionProfile.label CompositionProfile.Standard)
                "standard"

            Expect.equal
                (TrustBoundaries.profileLabel ContainmentProfile.Verified)
                (CompositionProfile.label CompositionProfile.Verified)
                "verified"
        }

        test "the claim states the shipped containment phases set" {
            // The ledger of the target's progress: each of these phases moved
            // exactly one entry, and a regression to `NotClaimed` is red here.
            let claimOf id = (TrustBoundaries.entry id).Claim

            Expect.equal
                (claimOf TrustBoundaryId.HandResolvedSubstrate)
                (TrustClaim.ContainedUnder ContainmentProfile.Verified)
                "770 — hand-resolved substrate"

            Expect.equal
                (claimOf TrustBoundaryId.OutboundEgress)
                (TrustClaim.ContainedUnder ContainmentProfile.Verified)
                "772 — outbound egress"

            Expect.equal
                (claimOf TrustBoundaryId.CustomRoutes)
                (TrustClaim.ContainedUnder ContainmentProfile.Standard)
                "773 — declared routes, under every profile"

            Expect.equal
                (claimOf TrustBoundaryId.HostileHostWithRoot)
                TrustClaim.NotClaimed
                "774 — the slot exists; nothing is observed above the record's credit"

            Expect.equal
                (claimOf TrustBoundaryId.RemotingDecoders)
                (TrustClaim.ContainedUnder ContainmentProfile.Verified)
                "785 / 799 / 801 — both wires"
        }
    ]

// ── the list cannot lag the code ──────────────────────────────────────

/// Production `IRuntimeAttestor` implementations under `src/`, test
/// projects excluded — the same object-expression count the conformance
/// ratchet reads.
let private runtimeAttestorImplementations () =
    Directory.EnumerateFiles(repoPath "src", "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (fun path ->
        let normalised = path.Replace('\\', '/')

        not (normalised.Contains ".Tests/")
        && not (normalised.Contains "/bin/")
        && not (normalised.Contains "/obj/"))
    |> Seq.sumBy (fun path -> Regex.Matches(File.ReadAllText path, @"\bIRuntimeAttestor\s+with\b").Count)

let private cannotLag =
    testList "the list cannot lag the code" [

        test "every page an entry cites exists in this repository" {
            for boundary in TrustBoundaries.all do
                for reference in referencesOf boundary do
                    Expect.isTrue
                        (File.Exists(repoPath reference))
                        $"{TrustBoundaries.label boundary.Id} cites '{reference}', which does not exist"
        }

        test "no entry still awaits a landing that has happened" {
            for boundary in TrustBoundaries.all do
                for mechanism in boundary.BoundedBy do
                    match mechanism with
                    | TrustMechanism.Awaits(phase, landsAt) ->
                        Expect.isFalse
                            (File.Exists(repoPath landsAt))
                            $"Phase {phase} has landed ('{landsAt}' exists) but the {TrustBoundaries.label boundary.Id} entry still awaits it. Move the entry in the same change-set: its claim state, its not-claimed line, and the mechanism (Awaits becomes Contains or Narrows)."
                    | _ -> ()
        }

        test "an in-tree runtime attestor beyond the unattested one moves the hostile-host entry" {
            // Phase 774 shipped the slot with exactly one in-tree provider,
            // which observes nothing. A second provider can raise a boot to
            // runtime-quoted, and the entry's "nothing above isolated-signer
            // is observed" would then be stale.
            let count = runtimeAttestorImplementations ()

            Expect.isGreaterThanOrEqual count 1 "the scan finds the unattested provider (the probe is live)"

            if (TrustBoundaries.entry TrustBoundaryId.HostileHostWithRoot).Claim = TrustClaim.NotClaimed then
                Expect.equal
                    count
                    1
                    "a second in-tree IRuntimeAttestor exists: revisit the hostile-host-with-root entry (its mediation and claim) in the same change-set"
        }

        test "the remoting-decoder entry is the trust-boundary statement on its page, verbatim" {
            let lines =
                File.ReadAllLines(repoPath "docs/platform/remoting-decoder-algebra.md")
                |> List.ofArray

            let afterHeading =
                lines
                |> List.skipWhile (fun line -> not (line.StartsWith "## 7."))
                |> List.skipWhile (fun line -> not (line.StartsWith "```"))
                |> List.skip 1

            let block = afterHeading |> List.takeWhile (fun line -> not (line.StartsWith "```"))

            Expect.isNonEmpty block "the page carries the statement in its section 7 block"

            let fields =
                block
                |> List.fold
                    (fun (acc: (string * string) list) line ->
                        let m = Regex.Match(line, "^(Crosses|Mediates|Claim|Not claimed):\s*(.*)$")

                        if m.Success then
                            acc @ [ m.Groups[1].Value, m.Groups[2].Value ]
                        else
                            match List.rev acc with
                            | (key, value) :: rest -> List.rev rest @ [ key, value + " " + line ]
                            | [] -> acc)
                    []
                |> List.map (fun (key, value) -> key, collapse value)
                |> Map.ofList

            let entry = TrustBoundaries.entry TrustBoundaryId.RemotingDecoders
            Expect.equal fields["Crosses"] (collapse entry.Crosses) "Crosses"
            Expect.equal fields["Mediates"] (collapse entry.Mediates) "Mediates"
            Expect.equal fields["Claim"] (collapse entry.Statement) "Claim"
            Expect.equal fields["Not claimed"] (collapse entry.NotClaimed) "Not claimed"
        }
    ]

// ── the public page ───────────────────────────────────────────────────

let private publicPage =
    testList "the public page" [

        test "docs/platform/trust-boundaries.md is generated from the list, byte for byte" {
            let expected = TrustBoundaries.renderMarkdown ()
            let path = repoPath DocPath

            if Environment.GetEnvironmentVariable RegenVariable = "1" then
                File.WriteAllText(path, expected)

            Expect.isTrue (File.Exists path) $"'{DocPath}' exists"

            Expect.equal
                (File.ReadAllText path)
                expected
                $"'{DocPath}' is stale: regenerate it with {RegenVariable}=1 and this list, never by hand"
        }

        test "the page names every boundary and every not-claimed line" {
            let page = TrustBoundaries.renderMarkdown ()

            for boundary in TrustBoundaries.all do
                Expect.stringContains page boundary.Title "title"
                Expect.stringContains page boundary.NotClaimed "not-claimed line"
        }

        test "the page carries no private vocabulary" {
            NeutralityTokens.assertNoBannedTokens DocPath (TrustBoundaries.renderMarkdown ())
            NeutralityTokens.skipUnlessExternalSource ()
        }
    ]

// ── the report section ────────────────────────────────────────────────

let private run (evidence: IDeploymentVerificationEvidence) =
    DeploymentVerificationReport.buildReport evidence "probe" DateTime.UnixEpoch
    |> Async.RunSynchronously

let private sectionOf (report: DeploymentVerificationReport) =
    report.Sections
    |> List.tryFind (fun s -> s.Id = TrustBoundariesSection)
    |> Option.defaultWith (fun () -> failtest "the trust-boundaries section is missing")

let private decisionPoint (report: DeploymentVerificationReport) =
    report.NotProved |> List.find (fun s -> s.Id = "gate-is-a-decision-point")

let private sampleRoutes = {
    DeclaredRoutes = [
        {
            RouteOwner = ComponentId.ofModule "reporter"
            RouteKey = "GET /api/report"
            RouteEnvelope = Some "pure"
            RouteEgress = Some "declared{}"
        }
    ]
}

let private verifiedEgress: EgressIntegrity = {
    EgressProfile = "verified"
    EgressPosture = EgressPosture.DenyAll
    EgressComponents = []
    EgressDenials = []
    EgressDenialsSinceBoot = 0
    EgressLabelVocabulary = "1"
}

let private reportSection =
    testList "the report section" [

        test "a bare deployment carries every entry and still reads nothing-composed" {
            let report = run DeploymentVerificationEvidence.none
            let section = sectionOf report

            Expect.equal (VerificationSectionVerdict.label section.Verdict) "not-composed" "no mechanism is in force"

            Expect.equal
                report.Outcome
                DeploymentVerificationOutcome.NothingComposed
                "the section must not make nothing-composed unreachable"

            for boundary in TrustBoundaries.all do
                let label = TrustBoundaries.label boundary.Id

                Expect.contains
                    section.Findings
                    (sprintf "%s [%s] %s" label (TrustBoundaries.claimLabel boundary.Claim) boundary.Title)
                    $"{label} is on the report"

                Expect.contains
                    section.Findings
                    (sprintf "%s not claimed: %s" label boundary.NotClaimed)
                    $"{label}'s not-claimed line is on the report"

            Expect.isNone (decisionPoint report).Narrowing "nothing narrows the decision-point statement"
        }

        test "it is the last section" {
            let report = run DeploymentVerificationEvidence.none
            Expect.equal (List.last report.Sections).Id TrustBoundariesSection "appended last"
        }

        test "declared routes are contained here, named, and narrow the decision-point statement" {
            let report =
                run (
                    DeploymentVerificationEvidence.none
                    |> DeploymentVerificationEvidence.withRouteEffects (Some sampleRoutes)
                )

            let section = sectionOf report

            Expect.equal
                (VerificationSectionVerdict.label section.Verdict)
                "verified"
                "one mechanism in force, none partial"

            Expect.exists
                section.Findings
                (fun f -> f.StartsWith "custom-routes here: in force")
                "the custom-routes entry is in force"

            Expect.exists
                section.Findings
                (fun f -> f.Contains "GET /api/report" && f.Contains "envelope=pure")
                "the declared route is named"

            let narrowing = (decisionPoint report).Narrowing
            Expect.isSome narrowing "declared routes narrow the decision-point statement"
            Expect.stringContains narrowing.Value "undeclared route" "and the narrowing names what still stands"
        }

        test "a mechanism its own section only observes reads as partly in force" {
            let unenforced = {
                verifiedEgress with
                    EgressProfile = "standard"
                    EgressPosture = EgressPosture.Unenforced
            }

            let report =
                run (
                    DeploymentVerificationEvidence.none
                    |> DeploymentVerificationEvidence.withEgress (Some unenforced)
                )

            Expect.equal (VerificationSectionVerdict.label (sectionOf report).Verdict) "observed" "partial"

            Expect.exists
                (sectionOf report).Findings
                (fun f -> f.StartsWith "outbound-egress here: partly in force")
                "egress is partly in force"
        }

        test "a verified egress section puts the egress boundary in force" {
            let report =
                run (
                    DeploymentVerificationEvidence.none
                    |> DeploymentVerificationEvidence.withEgress (Some verifiedEgress)
                )

            Expect.equal (VerificationSectionVerdict.label (sectionOf report).Verdict) "verified" "in force"

            Expect.exists
                (sectionOf report).Findings
                (fun f -> f.StartsWith "outbound-egress here: in force")
                "egress is in force"
        }

        test "a module's declared routes reach the report through the container" {
            let app =
                ServerApp.empty
                |> ServerApp.addModule (
                    ServerModule.create "reporter"
                    |> ServerModule.withRouteEffect
                        "get"
                        "/API/Report"
                        (RouteEffectDeclaration.envelope CompanionCapability.pure')
                )
                |> ServerApp.addModule (ServerModule.create "plain")

            let services =
                match app.Extensions.ServiceConfig with
                | Some register -> register (ServiceCollection())
                | None -> ServiceCollection() :> IServiceCollection

            let evidence =
                DeploymentVerificationReport.resolveEvidence (services.BuildServiceProvider())

            match DeploymentVerificationEvidence.routeEffectsOf evidence with
            | Some { DeclaredRoutes = [ route ] } ->
                Expect.equal route.RouteKey "GET /api/report" "normalised as the containment table keys it"
                Expect.equal route.RouteEgress (Some "declared{}") "a Pure envelope permits no outbound call"
            | other -> failtestf "expected one declared route, got %A" other
        }

        test "a composition declaring no route registers nothing" {
            let app = ServerApp.empty |> ServerApp.addModule (ServerModule.create "plain")

            let services =
                match app.Extensions.ServiceConfig with
                | Some register -> register (ServiceCollection())
                | None -> ServiceCollection() :> IServiceCollection

            let evidence =
                DeploymentVerificationReport.resolveEvidence (services.BuildServiceProvider())

            Expect.isNone (DeploymentVerificationEvidence.routeEffectsOf evidence) "no member"
        }
    ]

// ── the route-effect evidence member's contract pack ─────────────────

let private contractBindings =
    testList "IRouteEffectEvidence bindings" [
        IRouteEffectEvidenceContract.tests "DeploymentVerificationEvidence.none" None (fun () ->
            DeploymentVerificationEvidence.none)

        IRouteEffectEvidenceContract.tests "DeploymentVerificationEvidence.create (no facets)" None (fun () ->
            DeploymentVerificationEvidence.create None None None None None)

        IRouteEffectEvidenceContract.tests
            "DeploymentVerificationEvidence.withRouteEffects"
            (Some sampleRoutes)
            (fun () ->
                DeploymentVerificationEvidence.none
                |> DeploymentVerificationEvidence.withRouteEffects (Some sampleRoutes))

        IRouteEffectEvidenceContract.tests
            "withGroundingContinuity carries RouteEffects through"
            (Some sampleRoutes)
            (fun () ->
                DeploymentVerificationEvidence.none
                |> DeploymentVerificationEvidence.withRouteEffects (Some sampleRoutes)
                |> DeploymentVerificationEvidence.withGroundingContinuity None)

        IRouteEffectEvidenceContract.tests "withEgress carries RouteEffects through" (Some sampleRoutes) (fun () ->
            DeploymentVerificationEvidence.none
            |> DeploymentVerificationEvidence.withRouteEffects (Some sampleRoutes)
            |> DeploymentVerificationEvidence.withEgress (Some verifiedEgress))
    ]

let tests =
    testSequenced (
        testList "Phase 777 — trust boundaries as data" [
            closedList
            cannotLag
            publicPage
            reportSection
            contractBindings
        ]
    )