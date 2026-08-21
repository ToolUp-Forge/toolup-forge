// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.RestoreClosures

open System
open System.IO
open System.Text.Json
open ToolUp.Platform

// ─── Restore-output closure capture ──────────────────────────────────
//
// A build's compile-time dependency closure is OBSERVED, never
// re-derived: the restore already wrote down exactly what it resolved
// — package ids, exact versions, content hashes — in its own assets
// output (`project.assets.json`), and the origin of each package
// beside the extracted package itself (`.nupkg.metadata`, whose
// `source` field names the feed the package was downloaded from). This
// module reads those files back. It never runs a restore, never
// queries a feed, and never computes a hash of its own — a capture
// that re-derived any of this could disagree with what the build was
// actually given, which is the one thing a provenance record must not
// do.
//
// Honesty at the edges: a package whose origin metadata is absent (an
// offline restore, a folder feed that writes none, a cache populated
// by an older tool) is recorded with an EMPTY source, never a guessed
// one; a malformed or missing assets file is an `Error` naming the
// file, never an empty closure that would read as "no dependencies".
//
// Every captured entry starts honestly unattested (`ProviderAbsent`):
// capture records what was resolved, and attestation is a separate act
// (`DependencyClosure.attest`) against a registered
// `IUpstreamReleaseProvider`.

/// Decode the assets output's base64 content-hash value to the
/// lowercase-hex shape every other digest in this substrate uses.
/// `""` when the value is absent or not decodable — recorded as "no
/// digest observed", never fabricated. A re-encoding is not a
/// re-derivation: the bytes are the restore's own, only the spelling
/// changes.
let private contentDigestOf (hashBase64: string) : string =
    if String.IsNullOrWhiteSpace hashBase64 then
        ""
    else
        try
            Convert.ToHexString(Convert.FromBase64String hashBase64).ToLowerInvariant()
        with _ ->
            ""

let private stringProperty (name: string) (element: JsonElement) : string option =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
    | _ -> None

/// Read one package's origin source from the `.nupkg.metadata` the
/// restore wrote beside the extracted package, probing each package
/// folder in the order the assets output lists them. `""` when no
/// folder holds one, or the file carries no `source`.
let private sourceOf (packageFolders: string list) (libraryPath: string) : string =
    packageFolders
    |> List.tryPick (fun folder ->
        let metadataPath =
            Path.Combine(folder, libraryPath.Replace('/', Path.DirectorySeparatorChar), ".nupkg.metadata")

        if not (File.Exists metadataPath) then
            None
        else
            try
                use doc = JsonDocument.Parse(File.ReadAllText metadataPath)
                stringProperty "source" doc.RootElement
            with _ ->
                None)
    |> Option.defaultValue ""

/// Read the resolved dependency closure from a restore's assets output
/// — the `project.assets.json` the restore wrote under the project's
/// `obj/`.
///
/// One entry per resolved PACKAGE (project references are not part of
/// the package closure and are skipped), carrying the exact resolved
/// version, the content hash the restore recorded, and the source the
/// package was downloaded from. Every entry comes back unattested
/// (`ProviderAbsent`); run `DependencyClosure.attest` to resolve the
/// upstream references.
let readAssetsFile (assetsPath: string) : Result<DependencyClosure, string> =
    if not (File.Exists assetsPath) then
        Error $"restore assets output not found: {assetsPath}"
    else
        try
            use doc = JsonDocument.Parse(File.ReadAllText assetsPath)
            let root = doc.RootElement

            let packageFolders =
                match root.TryGetProperty "packageFolders" with
                | true, folders when folders.ValueKind = JsonValueKind.Object ->
                    folders.EnumerateObject() |> Seq.map _.Name |> List.ofSeq
                | _ -> []

            let entries =
                match root.TryGetProperty "libraries" with
                | true, libraries when libraries.ValueKind = JsonValueKind.Object ->
                    libraries.EnumerateObject()
                    |> Seq.choose (fun library ->
                        let isPackage = stringProperty "type" library.Value = Some "package"

                        if not isPackage then
                            None
                        else
                            match library.Name.Split '/' with
                            | [| id; version |] ->
                                let contentDigest =
                                    stringProperty "sha512" library.Value
                                    |> Option.map contentDigestOf
                                    |> Option.defaultValue ""

                                let path =
                                    stringProperty "path" library.Value
                                    |> Option.defaultValue (library.Name.ToLowerInvariant())

                                Some(
                                    DependencyClosure.unattestedEntry
                                        id
                                        version
                                        (sourceOf packageFolders path)
                                        contentDigest
                                )
                            | _ -> None)
                    |> List.ofSeq
                | _ -> []

            Ok(DependencyClosure.create entries)
        with ex ->
            Error $"restore assets output unreadable: {assetsPath}: {ex.Message}"