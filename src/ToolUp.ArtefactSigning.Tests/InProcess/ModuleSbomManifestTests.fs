// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.ModuleSbomManifestTests

open System
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.ArtefactSigning

// ─── Phase 216 — module SBOM inside the stamp manifest ───────────────────
//
// Exercises the SBOM half of the binding manifest: an optional, signed
// software-bill-of-materials carried beside the module stamp, verified as a
// unit with it by `DefaultModuleBindingVerifier`. The SBOM signature is the
// same stamp shape (JWS / MAC) minted under the same anchor, over the SBOM's
// canonical bytes (`ModuleSbomSigning.canonicalBytes`). The stamps here are
// produced through the public `JwsBuilder` surface a deploy-time stamper uses
// — the `toolup stamp --sbom-*` round-trip (Cli.Tests) pins the CLI's
// independent BCL minting to the same wire shape.

let private sampleComponents = [
    {
        Name = "Sales.dll"
        Version = ""
        Sha256 = "aGFzaC1vbmU"
    }
    {
        Name = "ToolUp.Platform.Server"
        Version = "0.9.4"
        Sha256 = ""
    }
]

/// Sign a module's SBOM with a fresh asymmetric (ES256) key; returns the
/// signed SBOM stamp plus the matching public-key anchor.
let private makeSbomJwsStamp
    (moduleId: string)
    (components: ModuleSbomComponent list)
    (keyId: string)
    : ModuleSbomStamp * ModuleBindingAnchor =
    let sbom = { Components = components }
    let bytes = ModuleSbomSigning.canonicalBytes moduleId sbom
    use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let header = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyId
    let input = JwsBuilder.signingInput header bytes

    let rawSig =
        ec.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    let jws = JwsBuilder.assembleDetachedJws header rawSig

    {
        Sbom = sbom
        Signature = JwsStamp jws
    },
    AsymmetricAnchor(keyId, EcdsaP256, ec.ExportSubjectPublicKeyInfo())

/// Sign a module's SBOM with a fresh symmetric (HMAC-SHA256) key.
let private makeSbomMacStamp
    (moduleId: string)
    (components: ModuleSbomComponent list)
    (keyId: string)
    : ModuleSbomStamp * ModuleBindingAnchor =
    let sbom = { Components = components }
    let bytes = ModuleSbomSigning.canonicalBytes moduleId sbom
    let key = RandomNumberGenerator.GetBytes 32
    use hmac = new HMACSHA256(key)
    let tag = JwsBuilder.base64UrlEncode (hmac.ComputeHash bytes)

    {
        Sbom = sbom
        Signature = MacStamp(keyId, tag)
    },
    SymmetricAnchor(keyId, key)

/// A JSON manifest with one entry carrying both an asymmetric stamp and an
/// asymmetric SBOM signature — the exact shape `toolup stamp --sbom-*` writes.
let private manifestJson (moduleId: string) (sbomStamp: ModuleSbomStamp) : string =
    let detachedJws =
        match sbomStamp.Signature with
        | JwsStamp j -> j
        | MacStamp _ -> failtest "this helper builds a JWS-signed entry"

    let componentsJson =
        sbomStamp.Sbom.Components
        |> List.map (fun c -> sprintf """{ "name": "%s", "version": "%s", "sha256": "%s" }""" c.Name c.Version c.Sha256)
        |> String.concat ", "

    // The module stamp itself is irrelevant to the SBOM reader test, but a
    // realistic entry carries one — reuse the SBOM JWS string as a stand-in.
    sprintf
        """{ "version": 1, "bindings": { "%s": { "kind": "jws", "detachedJws": "%s", "sbom": { "components": [ %s ] }, "sbomSig": { "kind": "jws", "detachedJws": "%s" } } } }"""
        moduleId
        detachedJws
        componentsJson
        detachedJws

