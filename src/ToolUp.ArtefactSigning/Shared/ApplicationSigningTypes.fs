// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open System.Text

// ─── Application signing — shared types ────────────────────────────────
//
// `IArtefactSigner` signs opaque bytes. That is the right shape for a
// build/publish pipeline, where "the artefact" is a file and the caller
// already knows what it is. It is the wrong shape on its own for an
// APPLICATION, which signs many different kinds of payload through one
// composed signer and needs each signature to say three further things:
//
//   * what the payload was signed AS (its purpose) — so a signature
//     minted for one use cannot be replayed into another;
//   * what the signature CLAIMS about the environment that produced it
//     (its attestation level) — an in-process key and a key held inside
//     an isolated boundary are not the same assertion, and a relying
//     party must be able to tell them apart;
//   * which key produced it, against a recorded key history — so a
//     signature outlives the rotation of the key that made it, and a
//     revoked key stops being accepted without anything being deleted.
//
// These types carry those three facts. They are purely additive: nothing
// here changes `ArtefactSignature`, the publish-pipeline surface, or any
// existing signer. A deployment that never composes an application
// signer is byte-for-byte unchanged (GP 11) and pays nothing (GP 13).

/// What a signature asserts about the environment that produced it.
///
/// The level is **bound into the signed bytes**, not merely recorded
/// beside them (see `ApplicationPayload.canonicalBytes`): an envelope
/// whose level is edited after the fact fails verification rather than
/// silently claiming more than the signer could. A relying party can
/// therefore trust the level exactly as far as it trusts the key.
///
/// Semantics, from weakest claim to strongest:
///
///   * `Attribution` — the signature attributes the payload to this
///     deployment's key, and nothing more. The private key is reachable
///     from the signing process (it is loaded from the deployment's own
///     secret store to sign), so anything able to read that store, or to
///     read the process's memory, can mint an equivalent signature.
///     This is the honest level for a key-file or secret-store-held key,
///     including in development.
///
///   * `IsolatedSigner` — the private key is held inside a boundary the
///     signing process cannot read: the process sends a digest out and
///     receives a signature back, and never sees key material. A key
///     managed by an external key-management service or a hardware
///     module has this property. The claim is that compromising the
///     application host does not yield the key — NOT that the payload
///     was produced by trusted code, which no signature over bytes can
///     establish on its own.
///
///   * `Reserved of label` — forward compatibility for levels that make
///     a claim about the executing environment itself (for example a
///     signature accompanied by a hardware-generated quote over the
///     code that produced it). No such level is implemented here; the
///     case exists so a future one can be introduced without retyping
///     this DU, and so an envelope carrying a level this build does not
///     understand round-trips rather than failing to parse. Treat an
///     unrecognised reserved label as UNVERIFIED — carrying a label is
///     not evidence for the claim the label names.
type AttestationLevel =
    | Attribution
    | IsolatedSigner
    | Reserved of label: string

module AttestationLevel =
    /// Stable wire/persistence name. Do not rename an existing case name
    /// without a compatibility note — the name is signed over, so a
    /// rename invalidates every signature already minted at that level.
    let name =
        function
        | Attribution -> "attribution"
        | IsolatedSigner -> "isolated-signer"
        | Reserved label -> "reserved:" + label

    /// Parse the persisted name. An unrecognised value round-trips as
    /// `Reserved` rather than failing, so a newer producer's level does
    /// not make an envelope unreadable — but see the type's doc comment:
    /// a reserved label is not evidence.
    let parse (s: string) : AttestationLevel =
        match s with
        | "attribution" -> Attribution
        | "isolated-signer" -> IsolatedSigner
        | _ when s.StartsWith("reserved:", StringComparison.Ordinal) -> Reserved(s.Substring "reserved:".Length)
        | other -> Reserved other

    /// One-line human description of what this level does and does not
    /// claim — suitable for an operator-facing surface.
    let describe =
        function
        | Attribution ->
            "attributes the payload to this deployment's signing key; the key is reachable from the signing process"
        | IsolatedSigner ->
            "the private key is held outside the signing process and never enters its memory; host compromise does not yield the key"
        | Reserved label -> $"reserved attestation level '{label}' — not understood by this build; treat as unverified"

