// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open System.Text
open System.Text.Json.Nodes

// ─── Phase 40 / 22a — public JWS-assembly surface ───────────────────────
//
// The in-process `DefaultArtefactSigner` owns its detached-JWS shape
// privately (`SigningInternals.Jws`). A KMS-backed signer lives in a
// separate companion package (the private key never enters process
// memory, so it cannot reuse the in-process crypto) but MUST produce a
// byte-identical detached JWS that the shipped `DefaultArtefactVerifier`
// validates. This module exposes the small, signer-agnostic pieces of
// that shape — header encoding, signing input, detached-JWS assembly —
// plus the DER→IEEE-P1363 conversion a KMS asymmetric `Sign` needs (KMS
// returns an ASN.1 DER ECDSA signature; JWS ES256 wants raw r‖s).
//
// The verifier re-derives the signing input from the *encoded header*
// verbatim, so a JWS assembled here verifies regardless of which signer
// produced the signature, as long as the header carries a recognised
// `alg`.
module JwsBuilder =

    /// base64url (RFC 4648 §5, no padding) — the JWS segment encoding.
    let base64UrlEncode (bytes: byte[]) : string =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let base64UrlDecode (s: string) : byte[] =
        let padded =
            match s.Length % 4 with
            | 2 -> s + "=="
            | 3 -> s + "="
            | _ -> s

        Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'))

    /// base64url-encoded protected header `{ alg, kid, typ:"JOSE" }` — the
    /// first JWS segment. Matches the in-process signer's header shape.
    let protectedHeaderEncoded (algorithm: SigningAlgorithm) (keyId: string) : string =
        let o = JsonObject()
        o["alg"] <- JsonValue.Create(SigningAlgorithm.jwsAlg algorithm)
        o["kid"] <- JsonValue.Create(keyId)
        o["typ"] <- JsonValue.Create("JOSE")
        base64UrlEncode (Encoding.UTF8.GetBytes(o.ToJsonString()))

    /// `base64url(header) + "." + base64url(artefact)` — the bytes the
    /// signature is computed over (the JWS signing input).
    let signingInput (encodedHeader: string) (artefact: byte[]) : byte[] =
        Encoding.UTF8.GetBytes(encodedHeader + "." + base64UrlEncode artefact)

    /// Assemble a detached JWS (`base64url(header) + ".." +
    /// base64url(rawSignature)`) — empty payload segment. `rawSignature`
    /// is the JWS-shaped signature: raw r‖s for ECDSA (use
    /// `derEcdsaToP1363` on a KMS DER signature first), raw 64 bytes for
    /// Ed25519.
    let assembleDetachedJws (encodedHeader: string) (rawSignature: byte[]) : string =
        encodedHeader + ".." + base64UrlEncode rawSignature

    /// Convert an ASN.1 DER-encoded ECDSA signature (`SEQUENCE { INTEGER
    /// r, INTEGER s }`, the shape AWS KMS / most HSMs return) to IEEE
    /// P1363 fixed-width `r‖s` (the JWS ES256 shape). `coordSize` is the
    /// per-coordinate byte width — 32 for P-256 (ES256), 48 for P-384,
    /// 66 for P-521. Returns `Error` on a malformed DER structure or a
    /// coordinate wider than `coordSize`.
    let derEcdsaToP1363 (der: byte[]) (coordSize: int) : Result<byte[], string> =
        try
            let mutable pos = 0

            let readLen () =
                let b = int der[pos]
                pos <- pos + 1

                if b < 0x80 then
                    b
                else
                    let n = b &&& 0x7f
                    let mutable len = 0

                    for _ in 1..n do
                        len <- (len <<< 8) ||| int der[pos]
                        pos <- pos + 1

                    len

            if der[pos] <> 0x30uy then
                failwith "expected DER SEQUENCE"

            pos <- pos + 1
            readLen () |> ignore // sequence length — not needed beyond bounds

            let readInteger () =
                if der[pos] <> 0x02uy then
                    failwith "expected DER INTEGER"

                pos <- pos + 1
                let len = readLen ()
                let v = der[pos .. pos + len - 1]
                pos <- pos + len
                v

            let r = readInteger ()
            let s = readInteger ()

            // Strip the DER sign-padding leading zero(s), then left-pad to
            // the fixed coordinate width.
            let fixWidth (x: byte[]) : byte[] =
                let mutable i = 0

                while i < x.Length - 1 && x[i] = 0uy do
                    i <- i + 1

                let trimmed = x[i..]

                if trimmed.Length > coordSize then
                    failwith "ECDSA coordinate wider than coordSize"

                Array.append (Array.zeroCreate (coordSize - trimmed.Length)) trimmed

            Ok(Array.append (fixWidth r) (fixWidth s))
        with ex ->
            Error ex.Message