// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Platform

// ─── Keyed byte signing over the application signing seam ───────────────
//
// `ToolUp.Platform.Server` declares `IKeyedByteSigner` / `IKeyedByteVerifier`
// — a recording substrate's minimum: a key id, a scheme name, and
// bytes-to-signature. This module fills that seam from `IApplicationSigner`,
// so a substrate that records a detached signature beside its own data is
// signing under the SAME composed key story as everything else the
// deployment signs: the same key material, the same recorded key lifecycle,
// the same attestation level, and the same rotation.
//
// **What this converges.** Before it, a substrate wanting a signed record
// implemented its own three-member signer against whatever key material it
// could reach. That is a working signature and an unstated trusted
// computing base: a relying party can check the bytes and cannot tell
// whether the key sat in a hardware boundary or in a file beside the
// process. Routing through the seam binds the attestation level into the
// signed bytes, so the claim travels with the signature and cannot be
// edited up.
//
// **What travels in the signature slot.** A recording surface has three
// slots — a key id, a scheme name, and an opaque signature blob — and an
// application signature is four facts (purpose, level, key id, detached
// JWS). The blob therefore carries a small, self-describing JSON document
// rather than raw signature bytes. That is a deliberate use of the slot,
// not an abuse of it: the slot's contract is "whatever the named scheme
// says", and a verifier that does not recognise the scheme refuses instead
// of parsing.
//
// **Every recorded field is a CHECKED claim.** The scheme names the level,
// the record names the key id, and the blob repeats both — and all three
// are compared before a signature is checked, with the level bound into
// the signed bytes by the seam's framing and the key id bound in by
// `KeyedByteSigning.bindKeyId`. Editing any of them fails verification.
// Recording a fact a signature does not cover would make it decoration.

/// Why a keyed-byte verification did not pass.
///
/// Four of the five cases are NOT "the signature is wrong", and keeping
/// them apart is the point of the type. A holder told their record does
/// not verify starts looking for tampering; a holder told the record names
/// one key while its signature was made under another, or that the
/// signature was minted for a different use, is looking at a filing error
/// or a replay — different fault, different remedy, and only one of them
/// implicates the bytes.
type KeyedVerificationFailure =
    /// The recorded signature blob could not be read at all — truncated,
    /// not JSON, or missing a field the scheme requires. Never a pass, and
    /// never reported as a bad signature.
    | KeyedSignatureMalformed of reason: string
    /// The recorded scheme is not one this build produces or checks. A
    /// newer producer's scheme lands here rather than being guessed at.
    | KeyedSchemeUnrecognised of scheme: string
    /// The scheme recorded beside the signature and the one the blob
    /// itself describes disagree — the recorded attestation level was
    /// edited after the fact.
    | KeyedSchemeMismatch of recorded: string * carried: string
    /// The recording surface names one key and the signature was made
    /// under another: a record re-pointed at a different key, or a
    /// signature transplanted from another record.
    | KeyedKeyIdMismatch of recorded: string * signed: string
    /// The application signing seam's own answer — a purpose replay, a
    /// revoked key, or a signature that genuinely does not verify.
    | KeyedPayloadRejected of PayloadVerificationError

module KeyedVerificationFailure =
    let describe =
        function
        | KeyedSignatureMalformed reason -> $"recorded signature is unreadable: {reason}"
        | KeyedSchemeUnrecognised scheme -> $"unrecognised signature scheme '{scheme}'; this build cannot check it"
        | KeyedSchemeMismatch(recorded, carried) ->
            $"recorded scheme '{recorded}' disagrees with the signature it labels ('{carried}')"
        | KeyedKeyIdMismatch(recorded, signed) ->
            $"the record names key '{recorded}' but its signature was made under '{signed}'"
        | KeyedPayloadRejected e -> PayloadVerificationError.describe e

