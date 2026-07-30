// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarRailA11yTests

// ─── Phase 610 — the Phase 180 a11y floor over the shell rail's STATES ──
//
// [Phase 180](180-a11y-assertions-module-harness.md) already shipped the
// rule that would have caught the [Phase
// 609](609-accessible-names-for-reserved-rail-rows-in-the-narrow-rail.md)
// defect in one line — `everyInteractiveHasAccessibleName` — and the shell
// was already run through the `Minimal` profile. The rule never fired
// because it never saw the STATE where the names went missing: in the
// narrow (w-20) icon-only rail no row renders text, so every row was an
// unnamed `<button>`, while the SAME markup in the hover-expanded rail is
// named by each row's visible `<span>`. The floor existed; its fixture set
// was one tree where the surface is a family of them.
//
// This pack widens the fixtures. It introduces NO new rule vocabulary: it
// renders the real component in each state and runs the Phase 180 rules
// over the result.
//
// ── What this pack proves, and what it does not ──
// It renders `Toolup.Sidebar.Sidebar` — the actual shipped component, not
// a model of it — into a real DOM with `react-dom/client`, drives the
// hover that expands the rail with a real `mouseover`, captures the mounted
// node's markup, and hands that to `Accessibility.ofHtml`. So the tree
// the rules judge IS the markup the browser would get, attribute for
// attribute; there is no hand-maintained projection of `renderRow` that
// could drift away from `renderRow`. Deleting `prop.ariaLabel` from a rail
// row fails this pack, which is the property the phase is for.
//
// It does NOT prove anything about CSS, focus rings, contrast, or the
// browser's real accessibility tree — `ofHtml` reads DOM/ARIA shape, and
// computed style is invisible to it. And a state is covered only if it is
// in `SidebarRailFixtures.railStates`.
//
// ── Where the states and the DOM harness live (Phase 613) ──
// Both were defined HERE originally. Phase 613 needed the same nine
// states for its structural snapshot gate and its 613.B says to reuse
// this enumeration rather than write a second one, so `RailState`,
// `railStates`, `render` and `isWideRail` moved verbatim to
// `SidebarRailFixtures` and both packs now open it. The rationale for the
// DOM (the rail's narrow-vs-expanded axis is component-local
// `React.useState`, unreachable from a string renderer), for `jsdom` as
// the one added devDependency, and for this tier rather than the .NET
// Expecto runner (`Toolup.Sidebar`'s module initialiser reaches a Fable
// `importDefault`) all moved with them — see that file's header.
//
// `MustName` — the per-state list of names that must be reachable — is
// this pack's own expectation and is read nowhere else, but it rides on
// the shared `RailState` record: one list of states with one row per
// state is the property worth keeping, and two gates disagreeing about
// which states exist is the failure mode the extraction was for.

open ToolUp.Platform.Testing
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.AI.Client.Tests.SidebarRailFixtures

// ─── Known, tracked gaps ─────────────────────────────────────────────

/// A finding that is REAL, is not this phase's to fix, and is pinned so it
/// cannot be mistaken for coverage — while a NEW finding still fails.
///
/// The pin is keyed by the offending element's own attributes rather than
/// by its document path, so it survives unrelated markup moving around it
/// and cannot accidentally start covering a different element.
type private KnownGap = {
    Rule: string
    /// Why the finding stands, and where it belongs.
    Why: string
    /// Recognises the offending element from its attributes.
    Matches: Map<string, string> -> bool
}

/// Empty, and that is the point: the one gap this pack shipped with —
/// dnd-kit's drag wrapper, an unnamed focusable `role="button"` around every
/// row because `SortableItem` splats `useSortable`'s `attributes` (including
/// `tabindex=0`) onto a bare <div> — was closed by Phase 612
/// (`toolup-forge@ac6f533`), which gave the wrapper an `aria-label` and folded
/// it into the rail's roving tabindex.
///
/// Its removal was NOT bookkeeping. The `every pinned known gap still fires
/// somewhere` case below failed the moment 612 landed, which is the guard
/// working exactly as designed: a pin that has stopped firing is an exemption
/// that has silently stopped checking anything, and the pack refuses to carry
/// one. Leaving the entry here would have suppressed a real future regression
/// of the same class.
///
/// The machinery stays for the next genuine not-mine-to-fix finding.
let private knownGaps: KnownGap list = []

