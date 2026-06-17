// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.JwtCrypto

// Shared HS256 / HMAC primitives for the symmetric-JWT validators that
// previously each carried a verbatim copy (StaticJwtAuthProvider, the
// InterPlatform peer auth provider, and ShareTokenStore). GP 1 permits
// BCL crypto in Core.
//
// **Server-only.** The implementation is gated `#if !FABLE_COMPILER`
// because it uses `System.Security.Cryptography`, which Fable cannot
// transpile. The `module` declaration above sits OUTSIDE the guard, so
// this file ships under the `fable/`-packed nupkg surface and a Fable
// consumer transpiles it to a valid empty module (the guard strips the
// crypto body). This is required: the Core .fsproj lists this file as a
// <Compile> item and is itself packed into fable/, so excluding the .fs
// would leave the packed Fable fsproj pointing at a missing source
// (FS0222). The same guard also covers the ProjectReference Fable path
// (a Fable client referencing Platform.Client → Core compiles Core's
// whole `<Compile>` graph). Every consumer (StaticJwt / peer auth /
// ShareTokenStore) is server-tier, so no Fable-compiled code references
// these members. The Fable-safe base64url codec lives separately in
// `Base64Url`.
//
// The OIDC providers' *asymmetric* JWS verification (RS256/ES256/PS256,
// `JwsAlgorithm.tryParse` / `verifyJws`) is a different trust model and
// is correctly kept where it is — not folded in here.

#if !FABLE_COMPILER

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// Clock-skew tolerance applied to `exp` / `nbf`, in seconds. Second-
/// precision matches the JWT standard's lower bound (GP 12 rule 6).
/// Shared so every symmetric validator tolerates the same drift.
let clockSkewSeconds = 60L

/// HMAC-SHA256 of `message` under `secret`.
let computeHmac (secret: byte[]) (message: byte[]) : byte[] =
    use hmac = new HMACSHA256(secret)
    hmac.ComputeHash message

/// Constant-time byte-array equality via the BCL primitive. Returns
/// `false` (in constant time w.r.t. length) when the lengths differ, so
/// callers need no separate length pre-check.
let fixedTimeEquals (a: byte[]) (b: byte[]) : bool =
    CryptographicOperations.FixedTimeEquals(ReadOnlySpan a, ReadOnlySpan b)

/// Refuse anything but `alg: HS256` before the signature is touched —
/// defence in depth against algorithm-confusion (`alg: none`, `alg:
/// RS256` against an HS256 secret, `alg: HS512`, …). `header` is the
/// raw (still base64url-encoded) first JWT segment.
let checkHs256Alg (header: string) : Result<unit, string> =
    try
        let bytes = Base64Url.decode header
        let json = Encoding.UTF8.GetString bytes
        use doc = JsonDocument.Parse json

        match doc.RootElement.TryGetProperty("alg") with
        | true, algElem when algElem.ValueKind = JsonValueKind.String ->
            let alg = algElem.GetString()

            if alg = "HS256" then
                Ok()
            else
                Error $"Unsupported JWT algorithm '{alg}' (only HS256 is accepted)"
        | true, _ -> Error "JWT header 'alg' field is not a string"
        | false, _ -> Error "JWT header missing 'alg' field"
    with ex ->
        Error $"Failed to parse JWT header: {ex.Message}"

/// Result computation expression for chaining the synchronous JWT
/// validation steps.
type ResultBuilder() =
    member _.Bind(m, f) =
        match m with
        | Ok x -> f x
        | Error e -> Error e

    member _.Return(x) = Ok x
    member _.ReturnFrom(m) = m
    member _.Zero() = Ok()

let result = ResultBuilder()

#endif