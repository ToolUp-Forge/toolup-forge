// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text

// ─── Phase 274 — hosted-tree content sanitization seam (CSP-aligned) ────
//
// A hosted tree (Phase 110 / 111) carries rich content — markdown, code
// blocks, raw HTML — and once an AI or an untrusted server emits the tree,
// that content can inject unsafe HTML (a `<script>`, an `<iframe>`, a
// `javascript:` URL) or otherwise violate the deployment's Phase 9j CSP.
// The host surface shipped no sanitization seam, so safety was left to the
// tree language. This file ships the neutral `IHostContentSanitizer`:
// hosted content is sanitized against an allow-list BEFORE render —
// **default-deny** on untrusted HTML — so a hosted view can't become an
// injection vector regardless of who emitted the tree.
//
// **Aligned to the Phase 9j CSP (default-deny mirrors the header).** The
// default policy strips exactly what a hardened CSP forbids: no inline
// `<script>` / `<style>` (mirrors `script-src` / `style-src` without
// `'unsafe-inline'`), no `javascript:` / `vbscript:` / `data:` URL in an
// href/src (mirrors the source allow-lists), no `on*` event-handler
// attribute (mirrors the no-inline-handler posture). It is a **content**
// allow-list, not the CSP itself — the CSP is the transport-header
// enforcement, this is the emitted-content enforcement, and the two agree
// by construction so a hosted view never emits content its own CSP would
// block. A consumer can supply a STRICTER policy; there is no weaker
// silent default (the production-hardening discipline — GP 2).
//
// **Renderer-neutral (GP 1) + shared client/SSR (GP 10).** The sanitizer
// operates on content STRINGS + a neutral `HostContentKind` tag
// (markdown / html / code / plain); no tree-language type appears. It is
// BCL-only (no Feliz, no regex — a hand-rolled scanner exactly like
// `HydrationParity`) so it compiles under BOTH Fable (client, Phase 110)
// and .NET (SSR, Phase 111) and produces BYTE-IDENTICAL output on both
// legs — no sanitize-on-one-side hydration mismatch. Lives in
// `ToolUp.Platform.Core` (the shared floor) because both render paths call
// the same code.
//
// **Zero cost when unused (GP 13) + byte-for-byte unchanged (GP 11).** A
// deployment that renders no hosted content never constructs a sanitizer
// and pays nothing; nothing about the pre-274 render path changes.

/// The neutral kind of a hosted content string — enough for the sanitizer
/// to pick the right safe transform, without naming any tree-language type
/// (GP 1).
[<RequireQualifiedAccess>]
type HostContentKind =
    /// Untrusted HTML — sanitized against the tag / attribute / URL-scheme
    /// allow-list (default-deny).
    | Html
    /// Markdown — HTML-escaped first (so any embedded raw HTML is inert),
    /// then a small set of SAFE inline/block transforms is applied.
    | Markdown
    /// A code block — escaped verbatim and wrapped in `<pre><code>` (never
    /// interpreted as markup).
    | Code
    /// Plain text — fully HTML-escaped; no markup survives.
    | PlainText

/// The result of sanitizing one content string. `Html` is the safe,
/// ready-to-inject string; `Modified` is `true` when the sanitizer removed
/// or neutralised something unsafe (a stripped tag, a blocked URL scheme,
/// a dropped attribute, a stripped comment) — so a caller can surface /
/// log that content was altered rather than silently swallow it.
type SanitizedContent = { Html: string; Modified: bool }

/// The allow-list the sanitizer enforces on `Html` content. Everything not
/// on the list is denied (default-deny). Tag / attribute names are matched
/// lower-cased; URL schemes are matched lower-cased against `AllowedUrlSchemes`.
type HostSanitizePolicy = {
    /// Element tags allowed through verbatim (with filtered attributes).
    AllowedTags: Set<string>
    /// Attribute names allowed on any permitted tag. `on*` handlers and
    /// `style` are ALWAYS dropped regardless of this set.
    AllowedAttributes: Set<string>
    /// URL schemes permitted in an href/src-style attribute value. A value
    /// with no scheme (relative / anchor) is always allowed; a value whose
    /// scheme is not listed (e.g. `javascript:`) drops the attribute.
    AllowedUrlSchemes: Set<string>
}

