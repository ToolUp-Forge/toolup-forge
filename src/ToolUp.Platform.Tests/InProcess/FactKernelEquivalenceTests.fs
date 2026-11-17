// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The equivalence proof of forge's adoption of the published Fact kernel:
/// facts persisted by forge's own kernel, byte for byte as its store wrote
/// them, each with the id and lineage key that kernel minted, read back
/// through the store's reader with the id and the lineage key unchanged, and
/// with the lineage digest the Postgres store's `lineage_hash` column and the
/// blob store's index already hold. Stored data does not change.
module ToolUp.Platform.Tests.FactKernelEquivalenceTests

open System
open System.Text
open Expecto
open Fuaran.Facts
open ToolUp.Facts
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Tests.Contracts

type private Stored = {
    Name: string
    /// The fact as forge's store persisted it (the legacy saved shape).
    Saved: string
    /// The lineage key forge's kernel computed for it.
    LineageKey: string
}

/// One of each value kind, periods with and without a zone, every
/// disclosure label.
let private stored: Stored list = [
    {
        Name = "scalar"
        Saved =
            """{"FactId":"2035f6bff71dd60cfe15197b2392b640bd4eb11eeabcc7a125b26e90d607f018","Subject":{"Hierarchy":"area","Path":["west","harbour"]},"Metric":{"MetricRef":"throughput"},"Value":{"Scalar":1234.50},"Period":{"From":"2026-01-01T00:00:00.0000000Z","To":"2026-04-01T00:00:00.0000000Z","Label":"Q1-2026"},"AsOf":"2026-04-02T06:30:00.5000000Z","Method":{"Computed":["op.sum","v1","p0"]},"Evidence":{"ResultRef":null,"InputHashes":["sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","run:7"],"TriggerRef":null},"Confidence":{"Value":0.9,"Basis":null},"Supersedes":null,"Disclosure":"Surfaceable"}"""
        LineageKey =
            "area/west>harbour|throughput|2026-01-01T00:00:00.0000000Z..2026-04-01T00:00:00.0000000Z|computed:op.sum:v1:p0"
    }
    {
        Name = "scalar-no-zone"
        Saved =
            """{"FactId":"cb1f59f6b5f6b72df897c7e5c42827934cd50c90d3023bd6cc0eebbd0dd1b98d","Subject":{"Hierarchy":"area","Path":["west","harbour"]},"Metric":{"MetricRef":"throughput"},"Value":{"Scalar":-0.125},"Period":{"From":"2026-01-01T00:00:00.0000000","To":"2026-04-01T00:00:00.0000000","Label":"Q1-2026"},"AsOf":"2026-04-02T06:30:00.5000000","Method":{"HumanAsserted":"analyst-a"},"Evidence":{"ResultRef":null,"InputHashes":[],"TriggerRef":null},"Confidence":null,"Supersedes":null,"Disclosure":"Internal"}"""
        LineageKey =
            "area/west>harbour|throughput|2026-01-01T00:00:00.0000000Z..2026-04-01T00:00:00.0000000Z|asserted:analyst-a"
    }
    {
        Name = "interval"
        Saved =
            """{"FactId":"180dbe87e8fd01678eef989432d29bcaa18d69ef0dba8cba7333ecb23a8f2a75","Subject":{"Hierarchy":"area","Path":["west","harbour"]},"Metric":{"MetricRef":"throughput"},"Value":{"Interval":[1.0,2.50]},"Period":{"From":"2026-01-01T00:00:00.0000000Z","To":"2026-04-01T00:00:00.0000000Z","Label":"Q1-2026"},"AsOf":"2026-04-02T06:30:00.5000000Z","Method":{"HumanAsserted":"analyst-a"},"Evidence":{"ResultRef":null,"InputHashes":[],"TriggerRef":null},"Confidence":null,"Supersedes":null,"Disclosure":{"Restricted":"panel-terms"}}"""
        LineageKey =
            "area/west>harbour|throughput|2026-01-01T00:00:00.0000000Z..2026-04-01T00:00:00.0000000Z|asserted:analyst-a"
    }
    {
        Name = "distribution"
        Saved =
            """{"FactId":"18605e5b283f463c35925b473aafa54ca77baec29bd208b7f6528169843c0615","Subject":{"Hierarchy":"area","Path":["west","harbour"]},"Metric":{"MetricRef":"throughput"},"Value":{"Distribution":{"p10":1.10,"p90":3}},"Period":{"From":"2026-01-01T00:00:00.0000000Z","To":"2026-04-01T00:00:00.0000000Z","Label":"Q1-2026"},"AsOf":"2026-04-02T06:30:00.5000000Z","Method":{"HumanAsserted":"analyst-b"},"Evidence":{"ResultRef":null,"InputHashes":[],"TriggerRef":null},"Confidence":null,"Supersedes":null,"Disclosure":"Surfaceable"}"""
        LineageKey =
            "area/west>harbour|throughput|2026-01-01T00:00:00.0000000Z..2026-04-01T00:00:00.0000000Z|asserted:analyst-b"
    }
    {
        Name = "series"
        Saved =
            """{"FactId":"3fa9c35e4b502a914d7c2466e857eb4b82b195e48e8dfd2d6395059bec81d433","Subject":{"Hierarchy":"area","Path":["west","harbour"]},"Metric":{"MetricRef":"throughput"},"Value":{"Series":"objects/throughput@v12"},"Period":{"From":"2026-01-01T00:00:00.0000000Z","To":"2026-04-01T00:00:00.0000000Z","Label":"Q1-2026"},"AsOf":"2026-04-02T06:30:00.5000000Z","Method":{"HumanAsserted":"analyst-b"},"Evidence":{"ResultRef":null,"InputHashes":[],"TriggerRef":null},"Confidence":null,"Supersedes":null,"Disclosure":"Surfaceable"}"""
        LineageKey =
            "area/west>harbour|throughput|2026-01-01T00:00:00.0000000Z..2026-04-01T00:00:00.0000000Z|asserted:analyst-b"
    }
    {
        Name = "series-computed"
        Saved =
            """{"FactId":"8deb83f3d1871e59a01fa8b686987d7937c67eef0c63e44e68e87a83e49cf7a3","Subject":{"Hierarchy":"area","Path":["west","harbour"]},"Metric":{"MetricRef":"throughput"},"Value":{"Series":"objects/throughput@v12"},"Period":{"From":"2026-01-01T00:00:00.0000000Z","To":"2026-04-01T00:00:00.0000000Z","Label":"Q1-2026"},"AsOf":"2026-04-02T06:30:00.5000000Z","Method":{"Computed":["op","1","h"]},"Evidence":{"ResultRef":null,"InputHashes":["sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","run:7"],"TriggerRef":null},"Confidence":null,"Supersedes":null,"Disclosure":"Surfaceable"}"""
        LineageKey =
            "area/west>harbour|throughput|2026-01-01T00:00:00.0000000Z..2026-04-01T00:00:00.0000000Z|computed:op:1:h"
    }
    {
        Name = "categorical"
        Saved =
            """{"FactId":"50e8b1174d915a13a36b455319c6dc59818ab9ff40f3b2482b76e9cc5120674a","Subject":{"Hierarchy":"area","Path":["west","harbour"]},"Metric":{"MetricRef":"throughput"},"Value":{"Categorical":"red"},"Period":{"From":"2026-01-01T00:00:00.0000000Z","To":"2026-04-01T00:00:00.0000000Z","Label":"Q1-2026"},"AsOf":"2026-04-02T06:30:00.5000000Z","Method":{"Imported":"cert:9"},"Evidence":{"ResultRef":null,"InputHashes":[],"TriggerRef":null},"Confidence":null,"Supersedes":null,"Disclosure":"Surfaceable"}"""
        LineageKey =
            "area/west>harbour|throughput|2026-01-01T00:00:00.0000000Z..2026-04-01T00:00:00.0000000Z|imported:cert:9"
    }
    {
        Name = "absent"
        Saved =
            """{"FactId":"ffb421a562b58717692d1f688724a4a4c8add402fccf772feac9745daa7c51b5","Subject":{"Hierarchy":"area","Path":["west","harbour"]},"Metric":{"MetricRef":"throughput"},"Value":{"Absent":"no data loaded"},"Period":{"From":"2026-01-01T00:00:00.0000000Z","To":"2026-04-01T00:00:00.0000000Z","Label":"Q1-2026"},"AsOf":"2026-04-02T06:30:00.5000000Z","Method":{"Computed":["op","1",""]},"Evidence":{"ResultRef":null,"InputHashes":["sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","run:7"],"TriggerRef":null},"Confidence":null,"Supersedes":null,"Disclosure":"Surfaceable"}"""
        LineageKey =
            "area/west>harbour|throughput|2026-01-01T00:00:00.0000000Z..2026-04-01T00:00:00.0000000Z|computed:op:1:"
    }
    {
        Name = "scalar-revenue-no-zone"
        Saved =
            """{"FactId":"c56704e19d11f01c5b91150fedc1689f1d219f1fe7e633594509d88c8d5e928d","Subject":{"Hierarchy":"region","Path":["uk","north"]},"Metric":{"MetricRef":"revenue"},"Value":{"Scalar":1234.50},"Period":{"From":"2026-01-01T00:00:00.0000000","To":"2026-04-01T00:00:00.0000000","Label":"Q1"},"AsOf":"2026-04-02T00:00:00.0000000","Method":{"Computed":["op","1","h"]},"Evidence":{"ResultRef":null,"InputHashes":[],"TriggerRef":null},"Confidence":{"Value":0.9,"Basis":null},"Supersedes":null,"Disclosure":"Surfaceable"}"""
        LineageKey =
            "region/uk>north|revenue|2026-01-01T00:00:00.0000000Z..2026-04-01T00:00:00.0000000Z|computed:op:1:h"
    }
]

