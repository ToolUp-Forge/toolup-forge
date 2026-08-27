module ToolUp.Platform.Tests.InProcess.BrandKitLayoutTests

open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open Giraffe.ViewEngine
open ToolUp.BrandKit
open ToolUp.BrandKit.Layouts

module BkText = ToolUp.BrandKit.Text

/// Phase 92 — BrandKit layout-library render-shape tests. The seven
/// layouts are pure `ChromeSpec -> spec -> XmlNode` functions; these
/// tests pin the markup contract — the accessibility baseline (skip
/// link, landmarks, single `<h1>`, focus target), the class hooks the
/// reference stylesheet sizes, and the optional-slot rule (a `None`
/// slot omits its wrapper entirely). Styling itself is consumer CSS
/// over the `--bk-*` variables, so nothing visual is asserted —
/// theming across contrasting brand-token sets is markup-invariant by
/// construction (the hooks are identical; only the consumer's `:root`
/// changes).

let private render (node: XmlNode) : string = RenderView.AsString.htmlNode node

let private countOf (needle: string) (haystack: string) : int = (haystack.Split needle).Length - 1

let private sentinel (name: string) : XmlNode = div [ _class ("sentinel-" + name) ] []

let private chrome = {
    Chrome.create "Test Page" with
        Description = Some "A test description"
        Stylesheets = [ "/brand.css" ]
        Header =
            Some(
                PageChrome.pageHeader {
                    Brand = PageChrome.Monogram("/m.svg", "Brand")
                    Nav = [ { Label = "Home"; Href = "/" } ]
                    Right = []
                }
            )
        Footer = Some(PageChrome.pageFooter { Copyright = "© Test"; Links = [] })
        BodyClass = Some "theme-test"
}

let private fullArticle: ArticleSpec = {
    Eyebrow = Some "NEWS"
    Title = "Headline"
    Lede = Some "The standfirst."
    Meta = [ Pill.pill "tag-one" ]
    Hero = Some(sentinel "hero")
    Body = sentinel "body"
    Aside = Some(sentinel "aside")
    Breadcrumb = Some(sentinel "crumbs")
}

// ─── Phase 197 — snapshot + theming-contract support ───────────────
//
// The Phase 81 / 92 tests above assert that a named hook is PRESENT.
// That leaves three regressions invisible, and this section closes each:
//
//   * a structural change to the emitted DOM (an element swapped, an
//     attribute dropped, a wrapper added) — pinned by a golden markup
//     snapshot per primitive and per layout;
//   * a `--bk-*` token that no longer corresponds to anything BrandKit
//     emits, or a hook renamed out from under the token that themes it
//     — pinned by the token/hook contract table below, which mirrors
//     the `docs/brandkit-tokens.md` variable table;
//   * opinionated styling leaking into the markup — pinned by rendering
//     the same layout under two contrasting `:root` token sets and
//     asserting the documents are byte-identical outside the `:root`
//     block itself.
//
// **The theming contract is CLASS HOOKS, not emitted `var()` refs.** The
// package emits no `var(--bk-…)` reference anywhere: a consumer attaches
// each token to the `bk-*` class hooks in its own stylesheet, and the
// only inline styles BrandKit emits are the two per-call values
// `docs/brandkit-tokens.md` names (a wordmark's emphasis colour and a
// persona's optional ring). The token contract below therefore asserts
// hook emission, and a separate case asserts the zero-`var()` property
// directly — so introducing an inline token reference is a deliberate,
// visible change rather than a silent one.

/// Snapshot rendering. Newlines are normalised so a baseline captured on
/// one platform compares byte-for-byte on another; a case whose markup
/// contains a newline is refused rather than silently flattened, because
/// the baseline file is one line per case.
let private renderSnapshot (node: XmlNode) : string = (render node).Replace("\r\n", "\n")

/// `<repo>/src/ToolUp.Platform.Tests/Support/brandkit-markup.approved.txt`
/// — resolved from the running assembly (`bin/<config>/<tfm>` → project →
/// `src` → repo root), the same shape the public-API approval baselines
/// use, so a worktree or a differently-configured build finds its own.
let private baselinePath () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    let repoRoot =
        Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    Path.Combine(repoRoot, "src", "ToolUp.Platform.Tests", "Support", "brandkit-markup.approved.txt")

/// `TOOLUP_APPROVE_BRANDKIT=1` rewrites the baseline instead of comparing
/// against it — the deliberate route for an intentional markup change.
let private approveMode () =
    not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "TOOLUP_APPROVE_BRANDKIT"))

let private loadBaseline () : Map<string, string> =
    let path = baselinePath ()

    if not (File.Exists path) then
        Map.empty
    else
        File.ReadAllLines path
        |> Array.filter (fun line -> line.Trim() <> "" && not (line.StartsWith "#"))
        |> Array.map (fun line ->
            match line.IndexOf '\t' with
            | -1 -> failwithf "malformed baseline line (expected <case-id> TAB <markup>): %s" line
            | i -> line.Substring(0, i), line.Substring(i + 1))
        |> Map.ofArray

let private writeBaseline (cases: (string * string) list) =
    let header = [
        "# ToolUp.BrandKit — golden markup snapshots (Phase 197). GENERATED FILE."
        "# One line per case: <case-id> TAB <rendered markup>."
        "# An intentional markup change is a one-line edit here; regenerate the whole"
        "# file with TOOLUP_APPROVE_BRANDKIT=1 and review the diff before committing."
    ]

    let body =
        cases |> List.sortBy fst |> List.map (fun (id, markup) -> id + "\t" + markup)

    File.WriteAllText(baselinePath (), String.concat "\n" (header @ body) + "\n")

let private firstDivergence (expected: string) (actual: string) =
    let bound = min expected.Length actual.Length

    let rec go i =
        if i >= bound then bound
        elif expected[i] <> actual[i] then i
        else go (i + 1)

    go 0

