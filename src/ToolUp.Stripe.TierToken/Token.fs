namespace ToolUp.Stripe.TierToken

open System
open System.Security.Cryptography
open System.Text

/// Token mint / validate errors.
///
/// New cases are appended at the END of the DU — never inserted — so the
/// compiler-assigned tag of every pre-existing case is unchanged.
type MintError =
    /// Caller passed a zero-or-negative `lifetimeSeconds`.
    | InvalidLifetime
    /// Signing secret was empty or below the 32-byte minimum strength —
    /// too weak to sign a token with. Fails closed.
    | SecretMissing
    /// Phase 340 — `mintFor` was handed an empty / whitespace subject.
    /// A revocable token must name whose epoch to check; a blank subject
    /// would produce a token no revocation lookup could ever match.
    | InvalidSubject
    /// Phase 340 — `mintFor` was handed a negative epoch. Epochs are
    /// monotonic non-negative counters.
    | InvalidEpoch

/// Token validate errors.
///
/// New cases are appended at the END of the DU — see `MintError`.
type ValidateError =
    /// Signing secret was empty or below the 32-byte minimum strength —
    /// too weak to verify against. Fails closed (never returns a tier).
    | SecretMissing
    /// Token didn't parse to a known dot-separated shape (three parts
    /// for a legacy token, five for a revocable one), or a field the
    /// signature covered did not decode.
    | MalformedToken
    /// HMAC signature didn't match. Constant-time compared.
    | SignatureMismatch
    /// Token expired (now > exp).
    | Expired
    /// Tier claim didn't parse to a known case.
    | UnknownTier
    /// Phase 340 — a revocable (epoch-bearing) token was handed to the
    /// revocation-UNAWARE `Token.validate`. Fails closed rather than
    /// silently granting the tier: the whole point of stamping an epoch
    /// is that somebody checks it, and a caller that cannot check it must
    /// not be the one to decide the token is good.
    | RevocationCheckRequired
    /// Phase 340 — the token's stamped epoch is below the subject's
    /// current server-side epoch: the grant was revoked (cancellation,
    /// chargeback, credential rotation after a leak) before `exp`.
    | Revoked
    /// Phase 340 — the revocation lookup did not recognise the token's
    /// subject. Fails closed: an unknown subject is indistinguishable
    /// from a deleted one, and a deleted user must not keep a paid tier.
    | SubjectUnknown

/// Phase 340 — everything a validated token asserts, before any
/// revocation decision is applied. `Subject` / `Epoch` are `None` for a
/// legacy three-part token and `Some` for a revocable five-part one.
type TokenClaims = {
    Tier: Tier
    Subject: string option
    Epoch: int64 option
    ExpiresAt: DateTimeOffset
}

/// Phase 340 — the server-side half of revocation: given a token's
/// subject, the subject's CURRENT epoch. `None` means "no such subject",
/// which fails closed.
///
/// A function rather than an interface on purpose. This package depends
/// on FSharp.Core and `Microsoft.AspNetCore.Http` and nothing else (GP 1);
/// an interface would either drag in a store abstraction or invent one
/// here, and every real implementation is a one-line read against a
/// database or blob the consumer already owns. `Async` because that read
/// is genuinely remote — the consumer bumps the epoch on a subscription
/// cancellation, a chargeback, or a "sign out everywhere".
type EpochLookup = string -> Async<int64 option>

