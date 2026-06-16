// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.ModuleBindingVerifierTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.ArtefactSigning

// ─── Phase 165 — module-binding gate contract tests ─────────────────────
//
// Exercises `DefaultModuleBindingVerifier` (the crypto) and the
// `ServerApp.addModule` wiring (the gate). The asymmetric stamp is produced
// through the public `JwsBuilder` surface a deploy-time stamper would use;
// the symmetric stamp is a plain base64url HMAC-SHA256 tag. No live crypto
// service is involved — local keys stand in for the consumer keyring.

// ── stamp producers (the deploy-time stamper's job; reproduced here) ─────

/// The canonical bytes a module-binding stamp covers: the UTF-8 module id.
let private canonical (moduleId: string) : byte[] = Encoding.UTF8.GetBytes moduleId

/// Produce an asymmetric (ES256 detached-JWS) stamp over `moduleId` plus the
/// matching asymmetric anchor. Uses only the public `JwsBuilder` surface.
let private makeJwsStamp (moduleId: string) (keyId: string) : ModuleBindingStamp * ModuleBindingAnchor =
    use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let encodedHeader = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyId
    let input = JwsBuilder.signingInput encodedHeader (canonical moduleId)
    // IeeeP1363 = raw r‖s, the JWS ES256 signature shape.
    let rawSig =
        ec.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    let jws = JwsBuilder.assembleDetachedJws encodedHeader rawSig
    JwsStamp jws, AsymmetricAnchor(keyId, EcdsaP256, ec.ExportSubjectPublicKeyInfo())

/// Produce a symmetric (HMAC-SHA256) stamp over `moduleId` plus the matching
/// symmetric anchor.
let private makeMacStamp (moduleId: string) (keyId: string) : ModuleBindingStamp * ModuleBindingAnchor =
    let key = RandomNumberGenerator.GetBytes 32
    use hmac = new HMACSHA256(key)
    let tag = JwsBuilder.base64UrlEncode (hmac.ComputeHash(canonical moduleId))
    MacStamp(keyId, tag), SymmetricAnchor(keyId, key)

// ── addModule wiring helpers ─────────────────────────────────────────────

let private loads (app: ServerApp) (moduleId: string) : bool =
    app.ModuleNames |> List.contains moduleId

let private addOf (verifier: IModuleBindingVerifier option) (m: ServerModule) : ServerApp =
    let baseApp =
        match verifier with
        | Some v -> ServerApp.empty |> ServerApp.withModuleBindingVerifier v
        | None -> ServerApp.empty

    baseApp |> ServerApp.addModule m

