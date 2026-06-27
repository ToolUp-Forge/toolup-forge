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
    ]