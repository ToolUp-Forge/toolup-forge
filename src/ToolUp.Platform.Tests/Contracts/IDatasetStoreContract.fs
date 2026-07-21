// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IDatasetStoreContract

open System
open Expecto
open ToolUp.Platform

// ─── Phase 448 — IDatasetStore conformance pack ─────────────────────────
//
// Bound to the blob-backed default (and any companion store / codec). Asserts
// the portable contract: rows + pre-encoded-content round-trip, version
// immutability (a new vintage never mutates the old), structural scope
// isolation (GP 4), deterministic paging, schema-mismatch rejection, panel
// role round-trip + queries, and `GetContentRef` for compute handoff.

let private t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

/// A small panel schema: unit / period / one plain feature / one target.
let private panelSchema: DatasetSchema = {
    Columns = [
        {
            Name = "region"
            DType = DatasetDType.Categorical
            Nullable = false
            Role = DatasetColumnRole.PanelUnit
        }
        {
            Name = "week"
            DType = DatasetDType.Timestamp
            Nullable = false
            Role = DatasetColumnRole.PanelPeriod
        }
        {
            Name = "spend"
            DType = DatasetDType.Float
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
        {
            Name = "sales"
            DType = DatasetDType.Float
            Nullable = true
            Role = DatasetColumnRole.Target
        }
    ]
}

let private row (region: string) (weekOffset: float) (spend: float) (sales: DatasetValue) : DatasetRow = {
    Cells = [
        DatasetValue.Categorical region
        DatasetValue.Timestamp(t0.AddDays(7.0 * weekOffset))
        DatasetValue.Float spend
        sales
    ]
}

let private sampleRows: DatasetRow list = [
    row "north" 0.0 100.0 (DatasetValue.Float 1000.0)
    row "north" 1.0 120.0 (DatasetValue.Float 1100.0)
    row "south" 0.0 80.0 (DatasetValue.Float 900.0)
    row "south" 1.0 90.0 DatasetValue.Null
]

let private okv =
    function
    | Ok v -> v
    | Error e -> failtestf "expected Ok; got %s" (DatasetError.describe e)

/// The contract pack, re-bindable to any store + codec pairing (Phase 598):
/// `codecFactory` must produce the same codec the store composes, so the
/// format-tag assertions and the `CreateFromContent` re-ingestion case run
/// against the composition under test.
let testsWithCodec (name: string) (codecFactory: unit -> IDatasetCodec) (factory: unit -> IDatasetStore) =
    let expectedFormat = codecFactory().Format

    testList $"{name} — IDatasetStore contract" [
        testCaseAsync "Create + ReadPage round-trips rows (typed)"
        <| async {
            let store = factory ()

            let! created =
                store.Create("scope-a", "sales-ds", panelSchema, sampleRows, "u1", Map.empty, StrictlyVersioned)

            let v = okv created
            Expect.equal v.Version 1 "first version is 1"
            Expect.equal v.RowCount 4L "row count recorded"
            Expect.equal v.Format expectedFormat "composed codec format tagged"

            let! page = store.ReadPage("scope-a", "sales-ds", 1, DatasetPageQuery.firstPage 100)
            let p = okv page
            Expect.equal p.TotalRows 4L "all rows match an empty filter"
            Expect.equal (List.length p.Rows) 4 "all four rows on the page"
            Expect.equal p.Rows sampleRows "rows round-trip byte-identical, in order"
            Expect.equal p.Schema panelSchema "schema round-trips (dtypes + roles)"
        }

        testCaseAsync "A second Create yields a new immutable version; the old one is intact (GP 5)"
        <| async {
            let store = factory ()
            let! _ = store.Create("scope-a", "ds", panelSchema, sampleRows, "u1", Map.empty, StrictlyVersioned)

            let changed = sampleRows @ [ row "east" 0.0 50.0 (DatasetValue.Float 500.0) ]
            let! second = store.Create("scope-a", "ds", panelSchema, changed, "u1", Map.empty, StrictlyVersioned)
            let v2 = okv second
            Expect.equal v2.Version 2 "second Create is version 2"
            Expect.equal v2.RowCount 5L "v2 has the changed row count"

            // v1 unchanged.
            let! p1 = store.ReadPage("scope-a", "ds", 1, DatasetPageQuery.firstPage 100)
            Expect.equal (okv p1).TotalRows 4L "v1 still has its original 4 rows"
            let! p2 = store.ReadPage("scope-a", "ds", 2, DatasetPageQuery.firstPage 100)
            Expect.equal (okv p2).TotalRows 5L "v2 has 5 rows"

            let! versions = store.ListVersions("scope-a", "ds")
            Expect.equal (versions |> List.map (fun v -> v.Version)) [ 1; 2 ] "both versions listed ascending"
        }

        testCaseAsync "StrictlyVersioned Delete is refused (immutable vintages)"
        <| async {
            let store = factory ()
            let! _ = store.Create("scope-a", "ds", panelSchema, sampleRows, "u1", Map.empty, StrictlyVersioned)

            match! store.Delete("scope-a", "ds") with
            | Error DatasetError.DeleteForbidden -> ()
            | other -> failtestf "expected DeleteForbidden; got %A" other

            // Still readable after the refused delete.
            let! got = store.GetLatest("scope-a", "ds")
            Expect.equal (okv got).Version 1 "dataset survives a refused delete"
        }

        testCaseAsync "Versioned Delete removes the dataset (policy-honouring)"
        <| async {
            let store = factory ()
            let! _ = store.Create("scope-a", "tmp", panelSchema, sampleRows, "u1", Map.empty, Versioned)

            match! store.Delete("scope-a", "tmp") with
            | Ok() -> ()
            | other -> failtestf "expected Ok; got %A" other

            match! store.GetLatest("scope-a", "tmp") with
            | Error DatasetError.NotFound -> ()
            | other -> failtestf "expected NotFound after delete; got %A" other
        }

        testCaseAsync "Scope isolation — one scope never reads another's dataset (GP 4)"
        <| async {
            let store = factory ()
            let! _ = store.Create("scope-a", "ds", panelSchema, sampleRows, "u1", Map.empty, StrictlyVersioned)

            let! _ =
                store.Create(
                    "scope-b",
                    "ds",
                    panelSchema,
                    [ row "z" 0.0 1.0 (DatasetValue.Float 9.0) ],
                    "u2",
                    Map.empty,
                    StrictlyVersioned
                )

            let! aVersions = store.ListVersions("scope-a", "ds")
            Expect.equal (aVersions |> List.head |> (fun v -> v.RowCount)) 4L "scope A sees its own 4-row dataset"

            let! bPage = store.ReadPage("scope-b", "ds", 1, DatasetPageQuery.firstPage 100)
            Expect.equal (okv bPage).TotalRows 1L "scope B sees only its own 1-row dataset"

            // scope-a cannot see a dataset id that only exists in scope-b (and vice versa is symmetric).
            let! missing = store.GetLatest("scope-c", "ds")

            match missing with
            | Error DatasetError.NotFound -> ()
            | other -> failtestf "expected NotFound in an empty scope; got %A" other
        }

        testCaseAsync "Paging is deterministic — disjoint offset windows tile the row order"
        <| async {
            let store = factory ()
            let! _ = store.Create("scope-a", "ds", panelSchema, sampleRows, "u1", Map.empty, StrictlyVersioned)

            let! page1 = store.ReadPage("scope-a", "ds", 1, { Offset = 0L; Limit = 2; Filters = [] })
            let! page2 = store.ReadPage("scope-a", "ds", 1, { Offset = 2L; Limit = 2; Filters = [] })
            let p1 = okv page1
            let p2 = okv page2
            Expect.equal p1.TotalRows 4L "total is stable across pages"
            Expect.equal p2.TotalRows 4L "total is stable across pages"
            Expect.equal (p1.Rows @ p2.Rows) sampleRows "the two windows reconstruct the full order"

            // Re-reading the same window gives the identical result (determinism).
            let! page1' = store.ReadPage("scope-a", "ds", 1, { Offset = 0L; Limit = 2; Filters = [] })
            Expect.equal (okv page1').Rows p1.Rows "same window, same rows on re-read"

            // Offset past the end yields an empty page, not an error.
            let! beyond =
                store.ReadPage(
                    "scope-a",
                    "ds",
                    1,
                    {
                        Offset = 100L
                        Limit = 2
                        Filters = []
                    }
                )

            Expect.isEmpty (okv beyond).Rows "offset past the end is an empty page"
        }

        testCaseAsync "Filters select rows (typed, AND-combined)"
        <| async {
            let store = factory ()
            let! _ = store.Create("scope-a", "ds", panelSchema, sampleRows, "u1", Map.empty, StrictlyVersioned)

            let! northPage =
                store.ReadPage(
                    "scope-a",
                    "ds",
                    1,
                    {
                        Offset = 0L
                        Limit = 100
                        Filters = [
                            {
                                Column = "region"
                                Op = DatasetFilterOp.Eq
                                Value = DatasetValue.Categorical "north"
                            }
                        ]
                    }
                )

            Expect.equal (okv northPage).TotalRows 2L "two north rows"

            let! bigSpend =
                store.ReadPage(
                    "scope-a",
                    "ds",
                    1,
                    {
                        Offset = 0L
                        Limit = 100
                        Filters = [
                            {
                                Column = "spend"
                                Op = DatasetFilterOp.Gte
                                Value = DatasetValue.Float 100.0
                            }
                        ]
                    }
                )

            Expect.equal (okv bigSpend).TotalRows 2L "two rows with spend >= 100"
        }

        testCaseAsync "Schema-mismatch rows are rejected at Create"
        <| async {
            let store = factory ()
            // Wrong dtype in the 'spend' (Float) column.
            let badRow: DatasetRow = {
                Cells = [
                    DatasetValue.Categorical "north"
                    DatasetValue.Timestamp t0
                    DatasetValue.Text "not-a-float"
                    DatasetValue.Float 1.0
                ]
            }

            match! store.Create("scope-a", "bad", panelSchema, [ badRow ], "u1", Map.empty, StrictlyVersioned) with
            | Error(DatasetError.SchemaMismatch _) -> ()
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        testCaseAsync "A filter whose literal dtype mismatches its column is rejected"
        <| async {
            let store = factory ()
            let! _ = store.Create("scope-a", "ds", panelSchema, sampleRows, "u1", Map.empty, StrictlyVersioned)

            let! result =
                store.ReadPage(
                    "scope-a",
                    "ds",
                    1,
                    {
                        Offset = 0L
                        Limit = 100
                        Filters = [
                            {
                                Column = "spend"
                                Op = DatasetFilterOp.Gt
                                Value = DatasetValue.Text "oops"
                            }
                        ]
                    }
                )

            match result with
            | Error(DatasetError.SchemaMismatch _) -> ()
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        testCaseAsync "Panel roles round-trip and are queryable (D8)"
        <| async {
            let store = factory ()
            let! created = store.Create("scope-a", "ds", panelSchema, sampleRows, "u1", Map.empty, StrictlyVersioned)
            let schema = (okv created).Schema

            Expect.equal
                (DatasetSchema.panelUnit schema |> Option.map (fun c -> c.Name))
                (Some "region")
                "PanelUnit role round-trips"

            Expect.equal
                (DatasetSchema.panelPeriod schema |> Option.map (fun c -> c.Name))
                (Some "week")
                "PanelPeriod role round-trips"

            Expect.equal
                (DatasetSchema.targets schema |> List.map (fun c -> c.Name))
                [ "sales" ]
                "Target role round-trips"
        }

        testCaseAsync "CreateFromContent round-trips pre-encoded bytes; GetContentRef hands off the vintage"
        <| async {
            let store = factory ()
            // Encode via the composed codec, then re-ingest as content.
            let codec = codecFactory ()
            let content = codec.Encode(panelSchema, sampleRows)

            let! created =
                store.CreateFromContent(
                    "scope-a",
                    "ext",
                    panelSchema,
                    content,
                    codec.Format,
                    int64 (List.length sampleRows),
                    "u1",
                    Map.empty,
                    StrictlyVersioned
                )

            let v = okv created
            Expect.equal v.RowCount 4L "declared row count recorded"

            let! page = store.ReadPage("scope-a", "ext", 1, DatasetPageQuery.firstPage 100)
            Expect.equal (okv page).Rows sampleRows "pre-encoded content reads back as typed rows"

            let! contentRef = store.GetContentRef("scope-a", "ext", 1)
            let r = okv contentRef
            Expect.equal r.Format codec.Format "content ref carries the honest format tag"
            Expect.equal r.RowCount 4L "content ref carries the row count"
            Expect.isTrue (r.ContentHash.Length > 0) "content ref carries a blob hash for handoff"
        }

        testCaseAsync "GetVersion / ReadPage on a missing dataset or version report typed errors"
        <| async {
            let store = factory ()

            match! store.GetVersion("scope-a", "nope", 1) with
            | Error DatasetError.NotFound -> ()
            | other -> failtestf "expected NotFound; got %A" other

            let! _ = store.Create("scope-a", "ds", panelSchema, sampleRows, "u1", Map.empty, StrictlyVersioned)

            match! store.GetVersion("scope-a", "ds", 7) with
            | Error(DatasetError.VersionNotFound 7) -> ()
            | other -> failtestf "expected VersionNotFound 7; got %A" other

            match!
                store.ReadPage(
                    "scope-a",
                    "ds",
                    1,
                    {
                        Offset = -1L
                        Limit = 10
                        Filters = []
                    }
                )
            with
            | Error(DatasetError.InvalidPage _) -> ()
            | other -> failtestf "expected InvalidPage; got %A" other
        }
    ]

/// The contract pack bound to the default JSON-frame codec composition.
let tests (name: string) (factory: unit -> IDatasetStore) =
    testsWithCodec name (fun () -> JsonFrameDatasetCodec() :> IDatasetCodec) factory