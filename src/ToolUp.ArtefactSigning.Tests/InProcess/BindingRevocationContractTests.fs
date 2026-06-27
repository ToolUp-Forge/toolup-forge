// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.BindingRevocationContractTests

open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.ArtefactSigning

// ─── Phase 215 — revocation + transparency-log contract tests ───────────
//
// Exercises the revocation check + transparency record the Phase 215 seams
// add over the Phase 165 `DefaultModuleBindingVerifier`:
//   * a cryptographically-valid stamp under a revoked anchor / revoked stamp
//     is denied;
//   * a non-revoked stamp admits unchanged;
//   * every admit/deny is recorded when a transparency log is configured;
//   * the no-op defaults change nothing (GP 13);
//   * the signed revocation-list loader verifies-then-parses and fails
//     closed on a bad signature.
//
// Stamps are produced through the public `JwsBuilder` surface a deploy-time
// stamper uses (mirrors the Phase 165 pack); no live crypto service.

let private canonical (moduleId: string) : byte[] = Encoding.UTF8.GetBytes moduleId

/// Produce an asymmetric (ES256 detached-JWS) stamp over `moduleId` plus the
/// matching anchor (and keep the EC key for signing revocation lists).
let private makeJwsStamp (moduleId: string) (keyId: string) : ModuleBindingStamp * ModuleBindingAnchor =
    use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let encodedHeader = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyId
    let input = JwsBuilder.signingInput encodedHeader (canonical moduleId)

    let rawSig =
        ec.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    let jws = JwsBuilder.assembleDetachedJws encodedHeader rawSig
    JwsStamp jws, AsymmetricAnchor(keyId, EcdsaP256, ec.ExportSubjectPublicKeyInfo())

/// Sign arbitrary bytes into a detached JWS under a fresh EC key; returns
/// (detachedJws, public SPKI DER) for the signed-revocation-list tests.
let private signDetached (keyId: string) (payload: byte[]) : string * byte[] =
    use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let encodedHeader = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyId
    let input = JwsBuilder.signingInput encodedHeader payload

    let rawSig =
        ec.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    JwsBuilder.assembleDetachedJws encodedHeader rawSig, ec.ExportSubjectPublicKeyInfo()

/// In-memory transparency log recording every decision for assertion.
type private RecordingTransparencyLog() =
    let decisions = ConcurrentBag<BindingDecision>()
    member _.Decisions: BindingDecision list = decisions |> List.ofSeq

    interface IBindingTransparencyLog with
        member _.Record(d) = async { decisions.Add d }

let private isRejected =
    function
    | Rejected _ -> true
    | Allowed -> false

