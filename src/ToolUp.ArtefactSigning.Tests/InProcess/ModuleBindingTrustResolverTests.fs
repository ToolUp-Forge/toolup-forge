// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.ModuleBindingTrustResolverTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning
open ToolUp.ArtefactSigning.Tests.Support.InMemoryStores

// ─── Phase 170 — trust-anchor config → verifier (resolver + validator) ──
//
// Exercises `ModuleBindingTrustResolver.resolve` (config refs + ISecretStore
// → verifier) and `ModuleBindingTrustValidator` (fail-closed preflight). The
// stamps are produced the same way a deploy-time stamper would (HMAC tag /
// detached ES256 JWS over the canonical module bytes).

let private canonical (moduleId: string) = Encoding.UTF8.GetBytes moduleId

let private seedSecret (store: ISecretStore) (scope: string) (key: string) (value: string) =
    store.SetSecret(scope, key, value) |> Async.RunSynchronously |> ignore

let private macStamp (moduleId: string) (keyId: string) (key: byte[]) : ModuleBindingStamp =
    use hmac = new HMACSHA256(key)
    MacStamp(keyId, JwsBuilder.base64UrlEncode (hmac.ComputeHash(canonical moduleId)))

let private resolveVerifier (config: ModuleBindingTrustConfig) (store: ISecretStore) =
    ModuleBindingTrustResolver.resolve config store |> Async.RunSynchronously

let tests =
    testList "Phase 170 — module-binding trust resolver" [

        test "symmetric anchor: secret resolves → verifier admits the matching MAC stamp" {
            let store = InMemorySecretStore() :> ISecretStore
            let key = RandomNumberGenerator.GetBytes 32
            seedSecret store "_platform" "mac-1" (Convert.ToBase64String key)

            let config = {
                Anchors = [ SymmetricAnchorRef("k1", "_platform", "mac-1") ]
                AllowUnbound = true
            }

            match resolveVerifier config store with
            | Error e -> failtestf "expected Ok verifier, got Error %s" e
            | Ok verifier ->
                let stamp = macStamp "Sales" "k1" key
                Expect.equal (verifier.Verify("Sales", Some stamp)) Allowed "matching MAC stamp admitted"
                Expect.equal (verifier.Verify("Sales", None)) Allowed "unstamped allowed (AllowUnbound=true)"
        }

        test "symmetric anchor: missing secret → resolve fails closed (Error)" {
            let store = InMemorySecretStore() :> ISecretStore

            let config = {
                Anchors = [ SymmetricAnchorRef("k1", "_platform", "absent") ]
                AllowUnbound = true
            }

            match resolveVerifier config store with
            | Ok _ -> failtest "an unresolvable symmetric secret must fail closed, not build a verifier"
            | Error e -> Expect.stringContains e "did not resolve" "names the unresolved secret"
        }

        test "asymmetric anchor: public key → verifier admits the matching JWS stamp" {
            let store = InMemorySecretStore() :> ISecretStore
            use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let spkiB64 = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo())

            let config = {
                Anchors = [ AsymmetricAnchorRef("ec1", "EcdsaP256", spkiB64) ]
                AllowUnbound = true
            }

            // Mint an ES256 detached JWS over the module bytes (the stamper path).
            let encodedHeader = JwsBuilder.protectedHeaderEncoded EcdsaP256 "ec1"
            let input = JwsBuilder.signingInput encodedHeader (canonical "Inventory")

            let rawSig =
                ec.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

            let stamp = JwsStamp(JwsBuilder.assembleDetachedJws encodedHeader rawSig)

            match resolveVerifier config store with
            | Error e -> failtestf "expected Ok verifier, got Error %s" e
            | Ok verifier ->
                Expect.equal (verifier.Verify("Inventory", Some stamp)) Allowed "matching JWS stamp admitted"
        }

        test "AllowUnbound=false → an unstamped module is rejected" {
            let store = InMemorySecretStore() :> ISecretStore
            let key = RandomNumberGenerator.GetBytes 32
            seedSecret store "_platform" "mac-1" (Convert.ToBase64String key)

            let config = {
                Anchors = [ SymmetricAnchorRef("k1", "_platform", "mac-1") ]
                AllowUnbound = false
            }

            match resolveVerifier config store with
            | Error e -> failtestf "expected Ok verifier, got Error %s" e
            | Ok verifier ->
                match verifier.Verify("Sales", None) with
                | Rejected _ -> ()
                | Allowed -> failtest "AllowUnbound=false must reject an unstamped module"

                Expect.equal
                    (verifier.Verify("Sales", Some(macStamp "Sales" "k1" key)))
                    Allowed
                    "a valid stamp still admitted"
        }

        test "validator: resolvable anchors → Ok; missing secret → Error; no anchors → Ok" {
            let store = InMemorySecretStore() :> ISecretStore
            seedSecret store "_platform" "mac-1" (Convert.ToBase64String(RandomNumberGenerator.GetBytes 32))

            let validate (config: ModuleBindingTrustConfig) =
                (ModuleBindingTrustValidator(config, store) :> ConfigValidation.IConfigValidator).Validate()
                |> Async.RunSynchronously

            let resolvable = {
                Anchors = [ SymmetricAnchorRef("k1", "_platform", "mac-1") ]
                AllowUnbound = true
            }

            let unresolvable = {
                Anchors = [ SymmetricAnchorRef("k1", "_platform", "absent") ]
                AllowUnbound = true
            }

            Expect.equal (validate resolvable) ConfigValidation.ValidationResult.Ok "resolvable anchors pass preflight"

            match validate unresolvable with
            | ConfigValidation.ValidationResult.Error _ -> ()
            | other -> failtestf "an unresolvable anchor must fail preflight (Error), got %A" other

            Expect.equal
                (validate ModuleBindingTrustConfig.defaults)
                ConfigValidation.ValidationResult.Ok
                "no anchors → nothing to validate"
        }
    ]