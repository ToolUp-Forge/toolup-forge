namespace ToolUp.Stripe.TierToken

open System
open Microsoft.AspNetCore.Http

/// Per-consumer cookie configuration. Cookie name and the
/// insecure-cookies env-var name (e.g. `MYAPP_INSECURE_COOKIES`)
/// are declared by the consumer; everything else is canonical.
type CookieConfig = {
    CookieName: string
    /// When `Some`, an env var with this name set to `"1"` drops the
    /// Secure flag on the issued cookie (dev / preview / local). When
    /// `None`, cookies are always Secure.
    ///
    /// Phase 340: the env var is now necessary but no longer sufficient
    /// — the downgrade additionally requires a non-production request
    /// host. See `Cookie.isNonProductionHost`.
    InsecureCookiesEnvVar: string option
}

module Cookie =
    /// Phase 340 — is this request host one on which dropping `Secure`
    /// is a development affordance rather than a credential leak?
    ///
    /// The `InsecureCookiesEnvVar` downgrade exists so a developer can
    /// run over plain HTTP on their own machine. Nothing tied that
    /// intent to where the deployment actually IS: an env var set once
    /// in a shared compose file, a preview stack, or a container image
    /// promoted to production carried the downgrade with it, and a tier
    /// cookie then rode a public network in the clear — silently, since
    /// the cookie still works without `Secure`. This predicate is the
    /// missing half of the gate.
    ///
    /// Non-production means loopback, an explicitly non-routable
    /// development suffix, or a single-label host (a compose / Kubernetes
    /// service name, which cannot be a public FQDN). A port suffix is
    /// stripped first; IPv6 literals arrive bracketed and are matched
    /// bracketed.
    ///
    /// Note the input is the `Host` HEADER, which a client controls. That
    /// is acceptable here and worth stating plainly: forging it can only
    /// weaken the attacker's OWN cookie, on a deployment that already
    /// opted into the env var, and it cannot re-enable the downgrade for
    /// anyone else. The gate is defence against a misplaced env var, not
    /// against a hostile client.
    let isNonProductionHost (host: string | null) : bool =
        match host with
        | null -> false
        | h ->
            let trimmed = h.Trim().ToLowerInvariant()

            let bare =
                if trimmed.StartsWith "[" then
                    // IPv6 literal: keep the brackets, drop any :port.
                    match trimmed.IndexOf ']' with
                    | -1 -> trimmed
                    | i -> trimmed.Substring(0, i + 1)
                else
                    match trimmed.IndexOf ':' with
                    | -1 -> trimmed
                    | i -> trimmed.Substring(0, i)

            if bare = "" then
                false
            else
                bare = "localhost"
                || bare = "[::1]"
                || bare = "::1"
                || bare = "0.0.0.0"
                || bare.StartsWith "127."
                || bare.EndsWith ".localhost"
                || bare.EndsWith ".local"
                || bare.EndsWith ".test"
                || bare.EndsWith ".internal"
                // A single-label host has no public TLD, so it is a
                // container / service name, never an internet-reachable
                // origin.
                || not (bare.Contains ".")

    /// Does the configured insecure-cookie downgrade apply to THIS
    /// request? Both halves must hold: the consumer's env var is `"1"`
    /// AND the request host is non-production. Public so a consumer can
    /// assert its own posture in a test rather than infer it from a
    /// `Set-Cookie` header.
    let insecureDowngradeApplies (config: CookieConfig) (ctx: HttpContext) : bool =
        match config.InsecureCookiesEnvVar with
        | None -> false
        | Some name ->
            match Environment.GetEnvironmentVariable name with
            | "1" -> isNonProductionHost ctx.Request.Host.Value
            | _ -> false

    /// The attribute set shared by `issue` and `clear`. Factored so the
    /// two cannot drift: a clear whose attributes differ from the issue
    /// path is a cookie the browser treats as a DIFFERENT cookie, so the
    /// original survives the signout that was supposed to remove it
    /// (Phase 340 — `clear` previously set only `Expires`, meaning it
    /// emitted a non-`Secure`, non-`HttpOnly`, `SameSite`-unset cookie
    /// and could leave the real one in place).
    let private baseOptions (config: CookieConfig) (ctx: HttpContext) : CookieOptions =
        let opts = CookieOptions()
        opts.HttpOnly <- true
        opts.IsEssential <- true
        opts.SameSite <- SameSiteMode.Lax
        opts.Secure <- not (insecureDowngradeApplies config ctx)
        // Explicit rather than relying on the framework default, so the
        // issue / clear pair is provably identical by reading this file.
        opts.Path <- "/"
        opts

    /// Issue a token cookie on the response. `HttpOnly`, `Secure`
    /// (unless the configured insecure-mode env var is `"1"` AND the
    /// request host is non-production), `SameSite=Lax`, `Path=/`,
    /// lifetime = `lifetimeSeconds`.
    ///
    /// See `Token.RecommendedMaxLifetimeSeconds` for the recommended
    /// ceiling on `lifetimeSeconds` — a legacy token cannot be withdrawn,
    /// so its lifetime is its blast radius.
    let issue
        (config: CookieConfig)
        (ctx: HttpContext)
        (tier: Tier)
        (lifetimeSeconds: int)
        (secret: byte[])
        : Result<unit, MintError> =
        match Token.mint tier lifetimeSeconds DateTimeOffset.UtcNow secret with
        | Error e -> Error e
        | Ok token ->
            let opts = baseOptions config ctx
            opts.MaxAge <- Nullable(TimeSpan.FromSeconds(float lifetimeSeconds))
            ctx.Response.Cookies.Append(config.CookieName, token, opts)
            Ok()

    /// Phase 340 — issue a **revocable** token cookie: the tier is bound
    /// to `subject` and that subject's current `epoch`, so bumping the
    /// epoch server-side withdraws the grant before `exp`. Same cookie
    /// name, same attributes; only the token payload differs.
    ///
    /// Resolve it with `resolveFromRequestWithEpoch` —
    /// `resolveFromRequest` will refuse it (`RevocationCheckRequired`)
    /// rather than grant an unchecked tier.
    let issueFor
        (config: CookieConfig)
        (ctx: HttpContext)
        (tier: Tier)
        (subject: string)
        (epoch: int64)
        (lifetimeSeconds: int)
        (secret: byte[])
        : Result<unit, MintError> =
        match Token.mintFor tier subject epoch lifetimeSeconds DateTimeOffset.UtcNow secret with
        | Error e -> Error e
        | Ok token ->
            let opts = baseOptions config ctx
            opts.MaxAge <- Nullable(TimeSpan.FromSeconds(float lifetimeSeconds))
            ctx.Response.Cookies.Append(config.CookieName, token, opts)
            Ok()

    /// Clear the tier-token cookie (signout / downgrade). Mirrors every
    /// attribute `issue` sets — a browser matches a replacement cookie on
    /// name + domain + path, and rejects or partitions one whose security
    /// attributes disagree, so an unmirrored clear can silently leave the
    /// original cookie live.
    let clear (config: CookieConfig) (ctx: HttpContext) : unit =
        let opts = baseOptions config ctx
        opts.Expires <- Nullable DateTimeOffset.UnixEpoch
        ctx.Response.Cookies.Append(config.CookieName, "", opts)

    /// Inspect the incoming token cookie. `None` = no cookie / bad
    /// token / expired.
    ///
    /// Phase 340: also `None` for a revocable token, which this path
    /// cannot check — see `resolveFromRequestWithEpoch`.
    let resolveFromRequest
        (config: CookieConfig)
        (ctx: HttpContext)
        (now: DateTimeOffset)
        (secret: byte[])
        : Tier option =
        match ctx.Request.Cookies.TryGetValue config.CookieName with
        | true, value when not (String.IsNullOrWhiteSpace value) ->
            match value with
            | null -> None
            | nn ->
                match Token.validate now nn secret with
                | Ok tier -> Some tier
                | Error _ -> None
        | _ -> None

    /// Phase 340 — inspect the incoming token cookie and apply the
    /// revocation check. `None` = no cookie / bad token / expired /
    /// revoked / unknown subject. A legacy three-part cookie resolves
    /// exactly as `resolveFromRequest` resolves it and never reaches the
    /// lookup, so a deployment can switch its resolve path to this
    /// function before it starts minting revocable tokens.
    let resolveFromRequestWithEpoch
        (config: CookieConfig)
        (ctx: HttpContext)
        (now: DateTimeOffset)
        (secret: byte[])
        (currentEpochFor: EpochLookup)
        : Async<Tier option> =
        async {
            match ctx.Request.Cookies.TryGetValue config.CookieName with
            | true, value when not (String.IsNullOrWhiteSpace value) ->
                match value with
                | null -> return None
                | nn ->
                    let! result = Token.validateWithEpoch currentEpochFor now nn secret

                    match result with
                    | Ok tier -> return Some tier
                    | Error _ -> return None
            | _ -> return None
        }