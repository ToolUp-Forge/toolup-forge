// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1130 — the conversation panel draws a block part.
module ToolUp.AI.Client.Tests.ConversationBlockTests

open System
open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.Facts
open ToolUp.Facts.Presentation
open ToolUp.AI.Client
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.AI.Client.Tests.RenderScope

// ─── Baseline files ──────────────────────────────────────────────────

[<Import("readFileSync", from = "node:fs")>]
let private readFileSync (path: obj) (encoding: string) : string = jsNative

[<Import("writeFileSync", from = "node:fs")>]
let private writeFileSync (path: obj) (data: string) (encoding: string) : unit = jsNative

[<Import("mkdirSync", from = "node:fs")>]
let private mkdirSync (path: obj) (options: obj) : unit = jsNative

[<Import("existsSync", from = "node:fs")>]
let private existsSync (path: obj) : bool = jsNative

[<Emit("new URL($0, import.meta.url)")>]
let private beside (relative: string) : obj = jsNative

[<Emit("(globalThis.process.env[$0] ?? \"\")")>]
let private envVar (name: string) : string = jsNative

[<Import("renderToStaticMarkup", from = "react-dom/server")>]
let private renderToStaticMarkup (element: ReactElement) : string = jsNative

[<Literal>]
let private BaselineDir = "../conversation-baselines/"

let private baselinePath (file: string) = beside (BaselineDir + file)

/// `TOOLUP_CONVERSATION_REFRESH=1` records the baseline instead of
/// comparing with it. A baseline recorded after a change to the panel
/// proves nothing about that change: record it from the tree before it.
let private refreshing () =
    envVar "TOOLUP_CONVERSATION_REFRESH" = "1"

let private matchBaseline (file: string) (actual: string) =
    if refreshing () then
        mkdirSync (beside BaselineDir) (createObj [ "recursive" ==> true ])
        writeFileSync (baselinePath file) actual "utf8"
    elif not (existsSync (baselinePath file)) then
        failwithf
            "conversation baseline %s has never been recorded. Record it from the tree BEFORE the change with $env:TOOLUP_CONVERSATION_REFRESH = \"1\"."
            file
    else
        let expected = (readFileSync (baselinePath file) "utf8").Replace("\r", "")
        Expect.equal actual expected ("conversation baseline " + file)

// ─── A conversation with no block ────────────────────────────────────

let private conversationId = Guid "11300000-0000-0000-0000-000000000001"

let private message (n: int) (participant: ParticipantType) (content: string) : ConversationMessage = {
    Id = Guid(sprintf "11300000-0000-0000-0000-%012d" (100 + n))
    ConversationId = conversationId
    Participant = participant
    Content = content
    Timestamp = DateTime(2026, 10, 28, 9, n, 0, DateTimeKind.Utc)
    ToolCalls = []
    RetrievedSources = []
    Parts = []
    CreatedBy = ""
    BeaconId = ""
    Verification = None
}

/// Prose, a markdown table, the `[unverified]` badge, a tool call and a
/// multipart turn whose parts hold text only: every path a message with
/// no block takes through the panel.
let private noBlockConversation: ConversationMessage list = [
    message 1 User "How did the north sites trade last week?"
    {
        message
            2
            AIAssistant
            "North revenue rose **9%** [unverified] on the week.\n\n| Site | Revenue |\n|---|---|\n| Leeds | 120 |" with
            ToolCalls = [
                {
                    ToolCallId = Guid "11300000-0000-0000-0000-000000000201"
                    ToolName = "_platform.facts.query"
                    Arguments = "{\"table\":\"trade.site\"}"
                    Result = Some "{\"rows\":1}"
                    Status = Completed
                }
            ]
    }
    {
        message 3 User "And the south?" with
            Parts = [ TextPart "And the south?" ]
    }
    message 4 System "Conversation restored."
]

