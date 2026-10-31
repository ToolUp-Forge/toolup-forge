// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open System
open System.Collections.Generic
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Ops
open Fuaran.UI.Ops.Types
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Grounding
open ToolUp.AI.Wire
open ToolUp.Facts
open ToolUp.AI

// ─── The layout tools ──────────────────────────────────────────────
//
// Eight server-resident tools that edit a stored page (Phase 1165) the way an
// analyst asks for it — "compare against a longer period", "add revenue by
// site under the breakdowns" — and a ninth that saves what they proposed.
//
// **The tools hold no op vocabulary and no interpreter.** Every edit is a list
// of the language tier's tree ops (`Fuaran.UI.Ops.Abstractions`), applied to a
// copy of the caller's folded page with the language tier's engine
// (`Fuaran.UI.Ops.Apply.apply`), and saved through the page store, which
// folds the same ops with the same engine.
//
// **Proposing is open; saving is gated (plan D3).** Each of the eight tools
// reads the caller's folded page, computes ops, applies them to a copy,
// validates the copy and answers with a preview block marked `proposed` and a
// proposal id. Nothing is written. `layout_save` takes that id and a layer and
// lands the ops through `IPageDocumentStore.AppendOps` (or, for a reset,
// `ResetLayer`), under the store's layer gate and audit. No proposing tool
// carries a save argument.
//
// **The validation, in order, each a typed refusal the model reads:**
//
//   1. the ops apply to the folded page (`Apply.apply`);
//   2. the pre-emit validator (`PreEmitValidate.validateForTransport`, the
//      stored page is wire JSON) finds no Error the edit introduced. No
//      language profile is registered yet, so the base rules are the grammar;
//   3. every chart the edit touched types against its request's resolved
//      schema (`QueryBinding.check`), resolved under the caller's gate
//      through the page request resolver (Phase 1166);
//   4. no chart or grid on the page draws inline rows: each names, by a
//      `Binding.Query`, a request the page holds;
//   5. the page's read-set — derived from its requests — names only tables,
//      metrics and subject levels the composition declares.
//
// **A tool can never type a number.** A data-bearing node a tool adds or
// changes names a request key; the request is a Phase 1166 `PageRequest`; the
// rows arrive at render, under the reader's gate. A request that changes is
// minted a new key and the nodes bound to it are rebound (`ReplaceBinding`),
// so one key always names one request and every request change is an op.
//
// **The request sidecar is an interim (until the typed page document).** The
// page store keeps one opaque sidecar per layer; here it is the canonical
// JSON of the page's request map (`PageRequestSidecar`, format
// `toolup.page-requests/1`), and the read-set is derived from it
// (`PageReadSet`, `toolup.page-read-set/1`). When the language tier publishes
// the typed page document and its demanded projection, both are replaced by
// them, not kept beside them.

/// A page's standing site and range: what a request added without one takes.
type PageDefaults = {
    /// The subject path prefix (member ids from the hierarchy root).
    Site: string list option
    /// The period.
    Period: PresentationPeriod option
}

/// What a stored page's request sidecar holds: the request each
/// data-bearing node names, by key, and the page's standing defaults.
type PageRequestSidecar = {
    Requests: Map<string, PageRequest>
    Defaults: PageDefaults
}

/// The tables, metrics, subject levels and pipeline verbs a page's requests
/// read — the interim read-set, derived from the request sidecar.
type PageReadSet = {
    /// The fact tables read, sorted and distinct.
    Tables: string list
    /// Each (table, metric) read.
    Metrics: (string * string) list
    /// Each (table, subject level) a request names.
    Levels: (string * string) list
    /// The `Fuaran.Compute` verbs the pipeline requests use.
    Verbs: string list
}

/// Why a layout tool refused. Nothing was written.
[<RequireQualifiedAccess>]
type LayoutRefusal =
    /// The arguments are malformed or incomplete.
    | Arguments of reason: string
    /// A chart or grid would draw inline rows rather than name a request.
    | InlineRows of where: string
    /// No node of the page has this id.
    | UnknownNode of nodeId: string
    /// The node cannot take this edit.
    | NotEditable of nodeId: string * reason: string
    /// The ops do not apply to the caller's folded page.
    | OpsDoNotApply of reason: string
    /// The pre-emit validator found errors the edit introduced.
    | Grammar of findings: string list
    /// A chart the edit touched does not type against its request's schema.
    | Binding of defects: string list
    /// A data-bearing node names a request the page does not hold.
    | UnknownRequest of nodeId: string * key: string
    /// The page's read-set names what the composition does not declare.
    | Undeclared of reasons: string list
    /// A request could not be resolved under the caller's gate.
    | Unresolved of key: string * refusal: PageRefusal
    /// The page's request sidecar is not one this platform reads.
    | Sidecar of reason: string
    /// The page store refused.
    | Store of PageStoreError
    /// The edit changes nothing.
    | NoChange of reason: string
    /// No proposal of the caller's has this id (or it expired).
    | UnknownProposal of proposalId: string
    /// The page changed since the proposal was made.
    | StaleProposal of proposalId: string
    /// A reset proposal names another layer than the save.
    | LayerMismatch of proposed: PageLayer * named: PageLayer
    /// The deployment does not compose what the tool needs.
    | Unavailable of reason: string

/// Reading a layout refusal.
[<RequireQualifiedAccess>]
module LayoutRefusal =

    /// The page store's refusal as one sentence the model can act on.
    let describeStore (error: PageStoreError) : string =
        match error with
        | PageStoreError.GrantMissing(grant, layer) ->
            sprintf
                "you do not hold the grant the %s layer needs (%s); a team owner or admin saves the team layer and a platform admin the platform layer"
                (PageLayer.toToken layer)
                (PageGrant.toToken grant)
        | PageStoreError.LayerDisabled layer ->
            sprintf "this deployment has not enabled the %s layer" (PageLayer.toToken layer)
        | PageStoreError.ScopeRefused reason -> sprintf "your scope cannot hold that layer: %s" reason
        | PageStoreError.InvalidPageRef reason -> sprintf "the page is not a valid page reference: %s" reason
        | PageStoreError.NotSeeded -> "the page has no stored document yet"
        | PageStoreError.NoOps -> "there is nothing to save"
        | PageStoreError.OpOutsideVocabulary kind -> sprintf "the op '%s' is outside the stored page vocabulary" kind
        | PageStoreError.OpsDoNotApply conflicts ->
            sprintf
                "%d op(s) do not apply to that layer's document; propose the edit again against the layer you save to"
                (List.length conflicts)
        | PageStoreError.DocumentUndecodable reason -> sprintf "the document is not a Fuaran-UI tree: %s" reason
        | PageStoreError.StoredLayerUnreadable(key, reason) ->
            sprintf "the stored %s layer could not be read: %s" (PageLayer.toToken key.Layer) reason
        | PageStoreError.StaleWrite _ -> "another save landed first; propose the edit again"
        | PageStoreError.PermissionsUnavailable reason -> sprintf "the permissions could not be read: %s" reason
        | PageStoreError.Storage _ -> "the page store could not be written"

    /// A stable name for the refusal's cause.
    let cause (refusal: LayoutRefusal) : string =
        match refusal with
        | LayoutRefusal.Arguments _ -> "arguments"
        | LayoutRefusal.InlineRows _ -> "inline-rows"
        | LayoutRefusal.UnknownNode _ -> "unknown-node"
        | LayoutRefusal.NotEditable _ -> "not-editable"
        | LayoutRefusal.OpsDoNotApply _ -> "ops-do-not-apply"
        | LayoutRefusal.Grammar _ -> "grammar"
        | LayoutRefusal.Binding _ -> "binding"
        | LayoutRefusal.UnknownRequest _ -> "unknown-request"
        | LayoutRefusal.Undeclared _ -> "undeclared"
        | LayoutRefusal.Unresolved _ -> "unresolved"
        | LayoutRefusal.Sidecar _ -> "sidecar"
        | LayoutRefusal.Store e -> "store-" + PageStoreError.code e
        | LayoutRefusal.NoChange _ -> "no-change"
        | LayoutRefusal.UnknownProposal _ -> "unknown-proposal"
        | LayoutRefusal.StaleProposal _ -> "stale-proposal"
        | LayoutRefusal.LayerMismatch _ -> "layer-mismatch"
        | LayoutRefusal.Unavailable _ -> "unavailable"

    /// The refusal as one sentence naming what to repair.
    let describe (refusal: LayoutRefusal) : string =
        match refusal with
        | LayoutRefusal.Arguments reason -> reason
        | LayoutRefusal.InlineRows where ->
            sprintf
                "inline rows are refused: %s carries rows, and a chart or table on a page names a request whose rows the reader's gate resolves"
                where
        | LayoutRefusal.UnknownNode id -> sprintf "the page has no node '%s'" id
        | LayoutRefusal.NotEditable(id, reason) -> sprintf "node '%s' cannot take this edit: %s" id reason
        | LayoutRefusal.OpsDoNotApply reason -> sprintf "the edit does not apply to the page: %s" reason
        | LayoutRefusal.Grammar findings ->
            sprintf "the edited page fails the pre-emit validator: %s" (String.concat "; " findings)
        | LayoutRefusal.Binding defects ->
            sprintf "a chart does not type against its request: %s" (String.concat "; " defects)
        | LayoutRefusal.UnknownRequest(id, key) ->
            sprintf "node '%s' names request '%s', which the page does not hold" id key
        | LayoutRefusal.Undeclared reasons -> String.concat "; " reasons
        | LayoutRefusal.Unresolved(key, refusal) ->
            sprintf "request '%s' resolves to nothing you may be shown: %s" key (PageRefusal.describe refusal)
        | LayoutRefusal.Sidecar reason -> sprintf "the page's request sidecar is unreadable: %s" reason
        | LayoutRefusal.Store e -> describeStore e
        | LayoutRefusal.NoChange reason -> sprintf "nothing to change: %s" reason
        | LayoutRefusal.UnknownProposal id ->
            sprintf "no proposal '%s' of yours is open (proposals expire); propose the edit again" id
        | LayoutRefusal.StaleProposal id ->
            sprintf "the page changed since proposal '%s' was made; propose the edit again" id
        | LayoutRefusal.LayerMismatch(proposed, named) ->
            sprintf
                "the proposal resets the %s layer, and the save names the %s layer"
                (PageLayer.toToken proposed)
                (PageLayer.toToken named)
        | LayoutRefusal.Unavailable reason -> reason