let tests =
    testList "Phase 165 — module-binding gate" [

        // ── GP 13 / acceptance: no binding configured, no stamp ──────────
        test "unbound module loads (no verifier, no stamp) — GP 13 byte-identical" {
            let app = addOf None (ServerModule.create "Alpha")
            Expect.isTrue (loads app "Alpha") "an unstamped module on a verifier-less app loads unchanged"
        }

        test "verifier configured but module unstamped (absent stamp) loads" {
            let verifier = DefaultModuleBindingVerifier.create []
            let app = addOf (Some verifier) (ServerModule.create "Beta")

            Expect.isTrue
                (loads app "Beta")
                "absent stamp is Allowed under a permitting verifier (even with no anchors)"
        }

        // ── correctly-stamped modules load ───────────────────────────────
        test "correctly asymmetric-stamped module loads" {
            let stamp, anchor = makeJwsStamp "Gamma" "asym-v1"
            let verifier = DefaultModuleBindingVerifier.create [ anchor ]

            let app =
                addOf (Some verifier) (ServerModule.create "Gamma" |> ServerModule.withBindingStamp stamp)

            Expect.isTrue (loads app "Gamma") "a JWS stamp verifying under its anchor admits the module"
        }

        test "correctly symmetric-stamped module loads" {
            let stamp, anchor = makeMacStamp "Delta" "sym-v1"
            let verifier = DefaultModuleBindingVerifier.create [ anchor ]

            let app =
                addOf (Some verifier) (ServerModule.create "Delta" |> ServerModule.withBindingStamp stamp)

            Expect.isTrue (loads app "Delta") "an HMAC stamp verifying under its anchor admits the module"
        }

        // ── acceptance: ≥1 symmetric + ≥1 asymmetric in one deployment ───
        test "multiple trust anchors (asymmetric + symmetric) verify in one deployment" {
            let jwsStamp, asymAnchor = makeJwsStamp "Epsilon" "asym-v1"
            let macStamp, symAnchor = makeMacStamp "Zeta" "sym-v1"
            // One verifier carrying both anchor kinds.
            let verifier = DefaultModuleBindingVerifier.create [ asymAnchor; symAnchor ]

            let appAsym =
                addOf (Some verifier) (ServerModule.create "Epsilon" |> ServerModule.withBindingStamp jwsStamp)

            let appSym =
                addOf (Some verifier) (ServerModule.create "Zeta" |> ServerModule.withBindingStamp macStamp)

            Expect.isTrue (loads appAsym "Epsilon") "the asymmetric stamp verifies under the asymmetric anchor"
            Expect.isTrue (loads appSym "Zeta") "the symmetric stamp verifies under the symmetric anchor"
        }

        // ── tampered / foreign / replayed stamps are rejected ────────────
        test "replayed stamp (minted for another module) is rejected" {
            // Stamp is signed over "Other" but presented for module "Theta";
            // addModule recomputes canonical bytes from "Theta", so it fails.
            let stamp, anchor = makeJwsStamp "Other" "asym-v1"
            let verifier = DefaultModuleBindingVerifier.create [ anchor ]

            let app =
                addOf (Some verifier) (ServerModule.create "Theta" |> ServerModule.withBindingStamp stamp)

            Expect.isFalse (loads app "Theta") "a stamp minted for a different module must not admit this one"
        }

        test "foreign-stamped module (key not in anchor set) is rejected" {
            // The module is stamped under one key; the verifier holds a
            // different, unrelated anchor.
            let stamp, _ = makeJwsStamp "Iota" "asym-v1"
            let _, otherAnchor = makeJwsStamp "Iota" "asym-v2"
            let verifier = DefaultModuleBindingVerifier.create [ otherAnchor ]

            let app =
                addOf (Some verifier) (ServerModule.create "Iota" |> ServerModule.withBindingStamp stamp)

            Expect.isFalse (loads app "Iota") "a stamp no configured anchor can verify drops the module"
        }

        test "tampered MAC tag is rejected" {
            let _, anchor = makeMacStamp "Kappa" "sym-v1"
            // Present a bogus tag under the right key id.
            let bogus = MacStamp("sym-v1", JwsBuilder.base64UrlEncode (Array.zeroCreate 32))
            let verifier = DefaultModuleBindingVerifier.create [ anchor ]

            let app =
                addOf (Some verifier) (ServerModule.create "Kappa" |> ServerModule.withBindingStamp bogus)

            Expect.isFalse (loads app "Kappa") "an HMAC tag that does not recompute drops the module"
        }

        // ── fail-closed cases (load-bearing rule) ────────────────────────
        test "absent anchor + present stamp is rejected (empty anchor set)" {
            let stamp, _ = makeJwsStamp "Lambda" "asym-v1"
            // Verifier is configured but holds NO anchors.
            let verifier = DefaultModuleBindingVerifier.create []

            let app =
                addOf (Some verifier) (ServerModule.create "Lambda" |> ServerModule.withBindingStamp stamp)

            Expect.isFalse (loads app "Lambda") "a present stamp with no anchor to verify it fails closed"
        }

        test "present stamp + NO verifier configured is rejected (fail-closed regardless)" {
            let stamp, _ = makeJwsStamp "Mu" "asym-v1"
            // No verifier on the app at all — the addModule None,Some arm.
            let app =
                addOf None (ServerModule.create "Mu" |> ServerModule.withBindingStamp stamp)

            Expect.isFalse
                (loads app "Mu")
                "a stamped module is self-protecting: it fails closed on a deployment with no verifier"
        }

        // ── direct verifier-level outcomes (unit of the crypto) ──────────
        test "verifier returns Allowed/Rejected at the unit level" {
            let stamp, anchor = makeJwsStamp "Nu" "asym-v1"
            let verifier = DefaultModuleBindingVerifier.create [ anchor ]

            Expect.equal (verifier.Verify("Nu", None)) Allowed "absent stamp → Allowed"
            Expect.equal (verifier.Verify("Nu", Some stamp)) Allowed "matching stamp → Allowed"

            match verifier.Verify("Nu", Some(JwsStamp "not.a.jws")) with
            | Rejected _ -> ()
            | Allowed -> failtest "a malformed JWS must be Rejected"
        }
    ]