let private snapshotDiff (caseId: string) (expected: string) (actual: string) =
    let at = firstDivergence expected actual

    let window (s: string) =
        let start = max 0 (at - 50)
        let len = min 140 (s.Length - start)

        if len <= 0 then
            "<end of markup>"
        else
            s.Substring(start, len)

    sprintf
        "markup snapshot '%s' diverged at character %d.\n  approved: …%s…\n  rendered: …%s…\nIf the change is intentional, update that one line (or regenerate with TOOLUP_APPROVE_BRANDKIT=1) and review the diff."
        caseId
        at
        (window expected)
        (window actual)

// ─── Fixtures the snapshot + contract cases render ─────────────────

let private wordmarkSpec: Wordmark.WordmarkSpec = {
    Stem = "Bright"
    Emphasis = "Spark"
    EmphasisColour = "#6B5FBF"
    Tail = Some "s"
}

let private personaSpec: Persona.PersonaSpec = {
    ImageUrl = "/persona.png"
    AltText = "Portrait of the briefing persona"
    Size = 56
    Treatment = Persona.Circle
    RingColour = Some "#D9C4A4"
}

let private iconSpec: Icon.IconSpec = {
    Paths = [ "M4 12h16"; "M12 4v16" ]
    Dots = None
}

let private headerSpec: PageChrome.HeaderSpec = {
    Brand = PageChrome.Monogram("/m.svg", "Brand")
    Nav = [ { Label = "Home"; Href = "/" }; { Label = "Docs"; Href = "/docs" } ]
    Right = [ Pill.pillOn "BETA" ]
}

let private minimalArticle: ArticleSpec = {
    fullArticle with
        Eyebrow = None
        Lede = None
        Meta = []
        Hero = None
        Aside = None
        Breadcrumb = None
}

let private fullLanding: LandingSpec = {
    HeroEyebrow = Some "PLATFORM"
    HeroTitle = "Build it"
    HeroLede = Some "Faster."
    HeroActions = [ a [ _href "/start" ] [ str "Start" ] ]
    HeroVisual = Some(sentinel "visual")
    Sections = [ sentinel "s1"; sentinel "s2" ]
}

let private minimalLanding: LandingSpec = {
    fullLanding with
        HeroEyebrow = None
        HeroLede = None
        HeroActions = []
        HeroVisual = None
        Sections = []
}

let private fullDashboard: DashboardSpec = {
    Title = "Ops"
    Toolbar = [ sentinel "toolbar" ]
    Kpis = [ Card.cardTight [ str "42" ] ]
    Panels = [ Card.card [ sentinel "panel" ] ]
}

let private minimalDashboard: DashboardSpec = {
    fullDashboard with
        Toolbar = []
        Kpis = []
        Panels = []
}

let private fullDoc: DocSpec = {
    Title = "Install"
    Sidebar = Some(sentinel "sidebar")
    Toc = Some(sentinel "toc")
    Body = sentinel "docbody"
    Breadcrumb = Some(sentinel "crumbs")
    PrevNext = Some(sentinel "pager")
}

let private minimalDoc: DocSpec = {
    fullDoc with
        Sidebar = None
        Toc = None
        Breadcrumb = None
        PrevNext = None
}

let private fullGallery: GallerySpec = {
    Title = "Shots"
    Intro = Some(sentinel "intro")
    Items = [ sentinel "item1"; sentinel "item2" ]
    Pager = Some(sentinel "pager")
}

let private minimalGallery: GallerySpec = {
    fullGallery with
        Intro = None
        Items = []
        Pager = None
}

let private fullVideo: VideoSpec = {
    Title = "Demo"
    Player = sentinel "player"
    Meta = [ Pill.pill "4 min" ]
    Description = Some(sentinel "desc")
    Transcript = Some(sentinel "transcript")
    Related = Some(sentinel "related")
}

let private minimalVideo: VideoSpec = {
    fullVideo with
        Meta = []
        Description = None
        Transcript = None
        Related = None
}

let private fullKnowledge: KnowledgePortalSpec = {
    Title = "Knowledge"
    Intro = Some "Ask anything."
    Search = Some(sentinel "search")
    Answer = Some(sentinel "answer")
    Browse = Some(sentinel "browse")
    Sidebar = Some(sentinel "sidebar")
}

let private minimalKnowledge: KnowledgePortalSpec = {
    fullKnowledge with
        Intro = None
        Search = None
        Answer = None
        Browse = None
        Sidebar = None
}

// ─── The snapshot corpus ───────────────────────────────────────────

