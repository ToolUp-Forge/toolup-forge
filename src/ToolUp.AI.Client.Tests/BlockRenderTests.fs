// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1156 — the conversation panel draws a pipeline block: the persisted
/// request computed again for the reader, drawn as a grid with the withheld
/// columns named, and a note for each way the reader's result differs from
/// the one the part recorded. Nothing is drawn from the stored node.
module ToolUp.AI.Client.Tests.BlockRenderTests

open System
open Fable.Core
open Feliz
open Fuaran.Core
open ToolUp.Platform.AI
open ToolUp.Facts
open ToolUp.Facts.Presentation
open ToolUp.AI.Client
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.AI.Client.Tests.RenderScope

[<Import("renderToStaticMarkup", from = "react-dom/server")>]
let private renderToStaticMarkup (element: ReactElement) : string = jsNative

// ─── Fixtures ────────────────────────────────────────────────────────

let private column (name: string) (ty: ColumnType) (cells: Cell list) : Field * Column =
    match Column.ofCells name ty cells with
    | Ok c -> Field.create name ty, c
    | Error e -> failwithf "column %s: %A" name e

/// A result table of `subject` and `total`, encoded as the re-run carries it.
let private encoded (totals: Cell list) : string =
    let pairs = [
        column "subject" StringType [ Cell.Str "north>s1"; Cell.Str "north>s2" ]
        column "total" FloatType totals
    ]

    match
        FactBrowseSlice.encode {
            Schema = pairs |> List.map fst
            Columns = pairs |> List.map snd
        }
    with
    | Ok table -> table
    | Error e -> failwith e

let private request: PipelineBlockRequest = {
    TableId = "daily-trade"
    PeriodFrom = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    PeriodTo = DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)
    PathPrefix = []
    Metrics = [ "revenue" ]
    Pipeline = """[{"$type":"GroupBy","aggs":[{"fn":"Sum","name":"total","of":"revenue"}],"keys":["subject"]}]"""
    PipelineHash = "a1b2c3d4e5f60718293a"
    RunStamp = Some "run-1"
}

/// What the writer saw: both totals, every revenue fact, nothing withheld.
let private written: PipelineBlockResult = {
    Table = encoded [ Cell.Float 3301.5; Cell.Float 2402.5 ]
    Rows = 2
    Columns = [ "subject"; "total" ]
    Withheld = []
    PipelineHash = request.PipelineHash
    RunStamp = request.RunStamp
    FactIds = [ "f1"; "f2"; "f3"; "f4" ]
    FactIdCount = 4
}

/// What a reader the gate withholds one revenue fact from sees, after a run
/// committed since: the total withheld, one fact fewer, a newer stamp.
let private restricted: PipelineBlockResult = {
    written with
        Table = encoded [ Cell.Null; Cell.Null ]
        Withheld = [
            {
                Column = "total"
                Metrics = [ "revenue" ]
                Withheld = [
                    {
                        PolicyRef = "trade-figures"
                        Count = 1
                    }
                ]
            }
        ]
        RunStamp = Some "run-2"
        FactIds = [ "f1"; "f2"; "f3" ]
        FactIdCount = 3
}

let private stored: BlockProvenance = PipelineBlock.provenance request written

/// A resolver answering `result`, recording the request it was sent.
let private fakeResolver (sent: ResizeArray<string>) (result: Result<PipelineBlockResult, string>) =
    fun (requestJson: string) -> async {
        sent.Add requestJson
        return result
    }

let private resolveNow (read: BlockRender.BlockPipelineResolver) : BlockRender.BlockOutcome =
    let mutable result = None

    Async.StartImmediate(
        async {
            let! r = BlockRender.resolvePipeline read stored
            result <- Some r
        }
    )

    match result with
    | Some r -> r
    | None -> failwith "the fake resolver did not answer synchronously"

let private markupOf (outcome: BlockRender.BlockOutcome) : string =
    installDom "block-1156" |> ignore
    renderToStaticMarkup (BlockRender.view stored outcome)