/// Bridges `IApplicationSigner` onto the `IKeyedByteSigner` /
/// `IKeyedByteVerifier` seam.
module ApplicationKeyedSigning =

    /// The scheme family name. The recorded scheme is this plus the
    /// attestation level, so an operator reading a stored record can see
    /// what its signature claims without decoding anything — and the level
    /// is bound into the signed bytes, so what they read is checked rather
    /// than asserted.
    [<Literal>]
    let SchemeFamily = "toolup.appsig.v1"

    /// The scheme string recorded for a signature at `level`.
    let scheme (level: AttestationLevel) : string =
        SchemeFamily + "+" + AttestationLevel.name level

    /// Split a recorded scheme into its level, or `None` when the family
    /// is not this one.
    let tryParseScheme (s: string) : AttestationLevel option =
        let prefix = SchemeFamily + "+"

        if s.StartsWith(prefix, StringComparison.Ordinal) then
            Some(AttestationLevel.parse (s.Substring prefix.Length))
        else
            None

    // ── the recorded blob ───────────────────────────────────────────────

    [<Literal>]
    let private fieldScheme = "scheme"

    [<Literal>]
    let private fieldPurpose = "purpose"

    [<Literal>]
    let private fieldLevel = "level"

    [<Literal>]
    let private fieldKeyId = "keyId"

    [<Literal>]
    let private fieldAlgorithm = "alg"

    [<Literal>]
    let private fieldSignedAt = "signedAt"

    [<Literal>]
    let private fieldJws = "jws"

    /// Encode an envelope as the self-describing document a recording
    /// surface stores in its signature slot.
    ///
    /// Hand-built with `System.Text.Json` primitives rather than serialised
    /// from the record: this is a WIRE shape a holder in another language
    /// may have to read, and deriving it from an F# record's field names
    /// would make a rename a silent format change.
    let encodeEnvelope (envelope: SignedPayloadEnvelope) : byte[] =
        let node = JsonObject()
        node[fieldScheme] <- JsonValue.Create(scheme envelope.Level)
        node[fieldPurpose] <- JsonValue.Create envelope.Purpose
        node[fieldLevel] <- JsonValue.Create(AttestationLevel.name envelope.Level)
        node[fieldKeyId] <- JsonValue.Create envelope.Signature.KeyId
        node[fieldAlgorithm] <- JsonValue.Create(SigningAlgorithm.name envelope.Signature.Algorithm)
        node[fieldSignedAt] <- JsonValue.Create(envelope.Signature.SignedAt.ToString("o"))
        node[fieldJws] <- JsonValue.Create envelope.Signature.DetachedJws

        node.ToJsonString() |> Encoding.UTF8.GetBytes

    /// Decode a recorded blob back into an envelope. Every failure is a
    /// reason, never an exception and never a default-filled record — a
    /// blob that half-parsed would verify against nothing and read as
    /// tampering.
    let decodeEnvelope (blob: byte[]) : Result<SignedPayloadEnvelope, string> =
        try
            match JsonNode.Parse(Encoding.UTF8.GetString blob) with
            | null -> Error "empty document"
            | node ->
                let field (name: string) : Result<string, string> =
                    match node[name] with
                    | null -> Error $"missing field '{name}'"
                    | value -> Ok(value.GetValue<string>())

                match field fieldPurpose, field fieldLevel, field fieldKeyId with
                | Error e, _, _
                | _, Error e, _
                | _, _, Error e -> Error e
                | Ok purpose, Ok levelName, Ok keyId ->
                    match field fieldAlgorithm, field fieldSignedAt, field fieldJws with
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Error e
                    | Ok algorithmName, Ok signedAt, Ok jws ->
                        match SigningAlgorithm.tryParse algorithmName with
                        | None -> Error $"unknown signing algorithm '{algorithmName}'"
                        | Some algorithm ->
                            match DateTimeOffset.TryParse(signedAt, Globalization.CultureInfo.InvariantCulture) with
                            | false, _ -> Error $"unparseable signing timestamp '{signedAt}'"
                            | true, at ->
                                Ok {
                                    Purpose = purpose
                                    Level = AttestationLevel.parse levelName
                                    Signature = {
                                        KeyId = keyId
                                        Algorithm = algorithm
                                        SignedAt = at
                                        DetachedJws = jws
                                    }
                                }
        with ex ->
            Error ex.Message

    // ── signer ──────────────────────────────────────────────────────────

    /// An `IKeyedByteSigner` over the application signing seam, signing
    /// everything it is handed as `purpose`.
    ///
    /// One signer per purpose, deliberately. The purpose is what stops a
    /// signature minted for one record being replayed into another, so a
    /// single signer serving several purposes would give that protection
    /// away at the moment it is composed.
    ///
    /// Stateless between calls (GP 12 rule 4): the underlying seam re-reads
    /// key material and key history per call, so a rotation or a revocation
    /// takes effect on the next signature with no restart.
    let signer (purpose: string) (application: IApplicationSigner) : IKeyedByteSigner =
        { new IKeyedByteSigner with
            member _.KeyId() = application.ActiveKeyId()

            member _.Scheme() = scheme (application.Level())

            member _.Sign(message: byte[]) : Async<Result<byte[], string>> = async {
                let bound = KeyedByteSigning.bindKeyId (application.ActiveKeyId()) message

                match! application.SignPayload(purpose, bound) with
                | Error e -> return Error(SigningError.describe e)
                | Ok envelope -> return Ok(encodeEnvelope envelope)
            }
        }

    // ── verification ────────────────────────────────────────────────────

    /// Check a recorded signature, reporting the full typed failure.
    ///
    /// Order, and why it is this one. The three consistency checks come
    /// first because they compare two RECORDED values against each other
    /// and make no claim about the signature either way — a record whose
    /// scheme and blob disagree is malformed regardless of what any
    /// signature would have said. Only then is the signature checked, and
    /// from that point a refusal genuinely means the bytes did not verify.
    /// Comparing the recorded key id afterwards would be worse than
    /// useless: `bindKeyId` frames it into the signed message, so a
    /// re-pointed record would surface as tampering — a true statement that
    /// sends the reader to entirely the wrong place.
    let verifyRecorded
        (purpose: string)
        (application: IApplicationSigner)
        (keyId: string)
        (recordedScheme: string)
        (message: byte[])
        (blob: byte[])
        : Async<Result<unit, KeyedVerificationFailure>> =
        async {
            match tryParseScheme recordedScheme with
            | None -> return Error(KeyedSchemeUnrecognised recordedScheme)
            | Some _ ->
                match decodeEnvelope blob with
                | Error reason -> return Error(KeyedSignatureMalformed reason)
                | Ok envelope ->
                    let carried = scheme envelope.Level

                    if carried <> recordedScheme then
                        return Error(KeyedSchemeMismatch(recordedScheme, carried))
                    elif envelope.Signature.KeyId <> keyId then
                        return Error(KeyedKeyIdMismatch(keyId, envelope.Signature.KeyId))
                    else
                        let bound = KeyedByteSigning.bindKeyId keyId message

                        match! application.VerifyPayload(purpose, bound, envelope) with
                        | Ok() -> return Ok()
                        | Error e -> return Error(KeyedPayloadRejected e)
        }

    /// An `IKeyedByteVerifier` over the application signing seam.
    ///
    /// The seam's `Result<bool, string>` is a LOSSY projection of
    /// `KeyedVerificationFailure`, and the mapping is chosen so nothing is
    /// overstated: only a signature that genuinely failed to verify reports
    /// `Ok false` ("I checked, and it is wrong"). A revoked key, a purpose
    /// replay, a re-pointed key id and an unreadable blob are all `Error` —
    /// definite refusals whose reason is preserved verbatim, because the
    /// recording surface renders `Error` as "could not be accepted, here is
    /// why" and rendering any of them as a wrong signature would be a false
    /// account of what happened. A caller wanting the distinction calls
    /// `verifyRecorded` directly.
    let verifier (purpose: string) (application: IApplicationSigner) : IKeyedByteVerifier =
        { new IKeyedByteVerifier with
            member _.Verify
                (keyId: string, recordedScheme: string, message: byte[], signature: byte[])
                : Async<Result<bool, string>> =
                async {
                    match! verifyRecorded purpose application keyId recordedScheme message signature with
                    | Ok() -> return Ok true
                    | Error(KeyedPayloadRejected(PayloadVerificationError.SignatureRejected _) as failure) ->
                        // The one case that is genuinely "checked, and
                        // wrong". Its reason is dropped here because the
                        // seam has nowhere to put it; `verifyRecorded`
                        // keeps it.
                        ignore failure
                        return Ok false
                    | Error failure -> return Error(KeyedVerificationFailure.describe failure)
                }
        }