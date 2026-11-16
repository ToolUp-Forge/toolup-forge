// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Forge's fact store and identity certified against the Fact wire
/// specification's conformance corpus (`wire-fixtures/` of the public
/// `fuaran-fact-specification` repository).
///
/// The kernel package certifies its own codec and laws against the same
/// corpus. What this leg certifies is forge's side of it: that the ids and
/// lineage digests forge derives (through `FactLineage` as well as through a
/// fact) are the specification's, that a fact the store persists reads back
/// through the store's own read path as the same fact, and that the store's
/// reader (`FactStoreCodec.read`, which also admits the legacy saved shape)
/// refuses every document the specification refuses, for the same class.
///
/// **The corpus is a build input.** `TOOLUP_FACT_SPEC_DIR` names a checkout
/// (the repository, or its `wire-fixtures/`). With it unset, the corpus is
/// searched for within three directory levels of this checkout and of the
/// directories enclosing its main working tree, identified by its manifest
/// declaring specification `fact-wire`. An absent corpus is one loud
/// failure, never a skip: a conformance run without its corpus certifies
/// nothing.
module ToolUp.Platform.Tests.FactSpecConformance

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Expecto
open Fuaran.Facts
open ToolUp.Facts
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Support

/// The corpus revision this leg was verified against, and the SHA-256 of
/// that revision's manifest. A corpus that moved fails the pin case by name;
/// moving the pin is a deliberate re-certification.
[<RequireQualifiedAccess>]
module Pin =
    let commit = "1b8ca93b291c6d01a23125391f5ac8d6d2f1e8d1"

    let manifestDigest =
        "c2e5cf6105e7a2ceddaf1d6caaf494f539e7264d2e2b7aa578caff51da4932e9"

    let specification = "fact-wire"
    let wireVersion = 1

[<RequireQualifiedAccess>]
module private Corpus =

    let private envVar = "TOOLUP_FACT_SPEC_DIR"

    let private pruned =
        HashSet<string>(
            [
                ".git"
                ".vs"
                ".idea"
                "bin"
                "obj"
                "node_modules"
                "artifacts"
                "output"
                "dist"
                "packages"
            ],
            StringComparer.OrdinalIgnoreCase
        )

    /// bin/<Config>/net10.0/ToolUp.Platform.Tests.dll -> up 5.
    let private repoRoot () =
        let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    let private isCorpusDir (dir: string) =
        let manifest = Path.Combine(dir, "manifest.json")

        File.Exists manifest
        && (try
                use doc = JsonDocument.Parse(File.ReadAllBytes manifest)

                match doc.RootElement.TryGetProperty "specification" with
                | true, v -> v.ValueKind = JsonValueKind.String && v.GetString() = Pin.specification
                | _ -> false
            with _ ->
                false)

    let private asFixtureDir (dir: string) =
        if isCorpusDir dir then
            Some dir
        else
            let nested = Path.Combine(dir, "wire-fixtures")
            if isCorpusDir nested then Some nested else None

    let private anchoring = lazy (CorpusAnchor.resolve (repoRoot ()))

    let private searchRoots () =
        let anchor = anchoring.Value.Anchor

        [
            repoRoot ()
            anchor
            Path.GetFullPath(Path.Combine(anchor, ".."))
            Path.GetFullPath(Path.Combine(anchor, "..", ".."))
            Path.GetFullPath(Path.Combine(anchor, "..", "..", ".."))
        ]
        |> List.distinct

    let private search (root: string) (maxDepth: int) =
        let rec go (dir: string) (depth: int) =
            if CorpusAnchor.excluded anchoring.Value dir then
                None
            else
                match asFixtureDir dir with
                | Some found -> Some found
                | None when depth >= maxDepth -> None
                | None ->
                    let children =
                        try
                            Directory.EnumerateDirectories dir
                            |> Seq.filter (fun d -> not (pruned.Contains(Path.GetFileName d)))
                            |> Seq.toList
                        with _ -> []

                    children |> List.tryPick (fun child -> go child (depth + 1))

        if Directory.Exists root then go root 0 else None

    let private locate () : string =
        match Environment.GetEnvironmentVariable envVar with
        | null
        | "" ->
            match searchRoots () |> List.tryPick (fun root -> search root 3) with
            | Some found -> found
            | None ->
                failwithf
                    "ENVIRONMENTAL FAILURE — the Fact wire specification corpus is absent; this is not a defect in the code under test. Clone https://github.com/Fuaran-Data/fuaran-fact-specification (revision %s) within three directory levels of this checkout, or set %s to the clone (or its wire-fixtures/). Searched: %s. A corpus under a different git worktree of this repository is never accepted. The failure is by design: a conformance run without its corpus certifies nothing."
                    Pin.commit
                    envVar
                    (searchRoots () |> String.concat "; ")
        | raw ->
            match asFixtureDir (raw.Trim()) with
            | Some found -> found
            | None ->
                failwithf
                    "%s is set to '%s', which is not the Fact wire specification corpus (no manifest.json declaring specification '%s' there or under wire-fixtures/)."
                    envVar
                    raw
                    Pin.specification

    let dir = lazy (locate ())

    let bytes (relative: string) =
        File.ReadAllBytes(Path.Combine(dir.Value, relative.Replace('/', Path.DirectorySeparatorChar)))