/// Every public rendering function in `ToolUp.BrandKit`, plus the seven
/// layouts in full-slot and every-optional-slot-omitted form. A new
/// primitive or layout is added here in the same commit that ships it —
/// the completeness case below fails on a case-id the baseline does not
/// carry, and vice versa.
let private snapshotCases: (string * XmlNode) list = [
    "text-display-large", BkText.displayLarge "Display large"
    "text-display-medium", BkText.displayMedium "Display medium"
    "text-display-small", BkText.displaySmall "Display small"
    "text-eyebrow", BkText.eyebrow "SECTION"
    "text-eyebrow-mute", BkText.eyebrowMute "MUTED"
    "text-mono-small", BkText.monoSmall "12:04"
    "text-mono-medium", BkText.monoMedium "12:04"
    "text-mono-large", BkText.monoLarge "12:04"
    "text-mono-body", BkText.monoBody "12:04"
    "text-hrule", BkText.hRule
    "text-hrule-soft", BkText.hRuleSoft
    "text-vdivider", BkText.vDivider

    "wordmark-with-tail", Wordmark.wordmark wordmarkSpec
    "wordmark-without-tail", Wordmark.wordmark { wordmarkSpec with Tail = None }

    "card", Card.card [ str "body" ]
    "card-tight", Card.cardTight [ str "body" ]
    "card-deep", Card.cardDeep [ str "body" ]
    "card-outlined", Card.cardOutlined [ str "body" ]

    "pill", Pill.pill "PLAIN"
    "pill-on", Pill.pillOn "ACTIVE"
    "pill-with-dot", Pill.pillWithDot "PRIORITY"
    "pill-severity-info", Pill.pillSeverity Pill.Info "NEW"
    "pill-severity-positive", Pill.pillSeverity Pill.Positive "UP"
    "pill-severity-priority", Pill.pillSeverity Pill.Priority "WATCH"
    "pill-severity-critical", Pill.pillSeverity Pill.Critical "ALARM"

    "persona-circle-ringed", Persona.personaAvatar personaSpec
    "persona-rounded-unringed",
    Persona.personaAvatar {
        personaSpec with
            Treatment = Persona.Rounded
            RingColour = None
    }
    "persona-square",
    Persona.personaAvatar {
        personaSpec with
            Treatment = Persona.Square
            RingColour = None
    }
    "persona-signed-summary", Persona.personaSignedSummary personaSpec "Briefed by " "Ada"

    "icon-default", Icon.iconSvg iconSpec
    "icon-sized-with-dots",
    Icon.iconSvgSized 32 {
        iconSpec with
            Dots = Some [ (12.0, 12.0, 1.5) ]
    }

    "page-header-monogram", PageChrome.pageHeader headerSpec
    "page-header-wordmark",
    PageChrome.pageHeader {
        headerSpec with
            Brand = PageChrome.Wordmark wordmarkSpec
    }
    "page-footer",
    PageChrome.pageFooter {
        Copyright = "© 2026 Example Co"
        Links = [ { Label = "Terms"; Href = "/terms" } ]
    }

    "layout-article-full", Layout.article chrome fullArticle
    "layout-article-minimal", Layout.article (Chrome.create "Bare") minimalArticle
    "layout-landing-full", Layout.landing chrome fullLanding
    "layout-landing-minimal", Layout.landing (Chrome.create "Bare") minimalLanding
    "layout-dashboard-full", Layout.dashboard chrome fullDashboard
    "layout-dashboard-minimal", Layout.dashboard (Chrome.create "Bare") minimalDashboard
    "layout-doc-full", Layout.doc chrome fullDoc
    "layout-doc-minimal", Layout.doc (Chrome.create "Bare") minimalDoc
    "layout-gallery-full", Layout.gallery chrome fullGallery
    "layout-gallery-minimal", Layout.gallery (Chrome.create "Bare") minimalGallery
    "layout-video-full", Layout.video chrome fullVideo
    "layout-video-minimal", Layout.video (Chrome.create "Bare") minimalVideo
    "layout-knowledge-full", Layout.knowledgePortal chrome fullKnowledge
    "layout-knowledge-minimal", Layout.knowledgePortal (Chrome.create "Bare") minimalKnowledge
]

/// Regenerate-then-load, once. Reading the baseline through this value
/// means an approval run rewrites the file before the first comparison
/// reads it, with no dependence on case ordering.
let private baseline =
    lazy
        (if approveMode () then
             writeBaseline (snapshotCases |> List.map (fun (id, node) -> id, renderSnapshot node))

         loadBaseline ())

// ─── The token / class-hook contract ───────────────────────────────

/// One row of the `docs/brandkit-tokens.md` variable table, made
/// executable: the token, the BrandKit-emitted class hook(s) a consumer
/// attaches its value to, and the render that must carry them.
type private TokenClaim = {
    Token: string
    Hooks: string list
    Render: unit -> string
}

let private tokenClaims: TokenClaim list = [
    {
        Token = Tokens.FontDisplayVar
        Hooks = [ "bk-display"; "bk-wordmark" ]
        Render =
            fun () ->
                renderSnapshot (BkText.displayLarge "D")
                + renderSnapshot (Wordmark.wordmark wordmarkSpec)
    }
    {
        // `docs/brandkit-tokens.md` records the three page-surface tokens
        // as "(consumer body)" — written at Phase 81, before Phase 92's
        // `LayoutShell` gave the body a BrandKit-emitted hook. `.bk-page`
        // is that hook, so the token is live rather than dead.
        Token = Tokens.FontUiVar
        Hooks = [ "bk-page" ]
        Render = fun () -> renderSnapshot (Layout.article chrome fullArticle)
    }
    {
        Token = Tokens.FontMonoVar
        Hooks = [ "bk-eyebrow"; "bk-mono" ]
        Render = fun () -> renderSnapshot (BkText.eyebrow "E") + renderSnapshot (BkText.monoSmall "M")
    }
    {
        Token = Tokens.InkVar
        Hooks = [ "bk-page" ]
        Render = fun () -> renderSnapshot (Layout.article chrome fullArticle)
    }
    {
        Token = Tokens.InkMuteVar
        Hooks = [ "bk-eyebrow-mute" ]
        Render = fun () -> renderSnapshot (BkText.eyebrowMute "M")
    }
    {
        Token = Tokens.PaperVar
        Hooks = [ "bk-page" ]
        Render = fun () -> renderSnapshot (Layout.article chrome fullArticle)
    }
    {
        Token = Tokens.PanelVar
        Hooks = [ "bk-card-deep" ]
        Render = fun () -> renderSnapshot (Card.cardDeep [ str "x" ])
    }
    {
        Token = Tokens.RuleVar
        Hooks = [ "bk-rule"; "bk-card-outlined" ]
        Render = fun () -> renderSnapshot BkText.hRule + renderSnapshot (Card.cardOutlined [ str "x" ])
    }
    {
        Token = Tokens.AccentVar
        Hooks = [ "bk-eyebrow"; "bk-tag-on" ]
        Render = fun () -> renderSnapshot (BkText.eyebrow "E") + renderSnapshot (Pill.pillOn "A")
    }
    {
        Token = Tokens.OnDarkTextVar
        Hooks = [ "bk-wordmark" ]
        Render = fun () -> renderSnapshot (Wordmark.wordmark wordmarkSpec)
    }
    {
        Token = Tokens.PositiveVar
        Hooks = [ "bk-tag-positive" ]
        Render = fun () -> renderSnapshot (Pill.pillSeverity Pill.Positive "UP")
    }
    {
        Token = Tokens.PriorityVar
        Hooks = [ "bk-tag-priority"; "bk-tag-critical" ]
        Render =
            fun () ->
                renderSnapshot (Pill.pillSeverity Pill.Priority "W")
                + renderSnapshot (Pill.pillSeverity Pill.Critical "C")
    }
    {
        Token = Tokens.InfoVar
        Hooks = [ "bk-tag-info" ]
        Render = fun () -> renderSnapshot (Pill.pillSeverity Pill.Info "N")
    }
    {
        Token = Tokens.RadiusMdVar
        Hooks = [ "bk-tag"; "bk-card-tight" ]
        Render = fun () -> renderSnapshot (Pill.pill "P") + renderSnapshot (Card.cardTight [ str "x" ])
    }
    {
        Token = Tokens.RadiusLgVar
        Hooks = [ "bk-card" ]
        Render = fun () -> renderSnapshot (Card.card [ str "x" ])
    }
    {
        Token = Tokens.ShadowCardVar
        Hooks = [ "bk-card" ]
        Render = fun () -> renderSnapshot (Card.card [ str "x" ])
    }
]

