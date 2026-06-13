// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcIdTokenValidator

// ─── Pure id_token parsing + claim validation (Phase 3b.A) ────────────
//
// The Fable-compiled production path runs in the browser and verifies
// the id_token's signature via WebCrypto (`crypto.subtle.verify`). The
// claim-validation portion (iss / aud / exp) and the JWT structural
// parse are pure F# — they live here so the contract tests in
// `ToolUp.Platform.Tests` (which run on .NET, not Fable) can exercise
// them directly, and so the same code services both runtimes.
//
// JSON parsing is the only step that needs a runtime split — Fable
// uses `JS.JSON.parse`, .NET uses `System.Text.Json`. Both produce the
// same `IdTokenClaims` shape downstream. Base64url decoding goes
// through `System.Convert.FromBase64String` (BCL — Fable-compatible
// today and tomorrow, mirroring the existing `readIdTokenNonce`
// `atob`-based path in OidcClient.fs without taking a JS-only
// dependency for the pure validator).

open System
open ToolUp.Platform
open ToolUp.AuthProviders.Oidc.OidcTypes

// ─── Types ───────────────────────────────────────────────────────────

/// Parsed id_token header fields we care about. `Kid` is `option`
/// because the JOSE spec does not require it for a single-key issuer,
/// though every real OIDC IdP we target publishes one.
type IdTokenHeader = {
    Algorithm: string
    Kid: string option
}

/// Parsed id_token payload claims this phase validates. `Audience` is
/// always a list — both string and array `aud` forms parse into it.
type IdTokenClaims = {
    Issuer: string option
    Audience: string list
    ExpiresAt: int64 option
}

/// Full structural break-down of a JWS-shaped id_token, ready for
/// signature verification by the runtime-specific backend (WebCrypto
/// in Fable, RSA/ECDsa in .NET tests).
type ParsedIdToken = {
    Header: IdTokenHeader
    Claims: IdTokenClaims
    /// `header.payload` as raw UTF-8 bytes — the bytes the issuer
    /// signed. Verifiers pass this + `Signature` to their crypto
    /// backend.
    SignedBytes: byte[]
    Signature: byte[]
}

// ─── Base64url ───────────────────────────────────────────────────────

/// Decode a base64url string to bytes. Throws on malformed input;
/// callers wrap in try. Thin re-export of the shared Fable-safe
/// `ToolUp.Platform.Base64Url.decode` — kept public (and named) to
/// support cross-assembly test fixtures that construct / tamper with
/// synthetic JWTs without re-implementing the same byte arithmetic.
let base64UrlDecode (input: string) : byte[] = Base64Url.decode input

// ─── JSON parsing (runtime-split) ─────────────────────────────────────

#if FABLE_COMPILER
open Fable.Core
open Fable.Core.JsInterop

let private parseHeaderJson (json: string) : IdTokenHeader =
    let parsed = JS.JSON.parse json
    let algValue = parsed?alg

    let alg =
        if isNullOrUndefined algValue then
            ""
        else
            unbox<string> algValue

    let kidValue = parsed?kid

    let kid =
        if isNullOrUndefined kidValue then
            None
        else
            Some(unbox<string> kidValue)

    { Algorithm = alg; Kid = kid }

let private parseClaimsJson (json: string) : IdTokenClaims =
    let parsed = JS.JSON.parse json
    let issValue = parsed?iss

    let issuer =
        if isNullOrUndefined issValue then
            None
        else
            Some(unbox<string> issValue)

    let audValue = parsed?aud

    let audience: string list =
        if isNullOrUndefined audValue then
            []
        else
            // `aud` is per RFC 7519 either a string or an array of
            // strings. JS.Array.isArray distinguishes them.
            let isArray: bool = emitJsExpr audValue "Array.isArray($0)"

            if isArray then
                unbox<string[]> audValue |> List.ofArray
            else
                [ unbox<string> audValue ]

    let expValue = parsed?exp

    let expiresAt =
        if isNullOrUndefined expValue then
            None
        else
            // JWT `exp` is numeric — unbox to float then to int64 to
            // tolerate either int or float serialisation.
            Some(int64 (unbox<float> expValue))

    {
        Issuer = issuer
        Audience = audience
        ExpiresAt = expiresAt
    }
