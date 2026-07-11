// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DatasetStoreTests

open System
open System.IO
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.Tests.Contracts

// ─── Phase 448 — IDatasetStore conformance ──────────────────────────────
//
// Binds the `IDatasetStoreContract` pack to the blob-backed `BlobDatasetStore`
// over a `DataObjectStore` over `LocalFileStorage` — the full default stack.
// Each factory call gets a fresh temp directory so tests never share state; a
// Parquet companion codec would run the same pack via `createWithCodec`.
//
// The `Ranged-read paging` list below covers the 448.D-verdict-#3 follow-on
// (streamed `ReadPage` over Phase 455 bounded ranged reads): a range-decodable
// test codec + an instrumented store prove that the streamed path pages
// without downloading the whole content blob, matches the whole-blob path
// result-for-result, and falls back to it when ranged reads are refused (the
// encryption decorator's posture).

let private freshDir () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-ds-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    tempDir

let private baseDataObjects () =
    let blob = LocalFileStorage.LocalFileStorage(freshDir ()) :> IBlobStorage
    DataObjectStore(blob) :> IDataObjectStore

// ─── Streaming test codec ────────────────────────────────────────────────
//
// A deliberately simple range-decodable wire: big-endian length-prefixed JSON
// frames — `[4-byte schema length][schema JSON]` then, per row,
// `[4-byte row length][row JSON]`. `DecodeChunk` re-reads the schema header on
// every call (the codec is stateless between calls — rule 4; the resume state
// is the byte-offset cursor) and then decodes at most `rowsPerChunk` rows,
// each via bounded range reads. Small `rowsPerChunk` values force genuinely
// multi-chunk paging in the tests.

let private writeLen (n: int) : byte[] = [| byte (n >>> 24); byte (n >>> 16); byte (n >>> 8); byte n |]

let private readLen (b: byte[]) (i: int) : int =
    (int b[i] <<< 24)
    ||| (int b[i + 1] <<< 16)
    ||| (int b[i + 2] <<< 8)
    ||| int b[i + 3]

type private ChunkedJsonCodec(rowsPerChunk: int) =
    static let jsonOptions = FableConverters.create ()

    let frame (payload: byte[]) =
        Array.append (writeLen payload.Length) payload

    interface IDatasetCodec with
        member _.Format = "toolup-test-chunked-v1"

        member _.Encode(schema: DatasetSchema, rows: DatasetRow list) : byte[] =
            let head = frame (JsonSerializer.SerializeToUtf8Bytes(schema, jsonOptions))

            let body =
                rows
                |> List.map (fun r -> frame (JsonSerializer.SerializeToUtf8Bytes(r, jsonOptions)))

            Array.concat (head :: body)

        member _.Decode(content: byte[]) : Result<DatasetSchema * DatasetRow list, string> =
            try
                let schemaLen = readLen content 0

                let schema =
                    JsonSerializer.Deserialize<DatasetSchema>(Array.sub content 4 schemaLen, jsonOptions)

                let rows = ResizeArray<DatasetRow>()
                let mutable pos = 4 + schemaLen

                while pos < content.Length do
                    let rowLen = readLen content pos

                    rows.Add(JsonSerializer.Deserialize<DatasetRow>(Array.sub content (pos + 4) rowLen, jsonOptions))

                    pos <- pos + 4 + rowLen

                Ok(schema, List.ofSeq rows)
            with ex ->
                Error ex.Message

    interface IStreamingDatasetCodec with
        member _.DecodeChunk(readRange, size, cursor) = async {
            match! readRange 0L 4 with
            | Error e -> return Error e
            | Ok lenBytes when lenBytes.Length < 4 -> return Error "chunked frame: truncated schema length"
            | Ok lenBytes ->
                let schemaLen = readLen lenBytes 0

                match! readRange 4L schemaLen with
                | Error e -> return Error e
                | Ok schemaBytes ->
                    try
                        let schema = JsonSerializer.Deserialize<DatasetSchema>(schemaBytes, jsonOptions)

                        let start =
                            match cursor with
                            | Some c -> int64 c
                            | None -> int64 (4 + schemaLen)

                        let rec readRows (pos: int64) (acc: DatasetRow list) (count: int) = async {
                            if count >= rowsPerChunk || pos >= size then
                                let next = if pos >= size then None else Some(string pos)
                                return Ok(List.rev acc, next)
                            else
                                match! readRange pos 4 with
                                | Error e -> return Error e
                                | Ok lb when lb.Length < 4 -> return Error "chunked frame: truncated row length"
                                | Ok lb ->
                                    let rowLen = readLen lb 0

                                    match! readRange (pos + 4L) rowLen with
                                    | Error e -> return Error e
                                    | Ok rb when rb.Length < rowLen -> return Error "chunked frame: truncated row"
                                    | Ok rb ->
                                        let row = JsonSerializer.Deserialize<DatasetRow>(rb, jsonOptions)

                                        return! readRows (pos + 4L + int64 rowLen) (row :: acc) (count + 1)
                        }

                        match! readRows start [] 0 with
                        | Error e -> return Error e
                        | Ok(rows, next) -> return Ok(schema, rows, next)
                    with ex ->
                        return Error ex.Message
        }