/// Every `--bk-*` `[<Literal>]` in `ToolUp.BrandKit.Tokens`, read by
/// reflection rather than transcribed — a token added to the module with
/// no claim in the table above fails the parity case below on the next
/// run, with no edit here.
let private declaredTokens () =
    let tokensType =
        typeof<Wordmark.WordmarkSpec>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.FullName = "ToolUp.BrandKit.Tokens")

    match tokensType with
    | None -> failwith "ToolUp.BrandKit.Tokens type not found in the BrandKit assembly"
    | Some t ->
        t.GetFields(BindingFlags.Public ||| BindingFlags.Static)
        |> Array.filter (fun f -> f.IsLiteral && f.FieldType = typeof<string>)
        |> Array.choose (fun f ->
            match f.GetRawConstantValue() with
            | :? string as v when v.StartsWith "--bk-" -> Some v
            | _ -> None)
        |> Array.distinct
        |> Array.sort
        |> List.ofArray

// ─── Theming isolation ─────────────────────────────────────────────

/// Two contrasting brand palettes over the canonical token set — a light
/// paper brand and a dark one. Every token differs, so a document that
/// varies with the palette anywhere OUTSIDE the `:root` block is caught.
let private paletteLight =
    Map.ofList [
        Tokens.FontDisplayVar, "'Newsreader', Georgia, serif"
        Tokens.FontUiVar, "'Inter', system-ui, sans-serif"
        Tokens.FontMonoVar, "'IBM Plex Mono', monospace"
        Tokens.InkVar, "#2B2638"
        Tokens.InkMuteVar, "#6C6478"
        Tokens.PaperVar, "#F3EEE4"
        Tokens.PanelVar, "#FBF8F2"
        Tokens.RuleVar, "#E4DBCB"
        Tokens.AccentVar, "#6B5FBF"
        Tokens.OnDarkTextVar, "#E7E2D8"
        Tokens.PositiveVar, "#6F8A6E"
        Tokens.PriorityVar, "#7E4550"
        Tokens.InfoVar, "#6B5FBF"
        Tokens.RadiusMdVar, "12px"
        Tokens.RadiusLgVar, "16px"
        Tokens.ShadowCardVar, "0 18px 40px -28px rgba(62, 51, 112, 0.40)"
    ]

let private paletteDark =
    Map.ofList [
        Tokens.FontDisplayVar, "'Fraunces', Times, serif"
        Tokens.FontUiVar, "'Public Sans', Arial, sans-serif"
        Tokens.FontMonoVar, "'JetBrains Mono', monospace"
        Tokens.InkVar, "#ECE7DD"
        Tokens.InkMuteVar, "#9C94A8"
        Tokens.PaperVar, "#141018"
        Tokens.PanelVar, "#1E1826"
        Tokens.RuleVar, "#332B3D"
        Tokens.AccentVar, "#C57A3C"
        Tokens.OnDarkTextVar, "#141018"
        Tokens.PositiveVar, "#8FBF8D"
        Tokens.PriorityVar, "#D98693"
        Tokens.InfoVar, "#C57A3C"
        Tokens.RadiusMdVar, "4px"
        Tokens.RadiusLgVar, "6px"
        Tokens.ShadowCardVar, "0 2px 6px 0 rgba(0, 0, 0, 0.80)"
    ]

/// The article layout under a `:root` block built from a palette. The
/// chrome deliberately uses the monogram brand lockup, so no caller
/// supplies a colour anywhere in the document body — every colour in the
/// output must therefore come from the `:root` block or be a leak.
let private themedDocument (palette: Map<string, string>) =
    let rootCss = HostThemeTokens.ofBrandKitValues palette |> HostThemeTokens.toRootCss

    let themedChrome = {
        chrome with
            HeadExtra = [ style [] [ rawText rootCss ] ]
    }

    renderSnapshot (Layout.article themedChrome fullArticle)

/// Replace the `:root` style block with a fixed marker — what remains is
/// the part of the document that must not vary with the brand.
let private withoutRootBlock (html: string) =
    Regex.Replace(html, "<style>.*?</style>", "<style>ROOT</style>", RegexOptions.Singleline)

/// Colour / font literals that would mean opinionated styling leaked out
/// of the consumer's stylesheet and into BrandKit's markup.
let private stylingLeaks (html: string) =
    [
        // The `(?<!&)` guard keeps an HTML numeric entity (`&#169;` for
        // the footer's copyright sign) from reading as a hex colour.
        "hex colour", Regex(@"(?<!&)#[0-9a-fA-F]{6}\b|(?<!&)#[0-9a-fA-F]{3}\b")
        "rgb()/rgba() colour", Regex(@"\brgba?\(")
        "hsl()/hsla() colour", Regex(@"\bhsla?\(")
        "font-family declaration", Regex(@"font-family\s*:")
        "CSS variable reference", Regex(@"var\(--")
    ]
    |> List.filter (fun (_, rx) -> rx.IsMatch html)
    |> List.map fst

// ─── Class-hook stability ──────────────────────────────────────────

let private classAttr = Regex("class=\"([^\"]*)\"")

/// Every distinct class token in the rendered markup carrying `prefix`.
let private hooksWithPrefix (prefix: string) (html: string) =
    classAttr.Matches html
    |> Seq.collect (fun m -> m.Groups[1].Value.Split ' ')
    |> Seq.filter (fun token -> token.StartsWith prefix)
    |> Seq.distinct
    |> Seq.sort
    |> List.ofSeq