/// A lifecycle transition of a signing key. Key lifecycle is recorded as
/// data — an append-only sequence of attributable events — rather than
/// inferred from which key file happens to be present.
/// Qualified access is deliberate: `Revoked` (an event that was
/// recorded) and `KeyRevoked` (a verification refused because of one)
/// are different statements, and unqualified cases would let a reader —
/// or an overload resolution — quietly conflate them.
[<RequireQualifiedAccess>]
type SigningKeyEventKind =
    /// The key became the deployment's active signing key.
    | Activated
    /// The key stopped being used for new signatures. Signatures already
    /// made under it remain valid — retirement is rotation, not distrust.
    | Retired
    /// The key is no longer trusted. Signatures made under it are
    /// refused from this point on, including ones made before the
    /// revocation: revocation is distrust of the key, not of a moment.
    | Revoked

module SigningKeyEventKind =
    let name =
        function
        | SigningKeyEventKind.Activated -> "activated"
        | SigningKeyEventKind.Retired -> "retired"
        | SigningKeyEventKind.Revoked -> "revoked"

    let tryParse (s: string) : SigningKeyEventKind option =
        match s with
        | "activated" -> Some SigningKeyEventKind.Activated
        | "retired" -> Some SigningKeyEventKind.Retired
        | "revoked" -> Some SigningKeyEventKind.Revoked
        | _ -> None

/// One recorded, attributable lifecycle event for a signing key.
type SigningKeyEvent = {
    KeyId: string
    Kind: SigningKeyEventKind
    /// When the transition was recorded.
    At: DateTimeOffset
    /// Who recorded it — `"system"` for an automated rotation, an
    /// authenticated operator id for a deliberate one. Attribution is
    /// the point of recording the event rather than mutating a key file.
    Actor: string
    /// Free-text justification. Required in practice for a revocation
    /// (it is what a relying party is shown); optional otherwise.
    Reason: string option
}

/// The derived trust state of a key, folded from its recorded events.
type SigningKeyState =
    | ActiveKey
    | RetiredKey
    /// Terminal — a revoked key never returns to any other state.
    | RevokedKey of at: DateTimeOffset * reason: string

/// One key's recorded history plus the state it folds to.
type SigningKeyHistoryEntry = {
    KeyId: string
    State: SigningKeyState
    /// Every recorded event for this key, oldest first.
    Events: SigningKeyEvent list
}

/// The deployment's whole recorded signing-key history. This is what
/// makes rotation continuous: a signature names the key that produced
/// it, and the history says whether that key is still trusted — so a
/// signature outlives the key's retirement without any signature being
/// re-issued.
type SigningKeyHistory = { Entries: SigningKeyHistoryEntry list }

module SigningKeyHistory =

    /// Fold one key's events (any order) into its trust state.
    /// Revocation is terminal and wins regardless of ordering, so a
    /// later activation cannot silently un-revoke a key.
    let stateOf (events: SigningKeyEvent list) : SigningKeyState =
        let ordered = events |> List.sortBy _.At

        let revoked =
            ordered
            |> List.tryPick (fun e ->
                match e.Kind with
                | SigningKeyEventKind.Revoked -> Some(e.At, defaultArg e.Reason "revoked")
                | _ -> None)

        match revoked with
        | Some(at, reason) -> RevokedKey(at, reason)
        | None ->
            match ordered |> List.tryLast with
            | Some e when e.Kind = SigningKeyEventKind.Retired -> RetiredKey
            | Some _ -> ActiveKey
            | None -> ActiveKey

    /// Build a history from a flat event sequence.
    let ofEvents (events: SigningKeyEvent seq) : SigningKeyHistory =
        let entries =
            events
            |> Seq.groupBy _.KeyId
            |> Seq.map (fun (keyId, es) ->
                let ordered = es |> List.ofSeq |> List.sortBy _.At

                {
                    KeyId = keyId
                    State = stateOf ordered
                    Events = ordered
                })
            |> Seq.sortBy _.KeyId
            |> List.ofSeq

        { Entries = entries }

    /// The recorded entry for `keyId`, if the key has any history.
    let tryFind (keyId: string) (history: SigningKeyHistory) : SigningKeyHistoryEntry option =
        history.Entries |> List.tryFind (fun e -> e.KeyId = keyId)

    /// Every key currently in `ActiveKey` state.
    let activeKeyIds (history: SigningKeyHistory) : string list =
        history.Entries
        |> List.filter (fun e -> e.State = ActiveKey)
        |> List.map _.KeyId

