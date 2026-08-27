// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Cli.Tests.StampRoundTripTests

open System
open System.IO
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.ArtefactSigning
open ToolUp.Cli
open ToolUp.Cli.Dispatch

// ─── Phase 166 — stamp ↔ verify round-trip ──────────────────────────────
//
// Pins the `toolup stamp` minting to the same wire shape the shipped
// `DefaultModuleBindingVerifier` (Phase 165) validates against — the
// anti-drift guarantee for the CLI reimplementing the JWS shape with pure
// BCL crypto. Each test runs the *real* `stamp` command end-to-end (it
// writes a manifest file), reads it back through the *real* server-side
// `ModuleBindingManifest` reader, and verifies the recovered stamp against
// a freshly-built anchor. A wrong anchor must fail closed.

let private tempPath () =
    Path.Combine(Path.GetTempPath(), "toolup-stamp-" + Guid.NewGuid().ToString("N") + ".json")

let private loadStamp (manifest: string) (moduleId: string) : ModuleBindingStamp =
    match ModuleBindingManifest.load manifest with
    | Error e -> failtestf "manifest did not parse: %s" e
    | Ok map ->
        match Map.tryFind moduleId map with
        | Some s -> s
        | None -> failtestf "no stamp for module '%s' in the manifest" moduleId

let private verify (anchors: ModuleBindingAnchor list) (moduleId: string) (stamp: ModuleBindingStamp) : BindingOutcome =
    (DefaultModuleBindingVerifier.create anchors).Verify(moduleId, Some stamp)