// ─── Instrumented store ──────────────────────────────────────────────────
//
// Delegates to a real `DataObjectStore` while counting whole-blob content
// reads vs ranged reads, and optionally refusing the ranged surface — the
// size probe (a store with no range support at all) or the range read itself
// (the `EncryptedBlobStorage` posture: metadata works, mid-blob ciphertext
// ranges are refused).

type private InstrumentedDataObjectStore(inner: IDataObjectStore, refuseSize: bool, refuseRange: bool) =
    let innerRanged = inner :?> IContentRangeReader
    let mutable contentReads = 0
    let mutable rangeReads = 0

    member _.ContentReads = contentReads
    member _.RangeReads = rangeReads

    interface IDataObjectStore with
        member _.Save(a, b, c, d, e, f, g) = inner.Save(a, b, c, d, e, f, g)
        member _.Get(a, b) = inner.Get(a, b)
        member _.GetVersion(a, b, c) = inner.GetVersion(a, b, c)

        member _.GetContent(a, b) =
            contentReads <- contentReads + 1
            inner.GetContent(a, b)

        member _.ListVersions(a, b) = inner.ListVersions(a, b)
        member _.ListObjects(a) = inner.ListObjects(a)
        member _.Recover(a, b, c, d) = inner.Recover(a, b, c, d)
        member _.Delete(a, b) = inner.Delete(a, b)
        member _.Evict(a, b) = inner.Evict(a, b)
        member _.Purge(a) = inner.Purge(a)
        member _.Erase(a, b, c, d) = inner.Erase(a, b, c, d)

    interface IContentRangeReader with
        member _.GetContentSize(a, b) =
            if refuseSize then
                async { return Error(DataObjectError.StorageFailure "ranged reads refused (test double)") }
            else
                innerRanged.GetContentSize(a, b)

        member _.ReadContentRange(a, b, c, d) =
            if refuseRange then
                async { return Error(DataObjectError.StorageFailure "ranged reads refused (test double)") }
            else
                rangeReads <- rangeReads + 1
                innerRanged.ReadContentRange(a, b, c, d)

// ─── Ranged-read paging tests ────────────────────────────────────────────