/// The id the saved fact states: the id forge's kernel minted.
let private statedId (saved: string) =
    let i = saved.IndexOf "\"FactId\":\"" + 10
    saved.Substring(i, 64)

let private read (s: Stored) : Fact =
    match FactStoreCodec.read s.Saved with
    | Ok f -> f
    | Error refusal -> failtestf "%s is refused: %s" s.Name (Refusal.describe refusal)

let tests =
    testList "Fact kernel — a fact forge stored keeps its id and its lineage" [
        test "every stored fact reads with the id forge minted, and that id recomputes" {
            for s in stored do
                let f = read s
                Expect.equal f.FactId (statedId s.Saved) s.Name
                Expect.equal (Fact.computeFor f) (statedId s.Saved) s.Name
        }

        test "every stored fact's lineage key and digest are the ones forge's stores index it by" {
            for s in stored do
                let f = read s
                Expect.equal (Fact.lineageKey f) s.LineageKey s.Name
                Expect.equal (Fact.lineageDigest f) (Sha256.hexOfUtf8 s.LineageKey) s.Name

                Expect.equal
                    (FactLineage.digestOf f.Subject f.Metric f.Period f.Method)
                    (Sha256.hexOfUtf8 s.LineageKey)
                    s.Name
        }

        test "a stored fact rewritten in the specification's encoding is the same fact" {
            for s in stored do
                let f = read s

                match FactWire.writeFact f with
                | Error refusal -> failtestf "%s does not write: %s" s.Name (Refusal.describe refusal)
                | Ok encoded ->
                    match FactStoreCodec.read encoded with
                    | Ok back -> Expect.equal back.FactId f.FactId s.Name
                    | Error refusal -> failtestf "%s does not read back: %s" s.Name (Refusal.describe refusal)
        }

        testAsync "the blob store reads a scope of stored facts as they stand, rewriting none" {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

            let store =
                BlobFactStore(
                    storage,
                    InMemoryEventStore.InMemoryEventStore(),
                    (fun () -> DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc))
                )

            for s in stored do
                let! uploaded =
                    storage.Upload(
                        "legacy",
                        sprintf "_facts/%s.json" (statedId s.Saved),
                        Encoding.UTF8.GetBytes s.Saved
                    )

                Expect.isOk uploaded s.Name

            let! export = store.ExportScope "legacy"
            Expect.isEmpty export.Unreadable "every stored fact reads"

            Expect.equal
                (export.Facts |> List.map _.FactId |> List.sort)
                (stored |> List.map (fun s -> statedId s.Saved) |> List.distinct |> List.sort)
                "the scope's facts, under the ids forge minted"

            for s in stored do
                let! bytes = storage.Download("legacy", sprintf "_facts/%s.json" (statedId s.Saved))

                match bytes with
                | Ok b ->
                    Expect.equal (Encoding.UTF8.GetString b) s.Saved (s.Name + ": nothing rewrote the stored fact")
                | Error e -> failtestf "%s: %s" s.Name e
        }
    ]