// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

// ─── Signed-statement envelope — the shared, crypto-free half ───────────
//
// Two open, vendor-neutral specifications describe exactly the artefact
// several substrates here already produce by hand: a signed claim ABOUT
// something, verifiable by a holder who has only the claim and a public
// key.
//
//   * **in-toto Statement v1** — `{ _type, subject, predicateType,
//     predicate }`. `subject` names the thing the claim is about, by name
//     plus a digest set; `predicateType` is a versioned URI naming the
//     claim's shape; `predicate` is that claim, opaque to the envelope.
//   * **DSSE** (Dead Simple Signing Envelope) — `{ payload, payloadType,
//     signatures }`, where the signed message is not the payload but its
//     **Pre-Authentication Encoding** (PAE), which binds the payload type
//     into the signature so a payload cannot be re-interpreted under a
//     different type.
//
// This module is the **format** half: types, PAE, statement assembly,
// envelope JSON, and the structural checks (payload type, predicate type,
// subject digest). It carries no signing or verification crypto beyond
// SHA-256, so a substrate can emit and structurally check an envelope
// without taking a key-management dependency (GP 1) — the signing and
// signature-verification primitives live behind the `IStatementEnvelopeSigner`
// seam below and in the signing companion that fills it.
//
// **One envelope implementation, several artefact kinds.** The helper is
// deliberately artefact-agnostic: a caller supplies its own subject,
// predicate type and predicate JSON. Nothing here knows what a grounding
// certificate or an audit-ledger segment is.

/// One in-toto `subject` entry — what the statement is ABOUT. `Digest` is
/// the digest set: an ordered list of (algorithm, value) pairs. Ordered
/// rather than a `Map` so the emitted JSON is deterministic.
///
/// The digest algorithm key is descriptive, per the in-toto DigestSet
/// convention: `"sha256"` when the value genuinely is a SHA-256 hex
/// digest, an explicit alternative name when the identifier is formed
/// some other way. Naming a non-hash identifier `"sha256"` would be a
/// false claim, so callers pick the key from how the id is actually
/// derived.
type InTotoSubject = {
    Name: string
    Digest: (string * string) list
}

/// One DSSE `signatures[]` entry. `Sig` is standard base64 (RFC 4648 §4,
/// padded — the DSSE wire encoding, NOT base64url) of the raw signature
/// over the envelope's PAE.
type DsseSignature = { KeyId: string; Sig: string }

/// A DSSE envelope. `Payload` is standard base64 of the payload bytes;
/// for an in-toto statement those bytes are the statement JSON and
/// `PayloadType` is `DsseEnvelope.InTotoPayloadType`.
type DsseEnvelope = {
    PayloadType: string
    Payload: string
    Signatures: DsseSignature list
}

/// A raw signature over an envelope's PAE, plus the key id that produced
/// it. Identity by value (GP 12 rule 1) — no key handle crosses the seam.
type EnvelopeKeySignature = { KeyId: string; Signature: byte[] }

/// The narrow signing seam a statement envelope needs: sign exactly these
/// bytes, raw. Deliberately NOT `IArtefactSigner` — that seam emits a
/// detached JWS, whose signature covers the JWS signing input
/// (`b64url(header) + "." + b64url(artefact)`) rather than the bytes
/// handed to it. DSSE requires the signature to cover the PAE itself, so
/// stock tooling can verify it; a JWS over the PAE would verify under no
/// standard DSSE implementation. The two seams therefore sign different
/// messages and one cannot be expressed in terms of the other — a
/// deployment fills this one from the same key material, key id and
/// algorithm set its `IArtefactSigner` uses.
type IStatementEnvelopeSigner =
    /// The active signing-key id, stamped into the envelope's signature
    /// entry so a verifier knows which public key to resolve.
    abstract KeyId: unit -> string

    /// Sign the pre-authentication encoding. The implementation signs the
    /// bytes verbatim — no hashing, framing or re-encoding beyond what the
    /// algorithm itself specifies.
    abstract SignPreAuthenticated: pae: byte[] -> Async<Result<EnvelopeKeySignature, string>>