/// The panel, open, over `messages`, as static markup in a fresh document.
let internal panelMarkup (messages: ConversationMessage list) : string =
    installDom "conversation-1130" |> ignore

    renderToStaticMarkup (ConversationPanel.View true "Assistant" messages "" ignore ignore ignore None)

let private noBlockTests =
    testList "a conversation with no block" [

        testCase "renders byte-identical markup to the panel before Phase 1130"
        <| fun () -> matchBaseline "no-block.html" (panelMarkup noBlockConversation)
    ]

// ─── Fixture facts and a fake resolver ───────────────────────────────

let private day (d: int) =
    DateTime(2026, 10, d, 0, 0, 0, DateTimeKind.Utc)

let private point (subject: string) (d: int) (value: decimal) (factId: string) : FactSeriesPoint = {
    Subject = "site/" + subject
    SubjectPath = [ subject ]
    PeriodFrom = day d
    PeriodTo = day (d + 1)
    PeriodLabel = None
    Value = Some value
    Rendering = Some(sprintf "£%M" value)
    FactId = factId
    AsOf = day 20
    Freshness = "fresh"
    Withheld = false
}

let private withheldPoint (subject: string) (d: int) : FactSeriesPoint = {
    point subject d 0m (sprintf "fact-%s-%d" subject d) with
        Value = None
        Rendering = None
        Withheld = true
}

let private series (metric: string) (points: FactSeriesPoint list) : FactSeries =
    let withheld = points |> List.filter _.Withheld |> List.length

    {
        TableId = "trade.site"
        Metric = metric
        SubjectPrefix = []
        SubjectLevel = "site"
        PeriodFrom = day 1
        PeriodTo = day 4
        DisplayFormat = ""
        Points = points
        MaxPoints = 2000
        WithheldCount = withheld
        Withheld =
            if withheld = 0 then
                []
            else
                [
                    {
                        PolicyRef = "small-cells"
                        Count = withheld
                    }
                ]
    }

let private id (subject: string) (d: int) = sprintf "fact-%s-%d" subject d

/// Revenue for two sites over three days; south's 2 October point is
/// withheld by the gate.
let private revenue =
    series "revenue" [
        point "north" 1 100m (id "north" 1)
        point "north" 2 110m (id "north" 2)
        point "north" 3 120m (id "north" 3)
        point "south" 1 300m (id "south" 1)
        withheldPoint "south" 2
        point "south" 3 330m (id "south" 3)
    ]

/// The same read after north's 3 October figure was restated.
let private revenueRestated =
    series "revenue" [
        point "north" 1 100m (id "north" 1)
        point "north" 2 110m (id "north" 2)
        point "north" 3 125m "fact-north-3-restated"
        point "south" 1 300m (id "south" 1)
        withheldPoint "south" 2
        point "south" 3 330m (id "south" 3)
    ]

let private orders =
    series "orders" [
        point "north" 1 10m (id "north-orders" 1)
        point "north" 2 11m (id "north-orders" 2)
        point "north" 3 12m (id "north-orders" 3)
        point "south" 1 30m (id "south-orders" 1)
        point "south" 2 31m (id "south-orders" 2)
        point "south" 3 33m (id "south-orders" 3)
    ]

let private request (kind: PresentationKind) (metric: string) : PresentationRequest = {
    Kind = kind
    TableId = "trade.site"
    Metrics = [ metric ]
    SubjectPrefix = []
    SubjectLevel = None
    Period = PresentationPeriod.Ending(day 3, 3)
    CompareTo = None
    Title = Some "Revenue by site"
    Annotations = []
}

/// The provenance a block part was written with: what the server's builder
/// produced for `request` from `answer` when the model asked.
let private storedProvenance (r: PresentationRequest) (answer: FactSeries) : BlockProvenance =
    match Presentation.build<unit> r [ SeriesAnswer.Answered answer ] with
    | Ok block -> block.Provenance
    | Error defect -> failwithf "fixture: the builder refused: %s" (PresentationDefect.describe defect)