/// JSON bridges between the tool arguments' portable value and the
/// canonical value the request codec writes.
module internal LayoutJson =

    let rec ofJson (v: JsonValue) : JVal option =
        match v with
        | JsonValue.JNull -> None
        | JsonValue.JBool b -> Some(JVal.JBool b)
        | JsonValue.JString s -> Some(JStr s)
        | JsonValue.JNumber n when n = Math.Floor n && n >= float Int32.MinValue && n <= float Int32.MaxValue ->
            Some(JInt(int n))
        | JsonValue.JNumber n -> Some(JFloat n)
        | JsonValue.JArray items -> Some(JArr(items |> List.choose ofJson))
        | JsonValue.JObject members ->
            Some(JObj(members |> List.choose (fun (k, m) -> ofJson m |> Option.map (fun j -> k, j))))

    let rec toJson (v: JVal) : JsonValue =
        match v with
        | JStr s -> JsonValue.JString s
        | JInt i -> JsonValue.JNumber(float i)
        | JFloat f -> JsonValue.JNumber f
        | JVal.JBool b -> JsonValue.JBool b
        | JArr items -> JsonValue.JArray(items |> List.map toJson)
        | JObj members -> JsonValue.JObject(members |> List.map (fun (k, m) -> k, toJson m))

    let canonical (v: JVal) : string = Canonical.write Rendering.CrossHost v

    let private probe (period: JsonValue) : JsonValue =
        JsonValue.JObject [
            "kind", JsonValue.JString "trend"
            "tableId", JsonValue.JString "probe"
            "metrics", JsonValue.JArray [ JsonValue.JString "probe" ]
            "period", period
        ]

    /// A period in the presentation codec's shape, read BY that codec, so a
    /// page default and a request's period are held to one rule.
    let periodOf (v: JsonValue) : Result<PresentationPeriod, string> =
        PresentationRequest.ofJsonValue (probe v)
        |> Result.map _.Period
        |> Result.mapError PresentationRequestError.describe

    /// A period as the presentation codec writes it.
    let periodJson (period: PresentationPeriod) : JsonValue =
        PresentationRequest.toJsonValue {
            Kind = PresentationKind.Trend
            TableId = "probe"
            Metrics = [ "probe" ]
            SubjectPrefix = []
            SubjectLevel = None
            Period = period
            CompareTo = None
            Title = None
            Annotations = []
        }
        |> JsonValue.tryField "period"
        |> Option.defaultValue JsonValue.JNull

    let strings (v: JsonValue) : Result<string list, string> =
        match v with
        | JsonValue.JArray items ->
            let texts = items |> List.choose JsonValue.asString

            if List.length texts = List.length items then
                Ok texts
            else
                Error "every item must be a string"
        | _ -> Error "it must be an array of strings"

/// The request sidecar's canonical JSON.
[<RequireQualifiedAccess>]
module PageRequestSidecar =

    /// The sidecar's format name. A later format replaces this one.
    [<Literal>]
    let Format = "toolup.page-requests/1"

    /// No requests and no defaults.
    let empty: PageRequestSidecar = {
        Requests = Map.empty
        Defaults = { Site = None; Period = None }
    }

    /// The sidecar as its canonical JSON value.
    let toJVal (sidecar: PageRequestSidecar) : Result<JVal, string> =
        let requests =
            sidecar.Requests
            |> Map.toList
            |> List.map (fun (key, request) ->
                PageRequest.toJVal request
                |> Result.map (fun v -> key, v)
                |> Result.mapError (fun e -> sprintf "request '%s': %s" key (PageRequestError.describe e)))

        match
            requests
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some e -> Error e
        | None ->
            let defaults =
                [
                    match sidecar.Defaults.Period with
                    | Some p ->
                        LayoutJson.periodJson p
                        |> LayoutJson.ofJson
                        |> Option.map (fun v -> "period", v)
                    | None -> None
                    match sidecar.Defaults.Site with
                    | Some site -> Some("site", JArr(site |> List.map JStr))
                    | None -> None
                ]
                |> List.choose id

            Ok(
                JObj [
                    if not (List.isEmpty defaults) then
                        "defaults", JObj defaults
                    "format", JStr Format
                    "requests",
                    JObj(
                        requests
                        |> List.choose (function
                            | Ok kv -> Some kv
                            | Error _ -> None)
                    )
                ]
            )

    /// The sidecar's canonical bytes.
    let toBytes (sidecar: PageRequestSidecar) : Result<byte[], string> =
        toJVal sidecar |> Result.map (LayoutJson.canonical >> Encoding.UTF8.GetBytes)

    /// The sidecar its bytes say, or why they are not one.
    let ofBytes (bytes: byte[]) : Result<PageRequestSidecar, string> =
        let text =
            try
                Ok(Encoding.UTF8.GetString bytes)
            with ex ->
                Error ex.Message

        let member' name members =
            members |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

        match text |> Result.bind (fun t -> Canonical.read t |> Result.mapError _.Message) with
        | Error e -> Error(sprintf "not JSON: %s" e)
        | Ok(JObj members) ->
            match member' "format" members with
            | Some(JStr Format) ->
                let requests =
                    match member' "requests" members with
                    | Some(JObj entries) ->
                        entries
                        |> List.fold
                            (fun acc (key, v) ->
                                acc
                                |> Result.bind (fun map ->
                                    PageRequest.ofJVal v
                                    |> Result.map (fun request -> Map.add key request map)
                                    |> Result.mapError (fun e ->
                                        sprintf "request '%s': %s" key (PageRequestError.describe e))))
                            (Ok Map.empty)
                    | None -> Ok Map.empty
                    | Some _ -> Error "'requests' is not an object"

                let defaults =
                    match member' "defaults" members with
                    | None -> Ok { Site = None; Period = None }
                    | Some(JObj d) ->
                        let site =
                            match member' "site" d with
                            | None -> Ok None
                            | Some v -> LayoutJson.strings (LayoutJson.toJson v) |> Result.map Some

                        let period =
                            match member' "period" d with
                            | None -> Ok None
                            | Some v -> LayoutJson.periodOf (LayoutJson.toJson v) |> Result.map Some

                        match site, period with
                        | Ok site, Ok period -> Ok { Site = site; Period = period }
                        | Error e, _
                        | _, Error e -> Error(sprintf "'defaults': %s" e)
                    | Some _ -> Error "'defaults' is not an object"

                match requests, defaults with
                | Ok requests, Ok defaults ->
                    Ok {
                        Requests = requests
                        Defaults = defaults
                    }
                | Error e, _
                | _, Error e -> Error e
            | Some(JStr other) -> Error(sprintf "format '%s' is not '%s'" other Format)
            | _ -> Error(sprintf "it names no format; this platform reads '%s'" Format)
        | Ok _ -> Error "not a JSON object"

/// Deriving and checking a page's read-set.
[<RequireQualifiedAccess>]
module PageReadSet =

    /// The read-set's version. The step to the language tier's demanded
    /// projection replaces this derivation.
    [<Literal>]
    let Version = "toolup.page-read-set/1"

    let private verbsOf (pipeline: string) : string list =
        match Canonical.read pipeline with
        | Ok(JArr steps) ->
            steps
            |> List.choose (function
                | JObj members ->
                    members
                    |> List.tryPick (function
                        | "$type", JStr verb -> Some verb
                        | _ -> None)
                | _ -> None)
        | _ -> []

    /// The read-set of a request map.
    let ofRequests (requests: Map<string, PageRequest>) : PageReadSet =
        let all = requests |> Map.toList |> List.map snd

        {
            Tables = all |> List.map PageRequest.tableId |> List.distinct |> List.sort
            Metrics =
                all
                |> List.collect (function
                    | PageRequest.Presentation(r, _) -> r.Metrics |> List.map (fun m -> r.TableId, m)
                    | PageRequest.Pipeline(r, _) -> r.Metrics |> List.map (fun m -> r.TableId, m))
                |> List.distinct
                |> List.sort
            Levels =
                all
                |> List.choose (function
                    | PageRequest.Presentation(r, _) -> r.SubjectLevel |> Option.map (fun l -> r.TableId, l)
                    | PageRequest.Pipeline _ -> None)
                |> List.distinct
                |> List.sort
            Verbs =
                all
                |> List.collect (function
                    | PageRequest.Pipeline(r, _) -> verbsOf r.Pipeline
                    | PageRequest.Presentation _ -> [])
                |> List.distinct
                |> List.sort
        }

    /// What the read-set names that the composition does not declare: a
    /// table the browse does not list, a metric its table does not carry, a
    /// subject level its hierarchy does not have. Empty when every name is
    /// declared.
    let check (deps: FactBrowseHandler.FactBrowseDeps) (readSet: PageReadSet) : string list = [
        for table in readSet.Tables do
            if (deps.Tables.TryGetTable table).IsNone then
                sprintf "the page reads fact table '%s', which this deployment does not declare" table
        for table, metric in readSet.Metrics do
            match deps.Tables.TryGetTable table with
            | Some definition when not (List.contains metric (FactTableDefinition.metrics definition)) ->
                sprintf "the page reads metric '%s' of fact table '%s', which the table does not carry" metric table
            | _ -> ()
        for table, level in readSet.Levels do
            match
                deps.Tables.TryGetTable table
                |> Option.bind (fun t -> deps.Registry |> Option.bind (fun r -> r.TryGetSubject t.Hierarchy))
            with
            | Some subject when (FactTableDefinition.levelDepth subject level).IsNone ->
                sprintf
                    "the page reads subject level '%s' of fact table '%s', which its hierarchy does not have"
                    level
                    table
            | _ -> ()
    ]

/// One edit a caller proposed and has not saved.
type LayoutProposal = {
    /// The proposal's id, which `layout_save` names.
    Id: string
    /// The page.
    Page: PageRef
    /// The tool that proposed it.
    Tool: string
    /// The ops and the request sidecar of the result, or the layer to reset.
    Change: LayoutChange
    /// The content hash of the folded page the proposal was made against.
    BaseHash: string
    /// When it was made.
    At: DateTime
}

/// What saving a proposal does.
and [<RequireQualifiedAccess>] LayoutChange =
    /// Append the ops to the layer the save names, with the sidecar of the result.
    | Append of ops: TreeOp<unit> list * sidecar: byte[]
    /// Empty one layer's stream.
    | Reset of layer: PageLayer

