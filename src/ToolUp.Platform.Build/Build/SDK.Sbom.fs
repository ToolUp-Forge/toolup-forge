// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json
open System.Xml.Linq

/// Phase 182 — release SBOM + build-provenance sidecar emission.
///
/// The `Publish` FAKE target (`SDK.Build.fs`) packs every public-surface
/// SDK fsproj into `./artifacts/` and pushes each `.nupkg` to GitHub
/// Packages. This module emits, for each produced `.nupkg`, a CycloneDX
/// 1.5 SBOM over the package's declared NuGet dependency set (read from
/// the artefact's own embedded `.nuspec`, so the SBOM reflects exactly
/// what shipped) and — where a deployment wires a signer — a detached-JWS
/// provenance sidecar binding the artefact bytes to a signing key.
///
/// **GP 11 / GP 13 — off by default.** Emission is gated on the
/// `TOOLUP_EMIT_SBOM` environment variable. Unset (the local-pack
/// default), `emit` is a no-op returning `[]` — zero extra artefacts, the
/// existing `--skip-duplicate` push byte-for-byte unchanged. CI's
/// `publish-nuget.yml` sets the flag on the release path; consumers'
/// build/CI rely on GitHub's native `actions/attest-build-provenance` for
/// the cryptographic provenance attestation.
///
/// **GP 1 — no crypto dependency in the Build package.** This module
/// carries no reference to `ToolUp.ArtefactSigning`; the optional signer
/// is a structural `SignArtefact` function. A deployment that has wired an
/// `IArtefactSigner` (Phase 40) adapts it at its own `Build.fs` call site
/// — `fun bytes -> async { let! r = signer.Sign bytes in return r |> Result.map (fun s -> s.DetachedJws) |> Result.mapError SigningError.describe }`
/// — reusing the Phase 40 primitive rather than introducing a second
/// signing stack.
module Sbom =

    /// Environment variable gating SBOM/provenance emission (GP 11/13).
    [<Literal>]
    let EmitFlag = "TOOLUP_EMIT_SBOM"

    /// Optional detached signer hook. Given artefact bytes, produces a
    /// detached-JWS string (or an error description). Structurally
    /// decoupled from `ToolUp.ArtefactSigning` so the Build package keeps
    /// zero crypto dependencies (GP 1). `None` (the default on the CI
    /// path) emits no `.sig` sidecar — GitHub's native build-provenance
    /// attestation covers the published artefacts instead.
    type SignArtefact = byte[] -> Async<Result<string, string>>

    /// A CycloneDX `library` component — a single declared dependency.
    type Component = { Name: string; Version: string }

    let private truthy (v: string) =
        match v.Trim().ToLowerInvariant() with
        | "1"
        | "true"
        | "yes"
        | "on" -> true
        | _ -> false

    /// GP 11/13 gate. SBOM/provenance emission is off unless
    /// `TOOLUP_EMIT_SBOM` is set to a truthy value (`1` / `true` / `yes`
    /// / `on`). `getEnv` is injected so the decision is unit-testable
    /// without touching process environment.
    let isEnabled (getEnv: string -> string) =
        match getEnv EmitFlag with
        | null
        | "" -> false
        | v -> truthy v

    /// Package URL (purl) for a NuGet package — the CycloneDX `bom-ref`
    /// and `purl` value, e.g. `pkg:nuget/Giraffe@7.0.2`.
    let private purl (id: string) (version: string) = $"pkg:nuget/{id}@{version}"

    /// Read a produced `.nupkg`'s embedded `.nuspec` → its package id,
    /// version, and the union of declared `<dependency>` entries across
    /// every `<group>` target framework (de-duplicated). Returns `None`
    /// when the archive carries no `.nuspec` or omits id/version.
    let readNuspec (nupkgPath: string) : (string * string * Component list) option =
        use archive = ZipFile.OpenRead nupkgPath

        let entry =
            archive.Entries
            |> Seq.tryFind (fun e -> e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))

        match entry with
        | None -> None
        | Some e ->
            use stream = e.Open()
            let doc = XDocument.Load stream
            let root = doc.Root
            let ns = root.Name.Namespace
            let metadata = root.Element(ns + "metadata")

            if isNull metadata then
                None
            else
                let idElem = metadata.Element(ns + "id")
                let versionElem = metadata.Element(ns + "version")

                if isNull idElem || isNull versionElem then
                    None
                else
                    // Dependencies may sit directly under <dependencies> or
                    // be nested in per-framework <group> elements; collect
                    // every <dependency> descendant either way.
                    let deps =
                        let depsElem = metadata.Element(ns + "dependencies")

                        if isNull depsElem then
                            []
                        else
                            depsElem.Descendants(ns + "dependency")
                            |> Seq.choose (fun d ->
                                let depId = d.Attribute(XName.Get "id")
                                let depVer = d.Attribute(XName.Get "version")

                                if isNull depId then
                                    None
                                else
                                    Some {
                                        Name = depId.Value.Trim()
                                        Version = (if isNull depVer then "" else depVer.Value.Trim())
                                    })
                            |> Seq.distinct
                            |> Seq.sortBy (fun c -> c.Name.ToLowerInvariant())
                            |> List.ofSeq

                    Some(idElem.Value.Trim(), versionElem.Value.Trim(), deps)

    /// Build a minimal CycloneDX 1.5 JSON SBOM for a package and its
    /// declared dependency set. Pure — `timestamp` (ISO-8601 UTC) and
    /// `serialNumber` (a `urn:uuid:` value) are injected so the result is
    /// deterministic and unit-testable.
    let buildCycloneDx
        (id: string)
        (version: string)
        (deps: Component list)
        (timestamp: string)
        (serialNumber: string)
        : string =
        use mem = new MemoryStream()
        use writer = new Utf8JsonWriter(mem, JsonWriterOptions(Indented = true))

        let writeComponent (name: string) (ver: string) =
            writer.WriteStartObject()
            writer.WriteString("type", "library")
            writer.WriteString("bom-ref", purl name ver)
            writer.WriteString("name", name)
            writer.WriteString("version", ver)
            writer.WriteString("purl", purl name ver)
            writer.WriteEndObject()

        writer.WriteStartObject()
        writer.WriteString("bomFormat", "CycloneDX")
        writer.WriteString("specVersion", "1.5")
        writer.WriteString("serialNumber", serialNumber)
        writer.WriteNumber("version", 1)

        writer.WriteStartObject("metadata")
        writer.WriteString("timestamp", timestamp)
        writer.WriteStartObject("component")
        writer.WriteString("type", "library")
        writer.WriteString("bom-ref", purl id version)
        writer.WriteString("name", id)
        writer.WriteString("version", version)
        writer.WriteString("purl", purl id version)
        writer.WriteEndObject()
        writer.WriteEndObject()

        writer.WriteStartArray("components")

        for d in deps do
            writeComponent d.Name d.Version

        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

        Encoding.UTF8.GetString(mem.ToArray())

    /// Emit a per-package CycloneDX SBOM — and, where `signer` is
    /// supplied, a detached-JWS provenance sidecar — for every `.nupkg`
    /// in `nupkgs`. Each SBOM lands next to its `.nupkg` as
    /// `<id>.<version>.cdx.json`; each sidecar as `<nupkg>.sig`.
    ///
    /// Returns the paths of every artefact written, in emission order.
    /// **No-op returning `[]` when `isEnabled getEnv` is false** — the
    /// GP 13 zero-cost-when-unused contract for the local pack path.
    ///
    /// `now` / `newSerial` are injected so the SBOM body is reproducible
    /// under test; the `Publish` target passes wall-clock + a fresh GUID.
    let emit
        (getEnv: string -> string)
        (now: unit -> DateTimeOffset)
        (newSerial: unit -> string)
        (signer: SignArtefact option)
        (trace: string -> unit)
        (nupkgs: string list)
        : string list =
        if not (isEnabled getEnv) then
            []
        else
            [
                for nupkg in nupkgs do
                    match readNuspec nupkg with
                    | None -> trace $"SBOM: no .nuspec in {Path.GetFileName nupkg} — skipped"
                    | Some(id, version, deps) ->
                        let dir = Path.GetDirectoryName nupkg
                        let sbomPath = Path.Combine(dir, $"{id}.{version}.cdx.json")
                        let timestamp = (now ()).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                        let json = buildCycloneDx id version deps timestamp (newSerial ())
                        File.WriteAllText(sbomPath, json)
                        trace $"SBOM: wrote {Path.GetFileName sbomPath} ({List.length deps} declared dependencies)"
                        sbomPath

                        match signer with
                        | None -> ()
                        | Some sign ->
                            let bytes = File.ReadAllBytes nupkg

                            match sign bytes |> Async.RunSynchronously with
                            | Ok jws ->
                                let sigPath = nupkg + ".sig"
                                File.WriteAllText(sigPath, jws)
                                trace $"SBOM: wrote provenance sidecar {Path.GetFileName sigPath}"
                                sigPath
                            | Error err -> trace $"SBOM: signing failed for {Path.GetFileName nupkg}: {err}"
            ]