/// A resolver answering each metric from `served`, refusing any other.
let private fakeResolver (served: FactSeries list) : BlockRender.BlockSeriesResolver =
    fun query -> async {
        match served |> List.tryFind (fun s -> s.Metric = query.Metric) with
        | Some s -> return Ok s
        | None -> return Error(sprintf "metric '%s' is not disclosed to you" query.Metric)
    }

/// Resolve synchronously: the fake resolver answers without a hop.
let private resolveNow
    (read: BlockRender.BlockSeriesResolver)
    (provenance: BlockProvenance)
    : BlockRender.BlockOutcome =
    let mutable result = None

    Async.StartImmediate(
        async {
            let! r = BlockRender.resolve read provenance
            result <- Some r
        }
    )

    match result with
    | Some r -> r
    | None -> failwith "the fake resolver did not answer synchronously"

let private blockMarkup (provenance: BlockProvenance) (outcome: BlockRender.BlockOutcome) : string =
    installDom "block-1130" |> ignore
    renderToStaticMarkup (BlockRender.view provenance outcome)

let private contains (fragment: string) (markup: string) = markup.Contains fragment

let private trendRequest = request PresentationKind.Trend "revenue"
let private trendStored = storedProvenance trendRequest revenue
let private tableRequest = request PresentationKind.Table "revenue"
let private tableStored = storedProvenance tableRequest revenue

// ─── The render ──────────────────────────────────────────────────────

let private renderTests =
    testList "a block renders through a resolver" [

        testCase "a trend block rebuilds its chart from the re-read and draws it through the renderer"
        <| fun () ->
            match resolveNow (fakeResolver [ revenue ]) trendStored with
            | BlockRender.BlockOutcome.Drawn(block, drift) ->
                Expect.isNone drift "the fact ids the read returns are the ones the block was written with"
                let markup = blockMarkup trendStored (BlockRender.BlockOutcome.Drawn(block, drift))
                Expect.isTrue (contains "<svg" markup) "the chart is drawn, as the renderer's lowered SVG"
                Expect.isTrue (contains "data-block-node" markup) "inside the block's node slot"
                Expect.isFalse (contains "data-block-placeholder" markup) "with no placeholder"
            | other -> failwithf "expected a drawn block, got %A" other

        testCase "nothing is drawn from the stored node: the drawn figures are the re-read's"
        <| fun () ->
            // The stored provenance says 120 for north on the 3rd; the read
            // now answers 125. The table shows what the read answered.
            match resolveNow (fakeResolver [ revenueRestated ]) tableStored with
            | BlockRender.BlockOutcome.Drawn _ as outcome ->
                let markup = blockMarkup tableStored outcome
                Expect.isTrue (contains "£125" markup) "the restated figure is drawn"
                Expect.isFalse (contains "£120" markup) "the figure the block was written with is not"
            | other -> failwithf "expected a drawn block, got %A" other

        testCase "the withheld point is noted by policy and never drawn"
        <| fun () ->
            match resolveNow (fakeResolver [ revenue ]) tableStored with
            | BlockRender.BlockOutcome.Drawn _ as outcome ->
                let markup = blockMarkup tableStored outcome

                Expect.isTrue
                    (contains "1 point withheld under the small-cells disclosure policy is not drawn." markup)
                    "the withheld note names the policy and the count"
            | other -> failwithf "expected a drawn block, got %A" other
    ]

// ─── Drift ───────────────────────────────────────────────────────────