/// Split a state's findings into the pinned gaps and the ones that must
/// fail the pack.
let private classify (node: Accessibility.A11yNode) (findings: Accessibility.A11yFinding list) =
    findings
    |> List.partition (fun f ->
        match Accessibility.locate node f with
        | None -> false
        | Some e -> knownGaps |> List.exists (fun g -> g.Rule = f.Rule && g.Matches e.Attrs))

// ─── Cases ───────────────────────────────────────────────────────────

/// `Accessibility.StateFinding`, built with the state name in front. The
/// return annotation is what lets the fields go unqualified.
let private inState (state: string) (finding: Accessibility.A11yFinding) : Accessibility.StateFinding = {
    State = state
    Finding = finding
}

/// Every accessible name exposed by an interactive element in a tree.
let private namesIn (node: Accessibility.A11yNode) =
    Accessibility.interactiveNames node |> List.choose snd

let tests =
    testList "Phase 610 — a11y floor over the shell rail states" [

        // 610.B — the Phase 180 rules, verbatim, over every state. `Strict`
        // rather than `Minimal` because it runs the full table: the four
        // error-class rules (accessible names, image alt, labelled
        // controls, valid ARIA) PLUS heading order and colour-only state,
        // which is the set Phase 610.B names, and it makes every finding
        // fatal.
        testCase "every rail state passes the Phase 180 Strict rule set" (fun () ->
            let failures =
                railStates
                |> List.collect (fun st ->
                    let html = render st
                    let node = Accessibility.ofHtml html

                    Expect.equal
                        (isWideRail html)
                        st.Hovered
                        ("state \""
                         + st.Name
                         + "\" rendered at the wrong rail width. The hover that expands the rail is a \
                            real `mouseover` on the <aside>; if it stopped registering, every \
                            expanded-rail state would silently check the narrow tree instead.")

                    let _, real = classify node (Accessibility.check Accessibility.Strict node)
                    real |> List.map (inState st.Name))

            if not (List.isEmpty failures) then
                failwith (
                    Accessibility.reportStates Accessibility.Strict failures
                    + "\n\nEach finding is a control the shell renders in that state with no \
                       accessible name, no alt text, or invalid ARIA. The rules are Phase 180's, \
                       unchanged; what this pack adds is the STATES. Fix the control in \
                       `Sidebar.fs` — a rail row inherits the naming rule by going through \
                       `renderRow`, and a new icon-only control sets both `prop.ariaLabel` and \
                       `prop.title` (see `rowAccessibleName`)."
                ))

        // 610.C — a violation names the offending row id and the state, not
        // a boolean. This is the half a RULE cannot do: a rule reports what
        // is present and unnamed, never what is absent entirely, and a row
        // that vanished from the rail is indistinguishable from a rail with
        // nothing wrong.
        testCase "every state's expected rows are reachable BY NAME" (fun () ->
            let missing = [
                for st in railStates do
                    let names = namesIn (Accessibility.ofHtml (render st))

                    for rowId, expected in st.MustName do
                        if not (List.contains expected names) then
                            yield st.Name, rowId, expected, names
            ]

            if not (List.isEmpty missing) then
                let detail = [
                    for stateName, rowId, expected, found in missing do
                        yield
                            sprintf
                                "  state \"%s\": row `%s` should be reachable as \"%s\" — the names actually exposed were [%s]"
                                stateName
                                rowId
                                expected
                                (found |> List.map (sprintf "\"%s\"") |> String.concat "; ")
                ]

                failwith (
                    sprintf "%d rail row(s) are not reachable by name:\n" missing.Length
                    + (detail |> String.concat "\n")
                    + "\n\nEither the row lost its accessible name (fix the control — see \
                       `rowAccessibleName` in `Sidebar.fs`), or it is no longer rendered in that \
                       state at all (fix `buildSections` / the render arm), or the expectation in \
                       `railStates` is stale (fix it HERE, and say why in the same commit — this \
                       list is the contract)."
                ))

        // Phase 609's own naming rule, asserted STRUCTURALLY rather than
        // textually — which is what makes this pack "the structural home"
        // its 609.D guard pointed forward to.
        //
        // This is NOT a new a11y rule and does not belong in the
        // `Accessibility` vocabulary: it is the SHELL's convention, one
        // notch stricter than the standard. The standard rule accepts
        // `title` as an accessible name, so a rail row that carries a
        // tooltip and nothing else passes `everyInteractiveHasAccessibleName`
        // — measured, not assumed: deleting `prop.ariaLabel` from
        // `renderRow` while leaving `prop.title` in place leaves every case
        // above green. But `title` is the weakest source in the name
        // computation, several assistive technologies skip it, and it does
        // not exist on touch at all. Phase 609 therefore set `aria-label` on
        // every control and added `title` only ON TOP of it. That is a
        // decision the standard cannot express, so the pack states it.
        testCase "every rail <button> carries an explicit aria-label (Phase 609's rule)" (fun () ->
            let offenders = [
                for st in railStates do
                    let node = Accessibility.ofHtml (render st)

                    for e in Accessibility.elements node do
                        if e.Tag = "button" then
                            let label =
                                Map.tryFind "aria-label" e.Attrs
                                |> Option.map _.Trim()
                                |> Option.filter (fun v -> v <> "")

                            if label.IsNone then
                                yield st.Name, e.Path, Accessibility.accessibleName e
            ]

            if not (List.isEmpty offenders) then
                let detail = [
                    for stateName, path, fallback in offenders do
                        yield
                            sprintf
                                "  state \"%s\": <button> at %s has no aria-label (it would announce as %s)"
                                stateName
                                path
                                (match fallback with
                                 | Some n -> sprintf "\"%s\", from its title or its text — which identifies the row" n
                                 | None -> "NOTHING AT ALL")
                ]

                failwith (
                    sprintf
                        "%d rail <button>(s) carry no aria-label:
"
                        offenders.Length
                    + (detail
                       |> String.concat
                           "
")
                    + "

Every interactive control in `Sidebar.fs` sets `prop.ariaLabel` — that is                        the accessible name, and the only source that survives both a screen reader                        and a touch device. `prop.title` is added ON TOP of it where the control has                        no visible text, and is never a substitute: an element named only by its                        title passes the standard rule above while still being unusable by voice                        control and skipped by some AT. See `rowAccessibleName`'s doc-comment for                        the rule a new control follows."
                ))

        // The pins have to be load-bearing in both directions: a pinned gap
        // that has been FIXED is a stale pin, and a stale pin is how a
        // whole class quietly stops being checked.
        testCase "every pinned known gap still fires somewhere" (fun () ->
            for gap in knownGaps do
                let fires =
                    railStates
                    |> List.exists (fun st ->
                        let node = Accessibility.ofHtml (render st)
                        let pinned, _ = classify node (Accessibility.check Accessibility.Strict node)
                        not (List.isEmpty pinned))

                Expect.isTrue
                    fires
                    ("the known gap pinned for rule `"
                     + gap.Rule
                     + "` no longer fires in any rail state. If it was fixed, DELETE the pin — "
                     + "leaving it in place exempts an element class that is now clean, and the "
                     + "next real instance of it would pass. The pin reads: "
                     + gap.Why))

        // The guard has to be able to fail, and the cheapest proof is to
        // feed it a tree that is wrong in exactly the Phase 609 way. Without
        // this the pack could be inspecting nothing and would look
        // identical — which is the failure mode Phase 610 exists to close,
        // so it would be a poor one to reproduce.
        testCase "the floor rejects a rail row stripped of its name" (fun () ->
            let named =
                """<aside><div class="relative group"><button aria-label="Administration" title="Administration"><svg></svg></button></div></aside>"""

            let stripped =
                """<aside><div class="relative group"><button><svg></svg></button></div></aside>"""

            Expect.isEmpty
                (Accessibility.check Accessibility.Strict (Accessibility.ofHtml named))
                "a narrow-rail row carrying aria-label + title is clean — the shape Phase 609 \
                 established"

            let findings =
                Accessibility.check Accessibility.Strict (Accessibility.ofHtml stripped)

            Expect.equal
                (findings |> List.map _.Rule)
                [ "everyInteractiveHasAccessibleName" ]
                "removing the name must produce exactly the accessible-name finding — if this is \
                 empty the rules are inspecting nothing, and every other case in this pack is \
                 vacuous"

            let reported =
                Accessibility.reportStates
                    Accessibility.Strict
                    (findings |> List.map (inState "narrow icon-only rail — Product area"))

            Expect.isTrue
                (reported.Contains "narrow icon-only rail — Product area")
                "and the report must name the STATE — that is the part a reader cannot recover from \
                 the element path (610.C)")
    ]