[<RequireQualifiedAccess>]
module HostSanitizePolicy =

    /// The CSP-aligned default allow-list: common text-formatting +
    /// structural tags, safe presentational attributes, and http/https/
    /// mailto/tel URL schemes only. No `<script>` / `<style>` / `<iframe>`
    /// (they are default-denied — never on this list), no `javascript:`
    /// URL, no `on*` handler.
    let default': HostSanitizePolicy = {
        AllowedTags =
            Set.ofList [
                "a"
                "abbr"
                "b"
                "blockquote"
                "br"
                "code"
                "dd"
                "div"
                "dl"
                "dt"
                "em"
                "h1"
                "h2"
                "h3"
                "h4"
                "h5"
                "h6"
                "hr"
                "i"
                "img"
                "li"
                "ol"
                "p"
                "pre"
                "small"
                "span"
                "strong"
                "sub"
                "sup"
                "table"
                "tbody"
                "td"
                "tfoot"
                "th"
                "thead"
                "tr"
                "u"
                "ul"
            ]
        AllowedAttributes = Set.ofList [ "alt"; "class"; "colspan"; "href"; "rowspan"; "src"; "title" ]
        AllowedUrlSchemes = Set.ofList [ "http"; "https"; "mailto"; "tel" ]
    }

/// Sanitize hosted-tree content against a policy before render. Composed
/// only when a host renders content; the default is the safe one (GP 2 —
/// no weaker silent default).
type IHostContentSanitizer =
    /// Sanitize `content` of the given `kind`, returning safe HTML plus a
    /// flag of whether anything was removed/neutralised. Must be pure and
    /// deterministic so the client (Phase 110) and SSR (Phase 111) legs
    /// produce byte-identical output.
    abstract Sanitize: kind: HostContentKind -> content: string -> SanitizedContent

