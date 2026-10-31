// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Fuaran.Core
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.TeamManagement
open ToolUp.Facts

// ─── The page request resolver ──────────────────────────────────────
//
// A stored page renders by resolving each request it names, for the reader,
// under the reader's gate. This is the one function that does it, for a
// client page and for a server-rendered report alike: both are handed the
// same resolver values, so one request resolves to the same canonical bytes
// through either.
//
// **Two kinds, two existing paths.** A presentation request is read through
// the fact browse's series read and drawn into rows by the presentation
// builders' data step (`Presentation.data`), the rows a built block draws. A
// pipeline request is evaluated by the pipeline tool over the gated slice
// (`PipelineAnsweringTool.rerun`), the re-run a pipeline block's panel draws.
// Both are judged at the `FactBrowse` door, the door a reader's browse is.
//
// **Data, never a node.** A resolution is the columnar table the page's node
// binds, its column names, and what the gate withheld, counted by policy. A
// withheld point is absent from the rows, never zero; a request whose every
// point is withheld is a refusal naming the count, never an empty table.
//
// **A refusal is typed by cause**, so a page names why a block is blank:
// a malformed request, an unknown kind, an undeclared table, everything
// withheld, no facts, a cap, a read or an evaluation that refused, no fact
// store for the caller, or a builder defect.
//
// **Cached per session.** One caller (a scope and a principal) has one
// session; a resolution is kept under the request's canonical hash and the
// fingerprint of the viewer facts the gate decides on, and dropped when the
// fact-table change signal reports its table changed. Nothing is shared
// between callers, and a gate whose viewer facts moved misses.

/// Why a page request was not resolved, by cause.
[<RequireQualifiedAccess>]
type PageRefusal =
    /// The request is not a page request's shape.
    | MalformedRequest of reason: string
    /// The request names a kind this platform does not resolve.
    | UnknownKind of kind: string
    /// The request names a fact table this deployment does not declare.
    | UnknownTable of tableId: string * declared: string list
    /// Every point the read served is withheld from the reader.
    | Withheld of read: int * withheld: BlockWithheld list
    /// No fact of the table answers the request.
    | NoFacts of tableId: string
    /// The answer would exceed a cap.
    | Budget of reason: string
    /// The read refused the request, in its own words.
    | ReadRefused of reason: string
    /// The pipeline was refused or failed over the slice, in the
    /// evaluation's own words.
    | Evaluation of reason: string
    /// The caller's deployment composes no fact store or disclosure gate.
    | NoFactStore
    /// The builders produced no data: a defect of the builder, never of the
    /// request.
    | Defect of reason: string