/// The proposals callers have made and not saved: process-local, bounded in
/// callers, in proposals per caller and in age, and never shared between
/// callers — a caller is its scope and principal.
type LayoutProposalStore(maxCallers: int, maxPerCaller: int, ttl: TimeSpan, clock: unit -> DateTime) =
    let sync = obj ()
    let callers = Dictionary<string, List<LayoutProposal>>()
    let order = Queue<string>()

    let live (now: DateTime) (p: LayoutProposal) = now - p.At <= ttl

    /// 256 callers, 32 proposals each, an hour old at most.
    new() = LayoutProposalStore(256, 32, TimeSpan.FromHours 1.0, fun () -> DateTime.UtcNow)

    /// The clock the store ages proposals by.
    member _.Now() = clock ()

    /// Keep `proposal` for `caller`, dropping the caller's oldest past the bound.
    member _.Put(caller: string, proposal: LayoutProposal) =
        lock sync (fun () ->
            let now = clock ()

            let list =
                match callers.TryGetValue caller with
                | true, list -> list
                | false, _ ->
                    while callers.Count >= maxCallers && order.Count > 0 do
                        callers.Remove(order.Dequeue()) |> ignore

                    let list = List<LayoutProposal>()
                    callers.[caller] <- list
                    order.Enqueue caller
                    list

            list.RemoveAll(fun p -> not (live now p)) |> ignore

            while list.Count >= maxPerCaller do
                list.RemoveAt 0

            list.Add proposal)

    /// The caller's live proposal `id`.
    member _.TryGet(caller: string, id: string) : LayoutProposal option =
        lock sync (fun () ->
            let now = clock ()

            match callers.TryGetValue caller with
            | true, list -> list |> Seq.tryFind (fun p -> p.Id = id && live now p)
            | false, _ -> None)

    /// Drop the caller's proposal `id`.
    member _.Remove(caller: string, id: string) =
        lock sync (fun () ->
            match callers.TryGetValue caller with
            | true, list -> list.RemoveAll(fun p -> p.Id = id) |> ignore
            | false, _ -> ())

/// Which layout tool.
[<RequireQualifiedAccess>]
type LayoutTool =
    | AddBlock
    | RemoveBlock
    | MoveBlock
    | SetRequest
    | Retitle
    | SetSection
    | SetDefault
    | Reset
    | Save

/// What one call of a layout tool works with: the caller's scope and access
/// context (from the request, as the platform resolved them), the fact
/// browse's dependencies, the caller's page request resolver, the page store
/// and the proposals.
type LayoutCaller = {
    Scope: ResolvedScope
    Access: AccessContext
    Deps: FactBrowseHandler.FactBrowseDeps
    Resolver: PageRequestResolver
    Store: IPageDocumentStore
    Proposals: LayoutProposalStore
}

/// What a deployment composing the layout tools registers: the page store,
/// the resolvers and the proposals, resolved once in a registration factory.
type LayoutToolsRuntime = {
    Store: IPageDocumentStore
    Resolvers: PageRequestResolvers
    Proposals: LayoutProposalStore
}

