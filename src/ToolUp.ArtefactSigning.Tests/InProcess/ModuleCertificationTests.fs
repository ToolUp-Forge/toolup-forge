// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.ModuleCertificationTests

open System
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.ArtefactSigning

// ─── Phase 589 — certified-surface module binding stamp ──────────────────
//
// Phase 165/166 bind *that* a module is stamped and Phase 216 records *what is
// inside it*; neither says anything about what the module OFFERS a composition.
// This pack exercises the third: the module's `ModuleSurface` (Phase 581)
// projected canonically, hashed, signed under the same anchor as its stamp, and
// re-derived at compose so a drifted module is refused with the drifted facet
// NAMED.
//
// The falsifier is the drift refusal, so it is exercised in both directions
// (added provide, added need) and its message is asserted to carry the facet —
// a gate that only ever agrees with itself proves nothing.

// ── witnesses ─────────────────────────────────────────────────────────

/// The certified module. Deliberately built only from primitives so the witness
/// carries no dependency on a domain shape.
let private salesModule () : ServerModule = {
    ServerModule.create "Sales" with
        RoutePrefixes = [ "/api/sales"; "/api/sales/reports" ]
}

/// The SAME declarations, constructed independently and DECLARED IN A DIFFERENT
/// ORDER — a separate `create` call on a separate value.
///
/// The reversed order is what makes the determinism assertion load-bearing
/// rather than a repeatability check: two runs of the same code over the same
/// list would agree even with no canonicalisation at all. Certifying on one
/// machine and composing on another must agree because the projection is
/// CANONICAL, not because both happened to iterate the same list.
let private salesModuleRebuilt () : ServerModule =
    let m = ServerModule.create "Sales"

    {
        m with
            RoutePrefixes = [ "/api/sales/reports"; "/api/sales" ]
    }

/// The certified module after a provide was ADDED — a third route prefix. This
/// is the acceptance criterion's "a provide added since certification".
let private salesModuleWithExtraRoute () : ServerModule = {
    ServerModule.create "Sales" with
        RoutePrefixes = [ "/api/sales"; "/api/sales/reports"; "/api/sales/admin" ]
}

/// The certified module after a NEED appeared: declaring a config schema
/// implies an `IConfigStore`, so the drift shows on both facets at once.
let private salesModuleWithConfig () : ServerModule = {
    ServerModule.create "Sales" with
        RoutePrefixes = [ "/api/sales"; "/api/sales/reports" ]
        ConfigSchema =
            Some(
                ModuleConfigSchema.ofFields [
                    {
                        Key = "retention-days"
                        DisplayName = "Retention (days)"
                        Description = None
                        Kind = ConfigFieldKind.Int(Some 1, Some 365)
                        Required = false
                        DefaultJson = "30"
                    }
                ]
            )
}

let private sampleVerdict: ModuleConformanceVerdict = {
    PackVersion = "module-contract/1"
    RunStamp = "2026-08-27T09:00:00Z"
    Laws = [
        {
            Law = "server/client id parity"
            Passed = true
            Detail = ""
        }
        {
            Law = "wire-TypeName uniqueness"
            Passed = true
            Detail = ""
        }
    ]
}

// ── stamp minting (the public JwsBuilder surface a stamper uses) ───────

/// Sign a certification with a fresh asymmetric (ES256) key; returns the signed
/// certification plus the matching public-key anchor.
let private makeCertificationJws
    (moduleId: string)
    (certified: CertifiedModuleSurface)
    (keyId: string)
    : ModuleCertificationStamp * ModuleBindingAnchor =
    let bytes = ModuleCertificationSigning.canonicalBytes moduleId certified
    use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let header = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyId
    let input = JwsBuilder.signingInput header bytes

    let rawSig =
        ec.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    {
        Certified = certified
        Signature = JwsStamp(JwsBuilder.assembleDetachedJws header rawSig)
    },
    AsymmetricAnchor(keyId, EcdsaP256, ec.ExportSubjectPublicKeyInfo())

/// Sign a certification with a fresh symmetric (HMAC-SHA256) key.
let private makeCertificationMac
    (moduleId: string)
    (certified: CertifiedModuleSurface)
    (keyId: string)
    : ModuleCertificationStamp * ModuleBindingAnchor =
    let bytes = ModuleCertificationSigning.canonicalBytes moduleId certified
    let key = RandomNumberGenerator.GetBytes 32
    use hmac = new HMACSHA256(key)

    {
        Certified = certified
        Signature = MacStamp(keyId, JwsBuilder.base64UrlEncode (hmac.ComputeHash bytes))
    },
    SymmetricAnchor(keyId, key)

