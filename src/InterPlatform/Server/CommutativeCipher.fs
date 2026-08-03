// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Security.Cryptography
open Org.BouncyCastle.Math
open Org.BouncyCastle.Utilities

// ─── Layer 5 — commutative cipher (oblivious PRF) ────────────────────
//
// A commutative cipher has one property everything here rests on: a value
// encrypted under two keys in either order yields the same ciphertext —
// `Apply ka (Apply kb x) = Apply kb (Apply ka x)`. Two deployments can
// therefore each apply their own secret key to both sets, exchange the
// doubly-encrypted lists, and match on equal ciphertexts to learn the
// INTERSECTION of their identifier sets, while every non-matching element
// stays an opaque group element neither side can invert. That is the
// Diffie-Hellman private-set-intersection construction, and it is the
// missing primitive beneath the federation substrate: `ICleanRoomBroker`
// gates aggregate ANSWERS, but a clean-room query frequently needs to
// intersect two id sets privately first.
//
// Scope discipline (GP 1): this ships the MECHANISM only. Keys and group
// elements are opaque `byte[]`; the substrate takes no position on what an
// identifier means, how a caller canonicalises it, or what a match
// implies. A consumer maps its own identifiers to `byte[]` inputs and
// interprets the matched tokens itself — the same line `ICleanRoomBroker`
// draws at privacy policy and `IPeerFanout` draws at aggregation.
//
// **Why a PRIME-ORDER group.** `Remove` peels a key back off, which needs
// the key's scalar inverse modulo the group order. In a prime-order group
// every non-zero scalar is invertible, so `Remove` is total for every key
// `GenerateKey` can produce; in a group of composite order it would not
// be. Both shipped backends are prime-order by construction and both
// REJECT an element outside the prime-order subgroup (`NotOnCurve`) rather
// than silently computing in a small subgroup.
//
// **Vendor posture (GP 1).** The arithmetic comes from BouncyCastle, which
// this companion now declares directly rather than inheriting by accident
// — the same provider `Ed25519ArtifactSigner` and `ToolUp.ArtefactSigning`
// already use, and for the same reason: the BCL exposes no raw group
// arithmetic (no scalar multiplication of an arbitrary point, no modular
// inverse on `System.Numerics.BigInteger`), and hand-rolling curve
// arithmetic in an SDK is not a trade this estate makes. Nothing
// vendor-typed reaches the public surface: keys and elements are `byte[]`
// on the way in and out.
//
// Six portability rules (GP 12): identity is by value — keys and elements
// are `byte[]`, never a live key handle (rule 1); failure is data, a
// `Result` over a typed DU rather than a throw (rule 3); a backend holds
// no state between calls, so two instances are interchangeable mid-run
// (rule 4). `Apply` / `Remove` are synchronous: pure CPU over
// already-materialised bytes with nothing to await, the documented shape
// for a pure transform (the same exception `ICleanRoomBroker.Enforce`
// takes).

/// Why a commutative-cipher operation could not be performed. Failure as
/// data (GP 12 rule 3) — a malformed element from a counterparty is an
/// ordinary protocol outcome, not an exception.
type CommutativeCipherError =
    /// The element is the right length but not a well-formed encoding for
    /// this backend (wrong magic, out-of-range coordinate, malformed
    /// point encoding, or the identity element).
    | InvalidPoint
    /// The key is not a usable scalar for this backend: zero, at or above
    /// the group order, or carrying another backend's magic.
    | InvalidKey
    /// The encoding parsed but the element is not in the prime-order
    /// subgroup this backend operates in. Named for the elliptic-curve
    /// case; the modular-exponentiation backend reports the same case for
    /// a non-quadratic-residue, which is the identical defence.
    | NotOnCurve
    /// The key or element is not the byte length this backend encodes.
    /// Also what a caller sees when an element from one backend is handed
    /// to another.
    | LengthMismatch

/// A commutative cipher (OPRF) over a prime-order group. Keys and points
/// are opaque `byte[]`; `Apply` commutes across keys; `Remove` peels a key
/// back off (which is why the group must have prime order — see the file
/// header).
///
/// A backend is stateless between calls and safe for concurrent use.
type ICommutativeCipher =
    /// Draw a fresh secret key uniformly from the usable scalar range.
    /// The encoding is backend-specific and opaque to the caller.
    abstract GenerateKey: unit -> byte[]

    /// Map arbitrary input bytes onto a group element, deterministically,
    /// such that the element's discrete logarithm relative to the group
    /// generator is unknown to everybody — including the caller. That last
    /// clause is load-bearing: a "hash" that multiplied the generator by a
    /// scalar the caller knows would let a party divide a counterparty's
    /// blinded element by a guessed pre-image and then brute-force the
    /// counterparty's whole set offline.
    abstract HashToPoint: input: byte[] -> byte[]

    /// Encrypt `point` under `key`. Commutes: applying two keys in either
    /// order yields the same element.
    abstract Apply: key: byte[] -> point: byte[] -> Result<byte[], CommutativeCipherError>

    /// Peel `key` back off `point` — the inverse of `Apply` for the same
    /// key. Total for every key `GenerateKey` produces, because the group
    /// has prime order.
    abstract Remove: key: byte[] -> point: byte[] -> Result<byte[], CommutativeCipherError>

