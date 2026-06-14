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
    InsecureCookiesEnvVar: string option
}

module Cookie =
    let private isInsecureMode (config: CookieConfig) : bool =
        match config.InsecureCookiesEnvVar with
        | None -> false
        | Some name ->
            match Environment.GetEnvironmentVariable name with
            | "1" -> true
            | _ -> false

    /// Issue a token cookie on the response. `HttpOnly`, `Secure`
    /// (unless the configured insecure-mode env var is `"1"`),
    /// `SameSite=Lax`, lifetime = `lifetimeSeconds`.
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
            let opts = CookieOptions()
            opts.HttpOnly <- true
            opts.IsEssential <- true
            opts.SameSite <- SameSiteMode.Lax
            opts.Secure <- not (isInsecureMode config)
            opts.MaxAge <- Nullable(TimeSpan.FromSeconds(float lifetimeSeconds))
            ctx.Response.Cookies.Append(config.CookieName, token, opts)
            Ok()

    /// Clear the tier-token cookie (signout / downgrade).
    let clear (config: CookieConfig) (ctx: HttpContext) : unit =
        let opts = CookieOptions()
        opts.Expires <- Nullable(DateTimeOffset.UtcNow.AddDays(-1.0))
        ctx.Response.Cookies.Append(config.CookieName, "", opts)

    /// Inspect the incoming token cookie. `None` = no cookie / bad
    /// token / expired.
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