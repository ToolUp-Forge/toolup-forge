module StaticJwtAuthProvider

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform.Auth

// ─── HS256 JWT helpers (BCL only, no package dependencies) ───────

module private Jwt =

    let private base64UrlDecode (input: string) =
        let s = input.Replace('-', '+').Replace('_', '/')

        let s =
            match s.Length % 4 with
            | 2 -> s + "=="
            | 3 -> s + "="
            | _ -> s

        Convert.FromBase64String(s)

    let private computeHmac (secret: byte[]) (message: byte[]) =
        use hmac = new Security.Cryptography.HMACSHA256(secret)
        hmac.ComputeHash(message)

    /// Parse a JWT into its three Base64URL-encoded parts.
    let split (token: string) =
        match token.Split('.') with
        | [| header; payload; signature |] -> Ok(header, payload, signature)
        | _ -> Error "Invalid JWT format"

    /// Gap audit pass-2 #4 — explicit `alg` header check. Decode the
    /// JWT header, parse JSON, refuse anything but `"HS256"`. Defence
    /// in depth against algorithm-confusion attacks (`alg: none`,
    /// `alg: RS256` against an HS256 secret, `alg: HS512` etc.). Today
    /// the length-mismatch in `verifySignature` happens to reject
    /// `alg: none` (signature length 0 ≠ 32-byte HMAC), but that's
    /// incidental — a maintenance change to the comparison logic
    /// could open the bypass. Best practice is to check the header
    /// alg before touching the signature.
    let checkAlgorithm (header: string) =
        try
            let bytes = base64UrlDecode header
            let json = Encoding.UTF8.GetString(bytes)
            use doc = JsonDocument.Parse json

            match doc.RootElement.TryGetProperty("alg") with
            | true, algElem when algElem.ValueKind = JsonValueKind.String ->
                let alg = algElem.GetString()

                if alg = "HS256" then
                    Ok()
                else
                    Error $"Unsupported JWT algorithm '{alg}' (only HS256 is accepted by StaticJwtAuthProvider)"
            | true, _ -> Error "JWT header 'alg' field is not a string"
            | false, _ -> Error "JWT header missing 'alg' field"
        with ex ->
            Error $"Failed to parse JWT header: {ex.Message}"

    /// Verify the HS256 signature.
    let verifySignature (secret: byte[]) (header: string) (payload: string) (signature: string) =
        let message = Encoding.UTF8.GetBytes($"{header}.{payload}")
        let expected = computeHmac secret message
        let actual = base64UrlDecode signature

        if
            expected.Length = actual.Length
            && Security.Cryptography.CryptographicOperations.FixedTimeEquals(ReadOnlySpan expected, ReadOnlySpan actual)
        then
            Ok()
        else
            Error "Invalid signature"

    /// Decode the payload and return it as a JsonDocument.
    let decodePayload (payload: string) =
        try
            let bytes = base64UrlDecode payload
            let json = Encoding.UTF8.GetString(bytes)
            Ok(JsonDocument.Parse(json))
        with ex ->
            Error $"Failed to decode JWT payload: {ex.Message}"

    /// Clock-skew tolerance applied to `exp` and `nbf`. Hard-coded at
    /// 60 seconds — matches `OidcAuthProvider.clockSkewSeconds` so the
    /// two providers validate token lifetime consistently.
    let private clockSkewSeconds = 60L

    /// Check the 'exp' claim. A token with no `exp` is rejected —
    /// "no expiry" is never a safe default for a bearer credential
    /// (matches `OidcAuthProvider.validateExpiry`, which returns
    /// `MissingExpiry`).
    let checkExpiry (doc: JsonDocument) =
        match doc.RootElement.TryGetProperty("exp") with
        | true, expElem ->
            let exp = expElem.GetInt64()
            let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

            if exp + clockSkewSeconds < now then
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

            if nbf - clockSkewSeconds > now then
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

/// Result computation expression for chaining validation steps.
type private ResultBuilder() =
    member _.Bind(m, f) =
        match m with
        | Ok x -> f x
        | Error e -> Error e

    member _.Return(x) = Ok x
    member _.ReturnFrom(m) = m
    member _.Zero() = Ok()

let private result = ResultBuilder()

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

    result {
        let! header, payload, signature = Jwt.split token
        // Gap audit pass-2 #4 — refuse non-HS256 tokens BEFORE running
        // the signature verifier. Defence in depth against algorithm
        // confusion.
        do! Jwt.checkAlgorithm header
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

        let userId = Jwt.getClaim "sub" doc |> Option.defaultValue "anonymous"

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