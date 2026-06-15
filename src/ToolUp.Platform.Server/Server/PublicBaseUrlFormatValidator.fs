module ToolUp.Platform.PublicBaseUrlFormatValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Investigate-gaps #8 — PublicBaseUrl format preflight ──────────────
//
// `ServerConfig.PublicBaseUrl` feeds sitemap `<loc>` entries, share-token
// redirect URLs, canonical / `rel=alternate` tags, and IndexNow. A
// relative or otherwise malformed value is accepted silently at compose
// and only surfaces as a broken public URL a crawler rejects or a user
// clicks and fails. The Forms publishable-config validator already
// rejects `None` / whitespace in persistent-auth modes, but nothing
// validates the *format*. This validator requires that, when set,
// `PublicBaseUrl` is a well-formed absolute http(s) URL.
//
// `Warning` (not `Error`): the value is a heuristic and many code paths
// tolerate an unusual-but-functional URL — surface it loudly without
// blocking startup. Unset (`None`) is fine — many deployments don't need
// a public base URL.

/// Warn when `ServerConfig.PublicBaseUrl` is set but is not a well-formed
/// absolute http(s) URL.
type PublicBaseUrlFormatValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "public-base-url-format"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.PublicBaseUrl with
            | None -> return Ok
            | Some raw ->
                let trimmed = raw.Trim()

                let isAbsoluteHttp =
                    Uri.IsWellFormedUriString(trimmed, UriKind.Absolute)
                    && (match Uri.TryCreate(trimmed, UriKind.Absolute) with
                        | true, uri -> uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps
                        | _ -> false)

                if isAbsoluteHttp then
                    return Ok
                else
                    return
                        Warning(
                            sprintf
                                "ServerConfig.PublicBaseUrl = '%s' is not a well-formed absolute http(s) URL. It feeds sitemap <loc> entries, canonical tags, share-token redirect URLs, and IndexNow — a relative or malformed value produces broken public URLs that crawlers reject and users cannot follow. Set an absolute URL like 'https://example.com'."
                                raw
                        )
        }