/// One vector of the corpus manifest.
type private Vector = {
    Id: string
    Family: string
    Kind: string
    File: string
    Digest: string option
    RejectClass: string option
    SameAs: string option
    LineageKey: string option
}

let private optString (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private manifest =
    lazy
        (use doc = JsonDocument.Parse(Corpus.bytes "manifest.json")
         let root = doc.RootElement

         let vectors =
             root.GetProperty("vectors").EnumerateArray()
             |> Seq.map (fun v -> {
                 Id = v.GetProperty("id").GetString()
                 Family = v.GetProperty("family").GetString()
                 Kind = v.GetProperty("kind").GetString()
                 File = v.GetProperty("file").GetString()
                 Digest = optString v "digest"
                 RejectClass = optString v "rejectClass"
                 SameAs = optString v "sameAs"
                 LineageKey = optString v "lineageKey"
             })
             |> List.ofSeq

         root.GetProperty("specification").GetString(), root.GetProperty("wireVersion").GetInt32(), vectors)

let private vectors family kind =
    let _, _, all = manifest.Value
    all |> List.filter (fun v -> v.Family = family && v.Kind = kind)

let private text (v: Vector) =
    Encoding.UTF8.GetString(Corpus.bytes v.File)

let private readFact (v: Vector) : Fact =
    match FactWire.readFactText (text v) with
    | Ok f -> f
    | Error refusal -> failtestf "%s does not read: %s" v.Id (Refusal.describe refusal)

let private written (f: Fact) : string =
    match FactWire.writeFact f with
    | Ok t -> t
    | Error refusal -> failtestf "%s does not write: %s" f.FactId (Refusal.describe refusal)

/// A non-empty family, so a corpus that lost a family is red, not vacuous.
let private some (family: string) (kind: string) =
    match vectors family kind with
    | [] -> failtestf "the corpus holds no %s/%s vectors" family kind
    | vs -> vs

let tests =
    testList "Fact wire specification — forge's fact store and identity" [
        test "the corpus is the pinned Fact wire specification" {
            let specification, wireVersion, _ = manifest.Value
            Expect.equal specification Pin.specification "the manifest names the specification"
            Expect.equal wireVersion Pin.wireVersion "the wire version forge reads and writes"

            let digest =
                SHA256.HashData(Corpus.bytes "manifest.json")
                |> Convert.ToHexString
                |> _.ToLowerInvariant()

            Expect.equal
                digest
                Pin.manifestDigest
                (sprintf
                    "the corpus manifest moved from the revision this leg was certified against (%s); re-certify and move the pin"
                    Pin.commit)
        }

        test "every identity vector's id is the one forge derives" {
            for v in some "identity" "hash" do
                let f = readFact v
                Expect.equal (Some(Fact.computeFor f)) v.Digest v.Id
                Expect.equal (Some f.FactId) v.Digest (v.Id + ": the document carries its own id")
        }

        test "every lineage vector's key and digest are the ones forge derives from the parts" {
            for v in some "lineage" "hash" do
                let f = readFact v
                Expect.equal (Some(FactLineage.keyOf f.Subject f.Metric f.Period f.Method)) v.LineageKey v.Id
                Expect.equal (Some(FactLineage.digestOf f.Subject f.Metric f.Period f.Method)) v.Digest v.Id
                Expect.equal (Some(Fact.lineageDigest f)) v.Digest v.Id
        }

        testAsync "a fact the store persists reads back through the store as the same fact" {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

            let store =
                ToolUp.Facts.BlobFactStore(
                    storage,
                    InMemoryEventStore.InMemoryEventStore(),
                    (fun () -> DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc))
                )

            let facts =
                some "identity" "hash" @ some "lineage" "hash"
                |> List.map readFact
                |> List.distinctBy _.FactId

            for f in facts do
                let! uploaded =
                    storage.Upload("spec", sprintf "_facts/%s.json" f.FactId, Encoding.UTF8.GetBytes(written f))

                Expect.isOk uploaded f.FactId

            let! export = store.ExportScope "spec"
            Expect.isEmpty export.Unreadable "every persisted fact reads back"

            let byId = export.Facts |> List.map (fun f -> f.FactId, f) |> Map.ofList

            for f in facts do
                match byId |> Map.tryFind f.FactId with
                | Some back -> Expect.equal (written back) (written f) f.FactId
                | None -> failtestf "%s did not read back" f.FactId
        }

        test "the store's reader refuses every record the specification refuses, for the same class" {
            for v in some "record" "reject" do
                match FactStoreCodec.read (text v) with
                | Ok f -> failtestf "%s was admitted as fact %s" v.Id f.FactId
                | Error refusal -> Expect.equal (Some(RefusalClass.code refusal.Class)) v.RejectClass v.Id
        }

        test "the store's reader admits what the specification admits, as the fact it names" {
            let _, _, all = manifest.Value

            for v in some "record" "accept" do
                let target = all |> List.find (fun t -> Some t.Id = v.SameAs)

                match FactStoreCodec.read (text v) with
                | Ok f -> Expect.equal (Some f.FactId) target.Digest v.Id
                | Error refusal -> failtestf "%s was refused: %s" v.Id (Refusal.describe refusal)
        }
    ]