let private driftTests =
    testList "drift between the stored and the re-read fact ids" [

        testCase "a changed fact id shows the drift note naming the supersession"
        <| fun () ->
            match resolveNow (fakeResolver [ revenueRestated ]) tableStored with
            | BlockRender.BlockOutcome.Drawn(_, Some drift) as outcome ->
                Expect.equal drift.Superseded [ id "north" 3 ] "the fact the block was written with"
                Expect.equal drift.Introduced [ "fact-north-3-restated" ] "the fact drawn instead"
                let markup = blockMarkup tableStored outcome
                Expect.isTrue (contains "data-block-drift" markup) "the drift note is shown"
                Expect.isTrue (contains (id "north" 3) markup) "naming the superseded fact"
                Expect.isTrue (contains "fact-north-3-restated" markup) "and the newer one"
            | other -> failwithf "expected a drawn block with drift, got %A" other

        testCase "matching fact ids show no drift note"
        <| fun () ->
            match resolveNow (fakeResolver [ revenue ]) tableStored with
            | BlockRender.BlockOutcome.Drawn(_, None) as outcome ->
                Expect.isFalse (contains "data-block-drift" (blockMarkup tableStored outcome)) "no note"
            | other -> failwithf "expected a drawn block without drift, got %A" other

        testCase "drift compares sets, in each list's order"
        <| fun () ->
            Expect.isNone (BlockRender.drift [ "a"; "b" ] [ "b"; "a" ]) "the same facts in another order"

            Expect.equal
                (BlockRender.drift [ "a"; "b"; "c" ] [ "a"; "d" ])
                (Some {
                    Superseded = [ "b"; "c" ]
                    Introduced = [ "d" ]
                })
                "what left and what arrived"
    ]

// ─── Refusals ────────────────────────────────────────────────────────

let private refusalTests =
    testList "a refused block renders a placeholder naming the refusal" [

        testCase "a request every read refuses"
        <| fun () ->
            match resolveNow (fakeResolver []) trendStored with
            | BlockRender.BlockOutcome.Refused reason as outcome ->
                Expect.isTrue (contains "metric 'revenue' is not disclosed to you" reason) "the read's own reason"
                let markup = blockMarkup trendStored outcome
                Expect.isTrue (contains "data-block-placeholder" markup) "a placeholder"
                Expect.isTrue (contains "is not disclosed to you" markup) "naming the refusal"
                Expect.isFalse (contains "<svg" markup) "and nothing drawn"
            | other -> failwithf "expected a refusal, got %A" other

        testCase "a request that is not a presentation request"
        <| fun () ->
            let malformed = {
                trendStored with
                    Request = "{\"kind\":\"pie-in-the-sky\"}"
            }

            match resolveNow (fakeResolver [ revenue ]) malformed with
            | BlockRender.BlockOutcome.Refused reason ->
                Expect.isTrue (reason.StartsWith "The block's request could not be read") "named as a request defect"
            | other -> failwithf "expected a refusal, got %A" other

        testCase "a resolver that throws is a refusal, not a broken panel"
        <| fun () ->
            let throwing: BlockRender.BlockSeriesResolver =
                fun _ -> async { return failwith "proxy offline" }

            match resolveNow throwing trendStored with
            | BlockRender.BlockOutcome.Refused reason ->
                Expect.isTrue (contains "proxy offline" reason) "the failure, named"
            | other -> failwithf "expected a refusal, got %A" other

        testCase "no resolver registered renders the no-reader placeholder"
        <| fun () ->
            let markup = blockMarkup trendStored BlockRender.BlockOutcome.NoReader

            Expect.isTrue
                (contains "no fact reader is composed in this deployment" markup)
                "the placeholder says no fact reader is composed"
    ]

// ─── Mounted: the component, the registration and the furniture ──────

[<Import("createRoot", from = "react-dom/client")>]
let private createRoot (container: obj) : obj = jsNative

[<Emit("$0.querySelector($1)")>]
let private query (host: obj) (selector: string) : obj = jsNative

[<Emit("$0.textContent")>]
let private textOf (el: obj) : string = jsNative

type private MountRun = {
    Text: string
    Svg: bool
    Navigated: string list
    Published: (string * string) list
}

let private browseEntry: RegisteredModules.ModuleEntry = {
    ModuleId = FactBrowseLinks.ModuleId
    ModuleName = "Facts"
    PageRoutes = []
}

