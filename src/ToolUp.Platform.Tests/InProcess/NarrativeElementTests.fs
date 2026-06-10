// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NarrativeElementTests

open Expecto
open ToolUp.Platform.Narrative

// ─── Phase 87 — media + layout narrative blocks ──────────────────
//
// Render-shape tests for the rich-content `NarrativeElement` cases
// (Video / Audio / ImageGallery / Embed / Card / Accordion / Tabs /
// Component) across all three pure renderers, plus the GP 11 guard
// that a legacy-only document still renders byte-for-byte unchanged.
//
// The renderers are pure (`NarrativeDocument -> string`), so the tests
// exercise them directly — no DI, no contract-pack indirection.

/// Normalise CRLF → LF so the byte-identity snapshots below are
/// platform-independent (StringBuilder.AppendLine emits the platform
/// newline).
let private norm (s: string) = s.Replace("\r\n", "\n")

/// Wrap a list of elements in a minimal single-section document.
let private docWith (elements: NarrativeElement list) : NarrativeDocument = {
    Title = "T"
    Subtitle = None
    Sections = [
        {
            Id = "s"
            Heading = "H"
            Subheading = None
            Elements = elements
        }
    ]
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

let private youTubeOptions = {
    NarrativeHtml.RenderOptions.Default with
        AllowedEmbedOrigins = Set.ofList [ "https://www.youtube.com" ]
}

let private componentOptions = {
    NarrativeHtml.RenderOptions.Default with
        ComponentRenderer =
            (fun name props ->
                if name = "callout-box" then
                    let text = props.TryFind "text" |> Option.defaultValue ""
                    Some(sprintf "<div class=\"cb\">%s</div>" text)
                else
                    None)
}

let private sampleVideo =
    Video {
        Sources = [
            {
                Src = "/media/intro.mp4"
                Type = Some "video/mp4"
            }
            {
                Src = "/media/intro.webm"
                Type = Some "video/webm"
            }
        ]
        Poster = Some "/media/intro.jpg"
        Tracks = [
            {
                Src = "/media/intro.en.vtt"
                Kind = "captions"
                Label = "English"
                SrcLang = Some "en"
                IsDefault = true
            }
        ]
        Caption = Some "Product intro"
    }

let private sampleGallery =
    ImageGallery [
        {
            Src = "/img/a.jpg"
            Alt = "Alpha shot"
            Caption = Some "Alpha"
            Href = Some "/img/a-full.jpg"
        }
        {
            Src = "/img/b.jpg"
            Alt = "Beta shot"
            Caption = None
            Href = None
        }
    ]

let tests =
    testList "NarrativeElement (Phase 87)" [

        // ─── GP 11 — additive cases don't disturb legacy output ──────
        test "GP 11 — legacy-only HTML renders byte-for-byte unchanged" {
            let html = norm (NarrativeHtml.render (docWith [ Paragraph [ Text "hi" ] ]))

            let expected =
                "<article class=\"narrative\">\n  <h1>T</h1>\n<section id=\"s\">\n  <h2>H</h2>\n<p>hi</p>\n</section>\n</article>"

            Expect.equal html expected "legacy HTML output is unchanged"
        }

        test "GP 11 — legacy-only Markdown renders byte-for-byte unchanged" {
            let md = norm (NarrativeMarkdown.render (docWith [ Paragraph [ Text "hi" ] ]))
            Expect.equal md "# T\n\n## H\n\nhi" "legacy Markdown output is unchanged"
        }

        test "GP 11 — legacy-only plaintext renders byte-for-byte unchanged" {
            let txt = norm (NarrativePlaintext.render (docWith [ Paragraph [ Text "hi" ] ]))
            Expect.equal txt "T\n=\n\nH\n=\nhi" "legacy plaintext output is unchanged"
        }

        // ─── Video ───────────────────────────────────────────────────
        test "Video — HTML uses semantic figure/video/source/track" {
            let html = NarrativeHtml.render (docWith [ sampleVideo ])
            Expect.stringContains html "<figure class=\"narrative-video\">" "figure wrapper"

            Expect.stringContains
                html
                "<video class=\"narrative-video__player\" controls poster=\"/media/intro.jpg\">"
                "video + poster"

            Expect.stringContains html "<source src=\"/media/intro.mp4\" type=\"video/mp4\" />" "first source"

            Expect.stringContains
                html
                "<track kind=\"captions\" src=\"/media/intro.en.vtt\" label=\"English\" srclang=\"en\" default />"
                "caption track"

            Expect.stringContains html "<figcaption>Product intro</figcaption>" "visible caption"
        }

        test "Video — Markdown degrades to poster image + source link" {
            let md = NarrativeMarkdown.render (docWith [ sampleVideo ])
            Expect.stringContains md "![Product intro](/media/intro.jpg)" "poster image"
            Expect.stringContains md "[▶ Product intro](/media/intro.mp4)" "source link"
        }

        test "Video — plaintext degrades to caption only" {
            let txt = NarrativePlaintext.render (docWith [ sampleVideo ])
            Expect.stringContains txt "[Video: Product intro]" "caption-only plaintext"
            Expect.isFalse (txt.Contains "/media/intro.mp4") "no source URL in plaintext video"
        }

        // ─── ImageGallery ────────────────────────────────────────────
        test "ImageGallery — HTML emits lightbox hooks + alt text" {
            let html = NarrativeHtml.render (docWith [ sampleGallery ])
            Expect.stringContains html "<div class=\"narrative-gallery\">" "gallery wrapper"

            Expect.stringContains
                html
                "<a class=\"narrative-gallery__lightbox\" href=\"/img/a-full.jpg\">"
                "explicit lightbox href"

            Expect.stringContains
                html
                "<a class=\"narrative-gallery__lightbox\" href=\"/img/b.jpg\">"
                "href falls back to src"

            Expect.stringContains html "alt=\"Alpha shot\"" "mandatory alt"
        }

        test "ImageGallery — plaintext is alt-text only" {
            let txt = NarrativePlaintext.render (docWith [ sampleGallery ])
            Expect.stringContains txt "[Alpha shot] — Alpha" "alt + caption"
            Expect.stringContains txt "[Beta shot]" "alt only when no caption"
        }

        // ─── Embed (CSP allowlist) ───────────────────────────────────
        test "Embed — allowlisted origin renders sandboxed iframe" {
            let doc =
                docWith [
                    Embed {
                        Url = "https://www.youtube.com/embed/abc"
                        Title = "Demo video"
                        AspectRatio = Some "16:9"
                    }
                ]

            let html = NarrativeHtml.renderWith youTubeOptions doc
            Expect.stringContains html "<iframe" "iframe emitted for allowed origin"
            Expect.stringContains html "narrative-embed--16-9" "aspect-ratio class hook"

            Expect.stringContains
                html
                "sandbox=\"allow-scripts allow-same-origin allow-popups allow-presentation\""
                "sandbox attribute"
        }

        test "Embed — unknown origin degrades to safe placeholder link" {
            let doc =
                docWith [
                    Embed {
                        Url = "https://evil.example.com/embed/abc"
                        Title = "Untrusted"
                        AspectRatio = None
                    }
                ]

            // youTubeOptions allows youtube.com only — evil.example.com is unknown.
            let html = NarrativeHtml.renderWith youTubeOptions doc
            Expect.isFalse (html.Contains "<iframe") "no iframe for unknown origin"
            Expect.stringContains html "narrative-embed--blocked" "blocked placeholder"

            Expect.stringContains
                html
                "<a href=\"https://evil.example.com/embed/abc\" rel=\"noopener nofollow\">Untrusted</a>"
                "safe link"
        }

        test "Embed — default deny-all renders placeholder even for a real provider" {
            let doc =
                docWith [
                    Embed {
                        Url = "https://www.youtube.com/embed/abc"
                        Title = "Demo"
                        AspectRatio = None
                    }
                ]

            // RenderOptions.Default has an EMPTY allowlist (secure-by-default).
            let html = NarrativeHtml.render doc
            Expect.isFalse (html.Contains "<iframe") "deny-all by default"
            Expect.stringContains html "narrative-embed--blocked" "placeholder by default"
        }

        // ─── Component (type-erasure boundary) ───────────────────────
        test "Component — registered renderer produces its HTML" {
            let doc = docWith [ Component("callout-box", Map.ofList [ "text", "Hello" ]) ]

            let html = NarrativeHtml.renderWith componentOptions doc
            Expect.stringContains html "<div class=\"cb\">Hello</div>" "registered component html"
        }

        test "Component — unregistered name degrades to safe placeholder" {
            let doc = docWith [ Component("not-registered", Map.empty) ]
            // Default resolver returns None for every name.
            let html = NarrativeHtml.render doc
            Expect.stringContains html "narrative-component--unresolved" "unresolved placeholder"
            Expect.stringContains html "data-component=\"not-registered\"" "name surfaced as data attribute"
        }

        // ─── Card / Accordion / Tabs + recursive nesting ─────────────
        test "Card — HTML wraps heading, image and nested body" {
            let doc =
                docWith [
                    Card {
                        Heading = Some "Featured"
                        Image =
                            Some {
                                Src = "/img/card.jpg"
                                Alt = "Card hero"
                                Caption = None
                                Href = None
                            }
                        Body = [ Paragraph [ Text "Inside the card" ] ]
                    }
                ]

            let html = NarrativeHtml.render doc
            Expect.stringContains html "<article class=\"narrative-card\">" "card wrapper"
            Expect.stringContains html "<h3 class=\"narrative-card__heading\">Featured</h3>" "card heading"
            Expect.stringContains html "<p>Inside the card</p>" "nested body element"
        }

        test "Accordion — HTML uses details/summary, plaintext keeps content" {
            let doc =
                docWith [
                    Accordion [
                        "Panel A", [ Paragraph [ Text "Body A" ] ]
                        "Panel B", [ Paragraph [ Text "Body B" ] ]
                    ]
                ]

            let html = NarrativeHtml.render doc
            Expect.stringContains html "<details class=\"narrative-accordion__panel\">" "details element"

            Expect.stringContains
                html
                "<summary class=\"narrative-accordion__heading\">Panel A</summary>"
                "summary heading"

            let txt = NarrativePlaintext.render doc
            Expect.stringContains txt "Body A" "panel content survives in plaintext"
            Expect.stringContains txt "Body B" "all panels visible in plaintext"
        }

        test "Tabs — HTML emits ARIA tablist with first tab selected" {
            let doc =
                docWith [
                    Tabs [
                        "Overview", [ Paragraph [ Text "First" ] ]
                        "Details", [ Paragraph [ Text "Second" ] ]
                    ]
                ]

            let html = NarrativeHtml.render doc
            Expect.stringContains html "role=\"tablist\"" "tablist role"

            Expect.stringContains
                html
                "id=\"tab-overview\" aria-controls=\"panel-overview\" aria-selected=\"true\""
                "first tab selected"

            Expect.stringContains
                html
                "id=\"tab-details\" aria-controls=\"panel-details\" aria-selected=\"false\""
                "second tab not selected"

            Expect.stringContains html "role=\"tabpanel\" id=\"panel-overview\"" "panel wired to tab"
        }

        test "Recursive nesting — accordion → card → paragraph renders through" {
            let doc =
                docWith [
                    Accordion [
                        "Outer",
                        [
                            Card {
                                Heading = None
                                Image = None
                                Body = [ Paragraph [ Text "deeply nested" ] ]
                            }
                        ]
                    ]
                ]

            let html = NarrativeHtml.render doc
            Expect.stringContains html "narrative-accordion__panel" "outer accordion"
            Expect.stringContains html "narrative-card" "inner card"
            Expect.stringContains html "<p>deeply nested</p>" "innermost paragraph"
        }

        // ─── Determinism / prerender safety ──────────────────────────
        test "Determinism — a rich document renders identically across runs" {
            let doc =
                docWith [
                    sampleVideo
                    sampleGallery
                    Accordion [ "A", [ Paragraph [ Text "x" ] ] ]
                    Tabs [ "T", [ Paragraph [ Text "y" ] ] ]
                    Component("callout-box", Map.ofList [ "text", "z" ])
                ]

            Expect.equal (NarrativeHtml.render doc) (NarrativeHtml.render doc) "HTML render is deterministic"

            Expect.equal
                (NarrativeMarkdown.render doc)
                (NarrativeMarkdown.render doc)
                "Markdown render is deterministic"

            Expect.equal
                (NarrativePlaintext.render doc)
                (NarrativePlaintext.render doc)
                "plaintext render is deterministic"
        }
    ]