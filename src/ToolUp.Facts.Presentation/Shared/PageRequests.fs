// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open System
open Fuaran.Core
open ToolUp.AI.Wire
open ToolUp.Platform.AI
open ToolUp.Facts

// ─── The requests a stored page names ───────────────────────────────
//
// A stored page holds no rows. Every data-bearing node in it names a
// request, and the page renders by resolving each request under the
// reader's gate. Two kinds of request exist: a presentation request (what
// `render_chart` and `render_table` draw) and a pipeline request (the slice
// and the `Fuaran.Compute` pipeline a pipeline block persists).
//
// **The canonical form is the contract.** A page tier that knows nothing
// about either kind carries a request as an opaque payload under a kind
// name, with the keys of the other requests it depends on. This file
// defines that payload: `{"dependsOn":[…],"kind":"…","payload":{…}}` in the
// platform's cross-host canonical JSON (`Canonical.write
// Rendering.CrossHost`: members sorted Ordinal, no whitespace, the pinned
// float layout). The presentation payload is the presentation request's own
// codec; the pipeline payload is the argument shape the pipeline tool reads,
// so the server reads it with the tool's own parser. One request has one
// text, so its hash keys a cache and two hosts agree on it.
//
// The resolution is data, never a node: the columnar table the page's node
// binds, its schema, and what the reader's gate withheld, counted.
//
// Compiled by both compilers.

/// A request a stored page's data-bearing node names.
[<RequireQualifiedAccess>]
type PageRequest =
    /// A presentation request: a trend, a breakdown, a comparison or a
    /// table of one fact table's metrics. `dependsOn` names the requests
    /// whose outputs it reads, by their keys in the page.
    | Presentation of request: PresentationRequest * dependsOn: string list
    /// A pipeline request: a slice of one fact table and the pipeline
    /// evaluated over it, as a pipeline block persists it. `dependsOn` names
    /// the requests whose outputs it reads, by their keys in the page.
    | Pipeline of request: PipelineBlockRequest * dependsOn: string list

/// Why a page request's JSON was refused. Each case names what to repair.
[<RequireQualifiedAccess>]
type PageRequestError =
    /// The text is not JSON, or not the JSON object a request is.
    | NotJson of reason: string
    /// A required member is absent.
    | MissingField of field: string
    /// A member is present and not what it must be.
    | InvalidField of field: string * reason: string
    /// The request names a kind this platform does not resolve.
    | UnknownKind of kind: string
    /// The presentation payload was refused by the presentation codec.
    | Presentation of PresentationRequestError

/// Reading a page request refusal.
[<RequireQualifiedAccess>]
module PageRequestError =

    /// The refusal as one sentence.
    let describe (error: PageRequestError) : string =
        match error with
        | PageRequestError.NotJson reason -> sprintf "the page request is not a JSON object: %s" reason
        | PageRequestError.MissingField field -> sprintf "the page request has no '%s'" field
        | PageRequestError.InvalidField(field, reason) -> sprintf "the page request's '%s' is invalid: %s" field reason
        | PageRequestError.UnknownKind kind ->
            sprintf "the page request's kind '%s' is not one this platform resolves (presentation, pipeline)" kind
        | PageRequestError.Presentation e -> PresentationRequestError.describe e

/// A page request resolved for one reader, under that reader's gate: the
/// data the page's node binds. It carries no node.
type PageResolution = {
    /// The request's kind.
    Kind: string
    /// The rows as a `Fuaran.Core.Column` table in its canonical codec. A
    /// withheld point is absent from it, never zero: a chart's row leaves it
    /// out, a table's cell and a pipeline's withheld column are null.
    Table: string
    /// The table's row count.
    Rows: int
    /// The table's column names, in order.
    Columns: string list
    /// What the reader's gate withheld, counted by disclosure policy.
    Withheld: BlockWithheld list
    /// Each point or column not shown, named with its reason.
    Notes: string list
    /// The ids of the facts the resolution read, in the order read.
    FactIds: string list
    /// The run stamp of the slice a pipeline request read; `None` for a
    /// presentation request, or when the table had committed no run.
    RunStamp: string option
}

