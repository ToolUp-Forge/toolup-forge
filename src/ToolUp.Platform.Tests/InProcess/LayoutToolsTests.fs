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
open Fuaran.UI.Ops
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

let private rigWith (options: PageStoreOptions) : Rig =
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
                options
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

/// root ─ title, revenue(card: the comparison chart), breakdowns(section: `breakdowns r`)
let private seedTreeWith (breakdowns: Rig -> Node<unit> list) (r: Rig) : Node<unit> =
    Fuaran.box "root" {
        Defaults.box with
            Children = [
                Fuaran.heading "title" {
                    Defaults.heading with
                        Text = TextSource.Literal "Daily trade"
                }
                container "revenue" BoxRole.Card "Revenue" [ chartFor r "revenue-chart" "revenue-30d" comparison ]
                container "breakdowns" BoxRole.Group "Breakdowns" (breakdowns r)
            ]
    }

/// The by-site card: the breakdown chart.
let private bySite (r: Rig) =
    container "by-site" BoxRole.Card "Revenue by site" [ chartFor r "by-site-chart" "revenue-by-site" breakdown ]

/// The breakdowns section holds the by-site card.
let private seedTree (r: Rig) : Node<unit> = seedTreeWith (fun r -> [ bySite r ]) r

let private sidecarOf (requests: Map<string, PageRequest>) =
    match
        PageRequestSidecar.toBytes {
            PageRequestSidecar.empty with
                Requests = requests
        }
    with
    | Ok bytes -> bytes
    | Error e -> failtest e

/// A rig over `options` with the page `tree` seeded through the store by a
/// platform admin.
let private seededWith (options: PageStoreOptions) (tree: Rig -> Node<unit>) : Rig =
    let r = rigWith options

    r.Store.Seed(
        teamScope,
        admin,
        page,
        {
            PageSeed.Document = Encoding.UTF8.GetBytes(PageLayers.encodeDocument (tree r))
            Sidecar = Some(sidecarOf seedRequests)
        }
    )
    |> ok "seed"
    |> ignore

    r

/// The page seeded under the default store options.
let private seeded () : Rig =
    seededWith PageStoreOptions.defaults seedTree

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

            saveOf (
                call
                    r
                    "owner"
                    LayoutTool.AddBlock
                    (pageArgs
                        """ "request":{"kind":"presentation","payload":{"kind":"breakdown","tableId":"daily-trade","metrics":["revenue"]}},"key":"standing","into":"notes" """)
            )

            match
                requestsOf (folded r "owner")
                |> Map.toList
                |> List.tryFind (fun (key, _) -> key.StartsWith "standing~")
            with
            | Some(_, PageRequest.Presentation(p, _)) ->
                Expect.equal
                    p.Period
                    (PresentationPeriod.Ending(day1.AddDays 1.0, 2))
                    "a block added without a range takes the page's"
            | other -> failtestf "expected the added request, got %A" other

            saveOf (call r "owner" LayoutTool.RemoveBlock (pageArgs """ "block":"standing" """))
            saveOf (call r "owner" LayoutTool.RemoveBlock (pageArgs """ "block":"by-site" """))
            Expect.equal (childrenOf (folded r "owner").Tree "breakdowns") (Some []) "removed"
            Expect.equal (requestsOf (folded r "owner")).Count 1 "its request is dropped with it"
        }
    ]

// ── Tabbed sections ────────────────────────────────────────────────────

/// The breakdowns section holds two cards: by site, and by day (the
/// comparison drawn again).
let private tabbable (r: Rig) : Node<unit> =
    seedTreeWith
        (fun r -> [
            bySite r
            container "by-day" BoxRole.Card "Revenue by day" [ chartFor r "by-day-chart" "revenue-30d" comparison ]
        ])
        r

/// A tabbable page whose deployment enables every viewer's own layer.
let private tabbableRig () =
    seededWith
        {
            PageStoreOptions.defaults with
                IndividualLayer = IndividualLayer.EveryViewer
        }
        tabbable