/// Reading a page refusal.
[<RequireQualifiedAccess>]
module PageRefusal =

    /// The cause's name, stable for a page to branch on.
    let cause (refusal: PageRefusal) : string =
        match refusal with
        | PageRefusal.MalformedRequest _ -> "malformed-request"
        | PageRefusal.UnknownKind _ -> "unknown-kind"
        | PageRefusal.UnknownTable _ -> "unknown-table"
        | PageRefusal.Withheld _ -> "withheld"
        | PageRefusal.NoFacts _ -> "no-facts"
        | PageRefusal.Budget _ -> "budget"
        | PageRefusal.ReadRefused _ -> "read-refused"
        | PageRefusal.Evaluation _ -> "evaluation"
        | PageRefusal.NoFactStore -> "no-fact-store"
        | PageRefusal.Defect _ -> "defect"

    /// The refusal as one sentence: counts and names, never a value.
    let describe (refusal: PageRefusal) : string =
        match refusal with
        | PageRefusal.MalformedRequest reason -> reason
        | PageRefusal.UnknownKind kind ->
            sprintf "the page request's kind '%s' is not one this platform resolves (presentation, pipeline)" kind
        | PageRefusal.UnknownTable(tableId, declared) ->
            sprintf
                "no fact table '%s' is declared in this deployment (declared: %s)"
                tableId
                (match declared with
                 | [] -> "none"
                 | ids -> ids |> List.map (sprintf "'%s'") |> String.concat ", ")
        | PageRefusal.Withheld(read, withheld) ->
            sprintf
                "no point it reads carries a number you may be shown: %d point(s) read, %s"
                read
                (PipelineRefusal.withheldPhrase (
                    withheld
                    |> List.map (fun w ->
                        ({
                            PolicyRef = w.PolicyRef
                            Count = w.Count
                        }
                        : PipelineWithheldCount))
                ))
        | PageRefusal.NoFacts tableId -> sprintf "no fact of table '%s' answers the request" tableId
        | PageRefusal.Budget reason
        | PageRefusal.ReadRefused reason
        | PageRefusal.Evaluation reason
        | PageRefusal.Defect reason -> reason
        | PageRefusal.NoFactStore ->
            "the fact store is not composed in this deployment, so there are no facts to resolve"

    /// The refusal for a malformed page request.
    let ofRequestError (error: PageRequestError) : PageRefusal =
        match error with
        | PageRequestError.UnknownKind kind -> PageRefusal.UnknownKind kind
        | other -> PageRefusal.MalformedRequest(PageRequestError.describe other)

    /// The refusal a pipeline evaluation answered with, by cause.
    let ofPipeline (refusal: PipelineRefusal) : PageRefusal =
        match refusal with
        | PipelineRefusal.MalformedArguments _ -> PageRefusal.MalformedRequest(PipelineRefusal.describe refusal)
        | PipelineRefusal.NoFacts tableId -> PageRefusal.NoFacts tableId
        | PipelineRefusal.NothingDisclosable(cells, withheld) ->
            PageRefusal.Withheld(
                cells,
                withheld
                |> List.map (fun w ->
                    ({
                        PolicyRef = w.PolicyRef
                        Count = w.Count
                    }
                    : BlockWithheld))
            )
        | PipelineRefusal.ResultOverCap _ -> PageRefusal.Budget(PipelineRefusal.describe refusal)
        | PipelineRefusal.SliceRefused _ -> PageRefusal.ReadRefused(PipelineRefusal.describe refusal)
        | other -> PageRefusal.Evaluation(PipelineRefusal.describe other)

/// The facts about one caller the disclosure gate decides on: who asks, in
/// which scope, as which member of which team, and the team's output level.
/// Never a row.
type PageGateViewer = {
    /// The principal the reads run for.
    Principal: string
    /// The scope the reads run in.
    ScopeId: string
    /// The viewer's active team.
    ActiveTeamId: string option
    /// Whether the viewer is a platform admin.
    IsPlatformAdmin: bool
    /// The team's role for the viewer.
    TeamRole: string option
    /// The team's output-visibility level in force.
    OutputVisibility: string option
}

/// Fingerprinting a viewer.
[<RequireQualifiedAccess>]
module PageGateViewer =

    let private sha256 (text: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes text)
        |> Array.map (fun b -> b.ToString "x2")
        |> String.concat ""

    /// The viewer's fingerprint: equal for two viewers the gate cannot tell
    /// apart, different when any fact it decides on differs.
    let fingerprint (viewer: PageGateViewer) : string =
        let optional (v: string option) =
            match v with
            | Some s -> JStr s
            | None -> JArr []

        JObj [
            "principal", JStr viewer.Principal
            "scope", JStr viewer.ScopeId
            "team", optional viewer.ActiveTeamId
            "platformAdmin", JBool viewer.IsPlatformAdmin
            "role", optional viewer.TeamRole
            "outputVisibility", optional viewer.OutputVisibility
        ]
        |> Canonical.write Rendering.CrossHost
        |> sha256

    /// The viewer a request's composition knows: the request's viewer, the
    /// team's role for it and the team's output level, where those services
    /// are composed.
    let forRequest (ctx: HttpContext) (scope: ResolvedScope) (principal: string) : Async<PageGateViewer> = async {
        let viewer = RequestViewer.ofItems ctx.Items
        let teamId = viewer |> Option.bind _.ActiveTeamId

        let! role =
            match teamId, ServiceComposition.tryService<ITeamStore> ctx.RequestServices with
            | Some team, Some teams -> teams.GetMemberRole(team, principal)
            | _ -> async.Return None

        let! level =
            match teamId, ServiceComposition.tryService<ITeamOutputVisibilitySource> ctx.RequestServices with
            | Some team, Some source -> source.Current team
            | _ -> async.Return(Error "")

        return {
            Principal = principal
            ScopeId = scope.ScopeId
            ActiveTeamId = teamId
            IsPlatformAdmin = viewer |> Option.exists _.IsPlatformAdmin
            TeamRole = role |> Option.map (sprintf "%A")
            OutputVisibility =
                match level with
                | Ok level -> Some(sprintf "%A" level)
                | Error _ -> None
        }
    }