[<RequireQualifiedAccess>]
module HostContentSanitizer =

    // ─── escaping ─────────────────────────────────────────────────────

    /// HTML-escape a text run — the five significant characters. Applied to
    /// every text node and to all `PlainText` / `Code` content, so no text
    /// can break out into markup.
    let escape (s: string) : string =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;")

    // ─── URL-scheme safety ────────────────────────────────────────────

    /// Is a URL attribute value safe under `schemes`? A relative / anchor
    /// value (no scheme) is always safe; a scheme-bearing value is safe
    /// only when its scheme is allow-listed. Control characters and
    /// whitespace are stripped before the scheme is read, so obfuscations
    /// like `java\tscript:` are neutralised.
    let private isSafeUrl (schemes: Set<string>) (raw: string) : bool =
        // Drop whitespace + control chars entirely, then lower-case.
        let cleaned =
            raw
            |> Seq.filter (fun c -> c > ' ')
            |> Seq.toArray
            |> System.String
            |> _.ToLowerInvariant()

        if cleaned = "" then
            true
        else
            let colon = cleaned.IndexOf ':'

            if colon < 0 then
                true // no scheme → relative
            else
                let scheme = cleaned.Substring(0, colon)
                // A ':' after a '/', '#' or '?' is a path/query colon, not a
                // scheme separator — treat as relative.
                let slash = cleaned.IndexOfAny([| '/'; '#'; '?' |])

                if slash >= 0 && slash < colon then
                    true
                elif
                    scheme
                    |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '+' || c = '-' || c = '.')
                then
                    schemes.Contains scheme
                else
                    true

    /// Attribute names whose value is a URL and must pass the scheme check.
    let private urlAttributes =
        Set.ofList [
            "href"
            "src"
            "action"
            "formaction"
            "poster"
            "background"
            "cite"
            "xlink:href"
        ]

    /// Tags whose ENTIRE content is unsafe and is dropped (open, children,
    /// close) — the default-deny raw-content set.
    let private dangerousElements =
        Set.ofList [ "script"; "style"; "iframe"; "object"; "embed"; "noscript"; "template" ]

    /// Dangerous void elements — dropped (no matching close to suppress to).
    let private dangerousVoids = Set.ofList [ "link"; "meta"; "base" ]

    /// HTML5 void elements (no end tag) — relevant allowed ones are `br`,
    /// `hr`, `img`.
    let private voidElements =
        Set.ofList [ "br"; "hr"; "img"; "area"; "col"; "input"; "source"; "track"; "wbr" ]

    let private isWhitespace (c: char) =
        c = ' ' || c = '\t' || c = '\n' || c = '\r' || c = '\f'

    // ─── HTML tokeniser (BCL-only, no regex — Fable-safe) ──────────────

    type private Token =
        | Open of name: string * attrs: (string * string option) list
        | Close of name: string
        | Text of string
        | Comment

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
                    let stop = if endIdx < 0 then n else endIdx + 3
                    acc.Add Comment
                    i <- stop
                elif i + 1 < n && html[i + 1] = '!' then
                    let endIdx = html.IndexOf('>', i)
                    i <- (if endIdx < 0 then n else endIdx + 1)
                elif i + 1 < n && html[i + 1] = '/' then
                    let name, j = readName (i + 2)
                    let endIdx = html.IndexOf('>', j)
                    acc.Add(Close name)
                    i <- (if endIdx < 0 then n else endIdx + 1)
                else
                    let name, j = readName (i + 1)
                    let attrs = System.Collections.Generic.List<string * string option>()
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
                                    attrs.Add(aName, Some(html.Substring(vStart, stopV - vStart)))
                                    kk <- (if vEnd < 0 then n else vEnd + 1)
                                else
                                    let vStart = kk

                                    while kk < n && not (isWhitespace html[kk]) && html[kk] <> '>' && html[kk] <> '/' do
                                        kk <- kk + 1

                                    attrs.Add(aName, Some(html.Substring(vStart, kk - vStart)))

                                k <- kk
                            else
                                if aName <> "" then
                                    attrs.Add(aName, None)

                                k <- ak

                    acc.Add(Open(name, List.ofSeq attrs))
                    i <- k
            else
                let start = i

                while i < n && html[i] <> '<' do
                    i <- i + 1

                acc.Add(Text(html.Substring(start, i - start)))

        List.ofSeq acc

    // ─── attribute filtering ──────────────────────────────────────────

    let private isHandler (name: string) =
        name.Length > 2
        && (name[0] = 'o' || name[0] = 'O')
        && (name[1] = 'n' || name[1] = 'N')

    /// Filter one open tag's attributes against the policy. Returns the
    /// kept attributes and whether anything was dropped.
    let private filterAttrs (policy: HostSanitizePolicy) (attrs: (string * string option) list) =
        let mutable modified = false

        let kept =
            attrs
            |> List.choose (fun (name, value) ->
                let l = name.ToLowerInvariant()

                if isHandler l || l = "style" then
                    modified <- true
                    None // event handlers + inline style always dropped
                elif urlAttributes.Contains l then
                    match value with
                    | Some v when isSafeUrl policy.AllowedUrlSchemes v -> Some(l, value)
                    | _ ->
                        modified <- true
                        None // unsafe / schemeless-blocked URL dropped
                elif policy.AllowedAttributes.Contains l then
                    Some(l, value)
                else
                    modified <- true
                    None // unknown attribute default-denied)

            )

        kept, modified

    let private renderAttrs (attrs: (string * string option) list) =
        attrs
        |> List.map (fun (name, value) ->
            match value with
            | None -> " " + name
            | Some v -> " " + name + "=\"" + escape v + "\"")
        |> String.concat ""

    // ─── HTML sanitiser ───────────────────────────────────────────────

    let private sanitizeHtml (policy: HostSanitizePolicy) (html: string) : SanitizedContent =
        let tokens = tokenize html
        let out = StringBuilder()
        let mutable modified = false
        // While inside a dangerous element, drop everything until its
        // matching close. Track the tag + nesting depth.
        let mutable suppressTag = None
        let mutable suppressDepth = 0

        for tok in tokens do
            match suppressTag with
            | Some t ->
                match tok with
                | Open(name, _) when name.ToLowerInvariant() = t && not (voidElements.Contains t) ->
                    suppressDepth <- suppressDepth + 1
                | Close name when name.ToLowerInvariant() = t ->
                    suppressDepth <- suppressDepth - 1

                    if suppressDepth = 0 then
                        suppressTag <- None
                | _ -> () // dropped
            | None ->
                match tok with
                | Comment -> modified <- true // comments dropped (can hide CDATA / IE conditionals)
                | Text s -> out.Append(escape s) |> ignore // text can contain no '<' (tokeniser split); escape & > " '
                | Open(name, attrs) ->
                    let lname = name.ToLowerInvariant()

                    if dangerousElements.Contains lname then
                        suppressTag <- Some lname
                        suppressDepth <- 1
                        modified <- true
                    elif dangerousVoids.Contains lname then
                        modified <- true // dropped
                    elif policy.AllowedTags.Contains lname then
                        let kept, attrMod = filterAttrs policy attrs
                        modified <- modified || attrMod
                        out.Append("<" + lname + renderAttrs kept + ">") |> ignore
                    else
                        modified <- true // unknown tag unwrapped (markers dropped, children kept)
                | Close name ->
                    let lname = name.ToLowerInvariant()

                    if policy.AllowedTags.Contains lname && not (voidElements.Contains lname) then
                        out.Append("</" + lname + ">") |> ignore
                    else
                        modified <- true // unknown/void close dropped

        {
            Html = out.ToString()
            Modified = modified
        }

    // ─── safe markdown (escape-first, then a small transform set) ──────

    /// Replace every `open X close` span with `before`/`after` wrappers.
    /// Operates on the already-escaped string; `marker` never contains a
    /// character escaping produces, so the scan is safe.
    let private wrapSpans (marker: string) (before: string) (after: string) (s: string) : string * bool =
        let mutable modified = false
        let sb = StringBuilder()
        let mutable idx = 0
        let mlen = marker.Length

        let mutable searching = true

        while searching do
            let start = s.IndexOf(marker, idx)

            if start < 0 then
                sb.Append(s.Substring idx) |> ignore
                searching <- false
            else
                let close = s.IndexOf(marker, start + mlen)

                if close < 0 then
                    sb.Append(s.Substring idx) |> ignore
                    searching <- false
                else
                    sb.Append(s.Substring(idx, start - idx)) |> ignore
                    let inner = s.Substring(start + mlen, close - start - mlen)
                    sb.Append(before).Append(inner).Append(after) |> ignore
                    idx <- close + mlen
                    modified <- true

        sb.ToString(), modified

    /// Convert `[text](url)` links, dropping the href when the URL is unsafe
    /// (the link text is kept). Runs on the escaped string.
    let private linkify (policy: HostSanitizePolicy) (s: string) : string * bool =
        let mutable modified = false
        let sb = StringBuilder()
        let n = s.Length
        let mutable i = 0

        while i < n do
            if s[i] = '[' then
                let closeText = s.IndexOf(']', i)

                if closeText > 0 && closeText + 1 < n && s[closeText + 1] = '(' then
                    let closeUrl = s.IndexOf(')', closeText + 2)

                    if closeUrl > 0 then
                        let text = s.Substring(i + 1, closeText - i - 1)
                        let url = s.Substring(closeText + 2, closeUrl - closeText - 2)
                        modified <- true

                        if isSafeUrl policy.AllowedUrlSchemes url then
                            sb.Append("<a href=\"").Append(url).Append("\">").Append(text).Append("</a>")
                            |> ignore
                        else
                            sb.Append text |> ignore // unsafe URL → keep text, drop link

                        i <- closeUrl + 1
                    else
                        sb.Append s[i] |> ignore
                        i <- i + 1
                else
                    sb.Append s[i] |> ignore
                    i <- i + 1
            else
                sb.Append s[i] |> ignore
                i <- i + 1

        sb.ToString(), modified

    let private sanitizeMarkdown (policy: HostSanitizePolicy) (md: string) : SanitizedContent =
        // 1. Escape first — any embedded raw HTML becomes inert text.
        let escaped = escape md
        let mutable modified = escaped <> md
        // 2. Safe inline transforms on the escaped string.
        let s1, m1 = linkify policy escaped
        let s2, m2 = wrapSpans "`" "<code>" "</code>" s1
        let s3, m3 = wrapSpans "**" "<strong>" "</strong>" s2
        let s4, m4 = wrapSpans "*" "<em>" "</em>" s3
        modified <- modified || m1 || m2 || m3 || m4

        { Html = s4; Modified = modified }

    // ─── the sanitizer ────────────────────────────────────────────────

    /// Build a sanitizer over an explicit policy. A consumer supplies a
    /// STRICTER policy here; there is no constructor for a weaker one than
    /// the default (GP 2).
    let create (policy: HostSanitizePolicy) : IHostContentSanitizer =
        { new IHostContentSanitizer with
            member _.Sanitize kind content =
                match kind with
                | HostContentKind.Html -> sanitizeHtml policy content
                | HostContentKind.Markdown -> sanitizeMarkdown policy content
                | HostContentKind.Code -> {
                    Html = "<pre><code>" + escape content + "</code></pre>"
                    Modified = escape content <> content
                  }
                | HostContentKind.PlainText -> {
                    Html = escape content
                    Modified = escape content <> content
                  }
        }

    /// The default sanitizer — the CSP-aligned `HostSanitizePolicy.default'`.
    /// This is the safe default that is ON when content rendering is used;
    /// a host that wants stricter supplies its own via `create`.
    let default': IHostContentSanitizer = create HostSanitizePolicy.default'