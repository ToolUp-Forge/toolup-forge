// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Security.Cryptography
open Org.BouncyCastle.Crypto.EC
open Org.BouncyCastle.Math
open Org.BouncyCastle.Math.EC

// ─── Layer 5 — production commutative-cipher backend ─────────────────
//
// `Apply k P = [k]P` on a prime-order elliptic curve. Scalar
// multiplication commutes because scalars multiply, and `Remove` is
// multiplication by `k⁻¹ mod n` — total for every key, because a
// cofactor-1 curve's group order `n` is prime, so every non-zero scalar
// below it is invertible. The curve arithmetic is BouncyCastle's; nothing
// here implements field or group operations.
//
// **Curve choice, and the deviation from the phase's Ristretto255.**
// Phase 18f specified a Ristretto255 backend over libsodium / NSec.
// Neither is reachable without a new dependency this companion should not
// take: BouncyCastle 2.6.2 ships no Ristretto255 and no RFC 9380
// hash-to-curve (checked against the shipped assembly, not assumed), and
// NSec's managed surface exposes X25519 key agreement and Ed25519
// signatures but not the raw `crypto_core_ristretto255_*` scalar
// arithmetic this needs — reaching it means P/Invoking libsodium, i.e. a
// native-dependency companion with RID-specific vendoring, hash-pinned
// artefacts and an LGPL-class packaging review. That is a phase of its
// own, not a file in this one. NIST P-256 is a prime-order group with
// cofactor 1, which is the only property the construction actually
// requires, and its arithmetic here is a vetted implementation rather
// than a hand-rolled one. The Ristretto255 backend stays open on the
// phase; adding it later is additive — a second `ICommutativeCipher`, not
// a change to this one.
//
// **`HashToPoint` is try-and-increment**, the classical construction:
// derive a candidate x-coordinate from a counter-salted digest, attempt to
// decompress it, and retry on failure. Roughly half of candidates decode,
// so the expected cost is two attempts. Two properties matter and one
// caveat is real:
//   - The resulting point's discrete logarithm relative to the generator
//     is unknown to everybody, including the caller. That is the security
//     requirement — see `ICommutativeCipher.HashToPoint`.
//   - It is deterministic and domain-separated, so two deployments derive
//     the same element for the same input and can never collide with the
//     reference backend or another protocol.
//   - The attempt COUNT depends on the input, so the running time leaks a
//     little about the pre-image. The input is the caller's own identifier
//     and is hashed locally before anything reaches the wire, so the
//     observer would have to be co-resident; RFC 9380's constant-time
//     encodings are the fix, and adopting one rides with the Ristretto255
//     backend.
//
// Zero cost when unused (GP 13): nothing here is composed by
// `PeerServerApp.run`. A deployment that never calls
// `PeerServerApp.withCommutativeCipher` allocates nothing and registers
// nothing.