let private schema: DatasetSchema = {
    Columns = [
        {
            Name = "name"
            DType = DatasetDType.Text
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
        {
            Name = "value"
            DType = DatasetDType.Float
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
    ]
}

let private mkRow (i: int) : DatasetRow = {
    Cells = [ DatasetValue.Text $"row-{i}"; DatasetValue.Float(float i * 10.0) ]
}

let private nineRows = List.init 9 mkRow

let private okv =
    function
    | Ok v -> v
    | Error e -> failtestf "expected Ok; got %s" (DatasetError.describe e)

/// Create the same nine-row dataset in a fresh store built by `mkStore`.
let private seeded (mkStore: IDataObjectStore -> IDatasetStore) (dataObjects: IDataObjectStore) = async {
    let store = mkStore dataObjects
    let! created = store.Create("scope-a", "ds", schema, nineRows, "u1", Map.empty, StrictlyVersioned)
    okv created |> ignore
    return store
}

let private rangedReadTests =
    testList "Ranged-read paging (448.D #3)" [
        testCaseAsync "streamed ReadPage pages without downloading the whole content blob"
        <| async {
            let instr = InstrumentedDataObjectStore(baseDataObjects (), false, false)

            let! store = seeded (fun d -> BlobDatasetStore.createWithCodec d (ChunkedJsonCodec(2))) instr

            let! page = store.ReadPage("scope-a", "ds", 1, { Offset = 3L; Limit = 2; Filters = [] })
            let p = okv page
            Expect.equal p.Rows [ mkRow 3; mkRow 4 ] "the offset window's rows come back typed"
            Expect.equal p.TotalRows 9L "unfiltered total comes from the row-count sidecar"
            Expect.equal p.Schema schema "the page is self-describing"
            Expect.equal instr.ContentReads 0 "the whole content blob is never downloaded"
            Expect.isGreaterThan instr.RangeReads 0 "the page is served by bounded ranged reads"
        }

        testCaseAsync "streamed ReadPage matches the whole-blob path result-for-result"
        <| async {
            let! wholeBlob = seeded BlobDatasetStore.create (baseDataObjects ())

            let! streamed =
                seeded (fun d -> BlobDatasetStore.createWithCodec d (ChunkedJsonCodec(2))) (baseDataObjects ())

            let queries: DatasetPageQuery list = [
                DatasetPageQuery.firstPage 100
                { Offset = 0L; Limit = 2; Filters = [] }
                { Offset = 2L; Limit = 3; Filters = [] }
                { Offset = 8L; Limit = 5; Filters = [] }
                {
                    Offset = 100L
                    Limit = 2
                    Filters = []
                }
                {
                    Offset = 0L
                    Limit = 100
                    Filters = [
                        {
                            Column = "value"
                            Op = DatasetFilterOp.Gte
                            Value = DatasetValue.Float 50.0
                        }
                    ]
                }
                {
                    Offset = 1L
                    Limit = 2
                    Filters = [
                        {
                            Column = "value"
                            Op = DatasetFilterOp.Gte
                            Value = DatasetValue.Float 50.0
                        }
                    ]
                }
            ]

            for query in queries do
                let! expected = wholeBlob.ReadPage("scope-a", "ds", 1, query)
                let! actual = streamed.ReadPage("scope-a", "ds", 1, query)
                Expect.equal (okv actual) (okv expected) $"streamed page equals whole-blob page for %A{query}"

            // A filter that does not fit the schema errors identically on both
            // paths — it is a semantic error, not a fallback trigger.
            let badFilter = {
                Offset = 0L
                Limit = 10
                Filters = [
                    {
                        Column = "value"
                        Op = DatasetFilterOp.Gt
                        Value = DatasetValue.Text "oops"
                    }
                ]
            }

            let! expectedErr = wholeBlob.ReadPage("scope-a", "ds", 1, badFilter)
            let! actualErr = streamed.ReadPage("scope-a", "ds", 1, badFilter)

            match expectedErr, actualErr with
            | Error(DatasetError.SchemaMismatch _), Error(DatasetError.SchemaMismatch _) -> ()
            | other -> failtestf "expected SchemaMismatch on both paths; got %A" other
        }

        testCaseAsync "refused range reads fall back to the whole-blob path (encryption posture)"
        <| async {
            // The EncryptedBlobStorage shape: the size probe works (ciphertext
            // metadata) but the mid-blob range read is refused.
            let instr = InstrumentedDataObjectStore(baseDataObjects (), false, true)

            let! store = seeded (fun d -> BlobDatasetStore.createWithCodec d (ChunkedJsonCodec(2))) instr

            let! page = store.ReadPage("scope-a", "ds", 1, { Offset = 3L; Limit = 2; Filters = [] })
            let p = okv page
            Expect.equal p.Rows [ mkRow 3; mkRow 4 ] "the fallback serves the identical window"
            Expect.equal p.TotalRows 9L "the fallback computes the identical total"
            Expect.isGreaterThan instr.ContentReads 0 "the fallback downloaded the whole blob"
        }

        testCaseAsync "a refused size probe falls back to the whole-blob path"
        <| async {
            let instr = InstrumentedDataObjectStore(baseDataObjects (), true, true)

            let! store = seeded (fun d -> BlobDatasetStore.createWithCodec d (ChunkedJsonCodec(2))) instr

            let! page = store.ReadPage("scope-a", "ds", 1, DatasetPageQuery.firstPage 100)
            let p = okv page
            Expect.equal p.Rows nineRows "all rows via the fallback"
            Expect.equal p.TotalRows 9L "total via the fallback"
            Expect.isGreaterThan instr.ContentReads 0 "the fallback downloaded the whole blob"
        }

        testCaseAsync "DataObjectStore serves bounded content ranges by content hash"
        <| async {
            let dataObjects = baseDataObjects ()
            let ranged = dataObjects :?> IContentRangeReader
            let content = Array.init 100 byte

            let! saved = dataObjects.Save("scope-a", "obj", content, "test.bytes", "u1", Map.empty, Versioned)

            let hash =
                match saved with
                | Ok d -> d.ContentHash
                | Error e -> failtestf "save failed: %A" e

            match! ranged.GetContentSize("scope-a", hash) with
            | Ok size -> Expect.equal size 100L "size probe reports the content length"
            | Error e -> failtestf "size probe failed: %A" e

            let! first = ranged.ReadContentRange("scope-a", hash, 0L, 40)
            let! second = ranged.ReadContentRange("scope-a", hash, 40L, 60)
            let! overshoot = ranged.ReadContentRange("scope-a", hash, 40L, 1000)
            let! pastEof = ranged.ReadContentRange("scope-a", hash, 100L, 10)

            match first, second, overshoot, pastEof with
            | Ok a, Ok b, Ok o, Ok p ->
                Expect.equal (Array.append a b) content "concatenated ranges byte-equal the content"
                Expect.equal o b "an overshooting length clamps at EOF"
                Expect.isEmpty p "a past-EOF offset reads empty, not an error"
            | other -> failtestf "expected Ok ranges; got %A" other
        }
    ]

let tests =
    let factory () : IDatasetStore =
        BlobDatasetStore.create (baseDataObjects ())

    testList "DatasetStore" [
        IDatasetStoreContract.tests "BlobDatasetStore (blob-backed, JSON-frame codec)" factory
        rangedReadTests
    ]