/// What a verifier requires of a statement before it will hand the
/// payload back. Every field is a claim the holder brought with them, not
/// one read out of the envelope — an envelope cannot satisfy an
/// expectation by asserting it.
type EnvelopeExpectation = {
    /// The predicate type URI the holder is prepared to interpret. A
    /// statement of a different shape is refused rather than parsed
    /// hopefully.
    PredicateType: string
    /// The subject digest the holder independently possesses (a content
    /// id). `None` skips the subject check — appropriate only when the
    /// caller has no independent handle on the artefact.
    SubjectDigest: string option
}

/// The outcome of verifying an envelope. Every non-`EnvelopeValid` case
/// names a distinct failure: a caller must never be able to read a
/// refusal as a pass, and must never have to distinguish "the signature
/// did not check" from "I could not parse this" by inspecting a string.
type EnvelopeVerdict =
    /// Signature valid over the PAE, payload type and predicate type as
    /// expected, subject digest matched (when one was expected).
    | EnvelopeValid
    /// Something needed to perform the check could not be READ: the
    /// envelope, its payload, the statement inside it, a signature blob,
    /// or the verifying key. Never a pass, and never conflated with a
    /// signature failure — "I cannot check this" and "this is wrong" are
    /// different answers, and a holder acts on them differently.
    | EnvelopeMalformed of reason: string
    /// The envelope's `payloadType` is not the one expected.
    | EnvelopePayloadTypeMismatch of expected: string * actual: string
    /// The statement's `predicateType` is not the one expected — the
    /// claim is about a different shape of thing.
    | EnvelopePredicateTypeMismatch of expected: string * actual: string
    /// No subject digest in the statement matches the one the holder
    /// brought: a correctly-signed statement about a DIFFERENT artefact.
    | EnvelopeSubjectMismatch of expected: string * actual: string
    /// No signature entry carries the verifying key's id — including the
    /// case where a signature block was transplanted from an envelope
    /// signed under another key.
    | EnvelopeUnsignedForKey of keyId: string
    /// A signature for the verifying key is present and does NOT validate
    /// over this envelope's PAE: the payload was altered after signing, or
    /// the signature was transplanted from another envelope signed under
    /// the same key. Those two are cryptographically indistinguishable and
    /// are deliberately reported as one verdict.
    | EnvelopeSignatureInvalid

module EnvelopeVerdict =
    let describe =
        function
        | EnvelopeValid -> "envelope signature valid"
        | EnvelopeMalformed r -> $"malformed envelope: {r}"
        | EnvelopePayloadTypeMismatch(e, a) -> $"payload type mismatch: expected '{e}', found '{a}'"
        | EnvelopePredicateTypeMismatch(e, a) -> $"predicate type mismatch: expected '{e}', found '{a}'"
        | EnvelopeSubjectMismatch(e, a) -> $"subject digest mismatch: expected '{e}', found '{a}'"
        | EnvelopeUnsignedForKey k -> $"no signature for key id: {k}"
        | EnvelopeSignatureInvalid -> "signature does not validate over this envelope (tampered or transplanted)"

    /// `true` only for `EnvelopeValid`. Exists so a caller cannot write
    /// `<> EnvelopeSignatureInvalid` and accidentally treat a malformed
    /// envelope as verified.
    let isValid =
        function
        | EnvelopeValid -> true
        | _ -> false

