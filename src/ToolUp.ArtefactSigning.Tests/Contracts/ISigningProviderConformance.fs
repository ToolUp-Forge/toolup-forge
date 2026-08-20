// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.Contracts.ISigningProviderConformance

open Expecto
open ToolUp.ArtefactSigning

/// The conformance bar every application signing provider is held to.
///
/// `IArtefactSignerContract` already certifies the BYTE level — that a
/// signer and its verifier round-trip, and that tampered bytes are
/// refused. This pack certifies the three properties the application seam
/// adds on top, which are exactly the properties a provider can appear to
/// have while not having them:
///
///   * the purpose is BOUND, not merely recorded beside the signature;
///   * the attestation level is BOUND, and is the one the provider
///     actually declares;
///   * key lifecycle is honoured — a rotated-out key keeps verifying, a
///     revoked key stops, including for signatures made before it.
///
/// A provider that recorded all three as plain fields and consulted none
/// of them would pass a naive test suite and provide no security at all.
/// Every case here is therefore written to fail such a provider, and the
/// probe alongside the pack (`SigningProviderConformanceTests`) proves it
/// does by running deliberately-broken providers through it and asserting
/// the specific case each one trips.
///
/// **Case names are part of the contract.** The probe asserts them by
/// name, so renaming one silently weakens the probe into a tautology
/// ("something failed"). Rename a case only together with the probe.
type SigningProviderFixture = {
    /// Provider name, for the test-list label.
    Name: string
    /// The level this provider declares. Asserted to be what its
    /// envelopes carry.
    DeclaredLevel: AttestationLevel
    /// A signer over the provider's current active key.
    Signer: IApplicationSigner
    /// Rotate to a fresh active key: record the outgoing key's
    /// retirement, mint a new key, record its activation, and return a
    /// signer over it. The outgoing key's MATERIAL must stay resolvable
    /// — that is what rotation continuity rests on.
    Rotate: unit -> IApplicationSigner
    /// The ledger the provider's key trust is recorded in, so the pack
    /// can revoke a key and observe the consequence.
    Ledger: ISigningKeyLedger
}

let private payload () : byte[] = [| for i in 0..127 -> byte (i * 11 % 256) |]

[<Literal>]
let private Purpose = "conformance.payload"

/// The level a bound-level check mutates TO — any level other than the
/// one the envelope was signed at.
let private otherLevel =
    function
    | Attribution -> IsolatedSigner
    | _ -> Attribution

let private signOrFail (signer: IApplicationSigner) purpose bytes = async {
    match! signer.SignPayload(purpose, bytes) with
    | Ok envelope -> return envelope
    | Error e -> return failtestf "SignPayload must succeed; got %s" (SigningError.describe e)
}