/// Mount the element `element` builds (after the module list is published,
/// since the furniture reads it as the element is built) with the fact
/// browse module composed or not, click the
/// element `clickSelector` names (if any) once it has rendered, and report.
let private mount
    (composed: bool)
    (element: unit -> ReactElement)
    (clickSelector: string option)
    (report: MountRun -> unit)
    =
    let document = installDom "mounted-1130"
    let host = document?getElementById "mounted-1130"
    let before = RegisteredModules.snapshot ()
    RegisteredModules.publish (if composed then [ browseEntry ] else [])
    let navigated = ResizeArray<string>()
    let published = ResizeArray<string * string>()
    let stopNavigation = NavigationRequest.subscribe navigated.Add

    let stopEvents =
        ModuleEvents.subscribe (fun topic payload -> published.Add(topic, payload))

    let root = createRoot host
    root?render (element ()) |> ignore

    JS.setTimeout
        (fun () ->
            match clickSelector with
            | Some selector ->
                let target = query host selector

                if not (isNull target) then
                    target?click () |> ignore
            | None -> ()

            let run = {
                Text = textOf host
                Svg = not (isNull (query host "svg"))
                Navigated = List.ofSeq navigated
                Published = List.ofSeq published
            }

            stopNavigation ()
            stopEvents ()
            RegisteredModules.publish before
            root?unmount () |> ignore
            report run)
        80
    |> ignore

let private drawnTrend () =
    resolveNow (fakeResolver [ revenue ]) trendStored

let private mountedTests =
    testList "mounted" [

        testCaseDeferred "the registered resolver draws the block the component mounts" 300
        <| fun () ->
            let mutable result = None
            AIClientConfig.registerBlockSeriesResolver (fakeResolver [ revenue ])
            mount false (fun () -> BlockRender.Block trendStored) None (fun r -> result <- Some r)

            fun () ->
                BlockRender.setResolver None
                let r = result.Value
                Expect.isTrue r.Svg "the re-read chart is drawn"
                Expect.isFalse (r.Text.Contains "Reading the facts") "the read has answered"

        testCaseDeferred "with no resolver registered the component renders the no-reader placeholder" 300
        <| fun () ->
            let mutable result = None
            BlockRender.setResolver None
            mount false (fun () -> BlockRender.Block trendStored) None (fun r -> result <- Some r)

            fun () ->
                let r = result.Value
                Expect.isTrue (r.Text.Contains "no fact reader is composed") "the placeholder"
                Expect.isFalse r.Svg "nothing drawn"

        testCaseDeferred "a citation opens its fact through the fact browse opener" 300
        <| fun () ->
            let mutable result = None

            mount
                true
                (fun () -> BlockRender.view trendStored (drawnTrend ()))
                (Some(sprintf "button[data-fact=\"%s\"]" (id "north" 1)))
                (fun r -> result <- Some r)

            fun () ->
                let r = result.Value
                Expect.equal r.Published [ FactBrowseLinks.OpenFactTopic, id "north" 1 ] "the fact, on the event bus"

                Expect.equal
                    r.Navigated
                    [ FactBrowseLinks.sidebarId FactBrowseLinks.RowsRoute ]
                    "and the shell opens the rows page"

        testCaseDeferred "the table link opens the block's fact table" 300
        <| fun () ->
            let mutable result = None

            mount
                true
                (fun () -> BlockRender.view trendStored (drawnTrend ()))
                (Some "button[data-block-table]")
                (fun r -> result <- Some r)

            fun () ->
                let r = result.Value
                Expect.equal r.Published [ FactBrowseLinks.OpenTableTopic, "trade.site" ] "the table, on the event bus"

                Expect.equal
                    r.Navigated
                    [ FactBrowseLinks.sidebarId FactBrowseLinks.TableRoute ]
                    "and the shell opens the table page"

        testCaseDeferred "without the fact browse module the citations are markers, not links" 300
        <| fun () ->
            let mutable result = None

            mount
                false
                (fun () -> BlockRender.view trendStored (drawnTrend ()))
                (Some(sprintf "[data-fact=\"%s\"]" (id "north" 1)))
                (fun r -> result <- Some r)

            fun () ->
                let r = result.Value
                Expect.isTrue (r.Text.Contains "Facts: [1]") "the strip still cites"
                Expect.isFalse (r.Text.Contains "Open the table") "no table link"
                Expect.equal r.Published [] "and a click on a marker publishes nothing"
    ]