module DsseEnvelope =

    /// The DSSE `payloadType` for an in-toto statement — the media type
    /// the in-toto attestation spec registers.
    [<Literal>]
    let InTotoPayloadType = "application/vnd.in-toto+json"

    /// The in-toto Statement v1 `_type` value.
    [<Literal>]
    let StatementType = "https://in-toto.io/Statement/v1"

    let private utf8 (s: string) = Encoding.UTF8.GetBytes s

    /// SHA-256 hex (lowercase) over UTF-8 bytes — the digest shape the
    /// in-toto `sha256` key names.
    let sha256Hex (bytes: byte[]) : string =
        use sha = SHA256.Create()
        sha.ComputeHash(bytes) |> Convert.ToHexStringLower

    /// DSSE Pre-Authentication Encoding — the bytes a signature actually
    /// covers:
    ///
    ///   `"DSSEv1" SP LEN(type) SP type SP LEN(body) SP body`
    ///
    /// `LEN` is the ASCII decimal **byte** length (not character count),
    /// which is what binds a multi-byte payload unambiguously. Signing the
    /// PAE rather than the payload is what stops a payload being replayed
    /// under a different `payloadType`.
    let pae (payloadType: string) (payload: byte[]) : byte[] =
        let typeBytes = utf8 payloadType
        let prefix = utf8 $"DSSEv1 {typeBytes.Length} {payloadType} {payload.Length} "
        Array.append prefix payload

    // ── statement assembly ──────────────────────────────────────────────

    /// Assemble an in-toto Statement v1 as JSON. `predicateJson` is
    /// spliced in verbatim as the `predicate` value (it must be a JSON
    /// object); the envelope never inspects or rewrites it.
    let statementJson (subjects: InTotoSubject list) (predicateType: string) (predicateJson: string) : string =
        let subjectArray = JsonArray()

        for s in subjects do
            let digest = JsonObject()

            for alg, value in s.Digest do
                digest[alg] <- JsonValue.Create(value)

            let entry = JsonObject()
            entry["name"] <- JsonValue.Create(s.Name)
            entry["digest"] <- digest
            subjectArray.Add(entry)

        let o = JsonObject()
        o["_type"] <- JsonValue.Create(StatementType)
        o["subject"] <- subjectArray
        o["predicateType"] <- JsonValue.Create(predicateType)
        o["predicate"] <- JsonNode.Parse(predicateJson)
        o.ToJsonString()

    // ── envelope JSON ───────────────────────────────────────────────────

    /// Serialise an envelope to its DSSE JSON wire form (`payload`,
    /// `payloadType`, `signatures[].keyid` / `.sig`).
    let toJson (envelope: DsseEnvelope) : string =
        let sigs = JsonArray()

        for s in envelope.Signatures do
            let entry = JsonObject()
            entry["keyid"] <- JsonValue.Create(s.KeyId)
            entry["sig"] <- JsonValue.Create(s.Sig)
            sigs.Add(entry)

        let o = JsonObject()
        o["payloadType"] <- JsonValue.Create(envelope.PayloadType)
        o["payload"] <- JsonValue.Create(envelope.Payload)
        o["signatures"] <- sigs
        o.ToJsonString()

    /// Parse a DSSE envelope from its JSON wire form. Structural only —
    /// nothing is verified here.
    let parse (json: string) : Result<DsseEnvelope, string> =
        try
            let node = JsonNode.Parse(json)

            let sigs =
                match node["signatures"] with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.map (fun e -> {
                        KeyId = e["keyid"].GetValue<string>()
                        Sig = e["sig"].GetValue<string>()
                    })
                    |> List.ofSeq
                | _ -> []

            Ok {
                PayloadType = node["payloadType"].GetValue<string>()
                Payload = node["payload"].GetValue<string>()
                Signatures = sigs
            }
        with ex ->
            Error ex.Message

    /// The envelope's decoded payload bytes.
    let payloadBytes (envelope: DsseEnvelope) : Result<byte[], string> =
        try
            Ok(Convert.FromBase64String envelope.Payload)
        with ex ->
            Error $"payload is not valid base64: {ex.Message}"

    /// The exact bytes this envelope's signatures cover.
    let paeOf (envelope: DsseEnvelope) : Result<byte[], string> =
        payloadBytes envelope |> Result.map (pae envelope.PayloadType)

    // ── structural checks (crypto-free) ─────────────────────────────────

    /// The parsed statement's `predicateType`, `subject` digest values,
    /// and raw `predicate` JSON.
    type ParsedStatement = {
        PredicateType: string
        SubjectDigests: string list
        PredicateJson: string
    }

    /// Parse the in-toto statement out of an envelope's payload. Refuses
    /// anything that is not a Statement v1 document rather than reading
    /// what it can and hoping.
    let readStatement (envelope: DsseEnvelope) : Result<ParsedStatement, EnvelopeVerdict> =
        match payloadBytes envelope with
        | Error e -> Error(EnvelopeMalformed e)
        | Ok bytes ->
            try
                let node = JsonNode.Parse(Encoding.UTF8.GetString bytes)

                match node["_type"] with
                | null -> Error(EnvelopeMalformed "statement has no _type")
                | t when t.GetValue<string>() <> StatementType ->
                    Error(EnvelopeMalformed $"unsupported statement type: {t.GetValue<string>()}")
                | _ ->
                    let digests =
                        match node["subject"] with
                        | :? JsonArray as arr ->
                            arr
                            |> Seq.collect (fun s ->
                                match s["digest"] with
                                | :? JsonObject as d -> d |> Seq.map (fun kv -> kv.Value.GetValue<string>())
                                | _ -> Seq.empty)
                            |> List.ofSeq
                        | _ -> []

                    match node["predicate"], node["predicateType"] with
                    | null, _ -> Error(EnvelopeMalformed "statement has no predicate")
                    | _, null -> Error(EnvelopeMalformed "statement has no predicateType")
                    | predicate, predicateType ->
                        Ok {
                            PredicateType = predicateType.GetValue<string>()
                            SubjectDigests = digests
                            PredicateJson = predicate.ToJsonString()
                        }
            with ex ->
                Error(EnvelopeMalformed $"statement is not parseable JSON: {ex.Message}")

    /// Check everything about an envelope that does NOT need a key:
    /// payload type, statement shape, predicate type, subject digest.
    /// Returns the parsed statement only on a pass — a caller cannot
    /// reach the predicate through a failing check.
    let checkShape
        (expectation: EnvelopeExpectation)
        (envelope: DsseEnvelope)
        : Result<ParsedStatement, EnvelopeVerdict> =
        if envelope.PayloadType <> InTotoPayloadType then
            Error(EnvelopePayloadTypeMismatch(InTotoPayloadType, envelope.PayloadType))
        else
            match readStatement envelope with
            | Error v -> Error v
            | Ok statement ->
                if statement.PredicateType <> expectation.PredicateType then
                    Error(EnvelopePredicateTypeMismatch(expectation.PredicateType, statement.PredicateType))
                else
                    match expectation.SubjectDigest with
                    | Some expected when not (statement.SubjectDigests |> List.contains expected) ->
                        Error(EnvelopeSubjectMismatch(expected, statement.SubjectDigests |> String.concat ", "))
                    | _ -> Ok statement

    // ── emit ────────────────────────────────────────────────────────────

    /// Build and sign an envelope over an in-toto statement. One
    /// `SignPreAuthenticated` call, over the PAE of the assembled
    /// statement.
    let sign
        (signer: IStatementEnvelopeSigner)
        (subjects: InTotoSubject list)
        (predicateType: string)
        (predicateJson: string)
        : Async<Result<DsseEnvelope, string>> =
        async {
            try
                let payload = utf8 (statementJson subjects predicateType predicateJson)

                match! signer.SignPreAuthenticated(pae InTotoPayloadType payload) with
                | Error e -> return Error e
                | Ok signature ->
                    return
                        Ok {
                            PayloadType = InTotoPayloadType
                            Payload = Convert.ToBase64String payload
                            Signatures = [
                                {
                                    KeyId = signature.KeyId
                                    Sig = Convert.ToBase64String signature.Signature
                                }
                            ]
                        }
            with ex ->
                return Error $"could not assemble statement: {ex.Message}"
        }

    /// The signature entry for `keyId`, if the envelope carries one.
    let signatureFor (keyId: string) (envelope: DsseEnvelope) : DsseSignature option =
        envelope.Signatures |> List.tryFind (fun s -> s.KeyId = keyId)

    /// Decode a signature entry's raw bytes.
    let signatureBytes (signature: DsseSignature) : Result<byte[], string> =
        try
            Ok(Convert.FromBase64String signature.Sig)
        with ex ->
            Error $"signature is not valid base64: {ex.Message}"

    /// Round-trip a JSON document through `JsonDocument` to confirm it is
    /// a well-formed object — the one precondition `statementJson` places
    /// on a caller's predicate.
    let isJsonObject (json: string) : bool =
        try
            use doc = JsonDocument.Parse(json)
            doc.RootElement.ValueKind = JsonValueKind.Object
        with _ ->
            false