// ─── The cases ───────────────────────────────────────────────────────

let tests =
    testList "Conversation pipeline block (Phase 1156)" [

        testCase "the stored request is sent verbatim and the reader's result is drawn as a grid"
        <| fun () ->
            let sent = ResizeArray()

            match resolveNow (fakeResolver sent (Ok written)) with
            | BlockRender.BlockOutcome.Computed(block, differences) as outcome ->
                Expect.equal (List.ofSeq sent) [ stored.Request ] "the part's request, verbatim"
                Expect.equal differences [] "the writer's own view: nothing differs"
                Expect.equal block.Provenance.Request stored.Request "the drawn block keeps the request"

                let markup = markupOf outcome
                Expect.isTrue (markup.Contains "<table") "a grid"
                Expect.isTrue (markup.Contains "north&gt;s2") "its keys"
                Expect.isTrue (markup.Contains "3301.5") "and the reader's figures"
                Expect.isFalse (markup.Contains "data-block-recomputed") "with no difference note"
            | other -> failwithf "expected a computed block, got %A" other

        testCase "a re-run that differs from the recorded one is drawn as the reader's, with a note saying so"
        <| fun () ->
            match resolveNow (fakeResolver (ResizeArray()) (Ok restricted)) with
            | BlockRender.BlockOutcome.Computed(_, differences) as outcome ->
                Expect.equal
                    differences
                    [
                        "A run has been committed to the table since this result was written, so it is computed over the newer facts."
                        "More is withheld from you than from the writer: 1 withheld cell read now, against 0 when this result was written."
                        "It reads different facts for you: 1 of the facts it was written with is not read now, and 0 are read now that were not."
                    ]
                    "each difference, word for word"

                let markup = markupOf outcome
                Expect.isTrue (markup.Contains "data-block-recomputed") "the note is shown"
                Expect.isTrue (markup.Contains "is withheld: it reads revenue") "the withheld column is named"

                Expect.isTrue
                    (markup.Contains "1 point withheld under the trade-figures disclosure policy")
                    "the furniture reports the reader's withheld cells"

                Expect.isFalse (markup.Contains "3301.5") "the writer's figure is not drawn"
            | other -> failwithf "expected a computed block, got %A" other

        testCase "a refused re-run is a placeholder naming the refusal"
        <| fun () ->
            match
                resolveNow (
                    fakeResolver (ResizeArray()) (Error "no cell of the slice carries a value you may be shown")
                )
            with
            | BlockRender.BlockOutcome.Refused reason as outcome ->
                Expect.equal
                    reason
                    "The pipeline could not be computed for you: no cell of the slice carries a value you may be shown"
                    "the refusal, named"

                Expect.isTrue ((markupOf outcome).Contains "data-block-placeholder") "a placeholder, no grid"
            | other -> failwithf "expected a refusal, got %A" other

        testCase "a resolver that throws is a refusal, never a broken panel"
        <| fun () ->
            let throwing: BlockRender.BlockPipelineResolver =
                fun _ -> async { return failwith "offline" }

            match resolveNow throwing with
            | BlockRender.BlockOutcome.Refused reason ->
                Expect.equal reason "The pipeline could not be computed for you: offline" "the exception's message"
            | other -> failwithf "expected a refusal, got %A" other

        testCase "the installed pipeline re-run is the server's until a test installs its own"
        <| fun () ->
            let fake = fakeResolver (ResizeArray()) (Ok written)

            Expect.isTrue
                (obj.ReferenceEquals(BlockRender.currentPipeline (), BlockRender.remotePipelineResolver))
                "the default"

            try
                BlockRender.registerPipelineResolver fake
                Expect.isTrue (obj.ReferenceEquals(BlockRender.currentPipeline (), fake)) "the installed one"
            finally
                BlockRender.registerPipelineResolver BlockRender.remotePipelineResolver
    ]