/// One caller's cached resolutions.
type PageResolutionSession = {
    /// The resolution kept under a request hash and a gate fingerprint.
    TryGet: string -> string -> PageResolution option
    /// Keep a resolution of the table named, under a request hash and a gate
    /// fingerprint.
    Put: string -> string -> string -> PageResolution -> unit
}

/// The resolutions every caller's session keeps, dropped by table when the
/// fact-table change signal reports a change. One per process.
type PageResolutionCache(signal: IFactTableChangeSignal option, maxSessions: int, maxEntries: int) =
    let sync = obj ()

    // session key -> (scope id, (request hash, gate fingerprint) -> (table, resolution))
    let sessions =
        Dictionary<string, string * Dictionary<struct (string * string), string * PageResolution>>()

    let order = Queue<string>()
    let listened = HashSet<string>()

    let invalidate (scopeId: string) (tableIds: string list) =
        lock sync (fun () ->
            for KeyValue(_, (sessionScope, entries)) in sessions do
                if sessionScope = scopeId then
                    if List.isEmpty tableIds then
                        entries.Clear()
                    else
                        entries
                        |> Seq.filter (fun (KeyValue(_, (table, _))) -> List.contains table tableIds)
                        |> Seq.map _.Key
                        |> List.ofSeq
                        |> List.iter (entries.Remove >> ignore))

    /// The default bounds: 1024 sessions, 256 resolutions each.
    new(signal: IFactTableChangeSignal option) = PageResolutionCache(signal, 1024, 256)

    /// Drop every resolution of `tableIds` (every table, when empty) kept in
    /// sessions of `scopeId`.
    member _.Invalidate(scopeId: string, tableIds: string list) = invalidate scopeId tableIds

    /// The session of the caller `principal` in `scope`, listening for the
    /// scope's changes from its first session on.
    member _.Session(scope: ResolvedScope, principal: string) : Async<PageResolutionSession> = async {
        let scopeId = scope.ScopeId

        let listen = lock sync (fun () -> signal.IsSome && listened.Add scopeId)

        if listen then
            let! _ = signal.Value.Listen(scopeId, invalidate scopeId)
            ()

        let key = scopeId + "\n" + principal

        let entries =
            lock sync (fun () ->
                match sessions.TryGetValue key with
                | true, (_, entries) -> entries
                | false, _ ->
                    while sessions.Count >= maxSessions && order.Count > 0 do
                        sessions.Remove(order.Dequeue()) |> ignore

                    let entries = Dictionary<struct (string * string), string * PageResolution>()

                    sessions.[key] <- (scopeId, entries)
                    order.Enqueue key
                    entries)

        return {
            TryGet =
                fun requestHash gate ->
                    lock sync (fun () ->
                        match entries.TryGetValue(struct (requestHash, gate)) with
                        | true, (_, resolution) -> Some resolution
                        | false, _ -> None)
            Put =
                fun requestHash gate tableId resolution ->
                    lock sync (fun () ->
                        if entries.Count >= maxEntries then
                            entries.Clear()

                        entries.[struct (requestHash, gate)] <- (tableId, resolution))
        }
    }

/// The resolver one caller renders a stored page through: each request kind
/// as a plain function, typed and over canonical JSON. A client page and a
/// server-rendered report are handed these same values.
type PageRequestResolver = {
    /// Resolve a page request.
    Resolve: PageRequest -> Async<Result<PageResolution, PageRefusal>>
    /// Resolve a presentation request.
    ResolvePresentation: PresentationRequest -> Async<Result<PageResolution, PageRefusal>>
    /// Resolve a pipeline request.
    ResolvePipeline: PipelineBlockRequest -> Async<Result<PageResolution, PageRefusal>>
    /// The resolver of one kind, over the request's canonical payload JSON,
    /// answering the resolution's canonical JSON. An unknown kind's resolver
    /// refuses naming the kind.
    ByKind: string -> (string -> Async<Result<string, PageRefusal>>)
}

