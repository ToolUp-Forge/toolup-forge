// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Testing

// ─── Phase 277 — hosted-tree a11y conformance harness ──────────────────
//
// Phase 180 asserts accessibility for MODULES from source, but a hosted
// tree's structure is produced at RUNTIME (possibly AI-emitted), so role /
// aria coverage and focus order go unchecked. This harness — modelled on
// Phase 203's (`HydrationParity`) render-fixture-and-gate shape — consumes
// a hosted tree's rendered HTML fragment and asserts a11y coverage:
// interactive-element accessible names, semantic roles on interaction
// sites, heading order, and tab/focus order. A regression fails the build
// (it runs under `VerifyAll` via the `HostedTreeA11yTests` pack), not a
// screen reader.
//
// It consumes the fragment STRING (exactly as `HydrationParity` does), so a
// hosted tree lowered by ANY tree language — the Phase 202 `ToyNode`
// witness included — is checkable without the harness knowing anything
// tree-specific (GP 1). Pure test/build infrastructure: BCL-only (a
// hand-rolled scanner, no regex), zero runtime surface, byte-for-byte
// absent from any consumer build (GP 13). Carries no banned vocabulary.

[<RequireQualifiedAccess>]
module HostedTreeA11y =

    open System.Text

    // ─── a11y violation model ─────────────────────────────────────────

    /// One accessibility failure the harness detects in a hosted fragment.
    type A11yViolation =
        /// An interactive control (`button` / `a[href]` / `input`) with no
        /// accessible name — no text content, no `aria-label` /
        /// `aria-labelledby`, no `title`.
        | UnlabelledControl of element: string
        /// A non-semantic element (`div` / `span`) carrying an interaction
        /// marker (`onclick` / `data-action` / `data-toy-event`) but no
        /// `role` — a screen reader cannot announce it as actionable.
        | MissingRole of element: string
        /// A heading level skip (e.g. `h1` → `h3`) — a broken document
        /// outline.
        | HeadingSkip of fromLevel: int * toLevel: int
        /// A tab/focus-order break — positive `tabindex` values that are not
        /// in ascending document order (the classic focus-trap smell).
        | FocusOrderBreak of detail: string

    /// The outcome of an a11y check.
    type A11yResult =
        | Conformant
        | Violations of A11yViolation list

    /// A readable one-line diagnostic for a violation — what the build
    /// failure prints (so a fix targets the exact node, not "somewhere").
    let describe (v: A11yViolation) : string =
        match v with
        | UnlabelledControl el -> sprintf "unlabelled control: %s has no accessible name (text / aria-label / title)" el
        | MissingRole el -> sprintf "missing role: interactive %s carries no role attribute" el
        | HeadingSkip(f, t) -> sprintf "heading skip: h%d followed by h%d (skips a level)" f t
        | FocusOrderBreak detail -> sprintf "focus-order break: %s" detail

    // ─── minimal HTML scanner (BCL-only, no regex — Fable-safe) ────────

    type private Token =
        | Open of name: string * attrs: Map<string, string>
        | Close of name: string
        | Text of string

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
                    acc.Add(Close(name.ToLowerInvariant()))
                    i <- (if endIdx < 0 then n else endIdx + 1)
                else
                    let name, j = readName (i + 1)
                    let attrs = System.Collections.Generic.Dictionary<string, string>()
                    let mutable k = j
                    let mutable stop = false

                    while not stop && k < n do
                        while k < n && isWhitespace html[k] do
                            k <- k + 1

                        if k >= n then
                            stop <- true
                        elif html[k] = '>' then
                            k <- k + 1
                            stop <- true
                        elif html[k] = '/' then
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

                    acc.Add(Open(name.ToLowerInvariant(), attrs |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq))
                    i <- k
            else
                let start = i

                while i < n && html[i] <> '<' do
                    i <- i + 1

                acc.Add(Text(html.Substring(start, i - start)))

        List.ofSeq acc

    // ─── the a11y checks ──────────────────────────────────────────────

    let private headingLevel (name: string) : int option =
        match name with
        | "h1" -> Some 1
        | "h2" -> Some 2
        | "h3" -> Some 3
        | "h4" -> Some 4
        | "h5" -> Some 5
        | "h6" -> Some 6
        | _ -> None

    /// Does an attribute map carry an accessible name (aria-label /
    /// aria-labelledby / title, non-empty)?
    let private hasAccessibleNameAttr (attrs: Map<string, string>) : bool =
        let nonEmpty k =
            match Map.tryFind k attrs with
            | Some v -> v.Trim() <> ""
            | None -> false

        nonEmpty "aria-label" || nonEmpty "aria-labelledby" || nonEmpty "title"

    let private hasInteractionMarker (attrs: Map<string, string>) : bool =
        Map.containsKey "onclick" attrs
        || Map.containsKey "data-action" attrs
        || Map.containsKey "data-toy-event" attrs

    // A control whose accessible name may come from text content — tracked
    // on a stack so descendant text is accumulated until the close tag.
    type private ControlFrame = {
        Tag: string
        Attrs: Map<string, string>
        Text: StringBuilder
    }

    /// Check a hosted-tree HTML fragment for accessibility conformance.
    /// `Conformant` means no violation; otherwise every violation is named
    /// in document order.
    let check (html: string) : A11yResult =
        let tokens = tokenize html
        let violations = System.Collections.Generic.List<A11yViolation>()
        let controlStack = System.Collections.Generic.Stack<ControlFrame>()
        let headingLevels = System.Collections.Generic.List<int>()
        let positiveTabindex = System.Collections.Generic.List<int>()

        let render (name: string) (attrs: Map<string, string>) =
            let idSuffix =
                match Map.tryFind "data-toy-event" attrs with
                | Some v -> sprintf " (data-toy-event=\"%s\")" v
                | None ->
                    match Map.tryFind "id" attrs with
                    | Some v -> sprintf " (id=\"%s\")" v
                    | None -> ""

            "<" + name + ">" + idSuffix

        for tok in tokens do
            match tok with
            | Text s ->
                if controlStack.Count > 0 then
                    controlStack.Peek().Text.Append s |> ignore
            | Open(name, attrs) ->
                // Heading order.
                match headingLevel name with
                | Some lvl -> headingLevels.Add lvl
                | None -> ()

                // Focus order — collect positive tabindex in document order.
                match Map.tryFind "tabindex" attrs with
                | Some raw ->
                    match System.Int32.TryParse(raw.Trim()) with
                    | true, v when v > 0 -> positiveTabindex.Add v
                    | _ -> ()
                | None -> ()

                // Missing role on a non-semantic interaction site.
                if
                    (name = "div" || name = "span")
                    && hasInteractionMarker attrs
                    && not (Map.containsKey "role" attrs)
                then
                    violations.Add(MissingRole(render name attrs))

                // Controls that need an accessible name.
                let isControl =
                    name = "button"
                    || name = "input"
                    || (name = "a" && Map.containsKey "href" attrs)

                if isControl then
                    let isHiddenInput = name = "input" && (Map.tryFind "type" attrs = Some "hidden")

                    if name = "input" then
                        // Void — evaluate immediately (no text children).
                        if not isHiddenInput && not (hasAccessibleNameAttr attrs) then
                            violations.Add(UnlabelledControl(render name attrs))
                    else
                        controlStack.Push {
                            Tag = name
                            Attrs = attrs
                            Text = StringBuilder()
                        }
            | Close name ->
                if controlStack.Count > 0 && controlStack.Peek().Tag = name then
                    let frame = controlStack.Pop()
                    let hasText = frame.Text.ToString().Trim() <> ""

                    if not hasText && not (hasAccessibleNameAttr frame.Attrs) then
                        violations.Add(UnlabelledControl(render frame.Tag frame.Attrs))

        // Heading-skip pass (deeper by more than one level).
        for i in 1 .. headingLevels.Count - 1 do
            let prev = headingLevels[i - 1]
            let curr = headingLevels[i]

            if curr > prev + 1 then
                violations.Add(HeadingSkip(prev, curr))

        // Focus-order pass — positive tabindex must be ascending.
        let mutable ordered = true

        for i in 1 .. positiveTabindex.Count - 1 do
            if positiveTabindex[i] <= positiveTabindex[i - 1] then
                ordered <- false

        if not ordered then
            violations.Add(
                FocusOrderBreak(
                    sprintf
                        "positive tabindex values not ascending: %s"
                        (positiveTabindex |> Seq.map string |> String.concat ", ")
                )
            )

        if violations.Count = 0 then
            Conformant
        else
            Violations(List.ofSeq violations)

    // ─── shipped fixtures ─────────────────────────────────────────────

    /// A fixture: a hosted-tree fragment + whether it should be conformant.
    type Fixture = {
        Name: string
        Html: string
        ExpectConformant: bool
    }

    /// An a11y-clean fragment — a well-formed outline with a labelled
    /// control and no interaction-site / focus-order defects. MUST `check`
    /// to `Conformant`.
    let conformantFixture: Fixture = {
        Name = "clean outline + labelled control"
        Html = "<section><h1>Report</h1><h2>Summary</h2><p>Body</p><button>Export</button></section>"
        ExpectConformant = true
    }

    /// One fixture per a11y failure class — each MUST `check` to a
    /// `Violations` naming its defect.
    let violationFixtures: Fixture list = [
        {
            Name = "unlabelled control"
            Html = "<div><button></button></div>"
            ExpectConformant = false
        }
        {
            Name = "missing role on an interaction site"
            Html = """<span data-toy-event="navigate:home">Go</span>"""
            ExpectConformant = false
        }
        {
            Name = "broken focus order"
            Html = """<button tabindex="3">a</button><button tabindex="1">b</button>"""
            ExpectConformant = false
        }
        {
            Name = "heading skip"
            Html = "<h1>Title</h1><h3>Sub</h3>"
            ExpectConformant = false
        }
    ]