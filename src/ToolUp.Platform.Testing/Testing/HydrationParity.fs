// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Testing

// ─── Phase 203 — hydration-parity conformance harness for hosted trees ─
//
// A hosted typed-tree renders twice: once on the server (the Phase 111
// server-rendered-fragment source → an HTML fragment string) and once on
// the client (the Phase 110 `ClientHostBridge` / `withElementView` mount,
// React reconciling onto that fragment). When the two disagree on the
// shape of the DOM — divergent attribute ORDER, boolean/void-element
// NORMALISATION, adjacent TEXT-NODE boundaries, insignificant WHITESPACE,
// or event-handler attributes that one side serialises and the other does
// not — React's hydration silently re-renders from scratch on top of the
// prerendered DOM. The page still works; the only symptom is a console
// warning nobody reads, and the SEO first-paint is quietly voided
// (the same failure class `PrerenderDeterminismTests` documents for the
// Phase 57 prerender path).
//
// This module is the structural diff that turns that silent class into a
// build-time signal. `normalise` reduces an HTML fragment to a canonical
// token stream — tag-name case folded, attributes sorted, boolean
// attributes valueless, void elements slash-free, self-closing non-void
// elements expanded, comments dropped, adjacent text coalesced,
// layout-insignificant whitespace collapsed (preserved verbatim only
// inside raw/whitespace-significant elements: `pre` / `textarea` /
// `script` / `style`), and `on*` event handlers + React's
// `data-reactroot` marker stripped. `check` normalises both sides and
// diffs them node-by-node, naming the first divergent node.
//
// What the harness deliberately does NOT do: run React. The CSR mount's
// real output is browser-side — the actual React hydration-mismatch
// warning is verified in the consumer's DevTools console, exactly as
// Phase 57's prerender determinism pack splits its concern (the byte-
// stable F# pieces are pinned in-process; the React behaviour is browser-
// verified). The harness consumes the two fragment STRINGS — in CI a
// consumer captures the SSR fragment from the Phase 111 path and the
// client `outerHTML` after mount, and feeds both here. The shipped
// fixtures (`divergenceClassFixtures`) model each renderer's real
// emission quirks (Feliz.ViewEngine's `<br>` vs React's `<br/>`, React's
// `<!-- -->` text separators and `="" ` boolean form, attribute-order
// differences) so the normalisation contract itself is under test.
//
// Pure test/build infrastructure: BCL-only (so it packs under `fable/`
// and runs under both .NET and Fable), zero runtime surface, byte-for-
// byte absent from any consumer build that never references it (GP 13).

