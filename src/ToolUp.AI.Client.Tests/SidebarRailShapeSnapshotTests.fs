// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarRailShapeSnapshotTests

// ─── Phase 613 — a structural snapshot gate for the composed shell ────
//
// [Phase 197](197-brandkit-visual-snapshot-theming-contract.md) established
// golden snapshots in this codebase, but only over `ToolUp.BrandKit`'s
// server-side ViewEngine primitives. The Elmish client shell had no
// equivalent, and the cost is on the record twice in one batch: the
// [Phase 609](609-accessible-names-for-reserved-rail-rows-in-the-narrow-rail.md)
// labelling gap and the
// [Phase 611](611-rail-placement-as-declared-data-not-incidental-bucketing.md)
// placement defect BOTH shipped through a green suite. The Phase 570 pack
// asserts *which modules are visible*; nothing asserted *how the composed
// rail is shaped*. Every other sidebar pack answers a question someone
// thought to ask. A snapshot answers the ones nobody thought to ask, which
// is the whole of its value.
//
// ── What is captured (613.A) ──
// The rail is rendered for real (`SidebarRailFixtures.render` — the shipped
// component, mounted in jsdom, hover driven by a real `mouseover`) and the
// markup is serialised to a canonical line-oriented shape:
//
//     state   = expanded rail — Product area
//     rail    = expanded
//     tabstop = row:_home:Home
//
//     section _home
//       row      Home                  button  name="Home"
//     section Analytics
//       header   -                     button  name="Analytics" expanded=true
//       reorder  sales                 div     name="Reorder Sales" role=button
//       row      sales                 button  name="Sales"
//
// One line per RAIL STOP — every element carrying `data-toolup-rail-stop`
// (Phase 612's attribute), which is exactly the set of controls the rail
// owns. The stop key encodes kind + section key + row id, so the section
// grouping, the row order and the row ids all come off the markup itself
// rather than from a parallel model that could drift away from it.
//
// A `section` heading is emitted whenever the section key CHANGES from the
// previous stop, NOT once per distinct section. That is deliberate: grouping
// by first appearance would silently tidy up interleaved sections, and
// interleaving is precisely the class of defect worth seeing.
//
// ── What is NORMALISED, and why each one has to be ──
// A snapshot that flaps gets ignored, and an ignored gate is worse than no
// gate — so the capture is an explicit ALLOWLIST of attributes (identity,
// name, ARIA state), never the raw markup:
//
//   * `class` is excluded entirely. It is presentation, it is where every
//     styling tweak lands, and including it would re-baseline this file on
//     changes that alter nothing structural. The one thing read off it is
//     the rail WIDTH, which is asserted per state rather than captured.
//   * `aria-describedby` and dnd-kit's live-region `id`s are excluded. They
//     are counter-generated per `DndContext` (`DndDescribedBy-0`, `-1`, …)
//     and the counter is process-global, so their values depend on how many
//     states rendered before this one — non-deterministic under any change
//     to case order, and a baseline that depends on test ordering is one
//     that will be regenerated until somebody stops reading it.
//   * `style` is excluded — dnd-kit writes inline transforms there.
//   * `title` is reduced to a `+title` FLAG, not its value. Phase 609 sets
//     it to the same string as `aria-label` and only where the control has
//     no visible text, so the value carries nothing the `name=` field does
//     not, while its PRESENCE carries the 609 rule.
//   * Line endings are normalised on read (`\r` stripped). `.gitattributes`
//     pins `eol=lf`, but a checkout that has not honoured it must not fail a
//     structural gate over invisible bytes.
//
// Attribute values are emitted through `quote`, which escapes `"` and `\`
// and renders a newline as `\n`, so one stop is always exactly one line and
// a name containing a quote cannot forge a second field.
//
// Selection state is NOT captured. The selected row is marked only by a
// `border-brand` class, and reading it would breach the no-presentation line
// above for one bit that Phase 611's own pack already asserts against the
// section fold. If a future phase wants it, the honest fix is an
// `aria-current` on the row — which lands in the allowlist for free.
//
// ── One baseline per state (613.B) ──
// The nine states come from `SidebarRailFixtures.railStates`, the Phase 610
// enumeration, per 613.B's "reuse … rather than defining a second one".
// They were extracted out of `SidebarRailA11yTests` into that module by this
// phase for exactly that reason. Baselines live beside this file in
// `rail-shape-baselines/<slug>.txt`, one per state, named from the state.
//
// ── Refreshing a baseline (613.C) ──
// A baseline is NEVER written by an ordinary run — an auto-overwriting
// snapshot is a rubber stamp that agrees with whatever it is shown. To
// re-record after a DELIBERATE shape change, from `src/ToolUp.AI.Client.Tests/`:
//
//     $env:TOOLUP_RAIL_SHAPE_REFRESH = "1"
//     node --import ./register-loader.mjs --test output/Program.js
//     Remove-Item Env:\TOOLUP_RAIL_SHAPE_REFRESH
//
// then READ `git diff rail-shape-baselines/` and commit the new baselines
// with the change that caused them. The refresh run deliberately FAILS the
// snapshot case after writing, so a run with the flag left set can never be
// mistaken for a green one, in CI or locally.
//
// ── KNOWN DEFECT THESE BASELINES ENCODE ──
// A baseline records what the shell DOES, not what it should do. One
// currently-tracked defect is therefore baked into the committed files and
// must not be read as the desired shape:
//
//   * `expanded rail — the no-active-team collapse` (state 9) captures NO
//     `_other` section at all — no header, no row, no chevron. That is real:
//     when `_other` is the only bucketed section `buildSections` drops its
//     title, and the expanded rail renders a header only for a TITLED
//     section and a body only for an OPEN one, so an untitled collapsed
//     `_other` renders nothing and its contents are unreachable at that
//     width. Tracked in the forge Tidy-Up bundle, not fixed here — this
//     phase builds the gate and does not change the surface it measures.
//     When it IS fixed that baseline changes, and the diff is the point.
//
// The sibling Tidy-Up finding — the area switchers passing `Icon = Html.none`,
// so the narrow-rail switcher is an empty 32×32 box — is out of this gate's
// reach in both directions: icons are not part of the captured shape, and
// the fixtures stub every icon anyway. This gate neither confirms nor
// encodes it.
//
// Pure test-tier: `IsPackable=false`, nothing ships, zero runtime cost
// (GP 13).

