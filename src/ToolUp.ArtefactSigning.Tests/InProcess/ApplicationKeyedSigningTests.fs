// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.ApplicationKeyedSigningTests

open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning
open ToolUp.ArtefactSigning.Tests.Support.InMemoryStores

// ─── Keyed byte signing over the application signing seam ───────────────
//
// The bridge that lets a recording substrate keep its own local signing
// interface while the signature underneath comes from the deployment's one
// composed key story. What is worth certifying here is not that a
// signature round-trips — the seam beneath already guarantees that — but
// the four things the bridge is responsible for and could get silently
// wrong: that every recorded field is covered by the signature, that a
// rotation does not invalidate what came before it, that a signature
// cannot be moved between uses, and that each of those refusals is
// reported as what it is rather than as tampering.

let private utf8 (s: string) = Encoding.UTF8.GetBytes s

let private purpose = "audit.ledger.head"

/// A fresh deployment: one secret store, one application signer over it.
let private deployment (keyId: string) =
    let secrets = InMemorySecretStore() :> ISecretStore
    let audit = InMemoryAuditLog()

    let signer =
        ApplicationSigning.inProcess secrets audit keyId EcdsaP256 "system"
        |> ApplicationSigning.create

    secrets, signer

/// A second signer over the SAME store under a new key id — what a
/// rotation looks like from the outside.
let private rotatedTo (secrets: ISecretStore) (keyId: string) =
    ApplicationSigning.inProcess secrets (InMemoryAuditLog()) keyId Ed25519 "system"
    |> ApplicationSigning.create

let private signed (signer: IApplicationSigner) (message: byte[]) = async {
    let keyed = ApplicationKeyedSigning.signer purpose signer

    match! keyed.Sign message with
    | Error e -> return failtestf "signing must succeed; got %s" e
    | Ok blob -> return keyed.KeyId(), keyed.Scheme(), blob
}

