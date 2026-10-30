module ToolUp.Platform.Tests.InProcess.CanonicalFrameTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Platform

// ─── Phase 1089 — `Sha256.hex` and `CanonicalFrame` ──────────────────
//
// The laws of the one digest helper and the one frame, and the pins that
// show the sites routed through them kept their bytes. Every expected
// digest below was computed OUTSIDE this code base (a few lines of Python's
// `hashlib` over the hand-written frame text), so a pin agreeing with the
// implementation is two derivations agreeing rather than one agreeing
// with itself.

/// An independent SHA-256 — the BCL, called directly, never through the
/// helper under test.
let private bclHex (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private envelope: GroundingEnvelope = {
    SchemaVersion = 1
    // Deliberately out of canonical order: the canonical form sorts.
    Declarations = [
        {
            Facet = PurposeDeclarationFacet
            Subject = "audit"
            Value = "v2"
        }
        {
            Facet = MetricRegistrationFacet
            Subject = "revenue"
            Value = "revenue"
        }
    ]
}

let tests =
    testList "Phase 1089 — Sha256.hex and CanonicalFrame" [

        testCase "Sha256.hex is lowercase-hex SHA-256 (FIPS 180-2 'abc' vector)"
        <| fun () ->
            let abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
            Expect.equal (Sha256.hex (Encoding.ASCII.GetBytes "abc")) abc "the bytes form"
            Expect.equal (Sha256.hexOfUtf8 "abc") abc "the UTF-8 text form"

        testCase "the frame is length-prefixed, field by field"
        <| fun () ->
            Expect.equal (CanonicalFrame.text [ "ab"; "c" ]) "2:ab1:c" "two fields"
            Expect.equal (CanonicalFrame.text []) "" "no fields"
            Expect.equal (CanonicalFrame.text [ ""; "" ]) "0:0:" "empty fields still frame"

        testCase "the frame is injective on field boundaries"
        <| fun () ->
            Expect.notEqual (CanonicalFrame.text [ "ab"; "c" ]) (CanonicalFrame.text [ "a"; "bc" ]) "re-cut fields"
            Expect.notEqual (CanonicalFrame.digest [ "ab"; "c" ]) (CanonicalFrame.digest [ "a"; "bc" ]) "re-cut digests"
            // A delimiter join — the shape every recipe used before 1089 —
            // collides here; the frame does not.
            Expect.equal (String.concat "|" [ "a|b"; "c" ]) (String.concat "|" [ "a"; "b|c" ]) "the join collides"

            Expect.notEqual
                (CanonicalFrame.digest [ "a|b"; "c" ])
                (CanonicalFrame.digest [ "a"; "b|c" ])
                "the frame does not"

            Expect.notEqual (CanonicalFrame.digest [ "" ]) (CanonicalFrame.digest []) "one empty field is not none"

        testCase "the digest is SHA-256 over the UTF-8 frame — pinned against an independent derivation"
        <| fun () ->
            Expect.equal
                (CanonicalFrame.digest [ "ab"; "c" ])
                "430fb1b4ac43316eca81fab27a1930ab8eff8fef6a1dc7903dce44bbc2790dc5"
                "hashlib over '2:ab1:c'"

            Expect.equal
                (CanonicalFrame.digest [])
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                "the empty frame is the empty string's digest"

            Expect.equal (CanonicalFrame.frame [ "ab"; "c" ]) (Encoding.UTF8.GetBytes "2:ab1:c") "frame bytes"

        testCase "lengths count UTF-16 code units, as ProvenanceFraming does"
        <| fun () ->
            Expect.equal (CanonicalFrame.text [ "é" ]) "1:é" "one BMP character"
            Expect.equal (CanonicalFrame.text [ "\U0001F600" ]) "2:\U0001F600" "a surrogate pair counts two"

        testCase "a null field frames as the empty string"
        <| fun () -> Expect.equal (CanonicalFrame.text [ null; "x" ]) (CanonicalFrame.text [ ""; "x" ]) "null is empty"

        testCase "the grounding envelope seal is byte-identical — the frame it already used"
        <| fun () ->
            let expectedText =
                "27:toolup.groundingenvelope.v11:11:219:metric-registration7:revenue7:revenue19:purpose-declaration5:audit2:v2"

            Expect.equal (GroundingEnvelope.canonicalForm envelope) expectedText "canonical form, byte for byte"

            Expect.equal
                (GroundingEnvelope.digest envelope)
                "1fed23e8e2db3c3686fa76bd67d2d7af7857337da51deb321370b22634b8a145"
                "the seal digest, pinned against hashlib"

            Expect.equal (GroundingEnvelope.canonicalBytes envelope) (Encoding.UTF8.GetBytes expectedText) "bytes"

        testCase "the deployment evidence chain's verdict digest is byte-identical"
        <| fun () ->
            let hops: EvidenceHop list = []
            let expected = bclHex (Encoding.UTF8.GetBytes(EvidenceChain.canonicalForm hops))
            Expect.equal (DefaultEvidenceChainWalker.VerdictDigest hops) expected "SHA-256 over the canonical form"

        testCase "the countersignature record id is byte-identical"
        <| fun () ->
            let bytes =
                CountersignatureCanonical.subjectBytes "phase-1089-kind" "phase-1089-subject" [||]

            Expect.equal
                (Sha256.hex bytes)
                (bclHex bytes)
                "the helper the record id and template digests now share is the BCL digest"

        testCase "the compute memo key digest is byte-identical"
        <| fun () ->
            let key: ComputeMemoKey = {
                ScopeId = "team-1089"
                Kind = "fit"
                PayloadHash = ComputeMemoKey.hashPayload "{\"spec\":1}"
                IdempotencyKey = "k-1"
                Profile = "default"
            }

            Expect.equal
                (ComputeMemoKey.hashPayload "{\"spec\":1}")
                (bclHex (Encoding.UTF8.GetBytes "{\"spec\":1}"))
                "payload hash"

            Expect.equal
                (ComputeMemoKey.digest key)
                (bclHex (Encoding.UTF8.GetBytes(ComputeMemoKey.composite key)))
                "memo digest over the composite"
    ]