open Fable.Core
open Fable.Core.JsInterop
open Toolup.Sidebar
open ToolUp.Platform.Testing
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.AI.Client.Tests.SidebarRailFixtures

// ─── Baseline files ──────────────────────────────────────────────────

[<Import("readFileSync", from = "node:fs")>]
let private readFileSync (path: obj) (encoding: string) : string = jsNative

[<Import("writeFileSync", from = "node:fs")>]
let private writeFileSync (path: obj) (data: string) (encoding: string) : unit = jsNative

[<Import("existsSync", from = "node:fs")>]
let private existsSync (path: obj) : bool = jsNative

[<Import("mkdirSync", from = "node:fs")>]
let private mkdirSync (path: obj) (options: obj) : unit = jsNative

/// Resolve a path relative to THIS MODULE rather than to the process cwd.
/// The harness is documented as `node … --test output/Program.js` run from
/// the project directory, but a gate that silently reads nothing when
/// invoked from elsewhere is the exact "an empty baseline still matches"
/// failure this phase exists to prevent. `import.meta.url` is the transpiled
/// module's own location (`output/…`), so the baselines sit one level up.
/// Node's `fs` accepts a `URL` directly, so no `fileURLToPath` hop is needed.
[<Emit("new URL($0, import.meta.url)")>]
let private beside (relative: string) : obj = jsNative

/// An environment variable, with an ABSENT one normalised to `""` in the
/// emitted JS rather than in F#.
///
/// The `?? ""` is load-bearing and was measured, not guessed. Written as a
/// bare `process.env[$0]` and matched F#-side with `| null -> false`, Fable
/// emits `matchValue === defaultOf()` — a `=== null` comparison — and an
/// unset variable in Node is `undefined`, which is not `=== null`. So the
/// unset case fell through to the "refresh requested" arm and the gate
/// rewrote all nine baselines on EVERY run: a snapshot gate that agrees
/// with whatever it is shown, which is precisely what 613.C forbids. It
/// presented as a passing refresh, not as an error. Normalising in JS makes
/// the absent case indistinguishable from the empty one by construction.
[<Emit("(globalThis.process.env[$0] ?? \"\")")>]
let private envVar (name: string) : string = jsNative

