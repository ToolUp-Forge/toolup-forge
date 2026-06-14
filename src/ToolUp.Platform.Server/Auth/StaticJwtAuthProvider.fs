module StaticJwtAuthProvider

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth

// ─── HS256 JWT helpers (BCL only, no package dependencies) ───────
//
// The base64url codec, HMAC, HS256 alg-check, fixed-time compare,
// clock-skew constant and the Result CE are the shared SDK primitives
// in `ToolUp.Platform.Base64Url` / `ToolUp.Platform.JwtCrypto`. Only
// the JWT-shape glue (split, payload decode, claim reads, exp/nbf,
// signature wiring) stays local.

module private Jwt =

    /// Parse a JWT into its three Base64URL-encoded parts.
    let split (token: string) =
        match token.Split('.') with
        | [| header; payload; signature |] -> Ok(header, payload, signature)
        | _ -> Error "Invalid JWT format"

    /// Verify the HS256 signature.
    let verifySignature (secret: byte[]) (header: string) (payload: string) (signature: string) =
        let message = Encoding.UTF8.GetBytes($"{header}.{payload}")
        let expected = JwtCrypto.computeHmac secret message
        let actual = Base64Url.decode signature

        if JwtCrypto.fixedTimeEquals expected actual then
            Ok()
        else
            Error "Invalid signature"

    /// Decode the payload and return it as a JsonDocument.
    let decodePayload (payload: string) =
        try
            let bytes = Base64Url.decode payload
            let json = Encoding.UTF8.GetString(bytes)
            Ok(JsonDocument.Parse(json))
        with ex ->
            Error $"Failed to decode JWT payload: {ex.Message}"

    /// Check the 'exp' claim. A token with no `exp` is rejected —
    /// "no expiry" is never a safe default for a bearer credential
    /// (matches `OidcAuthProvider.validateExpiry`, which returns
    /// `MissingExpiry`).
    let checkExpiry (doc: JsonDocument) =
        match doc.RootElement.TryGetProperty("exp") with
        | true, expElem ->
            let exp = expElem.GetInt64()
            let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

            if exp + JwtCrypto.clockSkewSeconds < now then
                Error "Token expired"
            else
                Ok()
        | false, _ -> Error "Missing 'exp' claim"

    /// Check the optional 'nbf' (not-before) claim. Absent `nbf` is
    /// fine (the claim is optional per RFC 7519); a present `nbf` in
    /// the future rejects the token (matches `OidcAuthProvider`).
    let checkNotBefore (doc: JsonDocument) =
        match doc.RootElement.TryGetProperty("nbf") with
        | true, nbfElem ->
            let nbf = nbfElem.GetInt64()
            let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

            if nbf - JwtCrypto.clockSkewSeconds > now then
                Error "Token not yet valid (nbf)"
            else
                Ok()
        | false, _ -> Ok()

    /// Extract a string claim, returning None if missing.
    let getClaim (name: string) (doc: JsonDocument) =
        match doc.RootElement.TryGetProperty(name) with
        | true, elem when elem.ValueKind = JsonValueKind.String -> Some(elem.GetString())
        | _ -> None

// ─── Configuration ───────────────────────────────────────────────

/// Configuration for the static JWT auth provider.
type StaticJwtConfig = {
    /// HS256 secret key (shared between token issuer and this provider).
    Secret: string
    /// Optional: expected issuer ('iss' claim). Skipped if None.
    Issuer: string option
    /// Optional: expected audience ('aud' claim). Skipped if None.
    Audience: string option
}

// ─── Provider ────────────────────────────────────────────────────

let private extractBearerToken (ctx: HttpContext) =
    match ctx.Request.Headers.TryGetValue("Authorization") with
    | true, values when values.Count > 0 ->
        let header = string values[0]

        if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
            Some(header.Substring(7).Trim())
        else
            None
    | _ -> None

let private validateToken (config: StaticJwtConfig) (token: string) : Result<AuthenticatedUser, string> =
    let secretBytes = Encoding.UTF8.GetBytes(config.Secret)

    JwtCrypto.result {
        let! header, payload, signature = Jwt.split token
        // Gap audit pass-2 #4 — refuse non-HS256 tokens BEFORE running
        // the signature verifier. Defence in depth against algorithm
        // confusion.
        do! JwtCrypto.checkHs256Alg header
        do! Jwt.verifySignature secretBytes header payload signature
        let! doc = Jwt.decodePayload payload
        do! Jwt.checkExpiry doc
        do! Jwt.checkNotBefore doc

        do!
            match config.Issuer with
            | Some expected ->
                match Jwt.getClaim "iss" doc with
                | Some iss when iss = expected -> Ok()
                | Some iss -> Error $"Invalid issuer: {iss}"
                | None -> Error "Missing 'iss' claim"
            | None -> Ok()

        do!
            match config.Audience with
            | Some expected ->
                match Jwt.getClaim "aud" doc with
                | Some aud when aud = expected -> Ok()
                | Some aud -> Error $"Invalid audience: {aud}"
                | None -> Error "Missing 'aud' claim"
            | None -> Ok()

        // Gap audit 2026-06-12 Auth G2 — `sub` is mandatory and
        // sanitised. Pre-hardening, a correctly-signed token with no
        // `sub` authenticated as the literal "anonymous" sentinel
        // (landing the caller in the shared anonymous scope), and the
        // claim flowed verbatim into `UserId` with none of the
        // path-traversal defence the OIDC provider applies. Mirror
        // `OidcAuthProvider`: same sanitiser, but a missing / empty /
        // rejected `sub` is a validation Error, never an anonymous
        // fallback — an "authenticated" caller must carry a real
        // identity. Sanitiser categories never echo the raw value, so
        // the reason is safe in a 401 body.
        let! userId =
            match Jwt.getClaim "sub" doc with
            | None -> Error "Missing 'sub' claim"
            | Some raw ->
                match IdentitySanitiser.sanitiseScopeId raw with
                | Result.Ok v -> Ok v
                | Result.Error reason -> Error $"Invalid 'sub' claim: {reason}"

        return {
            UserId = userId
            DisplayName = Jwt.getClaim "name" doc |> Option.defaultValue userId
            Email = Jwt.getClaim "email" doc
            TenantId = None
            Roles = []
        }
    }

/// HS256 JWT authentication provider.
/// Validates tokens using a static shared secret.
/// Proves the IAuthProvider abstraction is not Clerk-shaped.
type StaticJwtAuthProvider(config: StaticJwtConfig) =
    interface IAuthProvider with
        member _.GetUser(ctx: RequestContext) = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext

            return
                match extractBearerToken httpCtx with
                | Some token ->
                    match validateToken config token with
                    | Ok user -> user
                    | Error _ -> AuthenticatedUser.anonymous
                | None -> AuthenticatedUser.anonymous
        }

        member _.ValidateRequest(ctx: RequestContext) = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext

            return
                match extractBearerToken httpCtx with
                | Some token -> validateToken config token
                | None -> Error "No Bearer token provided"
        }

        // HS256 signature validation against the configured shared
        // secret — identity is cryptographically proven.
        member _.IsCryptographicallyVerified = true