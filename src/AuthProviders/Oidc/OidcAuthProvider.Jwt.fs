module internal ToolUp.AuthProviders.OidcAuthProviderJwt

open System
open System.Text
open System.Text.Json
open ToolUp.Platform

// ─── Base64url ───────────────────────────────────────────────────────
//
// JWT header, payload, signature, and JWK `n`/`e` are base64url. Differs
// from standard base64: `+` → `-`, `/` → `_`, trailing `=` padding
// omitted.

let base64UrlDecode (s: string) : byte[] =
    let padded =
        match s.Length % 4 with
        | 0 -> s
        | r -> s + String.replicate (4 - r) "="

    let standard = padded.Replace('-', '+').Replace('_', '/')
    Convert.FromBase64String standard

let private base64UrlDecodeString (s: string) : string =
    Encoding.UTF8.GetString(base64UrlDecode s)

// ─── JSON helpers ────────────────────────────────────────────────────

let tryGetString (name: string) (el: JsonElement) : string option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private tryGetInt64 (name: string) (el: JsonElement) : int64 option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt64() with
        | true, n -> Some n
        | _ -> None
    | _ -> None

// ─── Validation errors ───────────────────────────────────────────────

/// Classification of a token-validation failure. `ValidateRequest`
/// returns these stringified; `GetUser` maps any failure to
/// `AuthenticatedUser.anonymous`.
type JwtValidationError =
    | NoToken
    | MalformedToken of detail: string
    | UnsupportedAlgorithm of alg: string
    | UnknownKid of kid: string
    | InvalidSignature
    | InvalidIssuer of expected: string * actual: string
    | InvalidAudience of expected: string * actual: string
    | TokenExpired
    | TokenNotYetValid
    | MissingExpiry
    | JwksUnavailable of reason: string

module JwtValidationError =
    let toMessage =
        function
        | NoToken -> "No token in request"
        | MalformedToken d -> $"Malformed token: {d}"
        | UnsupportedAlgorithm a ->
            $"Unsupported algorithm: {a} (not in AuthConfig.AcceptedAlgorithms; default trust set is [RS256])"
        | UnknownKid k -> $"Token signed with unknown key id: {k}"
        | InvalidSignature -> "Token signature is invalid"
        | InvalidIssuer(e, a) -> $"Token issuer mismatch: expected {e}, got {a}"
        | InvalidAudience(e, a) -> $"Token audience mismatch: expected {e}, got {a}"
        | TokenExpired -> "Token is expired"
        | TokenNotYetValid -> "Token is not yet valid"
        | MissingExpiry -> "Token has no exp claim"
        | JwksUnavailable r -> $"JWKS unavailable: {r}"

// ─── JWT parsing ─────────────────────────────────────────────────────

type JwtHeader = {
    Algorithm: JwsAlgorithm
    KeyId: string option
}

/// Claims extracted from the payload. `JsonDocument` is disposed
/// immediately after parse — everything downstream needs is materialised
/// here.
type JwtPayload = {
    Issuer: string option
    /// `aud` claim as a list. Single-string audience becomes a one-
    /// element list; array audience is preserved. Empty when claim
    /// is absent.
    Audience: string list
    Subject: string option
    /// 0.5.4 — Microsoft Entra `oid` (object id) claim, present on
    /// every v2 endpoint token from `login.microsoftonline.com`. The
    /// `oid` is a tenant-wide stable identifier for the user across
    /// every app registration in the tenant, where `sub` is a
    /// PAIRWISE PSEUDONYMOUS identifier specific to the relying
    /// party. Consumers identifying users for cross-app artefacts
    /// (admin lists, RBAC entries, audit records) want `oid` — `sub`
    /// rotates per app and breaks identity continuity. The provider's
    /// `userFromPayload` prefers `oid` over `sub` when
    /// `AuthConfig.PreferOidWhenPresent = true` (auto-enabled by
    /// `AuthProvider.fromEnv` for `login.microsoftonline.com`
    /// issuers); the field is `None` and ignored on non-Microsoft IdPs.
    Oid: string option
    Name: string option
    Email: string option
    ExpiresAt: int64 option
    NotBefore: int64 option
    /// Well-known role/group claim names present on the token that the
    /// provider does NOT map into `AuthenticatedUser.Roles` (the SDK
    /// permission model is team-membership driven; a configurable
    /// claim-mapper is a tracked roadmap item). Detection only — never
    /// interpreted. Drives a one-time discoverability warning so a
    /// brownfield IdP migration notices the dropped claims.
    UnmappedRoleClaims: string list
}

type ParsedJwt = {
    Header: JwtHeader
    Payload: JwtPayload
    /// ASCII bytes of `{header}.{payload}` — what the signature covers.
    SignedBytes: byte[]
    Signature: byte[]
}

let private parseAudience (el: JsonElement) : string list =
    match el.ValueKind with
    | JsonValueKind.String -> [ el.GetString() ]
    | JsonValueKind.Array ->
        el.EnumerateArray()
        |> Seq.choose (fun v ->
            if v.ValueKind = JsonValueKind.String then
                Some(v.GetString())
            else
                None)
        |> List.ofSeq
    | _ -> []

let parseJwt (raw: string) : Result<ParsedJwt, JwtValidationError> =
    try
        let parts = raw.Split('.')

        if parts.Length <> 3 then
            Error(MalformedToken "expected three base64url-encoded segments separated by '.'")
        else
            let signedBytes = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1])
            let signature = base64UrlDecode parts[2]

            use headerDoc = JsonDocument.Parse(base64UrlDecodeString parts[0])

            let rawAlg = tryGetString "alg" headerDoc.RootElement |> Option.defaultValue ""

            // The provider only recognises the registered JWS algorithm
            // set (RS256/RS384/RS512/ES256/PS256). An unrecognised `alg`
            // — including HS256, which belongs to `StaticJwtAuthProvider`
            // because OIDC's trust model forbids sharing symmetric
            // secrets with browser-side parties — short-circuits to
            // `UnsupportedAlgorithm` with the raw string preserved for
            // the failure log.
            match JwsAlgorithm.tryParse rawAlg with
            | None -> Error(UnsupportedAlgorithm rawAlg)
            | Some alg ->
                let header = {
                    Algorithm = alg
                    KeyId = tryGetString "kid" headerDoc.RootElement
                }

                use payloadDoc = JsonDocument.Parse(base64UrlDecodeString parts[1])
                let root = payloadDoc.RootElement

                let audience =
                    match root.TryGetProperty "aud" with
                    | true, v -> parseAudience v
                    | _ -> []

                let unmappedRoleClaims =
                    [ "roles"; "groups"; "realm_access"; "resource_access" ]
                    |> List.filter (fun name -> root.TryGetProperty name |> fst)

                let payload = {
                    Issuer = tryGetString "iss" root
                    Audience = audience
                    Subject = tryGetString "sub" root
                    Oid = tryGetString "oid" root
                    Name = tryGetString "name" root
                    Email = tryGetString "email" root
                    ExpiresAt = tryGetInt64 "exp" root
                    NotBefore = tryGetInt64 "nbf" root
                    UnmappedRoleClaims = unmappedRoleClaims
                }

                Ok {
                    Header = header
                    Payload = payload
                    SignedBytes = signedBytes
                    Signature = signature
                }
    with ex ->
        Error(MalformedToken ex.Message)