[<Literal>]
let private RefreshEnvVar = "TOOLUP_RAIL_SHAPE_REFRESH"

/// The refresh command, quoted in every failure message so a reader never
/// has to come back to this file to find it.
[<Literal>]
let private RefreshCommand =
    "$env:TOOLUP_RAIL_SHAPE_REFRESH = \"1\"; node --import ./register-loader.mjs --test output/Program.js"

/// Is a refresh requested through `name`? Parameterised on the variable
/// name purely so the falsification case below can drive the ABSENT-variable
/// path over a name nothing could plausibly set — that path is where the bug
/// documented on `envVar` lived, and it is not reachable by any test that
/// only feeds this function strings.
let private refreshRequestedFor (name: string) =
    match envVar name with
    | ""
    | "0" -> false
    | _ -> true

let private refreshRequested () = refreshRequestedFor RefreshEnvVar

[<Literal>]
let private BaselineDir = "../rail-shape-baselines/"

/// A stable file name for a state. Lower-cased, every run of non-alphanumeric
/// characters folded to a single `-` (which is what absorbs the em-dashes in
/// the state names), no leading or trailing separator. Deliberately NOT
/// index-prefixed: inserting a state would then rename every later baseline
/// and bury a one-state change in a wall of file moves.
let private slug (name: string) =
    let folded =
        name
        |> Seq.map (fun c ->
            if System.Char.IsLetterOrDigit c then
                string (System.Char.ToLowerInvariant c)
            else
                "-")
        |> String.concat ""

    folded.Split('-') |> Array.filter (fun part -> part <> "") |> String.concat "-"

let private baselinePath (state: RailState) =
    beside (BaselineDir + slug state.Name + ".txt")

/// Read a baseline, `None` when it has never been recorded. `\r` is stripped
/// so a CRLF checkout compares equal — see the header note.
let private readBaseline (state: RailState) =
    let path = baselinePath state

    if existsSync path then
        Some((readFileSync path "utf8").Replace("\r", ""))
    else
        None

let private writeBaseline (state: RailState) (content: string) =
    mkdirSync (beside BaselineDir) (createObj [ "recursive" ==> true ])
    writeFileSync (baselinePath state) content "utf8"

// ─── Serialising the rendered rail (613.A) ───────────────────────────

/// An attribute value, made safe for a one-line-per-stop format.
let private quote (value: string) =
    let escaped =
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")

    "\"" + escaped + "\""

let private pad (width: int) (value: string) =
    if value.Length >= width then
        value + " "
    else
        value + String.replicate (width - value.Length) " "

/// Split a rail-stop key into its parts. `kind:section` for a section-level
/// stop, `kind:section:rowId` for a row-level one. Hand-rolled rather than
/// `String.Split(char[], int)` because only the FIRST TWO colons separate:
/// a page row id contains a `/` but a section key never contains a colon,
/// and everything after the second colon is the row id verbatim.
let private splitKey (key: string) =
    let i = key.IndexOf ':'

    if i < 0 then
        key, "?", None
    else
        let kind = key.Substring(0, i)
        let rest = key.Substring(i + 1)
        let j = rest.IndexOf ':'

        if j < 0 then
            kind, rest, None
        else
            kind, rest.Substring(0, j), Some(rest.Substring(j + 1))

/// One rail stop, as read off the rendered markup.
type private Stop = {
    /// `header` / `group` / `row` / `pin` / `hide` / `reorder` — the stop
    /// key's own prefix, which is the renderer's vocabulary and not a
    /// classification this pack invents.
    Kind: string
    SectionKey: string
    /// `None` for a section-level stop.
    RowId: string option
    Tag: string
    Name: string
    /// The disclosure state, when the element declares one — 613.A's
    /// "collapsed flag", read from `aria-expanded` rather than inferred.
    Expanded: bool option
    Role: string option
    HasTitle: bool
    IsTabStop: bool
    /// The whole key, verbatim — what the Phase 612 iff invariant compares.
    Key: string
}

