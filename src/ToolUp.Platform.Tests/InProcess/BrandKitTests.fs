module ToolUp.Platform.Tests.InProcess.BrandKitTests

open Expecto
open Giraffe.ViewEngine
open ToolUp.BrandKit

/// Phase 81 — `ToolUp.BrandKit` primitive rendering tests. Each
/// primitive emits the documented semantic markup with the right class
/// hooks + inline-style references. The package ships zero styling —
/// these tests pin only the markup contract consumers depend on.

let private render (node: XmlNode) : string = RenderView.AsString.htmlNode node

let tests =
    testList "ToolUp.BrandKit — primitives" [

        // ─── Text primitives ──────────────────────────────────

        testCase "displayLarge wraps the text in a div with .bk-display .bk-display-lg"
        <| fun _ ->
            let html = Text.displayLarge "Hello" |> render
            Expect.stringContains html "bk-display bk-display-lg" "class hooks present"
            Expect.stringContains html ">Hello<" "text is the element body"

        testCase "eyebrow wraps the text in a span with .bk-eyebrow"
        <| fun _ ->
            let html = Text.eyebrow "SECTION" |> render
            Expect.stringContains html "bk-eyebrow" "class hook present"
            Expect.stringContains html ">SECTION<" "text is the element body"

        testCase "hRule emits a div with .bk-rule"
        <| fun _ ->
            let html = Text.hRule |> render
            Expect.stringContains html "bk-rule" "class hook present"

        // ─── Wordmark ─────────────────────────────────────────

        testCase "wordmark splits into stem + emphasis + optional tail with EmphasisColour applied inline"
        <| fun _ ->
            let spec: Wordmark.WordmarkSpec = {
                Stem = "Ella-"
                Emphasis = "MA"
                EmphasisColour = "#6B5FBF"
                Tail = Some "e"
            }

            let html = Wordmark.wordmark spec |> render
            Expect.stringContains html "bk-wordmark" "outer class hook present"
            Expect.stringContains html "bk-wordmark-emphasis" "emphasis-span class hook present"
            Expect.stringContains html "Ella-" "stem rendered"
            Expect.stringContains html ">MA<" "emphasis substring rendered"
            Expect.stringContains html "e</span>" "tail rendered"
            Expect.stringContains html "color:#6B5FBF;" "EmphasisColour applied as inline style"

        testCase "wordmark without Tail omits the trailing italic span"
        <| fun _ ->
            let spec: Wordmark.WordmarkSpec = {
                Stem = "Scale"
                Emphasis = "Mastery"
                EmphasisColour = "#C57A3C"
                Tail = None
            }

            let html = Wordmark.wordmark spec |> render
            Expect.stringContains html "Scale" "stem rendered"
            Expect.stringContains html ">Mastery<" "emphasis rendered"
            // Markup ends with the closing emphasis span + outer wordmark span — no trailing text node.
            Expect.stringContains html "Mastery</span></span>" "no tail when Tail=None"

        // ─── Card ─────────────────────────────────────────────

        testCase "card wraps children in a section.bk-card"
        <| fun _ ->
            let html = Card.card [ Text.eyebrow "TEST" ] |> render
            Expect.stringContains html "<section class=\"bk-card\"" "section wrapper with class hook"
            Expect.stringContains html "bk-eyebrow" "child markup nested inside"

        testCase "cardTight adds the bk-card-tight modifier class"
        <| fun _ ->
            let html = Card.cardTight [] |> render
            Expect.stringContains html "bk-card bk-card-tight" "modifier class appended"

        testCase "cardDeep adds the bk-card-deep modifier class"
        <| fun _ ->
            let html = Card.cardDeep [] |> render
            Expect.stringContains html "bk-card bk-card-deep" "modifier class appended"

        // ─── Pill ─────────────────────────────────────────────

        testCase "pill emits a plain span.bk-tag"
        <| fun _ ->
            let html = Pill.pill "NEW" |> render
            Expect.stringContains html "<span class=\"bk-tag\"" "span with class hook"
            Expect.stringContains html ">NEW<" "text rendered"

        testCase "pillWithDot emits a leading aria-hidden dot span before the text"
        <| fun _ ->
            let html = Pill.pillWithDot "WATCH" |> render
            Expect.stringContains html "bk-tag-dot" "dot class hook present"
            Expect.stringContains html "aria-hidden=\"true\"" "dot is aria-hidden"
            Expect.stringContains html ">WATCH<" "text rendered after the dot"

        testCase "pillSeverity Priority maps to bk-tag-priority"
        <| fun _ ->
            let html = Pill.pillSeverity Pill.Severity.Priority "ALERT" |> render
            Expect.stringContains html "bk-tag bk-tag-priority" "severity modifier mapped"

        testCase "pillSeverity Positive maps to bk-tag-positive"
        <| fun _ ->
            let html = Pill.pillSeverity Pill.Severity.Positive "DONE" |> render
            Expect.stringContains html "bk-tag bk-tag-positive" "severity modifier mapped"

        // ─── Persona ──────────────────────────────────────────

        testCase "personaAvatar emits an img with class hook, sizing attributes, and object-position crop"
        <| fun _ ->
            let spec: Persona.PersonaSpec = {
                ImageUrl = "/persona.png"
                AltText = "Acme Co"
                Size = 56
                Treatment = Persona.AvatarTreatment.Circle
                RingColour = Some "#D9C4A4"
            }

            let html = Persona.personaAvatar spec |> render
            Expect.stringContains html "bk-persona bk-persona-circle" "class hook + treatment modifier"
            Expect.stringContains html "src=\"/persona.png\"" "image URL emitted"
            Expect.stringContains html "alt=\"Acme Co\"" "alt text emitted"
            Expect.stringContains html "width=\"56\"" "sizing emitted"
            Expect.stringContains html "object-fit:cover" "face-crop convention applied inline"
            Expect.stringContains html "border:2px solid #D9C4A4" "ring colour applied inline"

        // ─── Icon ─────────────────────────────────────────────

        testCase "iconSvg emits the canonical 24px / 1.5px stroke / currentColor shell with each path"
        <| fun _ ->
            let spec: Icon.IconSpec = {
                Paths = [ "M12 12l7.5-4.5"; "M3.5 12a8.5 8.5 0 0 1 8.5-8.5" ]
                Dots = Some [ (12.0, 12.0, 1.6) ]
            }

            let html = Icon.iconSvg spec |> render
            Expect.stringContains html "viewBox=\"0 0 24 24\"" "canonical viewBox"
            Expect.stringContains html "width=\"24\"" "default size"
            Expect.stringContains html "stroke-width=\"1.5\"" "canonical stroke"
            Expect.stringContains html "stroke=\"currentColor\"" "currentColor inheritance"
            Expect.stringContains html "stroke-linecap=\"round\"" "round joins"
            Expect.stringContains html "d=\"M12 12l7.5-4.5\"" "first path emitted"
            Expect.stringContains html "d=\"M3.5 12a8.5 8.5 0 0 1 8.5-8.5\"" "second path emitted"
            Expect.stringContains html "fill=\"currentColor\"" "dot fill is currentColor"

        // ─── PageChrome ───────────────────────────────────────

        testCase "pageHeader with a Monogram brand emits an img and the nav links"
        <| fun _ ->
            let spec: PageChrome.HeaderSpec = {
                Brand = PageChrome.BrandLockup.Monogram("/logo.svg", "MyApp")
                Nav = [ { Label = "Home"; Href = "/" }; { Label = "Docs"; Href = "/docs" } ]
                Right = []
            }

            let html = PageChrome.pageHeader spec |> render
            Expect.stringContains html "<header class=\"bk-header\"" "header element with class hook"
            Expect.stringContains html "src=\"/logo.svg\"" "monogram image emitted"
            Expect.stringContains html "href=\"/\"" "Home nav link"
            Expect.stringContains html "href=\"/docs\"" "Docs nav link"

        testCase "pageHeader with a Wordmark brand emits the wordmark markup"
        <| fun _ ->
            let spec: PageChrome.HeaderSpec = {
                Brand =
                    PageChrome.BrandLockup.Wordmark {
                        Stem = "Ella-"
                        Emphasis = "MA"
                        EmphasisColour = "#6B5FBF"
                        Tail = Some "e"
                    }
                Nav = []
                Right = []
            }

            let html = PageChrome.pageHeader spec |> render
            Expect.stringContains html "bk-wordmark" "wordmark rendered inside the header"
            Expect.stringContains html ">MA<" "emphasis substring rendered"

        testCase "pageFooter emits a copyright span and link items"
        <| fun _ ->
            // ASCII-only copyright text — Giraffe.ViewEngine's `str`
            // HTML-encodes non-ASCII (`©` → `&#x000A9;`) which is the
            // correct behaviour for rendered HTML but defeats a
            // literal-string assertion. Pin only what the test cares
            // about: the copyright span exists with its class hook,
            // the link hrefs emit verbatim, and link items carry
            // their class hook.
            let spec: PageChrome.FooterSpec = {
                Copyright = "Test Inc 2026"
                Links = [
                    { Label = "Terms"; Href = "/terms" }
                    { Label = "Privacy"; Href = "/privacy" }
                ]
            }

            let html = PageChrome.pageFooter spec |> render
            Expect.stringContains html "bk-footer-copyright" "copyright span class hook"
            Expect.stringContains html ">Test Inc 2026<" "copyright text emitted"
            Expect.stringContains html "href=\"/terms\"" "Terms link"
            Expect.stringContains html "href=\"/privacy\"" "Privacy link"
            Expect.stringContains html "bk-footer-link" "footer-link class hook on each link"
    ]