/// Per layout: the region-hook prefix, the full-slot render, the exact
/// set of `bk-<name>-<region>` hooks it must emit, and the render with
/// every optional slot omitted (whose hook set is asserted to be a strict
/// subset — a `None` slot omits its wrapper entirely).
let private layoutHookContracts: (string * string * (unit -> string) * string list * (unit -> string)) list = [
    "article",
    "bk-article-",
    (fun () -> renderSnapshot (Layout.article chrome fullArticle)),
    [
        "bk-article-aside"
        "bk-article-body"
        "bk-article-columns"
        "bk-article-header"
        "bk-article-hero"
        "bk-article-lede"
        "bk-article-meta"
        "bk-article-title"
    ],
    (fun () -> renderSnapshot (Layout.article chrome minimalArticle))

    "landing",
    "bk-landing-",
    (fun () -> renderSnapshot (Layout.landing chrome fullLanding)),
    [
        "bk-landing-actions"
        "bk-landing-hero"
        "bk-landing-hero-copy"
        "bk-landing-lede"
        "bk-landing-section"
        "bk-landing-title"
        "bk-landing-visual"
    ],
    (fun () -> renderSnapshot (Layout.landing chrome minimalLanding))

    "dashboard",
    "bk-dashboard-",
    (fun () -> renderSnapshot (Layout.dashboard chrome fullDashboard)),
    [
        "bk-dashboard-header"
        "bk-dashboard-kpis"
        "bk-dashboard-panels"
        "bk-dashboard-title"
        "bk-dashboard-toolbar"
    ],
    (fun () -> renderSnapshot (Layout.dashboard chrome minimalDashboard))

    "doc",
    "bk-doc-",
    (fun () -> renderSnapshot (Layout.doc chrome fullDoc)),
    [
        "bk-doc-body"
        "bk-doc-columns"
        "bk-doc-content"
        "bk-doc-prevnext"
        "bk-doc-sidebar"
        "bk-doc-title"
        "bk-doc-toc"
    ],
    (fun () -> renderSnapshot (Layout.doc chrome minimalDoc))

    "gallery",
    "bk-gallery-",
    (fun () -> renderSnapshot (Layout.gallery chrome fullGallery)),
    [
        "bk-gallery-grid"
        "bk-gallery-header"
        "bk-gallery-intro"
        "bk-gallery-pager"
        "bk-gallery-title"
    ],
    (fun () -> renderSnapshot (Layout.gallery chrome minimalGallery))

    "video",
    "bk-video-",
    (fun () -> renderSnapshot (Layout.video chrome fullVideo)),
    [
        "bk-video-description"
        "bk-video-meta"
        "bk-video-player"
        "bk-video-related"
        "bk-video-title"
        "bk-video-transcript"
    ],
    (fun () -> renderSnapshot (Layout.video chrome minimalVideo))

    "knowledge",
    "bk-knowledge-",
    (fun () -> renderSnapshot (Layout.knowledgePortal chrome fullKnowledge)),
    [
        "bk-knowledge-answer"
        "bk-knowledge-browse"
        "bk-knowledge-columns"
        "bk-knowledge-header"
        "bk-knowledge-intro"
        "bk-knowledge-main"
        "bk-knowledge-search"
        "bk-knowledge-sidebar"
        "bk-knowledge-title"
    ],
    (fun () -> renderSnapshot (Layout.knowledgePortal chrome minimalKnowledge))
]