let private stopsIn (node: Accessibility.A11yNode) : Stop list = [
    for e in Accessibility.elements node do
        match Map.tryFind RailStopAttr e.Attrs with
        | None -> ()
        | Some key ->
            let kind, sectionKey, rowId = splitKey key

            yield {
                Kind = kind
                SectionKey = sectionKey
                RowId = rowId
                Tag = e.Tag
                Name = Map.tryFind "aria-label" e.Attrs |> Option.defaultValue ""
                Expanded =
                    match Map.tryFind "aria-expanded" e.Attrs with
                    | Some "true" -> Some true
                    | Some "false" -> Some false
                    | _ -> None
                Role = Map.tryFind "role" e.Attrs
                HasTitle = (Map.tryFind "title" e.Attrs |> Option.isSome)
                IsTabStop = (Map.tryFind "tabindex" e.Attrs = Some "0")
                Key = key
            }
]

let private describeStop (stop: Stop) =
    let flags = [
        match stop.Expanded with
        | Some v -> yield "expanded=" + (if v then "true" else "false")
        | None -> ()

        match stop.Role with
        | Some r -> yield "role=" + r
        | None -> ()

        if stop.HasTitle then
            yield "+title"
    ]

    let head =
        "  "
        + pad 8 stop.Kind
        + pad 21 (stop.RowId |> Option.defaultValue "-")
        + pad 7 stop.Tag
        + "name="
        + quote stop.Name

    if List.isEmpty flags then
        head
    else
        head + " " + String.concat " " flags

/// The canonical textual shape of one rendered rail state — 613.A's
/// serialisation. Deterministic: every field is either a state constant or
/// an allowlisted attribute of an element the renderer drew, in document
/// order.
let private serialise (state: RailState) (html: string) =
    let stops = stopsIn (Accessibility.ofHtml html)

    let tabStop =
        match stops |> List.filter _.IsTabStop |> List.map _.Key with
        | [] -> "(NONE — the rail has no keyboard entry point)"
        | [ single ] -> single
        | many -> "(MORE THAN ONE: " + String.concat ", " many + ")"

    let header = [
        "state   = " + state.Name
        "rail    = " + (if isWideRail html then "expanded" else "narrow")
        "tabstop = " + tabStop
        ""
    ]

    // A heading per section CHANGE, not per distinct section — see the
    // header note on interleaving.
    let body =
        stops
        |> List.fold
            (fun (acc, current) stop ->
                let withHeading =
                    if current = Some stop.SectionKey then
                        acc
                    else
                        ("section " + stop.SectionKey) :: acc

                (describeStop stop :: withHeading), Some stop.SectionKey)
            ([], None)
        |> fst
        |> List.rev

    String.concat "\n" (header @ body) + "\n"

// ─── Reviewable diffs (613.C) ────────────────────────────────────────

let private lines (content: string) =
    content.Replace("\r", "").Split('\n') |> Array.toList

/// A readable structural diff. Two halves, because they answer different
/// questions: the positional walk shows WHERE the shape changed, and the set
/// difference names WHAT vanished or appeared — which is the part a reader
/// cannot recover from a positional diff when a whole section moved.
let private diffReport (expected: string) (actual: string) =
    let e = expected |> lines |> List.toArray
    let a = actual |> lines |> List.toArray

    let at (arr: string[]) i =
        if i < arr.Length then arr[i] else "(end of file)"

    let total = max e.Length a.Length

    let differing = [
        for i in 0 .. total - 1 do
            if at e i <> at a i then
                yield i
    ]

    let shown = differing |> List.truncate 20

    let positional = [
        for i in shown do
            yield sprintf "  line %d:" (i + 1)
            yield "    - baseline : " + at e i
            yield "    + rendered : " + at a i
    ]

    let elided =
        if differing.Length > shown.Length then
            [
                sprintf "  … and %d further differing line(s)." (differing.Length - shown.Length)
            ]
        else
            []

    // Blank lines and the `state = …` header carry no structure worth naming
    // twice, so the set halves report only real content.
    let structural (ls: string[]) =
        ls
        |> Array.filter (fun l -> l.Trim() <> "" && not (l.StartsWith "state   ="))
        |> Array.toList

    let onlyIn (xs: string list) (ys: string list) =
        xs |> List.filter (fun x -> not (List.contains x ys))

    let expectedStructural = structural e
    let actualStructural = structural a

    let vanished = onlyIn expectedStructural actualStructural
    let appeared = onlyIn actualStructural expectedStructural

    let named = [
        if not (List.isEmpty vanished) then
            yield "  Present in the baseline and NOT rendered (dropped or moved):"

            for l in List.truncate 20 vanished do
                yield "    - " + l.Trim()

        if not (List.isEmpty appeared) then
            yield "  Rendered and NOT in the baseline (added or moved):"

            for l in List.truncate 20 appeared do
                yield "    + " + l.Trim()
    ]

    let tail = if List.isEmpty named then [] else "" :: named
    String.concat "\n" (positional @ elided @ tail)