[<RequireQualifiedAccess>]
module CommutativeCipher =

    /// Constant-time byte equality over two already-length-checked
    /// buffers. Reused wherever a comparison's OUTCOME is
    /// secret-dependent — key-material framing here, and the
    /// membership decision in `PrivateSetIntersection` — following the
    /// platform convention (`JwtCrypto`, `PeerBearerAuthMiddleware`,
    /// `WebhookDispatcher`) that a secret-dependent `=` leaks a prefix
    /// length through timing. The length pre-check is structural:
    /// `FixedTimeEquals` requires equal-length spans and lengths are not
    /// themselves secret here.
    let bytesEqual (a: byte[]) (b: byte[]) : bool =
        not (isNull a)
        && not (isNull b)
        && a.Length = b.Length
        && CryptographicOperations.FixedTimeEquals(ReadOnlySpan a, ReadOnlySpan b)

    /// Draw a scalar uniformly from `[1, order - 1]`.
    ///
    /// FIPS 186-4 B.4.1 "extra random bits": draw 64 bits MORE than the
    /// order's width and reduce, so the modulo bias is below 2^-64 rather
    /// than the 2^-1-ish bias a naive same-width reduce-and-retry-free
    /// draw would carry. `RandomNumberGenerator` is the BCL CSPRNG and is
    /// thread-safe.
    let internal randomScalar (order: BigInteger) : BigInteger =
        let width = (order.BitLength + 7) / 8 + 8
        let bytes = RandomNumberGenerator.GetBytes width
        let candidate = BigInteger(1, bytes)
        candidate.Mod(order.Subtract BigInteger.One).Add BigInteger.One

    /// Fixed-width big-endian encoding, left-zero-padded. Fixed width
    /// matters: a variable-length encoding would make two equal elements
    /// compare unequal as bytes, which is the only comparison the
    /// intersection has.
    let internal toFixed (length: int) (value: BigInteger) : byte[] =
        BigIntegers.AsUnsignedByteArray(length, value)

    /// Constant-time prefix test used to frame a backend's magic tag.
    let internal hasPrefix (magic: byte[]) (bytes: byte[]) : bool =
        not (isNull bytes)
        && bytes.Length >= magic.Length
        && CryptographicOperations.FixedTimeEquals(ReadOnlySpan(bytes, 0, magic.Length), ReadOnlySpan magic)