let tests =
    testList "ToolUp.BrandKit — layout library (Phase 92)" [

        // ─── Shell baseline (shared by all seven) ─────────────

        testCase "shell: skip link is the first body element and targets #bk-main"
        <| fun _ ->
            let html = Layout.article chrome fullArticle |> render
            Expect.stringContains html "bk-skip-link" "skip-link hook present"
            Expect.stringContains html "href=\"#bk-main\"" "skip link targets the main id"
            Expect.stringContains html "id=\"bk-main\"" "main carries the target id"
            Expect.stringContains html "tabindex=\"-1\"" "main is programmatically focusable"

            let bodyStart = html.IndexOf "<body"
            let skipLinkAt = html.IndexOf "bk-skip-link"
            let headerAt = html.IndexOf "bk-header"
            Expect.isTrue (bodyStart < skipLinkAt && skipLinkAt < headerAt) "skip link precedes the header landmark"

        testCase "shell: chrome lands — lang, title, description, stylesheet, header, footer, body class"
        <| fun _ ->
            let html = Layout.article chrome fullArticle |> render
            Expect.stringContains html "lang=\"en\"" "html lang set"
            Expect.stringContains html "<title>Test Page</title>" "document title set"
            Expect.stringContains html "A test description" "meta description set"
            Expect.stringContains html "href=\"/brand.css\"" "stylesheet linked"
            Expect.stringContains html "bk-header" "header landmark rendered"
            Expect.stringContains html "bk-footer" "footer landmark rendered"
            Expect.stringContains html "bk-page theme-test" "body class extends bk-page"

        testCase "shell: minimal chrome omits description / stylesheets / header / footer"
        <| fun _ ->
            let bare = Chrome.create "Bare"
            let html = Layout.article bare { fullArticle with Aside = None } |> render
            Expect.isFalse (html.Contains "name=\"description\"") "no description meta"
            Expect.isFalse (html.Contains "stylesheet") "no stylesheet link"
            Expect.isFalse (html.Contains "bk-header") "no header landmark"
            Expect.isFalse (html.Contains "bk-footer") "no footer landmark"

        // ─── Article ──────────────────────────────────────────

        testCase "article: full slots render with their hooks, eyebrow via the BrandKit primitive"
        <| fun _ ->
            let html = Layout.article chrome fullArticle |> render
            Expect.stringContains html "bk-layout-article" "layout class on main"
            Expect.stringContains html "bk-eyebrow" "eyebrow uses the BrandKit primitive"
            Expect.stringContains html "<h1 class=\"bk-article-title\">Headline</h1>" "h1 title"
            Expect.stringContains html "bk-article-lede" "lede hook"
            Expect.stringContains html "bk-article-meta" "meta hook"
            Expect.stringContains html "bk-tag" "meta renders the supplied pill"
            Expect.stringContains html "bk-article-hero" "hero figure hook"
            Expect.stringContains html "sentinel-body" "body slot rendered"
            Expect.stringContains html "bk-article-aside" "aside hook"
            Expect.stringContains html "sentinel-crumbs" "breadcrumb slot rendered"
            Expect.equal (countOf "<h1" html) 1 "exactly one h1"

        testCase "article: None slots omit their wrappers entirely"
        <| fun _ ->
            let minimal = {
                fullArticle with
                    Eyebrow = None
                    Lede = None
                    Meta = []
                    Hero = None
                    Aside = None
                    Breadcrumb = None
            }

            let html = Layout.article chrome minimal |> render
            Expect.isFalse (html.Contains "bk-eyebrow") "no eyebrow"
            Expect.isFalse (html.Contains "bk-article-lede") "no lede wrapper"
            Expect.isFalse (html.Contains "bk-article-meta") "no meta wrapper"
            Expect.isFalse (html.Contains "bk-article-hero") "no hero wrapper"
            Expect.isFalse (html.Contains "bk-article-aside") "no aside wrapper"

        // ─── Landing ──────────────────────────────────────────

        testCase "landing: hero copy + actions + visual + one wrapper per section"
        <| fun _ ->
            let spec: LandingSpec = {
                HeroEyebrow = Some "PLATFORM"
                HeroTitle = "Build it"
                HeroLede = Some "Faster."
                HeroActions = [ a [ _href "/start" ] [ str "Start" ] ]
                HeroVisual = Some(sentinel "visual")
                Sections = [ sentinel "s1"; sentinel "s2" ]
            }

            let html = Layout.landing chrome spec |> render
            Expect.stringContains html "bk-layout-landing" "layout class on main"
            Expect.stringContains html "bk-landing-hero" "hero hook"
            Expect.stringContains html "<h1 class=\"bk-landing-title\">Build it</h1>" "h1 title"
            Expect.stringContains html "bk-landing-actions" "actions hook"
            Expect.stringContains html "bk-landing-visual" "visual hook"
            Expect.equal (countOf "bk-landing-section" html) 2 "one wrapper per section"
            Expect.equal (countOf "<h1" html) 1 "exactly one h1"

        // ─── Dashboard ────────────────────────────────────────

        testCase "dashboard: kpi + panel grids carry bk-grid; empty regions omit their wrappers"
        <| fun _ ->
            let spec: DashboardSpec = {
                Title = "Ops"
                Toolbar = [ sentinel "toolbar" ]
                Kpis = [ Card.cardTight [ str "42" ] ]
                Panels = [ Card.card [ sentinel "panel" ] ]
            }

            let html = Layout.dashboard chrome spec |> render
            Expect.stringContains html "bk-layout-dashboard" "layout class on main"
            Expect.stringContains html "bk-dashboard-kpis bk-grid" "kpi grid hook"
            Expect.stringContains html "bk-dashboard-panels bk-grid" "panel grid hook"
            Expect.stringContains html "bk-dashboard-toolbar" "toolbar hook"
            Expect.stringContains html "bk-card" "cards render inside the grids"

            let bareHtml =
                Layout.dashboard chrome { spec with Kpis = []; Toolbar = [] } |> render

            Expect.isFalse (bareHtml.Contains "bk-dashboard-kpis") "empty kpi region omitted"
            Expect.isFalse (bareHtml.Contains "bk-dashboard-toolbar") "empty toolbar omitted"

        // ─── Doc ──────────────────────────────────────────────

        testCase "doc: sidebar / toc / prev-next render and omit cleanly"
        <| fun _ ->
            let spec: DocSpec = {
                Title = "Install"
                Sidebar = Some(sentinel "sidebar")
                Toc = Some(sentinel "toc")
                Body = sentinel "docbody"
                Breadcrumb = Some(sentinel "crumbs")
                PrevNext = Some(sentinel "pager")
            }

            let html = Layout.doc chrome spec |> render
            Expect.stringContains html "bk-layout-doc" "layout class on main"
            Expect.stringContains html "bk-doc-sidebar" "sidebar hook"
            Expect.stringContains html "bk-doc-toc" "toc hook"
            Expect.stringContains html "bk-doc-prevnext" "prev-next hook"
            Expect.stringContains html "sentinel-docbody" "body slot rendered"
            Expect.equal (countOf "<h1" html) 1 "exactly one h1"

            let bareHtml =
                Layout.doc chrome {
                    spec with
                        Sidebar = None
                        Toc = None
                        PrevNext = None
                }
                |> render

            Expect.isFalse (bareHtml.Contains "bk-doc-sidebar") "no sidebar wrapper"
            Expect.isFalse (bareHtml.Contains "bk-doc-toc") "no toc wrapper"
            Expect.isFalse (bareHtml.Contains "bk-doc-prevnext") "no prev-next wrapper"

        // ─── Gallery ──────────────────────────────────────────

        testCase "gallery: item grid carries bk-grid; pager optional"
        <| fun _ ->
            let spec: GallerySpec = {
                Title = "Shots"
                Intro = Some(sentinel "intro")
                Items = [ sentinel "item1"; sentinel "item2"; sentinel "item3" ]
                Pager = Some(sentinel "pager")
            }

            let html = Layout.gallery chrome spec |> render
            Expect.stringContains html "bk-layout-gallery" "layout class on main"
            Expect.stringContains html "bk-gallery-grid bk-grid" "grid hook"
            Expect.equal (countOf "sentinel-item" html) 3 "all items render"
            Expect.stringContains html "bk-gallery-pager" "pager hook"

            let bareHtml =
                Layout.gallery chrome { spec with Pager = None; Items = [] } |> render

            Expect.isFalse (bareHtml.Contains "bk-gallery-grid") "empty grid omitted"
            Expect.isFalse (bareHtml.Contains "bk-gallery-pager") "no pager wrapper"

        // ─── Video ────────────────────────────────────────────

        testCase "video: player figure + transcript in a details disclosure"
        <| fun _ ->
            let spec: VideoSpec = {
                Title = "Demo"
                Player = sentinel "player"
                Meta = [ Pill.pill "4 min" ]
                Description = Some(sentinel "desc")
                Transcript = Some(sentinel "transcript")
                Related = Some(sentinel "related")
            }

            let html = Layout.video chrome spec |> render
            Expect.stringContains html "bk-layout-video" "layout class on main"
            Expect.stringContains html "bk-video-player" "player figure hook"
            Expect.stringContains html "sentinel-player" "player slot rendered"
            Expect.stringContains html "<details class=\"bk-video-transcript\">" "transcript is a details disclosure"
            Expect.stringContains html "<summary>" "details carries a summary"
            Expect.stringContains html "bk-video-related" "related hook"

            let bareHtml =
                Layout.video chrome {
                    spec with
                        Transcript = None
                        Related = None
                }
                |> render

            Expect.isFalse (bareHtml.Contains "<details") "no transcript disclosure"
            Expect.isFalse (bareHtml.Contains "bk-video-related") "no related wrapper"

        // ─── Knowledge portal ─────────────────────────────────

        testCase "knowledgePortal: search / answer / browse / sidebar render and omit cleanly"
        <| fun _ ->
            let spec: KnowledgePortalSpec = {
                Title = "Knowledge"
                Intro = Some "Ask anything."
                Search = Some(sentinel "search")
                Answer = Some(sentinel "answer")
                Browse = Some(sentinel "browse")
                Sidebar = Some(sentinel "sidebar")
            }

            let html = Layout.knowledgePortal chrome spec |> render
            Expect.stringContains html "bk-layout-knowledge" "layout class on main"
            Expect.stringContains html "bk-knowledge-search" "search hook"
            Expect.stringContains html "bk-knowledge-answer" "answer hook"
            Expect.stringContains html "bk-knowledge-browse" "browse hook"
            Expect.stringContains html "bk-knowledge-sidebar" "sidebar hook"
            Expect.stringContains html "bk-knowledge-intro" "intro hook"
            Expect.equal (countOf "<h1" html) 1 "exactly one h1"

            let bareHtml =
                Layout.knowledgePortal chrome {
                    spec with
                        Search = None
                        Answer = None
                        Browse = None
                        Sidebar = None
                        Intro = None
                }
                |> render

            Expect.isFalse (bareHtml.Contains "bk-knowledge-search") "no search wrapper"
            Expect.isFalse (bareHtml.Contains "bk-knowledge-answer") "no answer wrapper"
            Expect.isFalse (bareHtml.Contains "bk-knowledge-browse") "no browse wrapper"
            Expect.isFalse (bareHtml.Contains "bk-knowledge-sidebar") "no sidebar wrapper"

        // ─── Phase 197 — golden markup snapshots ──────────────

        testList
            "Phase 197 — golden markup snapshots"
            (List.concat [
                [
                    testCase "baseline covers exactly the declared snapshot cases"
                    <| fun _ ->
                        let approved = baseline.Value |> Map.toList |> List.map fst |> Set.ofList
                        let declared = snapshotCases |> List.map fst |> Set.ofList

                        Expect.isEmpty
                            (Set.difference declared approved |> Set.toList)
                            "snapshot cases with no approved baseline line — regenerate with TOOLUP_APPROVE_BRANDKIT=1"

                        Expect.isEmpty
                            (Set.difference approved declared |> Set.toList)
                            "approved baseline lines for cases that no longer exist — remove them"

                        Expect.isTrue
                            (List.length snapshotCases >= 40)
                            "the snapshot corpus must not shrink — a case list that stopped emitting would pass every comparison vacuously"

                        Expect.equal
                            (snapshotCases |> List.map fst |> List.distinct |> List.length)
                            (List.length snapshotCases)
                            "snapshot case ids are unique"
                ]

                snapshotCases
                |> List.map (fun (caseId, node) ->
                    testCase (sprintf "snapshot: %s" caseId)
                    <| fun _ ->
                        let rendered = renderSnapshot node

                        Expect.isFalse
                            (rendered.Contains "\n")
                            (sprintf "case '%s' rendered a newline; the baseline is one line per case" caseId)

                        match baseline.Value |> Map.tryFind caseId with
                        | None ->
                            failtestf
                                "no approved baseline for '%s'. Regenerate with TOOLUP_APPROVE_BRANDKIT=1. Rendered:\n%s"
                                caseId
                                rendered
                        | Some approved ->
                            if approved <> rendered then
                                failtest (snapshotDiff caseId approved rendered))
            ])

        // ─── Phase 197 — token / class-hook contract ──────────

        testList
            "Phase 197 — token emission contract"
            (List.concat [
                [
                    testCase "every --bk-* literal is claimed by the contract table, and every claim is a real literal"
                    <| fun _ ->
                        let declared = declaredTokens () |> Set.ofList
                        let claimed = tokenClaims |> List.map (fun c -> c.Token) |> Set.ofList

                        Expect.isEmpty
                            (Set.difference declared claimed |> Set.toList)
                            "tokens declared in ToolUp.BrandKit.Tokens with no claim in the contract table — documented-but-untested"

                        Expect.isEmpty
                            (Set.difference claimed declared |> Set.toList)
                            "contract-table claims for tokens ToolUp.BrandKit.Tokens no longer declares — dead claim"

                        Expect.isTrue
                            (Set.count declared >= 16)
                            "the reflected literal sweep must not come back empty — that would satisfy both set checks vacuously"

                    testCase "the contract table matches HostThemeTokens.brandKitVars"
                    <| fun _ ->
                        let claimed = tokenClaims |> List.map (fun c -> c.Token) |> Set.ofList
                        let projected = HostThemeTokens.brandKitVars |> Set.ofList

                        Expect.isEmpty
                            (Set.difference projected claimed |> Set.toList)
                            "tokens the hosted-theme projection carries but the contract table does not claim"

                        Expect.isEmpty
                            (Set.difference claimed projected |> Set.toList)
                            "tokens the contract table claims but the hosted-theme projection omits"

                    testCase "no primitive or layout emits a var(--bk-…) reference"
                    <| fun _ ->
                        // BrandKit's theming contract is class hooks, not
                        // emitted variable references (see the section header
                        // above and `docs/brandkit-tokens.md`). Introducing one
                        // changes that contract, so it must fail here first.
                        let offenders =
                            snapshotCases
                            |> List.filter (fun (_, node) -> (renderSnapshot node).Contains "var(--bk-")
                            |> List.map fst

                        Expect.isEmpty
                            offenders
                            "BrandKit emits class hooks only; a var(--bk-…) reference in the markup changes the theming contract"
                ]

                tokenClaims
                |> List.map (fun claim ->
                    testCase (sprintf "token %s is themed through %s" claim.Token (String.concat " + " claim.Hooks))
                    <| fun _ ->
                        let markup = claim.Render()

                        for hook in claim.Hooks do
                            Expect.isTrue
                                (classAttr.Matches markup
                                 |> Seq.collect (fun m -> m.Groups[1].Value.Split ' ')
                                 |> Seq.contains hook)
                                (sprintf
                                    "token %s is documented against the .%s hook, which the primitive no longer emits (documented-but-dead)"
                                    claim.Token
                                    hook))
            ])

        // ─── Phase 197 — theming isolation ────────────────────

        testList "Phase 197 — theming isolation" [
            testCase "two contrasting palettes change the :root block and nothing else"
            <| fun _ ->
                let light = themedDocument paletteLight
                let dark = themedDocument paletteDark

                // Falsifier: the probe must actually have varied
                // something, or the equality below is vacuous.
                Expect.notEqual light dark "the two palettes must produce different documents"

                Expect.equal
                    (withoutRootBlock dark)
                    (withoutRootBlock light)
                    "swapping every brand token changed the markup outside the :root block — BrandKit is meant to be markup-invariant under theming"

            testCase "the :root block carries the palette values verbatim"
            <| fun _ ->
                let light = themedDocument paletteLight

                // `Map.toList` rather than the `KeyValue` active pattern —
                // Giraffe.ViewEngine's `XmlAttribute.KeyValue` case shadows
                // it here.
                for token, value in Map.toList paletteLight do
                    Expect.stringContains light (token + ": " + value + ";") (sprintf "%s declared in :root" token)

            testCase "no opinionated styling leaks into the rendered markup"
            <| fun _ ->
                let leaks = themedDocument paletteLight |> withoutRootBlock |> stylingLeaks

                Expect.isEmpty
                    leaks
                    "BrandKit ships zero opinionated styling — a colour, font or variable reference in the markup is a leak from the consumer's stylesheet into the package"

            testCase "the leak detector fires on styling it is meant to catch"
            <| fun _ ->
                // The control for the case above. A detector that only ever
                // agrees with itself is the shape this pairing exists to
                // end: run it over the document WITH the `:root` block, and
                // it must find the colours and fonts the palette declares.
                let found = themedDocument paletteLight |> stylingLeaks

                Expect.containsAll
                    found
                    [ "hex colour"; "rgb()/rgba() colour" ]
                    "the un-stripped document declares hex and rgba() colours in :root, so the detector must report them"

            testCase "the only inline colours are the caller-supplied wordmark emphasis and persona ring"
            <| fun _ ->
                let hexes (html: string) =
                    Regex.Matches(html, @"(?<!&)#[0-9a-fA-F]{6}\b|(?<!&)#[0-9a-fA-F]{3}\b")
                    |> Seq.map (fun m -> m.Value)
                    |> Seq.distinct
                    |> Seq.sort
                    |> List.ofSeq

                let wordmarkHexes = renderSnapshot (Wordmark.wordmark wordmarkSpec) |> hexes

                Expect.equal
                    wordmarkHexes
                    [ wordmarkSpec.EmphasisColour ]
                    "wordmark emits exactly the caller's emphasis colour"

                let ringedHexes = renderSnapshot (Persona.personaAvatar personaSpec) |> hexes
                Expect.equal ringedHexes [ "#D9C4A4" ] "a ringed persona emits exactly the caller's ring colour"

                let unringedHexes =
                    renderSnapshot (Persona.personaAvatar { personaSpec with RingColour = None })
                    |> hexes

                Expect.isEmpty unringedHexes "a persona with no ring colour emits no colour at all"
        ]

        // ─── Phase 197 — per-layout class-hook stability ──────

        testList
            "Phase 197 — layout class-hook stability"
            (layoutHookContracts
             |> List.collect (fun (name, prefix, renderFull, expectedHooks, renderMinimal) -> [
                 testCase (sprintf "%s: emits exactly its documented bk-%s-<region> hooks" name name)
                 <| (fun _ ->
                     let actual = renderFull () |> hooksWithPrefix prefix

                     Expect.equal
                         actual
                         (List.sort expectedHooks)
                         (sprintf
                             "the %s layout's region hooks are the contract its reference stylesheet sizes; a rename or an added region is a consumer-visible change"
                             name))

                 testCase (sprintf "%s: omitting every optional slot drops hooks and adds none" name)
                 <| (fun _ ->
                     let full = renderFull () |> hooksWithPrefix prefix |> Set.ofList
                     let minimal = renderMinimal () |> hooksWithPrefix prefix |> Set.ofList

                     Expect.isEmpty
                         (Set.difference minimal full |> Set.toList)
                         (sprintf "the minimal %s render emitted a hook the full render does not" name)

                     Expect.isTrue
                         (Set.count minimal < Set.count full)
                         (sprintf "omitting every optional slot must drop at least one %s wrapper" name))

                 testCase (sprintf "%s: the shell landmarks survive slot omission" name)
                 <| (fun _ ->
                     let minimal = renderMinimal ()
                     Expect.stringContains minimal "bk-skip-link" "skip link present"
                     Expect.stringContains minimal "id=\"bk-main\"" "main landmark present"
                     Expect.stringContains minimal ("bk-layout-" + name) "layout class present"
                     Expect.equal (countOf "<h1" minimal) 1 "exactly one h1")
             ]))
    ]