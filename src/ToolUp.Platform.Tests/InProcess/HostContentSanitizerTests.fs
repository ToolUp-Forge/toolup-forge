module ToolUp.Platform.Tests.InProcess.HostContentSanitizerTests

open System.IO
open Expecto
open ToolUp.Platform

// ─── Phase 274 — hosted-tree content sanitization seam ─────────────────
//
// The default-deny content sanitizer. Five concerns:
//   1. Injection classes stripped — script/style/iframe elements, a
//      `javascript:` URL, an `on*` handler, an inline `style`, an unknown
//      tag/attribute.
//   2. Safe content preserved — allow-listed tags + attributes + http(s)
//      links survive verbatim.
//   3. Markdown is escape-first — embedded raw HTML is inert; safe inline
//      transforms (bold / italic / code / link) apply; a `javascript:`
//      link keeps its text but drops the href.
//   4. Determinism / client↔SSR parity — the sanitizer is a pure function
//      of (kind, content, policy), so the two render legs (which call the
//      same Core code) produce byte-identical output; sanitizing an
//      already-sanitized string is a fixed point.
//   5. The `Modified` flag + the OSS grep-guard.

let private sanitizer = HostContentSanitizer.default'

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

// ─── 1. Injection classes stripped ────────────────────────────────────

let private injectionTests =
    testList "Phase 274 — injection classes stripped (default-deny)" [
        testCase "a <script> element and its content are dropped entirely"
        <| fun _ ->
            let out =
                sanitizer.Sanitize HostContentKind.Html "<p>ok</p><script>alert(1)</script>"

            Expect.stringContains out.Html "<p>ok</p>" "the safe paragraph survives"
            Expect.isFalse (out.Html.Contains "alert") "the script body is gone"
            Expect.isFalse (out.Html.Contains "<script") "the script tag is gone"
            Expect.isTrue out.Modified "the sanitizer reports it modified the content"

        testCase "an <iframe> is dropped"
        <| fun _ ->
            let out =
                sanitizer.Sanitize HostContentKind.Html """<div><iframe src="evil"></iframe>hi</div>"""

            Expect.isFalse (out.Html.Contains "iframe") "iframe stripped"
            Expect.stringContains out.Html "hi" "sibling text survives"

        testCase "a javascript: URL drops the href but keeps the anchor"
        <| fun _ ->
            let out =
                sanitizer.Sanitize HostContentKind.Html """<a href="javascript:alert(1)">click</a>"""

            Expect.isFalse (out.Html.Contains "javascript:") "the unsafe scheme is gone"
            Expect.stringContains out.Html "click" "the link text survives"

        testCase "obfuscated java\\tscript: is still blocked"
        <| fun _ ->
            let out =
                sanitizer.Sanitize HostContentKind.Html "<a href=\"java\tscript:alert(1)\">x</a>"

            Expect.isFalse (out.Html.ToLowerInvariant().Contains "javascript") "control-char obfuscation neutralised"

        testCase "an on* event handler attribute is dropped"
        <| fun _ ->
            let out =
                sanitizer.Sanitize HostContentKind.Html """<p onclick="steal()">text</p>"""

            Expect.isFalse (out.Html.Contains "onclick") "the handler attribute is gone"
            Expect.stringContains out.Html "<p>text</p>" "the tag + text survive without the handler"

        testCase "an inline style attribute is dropped"
        <| fun _ ->
            let out =
                sanitizer.Sanitize HostContentKind.Html """<div style="position:absolute">x</div>"""

            Expect.isFalse (out.Html.Contains "style") "inline style stripped (CSP-aligned)"

        testCase "an unknown tag is unwrapped and an unknown attribute dropped"
        <| fun _ ->
            let out =
                sanitizer.Sanitize HostContentKind.Html """<marquee data-x="1"><b>hey</b></marquee>"""

            Expect.isFalse (out.Html.Contains "marquee") "unknown tag markers dropped"
            Expect.isFalse (out.Html.Contains "data-x") "unknown attribute dropped"
            Expect.stringContains out.Html "<b>hey</b>" "allow-listed children survive"
    ]

// ─── 2. Safe content preserved ────────────────────────────────────────