// ─── Failure prose ───────────────────────────────────────────────────

/// What a reader needs in front of them when the gate fires: how to read a
/// line, what each class of change means, and the refresh path. Assembled as
/// a line list rather than one continued string literal — a wall of `\`
/// continuations is where indentation goes to die.
let private howToRead =
    String.concat "\n" [
        ""
        "Each line is one rail stop: `<kind> <rowId> <tag> name=… [flags]`, grouped under the"
        "section key it belongs to, in document order."
        ""
        "  * a moved `section` block is a PLACEMENT change (the Phase 611 class)"
        "  * a vanished `row` line is a row no longer rendered in that state (the Phase 608 class)"
        "  * a changed `name=` is a LABELLING change (the Phase 609 class)"
        "  * a changed `expanded=` is a collapse-state change"
        "  * a changed `tabstop =` header means the keyboard entry point moved (Phase 612)"
        ""
        "If the change is a DEFECT, fix `Sidebar.fs` / `buildSections`. If it is INTENDED,"
        "re-record deliberately, from `src/ToolUp.AI.Client.Tests/`:"
        ""
        "    " + RefreshCommand
        ""
        "then read `git diff rail-shape-baselines/` and commit the baselines with the change that"
        "caused them. Do NOT edit a baseline by hand — it is generated output, and a hand-edit"
        "that happens to match is a gate that has stopped measuring."
    ]

let private refreshedMessage (count: int) =
    String.concat "\n" [
        sprintf "BASELINES REWRITTEN — %d state(s) re-recorded because %s is set." count RefreshEnvVar
        ""
        "This is NOT a pass. A refresh run fails on purpose, so a run with the flag left set can"
        "never be mistaken for a green gate — in CI or locally. Now:"
        ""
        "  1. Read `git diff rail-shape-baselines/` and satisfy yourself that every changed line"
        "     is a shape change you INTENDED."
        "  2. Commit the baselines with the change that caused them."
        sprintf "  3. Unset %s and re-run — the gate must be green with no flag set." RefreshEnvVar
    ]

// ─── Cases ───────────────────────────────────────────────────────────

