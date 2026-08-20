// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open System.Globalization
open System.Text.Json
open ToolUp.Platform

// ─── Sealing a deploy record with the application signer ─────────────
//
// `IDeployRecordSealer` is the deploy plane's seam: bytes in, an opaque
// seal out. This is the implementation over the application signing
// seam — the one an ordinary deployment composes, so that the record of
// what it deployed is signed by the same key, under the same recorded
// key history, as everything else the application signs.
//
// **Why the seam exists rather than a direct dependency.** The deploy
// plane sits in the platform tier; the signing surface sits above it in
// this companion. A pipeline that referenced a signer directly would
// invert that, and would bind every deployment to this SDK's signing
// story whether or not it wanted one. Over a byte-level seam the
// pipeline stays free of any crypto surface, and a deployment can seal
// with a scheme this SDK has never shipped.
//
// **What the seal binds.** The purpose below and the signer's own claim
// level are bound INTO the signed bytes by the application-signing
// framing, not merely recorded beside them. So a seal minted for a
// deploy record cannot be replayed as a signature over anything else,
// and the claim carried on the seal cannot be edited upward without the
// seal failing to verify.
//
// **What the seal does not establish.** That the record is TRUE. A seal
// makes the record attributable to a key and tamper-evident from the
// moment it was minted; whether the digests in it describe what was
// really deployed is a question about the producer, not about the
// signature. Verification says "this is the record that was sealed",
// never "this is what happened".

/// Scheme constants and the token codec for deploy-record seals minted
/// over the application signing seam.
[<RequireQualifiedAccess>]
module DeployRecordSealing =

    /// The scheme name seals from this implementation carry. A verifier
    /// refuses any other scheme rather than parsing a token it does not
    /// own.
    [<Literal>]
    let Scheme = "toolup.appsig.deployrecord.v1"

    /// The purpose deploy-record signatures are minted under. Bound
    /// into the signed bytes, so a signature made for a deploy record
    /// cannot be replayed as a signature over another payload — and a
    /// signature made elsewhere cannot be presented as a deploy-record
    /// seal.
    [<Literal>]
    let Purpose = "toolup.deploy-record"

    let private serialiserOptions = JsonSerializerOptions()

    // The token is carried as a flat string map rather than a typed
    // record, and deliberately: a checker holding nothing but this
    // companion has to be able to read one with the BCL serialiser and
    // no converter set registered. An F# record cannot promise that —
    // its shape depends on which constructor the serialiser happens to
    // find usable, which is exactly how the first cut of this codec
    // encoded fine and then refused to decode. A string map has one
    // representation everywhere.

    let private tokenField (name: string) (fields: Collections.Generic.Dictionary<string, string>) : string =
        match fields.TryGetValue name with
        | true, value when not (isNull value) -> value
        | _ -> ""

    /// Encode a signed envelope as this scheme's seal token.
    ///
    /// The token is opaque **to the deploy plane** — nothing in the
    /// substrate parses it. It is not a secret, though, and an
    /// independent checker implementing this scheme has to be able to
    /// produce and read one, so the codec is public. Depend on it only
    /// if you are implementing or checking THIS scheme; the deploy
    /// plane never will.
    let encodeToken (envelope: SignedPayloadEnvelope) : string =
        let fields = Collections.Generic.Dictionary<string, string>()

        fields["purpose"] <- envelope.Purpose
        fields["level"] <- AttestationLevel.name envelope.Level
        fields["keyId"] <- envelope.Signature.KeyId
        fields["algorithm"] <- SigningAlgorithm.name envelope.Signature.Algorithm
        fields["signedAt"] <- envelope.Signature.SignedAt.ToString("O", CultureInfo.InvariantCulture)
        fields["detachedJws"] <- envelope.Signature.DetachedJws

        JsonSerializer.Serialize(fields, serialiserOptions)

    /// Read this scheme's seal token back into a signed envelope.
    /// Every failure is a reason string — an unreadable token is
    /// refused, never absorbed into a "does not verify" that hides why.
    let decodeToken (token: string) : Result<SignedPayloadEnvelope, string> =
        let parsed =
            try
                match
                    JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, string>>(token, serialiserOptions)
                with
                | null -> Error "seal token is empty"
                | fields -> Ok fields
            with ex ->
                Error $"seal token is not readable: {ex.Message}"

        match parsed with
        | Error e -> Error e
        | Ok fields ->
            let detachedJws = tokenField "detachedJws" fields

            if detachedJws = "" then
                Error "seal token carries no signature"
            else
                let algorithmName = tokenField "algorithm" fields

                match SigningAlgorithm.tryParse algorithmName with
                | None -> Error $"seal token names an unsupported signing algorithm: {algorithmName}"
                | Some algorithm ->
                    let signedAt =
                        match
                            DateTimeOffset.TryParse(
                                tokenField "signedAt" fields,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind
                            )
                        with
                        | true, parsedAt -> parsedAt
                        | _ -> DateTimeOffset.MinValue

                    Ok {
                        Purpose = tokenField "purpose" fields
                        Level = AttestationLevel.parse (tokenField "level" fields)
                        Signature = {
                            KeyId = tokenField "keyId" fields
                            Algorithm = algorithm
                            SignedAt = signedAt
                            DetachedJws = detachedJws
                        }
                    }