let private preservationTests =
    testList "Phase 274 — safe content preserved" [
        testCase "allow-listed tags + attributes + an https link survive verbatim"
        <| fun _ ->
            let safe =
                """<p class="lead">See <a href="https://toolup.pro" title="home">the site</a>.</p>"""

            let out = sanitizer.Sanitize HostContentKind.Html safe
            Expect.equal out.Html safe "fully-safe HTML passes through byte-for-byte"
            Expect.isFalse out.Modified "nothing was modified"

        testCase "a relative URL is allowed (no scheme)"
        <| fun _ ->
            let out = sanitizer.Sanitize HostContentKind.Html """<a href="/docs/x">docs</a>"""

            Expect.stringContains out.Html "href=\"/docs/x\"" "relative href preserved"
    ]

// ─── 3. Markdown — escape-first + safe transforms ─────────────────────

let private markdownTests =
    testList "Phase 274 — markdown is escape-first" [
        testCase "embedded raw HTML in markdown is inert (escaped, not executed)"
        <| fun _ ->
            let out =
                sanitizer.Sanitize HostContentKind.Markdown "hello <script>alert(1)</script>"

            Expect.isFalse (out.Html.Contains "<script>") "no live script tag"
            Expect.stringContains out.Html "&lt;script&gt;" "the raw HTML is escaped to visible text"

        testCase "safe inline transforms apply"
        <| fun _ ->
            let out =
                sanitizer.Sanitize HostContentKind.Markdown "a **bold** and *em* and `code`"

            Expect.stringContains out.Html "<strong>bold</strong>" "bold"
            Expect.stringContains out.Html "<em>em</em>" "italic"
            Expect.stringContains out.Html "<code>code</code>" "code span"

        testCase "a safe markdown link becomes an anchor; a javascript: link keeps only its text"
        <| fun _ ->
            let safe = sanitizer.Sanitize HostContentKind.Markdown "[site](https://toolup.pro)"
            Expect.stringContains safe.Html """<a href="https://toolup.pro">site</a>""" "safe link linkified"

            let unsafe = sanitizer.Sanitize HostContentKind.Markdown "[x](javascript:alert(1))"
            Expect.isFalse (unsafe.Html.Contains "javascript:") "unsafe link scheme dropped"
            Expect.stringContains unsafe.Html "x" "link text retained"

        testCase "Code kind escapes and wraps, never interprets markup"
        <| fun _ ->
            let out = sanitizer.Sanitize HostContentKind.Code "<b>not bold</b>"
            Expect.stringContains out.Html "<pre><code>" "wrapped as a code block"
            Expect.stringContains out.Html "&lt;b&gt;not bold&lt;/b&gt;" "markup escaped verbatim"
    ]

// ─── 4. Determinism / client↔SSR parity ───────────────────────────────

let private parityTests =
    testList "Phase 274 — determinism + client↔SSR parity" [
        testCase "the sanitizer is a pure function — same input, byte-identical output"
        <| fun _ ->
            // The client (Phase 110) and SSR (Phase 111) legs call this same
            // Core code, so identical input ⇒ identical output ⇒ no
            // sanitize-on-one-side hydration mismatch.
            let input = """<p onclick="x">a <script>b</script><a href="https://ok">l</a></p>"""
            let a = (sanitizer.Sanitize HostContentKind.Html input).Html
            let b = (sanitizer.Sanitize HostContentKind.Html input).Html
            Expect.equal a b "two sanitizations of one input are byte-identical"

        testCase "sanitizing an already-sanitized string is a fixed point"
        <| fun _ ->
            let once =
                (sanitizer.Sanitize HostContentKind.Html """<p onclick="x">safe</p>""").Html

            let twice = (sanitizer.Sanitize HostContentKind.Html once).Html
            Expect.equal once twice "re-sanitizing safe output does not change it"
    ]

// ─── 5. OSS grep-guard ────────────────────────────────────────────────

let private ossTests =
    testList "Phase 274 — OSS boundary" [
        testCase "the sanitizer source carries no banned OSS vocabulary"
        <| fun _ ->
            let path =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Core", "Shared", "Types", "HostContentSanitizer.fs")

            Expect.isTrue (File.Exists path) (sprintf "expected the seam file at %s" path)
            let contents = (File.ReadAllText path).ToLowerInvariant()
            Expect.isFalse (contents.Contains "fuaran") "the sanitizer must name no private layer (GP 1)"
    ]

let tests =
    testList "HostContentSanitizer (Phase 274)" [
        injectionTests
        preservationTests
        markdownTests
        parityTests
        ossTests
    ]