/// Building a caller's resolver.
[<RequireQualifiedAccess>]
module PageRequestResolver =

    /// The kinds a resolver resolves.
    let kinds: string list = [ PageRequest.PresentationKind; PageRequest.PipelineKind ]

    let private sha256 (text: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes text)
        |> Array.map (fun b -> b.ToString "x2")
        |> String.concat ""

    /// The hash a request is cached under: its canonical text, without the
    /// keys it depends on (they place it in a page, and change nothing it
    /// reads).
    let requestHash (request: PageRequest) : Result<string, PageRefusal> =
        let bare =
            match request with
            | PageRequest.Presentation(r, _) -> PageRequest.Presentation(r, [])
            | PageRequest.Pipeline(r, _) -> PageRequest.Pipeline(r, [])

        PageRequest.toJson bare
        |> Result.map sha256
        |> Result.mapError PageRefusal.ofRequestError

    let private declared (deps: FactBrowseHandler.FactBrowseDeps) (tableId: string) : Result<unit, PageRefusal> =
        match deps.Tables.TryGetTable tableId with
        | Some _ -> Ok()
        | None -> Error(PageRefusal.UnknownTable(tableId, deps.Tables.Tables |> List.map _.Id))

    let private encoded (table: Table) : Result<string * int * string list, PageRefusal> =
        FactBrowseSlice.encode table
        |> Result.map (fun text -> text, Table.rowCount table, table.Schema |> List.map _.Name)
        |> Result.mapError (fun reason ->
            PageRefusal.Defect(sprintf "the resolution's table could not be encoded: %s" reason))

    /// A presentation request answered through the series read and the
    /// builders' data step.
    let private presentation
        (browse: IFactBrowseApi)
        (deps: FactBrowseHandler.FactBrowseDeps)
        (request: PresentationRequest)
        : Async<Result<PageResolution, PageRefusal>> =
        async {
            match PresentationRequest.validate request with
            | Error e -> return Error(PageRefusal.MalformedRequest(PresentationRequestError.describe e))
            | Ok request ->
                match declared deps request.TableId with
                | Error refusal -> return Error refusal
                | Ok() ->
                    let! answers =
                        PresentationRequest.toQueries FactBrowseApi.MaxSeriesPoints request
                        |> List.map (fun query -> async {
                            match! browse.QuerySeries query with
                            | Ok series -> return SeriesAnswer.Answered series
                            | Error reason -> return SeriesAnswer.Refused(query.Metric, reason)
                        })
                        |> Async.Sequential

                    let answers = List.ofArray answers

                    let served =
                        answers
                        |> List.choose (function
                            | SeriesAnswer.Answered s -> Some s
                            | SeriesAnswer.Refused _ -> None)

                    let points = served |> List.collect _.Points

                    match Presentation.data request answers with
                    | Error defect -> return Error(PageRefusal.Defect(PresentationDefect.describe defect))
                    | Ok _ when List.isEmpty served ->
                        return
                            Error(
                                PageRefusal.ReadRefused(
                                    answers
                                    |> List.choose (function
                                        | SeriesAnswer.Refused(metric, why) -> Some(sprintf "%s: %s" metric why)
                                        | SeriesAnswer.Answered _ -> None)
                                    |> String.concat "; "
                                )
                            )
                    | Ok data ->
                        match data.Table with
                        | None when data.Provenance.Withheld |> List.exists (fun w -> w.Count > 0) ->
                            return Error(PageRefusal.Withheld(List.length points, data.Provenance.Withheld))
                        | None -> return Error(PageRefusal.NoFacts request.TableId)
                        | Some table ->
                            return
                                encoded table
                                |> Result.map (fun (text, rows, columns) -> {
                                    Kind = PageRequest.PresentationKind
                                    Table = text
                                    Rows = rows
                                    Columns = columns
                                    Withheld = data.Provenance.Withheld
                                    Notes = data.Notes
                                    FactIds = data.Provenance.FactIds
                                    RunStamp = None
                                })
        }

    /// A pipeline request evaluated by the pipeline tool over the gated slice.
    let private pipeline
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (request: PipelineBlockRequest)
        : Async<Result<PageResolution, PageRefusal>> =
        async {
            match declared deps request.TableId with
            | Error refusal -> return Error refusal
            | Ok() ->
                match! PipelineAnsweringTool.rerun deps scope principal (PipelineBlock.toJson request) with
                | Error refused -> return Error(PageRefusal.ofPipeline refused)
                | Ok result ->
                    return
                        Ok {
                            Kind = PageRequest.PipelineKind
                            Table = result.Table
                            Rows = result.Rows
                            Columns = result.Columns
                            Withheld = PipelineBlock.withheldByPolicy result.Withheld
                            Notes = result.Withheld |> List.map PipelineBlock.withheldText
                            FactIds = result.FactIds
                            RunStamp = result.RunStamp
                        }
        }

    /// The resolver for the caller `principal` in `scope`, over the fact
    /// browse's dependencies, caching in `session` under the fingerprint
    /// `gate` answers for each resolution. `scope` is the one the platform
    /// minted for the caller; nothing here builds one.
    let create
        (session: PageResolutionSession)
        (gate: unit -> Async<string>)
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        : PageRequestResolver =
        let browse = FactBrowseHandler.api deps scope principal

        let resolve (request: PageRequest) : Async<Result<PageResolution, PageRefusal>> = async {
            match requestHash request with
            | Error refusal -> return Error refusal
            | Ok hash ->
                let! fingerprint = gate ()

                match session.TryGet hash fingerprint with
                | Some resolution -> return Ok resolution
                | None ->
                    let! resolved =
                        match request with
                        | PageRequest.Presentation(r, _) -> presentation browse deps r
                        | PageRequest.Pipeline(r, _) -> pipeline deps scope principal r

                    match resolved with
                    | Ok resolution -> session.Put hash fingerprint (PageRequest.tableId request) resolution
                    | Error _ -> ()

                    return resolved
        }

        let byKind (kind: string) (payloadJson: string) : Async<Result<string, PageRefusal>> = async {
            let request =
                match Canonical.read payloadJson with
                | Error e -> Error(PageRefusal.MalformedRequest(sprintf "the payload is not JSON: %s" e.Message))
                | Ok payload ->
                    PageRequest.ofJVal (JObj [ "kind", JStr kind; "payload", payload ])
                    |> Result.mapError PageRefusal.ofRequestError

            match request with
            | Error refusal -> return Error refusal
            | Ok request ->
                let! resolved = resolve request
                return resolved |> Result.map PageResolution.toJson
        }

        {
            Resolve = resolve
            ResolvePresentation = fun r -> resolve (PageRequest.Presentation(r, []))
            ResolvePipeline = fun r -> resolve (PageRequest.Pipeline(r, []))
            ByKind = byKind
        }

    /// Each kind's resolver over canonical JSON, keyed by kind: the map a
    /// stored-page view takes.
    let byKind (resolver: PageRequestResolver) : Map<string, string -> Async<Result<string, PageRefusal>>> =
        kinds |> List.map (fun kind -> kind, resolver.ByKind kind) |> Map.ofList