/// `IDeployRecordSealer` over an `IApplicationSigner`.
///
/// Holds no state: every call goes back through the signer, which
/// re-reads key material and key trust per call, so a rotation or a
/// revocation takes effect on the next seal or verification with no
/// restart and no cache to invalidate (GP 12 rule 4).
type ApplicationSignerDeployRecordSealer(signer: IApplicationSigner) =

    interface IDeployRecordSealer with

        member _.Scheme() = DeployRecordSealing.Scheme

        member _.Seal(canonicalBytes: byte[]) : Async<Result<DeployRecordSeal, string>> = async {
            match! signer.SignPayload(DeployRecordSealing.Purpose, canonicalBytes) with
            | Error e -> return Error(SigningError.describe e)
            | Ok envelope ->
                return
                    Ok {
                        Scheme = DeployRecordSealing.Scheme
                        KeyId = envelope.Signature.KeyId
                        // The signer's own claim label, carried for
                        // display. A relying party reads it as a claim,
                        // never as evidence — the claim is worth exactly
                        // what the key custody behind it is worth.
                        Claim = AttestationLevel.name envelope.Level
                        Token = DeployRecordSealing.encodeToken envelope
                        SealedAt = envelope.Signature.SignedAt
                    }
        }

        member _.VerifySeal(canonicalBytes: byte[], seal: DeployRecordSeal) : Async<Result<unit, string>> = async {
            if seal.Scheme <> DeployRecordSealing.Scheme then
                return Error $"seal scheme '{seal.Scheme}' is not '{DeployRecordSealing.Scheme}'"
            else
                match DeployRecordSealing.decodeToken seal.Token with
                | Error reason -> return Error reason
                | Ok envelope ->
                    match! signer.VerifyPayload(DeployRecordSealing.Purpose, canonicalBytes, envelope) with
                    | Ok() -> return Ok()
                    | Error e -> return Error(PayloadVerificationError.describe e)
        }

/// Construction helpers for the application-signer-backed sealer.
[<RequireQualifiedAccess>]
module DeployRecordSealer =

    /// Build a deploy-record sealer over a composed application signer.
    /// Pure — no I/O, no recording; the first key read happens on the
    /// first seal.
    let overApplicationSigner (signer: IApplicationSigner) : IDeployRecordSealer =
        ApplicationSignerDeployRecordSealer signer :> IDeployRecordSealer

    /// Build a deploy-record sealer straight from a signing provider,
    /// for a deployment composing one solely to seal its deploy records.
    let ofProvider (provider: SigningProvider) : IDeployRecordSealer =
        overApplicationSigner (ApplicationSigning.create provider)