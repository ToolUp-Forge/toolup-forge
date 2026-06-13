// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Base64Url

open System

// Single base64url (RFC 4648 §5) codec for the whole SDK. Before this
// module the encode/decode pair was hand-copied across every JWT /
// token site (StaticJwtAuthProvider, the OIDC JWT + id-token parsers,
// EntraExternalId, JwtPeerAuthProvider, ShareTokenStore, OAuthCrypto),
// with three divergent padding implementations — exactly the
// maintainability hazard a shared primitive exists to remove.
//
// Pure BCL `Convert` + string operations only — **Fable-safe** (it
// ships under `fable/` in the Core nupkg and is consumed by the
// Fable-compiled OIDC id-token validator). No `System.Security.
// Cryptography` here; the BCL-crypto primitives live in `JwtCrypto`,
// which is deliberately kept out of the Fable-packed surface.

/// Encode bytes as an unpadded base64url string: `+`→`-`, `/`→`_`,
/// trailing `=` padding stripped.
let encode (bytes: byte[]) : string =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

/// Decode an unpadded base64url string to bytes. Throws
/// `FormatException` on malformed input (non-base64url characters, or a
/// length ≡ 1 (mod 4) which cannot be valid base64) — use `tryDecode`
/// for untrusted input where a categorised `None` is preferred.
let decode (input: string) : byte[] =
    let s = input.Replace('-', '+').Replace('_', '/')

    let padded =
        match s.Length % 4 with
        | 2 -> s + "=="
        | 3 -> s + "="
        | _ -> s

    Convert.FromBase64String padded

/// Decode, returning `None` on any malformed input rather than throwing.
let tryDecode (input: string) : byte[] option =
    try
        Some(decode input)
    with _ ->
        None