/// A signature over an application payload, together with the two facts
/// that are bound into it: what the payload was signed AS, and what the
/// signature claims about the signer.
///
/// Deliberately a NEW type rather than a widened `ArtefactSignature`:
/// widening the existing record would retype its constructor and break
/// every publish-pipeline call site (and the public-API baseline). The
/// inner `Signature` is an ordinary `ArtefactSignature`, so anything that
/// already consumes one — the sidecar helpers, the public-key endpoint,
/// an external JWS verifier — consumes this envelope's signature too.
type SignedPayloadEnvelope = {
    /// What the payload was signed as. Bound into the signed bytes, so a
    /// signature minted for one purpose cannot be replayed as another.
    Purpose: string
    /// What this signature claims. Bound into the signed bytes.
    Level: AttestationLevel
    /// The detached JWS over the canonical framing of purpose + level +
    /// payload.
    Signature: ArtefactSignature
}

/// Failure modes for application-payload verification. Separate from
/// `VerificationError` (which stays the byte-level answer) so the
/// lifecycle answers — a revoked key, a replayed purpose — are not
/// flattened into "tampered".
///
/// Qualified access: `SignatureRejected` is a case name several unrelated
/// refusal types in the SDK also use, and an unqualified one here would
/// resolve by whichever `open` came last.
[<RequireQualifiedAccess>]
type PayloadVerificationError =
    /// The bytes did not verify against the signature. Carries the
    /// underlying byte-level reason, including `Tampered` for a payload,
    /// purpose, or attestation level edited after signing.
    | SignatureRejected of VerificationError
    /// The envelope was minted for a different purpose than the one the
    /// caller is verifying it under — a replay across uses.
    | PurposeMismatch of expected: string * actual: string
    /// The signing key has been revoked. Signatures under a revoked key
    /// are refused regardless of when they were made.
    | KeyRevoked of keyId: string * revokedAt: DateTimeOffset * reason: string

module PayloadVerificationError =
    let describe =
        function
        | PayloadVerificationError.SignatureRejected e -> VerificationError.describe e
        | PayloadVerificationError.PurposeMismatch(expected, actual) ->
            $"signature purpose mismatch: expected '{expected}', envelope carries '{actual}'"
        | PayloadVerificationError.KeyRevoked(keyId, at, reason) ->
            $"signing key '{keyId}' was revoked at {at:O}: {reason}"

/// The canonical byte framing an application signature is computed over.
///
/// The signed bytes are NOT the payload alone. They are a versioned,
/// length-prefixed framing of `(purpose, attestation level, payload)`, so
/// all three are covered by the one signature. That is what makes the
/// purpose and the level trustworthy rather than decorative: editing
/// either in the envelope changes the bytes and the signature stops
/// verifying.
///
/// Length prefixes rather than delimiters, because a delimiter can occur
/// inside a purpose string and two different `(purpose, payload)` pairs
/// must never frame to the same bytes.
module ApplicationPayload =

    /// Framing version. Bumping it invalidates existing signatures by
    /// construction, which is the intended behaviour for a framing
    /// change — so bump deliberately, with a migration note.
    [<Literal>]
    let FramingVersion = "toolup.appsig.v1"

    /// `version|len(purpose)|purpose|len(level)|level|len(payload)|` as
    /// UTF-8, immediately followed by the raw payload bytes.
    let canonicalBytes (purpose: string) (level: AttestationLevel) (payload: byte[]) : byte[] =
        let purposeBytes = Encoding.UTF8.GetBytes purpose
        let levelName = AttestationLevel.name level
        let levelBytes = Encoding.UTF8.GetBytes levelName

        let preamble =
            StringBuilder()
                .Append(FramingVersion)
                .Append('|')
                .Append(purposeBytes.Length)
                .Append('|')
                .Append(purpose)
                .Append('|')
                .Append(levelBytes.Length)
                .Append('|')
                .Append(levelName)
                .Append('|')
                .Append(payload.Length)
                .Append('|')
                .ToString()
            |> Encoding.UTF8.GetBytes

        Array.append preamble payload