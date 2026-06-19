// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.SbomTests

open System
open System.IO
open System.IO.Compression
open Expecto
open ToolUp.Platform

// ─── Phase 182 — release SBOM gate + emission ──────────────────────────
//
// Pins the GP 11 / GP 13 contract: SBOM emission is a no-op (zero extra
// artefacts) unless TOOLUP_EMIT_SBOM is set to a truthy value, and emits
// a per-package CycloneDX SBOM next to each nupkg when it is.

/// Write a minimal but valid `.nupkg` (a zip carrying a `.nuspec`) into
/// `dir`, returning its path. Mirrors the real artefact shape `readNuspec`
/// parses — id/version metadata plus a per-framework dependency group.
let private writeFakeNupkg (dir: string) (id: string) (version: string) =
    let path = Path.Combine(dir, $"{id}.{version}.nupkg")

    let nuspec =
        $"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{id}</id>
    <version>{version}</version>
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="Giraffe" version="7.0.2" />
        <dependency id="FSharp.Core" version="10.1.0" />
      </group>
    </dependencies>
  </metadata>
</package>"""

    use archive = ZipFile.Open(path, ZipArchiveMode.Create)
    let entry = archive.CreateEntry($"{id}.nuspec")
    use writer = new StreamWriter(entry.Open())
    writer.Write nuspec
    writer.Flush()
    path

let private withTempDir (f: string -> 'a) =
    let dir =
        Path.Combine(Path.GetTempPath(), "sbom-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private fixedNow () =
    DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero)

let private fixedSerial () =
    "urn:uuid:00000000-0000-0000-0000-000000000000"

let private noTrace (_: string) = ()

let tests =
    testList "Sbom" [
        testList "isEnabled (GP 11/13 gate)" [
            test "unset env → disabled" {
                Expect.isFalse (Sbom.isEnabled (fun _ -> null)) "null env value must disable"
                Expect.isFalse (Sbom.isEnabled (fun _ -> "")) "empty env value must disable"
            }

            test "truthy values → enabled" {
                for v in [ "1"; "true"; "TRUE"; "yes"; "on"; " true " ] do
                    Expect.isTrue (Sbom.isEnabled (fun _ -> v)) $"'{v}' must enable"
            }

            test "non-truthy values → disabled" {
                for v in [ "0"; "false"; "no"; "off"; "nope" ] do
                    Expect.isFalse (Sbom.isEnabled (fun _ -> v)) $"'{v}' must disable"
            }
        ]

        testList "emit" [
            test "TOOLUP_EMIT_SBOM unset → no artefact emitted (GP 13)" {
                withTempDir (fun dir ->
                    let nupkg = writeFakeNupkg dir "ToolUp.Platform.Core" "0.6.0"

                    let written = Sbom.emit (fun _ -> null) fixedNow fixedSerial None noTrace [ nupkg ]

                    Expect.isEmpty written "emit must write nothing when the flag is unset"

                    // The only file in the directory is still the nupkg we
                    // seeded — zero extra artefacts on disk.
                    let extras =
                        Directory.GetFiles dir |> Array.filter (fun p -> not (p.EndsWith ".nupkg"))

                    Expect.isEmpty extras "no SBOM / sidecar files may exist when the flag is unset")
            }

            test "TOOLUP_EMIT_SBOM set → one CycloneDX SBOM per nupkg" {
                withTempDir (fun dir ->
                    let nupkg = writeFakeNupkg dir "ToolUp.Platform.Core" "0.6.0"

                    let written =
                        Sbom.emit (fun _ -> "true") fixedNow fixedSerial None noTrace [ nupkg ]

                    Expect.hasLength written 1 "exactly one SBOM artefact"

                    let sbomPath = List.head written
                    Expect.stringEnds sbomPath "ToolUp.Platform.Core.0.6.0.cdx.json" "SBOM named after the package"
                    Expect.isTrue (File.Exists sbomPath) "SBOM file written to disk"

                    let json = File.ReadAllText sbomPath
                    Expect.stringContains json "\"bomFormat\": \"CycloneDX\"" "declares CycloneDX format"
                    Expect.stringContains json "\"specVersion\": \"1.5\"" "declares spec version"

                    Expect.stringContains json "pkg:nuget/ToolUp.Platform.Core@0.6.0" "metadata component purl present"

                    Expect.stringContains json "pkg:nuget/Giraffe@7.0.2" "declared dependency listed as component"
                    Expect.stringContains json "pkg:nuget/FSharp.Core@10.1.0" "second declared dependency listed")
            }

            test "no signer → no .sig sidecar" {
                withTempDir (fun dir ->
                    let nupkg = writeFakeNupkg dir "ToolUp.AI" "0.6.0"

                    Sbom.emit (fun _ -> "1") fixedNow fixedSerial None noTrace [ nupkg ] |> ignore

                    let sidecars = Directory.GetFiles(dir, "*.sig")
                    Expect.isEmpty sidecars "no detached-JWS sidecar without a signer")
            }

            test "signer supplied → detached-JWS sidecar emitted alongside the SBOM" {
                withTempDir (fun dir ->
                    let nupkg = writeFakeNupkg dir "ToolUp.AI" "0.6.0"
                    let signer: Sbom.SignArtefact = fun _ -> async { return Ok "header..signature" }

                    let written =
                        Sbom.emit (fun _ -> "1") fixedNow fixedSerial (Some signer) noTrace [ nupkg ]

                    Expect.hasLength written 2 "SBOM + sidecar"
                    let sigPath = nupkg + ".sig"
                    Expect.isTrue (File.Exists sigPath) "sidecar written next to the nupkg"
                    Expect.equal (File.ReadAllText sigPath) "header..signature" "sidecar carries the detached JWS")
            }
        ]

        testList "buildCycloneDx (deterministic)" [
            test "well-formed CycloneDX with injected timestamp + serial" {
                let json =
                    Sbom.buildCycloneDx
                        "ToolUp.Platform.Core"
                        "0.6.0"
                        [ { Name = "Giraffe"; Version = "7.0.2" } ]
                        "2026-06-19T00:00:00Z"
                        "urn:uuid:00000000-0000-0000-0000-000000000000"

                Expect.stringContains json "\"timestamp\": \"2026-06-19T00:00:00Z\"" "injected timestamp"

                Expect.stringContains
                    json
                    "\"serialNumber\": \"urn:uuid:00000000-0000-0000-0000-000000000000\""
                    "injected serial"

                // Round-trips as valid JSON.
                let parsed = System.Text.Json.JsonDocument.Parse json

                Expect.equal
                    (parsed.RootElement.GetProperty("bomFormat").GetString())
                    "CycloneDX"
                    "valid JSON, CycloneDX"
            }
        ]
    ]