let private saveAs (r: Rig) (userId: string) (layer: string) (answer: ToolHelpers.ToolAnswer) =
    let saved = save r userId (proposalOf answer) layer
    Expect.stringContains saved.Text "Saved proposal" saved.Text

let private tabsArgs (tabs: string) =
    pageArgs (sprintf """ "action":"tabs","section":"breakdowns","tabs":%s """ tabs)

let private bySiteAndByDay =
    tabsArgs """[{"label":"By site","blocks":["by-site"]},{"label":"By day","blocks":["by-day"]}]"""

let private findNode (tree: Node<unit>) (id: string) : Node<unit> option =
    let rec find (n: Node<unit>) =
        if n.Id = id then
            Some n
        else
            NodeChildren.children NodeChildren.Reach.kindHeld n |> List.tryPick find

    find tree

let private parentOf (tree: Node<unit>) (id: string) : string option =
    let rec find (n: Node<unit>) =
        let children = NodeChildren.children NodeChildren.Reach.kindHeld n

        if children |> List.exists (fun c -> c.Id = id) then
            Some n.Id
        else
            children |> List.tryPick find

    find tree

let private literal (text: TextSource option) =
    match text with
    | Some(TextSource.Literal s) -> Some s
    | _ -> None

let private headingOf (tree: Node<unit>) (id: string) =
    match findNode tree id |> Option.map _.Kind with
    | Some(NodeKind.Box spec) -> literal spec.Heading
    | other -> failtestf "expected box '%s', got %A" id other

/// The section's tab set, and its tabs as (id, label, block ids).
let private tabsOf (tree: Node<unit>) (sectionId: string) =
    match childrenOf tree sectionId with
    | Some ids ->
        match
            ids
            |> List.choose (findNode tree)
            |> List.filter (fun n ->
                match n.Kind with
                | NodeKind.Tabs _ -> true
                | _ -> false)
        with
        | [ tabSet ] ->
            match tabSet.Kind with
            | NodeKind.Tabs spec ->
                Expect.isNone spec.TabHeaders "the tabs carry no header list: each card's heading is its label"

                match spec.ActiveIndex with
                | Binding.State(key, Some 0) ->
                    Expect.equal key tabSet.Id "the active tab is viewer state keyed by the tab set"
                | other -> failtestf "expected the active tab bound to viewer state, got %A" other

                tabSet.Id,
                spec.Children
                |> List.map (fun tab -> tab.Id, headingOf tree tab.Id, childrenOf tree tab.Id |> Option.defaultValue [])
            | _ -> failtest "a tab set"
        | other -> failtestf "expected one tab set in '%s', got %A" sectionId (other |> List.map _.Id)
    | None -> failtestf "no section '%s'" sectionId

/// The ops one layer holds, as stored.
let private layerOps (page: FoldedPage) (layer: PageLayer) : TreeOp<unit> list =
    page.Layers
    |> List.filter (fun l -> l.Key.Layer = layer)
    |> List.collect (fun l -> l.Records |> List.map _.Op)

/// Every op stays inside `sectionId`'s subtree, and none re-states a node's
/// children: no `EditNode` over a node holding children, no `ReplaceRoot`.
let private expectConfined (before: Node<unit>) (sectionId: string) (ops: TreeOp<unit> list) =
    let subtree (tree: Node<unit>) =
        match findNode tree sectionId with
        | Some section -> Introspect.allNodeIds section |> Set.ofList
        | None -> Set.empty

    Expect.isNonEmpty ops "the verb emitted ops"

    ops
    |> List.fold
        (fun (tree: Node<unit>) op ->
            match op with
            | TreeOp.ReplaceRoot _ -> failtest "a ReplaceRoot"
            | TreeOp.Batch _ -> failtest "a batch: each op is checked on its own"
            | TreeOp.EditNode(NodeId id, _) ->
                match findNode tree id |> Option.bind (fun n -> Introspect.getChildren n.Kind) with
                | Some(_ :: _) -> failtestf "an EditNode over '%s', which holds children" id
                | _ -> ()
            | _ -> ()

            match Apply.apply op tree with
            | Error e -> failtestf "%A does not apply: %s" op e.Message
            | Ok after ->
                let inside = Set.union (subtree tree) (subtree after)

                for target in TreeOp.targets op do
                    Expect.isTrue (inside.Contains target) (sprintf "%A stays inside section '%s'" op sectionId)

                after)
        before
    |> ignore