/// Commutative cipher over a prime-order NIST / SEC named curve —
/// **the production backend**. Defaults to P-256.
///
/// Encoding is deliberately plain and interoperable: a key is the scalar
/// as a fixed-width big-endian byte string (32 bytes on P-256), and an
/// element is the SEC 1 compressed point encoding (33 bytes on P-256). A
/// counterparty on another stack needs only its own curve library to speak
/// this, which is the same posture the JSON-RPC peer wire takes.
///
/// Stateless between calls and safe for concurrent use (GP 12 rule 4).
type EcCommutativeCipher(curveName: string) =

    let parameters =
        match CustomNamedCurves.GetByName curveName with
        | null -> invalidArg (nameof curveName) $"'{curveName}' is not a known named curve"
        | found -> found

    let curve = parameters.Curve
    let order = parameters.N

    // The cofactor guard is not a formality. On a cofactor > 1 curve the
    // full point group has composite order, so a scalar's inverse modulo
    // `n` no longer inverts `Apply` for an element outside the
    // prime-order subgroup — `Remove` would return a wrong answer rather
    // than an error, which is the worst failure shape available. Refuse at
    // construction instead.
    do
        if not (parameters.H.Equals BigInteger.One) then
            invalidArg
                (nameof curveName)
                $"'{curveName}' has cofactor {parameters.H} — a commutative cipher needs a prime-order (cofactor 1) group so every key is invertible"

    let scalarLength = (order.BitLength + 7) / 8
    let fieldLength = (curve.FieldSize + 7) / 8

    let domain =
        Text.Encoding.UTF8.GetBytes $"ToolUp.InterPlatform/CommutativeCipher/EC/v1/{curveName}"

    /// The reference backend's magic tags, rejected explicitly so a
    /// mixed-backend wiring mistake reports the cause rather than a bare
    /// length mismatch.
    let referenceKeyMagic = "TU!INKEY"B
    let referencePointMagic = "TU!INSEC"B

    /// `fieldLength` bytes of counter-salted digest, expanded across as
    /// many SHA-256 blocks as the field needs.
    let deriveCandidate (attempt: int) (input: byte[]) =
        let blocks = (fieldLength + 31) / 32

        let expanded =
            [|
                for block in 0 .. blocks - 1 ->
                    SHA256.HashData(Array.concat [ domain; [| byte attempt; byte block |]; input ])
            |]
            |> Array.concat

        expanded[.. fieldLength - 1]

    let decodeKey (key: byte[]) : Result<BigInteger, CommutativeCipherError> =
        if isNull key then
            Error LengthMismatch
        elif CommutativeCipher.hasPrefix referenceKeyMagic key then
            Error InvalidKey
        elif key.Length <> scalarLength then
            Error LengthMismatch
        else
            let k = BigInteger(1, key)

            if k.SignValue <= 0 || k.CompareTo order >= 0 then
                Error InvalidKey
            else
                Ok k

    let decodePoint (point: byte[]) : Result<ECPoint, CommutativeCipherError> =
        if isNull point then
            Error LengthMismatch
        elif CommutativeCipher.hasPrefix referencePointMagic point then
            Error InvalidPoint
        elif point.Length <> fieldLength + 1 then
            Error LengthMismatch
        else
            let decoded =
                try
                    Some(curve.DecodePoint point)
                with _ ->
                    // BouncyCastle throws on a malformed encoding and on
                    // an x-coordinate with no square root. Both mean the
                    // counterparty sent something that is not an element,
                    // which is protocol data, not an exceptional condition.
                    None

            match decoded with
            | None -> Error InvalidPoint
            | Some p when p.IsInfinity -> Error InvalidPoint
            | Some p when not (p.IsValid()) -> Error NotOnCurve
            | Some p -> Ok p

    let multiply (scalar: BigInteger) (point: ECPoint) =
        let product = point.Multiply(scalar).Normalize()

        if product.IsInfinity then
            // Unreachable on a prime-order curve for a non-identity point
            // and a scalar in [1, n): the product's order divides n, which
            // is prime. Reported rather than asserted so a future curve
            // whose guard was loosened fails as data.
            Error InvalidPoint
        else
            Ok(product.GetEncoded true)

    /// The default production shape: NIST P-256.
    new() = EcCommutativeCipher("P-256")

    /// The curve this instance operates on, for diagnostics and for the
    /// conformance pack's published-parameter check.
    member _.CurveName = curveName

    /// The prime order of the group, as a big-endian byte string. Exposed
    /// so a caller can pin which curve it is actually talking to against
    /// published parameters rather than trusting a name string.
    member _.GroupOrder = CommutativeCipher.toFixed scalarLength order

    interface ICommutativeCipher with
        member _.GenerateKey() =
            CommutativeCipher.toFixed scalarLength (CommutativeCipher.randomScalar order)

        member _.HashToPoint(input) =
            let rec attempt (counter: int) =
                if counter > 255 then
                    // Probability ~2^-256 for a curve where half the field
                    // elements are valid x-coordinates. Failing loudly
                    // beats returning a degenerate element.
                    failwith
                        "hash-to-curve exhausted its counter — the curve parameters are not what this backend expects"
                else
                    let candidate = deriveCandidate counter input
                    let compressed = Array.append [| 0x02uy |] candidate

                    let decoded =
                        try
                            let p = curve.DecodePoint compressed
                            if p.IsInfinity || not (p.IsValid()) then None else Some p
                        with _ ->
                            None

                    match decoded with
                    | Some p -> p.Normalize().GetEncoded true
                    | None -> attempt (counter + 1)

            attempt 0

        member _.Apply key point =
            match decodeKey key, decodePoint point with
            | Error e, _ -> Error e
            | _, Error e -> Error e
            | Ok k, Ok p -> multiply k p

        member _.Remove key point =
            match decodeKey key, decodePoint point with
            | Error e, _ -> Error e
            | _, Error e -> Error e
            | Ok k, Ok p -> multiply (k.ModInverse order) p

[<RequireQualifiedAccess>]
module EcCommutativeCipher =
    /// The production backend on NIST P-256.
    let create () : ICommutativeCipher =
        EcCommutativeCipher() :> ICommutativeCipher

    /// The production backend on a caller-named prime-order curve.
    /// Rejects a curve with cofactor > 1 at construction.
    let onCurve (curveName: string) : ICommutativeCipher =
        EcCommutativeCipher(curveName) :> ICommutativeCipher