[<Tests>]
let tests =
    testList "Keyed byte signing — application seam bridge" [

        testCaseAsync "a recorded signature round-trips through the seam"
        <| async {
            let _, signer = deployment "ledger-key-v1"
            let message = utf8 "42|9f2c…"
            let! keyId, scheme, blob = signed signer message

            match! ApplicationKeyedSigning.verifyRecorded purpose signer keyId scheme message blob with
            | Ok() -> ()
            | Error f -> failtestf "round-trip must verify; got %s" (KeyedVerificationFailure.describe f)
        }

        // The claim the whole convergence exists to carry: the level is not
        // recorded beside the signature, it is inside it.
        testCaseAsync "the recorded scheme names the signer's attestation level"
        <| async {
            let _, signer = deployment "ledger-key-v1"
            let keyed = ApplicationKeyedSigning.signer purpose signer

            Expect.equal
                (keyed.Scheme())
                (ApplicationKeyedSigning.scheme Attribution)
                "an in-process provider claims attribution, and says so in the recorded scheme"

            let! _, scheme, blob = signed signer (utf8 "head")

            match ApplicationKeyedSigning.decodeEnvelope blob with
            | Error e -> failtestf "the recorded blob must decode; got %s" e
            | Ok envelope ->
                Expect.equal envelope.Level Attribution "the blob carries the level it was signed at"
                Expect.equal envelope.Purpose purpose "the blob carries the purpose it was signed as"

                Expect.equal
                    (ApplicationKeyedSigning.scheme envelope.Level)
                    scheme
                    "the recorded scheme and the blob's level must agree"
        }

        // ── rotation continuity ─────────────────────────────────────────
        //
        // The property that makes key rotation cheap: a signature outlives
        // the key that made it, because verification resolves the key the
        // signature NAMES rather than assuming the active one.
        testCaseAsync "a signature made before a rotation still verifies after it"
        <| async {
            let secrets, oldSigner = deployment "ledger-key-v1"
            let message = utf8 "the head as it stood before the rotation"
            let! oldKeyId, oldScheme, oldBlob = signed oldSigner message

            // Rotate: a new key id, a different algorithm, same store.
            let newSigner = rotatedTo secrets "ledger-key-v2"

            do!
                ApplicationSigning.retire (SecretStoreSigningKeyLedger.create secrets) "operator" "ledger-key-v1"
                |> Async.Ignore

            let! newKeyId, _, _ = signed newSigner (utf8 "the head after the rotation")

            Expect.equal oldKeyId "ledger-key-v1" "the old signature names the old key"
            Expect.equal newKeyId "ledger-key-v2" "new signatures are minted under the new key"

            // The old artefact verifies through the ROTATED signer,
            // because the key is resolved by the id in the signature.
            match! ApplicationKeyedSigning.verifyRecorded purpose newSigner oldKeyId oldScheme message oldBlob with
            | Ok() -> ()
            | Error f ->
                failtestf
                    "an artefact signed under a retired key must still verify; got %s"
                    (KeyedVerificationFailure.describe f)
        }

        testCaseAsync "a revoked key is refused, and not as a bad signature"
        <| async {
            let secrets, signer = deployment "ledger-key-v1"
            let message = utf8 "signed while the key was trusted"
            let! keyId, scheme, blob = signed signer message

            do!
                ApplicationSigning.revoke
                    (SecretStoreSigningKeyLedger.create secrets)
                    "operator"
                    "ledger-key-v1"
                    "suspected disclosure"
                |> Async.Ignore

            match! ApplicationKeyedSigning.verifyRecorded purpose signer keyId scheme message blob with
            | Ok() -> failtest "a signature under a revoked key must not verify"
            | Error(KeyedPayloadRejected(PayloadVerificationError.KeyRevoked(k, _, reason))) ->
                Expect.equal k "ledger-key-v1" "the refusal names the revoked key"
                Expect.equal reason "suspected disclosure" "and the reason a relying party is shown"
            | Error other ->
                failtestf "revocation must be reported as such; got %s" (KeyedVerificationFailure.describe other)

            // And through the seam projection it is an Error, never
            // `Ok false` — the key is untrusted, the bytes are fine.
            let keyed = ApplicationKeyedSigning.verifier purpose signer

            match! keyed.Verify(keyId, scheme, message, blob) with
            | Error reason -> Expect.stringContains reason "revoked" "the reason survives the projection"
            | Ok verdict -> failtestf "a revoked key must not project to Ok %b" verdict
        }

        // ── transplants, each refused as what it is ─────────────────────

        testCaseAsync "a signature transplanted between messages fails as a forgery"
        <| async {
            let _, signer = deployment "ledger-key-v1"
            let! keyId, scheme, blob = signed signer (utf8 "the head of ledger A")

            // Same key, same purpose, different bytes. This IS
            // cryptographically indistinguishable from tampering, and is
            // reported as such — the honest answer, and the one case that
            // projects to `Ok false`.
            match!
                ApplicationKeyedSigning.verifyRecorded purpose signer keyId scheme (utf8 "the head of ledger B") blob
            with
            | Error(KeyedPayloadRejected(PayloadVerificationError.SignatureRejected _)) -> ()
            | Ok() -> failtest "a signature over other bytes must not verify"
            | Error other -> failtestf "expected a rejected signature; got %s" (KeyedVerificationFailure.describe other)

            let keyed = ApplicationKeyedSigning.verifier purpose signer

            match! keyed.Verify(keyId, scheme, utf8 "the head of ledger B", blob) with
            | Ok false -> ()
            | Ok true -> failtest "a transplanted signature must not project to Ok true"
            | Error reason -> failtestf "a genuine forgery is Ok false, not Error: %s" reason
        }

        testCaseAsync "a signature transplanted between purposes is a replay, not a forgery"
        <| async {
            let _, signer = deployment "ledger-key-v1"
            let message = utf8 "identical bytes, two different uses"
            let! keyId, scheme, blob = signed signer message

            // The bytes verify perfectly. What is wrong is the USE — this
            // signature was minted to seal a ledger head and is being
            // offered as a certificate seal.
            match!
                ApplicationKeyedSigning.verifyRecorded "grounding-certificate/v1" signer keyId scheme message blob
            with
            | Error(KeyedPayloadRejected(PayloadVerificationError.PurposeMismatch(expected, actual))) ->
                Expect.equal expected "grounding-certificate/v1" "the refusal names the use it was offered for"
                Expect.equal actual purpose "and the use it was minted for"
            | Ok() -> failtest "a signature must not be replayable across purposes"
            | Error other -> failtestf "expected a purpose mismatch; got %s" (KeyedVerificationFailure.describe other)
        }

        testCaseAsync "a record re-pointed at another key is a mismatch, not a forgery"
        <| async {
            let secrets, signer = deployment "ledger-key-v1"
            let message = utf8 "head bytes"
            let! _, scheme, blob = signed signer message
            rotatedTo secrets "ledger-key-v2" |> ignore

            // The record's key-id field says v2; the signature was made
            // under v1. Reported as the filing error it is, BEFORE the
            // signature is checked — checking first would surface it as
            // tampering, which is true of the framed bytes and useless to
            // the reader.
            match! ApplicationKeyedSigning.verifyRecorded purpose signer "ledger-key-v2" scheme message blob with
            | Error(KeyedKeyIdMismatch(recorded, signedUnder)) ->
                Expect.equal recorded "ledger-key-v2" "the refusal names the key the record points at"
                Expect.equal signedUnder "ledger-key-v1" "and the key that actually signed"
            | Ok() -> failtest "a re-pointed record must not verify"
            | Error other -> failtestf "expected a key-id mismatch; got %s" (KeyedVerificationFailure.describe other)
        }

        testCaseAsync "an edited attestation level is refused"
        <| async {
            let _, signer = deployment "ledger-key-v1"
            let message = utf8 "head bytes"
            let! keyId, _, blob = signed signer message

            // Claiming more than the signer could: the recorded scheme is
            // upgraded to the isolated-signer level.
            let upgraded = ApplicationKeyedSigning.scheme IsolatedSigner

            match! ApplicationKeyedSigning.verifyRecorded purpose signer keyId upgraded message blob with
            | Error(KeyedSchemeMismatch(recorded, carried)) ->
                Expect.equal recorded upgraded "the refusal names the edited claim"

                Expect.equal
                    carried
                    (ApplicationKeyedSigning.scheme Attribution)
                    "and the claim the signature actually carries"
            | Ok() -> failtest "an upgraded attestation level must not verify"
            | Error other -> failtestf "expected a scheme mismatch; got %s" (KeyedVerificationFailure.describe other)
        }

        testCase "a scheme this build does not produce is refused rather than guessed"
        <| fun _ ->
            Expect.isNone
                (ApplicationKeyedSigning.tryParseScheme "some.future.scheme/v9")
                "an unrelated scheme family must not parse"

            match ApplicationKeyedSigning.tryParseScheme (ApplicationKeyedSigning.scheme IsolatedSigner) with
            | Some level -> Expect.equal level IsolatedSigner "this build's own scheme round-trips its level"
            | None -> failtest "this build must recognise its own scheme"

        testCaseAsync "an unreadable blob is reported as unreadable, never as a bad signature"
        <| async {
            let _, signer = deployment "ledger-key-v1"
            let scheme = ApplicationKeyedSigning.scheme Attribution

            for blob in [ utf8 ""; utf8 "not json at all"; utf8 """{"purpose":"p"}""" ] do
                match! ApplicationKeyedSigning.verifyRecorded purpose signer "k" scheme (utf8 "m") blob with
                | Error(KeyedSignatureMalformed _) -> ()
                | Ok() -> failtest "an unreadable blob must never verify"
                | Error other ->
                    failtestf "expected a malformed-blob refusal; got %s" (KeyedVerificationFailure.describe other)
        }

        // The key id is a RECORDED field, so it has to be covered by the
        // signature or it is a pointer anyone can move.
        testCase "the key-id binding puts the recorded id inside the signed message"
        <| fun _ ->
            let a = KeyedByteSigning.bindKeyId "k1" (utf8 "m")
            let b = KeyedByteSigning.bindKeyId "k2" (utf8 "m")
            Expect.notEqual a b "two key ids must never frame the same message to identical bytes"

            // Without length prefixes these collide: the id absorbs the
            // separator the message would have split on.
            let c = KeyedByteSigning.bindKeyId "k|m" (utf8 "x")
            let d = KeyedByteSigning.bindKeyId "k" (utf8 "m|x")
            Expect.notEqual c d "distinct (key id, message) pairs must never frame to identical bytes"

            Expect.stringStarts
                (Encoding.UTF8.GetString a)
                KeyedByteSigning.BindingVersion
                "the binding must be version-tagged"
    ]