/// Signed-token format. Two shapes, distinguished by part count:
///
///   * **Legacy (v1, three parts)** — `{tierClaim}.{expUnix}.{sig}`,
///     `sig = HMAC-SHA256("{tierClaim}.{expUnix}", secret)`. Stateless:
///     the only bound on a leaked or superseded token is `exp`.
///   * **Revocable (v2, five parts — Phase 340)** —
///     `{tierClaim}.{expUnix}.{subjectB64Url}.{epoch}.{sig}`,
///     `sig = HMAC-SHA256("{tierClaim}.{expUnix}.{subjectB64Url}.{epoch}", secret)`.
///     The subject is base64-url encoded so a subject containing a `.`
///     (an email address, a namespaced id) cannot shift the field
///     boundaries, and both new fields are inside the signed payload, so
///     neither can be edited without invalidating the signature.
///
/// Revocation is a **per-subject epoch**: the deployment stores one
/// monotonic counter per subject and bumps it whenever every outstanding
/// grant for that subject must stop working — cancellation, chargeback,
/// a leaked cookie, a "sign out everywhere". A token stamped below the
/// current epoch is refused on the next request, whatever its `exp` says.
/// The epoch, not the token, is the authority (GP 4).
///
/// Both shapes are minted and verified with the same secret and the same
/// constant-time compare; a deployment that never calls `mintFor` is
/// byte-for-byte unchanged (GP 11).
///
/// Why this rather than full JWT: zero extra NuGet dependencies +
/// same security guarantee for the single-issuer / single-audience
/// case that a typical paid-tier deployment runs. When a
/// federation provider lands, swap the signer for a JWT validator
/// without touching the cookie name or claim shape.
module Token =
    /// Recommended ceiling on a tier-cookie lifetime, in seconds (24h).
    ///
    /// This is guidance a consumer chooses to honour, not a limit `mint`
    /// enforces — clamping an existing deployment's configured lifetime
    /// would silently change its session behaviour on upgrade (GP 11).
    /// The number is the blast-radius argument made concrete: a LEGACY
    /// token cannot be withdrawn at all, so its lifetime IS the window in
    /// which a cancelled, charged-back or leaked grant keeps working, and
    /// beyond about a day that window stops being a session and starts
    /// being a licence. A REVOCABLE token (`mintFor`) is bounded by the
    /// epoch bump instead, so it can safely run longer — the ceiling then
    /// governs only how long a subject keeps a tier the deployment
    /// silently downgraded without bumping anything.
    [<Literal>]
    let RecommendedMaxLifetimeSeconds = 86400

    /// Minimum signing-secret length, in bytes. An HMAC-SHA256 key below
    /// the 32-byte SHA-256 block offers less than the full keyspace, and an
    /// empty key is publicly computable. Mirrored — not shared — with the
    /// identical guard in `ToolUp.Stripe.Webhook.WebhookSigner` and the peer
    /// auth provider; the three signing packages are deliberately decoupled
    /// (same rationale as the constant-time compare duplicated across them).
    [<Literal>]
    let private MinSecretBytes = 32

    /// `true` when the secret meets the minimum strength. An empty OR
    /// too-short key fails closed (GP 4); a well-formed ≥32-byte key is
    /// unchanged (GP 11).
    let private secretIsStrong (secret: byte[]) : bool = secret.Length >= MinSecretBytes

    /// Base64-URL encoding (RFC 4648 §5) without padding.
    let private base64UrlEncode (bytes: byte[]) : string =
        let standard = Convert.ToBase64String bytes
        standard.Replace('+', '-').Replace('/', '_').TrimEnd('=')

    /// Inverse of `base64UrlEncode`, for the Phase-340 subject field.
    /// Returns `None` on anything that is not a well-formed padding-less
    /// base64-url string — the caller renders that as `MalformedToken`,
    /// never as an empty subject (which would silently match nothing).
    let private base64UrlDecode (s: string) : string option =
        let restored = s.Replace('-', '+').Replace('_', '/')

        let padded =
            match restored.Length % 4 with
            | 0 -> Some restored
            | 2 -> Some(restored + "==")
            | 3 -> Some(restored + "=")
            // A length ≡ 1 (mod 4) cannot be base64 of any byte string.
            | _ -> None

        match padded with
        | None -> None
        | Some p ->
            try
                Some(Encoding.UTF8.GetString(Convert.FromBase64String p))
            with _ ->
                None

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
                diff <- diff ||| (int a[i] ^^^ int b[i])

            diff = 0

    /// Mint a signed token for the given tier + lifetime (in seconds).
    let mint (tier: Tier) (lifetimeSeconds: int) (now: DateTimeOffset) (secret: byte[]) : Result<string, MintError> =
        if not (secretIsStrong secret) then
            Error MintError.SecretMissing
        elif lifetimeSeconds <= 0 then
            Error InvalidLifetime
        else
            let claim = Tier.toClaim tier
            let exp = now.ToUnixTimeSeconds() + int64 lifetimeSeconds
            let payload = sprintf "%s.%d" claim exp
            let sig' = hmac secret payload |> base64UrlEncode
            Ok(sprintf "%s.%s" payload sig')

    /// Phase 340 — mint a **revocable** token binding the tier to a
    /// subject and that subject's current epoch. Same secret, same
    /// signature algorithm, same constant-time verification as `mint`;
    /// what it adds is a server-checkable handle, so the grant can be
    /// withdrawn before `exp` by bumping the subject's epoch.
    ///
    /// Verify it with `validateWithEpoch` (or
    /// `Cookie.resolveFromRequestWithEpoch`). Handing it to the
    /// revocation-unaware `validate` yields `RevocationCheckRequired`,
    /// never a tier.
    let mintFor
        (tier: Tier)
        (subject: string)
        (epoch: int64)
        (lifetimeSeconds: int)
        (now: DateTimeOffset)
        (secret: byte[])
        : Result<string, MintError> =
        if not (secretIsStrong secret) then
            Error MintError.SecretMissing
        elif lifetimeSeconds <= 0 then
            Error InvalidLifetime
        elif String.IsNullOrWhiteSpace subject then
            Error InvalidSubject
        elif epoch < 0L then
            Error InvalidEpoch
        else
            let claim = Tier.toClaim tier
            let exp = now.ToUnixTimeSeconds() + int64 lifetimeSeconds
            let subjectEncoded = base64UrlEncode (Encoding.UTF8.GetBytes subject)
            let payload = sprintf "%s.%d.%s.%d" claim exp subjectEncoded epoch
            let sig' = hmac secret payload |> base64UrlEncode
            Ok(sprintf "%s.%s" payload sig')

    /// Phase 340 — verify a token's signature, shape and expiry and
    /// return everything it claims, WITHOUT applying any revocation
    /// decision. The shared core of `validate` / `validateWithEpoch`, and
    /// the entry point for a consumer that wants to route on the subject
    /// itself (audit, "which account is this cookie for") rather than
    /// merely resolve a tier.
    ///
    /// Ordering is signature → expiry → tier, unchanged from Phase 141:
    /// nothing a token asserts is read as data until the HMAC says the
    /// deployment wrote it.
    let inspect (now: DateTimeOffset) (token: string) (secret: byte[]) : Result<TokenClaims, ValidateError> =
        if not (secretIsStrong secret) then
            Error ValidateError.SecretMissing
        else
            let parts = token.Split('.')

            let split =
                match parts.Length with
                | 3 -> Ok(parts[0], parts[1], (None: string option), (None: string option), parts[2])
                | 5 -> Ok(parts[0], parts[1], Some parts[2], Some parts[3], parts[4])
                | _ -> Error MalformedToken

            match split with
            | Error e -> Error e
            | Ok(tierStr, expStr, subjectRaw, epochRaw, providedSig) ->
                let payload =
                    match subjectRaw, epochRaw with
                    | Some s, Some e -> sprintf "%s.%s.%s.%s" tierStr expStr s e
                    | _ -> sprintf "%s.%s" tierStr expStr

                let expected = hmac secret payload |> base64UrlEncode

                let sigOk =
                    constantTimeEquals (Encoding.UTF8.GetBytes providedSig) (Encoding.UTF8.GetBytes expected)

                if not sigOk then
                    Error SignatureMismatch
                else
                    match Int64.TryParse expStr with
                    | false, _ -> Error MalformedToken
                    | true, exp when exp <= now.ToUnixTimeSeconds() -> Error Expired
                    | true, exp ->
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
                        | "enterprise" ->
                            let subject =
                                subjectRaw
                                |> Option.bind base64UrlDecode
                                |> Option.filter (String.IsNullOrWhiteSpace >> not)

                            let epoch =
                                epochRaw
                                |> Option.bind (fun e ->
                                    match Int64.TryParse e with
                                    | true, v when v >= 0L -> Some v
                                    | _ -> None)

                            // The signature already covered these fields,
                            // so a decode failure here is a shape mismatch
                            // (a v2 token minted by something else), not
                            // tampering. Report it as malformed rather than
                            // degrading to a legacy token — degrading is
                            // exactly how an epoch check gets skipped.
                            match subjectRaw, subject, epoch with
                            | Some _, None, _
                            | Some _, _, None -> Error MalformedToken
                            | _ ->
                                Ok {
                                    Tier = Tier.tryParse tierStr
                                    Subject = subject
                                    Epoch = epoch
                                    ExpiresAt = DateTimeOffset.FromUnixTimeSeconds exp
                                }
                        | _ -> Error UnknownTier

    /// Validate a token string. Returns `Ok Tier` on success; `Error`
    /// on bad format, bad signature, or expired. Caller falls back to
    /// `Tier.Anonymous` on `Error`.
    ///
    /// Phase 340: a **revocable** token reaches this function only when a
    /// deployment minted with `mintFor` and then verified with a code
    /// path that cannot check epochs. That is a wiring mistake, so it
    /// fails closed with `RevocationCheckRequired` rather than granting a
    /// tier nobody confirmed is still live. Legacy three-part tokens are
    /// unaffected — same inputs, same outputs, same errors as before.
    let validate (now: DateTimeOffset) (token: string) (secret: byte[]) : Result<Tier, ValidateError> =
        match inspect now token secret with
        | Error e -> Error e
        | Ok claims ->
            match claims.Epoch with
            | Some _ -> Error RevocationCheckRequired
            | None -> Ok claims.Tier

    /// Phase 340 — validate a token and apply the revocation check.
    ///
    /// A revocable token resolves its tier only when the subject's
    /// current epoch is at or below the epoch stamped into the signature;
    /// a bumped epoch yields `Revoked` and an unrecognised subject yields
    /// `SubjectUnknown`, both before `exp`. A legacy three-part token
    /// carries no subject to look up, so it passes through with its tier
    /// and the lookup is never called — an upgrading deployment can move
    /// its resolve path here first and start minting revocable tokens
    /// afterwards, with no flag day.
    let validateWithEpoch
        (currentEpochFor: EpochLookup)
        (now: DateTimeOffset)
        (token: string)
        (secret: byte[])
        : Async<Result<Tier, ValidateError>> =
        async {
            match inspect now token secret with
            | Error e -> return Error e
            | Ok claims ->
                match claims.Subject, claims.Epoch with
                | Some subject, Some stamped ->
                    let! current = currentEpochFor subject

                    match current with
                    | None -> return Error SubjectUnknown
                    | Some c when stamped < c -> return Error Revoked
                    | Some _ -> return Ok claims.Tier
                | _ -> return Ok claims.Tier
        }