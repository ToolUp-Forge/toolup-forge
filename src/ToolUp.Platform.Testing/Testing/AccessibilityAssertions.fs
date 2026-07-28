// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Testing

// ─── Phase 180 — accessibility assertions for the module harness ───────
//
// Phase 11a shipped `ModuleHarness` / `ServerHarness` so a module can unit-
// test its MVU and server logic, but there was no ACCESSIBILITY floor in
// that harness: a module could pass its whole test pack while shipping
// unlabelled buttons, alt-less images, unlabelled form controls or invalid
// ARIA, and nothing caught it until a real user with assistive tech did.
// This module is that floor — an axe-style rule set that runs in-process
// under Expecto, with no browser, no npm, and no runtime surface.
//
// ── What the rules run over ──
// A deliberately small, portable node model (`A11yNode`) rather than a
// renderer's own tree type. That choice is forced and is worth stating
// plainly: a consumer module's `view` is a Fable-tier Feliz
// `ReactElement`, which CANNOT be evaluated by the .NET Expecto runner
// (the same constraint `AriaPropTests` records — the Fable-only client
// tier is checked textually, not by rendering). So the assertions consume
// a tree the caller can actually produce on whichever side it runs:
//
//   * .NET / SSR — render a `Giraffe.ViewEngine` / `Feliz.ViewEngine`
//     view to its HTML fragment and hand it to `ofHtml` (the same
//     fragment-string seam `HydrationParity` and `HostedTreeA11y` use).
//   * Fable / browser — hand a mounted node's `outerHTML` to `ofHtml`,
//     or build the `A11yNode` directly from the view's own shape.
//   * Hand-built — `el` / `elem` / `text` construct a tree literally,
//     which is what the shipped fixtures and most unit tests do.
//
// The rules are therefore renderer-agnostic (GP 1): they check DOM/ARIA
// shape and name no domain, no framework and no tree language.
//
// ── Severity + profile ──
// Every finding carries an `A11ySeverity`. `Minimal` (the default) runs
// the `A11yError`-class table stakes — accessible names, image alt text,
// labelled controls, valid ARIA — the bar below which a surface is simply
// broken for assistive tech. `Strict` additionally runs the
// `A11yWarning`-class heuristics (heading order, colour-only state) AND
// treats every finding as fatal, so a module can opt into the higher bar.
//
// Pure test/build infrastructure: BCL-only (a hand-rolled scanner, no
// regex — so it packs under `fable/` and runs on both sides), zero runtime
// surface, byte-for-byte absent from any consumer deployment (GP 13).
// Side-effect-free over an immutable tree (GP 5).