let tests (factory: unit -> SigningProviderFixture) =
    let label = (factory ()).Name

    testList $"{label} — signing-provider conformance" [

        testCaseAsync "sign and verify a payload round-trips"
        <| async {
            let fx = factory ()
            let bytes = payload ()
            let! envelope = signOrFail fx.Signer Purpose bytes

            match! fx.Signer.VerifyPayload(Purpose, bytes, envelope) with
            | Ok() -> ()
            | Error e -> failtestf "Round-trip must verify; got %s" (PayloadVerificationError.describe e)
        }

        testCaseAsync "the envelope carries the provider's declared attestation level"
        <| async {
            let fx = factory ()
            let! envelope = signOrFail fx.Signer Purpose (payload ())

            Expect.equal (fx.Signer.Level()) fx.DeclaredLevel "signer must report the provider's declared level"

            Expect.equal
                envelope.Level
                fx.DeclaredLevel
                "envelope must carry the declared level — a signature that overstates its own custody is worse than an unsigned payload"
        }

        // The level is only worth reading if editing it breaks the
        // signature. A provider that stores the level beside the
        // signature rather than inside it fails here and nowhere else.
        testCaseAsync "the attestation level is bound into the signature"
        <| async {
            let fx = factory ()
            let bytes = payload ()
            let! envelope = signOrFail fx.Signer Purpose bytes

            let upgraded = {
                envelope with
                    Level = otherLevel envelope.Level
            }

            match! fx.Signer.VerifyPayload(Purpose, bytes, upgraded) with
            | Ok() ->
                failtest
                    "An envelope whose attestation level was edited after signing must NOT verify — otherwise any holder can upgrade a signature's claim"
            | Error _ -> ()
        }

        // Two halves, and both are needed. The first catches a provider
        // that never compares purposes; the second catches one that
        // compares them but does not sign over them, so a caller can
        // simply edit the envelope's purpose to match.
        testCaseAsync "the purpose is bound into the signature"
        <| async {
            let fx = factory ()
            let bytes = payload ()
            let! envelope = signOrFail fx.Signer Purpose bytes

            match! fx.Signer.VerifyPayload("conformance.other-use", bytes, envelope) with
            | Ok() -> failtest "A signature minted for one purpose must not verify under another"
            | Error _ -> ()

            let relabelled = {
                envelope with
                    Purpose = "conformance.other-use"
            }

            match! fx.Signer.VerifyPayload("conformance.other-use", bytes, relabelled) with
            | Ok() ->
                failtest
                    "Relabelling the envelope's purpose must break the signature — a purpose that is only compared, not signed over, can be edited to match"
            | Error _ -> ()
        }

        testCaseAsync "a tampered payload is refused"
        <| async {
            let fx = factory ()
            let bytes = payload ()
            let! envelope = signOrFail fx.Signer Purpose bytes
            let mutated = Array.copy bytes
            mutated[3] <- mutated[3] ^^^ 0xFFuy

            match! fx.Signer.VerifyPayload(Purpose, mutated, envelope) with
            | Ok() -> failtest "A mutated payload must not verify"
            | Error _ -> ()
        }

        testCaseAsync "rotation keeps earlier signatures verifiable"
        <| async {
            let fx = factory ()
            let bytes = payload ()
            let! before = signOrFail fx.Signer Purpose bytes
            let rotated = fx.Rotate()

            match! rotated.VerifyPayload(Purpose, bytes, before) with
            | Ok() -> ()
            | Error e ->
                failtestf
                    "A signature made under the previous key must still verify after rotation — rotation is not distrust. Got: %s"
                    (PayloadVerificationError.describe e)
        }

        testCaseAsync "rotation advances the active key"
        <| async {
            let fx = factory ()
            let before = fx.Signer.ActiveKeyId()
            let rotated = fx.Rotate()

            Expect.notEqual (rotated.ActiveKeyId()) before "rotation must mint a new active key id"

            let bytes = payload ()
            let! after = signOrFail rotated Purpose bytes

            Expect.equal
                after.Signature.KeyId
                (rotated.ActiveKeyId())
                "new signatures must be minted under the new active key"
        }

        // The case that separates "we deleted the key file" from "we
        // decided not to trust this key": a revocation reaches BACKWARDS.
        testCaseAsync "a revoked key refuses signatures made before the revocation"
        <| async {
            let fx = factory ()
            let bytes = payload ()
            let! envelope = signOrFail fx.Signer Purpose bytes
            let keyId = envelope.Signature.KeyId

            match! ApplicationSigning.revoke fx.Ledger "operator-1" keyId "key material disclosed" with
            | Ok() -> ()
            | Error e -> failtestf "revoke must record; got %s" e

            match! fx.Signer.VerifyPayload(Purpose, bytes, envelope) with
            | Ok() ->
                failtest
                    "A signature under a revoked key must be refused even though it was made before the revocation — a compromised key was compromised for longer than anyone knew"
            | Error(PayloadVerificationError.KeyRevoked(k, _, reason)) ->
                Expect.equal k keyId "refusal must name the revoked key"

                Expect.equal
                    reason
                    "key material disclosed"
                    "refusal must carry the recorded reason — an unexplained refusal is indistinguishable from a bug"
            | Error other -> failtestf "Expected a revocation refusal; got %s" (PayloadVerificationError.describe other)
        }

        testCaseAsync "key lifecycle is recorded as attributable events"
        <| async {
            let fx = factory ()
            let firstKey = fx.Signer.ActiveKeyId()
            let rotated = fx.Rotate()
            let secondKey = rotated.ActiveKeyId()

            match! ApplicationSigning.revoke fx.Ledger "operator-2" secondKey "rotation drill" with
            | Ok() -> ()
            | Error e -> failtestf "revoke must record; got %s" e

            let! history = rotated.KeyHistory()

            let entryFor keyId =
                match history |> SigningKeyHistory.tryFind keyId with
                | Some e -> e
                | None -> failtestf "history must carry an entry for key '%s'" keyId

            let first = entryFor firstKey
            let second = entryFor secondKey

            Expect.equal first.State RetiredKey "the rotated-out key must read as retired, not revoked"

            match second.State with
            | RevokedKey(_, reason) -> Expect.equal reason "rotation drill" "revocation must retain its reason"
            | other -> failtestf "expected the revoked key to read as revoked; got %A" other

            let actors = second.Events |> List.map _.Actor |> List.distinct

            Expect.contains actors "operator-2" "the revocation must be attributed to whoever recorded it"

            Expect.isTrue
                (first.Events |> List.exists (fun e -> e.Kind = SigningKeyEventKind.Activated))
                "the first key's activation must be recorded"
        }
    ]