#else
open System.Text.Json

let private parseHeaderJson (json: string) : IdTokenHeader =
    use doc = JsonDocument.Parse json
    let root = doc.RootElement

    let alg =
        match root.TryGetProperty "alg" with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""

    let kid =
        match root.TryGetProperty "kid" with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    { Algorithm = alg; Kid = kid }

let private parseClaimsJson (json: string) : IdTokenClaims =
    use doc = JsonDocument.Parse json
    let root = doc.RootElement

    let issuer =
        match root.TryGetProperty "iss" with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let audience =
        match root.TryGetProperty "aud" with
        | true, v when v.ValueKind = JsonValueKind.String -> [ v.GetString() ]
        | true, v when v.ValueKind = JsonValueKind.Array ->
            v.EnumerateArray()
            |> Seq.choose (fun item ->
                if item.ValueKind = JsonValueKind.String then
                    Some(item.GetString())
                else
                    None)
            |> List.ofSeq
        | _ -> []

    let expiresAt =
        match root.TryGetProperty "exp" with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt64() with
            | true, n -> Some n
            | _ -> None
        | _ -> None

    {
        Issuer = issuer
        Audience = audience
        ExpiresAt = expiresAt
    }
#endif

// ─── Parse the id_token ──────────────────────────────────────────────

/// Break the id_token into its JOSE segments, base64url-decode them,
/// and parse the JSON header + payload. `Error MalformedIdToken` on
/// any structural defect (not three segments, bad base64, bad JSON).
let parseIdToken (idToken: string) : Result<ParsedIdToken, AuthError> =
    try
        if String.IsNullOrEmpty idToken then
            Error MalformedIdToken
        else
            let parts = idToken.Split('.')

            if parts.Length <> 3 then
                Error MalformedIdToken
            else
                let headerBytes = base64UrlDecode parts[0]
                let payloadBytes = base64UrlDecode parts[1]
                let signature = base64UrlDecode parts[2]
                let headerJson = System.Text.Encoding.UTF8.GetString headerBytes
                let payloadJson = System.Text.Encoding.UTF8.GetString payloadBytes
                let header = parseHeaderJson headerJson
                let claims = parseClaimsJson payloadJson
                // The signed bytes are `header.payload` (the base64url
                // strings joined by '.', as ASCII), NOT the decoded JSON.
                let signedString = parts[0] + "." + parts[1]
                let signedBytes = System.Text.Encoding.UTF8.GetBytes signedString

                Ok {
                    Header = header
                    Claims = claims
                    SignedBytes = signedBytes
                    Signature = signature
                }
    with _ ->
        Error MalformedIdToken

// ─── Claim validators ────────────────────────────────────────────────

/// SDK default clock-skew tolerance applied to `exp`. 60 seconds is
/// standard practice and matches the server-side
/// `OidcAuthProvider.defaultClockSkewSeconds`.
[<Literal>]
let defaultClockSkewSeconds = 60L

let validateIssuer (expected: string) (claims: IdTokenClaims) : Result<unit, AuthError> =
    match claims.Issuer with
    | Some i when i = expected -> Ok()
    | _ -> Error IdTokenIssuerInvalid

let validateAudience (expected: string) (claims: IdTokenClaims) : Result<unit, AuthError> =
    if claims.Audience |> List.contains expected then
        Ok()
    else
        Error IdTokenAudienceInvalid

let validateExpiry (skewSeconds: int64) (nowEpoch: int64) (claims: IdTokenClaims) : Result<unit, AuthError> =
    match claims.ExpiresAt with
    | Some exp when exp + skewSeconds >= nowEpoch -> Ok()
    | _ -> Error IdTokenExpired

/// Run the three claim validators in order: issuer → audience →
/// expiry. The first failure short-circuits.
let validateClaims
    (expectedIssuer: string)
    (expectedAudience: string)
    (skewSeconds: int64)
    (nowEpoch: int64)
    (claims: IdTokenClaims)
    : Result<unit, AuthError> =
    validateIssuer expectedIssuer claims
    |> Result.bind (fun () -> validateAudience expectedAudience claims)
    |> Result.bind (fun () -> validateExpiry skewSeconds nowEpoch claims)