/// Certify a module's server-derived surface, with a verdict.
let private certifyModule (m: ServerModule) : CertifiedModuleSurface =
    ModuleSurface.describe m |> ModuleSurface.certifyWith sampleVerdict

let private rejectionReason (outcome: BindingOutcome) : string =
    match outcome with
    | Rejected reason -> reason
    | Allowed -> failtest "expected a Rejected outcome, got Allowed"

let tests =
    testList "Phase 589 — certified-surface module binding" [

        // ── determinism: the hash is a function of the declarations ──────
        //
        // The acceptance criterion asks for determinism ACROSS pack and
        // compose, not merely within one process — so the two sides are
        // derived from independently-constructed registrations, and the
        // pack-side value additionally makes the round trip through the
        // signed payload before the compose-side value is compared to it.
        test "the certified hash is identical across two independent derivations" {
            let packSide = certifyModule (salesModule ())

            let composeSide =
                ModuleSurface.certificationHash (ModuleSurface.describe (salesModuleRebuilt ()))

            Expect.equal
                composeSide
                packSide.SurfaceHash
                "an independently-built identical registration certifies identically"

            Expect.equal
                (ModuleSurface.certificationJson (ModuleSurface.describe (salesModuleRebuilt ())))
                packSide.SurfaceJson
                "and the canonical JSON is byte-identical, not merely hash-equal"
        }

        test "the projection is canonically ordered and de-duplicated by the projection itself" {
            // `ModuleSurface.describe` already orders its ENTRIES, so an
            // order-only witness cannot tell whether the projection
            // canonicalises or merely inherits. De-duplication can: a route
            // prefix declared twice reaches `Provides` as two identical
            // entries, and only the projection collapses them.
            let noisy = {
                ServerModule.create "Sales" with
                    RoutePrefixes = [ "/api/sales/reports"; "/api/sales"; "/api/sales" ]
            }

            let projection = ModuleSurface.project (ModuleSurface.describe noisy)

            Expect.equal
                (ModuleSurface.certificationHash (ModuleSurface.describe noisy))
                (ModuleSurface.certificationHash (ModuleSurface.describe (salesModule ())))
                "a repeated declaration certifies identically to a single one"

            Expect.equal
                projection.Provides
                (projection.Provides
                 |> List.distinct
                 |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)))
                "the projected provides are ordinal-sorted and distinct"

            Expect.equal
                projection.Needs
                (projection.Needs
                 |> List.distinct
                 |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)))
                "and so are the needs"
        }

        test "the declared hash is the hash of the declared JSON" {
            let certified = certifyModule (salesModule ())

            Expect.equal
                (ModuleSurface.certificationHashOfJson certified.SurfaceJson)
                certified.SurfaceHash
                "certify's two halves agree by construction"
        }

        test "the canonical projection round-trips through its JSON" {
            let live = ModuleSurface.describe (salesModule ())

            match ModuleSurface.parseProjection (ModuleSurface.certificationJson live) with
            | Error e -> failtestf "the canonical JSON must parse back: %s" e
            | Ok parsed -> Expect.equal parsed (ModuleSurface.project live) "parse ∘ render is the identity"
        }

        // ── acceptance: certified-and-matching binds ─────────────────────
        test "an asymmetric certification over the live surface binds" {
            let m = salesModule ()
            let certification, anchor = makeCertificationJws "Sales" (certifyModule m) "ec1"

            Expect.equal
                (DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Sales"
                    (ModuleSurface.describe m)
                    certification)
                Allowed
                "a module whose live surface matches its certification composes"
        }

        test "a symmetric certification over the live surface binds" {
            let m = salesModule ()
            let certification, anchor = makeCertificationMac "Sales" (certifyModule m) "k1"

            Expect.equal
                (DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Sales"
                    (ModuleSurface.describe m)
                    certification)
                Allowed
                "the MAC path admits an unchanged surface too"
        }

        test "a certification carrying no verdict still binds" {
            let m = salesModule ()
            let certified = ModuleSurface.describe m |> ModuleSurface.certify
            let certification, anchor = makeCertificationJws "Sales" certified "ec1"

            Expect.isNone certified.Verdict "certify records no verdict"

            Expect.equal
                (DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Sales"
                    (ModuleSurface.describe m)
                    certification)
                Allowed
                "the verdict is optional — a surface-only certification is valid"
        }

        // ── acceptance: certified-but-drifted refuses, naming the facet ──
        //
        // The falsifier. A gate that admitted a drifted surface would be
        // indistinguishable from no gate at all, so this is the case the phase
        // is built around — and the message is asserted to NAME the drift, not
        // merely to be a refusal.
        test "an ADDED provide is refused with the drifted facet named" {
            let certification, anchor =
                makeCertificationJws "Sales" (certifyModule (salesModule ())) "ec1"

            let outcome =
                DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Sales"
                    (ModuleSurface.describe (salesModuleWithExtraRoute ()))
                    certification

            let reason = rejectionReason outcome
            Expect.stringContains reason "drifted from its certified surface" "the refusal says what happened"
            Expect.stringContains reason "provides added" "the refusal names the facet and the direction"
            Expect.stringContains reason "route-prefix:/api/sales/admin" "the refusal names the declaration itself"
        }

        test "a REMOVED provide is refused with the drifted facet named" {
            // Certify the wider surface, compose the narrower one — the mirror
            // of the case above, and the one a module that DROPPED a capability
            // walks into.
            let certification, anchor =
                makeCertificationJws "Sales" (certifyModule (salesModuleWithExtraRoute ())) "ec1"

            let reason =
                DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Sales"
                    (ModuleSurface.describe (salesModule ()))
                    certification
                |> rejectionReason

            Expect.stringContains reason "provides removed" "a vanished provide is named as a removal"
            Expect.stringContains reason "route-prefix:/api/sales/admin" "and the declaration is named"
        }

        test "an added NEED is refused and named on the needs facet" {
            let certification, anchor =
                makeCertificationJws "Sales" (certifyModule (salesModule ())) "ec1"

            let reason =
                DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Sales"
                    (ModuleSurface.describe (salesModuleWithConfig ()))
                    certification
                |> rejectionReason

            Expect.stringContains reason "needs added" "a newly-implied substrate dependency is a need drift"
            Expect.stringContains reason "substrate:IConfigStore" "and the implied interface is named"
        }

        test "a described-side mismatch is reported once, not as phantom removals" {
            // Certifying `server+client` and re-deriving `server` differs in
            // nearly every entry. The honest report is the one fact that
            // explains them all.
            let m = salesModule ()

            let certifiedBothSides =
                ModuleSurface.describeWith (m, Some(box {| Definition = null |}))
                |> ModuleSurface.certify

            let certification, anchor = makeCertificationJws "Sales" certifiedBothSides "ec1"

            let reason =
                DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Sales"
                    (ModuleSurface.describe m)
                    certification
                |> rejectionReason

            Expect.stringContains reason "described changed" "the described facet is what moved"
            Expect.isFalse (reason.Contains "provides removed") "and the per-entry noise is suppressed"
        }

        // ── the certification is authentic before it is believed ─────────
        test "a tampered certified surface fails the signature check" {
            let m = salesModule ()
            let certification, anchor = makeCertificationJws "Sales" (certifyModule m) "ec1"

            // Re-certify against the drifted module and splice the payload in
            // under the original signature — the shape an attacker who wanted
            // to widen a module's surface would try.
            let forged = {
                certification with
                    Certified = certifyModule (salesModuleWithExtraRoute ())
            }

            let reason =
                DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Sales"
                    (ModuleSurface.describe (salesModuleWithExtraRoute ()))
                    forged
                |> rejectionReason

            Expect.stringContains reason "no configured trust anchor" "a spliced payload fails closed on the signature"
        }

        test "a certification minted for one module does not verify for another" {
            let m = salesModule ()
            let certification, anchor = makeCertificationJws "Sales" (certifyModule m) "ec1"

            Expect.notEqual
                (DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Inventory"
                    (ModuleSurface.describe m)
                    certification)
                Allowed
                "the certification signature is bound to the module id"
        }

        test "a wrong anchor fails closed" {
            let m = salesModule ()
            let certified = certifyModule m
            let certification, _ = makeCertificationJws "Sales" certified "ec1"
            let _, otherAnchor = makeCertificationJws "Sales" certified "ec2"

            Expect.notEqual
                (DefaultModuleBindingVerifier.verifyCertification
                    [ otherAnchor ]
                    "Sales"
                    (ModuleSurface.describe m)
                    certification)
                Allowed
                "an unrelated anchor cannot verify the certification"
        }

        test "a certification whose declared hash disagrees with its JSON is refused" {
            // Both halves are signed, so this is not tampering — it is a stamper
            // that computed one of them differently. Refuse rather than pick a
            // half to trust.
            let m = salesModule ()

            let inconsistent = {
                certifyModule m with
                    SurfaceHash = "bm90LXRoZS1oYXNo"
            }

            let certification, anchor = makeCertificationJws "Sales" inconsistent "ec1"

            let reason =
                DefaultModuleBindingVerifier.verifyCertification
                    [ anchor ]
                    "Sales"
                    (ModuleSurface.describe m)
                    certification
                |> rejectionReason

            Expect.stringContains
                reason
                "does not match its own surface JSON"
                "an internally-inconsistent certification is refused before the drift check"
        }

        // ── the compose gate ─────────────────────────────────────────────
        test "the gate admits a module whose surface still matches" {
            let m = salesModule ()
            let certification, anchor = makeCertificationJws "Sales" (certifyModule m) "ec1"
            let verifier = DefaultModuleBindingVerifier.certificationVerifier [ anchor ]
            let certifications = Map.ofList [ "Sales", certification ]

            let admitted, refused =
                ModuleCertificationGate.partition (Some verifier) certifications [ m ]

            Expect.equal (List.length admitted) 1 "the module composes"
            Expect.isEmpty refused "with nothing refused"
        }

        test "the gate refuses a drifted module and names the facet" {
            let certification, anchor =
                makeCertificationJws "Sales" (certifyModule (salesModule ())) "ec1"

            let verifier = DefaultModuleBindingVerifier.certificationVerifier [ anchor ]
            let certifications = Map.ofList [ "Sales", certification ]

            let admitted, refused =
                ModuleCertificationGate.partition (Some verifier) certifications [ salesModuleWithExtraRoute () ]

            Expect.isEmpty admitted "a drifted module does not compose"

            match refused with
            | [ (name, reason) ] ->
                Expect.equal name "Sales" "the refusal names the module"
                Expect.stringContains reason "route-prefix:/api/sales/admin" "and the drifted declaration"
            | other -> failtestf "expected exactly one refusal, got %d" (List.length other)
        }

        test "a certified module on a deployment with no verifier fails closed" {
            let m = salesModule ()
            let certification, _ = makeCertificationJws "Sales" (certifyModule m) "ec1"
            let certifications = Map.ofList [ "Sales", certification ]

            let reason = ModuleCertificationGate.decide None certifications m |> rejectionReason

            Expect.stringContains
                reason
                "no module-certification verifier configured"
                "a certified module is self-protecting, like a stamped one"
        }

        // ── GP 11 / GP 13: an uncertified module is unaffected ───────────
        test "an uncertified module is admitted with no verifier and no certifications" {
            let m = salesModule ()

            Expect.equal (ModuleCertificationGate.decide None Map.empty m) Allowed "the pre-589 path"

            let admitted, refused =
                ModuleCertificationGate.partition None Map.empty [ m; salesModuleWithExtraRoute () ]

            Expect.equal (List.length admitted) 2 "every module composes"
            Expect.isEmpty refused "nothing is refused"
        }

        test "an uncertified module is admitted even where OTHER modules are certified" {
            let certified = salesModule ()

            let certification, anchor =
                makeCertificationJws "Sales" (certifyModule certified) "ec1"

            let verifier = DefaultModuleBindingVerifier.certificationVerifier [ anchor ]
            let certifications = Map.ofList [ "Sales", certification ]

            let uncertified = {
                ServerModule.create "Inventory" with
                    RoutePrefixes = [ "/api/inventory" ]
            }

            let admitted, refused =
                ModuleCertificationGate.partition (Some verifier) certifications [ certified; uncertified ]

            Expect.equal (List.length admitted) 2 "the gate is per-module, not per-deployment"
            Expect.isEmpty refused "an uncertified module is not refused by a certified sibling"
        }

        // ── the stamp-only manifest reader (GP 11) ───────────────────────
        test "a stamp-only manifest entry yields no certification" {
            let json =
                """{ "version": 1, "bindings": { "Sales": { "kind": "mac", "keyId": "k1", "tag": "AAAA" } } }"""

            match ModuleBindingManifest.parse json with
            | Error e -> failtestf "stamp-only manifest should parse: %s" e
            | Ok stamps -> Expect.isTrue (Map.containsKey "Sales" stamps) "the stamp is still read"

            match ModuleBindingManifest.parseCertifications json with
            | Error e -> failtestf "parseCertifications on a stamp-only manifest should be Ok empty: %s" e
            | Ok certifications -> Expect.isTrue (Map.isEmpty certifications) "no section ⇒ no certification"
        }

        test "a certifiedSurface with no signature fails closed" {
            let json =
                """{ "version": 1, "bindings": { "Sales": { "kind": "mac", "keyId": "k1", "tag": "AAAA", "certifiedSurface": { "surfaceJson": "{}", "surfaceHash": "x" } } } }"""

            match ModuleBindingManifest.parseCertifications json with
            | Ok _ -> failtest "a certifiedSurface with no certifiedSurfaceSig must be an Error, not silently dropped"
            | Error _ -> ()
        }
    ]