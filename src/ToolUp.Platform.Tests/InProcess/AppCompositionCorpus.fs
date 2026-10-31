// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AppCompositionCorpus

// ─── The provenance of forge's pinned core vectors ─────────────────
//
// forge's composition profile (`spec/`) certifies its §P8 facet
// declaration against vectors of the App-composition wire specification
// (https://github.com/Fuaran-Build/fuaran-app-composition-specification).
// Those vectors stay with the specification: `spec/fixtures/core/` holds
// COPIES, and `spec/fixtures/manifest.json` records the commit they were
// copied at (`core.commit`) with a digest per copy.
//
// The digests are recorded by the same change that writes the copies, so
// on their own they prove only that the copies have not moved since. This
// module reads the specification's own corpus AT THAT COMMIT so the
// conformance pack can hold each copy to its home, byte for byte, and hold
// the pinned set to everything the specification scopes to this profile.
//
// It is data only: the manifests are JSON and the bytes come out of git.
// Nothing here depends on any package of the specification's, and no
// shipped forge package depends on this — conformance is a property of
// the test suite, never a runtime dependency.
//
// Where the specification home is found:
//   1. `TOOLUP_APP_COMPOSITION_SPEC_DIR`, when set — and when set but not a
//      specification home, that is the answer: no search runs behind it;
//   2. a checkout named `fuaran-app-composition-specification` at the
//      repository root (what CI clones);
//   3. a bounded search from the repository's main working tree, the same
//      walk the other externally-pinned corpora use (`Support.CorpusAnchor`),
//      recognising a clone by its content under whatever name it has.
// Of the clones found, the one holding the pinned commit is used.
// Absent, the pack FAILS, naming the remedy. It never skips: a pin nobody
// can check certifies nothing.

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open ToolUp.Platform.Tests.Support

/// The directory CI checks the specification out to, at the repository
/// root: the repository's own name.
[<Literal>]
let SpecDirName = "fuaran-app-composition-specification"

/// Environment override naming the specification home directly.
[<Literal>]
let SpecDirVariable = "TOOLUP_APP_COMPOSITION_SPEC_DIR"

/// The specification's public home, for the remedy text.
[<Literal>]
let SpecUrl = "https://github.com/Fuaran-Build/fuaran-app-composition-specification"

/// The profile whose vectors forge pins.
[<Literal>]
let Profile = "server-app-host"

/// One vector as a manifest enumerates it.
type VectorEntry = {
    Id: string
    Family: string
    Profile: string option
    File: string
    Sha256: string
}

/// Repo root derived from the running test assembly:
/// bin/<Config>/net10.0/ToolUp.Platform.Tests.dll → up 5 = repo root.
let repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private str (name: string) (e: JsonElement) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private entriesOf (vectors: JsonElement) : VectorEntry list = [
    for v in vectors.EnumerateArray() do
        match str "id" v, str "family" v, str "file" v, str "sha256" v with
        | Some id, Some family, Some file, Some sha ->
            yield {
                Id = id
                Family = family
                Profile = str "profile" v
                File = file
                Sha256 = sha
            }
        | _ -> failwithf "a manifest vector entry lacks id, family, file or sha256: %s" (v.GetRawText())
]

// ─── forge's side of the pin ─────────────────────────────────────────

/// The pin as forge records it in `spec/fixtures/manifest.json`.
type Pin = {
    /// `spec/fixtures`, absolute.
    FixturesDir: string
    Commit: string
    Url: string
    /// The core vectors forge carries copies of; `File` is relative to
    /// `FixturesDir`.
    Pinned: VectorEntry list
}

let loadPin () : Pin =
    let fixtures = Path.Combine(repoRoot (), "spec", "fixtures")
    let manifestPath = Path.Combine(fixtures, "manifest.json")

    if not (File.Exists manifestPath) then
        failwithf "forge's profile corpus is absent: %s does not exist" manifestPath

    use doc = JsonDocument.Parse(File.ReadAllText manifestPath)

    let core =
        match doc.RootElement.TryGetProperty "core" with
        | true, c -> c
        | _ -> failwith "spec/fixtures/manifest.json carries no core pin"

    let required name =
        str name core
        |> Option.defaultWith (fun () -> failwithf "the core pin names no %s" name)

    {
        FixturesDir = fixtures
        Commit = required "commit"
        Url = required "url"
        Pinned = entriesOf (core.GetProperty "vectors")
    }

// ─── Reading a specification home at a commit ───────────────────────