[<RequireQualifiedAccess>]
module HydrationParity =

    open System.Text

    // ─── token model ──────────────────────────────────────────────────

    type private Token =
        /// An element open tag. `Attrs` pairs name → value (`None` =
        /// valueless / boolean). Void elements carry no matching `Close`.
        | Open of name: string * attrs: (string * string option) list
        | Close of name: string
        /// Text run. `Preserved` = sits inside a whitespace-significant
        /// element, so its whitespace is kept verbatim.
        | Text of value: string * preserved: bool
        | Comment of string

    /// HTML5 void elements — no end tag; never self-closed in canonical form.
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

    /// Elements whose text content is whitespace-significant (or raw):
    /// content is preserved verbatim and inner `<` is NOT a tag start.
    let private preservedElements = set [ "pre"; "textarea"; "script"; "style" ]

    /// Attributes that are valueless booleans in HTML5: `checked` ≡
    /// `checked=""` ≡ `checked="checked"`. Canonicalised to valueless.
    let private booleanAttributes =
        set [
            "allowfullscreen"
            "async"
            "autofocus"
            "autoplay"
            "checked"
            "controls"
            "default"
            "defer"
            "disabled"
            "formnovalidate"
            "hidden"
            "ismap"
            "itemscope"
            "loop"
            "multiple"
            "muted"
            "nomodule"
            "novalidate"
            "open"
            "readonly"
            "required"
            "reversed"
            "selected"
        ]

    let private isWhitespace (c: char) =
        c = ' ' || c = '\t' || c = '\n' || c = '\r' || c = '\f'

    // ─── tokeniser ────────────────────────────────────────────────────
    //
    // A deliberately small single-pass scanner — NOT a full HTML5 parser.
    // It recognises open / close / self-closing tags, comments, and text
    // runs, plus raw-text capture for `script` / `style` / `textarea`
    // (whose bodies may legally contain `<`). Adequate for fragment
    // conformance; fixtures stay well-formed.

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

        // Capture a raw-text element body verbatim up to its close tag.
        let captureRaw (name: string) (start: int) =
            let close = "</" + name
            let idx = html.IndexOf(close, start, System.StringComparison.OrdinalIgnoreCase)

            if idx < 0 then
                html.Substring start, n
            else
                html.Substring(start, idx - start), idx

        while i < n do
            if html[i] = '<' then
                if i + 3 < n && html.Substring(i, 4) = "<!--" then
                    let endIdx = html.IndexOf("-->", i + 4)
                    let stop = if endIdx < 0 then n else endIdx + 3
                    acc.Add(Comment(html.Substring(i, stop - i)))
                    i <- stop
                elif i + 1 < n && html[i + 1] = '!' then
                    // Doctype / declaration — skip to '>' (fragments rarely carry one).
                    let endIdx = html.IndexOf('>', i)
                    i <- (if endIdx < 0 then n else endIdx + 1)
                elif i + 1 < n && html[i + 1] = '/' then
                    let name, j = readName (i + 2)
                    let endIdx = html.IndexOf('>', j)
                    acc.Add(Close name)
                    i <- (if endIdx < 0 then n else endIdx + 1)
                else
                    let name, j = readName (i + 1)
                    // Parse attributes until '>' or '/>'.
                    let attrs = System.Collections.Generic.List<string * string option>()
                    let mutable k = j
                    let mutable selfClose = false
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
                            selfClose <- true
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
                                    attrs.Add(aName, Some(html.Substring(vStart, stopV - vStart)))
                                    kk <- (if vEnd < 0 then n else vEnd + 1)
                                else
                                    let vStart = kk

                                    while kk < n && not (isWhitespace html[kk]) && html[kk] <> '>' && html[kk] <> '/' do
                                        kk <- kk + 1

                                    attrs.Add(aName, Some(html.Substring(vStart, kk - vStart)))

                                k <- kk
                            else
                                // Valueless attribute.
                                if aName <> "" then
                                    attrs.Add(aName, None)

                                k <- ak

                    acc.Add(Open(name, List.ofSeq attrs))
                    i <- k

                    // Raw-text element: capture its body verbatim, then its close tag.
                    let lower = name.ToLowerInvariant()

                    if preservedElements.Contains lower && not selfClose then
                        let raw, rawEnd = captureRaw lower i

                        if raw <> "" then
                            acc.Add(Text(raw, true))

                        i <- rawEnd
                    elif selfClose && not (voidElements.Contains lower) then
                        // Non-void self-close (`<div/>`) → empty element.
                        acc.Add(Close name)
            else
                let start = i

                while i < n && html[i] <> '<' do
                    i <- i + 1

                acc.Add(Text(html.Substring(start, i - start), false))

        List.ofSeq acc

    // ─── canonicalisation ─────────────────────────────────────────────

    let private isHandler (name: string) =
        let l = name.ToLowerInvariant()
        l.StartsWith "on" && l.Length > 2

    let private normaliseAttrs (attrs: (string * string option) list) =
        attrs
        |> List.choose (fun (name, value) ->
            let l = name.ToLowerInvariant()

            if isHandler l || l = "data-reactroot" then None // event handlers + React's root marker don't survive to SSR HTML
            elif booleanAttributes.Contains l then Some(l, None) // canonicalise checked="" / checked="checked" → checked
            else Some(l, value))
        |> List.sortBy fst

    /// Collapse runs of whitespace to a single space and trim the ends.
    let private collapseWhitespace (s: string) =
        let sb = StringBuilder()
        let mutable inWs = false

        for c in s do
            if isWhitespace c then
                inWs <- true
            else
                if inWs && sb.Length > 0 then
                    sb.Append ' ' |> ignore

                inWs <- false
                sb.Append c |> ignore

        sb.ToString()

    /// Reduce a token list to canonical form: drop comments + declarations,
    /// lowercase tag names, normalise attributes, coalesce adjacent text,
    /// collapse layout whitespace (verbatim inside preserved elements).
    let private canonicalise (tokens: Token list) : Token list =
        // Pass 1 — drop comments, normalise tag/attr shape.
        let stage1 =
            tokens
            |> List.choose (fun t ->
                match t with
                | Comment _ -> None
                | Open(name, attrs) -> Some(Open(name.ToLowerInvariant(), normaliseAttrs attrs))
                | Close name -> Some(Close(name.ToLowerInvariant()))
                | Text _ -> Some t)

        // Pass 2 — coalesce adjacent text tokens (React's `<!-- -->`
        // separators dropped above leave two adjacent runs that are one
        // DOM text node after hydration).
        let merged =
            (([], None), stage1)
            ||> List.fold (fun (acc, pending) t ->
                match t, pending with
                | Text(v, p), Some(pv, pp) -> acc, Some(pv + v, pp || p)
                | Text(v, p), None -> acc, Some(v, p)
                | other, Some(pv, pp) -> other :: Text(pv, pp) :: acc, None
                | other, None -> other :: acc, None)
            |> fun (acc, pending) ->
                match pending with
                | Some(pv, pp) -> Text(pv, pp) :: acc
                | None -> acc
            |> List.rev

        // Pass 3 — collapse whitespace on non-preserved text; drop now-empty runs.
        merged
        |> List.choose (fun t ->
            match t with
            | Text(v, false) ->
                let c = collapseWhitespace v
                if c = "" then None else Some(Text(c, false))
            | _ -> Some t)

    // ─── rendering + diff ─────────────────────────────────────────────

    let private renderAttr (name: string, value: string option) =
        match value with
        | None -> " " + name
        | Some v -> " " + name + "=\"" + v + "\""

    let private renderToken (t: Token) =
        match t with
        | Open(name, attrs) -> "<" + name + (attrs |> List.map renderAttr |> String.concat "") + ">"
        | Close name -> "</" + name + ">"
        | Text(v, _) -> v
        | Comment c -> c

    /// A human-readable one-line description of a node, for diff messages.
    let private describe (t: Token) =
        match t with
        | Open(name, _) -> "element <" + name + "> (" + renderToken t + ")"
        | Close name -> "</" + name + ">"
        | Text(v, _) -> "text \"" + v + "\""
        | Comment _ -> "comment"

    /// Normalise an HTML fragment to its canonical string form. Two
    /// fragments that are structurally identical for hydration purposes
    /// normalise to byte-identical strings.
    let normalise (html: string) : string =
        html |> tokenize |> canonicalise |> List.map renderToken |> String.concat ""

    /// The outcome of a parity check.
    type ParityResult =
        | Parity
        | Divergence of message: string

    /// Compare an SSR fragment against a CSR mount fragment. Both are
    /// normalised, then diffed node-by-node; the first structural
    /// divergence is named (node index + both sides described). `Parity`
    /// means the two normalise to byte-identical output.
    let check (ssr: string) (csr: string) : ParityResult =
        let a = ssr |> tokenize |> canonicalise
        let b = csr |> tokenize |> canonicalise

        let rec go i xs ys =
            match xs, ys with
            | [], [] -> Parity
            | x :: xs', y :: ys' ->
                if renderToken x = renderToken y then
                    go (i + 1) xs' ys'
                else
                    Divergence(
                        System.String.Format(
                            "hydration divergence at node #{0}: SSR {1} ≠ CSR {2}",
                            i,
                            describe x,
                            describe y
                        )
                    )
            | x :: _, [] ->
                Divergence(
                    System.String.Format(
                        "hydration divergence at node #{0}: SSR has extra {1}; CSR ended",
                        i,
                        describe x
                    )
                )
            | [], y :: _ ->
                Divergence(
                    System.String.Format(
                        "hydration divergence at node #{0}: CSR has extra {1}; SSR ended",
                        i,
                        describe y
                    )
                )

        go 0 a b

    // ─── shipped fixtures ─────────────────────────────────────────────

    /// A parity fixture: the same logical tree as the two renderers
    /// actually emit it. `Ssr` is the Feliz.ViewEngine / Giraffe.ViewEngine
    /// flavour (Phase 111 fragment); `Csr` is the React `outerHTML`
    /// flavour (Phase 110 mount).
    type Fixture = {
        Name: string
        Ssr: string
        Csr: string
    }

    /// One fixture per silent divergence class. Each pair is structurally
    /// identical and MUST `check` to `Parity` — the harness proves the
    /// normalisation collapses the renderers' incidental differences.
    let divergenceClassFixtures: Fixture list = [
        {
            // Attribute ordering: Feliz.ViewEngine emits source order; React
            // emits its own prop-iteration order. Sorted-attr normalisation
            // collapses both.
            Name = "attribute ordering"
            Ssr = "<div class=\"card\" data-id=\"7\" id=\"main\"></div>"
            Csr = "<div id=\"main\" class=\"card\" data-id=\"7\"></div>"
        }
        {
            // Boolean + void elements: SSR emits HTML5 bare `checked` and a
            // slash-free `<input>`/`<br>`; React emits `checked=""` and a
            // self-closing `<br/>`.
            Name = "boolean + void elements"
            Ssr = "<p>Line<br>text<input type=\"checkbox\" checked></p>"
            Csr = "<p>Line<br/>text<input checked=\"\" type=\"checkbox\"/></p>"
        }
        {
            // Adjacent text nodes: React inserts `<!-- -->` separators
            // between sibling text runs; SSR emits one contiguous run.
            Name = "adjacent text nodes"
            Ssr = "<span>Hello, world!</span>"
            Csr = "<span>Hello, <!-- -->world!</span>"
        }
        {
            // Whitespace: insignificant layout whitespace between elements is
            // collapsed away, while whitespace-significant `<pre>` content is
            // preserved verbatim on both sides.
            Name = "whitespace-significant content"
            Ssr = "<div>\n  <pre>line 1\n  line 2</pre>\n</div>"
            Csr = "<div><pre>line 1\n  line 2</pre></div>"
        }
        {
            // Event-handler-bearing nodes: a React mount may surface an
            // `onclick` attribute (and `data-reactroot`) that never appears
            // in the SSR HTML; both are stripped.
            Name = "event-handler-bearing nodes"
            Ssr = "<button type=\"button\">Go</button>"
            Csr = "<button data-reactroot=\"\" onclick=\"d(1)\" type=\"button\">Go</button>"
        }
    ]

    /// A deliberately mismatched fixture: the two trees genuinely differ
    /// (the third list item's text), so `check` MUST return a `Divergence`
    /// naming the divergent node.
    let mismatchedFixture: Fixture = {
        Name = "deliberate text mismatch"
        Ssr = "<ul><li>One</li><li>Two</li></ul>"
        Csr = "<ul><li>One</li><li>Three</li></ul>"
    }