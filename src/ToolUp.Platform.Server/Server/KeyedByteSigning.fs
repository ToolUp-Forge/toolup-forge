// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text

// ─── Keyed byte signing — the shared, crypto-free half ──────────────────
//
// Several substrates here record a detached signature beside the bytes it
// covers, and each needs the same two facts persisted alongside it: WHICH
// KEY made the signature, and WHAT A VERIFIER MUST KNOW to check it rather
// than guess. Each has so far declared its own small interface for the
// purpose, because asking for more would have coupled it to whichever key
// substrate happened to exist when it was written.
//
// That independence was right, and it has a cost that only shows up later:
// every such seam is an island, so a deployment composing three of them
// ends up with three key stories where it wanted one — three places a key
// is configured, three rotations to remember, and no single answer to
// "what signed this, and what does the signature claim?". This module is
// the shared shape those local seams can be BRIDGED onto without any of
// them taking a key-management dependency: the format half, carrying no
// crypto at all (GP 1). The signing companion fills it once; each
// recording substrate maps its own seam onto it in a few lines and keeps
// that seam exactly as it was (GP 11).
//
// **The key id is bound into the signed message, not merely recorded
// beside it.** A recording surface persists the key id in a field, and a
// field is editable: a signature covering only the payload says nothing
// about which key a reader was told to resolve, so re-pointing a stored
// record at a different key would be invisible. `bindKeyId` frames the id
// into the message the signature is taken over, which turns that edit into
// a failed verification.
//
// **Nothing composes this.** A deployment that bridges no seam onto it
// registers no service and allocates nothing (GP 13).

/// Produces a detached signature over an opaque, already-canonical byte
/// string.
///
/// Deliberately narrow: three facts, no key lifecycle, no algorithm
/// selection, no configuration. Everything richer belongs to whichever
/// substrate fills this, and is reachable from there — a caller holding
/// only this seam is holding it precisely because it does not want to know.
type IKeyedByteSigner =
    /// Stable identifier for the key material, recorded beside the
    /// signature so a verifier can select the matching public key.
    /// Rotating means a NEW id, so signatures made under the previous one
    /// stay verifiable rather than being invalidated by the rotation.
    abstract KeyId: unit -> string

    /// The scheme name recorded beside the signature: everything a
    /// verifier needs in order to reconstruct the signed message and pick
    /// a primitive. Opaque here — a verifier for the scheme interprets it,
    /// and one that does not recognise it refuses rather than guessing.
    abstract Scheme: unit -> string

    /// Sign `message`. `Error` is surfaced to the caller rather than
    /// swallowed: a signature that silently did not happen is worse than
    /// no signature at all, because the record still looks signed.
    abstract Sign: message: byte[] -> Async<Result<byte[], string>>

/// Checks a signature produced by an `IKeyedByteSigner`. Needs only PUBLIC
/// key material, which is what makes a cold check possible — a holder with
/// the record and the public key confirms it without any access to the
/// signing environment.
type IKeyedByteVerifier =
    /// `Ok true` when the signature is valid for the recorded key and
    /// scheme; `Ok false` when it is well-formed but WRONG; `Error` when
    /// the signature cannot be accepted for a reason that is not a bad
    /// signature — an unknown key, an unrecognised scheme, a revoked key,
    /// a signature minted for a different use.
    ///
    /// The third case is not fastidiousness. Collapsing it into "invalid"
    /// tells a holder to go hunting for tampering that never happened,
    /// when what they actually have is a correctly-made signature they are
    /// not entitled to accept.
    abstract Verify: keyId: string * scheme: string * message: byte[] * signature: byte[] -> Async<Result<bool, string>>

module KeyedByteSigning =

    /// Framing version for `bindKeyId`. Bumping it invalidates every
    /// existing signature by construction, which is the intended behaviour
    /// for a framing change — so bump deliberately, with a migration note.
    [<Literal>]
    let BindingVersion = "toolup.keyed.v1"

    /// Frame the recording key id into the message the signature covers:
    /// `version|len(keyId)|keyId|len(message)|` as UTF-8, immediately
    /// followed by the raw message bytes.
    ///
    /// Length prefixes rather than delimiters, because a key id may
    /// contain any character and two distinct `(keyId, message)` pairs
    /// must never frame to the same bytes.
    let bindKeyId (keyId: string) (message: byte[]) : byte[] =
        let idBytes = Encoding.UTF8.GetBytes keyId

        let preamble =
            StringBuilder()
                .Append(BindingVersion)
                .Append('|')
                .Append(idBytes.Length)
                .Append('|')
                .Append(keyId)
                .Append('|')
                .Append(message.Length)
                .Append('|')
                .ToString()
            |> Encoding.UTF8.GetBytes

        Array.append preamble message