let tests =
    testList "Phase 216 — module SBOM in stamp manifest" [

        // ── acceptance: a signed SBOM verifies as a unit ─────────────────
        test "asymmetric SBOM stamp verifies under its anchor" {
            let sbomStamp, anchor = makeSbomJwsStamp "Sales" sampleComponents "ec1"

            Expect.equal
                (DefaultModuleBindingVerifier.verifySbom [ anchor ] "Sales" sbomStamp)
                Allowed
                "a correctly signed SBOM verifies"
        }

        test "symmetric SBOM stamp verifies under its anchor" {
            let sbomStamp, anchor = makeSbomMacStamp "Inventory" sampleComponents "k1"

            Expect.equal
                (DefaultModuleBindingVerifier.verifySbom [ anchor ] "Inventory" sbomStamp)
                Allowed
                "a correctly MAC'd SBOM verifies"
        }

        // ── acceptance: tampering with any component hash fails ──────────
        test "tampering with a component hash fails verification" {
            let sbomStamp, anchor = makeSbomJwsStamp "Sales" sampleComponents "ec1"

            // Flip one component's content hash; the signature was over the
            // original, so the canonical bytes no longer match.
            let tampered = {
                sbomStamp with
                    Sbom = {
                        Components =
                            sbomStamp.Sbom.Components
                            |> List.mapi (fun i c -> if i = 0 then { c with Sha256 = "dGFtcGVyZWQ" } else c)
                    }
            }

            Expect.notEqual
                (DefaultModuleBindingVerifier.verifySbom [ anchor ] "Sales" tampered)
                Allowed
                "a mutated component hash must fail closed"
        }

        test "adding a component fails verification" {
            let sbomStamp, anchor = makeSbomMacStamp "Inventory" sampleComponents "k1"

            let extended = {
                sbomStamp with
                    Sbom = {
                        Components =
                            sbomStamp.Sbom.Components
                            @ [
                                {
                                    Name = "Sneaky.dll"
                                    Version = ""
                                    Sha256 = "ZXZpbA"
                                }
                            ]
                    }
            }

            Expect.notEqual
                (DefaultModuleBindingVerifier.verifySbom [ anchor ] "Inventory" extended)
                Allowed
                "an injected component must fail closed"
        }

        // ── bound to the module id (no cross-module replay) ──────────────
        test "an SBOM stamp minted for one module does not verify for another" {
            let sbomStamp, anchor = makeSbomJwsStamp "Sales" sampleComponents "ec1"

            Expect.notEqual
                (DefaultModuleBindingVerifier.verifySbom [ anchor ] "Inventory" sbomStamp)
                Allowed
                "the SBOM signature is bound to the module id"
        }

        test "a wrong anchor fails closed" {
            let sbomStamp, _ = makeSbomJwsStamp "Sales" sampleComponents "ec1"
            let _, otherAnchor = makeSbomJwsStamp "Sales" sampleComponents "ec2"

            Expect.notEqual
                (DefaultModuleBindingVerifier.verifySbom [ otherAnchor ] "Sales" sbomStamp)
                Allowed
                "an unrelated anchor cannot verify the SBOM"
        }

        // ── reader round-trip (parse → verify) ───────────────────────────
        test "parseSboms recovers a signed SBOM that then verifies" {
            let sbomStamp, anchor = makeSbomJwsStamp "Sales" sampleComponents "ec1"
            let json = manifestJson "Sales" sbomStamp

            match ModuleBindingManifest.parseSboms json with
            | Error e -> failtestf "parseSboms failed: %s" e
            | Ok map ->
                match Map.tryFind "Sales" map with
                | None -> failtest "no SBOM recovered for 'Sales'"
                | Some recovered ->
                    Expect.equal
                        (DefaultModuleBindingVerifier.verifySbom [ anchor ] "Sales" recovered)
                        Allowed
                        "the SBOM read back from the manifest verifies"
        }

        test "parseSboms surfaces a tampered component hash as a deny" {
            let sbomStamp, anchor = makeSbomJwsStamp "Sales" sampleComponents "ec1"

            // Build the manifest, then corrupt a component hash in the JSON
            // text after the signature was computed.
            let json =
                (manifestJson "Sales" sbomStamp).Replace("aGFzaC1vbmU", "dGFtcGVyZWQtaW4tdHJhbnNpdA")

            match ModuleBindingManifest.parseSboms json with
            | Error e -> failtestf "parseSboms failed: %s" e
            | Ok map ->
                let recovered = Map.find "Sales" map

                Expect.notEqual
                    (DefaultModuleBindingVerifier.verifySbom [ anchor ] "Sales" recovered)
                    Allowed
                    "an SBOM whose component hash was edited in transit must fail closed"
        }

        // ── GP 13: a stamp-only manifest yields no SBOM, unchanged ───────
        test "a stamp-only manifest entry yields no SBOM (GP 13 byte-identical)" {
            let json =
                """{ "version": 1, "bindings": { "Sales": { "kind": "mac", "keyId": "k1", "tag": "AAAA" } } }"""

            // The Phase-166 stamp reader still parses the entry.
            match ModuleBindingManifest.parse json with
            | Error e -> failtestf "stamp-only manifest should parse: %s" e
            | Ok stamps -> Expect.isTrue (Map.containsKey "Sales" stamps) "the stamp is still read"

            // But there is no SBOM section, so the SBOM map is empty.
            match ModuleBindingManifest.parseSboms json with
            | Error e -> failtestf "parseSboms on a stamp-only manifest should be Ok empty: %s" e
            | Ok sboms -> Expect.isTrue (Map.isEmpty sboms) "no SBOM section ⇒ no SBOM entry"
        }

        test "an SBOM section with no signature fails closed" {
            let json =
                """{ "version": 1, "bindings": { "Sales": { "kind": "mac", "keyId": "k1", "tag": "AAAA", "sbom": { "components": [] } } } }"""

            match ModuleBindingManifest.parseSboms json with
            | Ok _ -> failtest "an sbom with no sbomSig must be an Error, not silently dropped"
            | Error _ -> ()
        }
    ]