let tests =
    testList "Phase 613 — structural snapshot gate for the composed shell" [

        // 613.B — the baseline file names have to be distinct, or two states
        // silently share one baseline: both would then compare against
        // whichever wrote last, and one of them would be checking nothing at
        // all while looking green. Cheap to assert, invisible if it breaks.
        testCase "every rail state maps to its own baseline file" (fun () ->
            let slugs = railStates |> List.map (fun st -> slug st.Name)

            Expect.equal
                (List.distinct slugs).Length
                slugs.Length
                ("two rail states slugify to the same baseline file name, so they would share one \
                  baseline and one of them would assert nothing. The slugs were: "
                 + String.concat ", " slugs
                 + ". Rename one of the states in `SidebarRailFixtures.railStates`.")

            for s in slugs do
                Expect.isTrue
                    (s <> "")
                    "a rail state slugified to an empty baseline file name — give it a name containing \
                     at least one alphanumeric character")

        // The anti-vacuity guard, and the one case without which none of the
        // others mean anything.
        //
        // Everything captured here keys off `data-toolup-rail-stop`. Rename
        // that attribute, or stop emitting it, and `stopsIn` returns an EMPTY
        // list — at which point every state serialises to a bare three-line
        // header. Against the committed baselines that fails loudly, so the
        // ordinary path is safe. But the refresh path is not: refresh the
        // baselines in that state and nine empty files match nine empty
        // renders forever. A snapshot gate's characteristic failure is not
        // disagreeing with reality, it is agreeing with nothing, and the
        // agreement looks exactly like success.
        //
        // So the shape is asserted to be NON-EMPTY independently of any
        // baseline: a real section, a real row, and a real keyboard entry
        // point in every state.
        testCase "every state's captured shape is non-vacuous" (fun () ->
            let complaints = [
                for st in railStates do
                    let captured = serialise st (render st)
                    let ls = lines captured

                    let count (prefix: string) =
                        ls |> List.filter (fun l -> l.StartsWith prefix) |> List.length

                    if count "section " = 0 then
                        yield st.Name, "no `section` heading at all"

                    if count "  row " = 0 then
                        yield st.Name, "no `row` stop at all"

                    if not (ls |> List.exists (fun l -> l.StartsWith "tabstop = row:")) then
                        yield
                            st.Name,
                            "no row-shaped `tabstop` — the rail has no keyboard entry point, or the \
                             capture found no stops to choose one from"
            ]

            if not (List.isEmpty complaints) then
                let detail = [
                    for stateName, why in complaints do
                        yield sprintf "  state \"%s\": %s" stateName why
                ]

                failwith (
                    "the captured rail shape is EMPTY or near-empty in at least one state:\n"
                    + String.concat "\n" detail
                    + "\n\nThis is the failure that would otherwise be invisible. The capture reads \
                       every element carrying `"
                    + RailStopAttr
                    + "`; if that attribute was renamed, or a renderer stopped emitting it, the \
                       serialisation collapses to a bare header — and a baseline REFRESHED in that \
                       state would match it forever while measuring nothing. Fix the attribute or the \
                       renderer; do NOT refresh the baselines to make this pass."
                ))

        // 613.A + 613.C — the gate itself.
        testCase "the composed rail matches its committed structural baseline" (fun () ->
            let refreshing = refreshRequested ()

            let outcomes = [
                for st in railStates do
                    let html = render st

                    Expect.equal
                        (isWideRail html)
                        st.Hovered
                        ("state \""
                         + st.Name
                         + "\" rendered at the wrong rail width. The hover that expands the rail is a \
                            real `mouseover` on the <aside>; if it stopped registering, every \
                            expanded-rail baseline would be silently re-recorded against the narrow \
                            tree.")

                    let rendered = serialise st html

                    if refreshing then
                        writeBaseline st rendered
                        yield Choice1Of2 st.Name
                    else
                        match readBaseline st with
                        | None ->
                            yield
                                Choice2Of2(
                                    st.Name,
                                    sprintf
                                        "  NO BASELINE RECORDED. Expected `%s%s.txt`. The rendered shape was:\n\n%s"
                                        BaselineDir
                                        (slug st.Name)
                                        rendered
                                )
                        | Some baseline when baseline <> rendered ->
                            yield Choice2Of2(st.Name, diffReport baseline rendered)
                        | Some _ -> ()
            ]

            if refreshing then
                failwith (refreshedMessage outcomes.Length)

            let failures =
                outcomes
                |> List.choose (function
                    | Choice2Of2 pair -> Some pair
                    | Choice1Of2 _ -> None)

            if not (List.isEmpty failures) then
                let detail = [
                    for stateName, report in failures do
                        yield "── state \"" + stateName + "\" ──"
                        yield report
                        yield ""
                ]

                failwith (
                    sprintf "the composed rail's structure changed in %d state(s):\n\n" failures.Length
                    + String.concat "\n" detail
                    + howToRead
                ))

        // The Phase 612 iff invariant, which its own pure-function pack could
        // only assert one side of: `railStops` is a pure projection of the
        // section data, so it could prove "every stop I emit is one I meant to
        // emit" but never "every control the renderer DREW is in the order".
        // With the real markup in hand both directions are checkable, and the
        // failure it guards is specific — a control drawn with
        // `data-toolup-rail-stop` but absent from `railStops` is focusable by
        // click and unreachable by arrow key, while a stop in the order with
        // no element is a dead key that swallows a keypress.
        testCase "every rendered rail stop is in railStops, and every stop is rendered" (fun () ->
            let mismatches = [
                for st in railStates do
                    let node = Accessibility.ofHtml (render st)
                    let inDom = stopsIn node |> List.map _.Key |> List.distinct |> List.sort

                    let projected =
                        railStops st.Hovered st.Selected (sectionsFor st)
                        |> List.collect (fun s -> s.Key :: s.Controls)
                        |> List.distinct
                        |> List.sort

                    let missingFromDom = projected |> List.filter (fun k -> not (List.contains k inDom))

                    let missingFromOrder =
                        inDom |> List.filter (fun k -> not (List.contains k projected))

                    if not (List.isEmpty missingFromDom && List.isEmpty missingFromOrder) then
                        yield st.Name, missingFromDom, missingFromOrder
            ]

            if not (List.isEmpty mismatches) then
                let detail = [
                    for stateName, missingFromDom, missingFromOrder in mismatches do
                        yield "── state \"" + stateName + "\" ──"

                        if not (List.isEmpty missingFromOrder) then
                            yield
                                "  rendered but NOT in `railStops` (focusable by click, unreachable by \
                                 arrow key):"

                            for k in missingFromOrder do
                                yield "    + " + k

                        if not (List.isEmpty missingFromDom) then
                            yield "  in `railStops` but NOT rendered (a dead key that swallows a keypress):"

                            for k in missingFromDom do
                                yield "    - " + k
                ]

                failwith (
                    sprintf "`railStops` and the rendered rail disagree in %d state(s):\n" mismatches.Length
                    + String.concat "\n" detail
                    + "\n\nThe invariant is *a stop exists if and only if the control is rendered* \
                       (Phase 612). Fix whichever side is wrong: a new control in `Sidebar.fs` needs a \
                       `dataProp.custom RailStopAttr` key AND an arm in `railStops`."
                ))

        // The roving tabindex, asserted on the rendered tree rather than on
        // the pure `railTabIndex` function — the other half Phase 612 could
        // not reach. `railTabIndex` returning 0 for exactly one key proves
        // nothing about how many ELEMENTS the renderer gave that key to.
        testCase "each rendered rail state has exactly one tab stop" (fun () ->
            let offenders = [
                for st in railStates do
                    let node = Accessibility.ofHtml (render st)

                    let zeroes =
                        Accessibility.elements node
                        |> List.filter (fun e -> Map.tryFind "tabindex" e.Attrs = Some "0")

                    if zeroes.Length <> 1 then
                        yield st.Name, zeroes |> List.map (fun e -> e.Path, Map.tryFind RailStopAttr e.Attrs)
            ]

            if not (List.isEmpty offenders) then
                let detail = [
                    for stateName, elements in offenders do
                        yield sprintf "  state \"%s\": %d element(s) carry tabindex=0" stateName elements.Length

                        for path, key in elements do
                            yield
                                sprintf
                                    "    %s (rail stop: %s)"
                                    path
                                    (key |> Option.defaultValue "NONE — not a rail stop at all")
                ]

                failwith (
                    "the rail is not a single tab stop:\n"
                    + String.concat "\n" detail
                    + "\n\nExactly one control in the rail carries `tabindex=0` (Phase 612's roving \
                       tabindex); the rest are `-1`. ZERO means the rail is unreachable by keyboard — \
                       the worse failure. MORE THAN ONE means `Tab` walks the rail one control at a \
                       time. A new control routes its tab index through `railTabIndex activeKey ownKey`, \
                       and a wrapper that splats third-party props (dnd-kit hardcodes `tabIndex: 0`) \
                       must override it AFTER the splat — see `mergeSortableProps`."
                ))

        // The gate has to be able to FAIL, and for a snapshot that is not
        // pedantry: an empty or malformed baseline still "matches", so a
        // capture that silently stopped capturing looks exactly like a rail
        // with nothing wrong. This drives the comparison and the diff over a
        // known-perturbed shape and asserts the report NAMES what moved.
        testCase "the gate rejects a rail whose shape moved, and names what moved" (fun () ->
            let fixture (sections: string list) =
                String.concat
                    "\n"
                    ([ "state   = fixture"; "rail    = expanded"; "tabstop = row:_home:Home"; "" ]
                     @ sections)
                + "\n"

            let homeSection = [ "section _home"; "  row     Home                 button name=\"Home\"" ]
            let analyticsSection = [ "section Analytics"; "  row     sales                button name=\"Sales\"" ]

            let baseline = fixture (homeSection @ analyticsSection)

            Expect.equal (diffReport baseline baseline) "" "an unchanged shape must produce an EMPTY diff"

            // A dropped section — the Phase 608 class.
            let droppedReport = diffReport baseline (fixture homeSection)

            Expect.isTrue
                (droppedReport.Contains "section Analytics")
                ("dropping a section must be NAMED in the diff, and was not. The report was:\n"
                 + droppedReport)

            Expect.isTrue
                (droppedReport.Contains "NOT rendered")
                ("a dropped line must be reported as present-in-baseline-and-not-rendered. The report was:\n"
                 + droppedReport)

            // A stripped accessible name — the Phase 609 class.
            let unnamedReport =
                diffReport baseline (baseline.Replace("name=\"Sales\"", "name=\"\""))

            Expect.isTrue
                (unnamedReport.Contains "name=\"\"")
                ("a stripped accessible name must appear in the diff as an empty name. The report was:\n"
                 + unnamedReport)

            // A reordered section — the Phase 611 class. Positionally every
            // line from the swap onward differs; the set halves must stay
            // QUIET, because nothing was added or removed. That asymmetry is
            // the whole reason the report has both halves: a reorder that also
            // fired "dropped" would train the reader to ignore that half.
            let reorderedReport = diffReport baseline (fixture (analyticsSection @ homeSection))

            Expect.isTrue
                (reorderedReport.Contains "line 5:")
                ("a reordered section must be reported from the first line that moved. The report was:\n"
                 + reorderedReport)

            Expect.isFalse
                (reorderedReport.Contains "NOT rendered")
                ("a pure REORDER adds and removes nothing, so the set halves of the report must stay \
                  silent — if they fire here they fire on every reorder and the reader stops trusting \
                  them. The report was:\n"
                 + reorderedReport)

            // And the escaping, since an accessible name is arbitrary data: a
            // row called `x" name="y` must not be able to forge a second field.
            Expect.equal
                (quote "x\" name=\"y")
                "\"x\\\" name=\\\"y\""
                "a quote inside an accessible name must be escaped, or one stop could forge two fields"

            Expect.equal (quote "a\nb") "\"a\\nb\"" "a newline in a name must not break the one-line-per-stop format")

        // The refresh gate's DEFAULT, pinned because getting it wrong is
        // silent and total. Written first as a bare `process.env[$0]` matched
        // against `| null`, this returned TRUE for an unset variable — Fable
        // compiles the F# `null` test to `=== null` and Node gives `undefined`
        // — so every ordinary run rewrote all nine baselines and then reported
        // a refresh. The gate would have passed review while measuring
        // nothing: baselines that are re-recorded from the current render can
        // never disagree with it.
        //
        // The case drives the real code path, not a stringly-typed stand-in:
        // `refreshRequestedFor` reads `process.env` for a name that is
        // genuinely absent, which is exactly the shape that failed.
        testCase "an unset environment variable does NOT request a baseline refresh" (fun () ->
            Expect.isFalse
                (refreshRequestedFor "TOOLUP_RAIL_SHAPE_REFRESH_ABSENT_PROBE")
                "an ABSENT refresh variable must not request a refresh. If this fails, every ordinary \
                 run silently re-records the baselines from the current render and the gate can never \
                 fail — see the note on `envVar`, where `process.env[x]` is `undefined` and Fable's \
                 `null` test does not match it."

            Expect.isFalse
                (refreshRequestedFor "")
                "a nameless variable must not request a refresh either — `process.env[\"\"]` is \
                 undefined and travels the same path"

            // And the inverse, so the pin above cannot be satisfied by a
            // predicate that has simply stopped returning true.
            Expect.isTrue
                (match envVar "TOOLUP_RAIL_SHAPE_REFRESH_ABSENT_PROBE" with
                 | "" -> true
                 | _ -> false)
                "an absent variable must normalise to the empty string — the `?? \"\"` in `envVar` is \
                 what makes the two cases indistinguishable, and without it this whole guard is \
                 vacuous")
    ]