[<RequireQualifiedAccess>]
module Accessibility =

    open System

    // ─── node model ───────────────────────────────────────────────────

    /// A rendered node — the portable shape every rule runs over.
    /// `Element` carries a lower-cased tag name, a lower-cased attribute
    /// map, and its children in document order; `Text` carries a text run.
    /// A multi-root fragment is wrapped in an `Element` tagged
    /// `"#fragment"`, which the rules treat as transparent.
    type A11yNode =
        | Element of tag: string * attrs: Map<string, string> * children: A11yNode list
        | Text of string

    /// How badly a finding breaks the surface. `A11yError` is a
    /// table-stakes failure (assistive tech cannot use the element at
    /// all); `A11yWarning` is a heuristic worth a human look. Named with
    /// the `A11y` prefix deliberately — a bare `Error` would shadow
    /// `Result`'s case at every call site.
    type A11ySeverity =
        | A11yError
        | A11yWarning

    /// The bar a caller opts into. `Minimal` = the `A11yError`-class
    /// table-stakes rules (the default). `Strict` = those plus the
    /// `A11yWarning`-class heuristics, with every finding fatal.
    type A11yProfile =
        | Minimal
        | Strict

    /// One accessibility failure: which rule fired, which element it
    /// fired on (a document path, so the fix targets an exact node), and
    /// what is wrong.
    type A11yFinding = {
        /// The rule function's name, e.g. `everyImageHasAlt`.
        Rule: string
        /// Document path to the offending element, e.g.
        /// `section#main > nav > button[1]`.
        Path: string
        /// Human-readable diagnostic naming the missing affordance.
        Message: string
        Severity: A11ySeverity
    }

    // ─── node constructors ────────────────────────────────────────────

    let private lowerMap (attrs: (string * string) list) =
        attrs |> List.map (fun (k, v) -> k.ToLowerInvariant(), v) |> Map.ofList

    /// An element with attributes and children.
    let el (tag: string) (attrs: (string * string) list) (children: A11yNode list) : A11yNode =
        Element(tag.ToLowerInvariant(), lowerMap attrs, children)

    /// A childless element (void elements — `img`, `input`, `br`, … — and
    /// any element a test does not need to give children).
    let elem (tag: string) (attrs: (string * string) list) : A11yNode =
        Element(tag.ToLowerInvariant(), lowerMap attrs, [])

    /// A text run.
    let text (value: string) : A11yNode = Text value

    /// Wrap several roots into one checkable tree.
    let fragment (roots: A11yNode list) : A11yNode = Element("#fragment", Map.empty, roots)

    // ─── ARIA vocabulary (the allowlist the rules validate against) ────

    /// Every WAI-ARIA 1.2 state and property. This is the allowlist the
    /// `AriaProp.fs` blessed-helper set (`ariaProp.label` / `labelledBy` /
    /// `describedBy` / `hidden` / `expanded` / `selected` / `checked'` /
    /// `disabled` / `busy` / `live` / `atomic` / `controls` / `current` /
    /// `pressed` / `hasPopup` / `modal`) draws from — a typo'd or invented
    /// `aria-*` name is silently inert in the browser, which is exactly
    /// the class this catches.
    let ariaAttributeAllowlist: Set<string> =
        set [
            "aria-activedescendant"
            "aria-atomic"
            "aria-autocomplete"
            "aria-braillelabel"
            "aria-brailleroledescription"
            "aria-busy"
            "aria-checked"
            "aria-colcount"
            "aria-colindex"
            "aria-colindextext"
            "aria-colspan"
            "aria-controls"
            "aria-current"
            "aria-describedby"
            "aria-description"
            "aria-details"
            "aria-disabled"
            "aria-dropeffect"
            "aria-errormessage"
            "aria-expanded"
            "aria-flowto"
            "aria-grabbed"
            "aria-haspopup"
            "aria-hidden"
            "aria-invalid"
            "aria-keyshortcuts"
            "aria-label"
            "aria-labelledby"
            "aria-level"
            "aria-live"
            "aria-modal"
            "aria-multiline"
            "aria-multiselectable"
            "aria-orientation"
            "aria-owns"
            "aria-placeholder"
            "aria-posinset"
            "aria-pressed"
            "aria-readonly"
            "aria-relevant"
            "aria-required"
            "aria-roledescription"
            "aria-rowcount"
            "aria-rowindex"
            "aria-rowindextext"
            "aria-rowspan"
            "aria-selected"
            "aria-setsize"
            "aria-sort"
            "aria-valuemax"
            "aria-valuemin"
            "aria-valuenow"
            "aria-valuetext"
        ]

    /// Every WAI-ARIA 1.2 role a `role` attribute may name. An invented
    /// role (`role="clickable"`) is ignored by assistive tech — the
    /// element reads as a plain `div`.
    let roleAllowlist: Set<string> =
        set [
            "alert"
            "alertdialog"
            "application"
            "article"
            "banner"
            "blockquote"
            "button"
            "caption"
            "cell"
            "checkbox"
            "code"
            "columnheader"
            "combobox"
            "complementary"
            "contentinfo"
            "definition"
            "deletion"
            "dialog"
            "directory"
            "document"
            "emphasis"
            "feed"
            "figure"
            "form"
            "generic"
            "grid"
            "gridcell"
            "group"
            "heading"
            "img"
            "insertion"
            "link"
            "list"
            "listbox"
            "listitem"
            "log"
            "main"
            "marquee"
            "math"
            "menu"
            "menubar"
            "menuitem"
            "menuitemcheckbox"
            "menuitemradio"
            "meter"
            "navigation"
            "none"
            "note"
            "option"
            "paragraph"
            "presentation"
            "progressbar"
            "radio"
            "radiogroup"
            "region"
            "row"
            "rowgroup"
            "rowheader"
            "scrollbar"
            "search"
            "searchbox"
            "separator"
            "slider"
            "spinbutton"
            "status"
            "strong"
            "subscript"
            "superscript"
            "switch"
            "tab"
            "table"
            "tablist"
            "tabpanel"
            "term"
            "textbox"
            "time"
            "timer"
            "toolbar"
            "tooltip"
            "tree"
            "treegrid"
            "treeitem"
        ]

    /// Roles that make a non-semantic element interactive — a `div` or
    /// `span` carrying one of these must carry an accessible name too.
    let private interactiveRoles =
        set [
            "button"
            "checkbox"
            "combobox"
            "link"
            "menuitem"
            "menuitemcheckbox"
            "menuitemradio"
            "option"
            "radio"
            "searchbox"
            "slider"
            "spinbutton"
            "switch"
            "tab"
            "textbox"
            "treeitem"
        ]

    // ─── traversal ────────────────────────────────────────────────────

    /// One element in document order, with the path that locates it.
    type ElementRef = {
        Path: string
        Tag: string
        Attrs: Map<string, string>
        Children: A11yNode list
    }

    let private attr (name: string) (attrs: Map<string, string>) =
        Map.tryFind name attrs |> Option.map _.Trim()

    let private attrNonEmpty (name: string) (attrs: Map<string, string>) =
        match attr name attrs with
        | Some v when v <> "" -> true
        | _ -> false

    /// Every element in document order, each paired with its path. The
    /// synthetic `#fragment` wrapper is transparent — its children are
    /// path roots.
    let elements (root: A11yNode) : ElementRef list =
        let acc = System.Collections.Generic.List<ElementRef>()

        let segment (tag: string) (attrs: Map<string, string>) (index: int) (total: int) =
            let idPart =
                match attr "id" attrs with
                | Some v when v <> "" -> "#" + v
                | _ -> ""

            let idxPart = if total > 1 then sprintf "[%d]" index else ""
            tag + idPart + idxPart

        let rec goChildren (prefix: string) (children: A11yNode list) =
            let counts =
                children
                |> List.choose (function
                    | Element(t, _, _) -> Some t
                    | Text _ -> None)
                |> List.countBy id
                |> Map.ofList

            let mutable seen = Map.empty

            for child in children do
                match child with
                | Text _ -> ()
                | Element(tag, attrs, kids) ->
                    let index = defaultArg (Map.tryFind tag seen) 0
                    seen <- Map.add tag (index + 1) seen
                    let total = defaultArg (Map.tryFind tag counts) 1
                    let seg = segment tag attrs index total
                    let path = if prefix = "" then seg else prefix + " > " + seg

                    acc.Add {
                        Path = path
                        Tag = tag
                        Attrs = attrs
                        Children = kids
                    }

                    goChildren path kids

        match root with
        | Element("#fragment", _, children) -> goChildren "" children
        | node -> goChildren "" [ node ]

        List.ofSeq acc

    /// The text an assistive technology would announce for a subtree:
    /// text runs plus `img` `alt` text, with `aria-hidden="true"`
    /// subtrees and non-rendered elements (`script` / `style`) excluded.
    let rec visibleText (node: A11yNode) : string =
        match node with
        | Text s -> s
        | Element(tag, attrs, children) ->
            if attr "aria-hidden" attrs = Some "true" then
                ""
            elif tag = "script" || tag = "style" || tag = "template" then
                ""
            else
                let altPart =
                    match tag, attr "alt" attrs with
                    | "img", Some a -> a
                    | _ -> ""

                let inner = children |> List.map visibleText |> String.concat " "
                (altPart + " " + inner)

    let private hasVisibleText (children: A11yNode list) =
        children |> List.map visibleText |> String.concat " " |> _.Trim() |> (<>) ""

    /// Does this element carry an accessible name — from text content,
    /// `aria-label`, `aria-labelledby`, `title`, or (for the button-like
    /// `input` types) its `value` / `alt`?
    let private hasAccessibleName (tag: string) (attrs: Map<string, string>) (children: A11yNode list) =
        let fromAttrs =
            attrNonEmpty "aria-label" attrs
            || attrNonEmpty "aria-labelledby" attrs
            || attrNonEmpty "title" attrs

        let fromInput =
            tag = "input"
            && (match attr "type" attrs with
                | Some("submit" | "button" | "reset") -> attrNonEmpty "value" attrs
                | Some "image" -> attrNonEmpty "alt" attrs
                | _ -> false)

        fromAttrs || fromInput || hasVisibleText children

    // ─── rule: every interactive element has an accessible name ───────

    /// `A11yError` — a `button`, `a[href]`, `summary`, or any element
    /// carrying an interactive `role`, with no accessible name. A screen
    /// reader announces "button" and nothing else; the control is
    /// unusable without sight.
    let everyInteractiveHasAccessibleName (root: A11yNode) : A11yFinding list = [
        for e in elements root do
            let role = attr "role" e.Attrs |> Option.map _.ToLowerInvariant()
            let hidden = attr "aria-hidden" e.Attrs = Some "true"

            let isInteractive =
                e.Tag = "button"
                || e.Tag = "summary"
                || (e.Tag = "a" && attrNonEmpty "href" e.Attrs)
                || (match role with
                    | Some r -> interactiveRoles.Contains r
                    | None -> false)

            // Form controls are the `everyControlHasLabel` rule's job —
            // one finding per defect, not two.
            let isFormControl = e.Tag = "input" || e.Tag = "select" || e.Tag = "textarea"

            if isInteractive && not hidden && not isFormControl then
                if not (hasAccessibleName e.Tag e.Attrs e.Children) then
                    yield {
                        Rule = "everyInteractiveHasAccessibleName"
                        Path = e.Path
                        Message =
                            sprintf
                                "<%s> is interactive but has no accessible name (no text content, aria-label, aria-labelledby or title)"
                                e.Tag
                        Severity = A11yError
                    }
    ]

    // ─── rule: every image has alt text ───────────────────────────────

    /// `A11yError` — an `img` / `area` / `input[type=image]` with no `alt`
    /// ATTRIBUTE. `alt=""` is valid and passes: it declares the image
    /// decorative. A missing attribute is the defect — assistive tech
    /// falls back to announcing the file name.
    let everyImageHasAlt (root: A11yNode) : A11yFinding list = [
        for e in elements root do
            let needsAlt =
                e.Tag = "img"
                || e.Tag = "area"
                || (e.Tag = "input" && attr "type" e.Attrs = Some "image")

            if needsAlt && not (Map.containsKey "alt" e.Attrs) then
                yield {
                    Rule = "everyImageHasAlt"
                    Path = e.Path
                    Message =
                        sprintf
                            "<%s> has no alt attribute (use alt=\"\" to declare it decorative, or descriptive text)"
                            e.Tag
                    Severity = A11yError
                }
    ]

    // ─── rule: every form control has a label ─────────────────────────

    /// `A11yError` — an `input` (other than `hidden` / the button-like
    /// types) / `select` / `textarea` with no label: no `aria-label`,
    /// no `aria-labelledby`, no `title`, no wrapping `<label>`, and no
    /// `<label for="…">` anywhere in the tree pointing at its `id`.
    let everyControlHasLabel (root: A11yNode) : A11yFinding list =
        let all = elements root

        // Ids claimed by a `<label for="…">` anywhere in the tree.
        let labelledIds =
            all
            |> List.filter (fun e -> e.Tag = "label")
            |> List.choose (fun e -> attr "for" e.Attrs)
            |> List.filter (fun v -> v <> "")
            |> Set.ofList

        // Paths whose ancestor chain includes a `<label>` — the
        // "wrapping label" form. Path prefixes are a faithful proxy for
        // ancestry because `elements` builds them by descent.
        let labelPrefixes =
            all
            |> List.filter (fun e -> e.Tag = "label")
            |> List.map (fun e -> e.Path + " > ")

        [
            for e in all do
                let controlType = attr "type" e.Attrs |> Option.map _.ToLowerInvariant()

                let needsLabel =
                    match e.Tag with
                    | "select"
                    | "textarea" -> true
                    | "input" ->
                        match controlType with
                        | Some("hidden" | "submit" | "button" | "reset" | "image") -> false
                        | _ -> true
                    | _ -> false

                if needsLabel && attr "aria-hidden" e.Attrs <> Some "true" then
                    let named =
                        attrNonEmpty "aria-label" e.Attrs
                        || attrNonEmpty "aria-labelledby" e.Attrs
                        || attrNonEmpty "title" e.Attrs

                    let forLabelled =
                        match attr "id" e.Attrs with
                        | Some id when id <> "" -> labelledIds.Contains id
                        | _ -> false

                    let wrapped =
                        labelPrefixes
                        |> List.exists (fun p -> e.Path.StartsWith(p, StringComparison.Ordinal))

                    if not (named || forLabelled || wrapped) then
                        yield {
                            Rule = "everyControlHasLabel"
                            Path = e.Path
                            Message =
                                sprintf
                                    "<%s> is a form control with no label (no aria-label / aria-labelledby / title, no wrapping <label>, no <label for=\"…\"> targeting its id)"
                                    e.Tag
                            Severity = A11yError
                        }
        ]

    // ─── rule: valid ARIA roles + properties ──────────────────────────

    /// `A11yError` — a `role` value outside the WAI-ARIA role set, or an
    /// `aria-*` attribute outside the state/property allowlist. Both are
    /// silently inert in the browser, so the surface LOOKS annotated and
    /// announces nothing.
    let ariaRolesAndPropsValid (root: A11yNode) : A11yFinding list = [
        for e in elements root do
            match attr "role" e.Attrs with
            | Some raw when raw <> "" ->
                // `role` accepts a space-separated fallback list.
                for token in raw.ToLowerInvariant().Split(' ') do
                    let t = token.Trim()

                    if t <> "" && not (roleAllowlist.Contains t) then
                        yield {
                            Rule = "ariaRolesAndPropsValid"
                            Path = e.Path
                            Message = sprintf "<%s> declares role=\"%s\", which is not a WAI-ARIA role" e.Tag t
                            Severity = A11yError
                        }
            | _ -> ()

            for KeyValue(name, _) in e.Attrs do
                if
                    name.StartsWith("aria-", StringComparison.Ordinal)
                    && not (ariaAttributeAllowlist.Contains name)
                then
                    yield {
                        Rule = "ariaRolesAndPropsValid"
                        Path = e.Path
                        Message = sprintf "<%s> carries %s, which is not a WAI-ARIA state or property" e.Tag name
                        Severity = A11yError
                    }
    ]

    // ─── rule: heading order is monotonic ─────────────────────────────

    let private headingLevel (tag: string) =
        match tag with
        | "h1" -> Some 1
        | "h2" -> Some 2
        | "h3" -> Some 3
        | "h4" -> Some 4
        | "h5" -> Some 5
        | "h6" -> Some 6
        | _ -> None

    /// `A11yWarning` — a heading level skipped going deeper (`h1` → `h3`).
    /// Screen-reader users navigate by heading outline; a skipped level
    /// reads as a missing section. Warning-class because a legitimate
    /// fragment can start mid-outline — under `Strict` it is fatal.
    let headingOrderIsMonotonic (root: A11yNode) : A11yFinding list =
        let headings =
            elements root
            |> List.choose (fun e -> headingLevel e.Tag |> Option.map (fun lvl -> e.Path, lvl))

        [
            for i in 1 .. headings.Length - 1 do
                let _, prev = headings[i - 1]
                let path, curr = headings[i]

                if curr > prev + 1 then
                    yield {
                        Rule = "headingOrderIsMonotonic"
                        Path = path
                        Message = sprintf "heading level skips from h%d to h%d (the outline loses a level)" prev curr
                        Severity = A11yWarning
                    }
        ]

    // ─── rule: no colour-only state ───────────────────────────────────

    /// Class tokens / inline-style values that conventionally encode a
    /// STATE through colour alone.
    let private colourStateWords = [
        "error"
        "danger"
        "invalid"
        "success"
        "valid"
        "warning"
        "critical"
        "positive"
        "negative"
        "alert"
    ]

    /// Attributes that give a colour-encoded state a non-visual
    /// equivalent.
    let private stateEquivalentAttrs = [
        "aria-label"
        "aria-labelledby"
        "aria-describedby"
        "aria-invalid"
        "aria-current"
        "aria-pressed"
        "aria-selected"
        "aria-checked"
        "aria-live"
        "title"
    ]

    /// `A11yWarning` — best-effort heuristic for state conveyed by colour
    /// alone (WCAG 1.4.1): an element whose class or inline style carries
    /// a state-colour signal but which has no text, no state-bearing
    /// `aria-*` attribute, no `title`, and no `role` of `alert`/`status`.
    /// The canonical shape is a bare coloured dot or bar.
    ///
    /// Heuristic by construction — it reads conventional class naming, so
    /// it has false negatives (a state class named nothing like a state
    /// word) and can have false positives (a decorative element that
    /// happens to match). Warning-class for exactly that reason; it points
    /// a human at a candidate, it does not adjudicate.
    let noColourOnlyState (root: A11yNode) : A11yFinding list = [
        for e in elements root do
            let signalText =
                let cls = defaultArg (attr "class" e.Attrs) ""
                let style = defaultArg (attr "style" e.Attrs) ""
                (cls + " " + style).ToLowerInvariant()

            let matched = colourStateWords |> List.tryFind (fun w -> signalText.Contains w)

            match matched with
            | Some word ->
                let hasEquivalent =
                    stateEquivalentAttrs |> List.exists (fun a -> attrNonEmpty a e.Attrs)

                let role = attr "role" e.Attrs |> Option.map _.ToLowerInvariant()
                let announcingRole = role = Some "alert" || role = Some "status"
                let hasText = hasVisibleText e.Children

                if not (hasEquivalent || announcingRole || hasText) then
                    yield {
                        Rule = "noColourOnlyState"
                        Path = e.Path
                        Message =
                            sprintf
                                "<%s> signals the \"%s\" state through colour alone (no text, no aria-* state attribute, no title, no role=alert/status)"
                                e.Tag
                                word
                        Severity = A11yWarning
                    }
            | None -> ()
    ]

    // ─── profiles ─────────────────────────────────────────────────────

    /// The rules a profile runs, in the order their findings are
    /// reported. `Minimal` = the `A11yError`-class table stakes;
    /// `Strict` = those plus the `A11yWarning`-class heuristics.
    let rulesFor (profile: A11yProfile) : (string * (A11yNode -> A11yFinding list)) list =
        let baseline = [
            "everyInteractiveHasAccessibleName", everyInteractiveHasAccessibleName
            "everyImageHasAlt", everyImageHasAlt
            "everyControlHasLabel", everyControlHasLabel
            "ariaRolesAndPropsValid", ariaRolesAndPropsValid
        ]

        match profile with
        | Minimal -> baseline
        | Strict ->
            baseline
            @ [
                "headingOrderIsMonotonic", headingOrderIsMonotonic
                "noColourOnlyState", noColourOnlyState
            ]

    /// Run a profile's rules over a tree. An empty list means the tree
    /// passes that profile.
    let check (profile: A11yProfile) (root: A11yNode) : A11yFinding list =
        rulesFor profile |> List.collect (fun (_, rule) -> rule root)

    /// Is this finding fatal under this profile? `A11yError` always is;
    /// under `Strict` the `A11yWarning` heuristics are fatal too — that
    /// is what opting into `Strict` means.
    let isFatal (profile: A11yProfile) (finding: A11yFinding) =
        finding.Severity = A11yError || profile = Strict

    /// A readable one-line diagnostic — rule, element path, message.
    let describe (f: A11yFinding) : string =
        let sev =
            match f.Severity with
            | A11yError -> "error"
            | A11yWarning -> "warning"

        sprintf "  [%s] %s at %s: %s" sev f.Rule f.Path f.Message

    /// The consolidated failure report for a finding list.
    let report (profile: A11yProfile) (findings: A11yFinding list) : string =
        let header =
            sprintf "Accessibility check failed under the %A profile — %d finding(s):" profile findings.Length

        header + "\n" + (findings |> List.map describe |> String.concat "\n")

    /// Assert a tree against a profile: throws with the consolidated
    /// finding list when any finding is fatal for that profile, and
    /// returns the (possibly non-empty, non-fatal) finding list otherwise
    /// so a caller can inspect the warnings it tolerated.
    let assertAccessible (profile: A11yProfile) (root: A11yNode) : A11yFinding list =
        let findings = check profile root
        let fatal = findings |> List.filter (isFatal profile)

        if not (List.isEmpty fatal) then
            failwith (report profile fatal)

        findings

    /// The standalone entry named by the phase — for tests that render a
    /// view directly without the full harness (a standalone embed, a
    /// narrative renderer, an SSR layout). `assert` is an F# keyword, so
    /// the call site needs backticks; `assertAccessible` is the
    /// ceremony-free spelling of the same thing.
    let ``assert`` (profile: A11yProfile) (root: A11yNode) : A11yFinding list = assertAccessible profile root

    // ─── HTML fragment adapter (BCL-only, no regex — Fable-safe) ───────

    /// HTML5 void elements — no end tag, never given children.
    let private voidElements =
        set [
            "area"
            "base"
            "br"
            "col"
            "embed"
            "hr"
            "img"
            "input"
            "link"
            "meta"
            "param"
            "source"
            "track"
            "wbr"
        ]

    type private Token =
        | TOpen of name: string * attrs: Map<string, string> * selfClosing: bool
        | TClose of name: string
        | TText of string

    let private isWhitespace (c: char) =
        c = ' ' || c = '\t' || c = '\n' || c = '\r' || c = '\f'

    let private tokenize (html: string) : Token list =
        let n = html.Length
        let acc = System.Collections.Generic.List<Token>()
        let mutable i = 0

        let readName (start: int) =
            let mutable j = start

            while j < n
                  && not (isWhitespace html[j])
                  && html[j] <> '>'
                  && html[j] <> '/'
                  && html[j] <> '=' do
                j <- j + 1

            html.Substring(start, j - start), j

        while i < n do
            if html[i] = '<' then
                if i + 3 < n && html.Substring(i, 4) = "<!--" then
                    let endIdx = html.IndexOf("-->", i + 4)
                    i <- (if endIdx < 0 then n else endIdx + 3)
                elif i + 1 < n && html[i + 1] = '!' then
                    let endIdx = html.IndexOf('>', i)
                    i <- (if endIdx < 0 then n else endIdx + 1)
                elif i + 1 < n && html[i + 1] = '/' then
                    let name, j = readName (i + 2)
                    let endIdx = html.IndexOf('>', j)
                    acc.Add(TClose(name.ToLowerInvariant()))
                    i <- (if endIdx < 0 then n else endIdx + 1)
                else
                    let name, j = readName (i + 1)
                    let attrs = System.Collections.Generic.Dictionary<string, string>()
                    let mutable k = j
                    let mutable stop = false
                    let mutable selfClosing = false

                    while not stop && k < n do
                        while k < n && isWhitespace html[k] do
                            k <- k + 1

                        if k >= n then
                            stop <- true
                        elif html[k] = '>' then
                            k <- k + 1
                            stop <- true
                        elif html[k] = '/' then
                            selfClosing <- true
                            k <- k + 1
                        else
                            let aName, ak = readName k
                            let mutable kk = ak

                            while kk < n && isWhitespace html[kk] do
                                kk <- kk + 1

                            if kk < n && html[kk] = '=' then
                                kk <- kk + 1

                                while kk < n && isWhitespace html[kk] do
                                    kk <- kk + 1

                                if kk < n && (html[kk] = '"' || html[kk] = '\'') then
                                    let quote = html[kk]
                                    let vStart = kk + 1
                                    let vEnd = html.IndexOf(quote, vStart)
                                    let stopV = if vEnd < 0 then n else vEnd
                                    attrs[aName.ToLowerInvariant()] <- html.Substring(vStart, stopV - vStart)
                                    kk <- (if vEnd < 0 then n else vEnd + 1)
                                else
                                    let vStart = kk

                                    while kk < n && not (isWhitespace html[kk]) && html[kk] <> '>' && html[kk] <> '/' do
                                        kk <- kk + 1

                                    attrs[aName.ToLowerInvariant()] <- html.Substring(vStart, kk - vStart)

                                k <- kk
                            else
                                if aName <> "" then
                                    attrs[aName.ToLowerInvariant()] <- ""

                                k <- ak

                    let attrMap = attrs |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                    acc.Add(TOpen(name.ToLowerInvariant(), attrMap, selfClosing))
                    i <- k
            else
                let start = i

                while i < n && html[i] <> '<' do
                    i <- i + 1

                acc.Add(TText(html.Substring(start, i - start)))

        List.ofSeq acc

    /// Parse a rendered HTML fragment into an `A11yNode` tree — the seam
    /// every renderer can reach: `Giraffe.ViewEngine` /
    /// `Feliz.ViewEngine` SSR output, a browser-side `outerHTML` capture,
    /// or any other tree language lowered to HTML (the same
    /// fragment-string contract `HydrationParity` and `HostedTreeA11y`
    /// consume). Multiple roots are wrapped in a transparent
    /// `"#fragment"` element. Unclosed and mismatched tags are tolerated
    /// — the parser closes what it can and never throws.
    let ofHtml (html: string) : A11yNode =
        // Each stack frame accumulates children for one open element.
        let roots = System.Collections.Generic.List<A11yNode>()

        let stack =
            System.Collections.Generic.Stack<string * Map<string, string> * System.Collections.Generic.List<A11yNode>>()

        let emit (node: A11yNode) =
            if stack.Count > 0 then
                let _, _, children = stack.Peek()
                children.Add node
            else
                roots.Add node

        let closeTop () =
            let tag, attrs, children = stack.Pop()
            emit (Element(tag, attrs, List.ofSeq children))

        for token in tokenize html do
            match token with
            | TText s ->
                if s.Trim() <> "" then
                    emit (Text s)
            | TOpen(name, attrs, selfClosing) ->
                if selfClosing || voidElements.Contains name then
                    emit (Element(name, attrs, []))
                else
                    stack.Push(name, attrs, System.Collections.Generic.List<A11yNode>())
            | TClose name ->
                // Close up to and including the matching open tag; a
                // stray close with no match is ignored.
                let hasMatch = stack |> Seq.exists (fun (t, _, _) -> t = name)

                if hasMatch then
                    let mutable closing = true

                    while closing && stack.Count > 0 do
                        let tag, _, _ = stack.Peek()
                        closeTop ()

                        if tag = name then
                            closing <- false

        while stack.Count > 0 do
            closeTop ()

        match List.ofSeq roots with
        | [ single ] -> single
        | many -> Element("#fragment", Map.empty, many)

    /// `check` over a rendered HTML fragment.
    let checkHtml (profile: A11yProfile) (html: string) : A11yFinding list = check profile (ofHtml html)

    /// `assertAccessible` over a rendered HTML fragment — the entry an
    /// SSR test reaches for after `RenderView.AsString.htmlNode`.
    let assertHtml (profile: A11yProfile) (html: string) : A11yFinding list = assertAccessible profile (ofHtml html)

    // ─── shipped fixtures ─────────────────────────────────────────────

    /// A fixture: a tree + the profile it is expected to be judged under
    /// + whether it should pass.
    type Fixture = {
        Name: string
        Node: A11yNode
        Profile: A11yProfile
        ExpectClean: bool
    }

    /// An a11y-clean tree — a labelled button, an alt-bearing image, a
    /// labelled input, a monotonic outline, valid ARIA. MUST `check`
    /// empty under BOTH profiles.
    let cleanFixture: Fixture = {
        Name = "clean view"
        Profile = Strict
        ExpectClean = true
        Node =
            el "section" [ "id", "report" ] [
                el "h1" [] [ text "Report" ]
                el "h2" [] [ text "Summary" ]
                elem "img" [ "src", "/svg/chart.svg"; "alt", "Revenue by quarter" ]
                el "label" [ "for", "q" ] [ text "Query" ]
                elem "input" [ "id", "q"; "type", "text" ]
                el "button" [ "aria-pressed", "false" ] [ text "Export" ]
            ]
    }

    /// One fixture per failure class — each MUST produce a finding naming
    /// its rule.
    let violationFixtures: Fixture list = [
        {
            Name = "unlabelled button"
            Profile = Minimal
            ExpectClean = false
            Node = el "div" [] [ el "button" [] [] ]
        }
        {
            Name = "image with no alt attribute"
            Profile = Minimal
            ExpectClean = false
            Node = el "div" [] [ elem "img" [ "src", "/svg/chart.svg" ] ]
        }
        {
            Name = "unlabelled form control"
            Profile = Minimal
            ExpectClean = false
            Node = el "form" [] [ elem "input" [ "type", "text"; "name", "q" ] ]
        }
        {
            Name = "invalid ARIA role"
            Profile = Minimal
            ExpectClean = false
            Node = el "div" [ "role", "clickable" ] [ text "Go" ]
        }
        {
            Name = "invented aria-* property"
            Profile = Minimal
            ExpectClean = false
            Node = el "div" [ "aria-clicked", "true" ] [ text "Go" ]
        }
        {
            Name = "heading order skip"
            Profile = Strict
            ExpectClean = false
            Node = el "section" [] [ el "h1" [] [ text "Title" ]; el "h3" [] [ text "Sub" ] ]
        }
        {
            Name = "colour-only state"
            Profile = Strict
            ExpectClean = false
            Node = el "div" [] [ elem "span" [ "class", "status-dot status-error" ] ]
        }
    ]