/// The bytes of `path` (relative to the home's root) at `commit`, via
/// `git cat-file`, so the leg reads the pinned revision whatever the
/// clone has checked out. `Error` carries git's own message.
let blobAt (home: string) (commit: string) (path: string) : Result<byte array, string> =
    try
        let psi =
            ProcessStartInfo("git", sprintf "cat-file blob %s:%s" commit (path.Replace('\\', '/')))

        psi.WorkingDirectory <- home
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start psi
        use buffer = new MemoryStream()
        let stderr = proc.StandardError.ReadToEndAsync()
        proc.StandardOutput.BaseStream.CopyTo buffer

        if not (proc.WaitForExit 30000) then
            proc.Kill true
            Error "git cat-file did not finish within 30 s"
        elif proc.ExitCode <> 0 then
            Error(stderr.Result.Trim())
        else
            Ok(buffer.ToArray())
    with ex ->
        Error ex.Message

/// Is `commit` present in the home's object store?
let commitPresent (home: string) (commit: string) =
    match blobAt home commit "wire-fixtures/manifest.json" with
    | Ok _ -> true
    | Error _ -> false

/// The vectors the specification's manifest enumerates at `commit`. Their
/// `File` is relative to `wire-fixtures/`.
let upstreamVectors (home: string) (commit: string) : Result<VectorEntry list, string> =
    blobAt home commit "wire-fixtures/manifest.json"
    |> Result.map (fun bytes ->
        use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
        entriesOf (doc.RootElement.GetProperty "vectors"))

// ─── Finding the specification home ──────────────────────────────────

/// A clone of the specification, recognised by its CONTENT rather than its
/// directory name: a local clone may sit under any name.
let private isSpecHome (dir: string) =
    File.Exists(Path.Combine(dir, "wire-fixtures", "manifest.json"))
    && File.Exists(Path.Combine(dir, "APP_COMPOSITION_WIRE.md"))

let private anchoring = lazy (CorpusAnchor.resolve (repoRoot ()))

/// Bounded search: each ancestor of the anchor (up to four), then up to
/// three levels beneath each, never entering a foreign working tree of
/// this repository. Yields every specification clone it meets, in order.
let private specClonesNear (a: CorpusAnchor.Anchoring) : string seq =
    let skip (name: string) =
        name.StartsWith '.'
        || name = "node_modules"
        || name = "bin"
        || name = "obj"
        || name = "packages"

    // A clone is recognised among the CHILDREN of a directory searched at
    // depth <= 3, the same reach a search by directory name has.
    let rec descend (dir: string) (depth: int) : string seq = seq {
        if depth <= 3 && not (CorpusAnchor.excluded a dir) then
            let children =
                try
                    Directory.EnumerateDirectories dir
                    |> Seq.filter (fun d -> not (skip (Path.GetFileName d)))
                    |> Array.ofSeq
                with _ -> [||]

            for child in children do
                if isSpecHome child then
                    if not (CorpusAnchor.excluded a child) then
                        yield child
                else
                    yield! descend child (depth + 1)
    }

    let rec ancestors (dir: string) (levels: int) = seq {
        if levels <= 4 && not (String.IsNullOrEmpty dir) then
            yield dir

            match Path.GetDirectoryName dir with
            | null -> ()
            | parent -> yield! ancestors parent (levels + 1)
    }

    ancestors a.Anchor 0 |> Seq.collect (fun dir -> descend dir 1)

let private resolvedSpecHome =
    lazy
        (let commit = (loadPin ()).Commit

         match Environment.GetEnvironmentVariable SpecDirVariable with
         | null
         | "" ->
             // The clone that holds the pinned commit wins; failing that, the
             // first clone found, so the failure names the fetch it needs.
             let clones =
                 Seq.append
                     ([ repoRoot (); anchoring.Value.Anchor ]
                      |> List.distinct
                      |> Seq.map (fun root -> Path.Combine(root, SpecDirName))
                      |> Seq.filter isSpecHome)
                     (specClonesNear anchoring.Value)
                 |> Seq.cache

             clones
             |> Seq.tryFind (fun dir -> commitPresent dir commit)
             |> Option.orElseWith (fun () -> Seq.tryHead clones)
         | named when isSpecHome named -> Some named
         | _ -> None)

let specHome () : string option = resolvedSpecHome.Value

/// The whole remedy, in the failure message.
let missingSpecHomeMessage () =
    let named =
        match Environment.GetEnvironmentVariable SpecDirVariable with
        | null
        | "" -> sprintf "%s is unset, and no clone of the specification was found near this repository" SpecDirVariable
        | v -> sprintf "%s names '%s', which is not a clone of the specification" SpecDirVariable v

    sprintf
        "The App-composition wire specification home is required and was not found: %s. Clone %s (it is public) next to this repository, or anywhere and point %s at it. Its pinned commit must be present in that clone. The leg never skips: a pin nobody can check certifies nothing."
        named
        SpecUrl
        SpecDirVariable