let tests =
    testList "Phase 166 — stamp round-trip" [

        test "symmetric (MAC) stamp verifies under its anchor, fails under a wrong one" {
            let manifest = tempPath ()

            try
                let key = RandomNumberGenerator.GetBytes 32
                let keyB64 = Convert.ToBase64String key

                let code =
                    StampCommand.command.Run [
                        "--manifest"
                        manifest
                        "--module"
                        "Sales"
                        "--key-id"
                        "k1"
                        "--mac-key"
                        keyB64
                    ]

                Expect.equal code ExitOk "stamp succeeds"

                let stamp = loadStamp manifest "Sales"

                Expect.equal
                    (verify [ SymmetricAnchor("k1", key) ] "Sales" stamp)
                    Allowed
                    "verifies under the minting key"

                let wrongKey = RandomNumberGenerator.GetBytes 32

                Expect.notEqual
                    (verify [ SymmetricAnchor("k1", wrongKey) ] "Sales" stamp)
                    Allowed
                    "fails closed under a wrong key"

                // Replayed onto a different module id → rejected (stamp is
                // bound to the module's bytes).
                Expect.notEqual
                    (verify [ SymmetricAnchor("k1", key) ] "Inventory" stamp)
                    Allowed
                    "not replayable onto another module"
            finally
                File.Delete manifest
        }

        test "asymmetric (ES256 JWS) stamp verifies under its public-key anchor" {
            let manifest = tempPath ()
            let pem = tempPath () + ".pem"

            try
                use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
                File.WriteAllText(pem, ec.ExportPkcs8PrivateKeyPem())

                let code =
                    StampCommand.command.Run [
                        "--manifest"
                        manifest
                        "--module"
                        "Inventory"
                        "--key-id"
                        "ec1"
                        "--ec-key-file"
                        pem
                    ]

                Expect.equal code ExitOk "stamp succeeds"

                let stamp = loadStamp manifest "Inventory"
                let anchor = AsymmetricAnchor("ec1", EcdsaP256, ec.ExportSubjectPublicKeyInfo())
                Expect.equal (verify [ anchor ] "Inventory" stamp) Allowed "verifies under the matching public key"

                use other = ECDsa.Create(ECCurve.NamedCurves.nistP256)

                let wrongAnchor =
                    AsymmetricAnchor("ec1", EcdsaP256, other.ExportSubjectPublicKeyInfo())

                Expect.notEqual
                    (verify [ wrongAnchor ] "Inventory" stamp)
                    Allowed
                    "fails closed under a different key pair"
            finally
                File.Delete manifest
                File.Delete pem
        }

        test "re-stamping under a new key re-binds (old anchor stops verifying)" {
            let manifest = tempPath ()

            try
                let keyA = RandomNumberGenerator.GetBytes 32
                let keyB = RandomNumberGenerator.GetBytes 32

                StampCommand.command.Run [
                    "--manifest"
                    manifest
                    "--module"
                    "Sales"
                    "--key-id"
                    "kA"
                    "--mac-key"
                    Convert.ToBase64String keyA
                ]
                |> ignore
                // Re-key: same module, different anchor — overwrites the entry.
                StampCommand.command.Run [
                    "--manifest"
                    manifest
                    "--module"
                    "Sales"
                    "--key-id"
                    "kB"
                    "--mac-key"
                    Convert.ToBase64String keyB
                ]
                |> ignore

                let stamp = loadStamp manifest "Sales"

                Expect.equal
                    (verify [ SymmetricAnchor("kB", keyB) ] "Sales" stamp)
                    Allowed
                    "verifies under the new anchor"

                Expect.notEqual
                    (verify [ SymmetricAnchor("kA", keyA) ] "Sales" stamp)
                    Allowed
                    "the old anchor no longer verifies"
            finally
                File.Delete manifest
        }

        test "--unbind removes the entry (module ships unbound again)" {
            let manifest = tempPath ()

            try
                let key = RandomNumberGenerator.GetBytes 32

                StampCommand.command.Run [
                    "--manifest"
                    manifest
                    "--module"
                    "Sales"
                    "--key-id"
                    "k1"
                    "--mac-key"
                    Convert.ToBase64String key
                ]
                |> ignore

                Expect.equal
                    (StampCommand.command.Run [ "--manifest"; manifest; "--module"; "Sales"; "--unbind" ])
                    ExitOk
                    "unbind succeeds"

                match ModuleBindingManifest.load manifest with
                | Ok map -> Expect.isFalse (Map.containsKey "Sales" map) "the entry is gone"
                | Error e -> failtestf "manifest did not parse: %s" e
            finally
                File.Delete manifest
        }

        test "applyTo attaches the manifest stamp onto the matching ServerModule" {
            let manifest = tempPath ()

            try
                let key = RandomNumberGenerator.GetBytes 32

                StampCommand.command.Run [
                    "--manifest"
                    manifest
                    "--module"
                    "Sales"
                    "--key-id"
                    "k1"
                    "--mac-key"
                    Convert.ToBase64String key
                ]
                |> ignore

                let map =
                    match ModuleBindingManifest.load manifest with
                    | Ok m -> m
                    | Error e -> failtestf "manifest did not parse: %s" e

                let stamped = ModuleBindingManifest.applyTo map (ServerModule.create "Sales")
                let untouched = ModuleBindingManifest.applyTo map (ServerModule.create "Other")

                Expect.isSome stamped.BindingStamp "the matching module gets a stamp"
                Expect.isNone untouched.BindingStamp "a module with no entry is unchanged"
            finally
                File.Delete manifest
        }

        test "a missing manifest reads as an empty map (GP 13 byte-identical)" {
            match ModuleBindingManifest.load (tempPath ()) with
            | Ok map -> Expect.isTrue (Map.isEmpty map) "no manifest → no bindings"
            | Error e -> failtestf "absent manifest should be Ok empty, got Error %s" e
        }

        // ── Phase 216 — SBOM minting round-trip (CLI ↔ verifier) ─────────
        test "an SBOM minted by `toolup stamp` verifies under the server verifier" {
            let manifest = tempPath ()
            let assetA = tempPath () + ".dll"
            let assetB = tempPath () + ".dll"

            try
                File.WriteAllText(assetA, "contents of assembly A")
                File.WriteAllText(assetB, "contents of assembly B")
                let key = RandomNumberGenerator.GetBytes 32

                let code =
                    StampCommand.command.Run [
                        "--manifest"
                        manifest
                        "--module"
                        "Sales"
                        "--key-id"
                        "k1"
                        "--mac-key"
                        Convert.ToBase64String key
                        "--sbom-file"
                        assetA
                        "--sbom-file"
                        assetB
                        "--sbom-package"
                        "ToolUp.Platform.Server@0.9.4"
                    ]

                Expect.equal code ExitOk "stamp with SBOM succeeds"

                // The stamp itself still verifies (the SBOM is additive).
                Expect.equal
                    (verify [ SymmetricAnchor("k1", key) ] "Sales" (loadStamp manifest "Sales"))
                    Allowed
                    "stamp verifies"

                // The SBOM read back through the server reader verifies under
                // the same anchor — the CLI's BCL minting matches the verifier.
                match ModuleBindingManifest.loadSboms manifest with
                | Error e -> failtestf "loadSboms failed: %s" e
                | Ok map ->
                    match Map.tryFind "Sales" map with
                    | None -> failtest "no SBOM recovered for 'Sales'"
                    | Some sbom ->
                        Expect.equal sbom.Sbom.Components.Length 3 "two files + one package = three components"

                        Expect.equal
                            (DefaultModuleBindingVerifier.verifySbom [ SymmetricAnchor("k1", key) ] "Sales" sbom)
                            Allowed
                            "the CLI-minted SBOM verifies under the server verifier"

                        Expect.notEqual
                            (DefaultModuleBindingVerifier.verifySbom
                                [ SymmetricAnchor("k1", RandomNumberGenerator.GetBytes 32) ]
                                "Sales"
                                sbom)
                            Allowed
                            "a wrong anchor fails closed"
            finally
                File.Delete manifest
                File.Delete assetA
                File.Delete assetB
        }

        test "re-stamping with a new SBOM regenerates it (old content stops verifying)" {
            let manifest = tempPath ()
            let asset = tempPath () + ".dll"

            try
                let key = RandomNumberGenerator.GetBytes 32
                File.WriteAllText(asset, "version one")

                StampCommand.command.Run [
                    "--manifest"
                    manifest
                    "--module"
                    "Sales"
                    "--key-id"
                    "k1"
                    "--mac-key"
                    Convert.ToBase64String key
                    "--sbom-file"
                    asset
                ]
                |> ignore

                let firstHash =
                    match ModuleBindingManifest.loadSboms manifest with
                    | Ok m -> (Map.find "Sales" m).Sbom.Components.Head.Sha256
                    | Error e -> failtestf "loadSboms failed: %s" e

                // Change the asset content and re-stamp — the SBOM regenerates.
                File.WriteAllText(asset, "version two — different bytes")

                StampCommand.command.Run [
                    "--manifest"
                    manifest
                    "--module"
                    "Sales"
                    "--key-id"
                    "k1"
                    "--mac-key"
                    Convert.ToBase64String key
                    "--sbom-file"
                    asset
                ]
                |> ignore

                match ModuleBindingManifest.loadSboms manifest with
                | Error e -> failtestf "loadSboms failed: %s" e
                | Ok m ->
                    let sbom = Map.find "Sales" m

                    Expect.notEqual
                        sbom.Sbom.Components.Head.Sha256
                        firstHash
                        "the re-stamp recomputed the content hash"

                    Expect.equal
                        (DefaultModuleBindingVerifier.verifySbom [ SymmetricAnchor("k1", key) ] "Sales" sbom)
                        Allowed
                        "the regenerated SBOM verifies"
            finally
                File.Delete manifest
                File.Delete asset
        }

        // ── Phase 589 — certified-surface round-trip ──────────────────
        //
        // The CLI mints the certification with pure BCL crypto and its own
        // canonical-bytes rendering; the server verifies it with
        // `ModuleCertificationSigning.canonicalBytes`. Two independent
        // implementations of one wire shape is exactly the drift this pins —
        // and the surface JSON handed to the stamper on the PACK side is
        // re-derived from the live registration on the COMPOSE side, so the
        // hash equality asserted here spans both.

        test "a CLI-minted certified surface verifies against the live module surface" {
            let manifest = tempPath ()
            let surfaceFile = tempPath ()

            try
                let key = RandomNumberGenerator.GetBytes 32

                // Pack side: the module repo's conformance run emits the
                // canonical projection of its own registration.
                let packSideModule = {
                    ServerModule.create "Sales" with
                        RoutePrefixes = [ "/api/sales"; "/api/sales/reports" ]
                }

                File.WriteAllText(surfaceFile, ModuleSurface.certificationJson (ModuleSurface.describe packSideModule))

                let code =
                    StampCommand.command.Run [
                        "--manifest"
                        manifest
                        "--module"
                        "Sales"
                        "--key-id"
                        "k1"
                        "--mac-key"
                        Convert.ToBase64String key
                        "--certified-surface"
                        surfaceFile
                    ]

                Expect.equal code ExitOk "the stamp command succeeded"

                match ModuleBindingManifest.loadCertifications manifest with
                | Error e -> failtestf "loadCertifications failed: %s" e
                | Ok map ->
                    let certification = Map.find "Sales" map

                    // Compose side: an INDEPENDENTLY-constructed registration
                    // declaring the same surface in a different order.
                    let composeSideModule = {
                        ServerModule.create "Sales" with
                            RoutePrefixes = [ "/api/sales/reports"; "/api/sales" ]
                    }

                    Expect.equal
                        (DefaultModuleBindingVerifier.verifyCertification
                            [ SymmetricAnchor("k1", key) ]
                            "Sales"
                            (ModuleSurface.describe composeSideModule)
                            certification)
                        Allowed
                        "the CLI-minted certification verifies and the live surface still matches"
            finally
                File.Delete manifest
                File.Delete surfaceFile
        }

        test "a CLI-minted certification refuses a drifted module, naming the facet" {
            let manifest = tempPath ()
            let surfaceFile = tempPath ()

            try
                let key = RandomNumberGenerator.GetBytes 32

                let certified = {
                    ServerModule.create "Sales" with
                        RoutePrefixes = [ "/api/sales" ]
                }

                File.WriteAllText(surfaceFile, ModuleSurface.certificationJson (ModuleSurface.describe certified))

                StampCommand.command.Run [
                    "--manifest"
                    manifest
                    "--module"
                    "Sales"
                    "--key-id"
                    "k1"
                    "--mac-key"
                    Convert.ToBase64String key
                    "--certified-surface"
                    surfaceFile
                ]
                |> ignore

                let drifted = {
                    ServerModule.create "Sales" with
                        RoutePrefixes = [ "/api/sales"; "/api/sales/admin" ]
                }

                match ModuleBindingManifest.loadCertifications manifest with
                | Error e -> failtestf "loadCertifications failed: %s" e
                | Ok map ->
                    match
                        DefaultModuleBindingVerifier.verifyCertification
                            [ SymmetricAnchor("k1", key) ]
                            "Sales"
                            (ModuleSurface.describe drifted)
                            (Map.find "Sales" map)
                    with
                    | Allowed -> failtest "a module that gained a route since certification must be refused"
                    | Rejected reason ->
                        Expect.stringContains
                            reason
                            "route-prefix:/api/sales/admin"
                            "the refusal names the provide added since certification"
            finally
                File.Delete manifest
                File.Delete surfaceFile
        }

        test "a CLI-minted certification carries the conformance verdict into the signed bytes" {
            let manifest = tempPath ()
            let surfaceFile = tempPath ()
            let verdictFile = tempPath ()

            try
                let key = RandomNumberGenerator.GetBytes 32

                let m = {
                    ServerModule.create "Sales" with
                        RoutePrefixes = [ "/api/sales" ]
                }

                File.WriteAllText(surfaceFile, ModuleSurface.certificationJson (ModuleSurface.describe m))

                File.WriteAllText(
                    verdictFile,
                    """{ "packVersion": "module-contract/1", "runStamp": "2026-08-27T09:00:00Z",
                         "laws": [ { "law": "server/client id parity", "passed": true, "detail": "" },
                                   { "law": "wire-TypeName uniqueness", "passed": true, "detail": "" } ] }"""
                )

                StampCommand.command.Run [
                    "--manifest"
                    manifest
                    "--module"
                    "Sales"
                    "--key-id"
                    "k1"
                    "--mac-key"
                    Convert.ToBase64String key
                    "--certified-surface"
                    surfaceFile
                    "--conformance-verdict"
                    verdictFile
                ]
                |> ignore

                match ModuleBindingManifest.loadCertifications manifest with
                | Error e -> failtestf "loadCertifications failed: %s" e
                | Ok map ->
                    let certification = Map.find "Sales" map

                    match certification.Certified.Verdict with
                    | None -> failtest "the verdict must round-trip through the manifest"
                    | Some verdict ->
                        Expect.equal verdict.PackVersion "module-contract/1" "the pack version round-trips"
                        Expect.equal (List.length verdict.Laws) 2 "both law results round-trip"
                        Expect.isTrue (verdict.Laws |> List.forall _.Passed) "and their results"

                    Expect.equal
                        (DefaultModuleBindingVerifier.verifyCertification
                            [ SymmetricAnchor("k1", key) ]
                            "Sales"
                            (ModuleSurface.describe m)
                            certification)
                        Allowed
                        "the CLI's verdict rendering matches ModuleCertificationSigning's"

                    // The verdict is INSIDE the signed bytes: flipping a law
                    // result must break the signature, not pass unnoticed.
                    let tampered = {
                        certification with
                            Certified = {
                                certification.Certified with
                                    Verdict =
                                        certification.Certified.Verdict
                                        |> Option.map (fun v -> {
                                            v with
                                                Laws = v.Laws |> List.map (fun l -> { l with Passed = false })
                                        })
                            }
                    }

                    Expect.notEqual
                        (DefaultModuleBindingVerifier.verifyCertification
                            [ SymmetricAnchor("k1", key) ]
                            "Sales"
                            (ModuleSurface.describe m)
                            tampered)
                        Allowed
                        "a flipped law result must fail closed"
            finally
                File.Delete manifest
                File.Delete surfaceFile
                File.Delete verdictFile
        }

        test "--certified-surface refuses more than one --module" {
            let manifest = tempPath ()
            let surfaceFile = tempPath ()

            try
                File.WriteAllText(
                    surfaceFile,
                    ModuleSurface.certificationJson (ModuleSurface.describe (ServerModule.create "Sales"))
                )

                let code =
                    StampCommand.command.Run [
                        "--manifest"
                        manifest
                        "--module"
                        "Sales"
                        "--module"
                        "Inventory"
                        "--key-id"
                        "k1"
                        "--mac-key"
                        Convert.ToBase64String(RandomNumberGenerator.GetBytes 32)
                        "--certified-surface"
                        surfaceFile
                    ]

                Expect.equal
                    code
                    ExitUsage
                    "a certified surface is one module's declaration set, never a shared payload"
            finally
                File.Delete manifest
                File.Delete surfaceFile
        }

        test "a stamp with no --certified-surface carries no certification (GP 11)" {
            let manifest = tempPath ()

            try
                StampCommand.command.Run [
                    "--manifest"
                    manifest
                    "--module"
                    "Sales"
                    "--key-id"
                    "k1"
                    "--mac-key"
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes 32)
                ]
                |> ignore

                match ModuleBindingManifest.loadCertifications manifest with
                | Error e -> failtestf "loadCertifications on a stamp-only manifest should be Ok empty: %s" e
                | Ok map -> Expect.isTrue (Map.isEmpty map) "no certified surface ⇒ no certification entry"
            finally
                File.Delete manifest
        }
    ]