/// **Reference backend. NOT FOR PRODUCTION USE.**
///
/// Commutative encryption by modular exponentiation in the prime-order
/// subgroup of quadratic residues modulo a 2048-bit safe prime:
/// `Apply k x = x^k mod p`, which commutes because exponents multiply.
/// Ships so a consumer can exercise the `ICommutativeCipher` contract, and
/// the PSI protocol above it, with nothing but this assembly — no curve
/// backend selection, no key-length negotiation, no test-only branch in
/// the consumer's own code. It is the same role the in-memory job / state
/// stores play elsewhere in this companion.
///
/// **Why it is marked rather than merely documented.** Every key and
/// element it produces carries an ASCII magic prefix (`TU!INKEY` /
/// `TU!INSEC` — the second reads "insecure" in a hex dump on purpose), and
/// every other backend rejects a value carrying one. So a deployment that
/// wires this by accident and then talks to a counterparty on a real
/// backend fails immediately and loudly at the first `Apply`, rather than
/// silently completing a protocol run whose privacy nobody reviewed. A
/// comment cannot do that.
///
/// The group itself is honest — the modulus is the published RFC 5054
/// 2048-bit group prime `N`, a safe prime (`(N-1)/2` is prime), verified
/// as such before it was pinned here — so the reference backend is slow
/// rather than weak. What it is not is reviewed, hardened, or
/// side-channel-analysed for production traffic, and 2048-bit modular
/// exponentiation is roughly two orders of magnitude slower per element
/// than the curve backend.
type InMemoryCommutativeCipher() =

    /// RFC 5054 §"2048-bit Group" modulus, big-endian hex. A safe prime:
    /// `p = 2q + 1` with `q` prime, so the quadratic residues form a
    /// subgroup of prime order `q` and every non-zero scalar below `q` is
    /// invertible modulo it.
    static let primeHex =
        "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050"
        + "A37329CBB4A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50"
        + "E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B8"
        + "55F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773B"
        + "CA97B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748"
        + "544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6"
        + "AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB6"
        + "94B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73"

    static let p = BigInteger(primeHex, 16)

    /// The prime order of the quadratic-residue subgroup.
    static let q = p.Subtract(BigInteger.One).Divide BigInteger.Two

    /// Byte width of an encoded element (2048 bits).
    static let elementLength = 256

    /// Byte width of an encoded scalar. 256 bits is a strict subset of
    /// `[1, q)` (q is 2047 bits), so every drawn key is a valid scalar.
    static let scalarLength = 32

    static let pointMagic = "TU!INSEC"B
    static let keyMagic = "TU!INKEY"B
    static let magicLength = 8

    /// Domain separation, so an element produced here can never collide
    /// with one produced by another backend or another protocol.
    static let domain = "ToolUp.InterPlatform/CommutativeCipher/InMemory/v1"B

    static let encodePoint (x: BigInteger) =
        Array.append pointMagic (CommutativeCipher.toFixed elementLength x)

    static let decodePoint (bytes: byte[]) : Result<BigInteger, CommutativeCipherError> =
        if isNull bytes || bytes.Length <> magicLength + elementLength then
            Error LengthMismatch
        elif not (CommutativeCipher.hasPrefix pointMagic bytes) then
            Error InvalidPoint
        else
            let x = BigInteger(1, bytes[magicLength..])

            if x.SignValue <= 0 || x.CompareTo p >= 0 || x.Equals BigInteger.One then
                Error InvalidPoint
            elif not (x.ModPow(q, p).Equals BigInteger.One) then
                // Subgroup-membership check: `x^q = 1` iff `x` is a
                // quadratic residue. Without it a counterparty could send
                // a generator of the order-2 subgroup and learn a bit of
                // the local key from the response — the small-subgroup
                // attack, and the exact analogue of the curve backend's
                // on-curve check.
                Error NotOnCurve
            else
                Ok x

    static let decodeKey (bytes: byte[]) : Result<BigInteger, CommutativeCipherError> =
        if isNull bytes || bytes.Length <> magicLength + scalarLength then
            Error LengthMismatch
        elif not (CommutativeCipher.hasPrefix keyMagic bytes) then
            Error InvalidKey
        else
            let k = BigInteger(1, bytes[magicLength..])

            if k.SignValue <= 0 || k.CompareTo q >= 0 then
                Error InvalidKey
            else
                Ok k

    interface ICommutativeCipher with
        member _.GenerateKey() =
            let k = CommutativeCipher.randomScalar (BigInteger.Two.Pow(scalarLength * 8))
            Array.append keyMagic (CommutativeCipher.toFixed scalarLength k)

        member _.HashToPoint(input) =
            // Squaring lands the digest in the quadratic-residue subgroup
            // unconditionally, which is what makes this a hash INTO the
            // prime-order group rather than into the full multiplicative
            // group. The counter loop is a formality — it retries only if
            // the square is 0 or 1, which no digest realistically produces
            // — but it keeps the function total by construction.
            let rec attempt (counter: int) =
                let digest = SHA512.HashData(Array.concat [ domain; [| byte counter |]; input ])

                let candidate = BigInteger(1, digest).Mod(p)
                let squared = candidate.ModPow(BigInteger.Two, p)

                if squared.SignValue <= 0 || squared.Equals BigInteger.One then
                    attempt (counter + 1)
                else
                    encodePoint squared

            attempt 0

        member _.Apply key point =
            match decodeKey key, decodePoint point with
            | Error e, _ -> Error e
            | _, Error e -> Error e
            | Ok k, Ok x -> Ok(encodePoint (x.ModPow(k, p)))

        member _.Remove key point =
            match decodeKey key, decodePoint point with
            | Error e, _ -> Error e
            | _, Error e -> Error e
            | Ok k, Ok x ->
                // `x^(k · k⁻¹ mod q) = x^(1 + mq) = x · (x^q)^m = x`,
                // because `x` is in the order-`q` subgroup. This identity
                // is the whole reason the group must have prime order.
                let inverse = k.ModInverse q
                Ok(encodePoint (x.ModPow(inverse, p)))

[<RequireQualifiedAccess>]
module InMemoryCommutativeCipher =
    /// The reference backend behind the seam. **Not for production use** —
    /// see the type's documentation.
    let create () : ICommutativeCipher =
        InMemoryCommutativeCipher() :> ICommutativeCipher