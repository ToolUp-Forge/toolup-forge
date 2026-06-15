namespace ToolUp.Stripe.TierToken

open System
open System.Security.Cryptography
open System.Text

/// Token mint / validate errors.
type MintError =
    /// Caller passed a zero-or-negative `lifetimeSeconds`.
    | InvalidLifetime
    /// Caller passed an empty `secret` byte array.
    | SecretMissing

/// Token validate errors.
type ValidateError =
    /// Caller passed an empty `secret` byte array.
    | SecretMissing
    /// Token didn't parse to three dot-separated parts.
    | MalformedToken
    /// HMAC signature didn't match. Constant-time compared.
    | SignatureMismatch
    /// Token expired (now > exp).
    | Expired
    /// Tier claim didn't parse to a known case.
    | UnknownTier

/// Signed-token format. The cookie payload is
/// `{tierClaim}.{expUnix}.{sigBase64Url}` where
/// `sig = HMAC-SHA256(payload="{tierClaim}.{expUnix}", key=secret)`,
/// encoded with base64-url (no padding).
///
/// Why this rather than full JWT: zero extra NuGet dependencies +
/// same security guarantee for the single-issuer / single-audience
/// case that a typical paid-tier deployment runs. When a
/// federation provider lands, swap the signer for a JWT validator
/// without touching the cookie name or claim shape.
module Token =
    /// Base64-URL encoding (RFC 4648 §5) without padding.
    let private base64UrlEncode (bytes: byte[]) : string =
        let standard = Convert.ToBase64String bytes
        standard.Replace('+', '-').Replace('/', '_').TrimEnd('=')

    let private hmac (secret: byte[]) (payload: string) : byte[] =
        use h = new HMACSHA256(secret)
        h.ComputeHash(Encoding.UTF8.GetBytes payload)

    /// Constant-time byte comparison so an attacker can't time-side-
    /// channel the secret bit-by-bit.
    let private constantTimeEquals (a: byte[]) (b: byte[]) : bool =
        if a.Length <> b.Length then
            false
        else
            let mutable diff = 0

            for i in 0 .. a.Length - 1 do
                diff <- diff ||| (int a.[i] ^^^ int b.[i])

            diff = 0

    /// Mint a signed token for the given tier + lifetime (in seconds).
    let mint (tier: Tier) (lifetimeSeconds: int) (now: DateTimeOffset) (secret: byte[]) : Result<string, MintError> =
        if secret.Length = 0 then
            Error MintError.SecretMissing
        elif lifetimeSeconds <= 0 then
            Error InvalidLifetime
        else
            let claim = Tier.toClaim tier
            let exp = now.ToUnixTimeSeconds() + int64 lifetimeSeconds
            let payload = sprintf "%s.%d" claim exp
            let sig' = hmac secret payload |> base64UrlEncode
            Ok(sprintf "%s.%s" payload sig')

    /// Validate a token string. Returns `Ok Tier` on success; `Error`
    /// on bad format, bad signature, or expired. Caller falls back to
    /// `Tier.Anonymous` on `Error`.
    let validate (now: DateTimeOffset) (token: string) (secret: byte[]) : Result<Tier, ValidateError> =
        if secret.Length = 0 then
            Error ValidateError.SecretMissing
        else
            let parts = token.Split('.')

            if parts.Length <> 3 then
                Error MalformedToken
            else
                let tierStr = parts.[0]
                let expStr = parts.[1]
                let providedSig = parts.[2]
                let payload = sprintf "%s.%s" tierStr expStr
                let expected = hmac secret payload |> base64UrlEncode

                let sigOk =
                    constantTimeEquals (Encoding.UTF8.GetBytes providedSig) (Encoding.UTF8.GetBytes expected)

                if not sigOk then
                    Error SignatureMismatch
                else
                    match Int64.TryParse expStr with
                    | false, _ -> Error MalformedToken
                    | true, exp when exp <= now.ToUnixTimeSeconds() -> Error Expired
                    | true, _ ->
                        // tryParse maps the unknown / empty-string case to
                        // Anonymous (safe default). For validation we
                        // surface that distinction so callers can log
                        // "tier claim drift" if their schema changes.
                        match tierStr.Trim().ToLowerInvariant() with
                        | "anonymous"
                        | "free"
                        | "personal"
                        | "teacher"
                        | "pro"
                        | "enterprise" -> Ok(Tier.tryParse tierStr)
                        | _ -> Error UnknownTier