/// The page tree, read.
module internal LayoutTree =

    let children (n: Node<unit>) : Node<unit> list =
        Introspect.getChildren n.Kind |> Option.defaultValue []

    let isContainer (n: Node<unit>) = (Introspect.getChildren n.Kind).IsSome

    let rec all (n: Node<unit>) : Node<unit> list =
        n :: (NodeChildren.children NodeChildren.Reach.kindHeld n |> List.collect all)

    let tryFind (id: string) (tree: Node<unit>) =
        all tree |> List.tryFind (fun n -> n.Id = id)

    let parentOf (id: string) (tree: Node<unit>) =
        all tree
        |> List.tryFind (fun p -> children p |> List.exists (fun c -> c.Id = id))

    let queryKey (binding: Binding<'T>) : string option =
        match binding with
        | Binding.Query(name, _, _) -> Some name
        | _ -> None

    /// A chart's or grid's kind name and the request key it draws, `None`
    /// when it draws anything else.
    let dataSource (n: Node<unit>) : (string * string option) option =
        match n.Kind with
        | NodeKind.Chart spec -> Some("Chart", queryKey spec.Source)
        | NodeKind.DataGrid spec ->
            Some(
                "DataGrid",
                (if spec.StaticRows.IsSome then
                     None
                 else
                     queryKey spec.Source)
            )
        | _ -> None

    /// Every request key the tree's data-bearing nodes name.
    let referenced (tree: Node<unit>) : string list =
        all tree
        |> List.choose (fun n ->
            match n.Kind with
            | NodeKind.Chart spec -> queryKey spec.Source
            | NodeKind.DataGrid spec -> queryKey spec.Source
            | NodeKind.Metric spec -> queryKey spec.Value
            | NodeKind.Sparkline spec -> queryKey spec.Source
            | _ -> None)
        |> List.distinct

    let slug (text: string) : string =
        let chars =
            text.ToLowerInvariant()
            |> Seq.map (fun c ->
                if (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') then
                    c
                else
                    '-')
            |> Seq.toArray

        let s = String(chars)

        let collapsed =
            s.Split([| '-' |], StringSplitOptions.RemoveEmptyEntries) |> String.concat "-"

        if collapsed = "" then
            "block"
        elif collapsed.Length > 40 then
            collapsed.Substring(0, 40).TrimEnd('-')
        else
            collapsed

    /// `base'`, or the first `base'-n` the tree does not hold.
    let uniqueId (base': string) (tree: Node<unit>) : string =
        let taken = all tree |> List.map _.Id |> Set.ofList

        if not (taken.Contains base') then
            base'
        else
            Seq.initInfinite (fun i -> sprintf "%s-%d" base' (i + 2))
            |> Seq.find (fun id -> not (taken.Contains id))

/// The layout tools: definitions, executors and the save.
[<RequireQualifiedAccess>]
module LayoutTools =

    /// The `Kind` a proposal's preview block carries in its provenance.
    [<Literal>]
    let ProposedKind = "proposed"

    /// Each tool's name.
    let name (tool: LayoutTool) : string =
        match tool with
        | LayoutTool.AddBlock -> "layout_add_block"
        | LayoutTool.RemoveBlock -> "layout_remove_block"
        | LayoutTool.MoveBlock -> "layout_move_block"
        | LayoutTool.SetRequest -> "layout_set_request"
        | LayoutTool.Retitle -> "layout_retitle"
        | LayoutTool.SetSection -> "layout_set_section"
        | LayoutTool.SetDefault -> "layout_set_default"
        | LayoutTool.Reset -> "layout_reset"
        | LayoutTool.Save -> "layout_save"

    /// Every tool, the eight that propose and then the save.
    let all: LayoutTool list = [
        LayoutTool.AddBlock
        LayoutTool.RemoveBlock
        LayoutTool.MoveBlock
        LayoutTool.SetRequest
        LayoutTool.Retitle
        LayoutTool.SetSection
        LayoutTool.SetDefault
        LayoutTool.Reset
        LayoutTool.Save
    ]

    // ── Definitions ───────────────────────────────────────────────

    let private pageParameters = [
        ToolSchema.parameter "module" "The module that declares the page." true ToolSchema.text
        ToolSchema.parameter "page" "The page's name within its module." true ToolSchema.text
    ]

    let private periodSchema =
        ToolSchema.objectOf [
            ToolSchema.field
                "day"
                "The last day covered, yyyy-MM-dd. Give day (with rangeDays), or from and to."
                false
                ToolSchema.text
            ToolSchema.field "rangeDays" "The days ending on day, inclusive. Defaults to 1." false ToolSchema.integer
            ToolSchema.field "from" "The first day covered, yyyy-MM-dd (inclusive)." false ToolSchema.text
            ToolSchema.field "to" "The day after the last day covered, yyyy-MM-dd (exclusive)." false ToolSchema.text
        ]

    let private compareToValues = [ "previousDelivery" ]

    let private requestSchema =
        ToolSchema.objectOf [
            ToolSchema.field
                "kind"
                "presentation: a trend, breakdown, comparison or table of one fact table. pipeline: a slice of one fact table and a Fuaran.Compute pipeline over it."
                true
                (ToolSchema.enumOf [ PageRequest.PresentationKind; PageRequest.PipelineKind ])
            ToolSchema.field
                "payload"
                "For a presentation: kind (trend, breakdown, comparison, table), tableId, metrics, subjectPrefix, subjectLevel, period, compareTo, title. For a pipeline: table, period_from, period_to, path_prefix, metrics, pipeline."
                true
                (ToolSchema.objectOf [
                    ToolSchema.field
                        "kind"
                        "A presentation's kind."
                        false
                        (ToolSchema.enumOf [ "trend"; "breakdown"; "comparison"; "table" ])
                    ToolSchema.field "tableId" "A presentation's fact table." false ToolSchema.text
                    ToolSchema.field "metrics" "The metric columns read." false (ToolSchema.arrayOf ToolSchema.text)
                    ToolSchema.field
                        "subjectPrefix"
                        "A presentation's member ids from the hierarchy root narrowing the subjects."
                        false
                        (ToolSchema.arrayOf ToolSchema.text)
                    ToolSchema.field "subjectLevel" "A presentation's subject level." false ToolSchema.text
                    ToolSchema.field "period" "A presentation's whole UTC days." false periodSchema
                    ToolSchema.field "compareTo" "A comparison's baseline." false (ToolSchema.enumOf compareToValues)
                    ToolSchema.field "title" "A presentation's title." false ToolSchema.text
                    ToolSchema.field "table" "A pipeline's fact table." false ToolSchema.text
                    ToolSchema.field "period_from" "A pipeline's first instant (inclusive)." false ToolSchema.text
                    ToolSchema.field "period_to" "A pipeline's last instant (exclusive)." false ToolSchema.text
                    ToolSchema.field
                        "path_prefix"
                        "A pipeline's subject path prefix."
                        false
                        (ToolSchema.arrayOf ToolSchema.text)
                    ToolSchema.field
                        "pipeline"
                        "A pipeline's steps, as a JSON array of $type-tagged Fuaran.Compute steps."
                        false
                        (ToolSchema.arrayOf (ToolSchema.objectOf []))
                ])
            ToolSchema.field
                "dependsOn"
                "Keys of the page's requests whose outputs this one reads."
                false
                (ToolSchema.arrayOf ToolSchema.text)
        ]

    let private placementParameters (what: string) = [
        ToolSchema.parameter
            "into"
            (sprintf "A section (or other container) id to %s into, last." what)
            false
            ToolSchema.text
        ToolSchema.parameter
            "before"
            (sprintf "A node id to %s before, in that node's container." what)
            false
            ToolSchema.text
        ToolSchema.parameter
            "after"
            (sprintf "A node id to %s after, in that node's container." what)
            false
            ToolSchema.text
    ]

    let private layerSchema =
        ToolSchema.enumOf [
            PageLayer.toToken PageLayer.Platform
            PageLayer.toToken PageLayer.Team
            PageLayer.toToken PageLayer.Individual
        ]

    let private parameters (tool: LayoutTool) : ToolParameterSchema list =
        match tool with
        | LayoutTool.AddBlock ->
            pageParameters
            @ [
                ToolSchema.parameter "request" "The request the block draws. Never rows." true requestSchema
                ToolSchema.parameter
                    "block"
                    "chart or table. Omit to take the request's own: a table-kind or pipeline request is a table, any other a chart."
                    false
                    (ToolSchema.enumOf [ "chart"; "table" ])
                ToolSchema.parameter
                    "key"
                    "A short name for the request (letters, digits, '-', '_', '.'). Omit to derive one."
                    false
                    ToolSchema.text
                ToolSchema.parameter "title" "The block's heading. Omit to derive one." false ToolSchema.text
            ]
            @ placementParameters "add the block"
        | LayoutTool.RemoveBlock ->
            pageParameters
            @ [
                ToolSchema.parameter "block" "The id of the block to remove." true ToolSchema.text
            ]
        | LayoutTool.MoveBlock ->
            pageParameters
            @ [
                ToolSchema.parameter "block" "The id of the block to move." true ToolSchema.text
            ]
            @ placementParameters "move it"
        | LayoutTool.SetRequest ->
            pageParameters
            @ [
                ToolSchema.parameter "key" "The key of the page request to change." true ToolSchema.text
                ToolSchema.parameter "period" "The new period (range)." false periodSchema
                ToolSchema.parameter
                    "compareTo"
                    "A comparison's new baseline."
                    false
                    (ToolSchema.enumOf compareToValues)
                ToolSchema.parameter "metrics" "The new metric columns." false (ToolSchema.arrayOf ToolSchema.text)
                ToolSchema.parameter "subjectLevel" "The new subject level." false ToolSchema.text
                ToolSchema.parameter
                    "site"
                    "The new subject path prefix (member ids from the hierarchy root); [] for every subject."
                    false
                    (ToolSchema.arrayOf ToolSchema.text)
            ]
        | LayoutTool.Retitle ->
            pageParameters
            @ [
                ToolSchema.parameter "block" "The id of the block, section, chart or heading." true ToolSchema.text
                ToolSchema.parameter "title" "The new title." true ToolSchema.text
            ]
        | LayoutTool.SetSection ->
            pageParameters
            @ [
                ToolSchema.parameter
                    "action"
                    "add: a new, empty section. rename: a section's heading."
                    true
                    (ToolSchema.enumOf [ "add"; "rename" ])
                ToolSchema.parameter "section" "rename: the section's id." false ToolSchema.text
                ToolSchema.parameter "title" "The section's heading." true ToolSchema.text
            ]
            @ placementParameters "add the section"
        | LayoutTool.SetDefault ->
            pageParameters
            @ [
                ToolSchema.parameter
                    "site"
                    "The page's standing subject path prefix; [] for every subject."
                    false
                    (ToolSchema.arrayOf ToolSchema.text)
                ToolSchema.parameter "period" "The page's standing period." false periodSchema
            ]
        | LayoutTool.Reset ->
            pageParameters
            @ [
                ToolSchema.parameter "layer" "The layer whose edits to drop." true layerSchema
            ]
        | LayoutTool.Save -> [
            ToolSchema.parameter "proposal" "The proposal id a layout_* tool answered." true ToolSchema.text
            ToolSchema.parameter
                "layer"
                "The layer to save to: team (a team owner or admin), platform (a platform admin), individual (when enabled)."
                true
                layerSchema
          ]

    let private proposes =
        " The edit is PROPOSED, never saved: the user sees a preview of the page, and the result names a proposal id. To keep it, call layout_save with that id and a layer. Every chart and table names a request; the reader's gate supplies its rows, so never state a number. A refused edit names what to repair."

    let private description (tool: LayoutTool) : string =
        match tool with
        | LayoutTool.AddBlock ->
            "Add a chart or table block to a stored page, drawing a request (never rows), placed into a section or before or after a node."
            + proposes
        | LayoutTool.RemoveBlock -> "Remove a block from a stored page." + proposes
        | LayoutTool.MoveBlock ->
            "Move a block of a stored page before or after a node, or into a section."
            + proposes
        | LayoutTool.SetRequest ->
            "Change one of a stored page's requests: its period, comparison baseline, metrics, subject level or site. Every block drawing it follows."
            + proposes
        | LayoutTool.Retitle -> "Retitle a block, section or chart of a stored page." + proposes
        | LayoutTool.SetSection -> "Add a section to a stored page, or rename one." + proposes
        | LayoutTool.SetDefault ->
            "Set a stored page's standing site and range: every request of the page takes them, and a block added later starts from them."
            + proposes
        | LayoutTool.Reset ->
            "Drop one layer's edits of a stored page, so it shows the layer below. Saving it needs that layer's grant."
            + proposes
        | LayoutTool.Save ->
            "Save a proposal a layout_* tool made to one layer of the page. The layer's grant is checked and the save is audited; a refusal names why."

    /// A tool's declaration: server-resident, on both surfaces. The eight
    /// proposing tools read the page and the facts its requests name; the
    /// save writes the page store.
    let definition (tool: LayoutTool) : AIToolDefinition = {
        Name = name tool
        Description = description tool
        Parameters = parameters tool
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects =
            match tool with
            | LayoutTool.Save -> ToolEffectDeclaration.declare [ ToolEffect.ReadContent; ToolEffect.WriteState "pages" ]
            | _ -> ToolEffectDeclaration.declare [ ToolEffect.ReadFacts; ToolEffect.ReadContent ]
    }

    // ── Arguments ─────────────────────────────────────────────────

    let private allowed (tool: LayoutTool) : Set<string> =
        parameters tool |> List.map _.Name |> Set.ofList

    /// Members that would carry rows into a page. Refused by name wherever
    /// they appear in the arguments, before anything else is read.
    let private rowMembers =
        set [ "rows"; "data"; "staticRows"; "source"; "values"; "table" ]

    let rec private carriesRows (path: string) (v: JsonValue) : string option =
        match v with
        | JsonValue.JObject members ->
            members
            |> List.tryPick (fun (k, m) ->
                let here = if path = "" then k else path + "." + k

                if rowMembers.Contains k && not (path.EndsWith "payload" && k = "table") then
                    match m with
                    | JsonValue.JArray _
                    | JsonValue.JObject _ -> Some here
                    | _ -> carriesRows here m
                else
                    carriesRows here m)
        | _ -> None

    type private Args = (string * JsonValue) list

    let private arg (name: string) (args: Args) =
        args |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

    let private text (name: string) (args: Args) : Result<string option, LayoutRefusal> =
        match arg name args with
        | None
        | Some JsonValue.JNull -> Ok None
        | Some(JsonValue.JString s) when not (String.IsNullOrWhiteSpace s) -> Ok(Some(s.Trim()))
        | Some _ -> Error(LayoutRefusal.Arguments(sprintf "'%s' must be a non-empty string" name))

    let private required (name: string) (args: Args) : Result<string, LayoutRefusal> =
        text name args
        |> Result.bind (function
            | Some s -> Ok s
            | None -> Error(LayoutRefusal.Arguments(sprintf "'%s' is required" name)))

    let private stringList (name: string) (args: Args) : Result<string list option, LayoutRefusal> =
        match arg name args with
        | None
        | Some JsonValue.JNull -> Ok None
        | Some v ->
            LayoutJson.strings v
            |> Result.map Some
            |> Result.mapError (fun e -> LayoutRefusal.Arguments(sprintf "'%s': %s" name e))

    let private period (name: string) (args: Args) : Result<PresentationPeriod option, LayoutRefusal> =
        match arg name args with
        | None
        | Some JsonValue.JNull -> Ok None
        | Some v ->
            LayoutJson.periodOf v
            |> Result.map Some
            |> Result.mapError (fun e -> LayoutRefusal.Arguments(sprintf "'%s': %s" name e))

    let private layerOf (token: string) : Result<PageLayer, LayoutRefusal> =
        match token.ToLowerInvariant() with
        | "platform" -> Ok PageLayer.Platform
        | "team" -> Ok PageLayer.Team
        | "individual" -> Ok PageLayer.Individual
        | other ->
            Error(LayoutRefusal.Arguments(sprintf "'layer' is '%s'; it is one of platform, team, individual" other))

    /// The arguments as an object, refusing rows, unknown members and
    /// malformed JSON by name.
    let private argsOf (tool: LayoutTool) (argsJson: string) : Result<Args, LayoutRefusal> =
        match
            (if String.IsNullOrWhiteSpace argsJson then
                 None
             else
                 JsonHost.parse argsJson)
        with
        | Some(JsonValue.JObject members as v) ->
            match carriesRows "" v with
            | Some path -> Error(LayoutRefusal.InlineRows(sprintf "the argument '%s'" path))
            | None ->
                match members |> List.tryFind (fun (k, _) -> not ((allowed tool).Contains k)) with
                | Some(k, _) ->
                    Error(
                        LayoutRefusal.Arguments(
                            sprintf
                                "'%s' is not an argument of %s (its arguments: %s)"
                                k
                                (name tool)
                                (allowed tool |> Set.toList |> String.concat ", ")
                        )
                    )
                | None -> Ok members
        | _ -> Error(LayoutRefusal.Arguments "the arguments are not a JSON object")

    let private pageOf (args: Args) : Result<PageRef, LayoutRefusal> =
        match required "module" args, required "page" args with
        | Ok m, Ok p -> Ok { PageRef.Module = m; Page = p }
        | Error e, _
        | _, Error e -> Error e

    // ── Requests and keys ─────────────────────────────────────────

    let private keyChars (s: string) =
        s.Length > 0
        && s.Length <= 64
        && s
           |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = '.')

    /// A key's stem: the key up to its request hash.
    let private stemOf (key: string) =
        match key.IndexOf '~' with
        | -1 -> key
        | i -> key.Substring(0, i)

    /// The key a request is held under: its stem and the first eight hex
    /// digits of its canonical text's SHA-256. One key names one request.
    let mintKey (stem: string) (request: PageRequest) : Result<string, LayoutRefusal> =
        if not (keyChars stem) then
            Error(LayoutRefusal.Arguments(sprintf "the key '%s' must be 1 to 64 letters, digits, '-', '_' or '.'" stem))
        else
            PageRequest.toJson request
            |> Result.map (fun text -> stem + "~" + (PageLayers.digest text).Substring(0, 8))
            |> Result.mapError (fun e -> LayoutRefusal.Arguments(PageRequestError.describe e))

    let private requestOf (v: JsonValue) : Result<PageRequest, LayoutRefusal> =
        match LayoutJson.ofJson v with
        | Some j ->
            PageRequest.ofJVal j
            |> Result.mapError (fun e -> LayoutRefusal.Arguments(sprintf "'request': %s" (PageRequestError.describe e)))
        | None -> Error(LayoutRefusal.Arguments "'request' is required")

    /// The requests reachable from the tree's bindings through `dependsOn`.
    let private reachable (tree: Node<unit>) (requests: Map<string, PageRequest>) : Map<string, PageRequest> =
        let rec close (seen: Set<string>) (frontier: string list) =
            match frontier with
            | [] -> seen
            | key :: rest when seen.Contains key -> close seen rest
            | key :: rest ->
                let deps =
                    requests
                    |> Map.tryFind key
                    |> Option.map PageRequest.dependsOn
                    |> Option.defaultValue []

                close (seen.Add key) (deps @ rest)

        let keep = close Set.empty (LayoutTree.referenced tree)
        requests |> Map.filter (fun key _ -> keep.Contains key)

    /// Apply a page's standing defaults (or a change) to a request: the site
    /// and the period, through the request's own codec.
    let private withSiteAndPeriod
        (site: string list option)
        (period: PresentationPeriod option)
        (request: PageRequest)
        : Result<PageRequest, LayoutRefusal> =
        match request with
        | PageRequest.Presentation(r, deps) ->
            let r' = {
                r with
                    SubjectPrefix = defaultArg site r.SubjectPrefix
                    Period = defaultArg period r.Period
            }

            PresentationRequest.validate r'
            |> Result.map (fun r -> PageRequest.Presentation(r, deps))
            |> Result.mapError (fun e -> LayoutRefusal.Arguments(PresentationRequestError.describe e))
        | PageRequest.Pipeline(r, deps) ->
            let from, until =
                match period with
                | Some p ->
                    PresentationRequest.range {
                        Kind = PresentationKind.Trend
                        TableId = r.TableId
                        Metrics = [ "range" ]
                        SubjectPrefix = []
                        SubjectLevel = None
                        Period = p
                        CompareTo = None
                        Title = None
                        Annotations = []
                    }
                | None -> r.PeriodFrom, r.PeriodTo

            Ok(
                PageRequest.Pipeline(
                    {
                        r with
                            PathPrefix = defaultArg site r.PathPrefix
                            PeriodFrom = from
                            PeriodTo = until
                    },
                    deps
                )
            )

    // ── Nodes ─────────────────────────────────────────────────────

    let private query (key: string) : Binding<Map<string, obj> seq> =
        Binding.Query(key, (fun (o: obj) -> unbox<Map<string, obj> seq> o), None)

    let private numeric (t: ColumnType) =
        match t with
        | IntType
        | FloatType
        | DecimalType -> true
        | _ -> false

    /// A chart bound to `key`, its axis and series named by the request's
    /// resolved columns: the first column is the axis, the rest the series.
    let private chartSpec
        (key: string)
        (request: PresentationRequest)
        (schema: Field list)
        : Result<ChartSpec<unit>, string> =
        match schema with
        | x :: (_ :: _ as series) ->
            Ok {
                Defaults.chart with
                    Kind =
                        match request.Kind with
                        | PresentationKind.Trend -> ChartKind.Line
                        | _ -> ChartKind.Bar
                    Source = query key
                    XField = x.Name
                    YFields = series |> List.map _.Name
                    XScale =
                        if request.Kind = PresentationKind.Trend && x.Type = DateType then
                            Some ChartXScale.Temporal
                        else
                            None
            }
        | _ -> Error "its resolution has no value column to draw"

    let private gridSpec (key: string) (schema: Field list) : DataGridSpec<unit> = {
        Defaults.dataGrid with
            Source = query key
            Columns =
                schema
                |> List.map (fun f -> {
                    Defaults.columnErased with
                        Label = f.Name
                        Field = Some f.Name
                        Kind =
                            if numeric f.Type then
                                CellKindErased.Numeric
                            else
                                CellKindErased.Text
                })
    }

    let private leaf (id: string) (kind: NodeKind<unit>) : Node<unit> =
        match kind with
        | NodeKind.Chart spec -> Fuaran.chart id spec
        | _ -> {
            Fuaran.chart id Defaults.chart with
                Kind = kind
                Accessibility = Defaults.Accessibility.table
          }

    // ── One call's context ────────────────────────────────────────

    /// The caller's page, as one call reads it: the fold, the request
    /// sidecar of its highest layer, and the resolutions made so far.
    type private Session = {
        Caller: LayoutCaller
        Page: PageRef
        Folded: FoldedPage
        Sidecar: PageRequestSidecar
        Resolved: Dictionary<string, Result<PageResolution * Table, LayoutRefusal>>
    }

    let private open' (caller: LayoutCaller) (page: PageRef) : Async<Result<Session, LayoutRefusal>> = async {
        match! caller.Store.ReadFolded(caller.Scope, caller.Access, page) with
        | Error e -> return Error(LayoutRefusal.Store e)
        | Ok folded ->
            let sidecar =
                match folded.Layers |> List.rev |> List.tryPick _.Sidecar with
                | None -> Ok PageRequestSidecar.empty
                | Some bytes -> PageRequestSidecar.ofBytes bytes |> Result.mapError LayoutRefusal.Sidecar

            return
                sidecar
                |> Result.map (fun sidecar -> {
                    Caller = caller
                    Page = page
                    Folded = folded
                    Sidecar = sidecar
                    Resolved = Dictionary()
                })
    }

    /// A request resolved under the caller's gate, once per call.
    let private resolve
        (session: Session)
        (key: string)
        (request: PageRequest)
        : Async<Result<PageResolution * Table, LayoutRefusal>> =
        async {
            match session.Resolved.TryGetValue key with
            | true, known -> return known
            | false, _ ->
                let! resolved = session.Caller.Resolver.Resolve request

                let result =
                    match resolved with
                    | Error refusal -> Error(LayoutRefusal.Unresolved(key, refusal))
                    | Ok resolution ->
                        match PageResolution.table resolution with
                        | Ok table -> Ok(resolution, table)
                        | Error e -> Error(LayoutRefusal.Unresolved(key, PageRefusal.Defect e))

                session.Resolved.[key] <- result
                return result
        }

    // ── Edits ─────────────────────────────────────────────────────

    /// What a tool proposes, before validation: the ops, the request map of
    /// the result, its defaults, and what it did, in words.
    type private Edit = {
        Ops: TreeOp<unit> list
        Requests: Map<string, PageRequest>
        Defaults: PageDefaults
        Said: string list
    }

    type private Placement =
        | Into of string
        | Before of string
        | After of string
        | AtEnd

    let private placementOf (args: Args) : Result<Placement, LayoutRefusal> =
        match text "into" args, text "before" args, text "after" args with
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error e
        | Ok None, Ok None, Ok None -> Ok AtEnd
        | Ok(Some id), Ok None, Ok None -> Ok(Into id)
        | Ok None, Ok(Some id), Ok None -> Ok(Before id)
        | Ok None, Ok None, Ok(Some id) -> Ok(After id)
        | _ -> Error(LayoutRefusal.Arguments "give at most one of 'into', 'before' and 'after'")

    /// The container a placement lands in, and the order of its children
    /// once `nodeId` sits there.
    let private target
        (tree: Node<unit>)
        (nodeId: string)
        (placement: Placement)
        : Result<string * string list, LayoutRefusal> =
        let orderIn (parent: Node<unit>) (anchor: (string * bool) option) =
            let others =
                LayoutTree.children parent
                |> List.map _.Id
                |> List.filter (fun id -> id <> nodeId)

            match anchor with
            | None -> Ok(parent.Id, others @ [ nodeId ])
            | Some(anchorId, before) ->
                let i = others |> List.findIndex (fun id -> id = anchorId)
                let at = if before then i else i + 1
                Ok(parent.Id, List.take at others @ [ nodeId ] @ List.skip at others)

        let besides (anchorId: string) (before: bool) =
            if anchorId = nodeId then
                Error(LayoutRefusal.NotEditable(nodeId, "it cannot be placed beside itself"))
            else
                match LayoutTree.parentOf anchorId tree with
                | Some parent -> orderIn parent (Some(anchorId, before))
                | None ->
                    match LayoutTree.tryFind anchorId tree with
                    | Some _ -> Error(LayoutRefusal.NotEditable(anchorId, "the page root has no siblings"))
                    | None -> Error(LayoutRefusal.UnknownNode anchorId)

        match placement with
        | AtEnd -> orderIn tree None
        | Into id ->
            match LayoutTree.tryFind id tree with
            | None -> Error(LayoutRefusal.UnknownNode id)
            | Some container when LayoutTree.isContainer container -> orderIn container None
            | Some _ -> Error(LayoutRefusal.NotEditable(id, "it holds no blocks"))
        | Before id -> besides id true
        | After id -> besides id false

    /// The ops that put `nodeId` (already in `parentId`'s children after
    /// `structural`) into `order`, when the order differs.
    let private reorder (tree: Node<unit>) (parentId: string) (order: string list) (structural: TreeOp<unit> list) =
        let after =
            structural
            |> List.fold (fun t op -> t |> Result.bind (Apply.apply op)) (Ok tree)

        match after |> Result.toOption |> Option.bind (LayoutTree.tryFind parentId) with
        | Some parent when (LayoutTree.children parent |> List.map _.Id) <> order ->
            structural
            @ [ TreeOp.ReorderChildren(NodeId parentId, order |> List.map NodeId) ]
        | _ -> structural

    let private block (session: Session) (args: Args) : Result<Node<unit>, LayoutRefusal> =
        required "block" args
        |> Result.bind (fun id ->
            match LayoutTree.tryFind id session.Folded.Tree with
            | None -> Error(LayoutRefusal.UnknownNode id)
            | Some n when n.Id = session.Folded.Tree.Id -> Error(LayoutRefusal.NotEditable(id, "it is the page root"))
            | Some n -> Ok n)

    let private unchanged (session: Session) (ops: TreeOp<unit> list) (said: string list) : Edit = {
        Ops = ops
        Requests = session.Sidecar.Requests
        Defaults = session.Sidecar.Defaults
        Said = said
    }

    let private addBlock (session: Session) (args: Args) : Async<Result<Edit, LayoutRefusal>> = async {
        let tree = session.Folded.Tree

        let parsed =
            match arg "request" args with
            | None -> Error(LayoutRefusal.Arguments "'request' is required")
            | Some v ->
                requestOf v
                |> Result.bind (fun request ->
                    // A request that leaves the site or the range to the
                    // page takes the page's standing defaults.
                    let defaults = session.Sidecar.Defaults

                    let request =
                        match request, defaults.Site with
                        | PageRequest.Presentation(r, deps), Some site when List.isEmpty r.SubjectPrefix ->
                            PageRequest.Presentation({ r with SubjectPrefix = site }, deps)
                        | _ -> request

                    match text "key" args, text "title" args, text "block" args, placementOf args with
                    | Error e, _, _, _
                    | _, Error e, _, _
                    | _, _, Error e, _
                    | _, _, _, Error e -> Error e
                    | Ok key, Ok title, Ok shape, Ok placement ->
                        let stem =
                            key
                            |> Option.defaultWith (fun () ->
                                LayoutTree.slug (PageRequest.tableId request + "-" + PageRequest.kind request))

                        mintKey stem request
                        |> Result.map (fun minted -> request, minted, title, shape, placement))

        match parsed with
        | Error e -> return Error e
        | Ok(request, key, title, shape, placement) ->
            let unknownDeps =
                PageRequest.dependsOn request
                |> List.filter (fun k -> not (session.Sidecar.Requests.ContainsKey k))

            if not (List.isEmpty unknownDeps) then
                return
                    Error(
                        LayoutRefusal.Arguments(
                            sprintf
                                "'dependsOn' names %s, which the page does not hold"
                                (String.concat ", " unknownDeps)
                        )
                    )
            else
                match! resolve session key request with
                | Error e -> return Error e
                | Ok(_, table) ->
                    let asTable =
                        match shape, request with
                        | Some "table", _ -> Ok true
                        | Some "chart", PageRequest.Pipeline _ ->
                            Error(LayoutRefusal.Arguments "a pipeline request is drawn as a table")
                        | Some "chart", PageRequest.Presentation(r, _) when r.Kind = PresentationKind.Table ->
                            Error(LayoutRefusal.Arguments "a table-kind presentation request is drawn as a table")
                        | Some "chart", _ -> Ok false
                        | Some other, _ ->
                            Error(LayoutRefusal.Arguments(sprintf "'block' is '%s'; it is chart or table" other))
                        | None, PageRequest.Presentation(r, _) -> Ok(r.Kind = PresentationKind.Table)
                        | None, PageRequest.Pipeline _ -> Ok true

                    let heading =
                        title
                        |> Option.defaultWith (fun () ->
                            match request with
                            | PageRequest.Presentation(r, _) -> PresentationRequest.title r
                            | PageRequest.Pipeline(r, _) -> sprintf "%s: %s" r.TableId (String.concat ", " r.Metrics))

                    let blockId = LayoutTree.uniqueId (LayoutTree.slug (stemOf key)) tree

                    let data =
                        asTable
                        |> Result.bind (fun asTable ->
                            let dataId = blockId + (if asTable then "-table" else "-chart")

                            if asTable then
                                Ok(leaf dataId (NodeKind.DataGrid(gridSpec key table.Schema)))
                            else
                                match request with
                                | PageRequest.Presentation(r, _) ->
                                    chartSpec key r table.Schema
                                    |> Result.map (fun spec -> leaf dataId (NodeKind.Chart spec))
                                    |> Result.mapError (fun e -> LayoutRefusal.NotEditable(dataId, e))
                                | PageRequest.Pipeline _ ->
                                    Error(LayoutRefusal.Arguments "a pipeline request is drawn as a table"))

                    match data with
                    | Error e -> return Error e
                    | Ok data ->
                        let card =
                            Fuaran.box blockId {
                                Defaults.box with
                                    Role = BoxRole.Card
                                    Heading = Some(TextSource.Literal heading)
                                    Children = [ data ]
                            }

                        match target tree blockId placement with
                        | Error e -> return Error e
                        | Ok(parentId, order) ->
                            return
                                Ok {
                                    Ops = reorder tree parentId order [ TreeOp.InsertChild(NodeId parentId, card) ]
                                    Requests = session.Sidecar.Requests |> Map.add key request
                                    Defaults = session.Sidecar.Defaults
                                    Said = [
                                        sprintf
                                            "adds block '%s' (%s) into '%s', drawing request '%s'"
                                            blockId
                                            (if data.Kind.IsDataGrid then "table" else "chart")
                                            parentId
                                            key
                                    ]
                                }
    }

    let private removeBlock (session: Session) (args: Args) : Result<Edit, LayoutRefusal> =
        block session args
        |> Result.map (fun n -> unchanged session [ TreeOp.RemoveNode(NodeId n.Id) ] [ sprintf "removes '%s'" n.Id ])

    let private moveBlock (session: Session) (args: Args) : Result<Edit, LayoutRefusal> =
        let tree = session.Folded.Tree

        match block session args, placementOf args with
        | Error e, _
        | _, Error e -> Error e
        | Ok _, Ok AtEnd -> Error(LayoutRefusal.Arguments "give one of 'into', 'before' and 'after'")
        | Ok n, Ok placement ->
            target tree n.Id placement
            |> Result.map (fun (parentId, order) ->
                let current = LayoutTree.parentOf n.Id tree |> Option.map _.Id

                let structural =
                    if current = Some parentId then
                        []
                    else
                        [ TreeOp.MoveNode(NodeId n.Id, NodeId parentId) ]

                unchanged session (reorder tree parentId order structural) [
                    sprintf "moves '%s' into '%s'" n.Id parentId
                ])

    /// Rebind every node drawing `oldKey` to `newKey`, re-reading the chart
    /// fields and grid columns from the new request's resolution.
    let private rebind
        (session: Session)
        (tree: Node<unit>)
        (oldKey: string)
        (newKey: string)
        (request: PageRequest)
        : Async<Result<TreeOp<unit> list, LayoutRefusal>> =
        async {
            let bound =
                LayoutTree.all tree
                |> List.filter (fun n ->
                    match LayoutTree.dataSource n with
                    | Some(_, Some key) -> key = oldKey
                    | _ -> false)

            if List.isEmpty bound then
                return Ok []
            else
                match! resolve session newKey request with
                | Error e -> return Error e
                | Ok(_, table) ->
                    let opsFor (n: Node<unit>) : Result<TreeOp<unit> list, LayoutRefusal> =
                        match n.Kind, request with
                        | NodeKind.Chart spec, PageRequest.Presentation(r, _) ->
                            chartSpec newKey r table.Schema
                            |> Result.mapError (fun e -> LayoutRefusal.NotEditable(n.Id, e))
                            |> Result.map (fun fresh -> [
                                TreeOp.ReplaceBinding(
                                    NodeId n.Id,
                                    "Source",
                                    Binding.Query(newKey, (fun (o: obj) -> o), None)
                                )
                                if fresh.XField <> spec.XField then
                                    TreeOp.UpdateProp(NodeId n.Id, "XField", PropValue.Wire(JStr fresh.XField))
                                if fresh.YFields <> spec.YFields then
                                    TreeOp.UpdateProp(
                                        NodeId n.Id,
                                        "YFields",
                                        PropValue.Wire(JArr(fresh.YFields |> List.map JStr))
                                    )
                            ])
                        | NodeKind.Chart _, PageRequest.Pipeline _ ->
                            Error(LayoutRefusal.NotEditable(n.Id, "a chart draws a presentation request"))
                        | NodeKind.DataGrid spec, _ ->
                            // A grid's columns are not addressable by a granular op, and a
                            // grid holds no children, so the whole kind is the edit.
                            let fresh = gridSpec newKey table.Schema

                            Ok [
                                TreeOp.EditNode(
                                    NodeId n.Id,
                                    NodeKind.DataGrid {
                                        spec with
                                            Source = fresh.Source
                                            Columns = fresh.Columns
                                    }
                                )
                            ]
                        | _ -> Ok []

                    let results = bound |> List.map opsFor

                    match
                        results
                        |> List.tryPick (function
                            | Error e -> Some e
                            | Ok _ -> None)
                    with
                    | Some e -> return Error e
                    | None -> return Ok(results |> List.collect (Result.defaultValue []))
        }

    /// Replace `key`'s request by `request` under a freshly minted key and
    /// rebind its nodes. `None` when the request did not change.
    let private replaceRequest
        (session: Session)
        (tree: Node<unit>)
        (requests: Map<string, PageRequest>)
        (key: string)
        (request: PageRequest)
        : Async<Result<(TreeOp<unit> list * Map<string, PageRequest> * string) option, LayoutRefusal>> =
        async {
            match mintKey (stemOf key) request with
            | Error e -> return Error e
            | Ok newKey when newKey = key -> return Ok None
            | Ok newKey ->
                match! rebind session tree key newKey request with
                | Error e -> return Error e
                | Ok ops ->
                    let requests =
                        requests
                        |> Map.remove key
                        |> Map.add newKey request
                        |> Map.map (fun _ r ->
                            // A request that read the old key now reads the new one.
                            let swap deps =
                                deps |> List.map (fun d -> if d = key then newKey else d)

                            match r with
                            | PageRequest.Presentation(p, deps) -> PageRequest.Presentation(p, swap deps)
                            | PageRequest.Pipeline(p, deps) -> PageRequest.Pipeline(p, swap deps))

                    return Ok(Some(ops, requests, newKey))
        }

    let private patchRequest (request: PageRequest) (args: Args) : Result<PageRequest, LayoutRefusal> =
        match
            period "period" args,
            text "compareTo" args,
            stringList "metrics" args,
            text "subjectLevel" args,
            stringList "site" args
        with
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e
        | Ok None, Ok None, Ok None, Ok None, Ok None ->
            Error(
                LayoutRefusal.Arguments "give at least one of 'period', 'compareTo', 'metrics', 'subjectLevel', 'site'"
            )
        | Ok period, Ok compareTo, Ok metrics, Ok level, Ok site ->
            match request with
            | PageRequest.Presentation(r, deps) ->
                let payload =
                    PresentationRequest.toJsonValue r
                    |> function
                        | JsonValue.JObject members ->
                            let set (name: string) (value: JsonValue option) members =
                                match value with
                                | None -> members
                                | Some v -> (members |> List.filter (fun (k, _) -> k <> name)) @ [ name, v ]

                            JsonValue.JObject(
                                members
                                |> set "period" (period |> Option.map LayoutJson.periodJson)
                                |> set "compareTo" (compareTo |> Option.map JsonValue.JString)
                                |> set
                                    "metrics"
                                    (metrics |> Option.map (List.map JsonValue.JString >> JsonValue.JArray))
                                |> set "subjectLevel" (level |> Option.map JsonValue.JString)
                                |> set
                                    "subjectPrefix"
                                    (site |> Option.map (List.map JsonValue.JString >> JsonValue.JArray))
                            )
                        | other -> other

                PresentationRequest.ofJsonValue payload
                |> Result.map (fun r -> PageRequest.Presentation(r, deps))
                |> Result.mapError (fun e -> LayoutRefusal.Arguments(PresentationRequestError.describe e))
            | PageRequest.Pipeline(r, deps) ->
                match compareTo, level with
                | Some _, _ -> Error(LayoutRefusal.Arguments "a pipeline request has no comparison baseline")
                | _, Some _ -> Error(LayoutRefusal.Arguments "a pipeline request has no subject level")
                | None, None ->
                    let r = {
                        r with
                            Metrics = defaultArg metrics r.Metrics
                    }

                    withSiteAndPeriod site period (PageRequest.Pipeline(r, deps))

    let private setRequest (session: Session) (args: Args) : Async<Result<Edit, LayoutRefusal>> = async {
        match required "key" args with
        | Error e -> return Error e
        | Ok key ->
            match session.Sidecar.Requests.TryFind key with
            | None ->
                return
                    Error(
                        LayoutRefusal.Arguments(
                            sprintf
                                "the page holds no request '%s' (its requests: %s)"
                                key
                                (session.Sidecar.Requests |> Map.toList |> List.map fst |> String.concat ", ")
                        )
                    )
            | Some request ->
                match patchRequest request args with
                | Error e -> return Error e
                | Ok changed ->
                    match! replaceRequest session session.Folded.Tree session.Sidecar.Requests key changed with
                    | Error e -> return Error e
                    | Ok None -> return Error(LayoutRefusal.NoChange(sprintf "request '%s' already says that" key))
                    | Ok(Some(ops, requests, newKey)) ->
                        return
                            Ok {
                                Ops = ops
                                Requests = requests
                                Defaults = session.Sidecar.Defaults
                                Said = [ sprintf "changes request '%s' (now '%s')" key newKey ]
                            }
    }

    let private setDefault (session: Session) (args: Args) : Async<Result<Edit, LayoutRefusal>> = async {
        match stringList "site" args, period "period" args with
        | Error e, _
        | _, Error e -> return Error e
        | Ok None, Ok None -> return Error(LayoutRefusal.Arguments "give 'site', 'period' or both")
        | Ok site, Ok period ->
            let rec each (pending: (string * PageRequest) list) (ops: TreeOp<unit> list) requests said = async {
                match pending with
                | [] -> return Ok(ops, requests, said)
                | (key, request) :: rest ->
                    match withSiteAndPeriod site period request with
                    | Error e -> return Error e
                    | Ok changed ->
                        match! replaceRequest session session.Folded.Tree requests key changed with
                        | Error e -> return Error e
                        | Ok None -> return! each rest ops requests said
                        | Ok(Some(more, requests, newKey)) ->
                            return! each rest (ops @ more) requests (said @ [ sprintf "'%s' (now '%s')" key newKey ])
            }

            match! each (Map.toList session.Sidecar.Requests) [] session.Sidecar.Requests [] with
            | Error e -> return Error e
            | Ok([], _, _) ->
                return Error(LayoutRefusal.NoChange "every request of the page already stands at that site and range")
            | Ok(ops, requests, said) ->
                return
                    Ok {
                        Ops = ops
                        Requests = requests
                        Defaults = {
                            Site = site |> Option.orElse session.Sidecar.Defaults.Site
                            Period = period |> Option.orElse session.Sidecar.Defaults.Period
                        }
                        Said = [
                            sprintf "sets the page's standing site and range, changing %s" (String.concat ", " said)
                        ]
                    }
    }

    let private retitle (session: Session) (args: Args) : Result<Edit, LayoutRefusal> =
        match block session args, required "title" args with
        | Error e, _
        | _, Error e -> Error e
        | Ok n, Ok title ->
            let path =
                match n.Kind with
                | NodeKind.Box _ -> Ok "Heading"
                | NodeKind.Chart _ -> Ok "Title"
                | NodeKind.Heading _ -> Ok "Text"
                | _ -> Error(LayoutRefusal.NotEditable(n.Id, "it carries no title; retitle the block that holds it"))

            path
            |> Result.map (fun path ->
                unchanged session [ TreeOp.UpdateProp(NodeId n.Id, path, PropValue.Wire(JStr title)) ] [
                    sprintf "retitles '%s'" n.Id
                ])

    let private setSection (session: Session) (args: Args) : Result<Edit, LayoutRefusal> =
        let tree = session.Folded.Tree

        match required "action" args, required "title" args with
        | Error e, _
        | _, Error e -> Error e
        | Ok "rename", Ok title ->
            required "section" args
            |> Result.bind (fun id ->
                match LayoutTree.tryFind id tree with
                | None -> Error(LayoutRefusal.UnknownNode id)
                | Some { Kind = NodeKind.Box _ } ->
                    Ok(
                        unchanged session [ TreeOp.UpdateProp(NodeId id, "Heading", PropValue.Wire(JStr title)) ] [
                            sprintf "renames section '%s'" id
                        ]
                    )
                | Some _ -> Error(LayoutRefusal.NotEditable(id, "it is not a section")))
        | Ok "add", Ok title ->
            placementOf args
            |> Result.bind (fun placement ->
                let id = LayoutTree.uniqueId (LayoutTree.slug title) tree

                target tree id placement
                |> Result.map (fun (parentId, order) ->
                    let section =
                        Fuaran.box id {
                            Defaults.box with
                                Role = BoxRole.Group
                                Heading = Some(TextSource.Literal title)
                        }

                    unchanged session (reorder tree parentId order [ TreeOp.InsertChild(NodeId parentId, section) ]) [
                        sprintf "adds section '%s' into '%s'" id parentId
                    ]))
        | Ok other, _ -> Error(LayoutRefusal.Arguments(sprintf "'action' is '%s'; it is add or rename" other))

    // ── Validation ────────────────────────────────────────────────

    let private errorsOf (tree: Node<unit>) : Set<string> =
        match PreEmitValidate.validateForTransport tree with
        | Ok() -> Set.empty
        | Error defects ->
            defects
            |> List.choose (fun d ->
                match PreEmitValidate.describe d with
                | code, Severity.Error, message -> Some(sprintf "%s: %s" code message)
                | _ -> None)
            |> Set.ofList

    /// Every chart or grid of the tree that draws anything but a request the
    /// map holds, refused by name. A static source, a transform over embedded
    /// rows and a grid's static rows are all inline rows.
    let inlineRows (requests: Map<string, PageRequest>) (tree: Node<unit>) : LayoutRefusal list =
        LayoutTree.all tree
        |> List.choose (fun n ->
            match LayoutTree.dataSource n with
            | Some(kind, None) -> Some(LayoutRefusal.InlineRows(sprintf "%s '%s'" kind n.Id))
            | Some(_, Some key) when not (requests.ContainsKey key) -> Some(LayoutRefusal.UnknownRequest(n.Id, key))
            | _ -> None)

    /// The ids of the data-bearing nodes an op list adds or changes.
    let private touched (tree: Node<unit>) (ops: TreeOp<unit> list) : string list =
        ops
        |> List.collect (function
            | TreeOp.InsertChild(_, child) -> LayoutTree.all child |> List.map _.Id
            | TreeOp.EditNode(NodeId id, _)
            | TreeOp.UpdateProp(NodeId id, _, _)
            | TreeOp.ReplaceBinding(NodeId id, _, _) -> [ id ]
            | _ -> [])
        |> List.distinct
        |> List.filter (fun id -> LayoutTree.tryFind id tree |> Option.bind LayoutTree.dataSource |> Option.isSome)

    let private validate
        (session: Session)
        (edit: Edit)
        : Async<Result<Node<unit> * Map<string, PageRequest>, LayoutRefusal>> =
        async {
            let before = session.Folded.Tree

            let applied =
                edit.Ops
                |> List.fold (fun t op -> t |> Result.bind (Apply.apply op)) (Ok before)

            match applied with
            | Error e -> return Error(LayoutRefusal.OpsDoNotApply e.Message)
            | Ok edited ->
                let introduced = Set.difference (errorsOf edited) (errorsOf before)

                if not introduced.IsEmpty then
                    return Error(LayoutRefusal.Grammar(Set.toList introduced))
                else
                    let requests = reachable edited edit.Requests

                    let charts =
                        touched edited edit.Ops
                        |> List.choose (fun id -> LayoutTree.tryFind id edited)
                        |> List.choose (fun n ->
                            match n.Kind, LayoutTree.dataSource n with
                            | NodeKind.Chart _, Some(_, Some key) ->
                                requests.TryFind key |> Option.map (fun request -> n, key, request)
                            | _ -> None)

                    let rec check (pending: (Node<unit> * string * PageRequest) list) (defects: string list) = async {
                        match pending with
                        | [] -> return Ok defects
                        | (n, key, request) :: rest ->
                            match! resolve session key request with
                            | Error e -> return Error e
                            | Ok(_, table) ->
                                let found =
                                    QueryBinding.check table.Schema n
                                    |> List.map (fun d -> sprintf "%s on '%s': %s" d.Code d.NodeId d.Message)

                                return! check rest (defects @ found)
                    }

                    match! check charts [] with
                    | Error e -> return Error e
                    | Ok(_ :: _ as defects) -> return Error(LayoutRefusal.Binding defects)
                    | Ok [] ->
                        match inlineRows requests edited with
                        | refusal :: _ -> return Error refusal
                        | [] ->
                            match PageReadSet.check session.Caller.Deps (PageReadSet.ofRequests requests) with
                            | _ :: _ as reasons -> return Error(LayoutRefusal.Undeclared reasons)
                            | [] -> return Ok(edited, requests)
        }

    // ── The preview ───────────────────────────────────────────────

    /// The page as the reader would see it: each request resolved under the
    /// caller's gate and drawn into its node, a request that resolves to
    /// nothing shown as a note naming why. Built with the same apply engine,
    /// never stored.
    let private preview
        (session: Session)
        (tree: Node<unit>)
        (requests: Map<string, PageRequest>)
        : Async<Node<unit> * PageResolution list> =
        async {
            let rec draw (pending: Node<unit> list) (tree: Node<unit>) (read: PageResolution list) = async {
                match pending with
                | [] -> return tree, read
                | n :: rest ->
                    match LayoutTree.dataSource n with
                    | Some(_, Some key) when requests.ContainsKey key ->
                        let! resolved = resolve session key requests.[key]

                        let op, read =
                            match resolved with
                            | Ok(resolution, table) ->
                                TreeOp.ReplaceBinding(
                                    NodeId n.Id,
                                    "Source",
                                    Binding.Transform(TransformSource.Data(Embedded table), [], None)
                                ),
                                read @ [ resolution ]
                            | Error refusal ->
                                TreeOp.EditNode(
                                    NodeId n.Id,
                                    NodeKind.Callout {
                                        Defaults.callout with
                                            Tone = ToneVariant.Warning
                                            Heading = Some(TextSource.Literal "Not shown")
                                            Body = TextSource.Literal(LayoutRefusal.describe refusal)
                                    }
                                ),
                                read

                        return! draw rest (Apply.apply op tree |> Result.defaultValue tree) read
                    | _ -> return! draw rest tree read
            }

            return! draw (LayoutTree.all tree) tree []
        }

    // ── Answers ───────────────────────────────────────────────────

    let private refusal (tool: LayoutTool) (r: LayoutRefusal) : ToolHelpers.ToolAnswer = {
        Text = sprintf "%s refused (%s): %s." (name tool) (LayoutRefusal.cause r) (LayoutRefusal.describe r)
        Parts = []
    }

    let private callerKey (caller: LayoutCaller) =
        caller.Scope.ScopeId + "\n" + caller.Access.UserId

    let private withheldOf (read: PageResolution list) : BlockWithheld list =
        read
        |> List.collect _.Withheld
        |> List.groupBy _.PolicyRef
        |> List.map (fun (policy, ws) -> {
            PolicyRef = policy
            Count = ws |> List.sumBy _.Count
        })

    let private propose
        (tool: LayoutTool)
        (session: Session)
        (change: LayoutChange)
        (shown: Node<unit>)
        (requests: Map<string, PageRequest>)
        (opCount: int)
        (said: string list)
        : Async<ToolHelpers.ToolAnswer> =
        async {
            let caller = session.Caller
            let id = Guid.NewGuid().ToString "N"

            let proposal = {
                Id = id
                Page = session.Page
                Tool = name tool
                Change = change
                BaseHash = session.Folded.ContentHash
                At = caller.Proposals.Now()
            }

            caller.Proposals.Put(callerKey caller, proposal)

            let! drawn, read = preview session shown requests
            let readSet = PageReadSet.ofRequests requests
            let withheld = withheldOf read

            let request =
                LayoutJson.canonical (
                    JObj [
                        "module", JStr session.Page.Module
                        "page", JStr session.Page.Page
                        "proposal", JStr id
                        "tool", JStr(name tool)
                        match change with
                        | LayoutChange.Append(ops, _) -> "opsHash", JStr(PageLayers.opsHash ops)
                        | LayoutChange.Reset layer -> "reset", JStr(PageLayer.toToken layer)
                    ]
                )

            let provenance: BlockProvenance = {
                Kind = ProposedKind
                Request = request
                FactIds = read |> List.collect _.FactIds
                Withheld = withheld
                TableId = String.concat ", " readSet.Tables
                Metric = readSet.Metrics |> List.map snd |> List.distinct |> String.concat ", "
                Period = ""
            }

            let withheldText =
                match withheld |> List.sumBy _.Count with
                | 0 -> "none withheld"
                | n -> sprintf "%d point(s) withheld" n

            return {
                Text =
                    sprintf
                        "Proposed an edit of page %s/%s: %s (%d op(s)). Proposal id: %s. The user sees the proposed page; %d request(s) drawn, %s. Nothing is saved: to keep it, call layout_save with this proposal id and a layer. This result carries none of the page's values, so state none."
                        session.Page.Module
                        session.Page.Page
                        (String.concat "; " said)
                        opCount
                        id
                        (List.length read)
                        withheldText
                Parts = [ BlockPart(Fuaran.UI.Generated.encodeNode drawn, provenance) ]
            }
        }

    let private proposeEdit (tool: LayoutTool) (session: Session) (edit: Edit) : Async<ToolHelpers.ToolAnswer> = async {
        if List.isEmpty edit.Ops then
            return refusal tool (LayoutRefusal.NoChange "the edit has no ops")
        else
            match! validate session edit with
            | Error r -> return refusal tool r
            | Ok(edited, requests) ->
                match
                    PageRequestSidecar.toBytes {
                        Requests = requests
                        Defaults = edit.Defaults
                    }
                with
                | Error e -> return refusal tool (LayoutRefusal.Sidecar e)
                | Ok sidecar ->
                    return!
                        propose
                            tool
                            session
                            (LayoutChange.Append(edit.Ops, sidecar))
                            edited
                            requests
                            (List.length edit.Ops)
                            edit.Said
    }

    let private proposeReset (session: Session) (layer: PageLayer) : Async<ToolHelpers.ToolAnswer> = async {
        let tool = LayoutTool.Reset
        let layers = session.Folded.Layers

        match layers |> List.tryFind (fun l -> l.Key.Layer = layer) with
        | None
        | Some { Records = [] } ->
            return
                refusal
                    tool
                    (LayoutRefusal.NoChange(
                        sprintf "the %s layer holds no edits of this page" (PageLayer.toToken layer)
                    ))
        | Some _ ->
            let platform = layers |> List.find (fun l -> l.Key.Layer = PageLayer.Platform)

            match platform.Seed |> Option.map PageLayers.decodeDocument with
            | None
            | Some(Error _) -> return refusal tool (LayoutRefusal.Store PageStoreError.NotSeeded)
            | Some(Ok seed) ->
                let kept =
                    layers
                    |> List.map (fun l ->
                        if l.Key.Layer = layer then
                            {
                                l with
                                    Records = []
                                    Sidecar = (if layer = PageLayer.Platform then l.Sidecar else None)
                            }
                        else
                            l)

                let fold =
                    PageLayers.fold
                        seed
                        (kept
                         |> List.map (fun l -> {
                             LayerStream.Layer = l.Key.Layer
                             Principal = l.Key.Principal
                             Records = l.Records
                         }))

                let sidecar =
                    match kept |> List.rev |> List.tryPick _.Sidecar with
                    | None -> Ok PageRequestSidecar.empty
                    | Some bytes -> PageRequestSidecar.ofBytes bytes

                match sidecar with
                | Error e -> return refusal tool (LayoutRefusal.Sidecar e)
                | Ok sidecar ->
                    return!
                        propose tool session (LayoutChange.Reset layer) fold.Document sidecar.Requests 0 [
                            sprintf "drops the %s layer's edits" (PageLayer.toToken layer)
                        ]
    }

    let private save (caller: LayoutCaller) (args: Args) : Async<ToolHelpers.ToolAnswer> = async {
        let tool = LayoutTool.Save

        match required "proposal" args, required "layer" args |> Result.bind layerOf with
        | Error e, _
        | _, Error e -> return refusal tool e
        | Ok id, Ok layer ->
            match caller.Proposals.TryGet(callerKey caller, id) with
            | None -> return refusal tool (LayoutRefusal.UnknownProposal id)
            | Some proposal ->
                match! caller.Store.ReadFolded(caller.Scope, caller.Access, proposal.Page) with
                | Error e -> return refusal tool (LayoutRefusal.Store e)
                | Ok folded when folded.ContentHash <> proposal.BaseHash ->
                    caller.Proposals.Remove(callerKey caller, id)
                    return refusal tool (LayoutRefusal.StaleProposal id)
                | Ok _ ->
                    let! saved =
                        match proposal.Change with
                        | LayoutChange.Append(ops, sidecar) ->
                            caller.Store.AppendOps(caller.Scope, caller.Access, proposal.Page, layer, ops, Some sidecar)
                            |> fun a -> async {
                                let! r = a
                                return r |> Result.mapError LayoutRefusal.Store
                            }
                        | LayoutChange.Reset proposed when proposed <> layer ->
                            async.Return(Error(LayoutRefusal.LayerMismatch(proposed, layer)))
                        | LayoutChange.Reset _ ->
                            caller.Store.ResetLayer(caller.Scope, caller.Access, proposal.Page, layer)
                            |> fun a -> async {
                                let! r = a
                                return r |> Result.mapError LayoutRefusal.Store
                            }

                    match saved with
                    | Error r -> return refusal tool r
                    | Ok receipt ->
                        caller.Proposals.Remove(callerKey caller, id)

                        return {
                            Text =
                                sprintf
                                    "Saved proposal %s (%s) to the %s layer of page %s/%s: version %d, document %s. The page now shows it."
                                    id
                                    proposal.Tool
                                    (PageLayer.toToken layer)
                                    proposal.Page.Module
                                    proposal.Page.Page
                                    receipt.Version
                                    receipt.DocumentHash
                            Parts = []
                        }
    }

    /// The executor over explicit dependencies — the testable core `answer`
    /// adapts `HttpContext` onto.
    let answerWith (tool: LayoutTool) (caller: LayoutCaller) (argsJson: string) : Async<ToolHelpers.ToolAnswer> = async {
        match argsOf tool argsJson with
        | Error e -> return refusal tool e
        | Ok args when tool = LayoutTool.Save -> return! save caller args
        | Ok args ->
            match pageOf args with
            | Error e -> return refusal tool e
            | Ok page ->
                match! open' caller page with
                | Error e -> return refusal tool e
                | Ok session ->
                    let! edit =
                        match tool with
                        | LayoutTool.AddBlock -> addBlock session args
                        | LayoutTool.RemoveBlock -> async.Return(removeBlock session args)
                        | LayoutTool.MoveBlock -> async.Return(moveBlock session args)
                        | LayoutTool.SetRequest -> setRequest session args
                        | LayoutTool.Retitle -> async.Return(retitle session args)
                        | LayoutTool.SetSection -> async.Return(setSection session args)
                        | LayoutTool.SetDefault -> setDefault session args
                        | LayoutTool.Reset
                        | LayoutTool.Save -> async.Return(Error(LayoutRefusal.Arguments "unreachable"))

                    match tool with
                    | LayoutTool.Reset ->
                        match required "layer" args |> Result.bind layerOf with
                        | Error e -> return refusal tool e
                        | Ok layer -> return! proposeReset session layer
                    | _ ->
                        match edit with
                        | Error e -> return refusal tool e
                        | Ok edit -> return! proposeEdit tool session edit
    }

    /// The registered executor: the runtime the registration factory built,
    /// the fact browse's dependencies for this request, the scope the
    /// platform's scope resolution minted for it, and the access context the
    /// request carries.
    let answer (tool: LayoutTool) (ctx: HttpContext) (argsJson: string) : Async<ToolHelpers.ToolAnswer> = async {
        let runtime =
            match ctx.RequestServices.GetService(typeof<LayoutToolsRuntime>) with
            | :? LayoutToolsRuntime as runtime -> Some runtime
            | _ -> None

        match runtime, FactBrowseHandler.factBrowseDeps ctx with
        | None, _ ->
            return
                refusal
                    tool
                    (LayoutRefusal.Unavailable
                        "the page store is not composed in this deployment, so there is no page to edit")
        | _, None ->
            return
                refusal
                    tool
                    (LayoutRefusal.Unavailable
                        "the fact store is not composed in this deployment, so no request resolves")
        | Some runtime, Some deps ->
            match! runtime.Resolvers.ForRequest ctx with
            | Error e -> return refusal tool (LayoutRefusal.Unavailable(PageRefusal.describe e))
            | Ok resolver ->
                let caller = {
                    Scope = StorageScopeResolver.ScopeResolution.forRequest ctx
                    Access = AIToolRegistry.reconstructAccessContext ctx
                    Deps = deps
                    Resolver = resolver
                    Store = runtime.Store
                    Proposals = runtime.Proposals
                }

                return! answerWith tool caller argsJson
    }

    /// Register the runtime: the page store and the resolvers, resolved here,
    /// in the registration factory, and the process's proposals.
    let register (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<LayoutToolsRuntime>(
            Func<IServiceProvider, LayoutToolsRuntime>(fun sp -> {
                Store = sp.GetRequiredService<IPageDocumentStore>()
                Resolvers = sp.GetRequiredService<PageRequestResolvers>()
                Proposals = LayoutProposalStore()
            })
        )

    /// All nine tools, as `AIServerApp.withAIAnsweringTools` takes them.
    let tools: (AIToolDefinition * (HttpContext -> string -> Async<ToolHelpers.ToolAnswer>)) list =
        all |> List.map (fun tool -> definition tool, answer tool)