/// The canonical JSON of a page request.
[<RequireQualifiedAccess>]
module PageRequest =

    /// The kind name of a presentation request.
    [<Literal>]
    let PresentationKind = "presentation"

    /// The kind name of a pipeline request.
    [<Literal>]
    let PipelineKind = "pipeline"

    /// The request's kind name.
    let kind (request: PageRequest) : string =
        match request with
        | PageRequest.Presentation _ -> PresentationKind
        | PageRequest.Pipeline _ -> PipelineKind

    /// The keys of the requests this one depends on, sorted and distinct.
    let dependsOn (request: PageRequest) : string list =
        match request with
        | PageRequest.Presentation(_, keys)
        | PageRequest.Pipeline(_, keys) -> keys |> List.distinct |> List.sort

    /// The fact table the request reads.
    let tableId (request: PageRequest) : string =
        match request with
        | PageRequest.Presentation(r, _) -> r.TableId
        | PageRequest.Pipeline(r, _) -> r.TableId

    // ── JSON value bridges ────────────────────────────────────────────
    //
    // The presentation codec writes the portable JSON value model; the
    // canonical writer reads `JVal`. These bridges are total over the
    // values the codec writes, and a number keeps its value.

    let rec private ofJsonValue (v: JsonValue) : Result<JVal, string> =
        let all (items: Result<'a, string> list) : Result<'a list, string> =
            List.foldBack
                (fun item acc ->
                    match item, acc with
                    | Ok x, Ok xs -> Ok(x :: xs)
                    | Error e, _
                    | _, Error e -> Error e)
                items
                (Ok [])

        match v with
        | JsonValue.JNull -> Error "a null member, which the canonical form does not carry"
        | JsonValue.JBool b -> Ok(JVal.JBool b)
        | JsonValue.JString s -> Ok(JStr s)
        | JsonValue.JNumber n when n = Math.Floor n && n >= float Int32.MinValue && n <= float Int32.MaxValue ->
            Ok(JInt(int n))
        | JsonValue.JNumber n -> Ok(JFloat n)
        | JsonValue.JArray items -> items |> List.map ofJsonValue |> all |> Result.map JArr
        | JsonValue.JObject members ->
            members
            |> List.map (fun (k, m) -> ofJsonValue m |> Result.map (fun j -> k, j))
            |> all
            |> Result.map JObj

    let rec private toJsonValue (v: JVal) : JsonValue =
        match v with
        | JStr s -> JsonValue.JString s
        | JInt i -> JsonValue.JNumber(float i)
        | JFloat f -> JsonValue.JNumber f
        | JVal.JBool b -> JsonValue.JBool b
        | JArr items -> JsonValue.JArray(items |> List.map toJsonValue)
        | JObj members -> JsonValue.JObject(members |> List.map (fun (k, m) -> k, toJsonValue m))

    let private texts (items: string list) = JArr(items |> List.map JStr)

    let private pipelinePayload (request: PipelineBlockRequest) : Result<JVal, PageRequestError> =
        match Canonical.read request.Pipeline with
        | Error e -> Error(PageRequestError.InvalidField("payload.pipeline", e.Message))
        | Ok(JArr _ as steps) ->
            Ok(
                JObj [
                    "table", JStr request.TableId
                    "period_from", JStr(FactBrowseSlice.timestampText request.PeriodFrom)
                    "period_to", JStr(FactBrowseSlice.timestampText request.PeriodTo)
                    "path_prefix", texts request.PathPrefix
                    "metrics", texts request.Metrics
                    "pipeline", steps
                    "pipeline_hash", JStr request.PipelineHash
                    match request.RunStamp with
                    | Some stamp -> "run_stamp", JStr stamp
                    | None -> ()
                ]
            )
        | Ok _ -> Error(PageRequestError.InvalidField("payload.pipeline", "it is not a JSON array of steps"))

    /// The request as its canonical JSON value.
    let toJVal (request: PageRequest) : Result<JVal, PageRequestError> =
        let payload =
            match request with
            | PageRequest.Presentation(r, _) ->
                PresentationRequest.toJsonValue r
                |> ofJsonValue
                |> Result.mapError (fun reason -> PageRequestError.InvalidField("payload", reason))
            | PageRequest.Pipeline(r, _) -> pipelinePayload r

        payload
        |> Result.map (fun payload ->
            JObj [
                match dependsOn request with
                | [] -> ()
                | keys -> "dependsOn", texts keys
                "kind", JStr(kind request)
                "payload", payload
            ])

    /// The request's canonical text: one request, one text.
    let toJson (request: PageRequest) : Result<string, PageRequestError> =
        toJVal request
        |> Result.bind (fun v ->
            Canonical.tryWrite Rendering.CrossHost v
            |> Result.mapError (fun reason -> PageRequestError.InvalidField("payload", reason)))

    let private member' (name: string) (members: (string * JVal) list) : JVal option =
        members |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

    let private stringsOf (field: string) (v: JVal option) : Result<string list, PageRequestError> =
        match v with
        | None -> Ok []
        | Some(JArr items) ->
            let strings =
                items
                |> List.choose (function
                    | JStr s -> Some s
                    | _ -> None)

            if List.length strings = List.length items then
                Ok strings
            else
                Error(PageRequestError.InvalidField(field, "it is not an array of strings"))
        | Some _ -> Error(PageRequestError.InvalidField(field, "it is not an array of strings"))

    let private textOf (field: string) (v: JVal option) : Result<string, PageRequestError> =
        match v with
        | Some(JStr s) when not (String.IsNullOrWhiteSpace s) -> Ok s
        | Some(JStr _)
        | None -> Error(PageRequestError.MissingField field)
        | Some _ -> Error(PageRequestError.InvalidField(field, "it is not a string"))

    let private instantOf (field: string) (v: JVal option) : Result<DateTime, PageRequestError> =
        textOf field v
        |> Result.bind (fun text ->
            match PresentationDay.tryParse text with
            | Some day -> Ok day
            | None ->
                try
                    Ok(FactBrowseSlice.parseTimestamp text)
                with _ ->
                    Error(
                        PageRequestError.InvalidField(
                            field,
                            "it is not a day (yyyy-MM-dd) or a canonical instant (yyyy-MM-ddTHH:mm:ssZ)"
                        )
                    ))

    let private pipelineOf (payload: (string * JVal) list) : Result<PipelineBlockRequest, PageRequestError> =
        let pipeline =
            match member' "pipeline" payload with
            | Some(JArr _ as steps) ->
                Canonical.tryWrite Rendering.CrossHost steps
                |> Result.mapError (fun reason -> PageRequestError.InvalidField("payload.pipeline", reason))
            | Some _ -> Error(PageRequestError.InvalidField("payload.pipeline", "it is not a JSON array of steps"))
            | None -> Error(PageRequestError.MissingField "payload.pipeline")

        let runStamp =
            match member' "run_stamp" payload with
            | None -> Ok None
            | Some(JStr s) -> Ok(Some s)
            | Some _ -> Error(PageRequestError.InvalidField("payload.run_stamp", "it is not a string"))

        let pipelineHash =
            match member' "pipeline_hash" payload with
            | None -> Ok ""
            | Some(JStr s) -> Ok s
            | Some _ -> Error(PageRequestError.InvalidField("payload.pipeline_hash", "it is not a string"))

        match
            textOf "payload.table" (member' "table" payload),
            instantOf "payload.period_from" (member' "period_from" payload),
            instantOf "payload.period_to" (member' "period_to" payload),
            stringsOf "payload.path_prefix" (member' "path_prefix" payload),
            stringsOf "payload.metrics" (member' "metrics" payload)
        with
        | Ok table, Ok from, Ok until, Ok prefix, Ok metrics ->
            match pipeline, pipelineHash, runStamp with
            | Ok pipeline, Ok hash, Ok stamp ->
                Ok {
                    TableId = table
                    PeriodFrom = from
                    PeriodTo = until
                    PathPrefix = prefix
                    Metrics = metrics
                    Pipeline = pipeline
                    PipelineHash = hash
                    RunStamp = stamp
                }
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e

    /// The request a canonical JSON value says, or the refusal naming what
    /// to repair. Any member order is read; `toJVal` writes the canonical one.
    let ofJVal (v: JVal) : Result<PageRequest, PageRequestError> =
        match v with
        | JObj members ->
            match
                textOf "kind" (member' "kind" members),
                stringsOf "dependsOn" (member' "dependsOn" members),
                member' "payload" members
            with
            | Error e, _, _
            | _, Error e, _ -> Error e
            | _, _, None -> Error(PageRequestError.MissingField "payload")
            | Ok kind, Ok keys, Some payload ->
                match kind, payload with
                | PresentationKind, (JObj _ as payload) ->
                    PresentationRequest.ofJsonValue (toJsonValue payload)
                    |> Result.mapError PageRequestError.Presentation
                    |> Result.map (fun r -> PageRequest.Presentation(r, keys |> List.distinct |> List.sort))
                | PipelineKind, JObj payload ->
                    pipelineOf payload
                    |> Result.map (fun r -> PageRequest.Pipeline(r, keys |> List.distinct |> List.sort))
                | (PresentationKind | PipelineKind), _ ->
                    Error(PageRequestError.InvalidField("payload", "it is not a JSON object"))
                | other, _ -> Error(PageRequestError.UnknownKind other)
        | _ -> Error(PageRequestError.NotJson "the request is not a JSON object")

    /// The request a JSON text says, or the refusal naming what to repair.
    let ofJson (json: string) : Result<PageRequest, PageRequestError> =
        if String.IsNullOrWhiteSpace json then
            Error(PageRequestError.NotJson "the text is empty")
        else
            match Canonical.read json with
            | Error e -> Error(PageRequestError.NotJson e.Message)
            | Ok v -> ofJVal v

/// The canonical JSON of a resolution, and its table.
[<RequireQualifiedAccess>]
module PageResolution =

    /// The resolution as its canonical JSON value.
    let toJVal (resolution: PageResolution) : JVal =
        let texts (items: string list) = JArr(items |> List.map JStr)

        JObj [
            "kind", JStr resolution.Kind
            "table", JStr resolution.Table
            "rows", JInt resolution.Rows
            "columns", texts resolution.Columns
            "withheld",
            JArr(
                resolution.Withheld
                |> List.map (fun w -> JObj [ "policyRef", JStr w.PolicyRef; "count", JInt w.Count ])
            )
            "notes", texts resolution.Notes
            "factIds", texts resolution.FactIds
            match resolution.RunStamp with
            | Some stamp -> "runStamp", JStr stamp
            | None -> ()
        ]

    /// The resolution's canonical text: what every entry point answers, so
    /// two entry points that resolved one request answer the same bytes.
    let toJson (resolution: PageResolution) : string =
        Canonical.write Rendering.CrossHost (toJVal resolution)

    /// The withheld points counted over every policy.
    let withheldCount (resolution: PageResolution) : int =
        resolution.Withheld |> List.sumBy _.Count

    /// The resolution's table, decoded.
    let table (resolution: PageResolution) : Result<Table, string> =
        match ColumnCodec.decode resolution.Table with
        | Ok(Embedded table) -> Ok table
        | Ok(Ref name) -> Error(sprintf "the resolution names a source '%s' rather than carrying its rows" name)
        | Error e -> Error("the resolution's table could not be decoded: " + ColumnCodec.errorString e)