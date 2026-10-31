// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.LayoutToolsTests

// The layout tools: eight tools that propose an edit of a stored page as ops
// over the caller's folded page, validated and previewed, and a ninth that
// saves a proposal to a layer under the page store's gate. Driven in process
// over the real page store, the real fact store and the real page request
// resolver: a page seeded through the store with a comparison chart and a
// breakdowns section; the two acceptance edits proposed, previewed and saved
// by a team owner; inline rows refused by name; a member's team save refused
// and audited; a reset that needs the layer's grant; proposals never shared
// between callers; and composition only with a fact store and a page store.

open System
open System.Text
open Expecto
open Microsoft.Extensions.DependencyInjection
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Ops.Types
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.StorageScopeResolver
open ToolUp.AI
open ToolUp.AI.AICompose
open ToolUp.Facts
open ToolUp.Facts.Presentation
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.FactRenderToolTests.RenderFixtures

let private run (a: Async<'T>) : 'T = Async.RunSynchronously a

// ── The rig: one team, its facts, its page ─────────────────────────────

let private teamId = "t1"

let private teamScope =
    ScopeResolution.ofStorageScope (StorageScope.ofKey true (ScopeKey.Team teamId))

let private roles =
    Map.ofList [ "owner", TeamRole.Owner; "member", TeamRole.Member ]

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private page = {
    PageRef.Module = "trade"
    Page = "daily"
}

type private Rig = {
    Deps: FactBrowseHandler.FactBrowseDeps
    Store: IPageDocumentStore
    Audit: IAuditLog
    Cache: PageResolutionCache
    Proposals: LayoutProposalStore
}

let private ok (label: string) (r: Async<Result<'T, 'E>>) : 'T =
    match run r with
    | Ok v -> v
    | Error e -> failtestf "%s: %A" label e

/// Facts written in the team's scope, a page store over in-memory stores.
let private facts () : FactBrowseHandler.FactBrowseDeps =
    let storage = InMemoryBlobStorage()
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let ticks = ref 0

    let clock () =
        ticks.Value <- ticks.Value + 1
        day1.AddDays(10.0).AddSeconds(float ticks.Value)

    let store =
        BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

    let writer =
        DefaultFactTableWriter.createWithClock store storage events tables (Some registry) clock

    seed writer teamScope trade "revenue"

    {
        Tables = tables
        Writer = Some writer
        Store = store
        Gate = FactDisclosureGate(store, events)
        Registry = Some registry
        Clock = clock
    }

let private rig () : Rig =
    let audit =
        AuditLog.EventStoreAuditLog(InMemoryEventStore.InMemoryEventStore(), silentLogger) :> IAuditLog

    {
        Deps = facts ()
        Store =
            BlobPageDocumentStore(
                DataObjectStore.DataObjectStore(InMemoryBlobStorage()),
                PermissionStore.PermissionStore(InMemoryBlobStorage()),
                (fun _ user -> async { return Map.tryFind user roles }),
                audit,
                PageStoreOptions.defaults
            )
        Audit = audit
        Cache = PageResolutionCache None
        Proposals = LayoutProposalStore()
    }

let private memberOf (userId: string) =
    AccessContext.unrestricted (TeamMember(userId, teamId))

let private admin = {
    memberOf "operator" with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

/// The caller `userId` of the rig.
let private callerOf (r: Rig) (userId: string) : LayoutCaller =
    let gate () =
        async.Return(
            PageGateViewer.fingerprint {
                Principal = userId
                ScopeId = teamScope.ScopeId
                ActiveTeamId = Some teamId
                IsPlatformAdmin = false
                TeamRole = None
                OutputVisibility = None
            }
        )

    let session = run (r.Cache.Session(teamScope, userId))

    {
        Scope = teamScope
        Access = memberOf userId
        Deps = r.Deps
        Resolver = PageRequestResolver.create session gate r.Deps teamScope userId
        Store = r.Store
        Proposals = r.Proposals
    }

// ── The seed page ──────────────────────────────────────────────────────

/// The last day's revenue against the delivery before it, per site.
let private comparison: PresentationRequest = {
    trendRequest with
        Kind = PresentationKind.Comparison
        Title = Some "Revenue against the delivery before"
}

/// One bar per site on the last day.
let private breakdown: PresentationRequest = {
    trendRequest with
        Kind = PresentationKind.Breakdown
        Title = Some "Revenue by site"
}

let private seedRequests =
    Map.ofList [
        "revenue-30d", PageRequest.Presentation(comparison, [])
        "revenue-by-site", PageRequest.Presentation(breakdown, [])
    ]

/// A chart bound to `key`, its fields read from the request's resolution.
let private chartFor (r: Rig) (id: string) (key: string) (request: PresentationRequest) : Node<unit> =
    let resolution =
        (callerOf r "operator").Resolver.ResolvePresentation request
        |> run
        |> function
            | Ok res -> res
            | Error e -> failtestf "the fixture request resolves: %s" (PageRefusal.describe e)

    let x, ys =
        match resolution.Columns with
        | x :: ys -> x, ys
        | [] -> failtest "the fixture resolution has columns"

    Fuaran.chart id {
        Defaults.chart with
            Kind = ChartKind.Bar
            Source = Binding.Query(key, (fun (o: obj) -> unbox<Map<string, obj> seq> o), None)
            XField = x
            YFields = ys
    }

let private container (id: string) (role: BoxRole) (heading: string) (children: Node<unit> list) =
    Fuaran.box id {
        Defaults.box with
            Role = role
            Heading = Some(TextSource.Literal heading)
            Children = children
    }

/// root ─ title, revenue(card: the comparison chart), breakdowns(section: by-site card)
let private seedTree (r: Rig) : Node<unit> =
    Fuaran.box "root" {
        Defaults.box with
            Children = [
                Fuaran.heading "title" {
                    Defaults.heading with
                        Text = TextSource.Literal "Daily trade"
                }
                container "revenue" BoxRole.Card "Revenue" [ chartFor r "revenue-chart" "revenue-30d" comparison ]
                container "breakdowns" BoxRole.Group "Breakdowns" [
                    container "by-site" BoxRole.Card "Revenue by site" [
                        chartFor r "by-site-chart" "revenue-by-site" breakdown
                    ]
                ]
            ]
    }

let private sidecarOf (requests: Map<string, PageRequest>) =
    match
        PageRequestSidecar.toBytes {
            PageRequestSidecar.empty with
                Requests = requests
        }
    with
    | Ok bytes -> bytes
    | Error e -> failtest e

/// A rig with the page seeded through the store by a platform admin.
let private seeded () : Rig =
    let r = rig ()

    r.Store.Seed(
        teamScope,
        admin,
        page,
        {
            PageSeed.Document = Encoding.UTF8.GetBytes(PageLayers.encodeDocument (seedTree r))
            Sidecar = Some(sidecarOf seedRequests)
        }
    )
    |> ok "seed"
    |> ignore

    r

// ── Calls ──────────────────────────────────────────────────────────────

let private call (r: Rig) (userId: string) (tool: LayoutTool) (args: string) : ToolHelpers.ToolAnswer =
    LayoutTools.answerWith tool (callerOf r userId) args |> run

let private pageArgs (members: string) =
    sprintf """{"module":"trade","page":"daily",%s}""" members

/// The proposal id an answer names, failing when it names none.
let private proposalOf (answer: ToolHelpers.ToolAnswer) : string =
    let marker = "Proposal id: "

    match answer.Text.IndexOf marker with
    | -1 -> failtestf "expected a proposal, got: %s" answer.Text
    | i -> answer.Text.Substring(i + marker.Length, 32)

let private save (r: Rig) (userId: string) (proposal: string) (layer: string) =
    call r userId LayoutTool.Save (sprintf """{"proposal":"%s","layer":"%s"}""" proposal layer)

let private folded (r: Rig) (userId: string) : FoldedPage =
    r.Store.ReadFolded(teamScope, memberOf userId, page) |> ok "fold"

let private requestsOf (page: FoldedPage) : Map<string, PageRequest> =
    match page.Layers |> List.rev |> List.tryPick _.Sidecar with
    | Some bytes ->
        match PageRequestSidecar.ofBytes bytes with
        | Ok sidecar -> sidecar.Requests
        | Error e -> failtest e
    | None -> failtest "the page carries a sidecar"

let private chartOf (tree: Node<unit>) (id: string) : ChartSpec<unit> =
    let rec find (n: Node<unit>) =
        if n.Id = id then
            Some n
        else
            NodeChildren.children NodeChildren.Reach.kindHeld n |> List.tryPick find

    match find tree |> Option.map _.Kind with
    | Some(NodeKind.Chart spec) -> spec
    | other -> failtestf "expected chart '%s', got %A" id other

/// The ids of a container's children.
let private childrenOf (tree: Node<unit>) (id: string) : string list option =
    let rec find (n: Node<unit>) =
        if n.Id = id then
            Some n
        else
            NodeChildren.children NodeChildren.Reach.kindHeld n |> List.tryPick find

    find tree
    |> Option.map (fun n -> NodeChildren.children NodeChildren.Reach.kindHeld n |> List.map _.Id)

let private keyOf (spec: ChartSpec<unit>) =
    match spec.Source with
    | Binding.Query(key, _, _) -> key
    | other -> failtestf "expected a request binding, got %A" other

let private expectProposed (answer: ToolHelpers.ToolAnswer) =
    match answer.Parts with
    | [ BlockPart(node, provenance) ] ->
        Expect.equal provenance.Kind LayoutTools.ProposedKind "the preview is marked proposed"
        Expect.stringContains provenance.Request (proposalOf answer) "the provenance names the proposal"
        Expect.isNotEmpty node "the preview carries the proposed page"
        Expect.isNonEmpty provenance.FactIds "the preview draws facts read under the caller's gate"
        Expect.isFalse (answer.Text.Contains "1000.25") "the text the model reads carries no value"
    | other -> failtestf "expected one proposed block, got %A (%s)" other answer.Text

// ── The tests ──────────────────────────────────────────────────────────

let private acceptanceTests =
    testList "the acceptance edits" [
        test
            "changing what the comparison block compares proposes ops, previews them as proposed, and saves under an owner" {
            let r = seeded ()

            let answer =
                call
                    r
                    "owner"
                    LayoutTool.SetRequest
                    (pageArgs """ "key":"revenue-30d","period":{"day":"2026-10-02","rangeDays":2} """)

            expectProposed answer

            Expect.equal
                (chartOf (folded r "owner").Tree "revenue-chart" |> keyOf)
                "revenue-30d"
                "a proposal writes nothing"

            let saved = save r "owner" (proposalOf answer) "team"
            Expect.stringContains saved.Text "Saved proposal" saved.Text

            let after = folded r "owner"
            let chart = chartOf after.Tree "revenue-chart"
            let key = keyOf chart

            Expect.stringStarts key "revenue-30d~" "the changed request is held under a new key of the same stem"
            Expect.equal chart.YFields [ "2026-10-01"; "2026-10-02" ] "the series follow the new deliveries"

            let requests = requestsOf after
            Expect.isFalse (requests.ContainsKey "revenue-30d") "the old request is dropped"

            match requests.TryFind key with
            | Some(PageRequest.Presentation(request, _)) ->
                Expect.equal request.Kind PresentationKind.Comparison "still a comparison"
                Expect.equal request.Period (PresentationPeriod.Ending(day1.AddDays 1.0, 2)) "over the new range"
            | other -> failtestf "expected the new request, got %A" other
        }

        test "adding a breakdown under the breakdowns section binds it to a new request and saves under an owner" {
            let r = seeded ()

            let answer =
                call
                    r
                    "owner"
                    LayoutTool.AddBlock
                    (pageArgs
                        """ "request":{"kind":"presentation","payload":{"kind":"breakdown","tableId":"daily-trade","metrics":["revenue"],"period":{"day":"2026-10-01"}}},"key":"revenue-day-one","title":"Revenue by site, first day","into":"breakdowns" """)

            expectProposed answer
            Expect.stringContains answer.Text "into 'breakdowns'" "the summary says where"

            let saved = save r "owner" (proposalOf answer) "team"
            Expect.stringContains saved.Text "Saved proposal" saved.Text

            let after = folded r "owner"
            let chart = chartOf after.Tree "revenue-day-one-chart"
            Expect.stringStarts (keyOf chart) "revenue-day-one~" "the block names its request"
            Expect.equal chart.XField "subject" "a breakdown's axis"

            let section =
                match childrenOf after.Tree "breakdowns" with
                | Some ids -> ids
                | None -> failtest "the section is on the page"

            Expect.equal section [ "by-site"; "revenue-day-one" ] "added last in the section"
            Expect.isTrue ((requestsOf after).ContainsKey(keyOf chart)) "the sidecar holds the request"
        }
    ]


let private refusalTests =
    testList "refusals" [
        test "layout_add_block with inline rows is refused by name, and nothing is proposed" {
            let r = seeded ()

            let answer =
                call
                    r
                    "owner"
                    LayoutTool.AddBlock
                    (pageArgs
                        """ "request":{"kind":"presentation","payload":{"kind":"breakdown","tableId":"daily-trade","metrics":["revenue"],"period":{"day":"2026-10-01"}}},"rows":[{"subject":"s1","revenue":1}] """)

            Expect.stringContains answer.Text "(inline-rows)" answer.Text
            Expect.stringContains answer.Text "'rows'" "the refusal names the argument"
            Expect.isEmpty answer.Parts "nothing is previewed"
        }

        test "a chart on the page that draws anything but a held request is refused by name" {
            let r = seeded ()
            let tree = (folded r "owner").Tree

            Expect.isEmpty (LayoutTools.inlineRows seedRequests tree) "the seed names only requests it holds"

            let inline' =
                Fuaran.chart "static-chart" {
                    Defaults.chart with
                        Source = Binding.Static(Some Seq.empty)
                        XField = "subject"
                        YFields = [ "revenue" ]
                }

            let withStatic =
                match Fuaran.UI.Ops.Apply.apply (TreeOp.InsertChild(NodeId "root", inline')) tree with
                | Ok t -> t
                | Error e -> failtest e.Message

            match LayoutTools.inlineRows seedRequests withStatic with
            | [ LayoutRefusal.InlineRows where ] -> Expect.stringContains where "'static-chart'" "named"
            | other -> failtestf "expected the static chart refused, got %A" other

            match LayoutTools.inlineRows (seedRequests |> Map.remove "revenue-30d") tree with
            | [ LayoutRefusal.UnknownRequest("revenue-chart", "revenue-30d") ] -> ()
            | other -> failtestf "expected the unheld request refused, got %A" other
        }

        test
            "an argument the tool does not take, an unknown block and a request the page does not hold are refused by name" {
            let r = seeded ()

            let unknown =
                call r "owner" LayoutTool.RemoveBlock (pageArgs """ "block":"by-site","save":"team" """)

            Expect.stringContains unknown.Text "'save' is not an argument" unknown.Text

            let missing =
                call r "owner" LayoutTool.RemoveBlock (pageArgs """ "block":"nowhere" """)

            Expect.stringContains missing.Text "(unknown-node)" missing.Text

            let noRequest =
                call r "owner" LayoutTool.SetRequest (pageArgs """ "key":"nothing","metrics":["revenue"] """)

            Expect.stringContains noRequest.Text "holds no request 'nothing'" noRequest.Text
        }

        test "a chart the edit touches that does not type against its request's schema is refused by the binding check" {
            let r = seeded ()

            r.Store.AppendOps(
                teamScope,
                admin,
                page,
                PageLayer.Platform,
                [
                    TreeOp.UpdateProp(
                        NodeId "by-site-chart",
                        "YFields",
                        PropValue.Wire(Fuaran.Core.JArr [ Fuaran.Core.JStr "margin" ])
                    )
                ],
                Some(sidecarOf seedRequests)
            )
            |> ok "a platform edit naming a column the request does not resolve to"
            |> ignore

            let answer =
                call r "owner" LayoutTool.Retitle (pageArgs """ "block":"by-site-chart","title":"Sites" """)

            Expect.stringContains answer.Text "(binding)" answer.Text
            Expect.stringContains answer.Text "margin" "the defect names the column"
            Expect.isEmpty answer.Parts "nothing is previewed"
        }

        test "a request naming a metric its table does not carry is refused, and nothing is proposed" {
            let r = seeded ()

            let answer =
                call r "owner" LayoutTool.SetRequest (pageArgs """ "key":"revenue-by-site","metrics":["shoppers"] """)

            Expect.isEmpty answer.Parts answer.Text
            Expect.stringContains answer.Text "shoppers" "the refusal names the metric"
        }
    ]

let private gateTests =
    testList "the save gate" [
        test "a member may propose, and a member's team save is refused, audited and writes nothing" {
            let r = seeded ()
            let before = (folded r "owner").ContentHash

            let answer =
                call r "member" LayoutTool.Retitle (pageArgs """ "block":"breakdowns","title":"By site" """)

            expectProposed answer

            let refused = save r "member" (proposalOf answer) "team"
            Expect.stringContains refused.Text "(store-grant-missing)" refused.Text

            match r.Audit.GetAuditTrail(teamScope.ScopeId, None, Some "PageWriteRefused") |> run with
            | [ PageWriteRefused p ] ->
                Expect.equal p.Reason "grant-missing" "the refusal is audited by cause"
                Expect.equal p.ActorId "member" "naming the member"
                Expect.equal p.Layer "team" "and the layer"
            | other -> failtestf "expected one refusal audited, got %A" other

            Expect.equal (folded r "owner").ContentHash before "nothing was written"
        }

        test "a proposal is its proposer's: another caller cannot save it" {
            let r = seeded ()

            let answer =
                call r "owner" LayoutTool.Retitle (pageArgs """ "block":"breakdowns","title":"By site" """)

            let other = save r "member" (proposalOf answer) "team"
            Expect.stringContains other.Text "(unknown-proposal)" other.Text
        }

        test "layout_reset names the layer and lands only with that layer's grant" {
            let r = seeded ()
            let seededHash = (folded r "owner").ContentHash

            let retitled =
                call r "owner" LayoutTool.Retitle (pageArgs """ "block":"breakdowns","title":"By site" """)

            Expect.stringContains (save r "owner" (proposalOf retitled) "team").Text "Saved proposal" "the team edit"

            let edited = (folded r "owner").ContentHash
            Expect.notEqual edited seededHash "the team layer changed the page"

            let byMember = call r "member" LayoutTool.Reset (pageArgs """ "layer":"team" """)
            expectProposed byMember

            let refused = save r "member" (proposalOf byMember) "team"
            Expect.stringContains refused.Text "(store-grant-missing)" refused.Text
            Expect.equal (folded r "owner").ContentHash edited "the member's reset wrote nothing"

            let byOwner = call r "owner" LayoutTool.Reset (pageArgs """ "layer":"team" """)

            let mismatch = save r "owner" (proposalOf byOwner) "platform"
            Expect.stringContains mismatch.Text "(layer-mismatch)" "a reset saves to the layer it names"

            let reset = save r "owner" (proposalOf byOwner) "team"
            Expect.stringContains reset.Text "Saved proposal" reset.Text
            Expect.equal (folded r "owner").ContentHash seededHash "the page folds to the seed again"
        }

        test "a proposal made against a page that has since changed is refused as stale" {
            let r = seeded ()

            let first =
                call r "owner" LayoutTool.Retitle (pageArgs """ "block":"breakdowns","title":"One" """)

            let second =
                call r "owner" LayoutTool.Retitle (pageArgs """ "block":"title","title":"Two" """)

            Expect.stringContains (save r "owner" (proposalOf first) "team").Text "Saved proposal" "the first lands"

            let stale = save r "owner" (proposalOf second) "team"
            Expect.stringContains stale.Text "(stale-proposal)" stale.Text
        }
    ]

let private editTests =
    testList "the other edits" [
        test "move, add a section, set the page's standing range and remove each propose and save" {
            let r = seeded ()

            let saveOf (answer: ToolHelpers.ToolAnswer) =
                let saved = save r "owner" (proposalOf answer) "team"
                Expect.stringContains saved.Text "Saved proposal" saved.Text

            saveOf (call r "owner" LayoutTool.MoveBlock (pageArgs """ "block":"revenue","after":"breakdowns" """))

            Expect.equal (childrenOf (folded r "owner").Tree "root") (Some [ "title"; "breakdowns"; "revenue" ]) "moved"

            saveOf (
                call
                    r
                    "owner"
                    LayoutTool.SetSection
                    (pageArgs """ "action":"add","title":"Notes","before":"revenue" """)
            )

            Expect.equal
                (childrenOf (folded r "owner").Tree "root")
                (Some [ "title"; "breakdowns"; "notes"; "revenue" ])
                "a new section, placed"

            saveOf (call r "owner" LayoutTool.SetDefault (pageArgs """ "period":{"day":"2026-10-02","rangeDays":2} """))

            let requests = requestsOf (folded r "owner")
            Expect.equal requests.Count 2 "both requests, re-keyed"

            for KeyValue(_, request) in requests do
                match request with
                | PageRequest.Presentation(p, _) ->
                    Expect.equal p.Period (PresentationPeriod.Ending(day1.AddDays 1.0, 2)) "each takes the range"
                | PageRequest.Pipeline _ -> failtest "no pipeline here"

            saveOf (call r "owner" LayoutTool.RemoveBlock (pageArgs """ "block":"by-site" """))
            Expect.equal (childrenOf (folded r "owner").Tree "breakdowns") (Some []) "removed"
            Expect.equal (requestsOf (folded r "owner")).Count 1 "its request is dropped with it"
        }
    ]

// ── Composition ────────────────────────────────────────────────────────

let private aiApp = AIToolSchemaTests.LayoutCensus.aiApp

let private names (app: AIServerApp) =
    app.AIAnsweringTools |> List.map (fun (d, _) -> d.Name)

let private compositionTests =
    testList "the composition" [
        test "withLayoutTools composes the nine tools with a fact store and a page store, once" {
            let once = aiApp EnabledFactStore true |> PresentationCompose.withLayoutTools
            Expect.equal (names once) (LayoutTools.all |> List.map LayoutTools.name) "the nine tools"

            let twice = once |> PresentationCompose.withLayoutTools
            Expect.equal (names twice) (names once) "idempotent"

            let again = twice.Base |> PresentationCompose.withPageRequests
            Expect.isTrue (obj.ReferenceEquals(again, twice.Base)) "the page request resolvers are composed once"
        }

        test "no fact store, or no page store, registers nothing and returns the app itself" {
            for factStore, pageStore in [ NoFactStore, true; EnabledFactStore, false; NoFactStore, false ] do
                let before = aiApp factStore pageStore
                let after = before |> PresentationCompose.withLayoutTools
                Expect.isTrue (obj.ReferenceEquals(before, after)) (sprintf "%A, page store %b" factStore pageStore)
        }

        test "the runtime is registered in a factory over the page store and the resolvers" {
            let app =
                aiApp EnabledFactStore true |> PresentationCompose.withLayoutTools |> composeAI

            let services = ServiceCollection() :> IServiceCollection

            match ServerApp.serviceConfig app with
            | Some configure -> configure services |> ignore
            | None -> failtest "the composition registers its services"

            let runtime =
                services |> Seq.tryFind (fun d -> d.ServiceType = typeof<LayoutToolsRuntime>)

            match runtime with
            | Some d -> Expect.isNotNull (box d.ImplementationFactory) "resolved in a registration factory"
            | None -> failtest "the runtime is registered"
        }
    ]

let tests =
    testList "Layout tools" [ acceptanceTests; refusalTests; gateTests; editTests; compositionTests ]