let private tabTests =
    testList "tabbed sections" [
        test
            "putting the breakdowns in tabs by site and by day proposes, previews and saves, and moves no block out of its id" {
            let r = tabbableRig ()
            let before = (folded r "owner").Tree

            let answer = call r "owner" LayoutTool.SetSection bySiteAndByDay
            expectProposed answer
            Expect.stringContains answer.Text "2 tab(s): 'By site', 'By day'" answer.Text

            Expect.equal
                (childrenOf (folded r "owner").Tree "breakdowns")
                (Some [ "by-site"; "by-day" ])
                "a proposal writes nothing"

            saveAs r "owner" "team" answer

            let after = folded r "owner"
            let tabSetId, tabs = tabsOf after.Tree "breakdowns"
            Expect.equal (childrenOf after.Tree "breakdowns") (Some [ tabSetId ]) "the section holds its tabs"

            Expect.equal
                (tabs |> List.map (fun (_, label, blocks) -> label, blocks))
                [ Some "By site", [ "by-site" ]; Some "By day", [ "by-day" ] ]
                "each block in its tab, under its label"

            Expect.equal (chartOf after.Tree "by-site-chart" |> keyOf) "revenue-by-site" "the block is the same node"
            Expect.isEmpty after.Conflicts "the fold is clean"
            expectConfined before "breakdowns" (layerOps after PageLayer.Team)
        }

        test "an individual's retitle inside a block survives the team tabbing the section" {
            let r = tabbableRig ()

            call r "member" LayoutTool.Retitle (pageArgs """ "block":"by-site-chart","title":"My sites" """)
            |> saveAs r "member" "individual"

            call r "owner" LayoutTool.SetSection bySiteAndByDay |> saveAs r "owner" "team"

            let mine = folded r "member"
            Expect.isEmpty mine.Conflicts "nothing conflicts"
            Expect.equal (literal (chartOf mine.Tree "by-site-chart").Title) (Some "My sites") "the retitle is intact"

            let _, tabs = tabsOf mine.Tree "breakdowns"

            Expect.equal
                (tabs |> List.map (fun (_, label, blocks) -> label, blocks))
                [ Some "By site", [ "by-site" ]; Some "By day", [ "by-day" ] ]
                "and the block is inside its tab"

            let ops = layerOps mine PageLayer.Individual

            match ops with
            | [ TreeOp.UpdateProp(NodeId "by-site-chart", "Title", _) ] -> ()
            | other -> failtestf "the individual layer holds its one edit, got %A" other
        }

        test "the team tabs, an individual retitles a block inside a tab, the team renames the tab: both edits hold" {
            let r = tabbableRig ()
            call r "owner" LayoutTool.SetSection bySiteAndByDay |> saveAs r "owner" "team"

            let siteTab =
                match tabsOf (folded r "owner").Tree "breakdowns" with
                | _, (id, _, _) :: _ -> id
                | _ -> failtest "the tabs"

            call r "member" LayoutTool.Retitle (pageArgs """ "block":"by-site-chart","title":"My sites" """)
            |> saveAs r "member" "individual"

            let before = (folded r "owner").Tree
            let opsBefore = layerOps (folded r "owner") PageLayer.Team |> List.length

            call
                r
                "owner"
                LayoutTool.SetSection
                (tabsArgs (
                    sprintf
                        """[{"tab":"%s","label":"Sites","blocks":["by-site"]},{"label":"By day","blocks":["by-day"]}]"""
                        siteTab
                ))
            |> saveAs r "owner" "team"

            let renamed = layerOps (folded r "owner") PageLayer.Team |> List.skip opsBefore

            match renamed with
            | [ TreeOp.UpdateProp(NodeId id, "Heading", _) ] when id = siteTab -> ()
            | other -> failtestf "renaming a tab is one edit of its heading, got %A" other

            expectConfined before "breakdowns" renamed

            let mine = folded r "member"
            Expect.isEmpty mine.Conflicts "nothing conflicts"
            Expect.equal (literal (chartOf mine.Tree "by-site-chart").Title) (Some "My sites") "the retitle holds"
            Expect.equal (headingOf mine.Tree siteTab) (Some "Sites") "and so does the rename"
            Expect.equal (parentOf mine.Tree "by-site") (Some siteTab) "the block is still in its tab"
        }

        test "an individual's tabs keep the team's later edits under the section" {
            let r = tabbableRig ()

            call r "member" LayoutTool.SetSection bySiteAndByDay
            |> saveAs r "member" "individual"

            call r "owner" LayoutTool.Retitle (pageArgs """ "block":"by-site-chart","title":"Sites, team" """)
            |> saveAs r "owner" "team"

            call r "owner" LayoutTool.MoveBlock (pageArgs """ "block":"revenue","into":"breakdowns" """)
            |> saveAs r "owner" "team"

            let mine = folded r "member"
            Expect.isEmpty mine.Conflicts "nothing conflicts"

            Expect.equal
                (literal (chartOf mine.Tree "by-site-chart").Title)
                (Some "Sites, team")
                "the team's later retitle reaches the block inside the individual's tab"

            let tabSetId, tabs = tabsOf mine.Tree "breakdowns"

            Expect.equal
                (childrenOf mine.Tree "breakdowns")
                (Some [ "revenue"; tabSetId ])
                "the block the team added to the section is not lost"

            Expect.equal
                (tabs |> List.map (fun (_, _, blocks) -> blocks))
                [ [ "by-site" ]; [ "by-day" ] ]
                "the tabs hold"
        }

        test "a tab the team removes under an individual's retitle of it is a conflict naming both acts" {
            let r = tabbableRig ()
            call r "owner" LayoutTool.SetSection bySiteAndByDay |> saveAs r "owner" "team"

            let siteTab, dayTab =
                match tabsOf (folded r "owner").Tree "breakdowns" with
                | _, [ (site, _, _); (day, _, _) ] -> site, day
                | _ -> failtest "two tabs"

            call r "member" LayoutTool.Retitle (pageArgs (sprintf """ "block":"%s","title":"Days" """ dayTab))
            |> saveAs r "member" "individual"

            call
                r
                "owner"
                LayoutTool.SetSection
                (tabsArgs (sprintf """[{"tab":"%s","label":"All","blocks":["by-site","by-day"]}]""" siteTab))
            |> saveAs r "owner" "team"

            let removedAt =
                layerOps (folded r "owner") PageLayer.Team
                |> List.findIndexBack (function
                    | TreeOp.RemoveNode(NodeId id) -> id = dayTab
                    | _ -> false)

            let mine = folded r "member"

            match mine.Conflicts with
            | [ conflict ] ->
                Expect.equal conflict.Act.Layer PageLayer.Individual "the individual's act"
                Expect.equal conflict.Act.Principal "member" "by the member"
                Expect.equal conflict.Act.Seq 0 "its first"

                match conflict.Cause with
                | ConflictCause.Removed(NodeId target, removedBy) ->
                    Expect.equal target dayTab "the retitled tab"
                    Expect.equal removedBy.Layer PageLayer.Team "removed by the team's act"
                    Expect.equal removedBy.Seq removedAt "the re-tab's removal"
                | other -> failtestf "expected a removal, got %A" other
            | other -> failtestf "expected one conflict, got %A" other

            let _, tabs = tabsOf mine.Tree "breakdowns"

            Expect.equal
                (tabs |> List.map (fun (_, label, blocks) -> label, blocks))
                [ Some "All", [ "by-site"; "by-day" ] ]
                "the rest folds"
        }

        test "reordering tabs is one reorder, and untab puts the blocks back where the tabs stood" {
            let r = tabbableRig ()
            call r "owner" LayoutTool.SetSection bySiteAndByDay |> saveAs r "owner" "team"
            let tabSetId, tabs = tabsOf (folded r "owner").Tree "breakdowns"

            let count () =
                layerOps (folded r "owner") PageLayer.Team |> List.length

            let n = count ()

            call
                r
                "owner"
                LayoutTool.SetSection
                (tabsArgs """[{"label":"By day","blocks":["by-day"]},{"label":"By site","blocks":["by-site"]}]""")
            |> saveAs r "owner" "team"

            match layerOps (folded r "owner") PageLayer.Team |> List.skip n with
            | [ TreeOp.ReorderChildren(NodeId id, order) ] ->
                Expect.equal id tabSetId "the tab set"
                Expect.equal order (tabs |> List.rev |> List.map (fun (tab, _, _) -> NodeId tab)) "day, then site"
            | other -> failtestf "expected one reorder, got %A" other

            let n = count ()
            let before' = (folded r "owner").Tree

            call r "owner" LayoutTool.SetSection (pageArgs """ "action":"untab","section":"breakdowns" """)
            |> saveAs r "owner" "team"

            let after = folded r "owner"
            Expect.equal (childrenOf after.Tree "breakdowns") (Some [ "by-day"; "by-site" ]) "in tab order"
            Expect.isNone (findNode after.Tree tabSetId) "the tab set is gone"
            expectConfined before' "breakdowns" (layerOps after PageLayer.Team |> List.skip n)
        }

        test "a block named twice, a node outside the section, a block in no tab and an unknown tab are refused by name" {
            let r = tabbableRig ()
            let hash = (folded r "owner").ContentHash

            let refused (args: string) (cause: string) (names: string) =
                let answer = call r "owner" LayoutTool.SetSection args
                Expect.stringContains answer.Text (sprintf "(%s)" cause) answer.Text
                Expect.stringContains answer.Text names answer.Text
                Expect.isEmpty answer.Parts "nothing is proposed"

            refused
                (tabsArgs """[{"label":"A","blocks":["by-site"]},{"label":"B","blocks":["by-day","by-site"]}]""")
                "tabs-block-named-twice"
                "'by-site'"

            refused
                (tabsArgs """[{"label":"A","blocks":["by-site","title"]},{"label":"B","blocks":["by-day"]}]""")
                "tabs-not-in-section"
                "'title'"

            refused
                (tabsArgs """[{"label":"A","blocks":["by-site","nowhere"]},{"label":"B","blocks":["by-day"]}]""")
                "unknown-node"
                "'nowhere'"

            refused (tabsArgs """[{"label":"A","blocks":["by-site"]}]""") "tabs-unassigned" "'by-day'"

            refused
                (tabsArgs """[{"tab":"by-site","label":"A","blocks":["by-site","by-day"]}]""")
                "tabs-unknown-tab"
                "'by-site'"

            refused (pageArgs """ "action":"untab","section":"breakdowns" """) "tabs-not-tabbed" "'breakdowns'"
            refused (tabsArgs "[]") "arguments" "untab"
            refused (tabsArgs """[{"label":"A","blocks":[]}]""") "arguments" "holds no blocks"

            refused
                (pageArgs """ "action":"add","title":"X","tabs":[{"label":"A","blocks":["by-site"]}] """)
                "arguments"
                "'tabs'"

            Expect.equal (folded r "owner").ContentHash hash "nothing was written"
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
    testList "Layout tools" [
        acceptanceTests
        refusalTests
        gateTests
        editTests
        tabTests
        compositionTests
    ]