/// What a deployment composing page requests registers: the resolver for a
/// request's caller. Registered only when a fact store is composed.
type PageRequestResolvers = {
    /// The resolver for the request's caller: the scope the platform minted
    /// for the request and its user, in that caller's session.
    ForRequest: HttpContext -> Async<Result<PageRequestResolver, PageRefusal>>
}

/// Composing page request resolution.
[<RequireQualifiedAccess>]
module PageRequestResolvers =

    /// The registration over a process's cache.
    let create (cache: PageResolutionCache) : PageRequestResolvers = {
        ForRequest =
            fun ctx -> async {
                match FactBrowseHandler.factBrowseDeps ctx with
                | None -> return Error PageRefusal.NoFactStore
                | Some deps ->
                    let scope = StorageScopeResolver.ScopeResolution.forRequest ctx

                    let principal =
                        RequestViewer.ofItems ctx.Items
                        |> Option.map _.UserId
                        |> Option.defaultValue "anonymous"

                    let! session = cache.Session(scope, principal)

                    let gate () = async {
                        let! viewer = PageGateViewer.forRequest ctx scope principal
                        return PageGateViewer.fingerprint viewer
                    }

                    return Ok(PageRequestResolver.create session gate deps scope principal)
            }
    }

    /// Register the cache, the registration and the demand that arms the
    /// fact-table change signal. `FactsCompose.withFactStore` composes the
    /// signal; without it the cache never invalidates, so this is called only
    /// under a composed fact store.
    let register (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<PageResolutionCache>(
            Func<IServiceProvider, PageResolutionCache>(fun sp ->
                PageResolutionCache(ServiceComposition.tryService<IFactTableChangeSignal> sp))
        )
        |> ignore

        services.AddSingleton<PageRequestResolvers>(
            Func<IServiceProvider, PageRequestResolvers>(fun sp ->
                create (sp.GetRequiredService<PageResolutionCache>()))
        )
        |> ignore

        FactTableChangeSignal.demand services