// ─── In the panel ────────────────────────────────────────────────────

let private panelTests =
    testList "in the panel" [

        testCase "a message's block renders in its own slot beside its text, outside the bubble"
        <| fun () ->
            let withBlock = {
                message 5 AIAssistant "Revenue by site [block]" with
                    Parts = [
                        TextPart "Here is revenue by site."
                        BlockPart("{\"not\":\"drawn\"}", trendStored)
                        TextPart "[conjecture] The rise may follow the new opening hours."
                    ]
            }

            let markup = panelMarkup [ withBlock ]
            Expect.isTrue (contains "Here is revenue by site." markup) "the text part"
            Expect.isTrue (contains "data-block-part=\"trend\"" markup) "the block slot"
            Expect.isFalse (contains "not&quot;:&quot;drawn" markup) "the stored node never reaches the page"
            Expect.isTrue (contains ">conjecture</span>" markup) "the marked text part wears the conjecture badge"
            Expect.isFalse (contains "[conjecture]" markup) "in place of the marker"

        testCase "the markdown export of a block message is the Phase 1128 export, unchanged"
        <| fun () ->
            let withBlock = {
                message 6 AIAssistant "x" with
                    Parts = [ BlockPart("{}", tableStored) ]
            }

            let exported = ConversationPanel.exportAsMarkdown "c" [ withBlock ] false

            Expect.isTrue
                (contains (AIContentPart.exportPlaceholder 1 (BlockPart("{}", tableStored))) exported)
                "1128's"
    ]

// ─── The theme ───────────────────────────────────────────────────────

/// The page's non-default token set: a dark teal surface, warm text, a
/// sage border — and the text colour the page sets on its body.
let private pageTokens = [
    "--fuaran-tone-default-bg", "#0b1d2a"
    "--fuaran-tone-default-fg", "#f2e8d5"
    "--fuaran-tone-default-border", "#3c6e71"
]

[<Literal>]
let private PageTextColour = "rgb(242, 232, 213)"

[<Emit("$1.getComputedStyle($0).getPropertyValue($2).trim()")>]
let private computed (el: obj) (window: obj) (name: string) : string = jsNative

[<Emit("Array.from($0.querySelectorAll('*'))")>]
let private descendants (el: obj) : obj array = jsNative

[<Emit("$0.parentElement")>]
let private parentOf (el: obj) : obj = jsNative

/// A custom property's value as the cascade gives it to `el`: declared on
/// `el` or inherited from the nearest ancestor that declares it. jsdom's
/// `getComputedStyle` reports a custom property only where it is declared
/// (it does not implement their inheritance), so the walk up the ancestors
/// is the inheritance the browser performs.
let rec private effective (window: obj) (name: string) (el: obj) : string =
    if isNull el then
        ""
    else
        match computed el window name with
        | "" -> effective window name (parentOf el)
        | value -> value

/// Every element under `root` (itself included) whose surface, text or
/// border token, or whose computed text colour, differs from the page's.
let private offTheme (window: obj) (root: obj) : string list =
    Array.append [| root |] (descendants root)
    |> Array.toList
    |> List.collect (fun el -> [
        for name, value in pageTokens do
            let got = effective window name el

            if got <> value then
                sprintf "%s: %s = %s" (string el?tagName) name got

        let colour = computed el window "color"

        // An SVG element's own `color` is the renderer's chrome ink
        // (`currentColor`), inherited too; an empty value is jsdom's
        // "not computed", never a colour of the block's own.
        if colour <> "" && colour <> PageTextColour then
            sprintf "%s: color = %s" (string el?tagName) colour
    ])

