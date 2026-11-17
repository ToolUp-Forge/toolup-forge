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
/// **The corpus is a build input**, found as `Corpus` says. An absent
/// corpus is one loud failure, never a skip: a conformance run without its
/// corpus certifies nothing.
module ToolUp.Platform.Tests.FactSpecConformance

open System
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
    let commit = "9d2347f4c2dbab831781f8c0f47fb9cd1a9fc75e"

    let manifestDigest =
        "928abaf19a32ae2d1f0987fc8af4137c3dabbaaa0cf9e3ccfc35236d99569616"

    let specification = "fact-wire"
    let wireVersion = 1

/// Where the corpus is, resolved in the order the federation-seam
/// specification home is (`FederationWireCorpus`): the directory
/// `TOOLUP_FACT_SPEC_DIR` names, checked, and no fallback when it is set but
/// wrong; else a clone named `fuaran-fact-specification` in this repository
/// (what CI checks out; the path is gitignored), here or in the main working
/// tree; else a bounded search from the main working tree's ancestors for a
/// directory of that name. The directory name is the interface.
[<RequireQualifiedAccess>]
module private Corpus =

    [<Literal>]
    let SpecDirName = "fuaran-fact-specification"

    [<Literal>]
    let SpecDirVariable = "TOOLUP_FACT_SPEC_DIR"

    /// bin/<Config>/net10.0/ToolUp.Platform.Tests.dll -> up 5.
    let private repoRoot () =
        let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    /// A specification home: its `wire-fixtures/manifest.json` declares the
    /// Fact wire specification. Keyed on the content, so a directory of the
    /// right name and the wrong contents is not taken for it.
    let private isSpecHome (dir: string) =
        let manifest = Path.Combine(dir, "wire-fixtures", "manifest.json")

        File.Exists manifest
        && (try
                use doc = JsonDocument.Parse(File.ReadAllBytes manifest)

                match doc.RootElement.TryGetProperty "specification" with
                | true, v -> v.ValueKind = JsonValueKind.String && v.GetString() = Pin.specification
                | _ -> false
            with _ ->
                false)

    let private anchoring = lazy (CorpusAnchor.resolve (repoRoot ()))

    /// Each ancestor of the anchor, then up to three levels beneath each,
    /// never entering another working tree of this repository.
    let private search (anchoring: CorpusAnchor.Anchoring) =
        let skip (name: string) =
            name.StartsWith '.'
            || name = "node_modules"
            || name = "bin"
            || name = "obj"
            || name = "packages"

        let rec descend (dir: string) (depth: int) =
            if depth > 3 || CorpusAnchor.excluded anchoring dir then
                None
            else
                let candidate = Path.Combine(dir, SpecDirName)

                if
                    Directory.Exists candidate
                    && isSpecHome candidate
                    && not (CorpusAnchor.excluded anchoring candidate)
                then
                    Some candidate
                else
                    try
                        Directory.EnumerateDirectories dir
                        |> Seq.filter (fun d -> not (skip (Path.GetFileName d)))
                        |> Seq.tryPick (fun d -> descend d (depth + 1))
                    with _ ->
                        None

        let rec ascend (dir: string) (levels: int) =
            if levels > 4 || String.IsNullOrEmpty dir then
                None
            else
                match descend dir 1 with
                | Some found -> Some found
                | None ->
                    match Path.GetDirectoryName dir with
                    | null -> None
                    | parent -> ascend parent (levels + 1)

        ascend anchoring.Anchor 0

    let private resolved =
        lazy
            (match Environment.GetEnvironmentVariable SpecDirVariable with
             | null
             | "" ->
                 [ repoRoot (); anchoring.Value.Anchor ]
                 |> List.distinct
                 |> List.tryPick (fun root ->
                     let inRepo = Path.Combine(root, SpecDirName)

                     if Directory.Exists inRepo && isSpecHome inRepo then
                         Some inRepo
                     else
                         None)
                 |> Option.orElseWith (fun () -> search anchoring.Value)
             // Set and right, or set and wrong: a wrong pointer is the answer,
             // never a reason to certify against some other corpus.
             | explicitDir -> if isSpecHome explicitDir then Some explicitDir else None)

    let private missing =
        $"ENVIRONMENTAL FAILURE — the Fact wire specification home was not found, so there is no
       corpus to certify against; this is the checkout's arrangement, not a defect in the code under test.
       This repository certifies against that specification; it does not own it: the normative text and
       the conformance corpus live in their own public repository (revision {Pin.commit} is the one pinned).
       Fix it in one of three ways:
         1. clone it into this repository (what CI does; the path is gitignored):
              git clone https://github.com/Fuaran-Data/{SpecDirName}.git {SpecDirName}
         2. set {SpecDirVariable} to an existing checkout of it — if it is already set, it does
            not point at one (a spec home contains wire-fixtures/manifest.json declaring '{Pin.specification}'), or
         3. place a checkout named '{SpecDirName}' at or near this repository's parent — for a
            linked git worktree that means near the MAIN working tree, which is where the search
            runs from; a checkout under a different worktree of this repository is never accepted.
       A conformance run without its corpus certifies nothing, so the leg fails rather than skips."

    let dir =
        lazy
            (match resolved.Value with
             | Some home -> Path.Combine(home, "wire-fixtures")
             | None ->
                 let worktreeNote =
                     match CorpusAnchor.mainWorkingTree (repoRoot ()) with
                     | Some main -> $" (This checkout is a linked git worktree; its main working tree is '{main}'.)"
                     | None -> ""

                 failwith (missing + worktreeNote))

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