let tests =
    testList "Phase 215 — module-binding revocation + transparency" [

        // ── revocation precedence: valid-but-revoked denies ──────────────
        test "valid stamp under a revoked anchor is denied (whole-key kill)" {
            let stamp, anchor = makeJwsStamp "Alpha" "asym-v1"

            let revocation =
                ModuleBindingRevocation.toRevocationList {
                    ModuleBindingRevocation.empty with
                        RevokedAnchors = Set.ofList [ "asym-v1" ]
                }

            let verifier =
                DefaultModuleBindingVerifier.createWith [ anchor ] revocation BindingTransparencyLog.none

            Expect.isTrue
                (isRejected (verifier.Verify("Alpha", Some stamp)))
                "a cryptographically-valid stamp under a revoked anchor must be denied"
        }

        test "valid stamp with a revoked stampId is denied (single-stamp revocation)" {
            let stamp, anchor = makeJwsStamp "Beta" "asym-v1"
            let sid = ModuleBindingRevocation.stampId stamp

            let revocation =
                ModuleBindingRevocation.toRevocationList {
                    ModuleBindingRevocation.empty with
                        RevokedStamps = Set.ofList [ "asym-v1", sid ]
                }

            let verifier =
                DefaultModuleBindingVerifier.createWith [ anchor ] revocation BindingTransparencyLog.none

            Expect.isTrue
                (isRejected (verifier.Verify("Beta", Some stamp)))
                "a stamp whose (anchor, stampId) pair is revoked must be denied"
        }

        // ── empty / absent list admits exactly as today ──────────────────
        test "valid stamp under an empty revocation list admits unchanged" {
            let stamp, anchor = makeJwsStamp "Gamma" "asym-v1"

            let revocation =
                ModuleBindingRevocation.toRevocationList ModuleBindingRevocation.empty

            let verifier =
                DefaultModuleBindingVerifier.createWith [ anchor ] revocation BindingTransparencyLog.none

            Expect.equal
                (verifier.Verify("Gamma", Some stamp))
                Allowed
                "an empty revocation list admits exactly as the crypto decided"
        }

        test "a different anchor's revocation does not deny this stamp" {
            let stamp, anchor = makeJwsStamp "Delta" "asym-v1"

            // Revoke an unrelated anchor id.
            let revocation =
                ModuleBindingRevocation.toRevocationList {
                    ModuleBindingRevocation.empty with
                        RevokedAnchors = Set.ofList [ "some-other-anchor" ]
                }

            let verifier =
                DefaultModuleBindingVerifier.createWith [ anchor ] revocation BindingTransparencyLog.none

            Expect.equal
                (verifier.Verify("Delta", Some stamp))
                Allowed
                "revoking an unrelated anchor must not deny this stamp"
        }

        // ── transparency log records both outcomes ───────────────────────
        test "transparency log records an admit" {
            let stamp, anchor = makeJwsStamp "Epsilon" "asym-v1"
            let log = RecordingTransparencyLog()

            let verifier =
                DefaultModuleBindingVerifier.createWith [ anchor ] BindingRevocationList.none log

            verifier.Verify("Epsilon", Some stamp) |> ignore

            match log.Decisions with
            | [ d ] ->
                Expect.isTrue d.Admitted "the admit is recorded as admitted"
                Expect.equal d.ModuleId "Epsilon" "the recorded module id matches"
                Expect.equal d.AnchorId (Some "asym-v1") "the verifying anchor is recorded"
                Expect.isSome d.StampId "the stamp id is recorded"
            | other -> failtestf "expected exactly one recorded decision, got %d" other.Length
        }

        test "transparency log records a deny (revoked stamp)" {
            let stamp, anchor = makeJwsStamp "Zeta" "asym-v1"
            let log = RecordingTransparencyLog()

            let revocation =
                ModuleBindingRevocation.toRevocationList {
                    ModuleBindingRevocation.empty with
                        RevokedAnchors = Set.ofList [ "asym-v1" ]
                }

            let verifier = DefaultModuleBindingVerifier.createWith [ anchor ] revocation log

            verifier.Verify("Zeta", Some stamp) |> ignore

            match log.Decisions with
            | [ d ] ->
                Expect.isFalse d.Admitted "the deny is recorded as not admitted"
                Expect.isSome d.Reason "the deny carries a neutral reason"
            | other -> failtestf "expected exactly one recorded decision, got %d" other.Length
        }

        // ── GP 13: no-op defaults change nothing ─────────────────────────
        test "no-op defaults behave identically to the bare verifier" {
            let stamp, anchor = makeJwsStamp "Eta" "asym-v1"
            let foreign, _ = makeJwsStamp "Eta" "asym-v2"

            let bare = DefaultModuleBindingVerifier.create [ anchor ]

            let withNoOps =
                DefaultModuleBindingVerifier.createWith
                    [ anchor ]
                    BindingRevocationList.none
                    BindingTransparencyLog.none

            Expect.equal (withNoOps.Verify("Eta", None)) (bare.Verify("Eta", None)) "absent-stamp outcome identical"

            Expect.equal
                (withNoOps.Verify("Eta", Some stamp))
                (bare.Verify("Eta", Some stamp))
                "valid-stamp outcome identical"

            Expect.equal
                (isRejected (withNoOps.Verify("Eta", Some foreign)))
                (isRejected (bare.Verify("Eta", Some foreign)))
                "foreign-stamp rejection identical"
        }

        // ── addModule wiring: a revoked module does not load ─────────────
        test "addModule drops a module whose valid stamp is revoked" {
            let stamp, anchor = makeJwsStamp "Theta" "asym-v1"

            let revocation =
                ModuleBindingRevocation.toRevocationList {
                    ModuleBindingRevocation.empty with
                        RevokedAnchors = Set.ofList [ "asym-v1" ]
                }

            let verifier =
                DefaultModuleBindingVerifier.createWith [ anchor ] revocation BindingTransparencyLog.none

            let app =
                ServerApp.empty
                |> ServerApp.withModuleBindingVerifier verifier
                |> ServerApp.addModule (ServerModule.create "Theta" |> ServerModule.withBindingStamp stamp)

            Expect.isFalse
                (app.ModuleNames |> List.contains "Theta")
                "a revoked-stamp module must not load even though its signature is valid"
        }

        // ── stampId determinism ──────────────────────────────────────────
        test "stampId is deterministic and stamp-specific" {
            let stampA, _ = makeJwsStamp "Iota" "asym-v1"
            let stampB, _ = makeJwsStamp "Iota" "asym-v1"

            Expect.equal
                (ModuleBindingRevocation.stampId stampA)
                (ModuleBindingRevocation.stampId stampA)
                "stampId is stable for the same stamp"

            Expect.notEqual
                (ModuleBindingRevocation.stampId stampA)
                (ModuleBindingRevocation.stampId stampB)
                "two distinct stamps (distinct signatures) get distinct ids"
        }

        // ── revocation-list JSON parser ──────────────────────────────────
        test "parse reads revokedAnchors + revokedStamps" {
            let json =
                """
                { "version": 1,
                  "revokedAnchors": [ "k1", "k2" ],
                  "revokedStamps": [ { "anchorId": "k3", "stampId": "abc" } ] }
                """

            match ModuleBindingRevocation.parse json with
            | Ok set ->
                Expect.isTrue (set.RevokedAnchors.Contains "k1") "k1 revoked"
                Expect.isTrue (set.RevokedAnchors.Contains "k2") "k2 revoked"
                Expect.isTrue (set.RevokedStamps.Contains("k3", "abc")) "(k3, abc) revoked"
            | Error e -> failtestf "expected a parsed set, got Error: %s" e
        }

        test "parse of an empty document revokes nothing" {
            match ModuleBindingRevocation.parse "{}" with
            | Ok set -> Expect.equal set ModuleBindingRevocation.empty "an empty document is the empty set"
            | Error e -> failtestf "expected the empty set, got Error: %s" e
        }

        test "parse rejects a newer major version (fail-closed, no silent under-revoke)" {
            match ModuleBindingRevocation.parse """{ "version": 999 }""" with
            | Error _ -> ()
            | Ok _ -> failtest "a version newer than this SDK understands must be an Error"
        }

        // ── signed revocation-list loader ────────────────────────────────
        test "verifyAndParse accepts a correctly-signed list" {
            let json = """{ "version": 1, "revokedAnchors": [ "compromised" ] }"""
            let jws, publicKey = signDetached "rev-signer" (Encoding.UTF8.GetBytes json)

            match SignedRevocationList.verifyAndParse publicKey EcdsaP256 json jws with
            | Ok list ->
                Expect.isTrue (list.IsRevoked("compromised", "anything")) "the revoked anchor is in effect"
                Expect.isFalse (list.IsRevoked("safe", "anything")) "a non-listed anchor is not revoked"
            | Error e -> failtestf "expected the signed list to verify, got Error: %s" e
        }

        test "verifyAndParse fails closed on a tampered list (signature over different bytes)" {
            let signedJson = """{ "version": 1, "revokedAnchors": [ "compromised" ] }"""
            let jws, publicKey = signDetached "rev-signer" (Encoding.UTF8.GetBytes signedJson)
            // Present a *different* JSON body against the same signature.
            let tampered = """{ "version": 1, "revokedAnchors": [] }"""

            match SignedRevocationList.verifyAndParse publicKey EcdsaP256 tampered jws with
            | Error _ -> ()
            | Ok _ -> failtest "a body that does not match the signature must fail closed"
        }

        test "verifyAndParse fails closed on an algorithm mismatch" {
            let json = """{ "version": 1 }"""
            let jws, publicKey = signDetached "rev-signer" (Encoding.UTF8.GetBytes json)

            // The detached JWS is ES256; assert an Ed25519 anchor.
            match SignedRevocationList.verifyAndParse publicKey Ed25519 json jws with
            | Error _ -> ()
            | Ok _ -> failtest "an algorithm mismatch between header and anchor must fail closed"
        }
    ]