/// A page carrying the non-default token set, with `markup` mounted in it.
let private themedPage (markup: string) : obj * obj =
    let document = installDom "themed-1130"
    let style = document?createElement "style"

    style?textContent <-
        sprintf
            ":root { %s } body { color: %s; }"
            (pageTokens
             |> List.map (fun (n, v) -> sprintf "%s: %s;" n v)
             |> String.concat " ")
            PageTextColour

    document?head?appendChild style |> ignore
    let host = document?getElementById "themed-1130"
    host?innerHTML <- markup
    document?defaultView, host

[<Emit("Array.from(new Set(($0.match(/#[0-9a-fA-F]{6}\\b/g) || []).map(function (h) { return h.toLowerCase(); })))")>]
let private hexColours (markup: string) : string array = jsNative

/// A house palette that is not the shipped default.
let private housePalette = [| "#1b4d3e"; "#c8553d"; "#2e86ab"; "#f6ae2d" |]

let private seriesColours (markup: string) =
    hexColours markup
    |> Array.filter (fun h -> Array.contains h housePalette)
    |> Array.sort

let private themeTests =
    testList "the block takes the page's theme" [

        testCase "the block's surface, text and border tokens are the page's"
        <| fun () ->
            let window, host = themedPage (blockMarkup trendStored (drawnTrend ()))
            let block = query host "[data-block-part]"
            Expect.isFalse (isNull block) "the block is on the page"
            Expect.equal (offTheme window block) [] "no element of the block departs from the page's tokens"
            Expect.isTrue (isNull (block?getAttribute "class")) "the container carries no class"
            Expect.isTrue (isNull (block?getAttribute "style")) "and no style"
            Expect.equal (computed block window "background-color") "rgba(0, 0, 0, 0)" "so the page's surface shows"

        testCase "the theme probe is not vacuous: an element with its own tokens is caught"
        <| fun () ->
            let window, host =
                themedPage
                    "<div data-control><span style=\"--fuaran-tone-default-bg: #ffffff; color: rgb(0, 0, 0)\">x</span></div>"

            Expect.equal
                (offTheme window (query host "[data-control]"))
                [ "SPAN: --fuaran-tone-default-bg = #ffffff"; "SPAN: color = rgb(0, 0, 0)" ]
                "a declared token and a colour of its own are both reported"

        testCase "the block's chart and a hosted view's chart draw one series palette, the host's installed style"
        <| fun () ->
            Fuaran.UI.Charts.installChartStyle {
                Fuaran.UI.Charts.ChartStyle.defaults with
                    Palette = housePalette
            }

            try
                let blockChart = blockMarkup trendStored (drawnTrend ())

                // A hosted view: a Fuaran-UI chart the host renders itself,
                // through the same published renderer entry point.
                let hosted =
                    match
                        Presentation.build<unit> (request PresentationKind.Trend "orders") [
                            SeriesAnswer.Answered orders
                        ]
                    with
                    | Ok block ->
                        renderToStaticMarkup (
                            Fuaran.UI.Renderer.Render.renderWithSources
                                Fuaran.UI.Renderer.BindingResolver.empty
                                ignore
                                block.Node
                        )
                    | Error defect -> failwithf "fixture: %s" (PresentationDefect.describe defect)

                Expect.equal
                    (seriesColours blockChart)
                    (Array.sort housePalette.[0..1])
                    "the block draws the house palette"

                Expect.equal
                    (seriesColours blockChart)
                    (seriesColours hosted)
                    "the same series colours as the hosted view"

                let defaults =
                    Fuaran.UI.Charts.ChartStyle.defaults.Palette |> Array.map _.ToLowerInvariant()

                Expect.isFalse
                    (hexColours blockChart |> Array.exists (fun h -> Array.contains h defaults))
                    "and none of the shipped default palette"
            finally
                Fuaran.UI.Charts.clearChartStyle ()
    ]

let tests =
    testList "Conversation block render (Phase 1130)" [
        noBlockTests
        renderTests
        driftTests
        refusalTests
        mountedTests
        panelTests
        themeTests
    ]