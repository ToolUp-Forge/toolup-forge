// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GitHubAuthValidator

open System
open System.Net.Http
open System.Threading
open ToolUp.Platform.ConfigValidation

// ─── GitHub API-reachability preflight ───────────────────────────────
//
// Probes the configured GitHub REST base (`{ApiBaseUrl}/meta`) once at
// startup. `/meta` is public + unauthenticated, so the probe only tests
// "can this deployment reach the GitHub API at all?" — a wrong / firewalled
// GHES base URL is caught at deploy instead of surfacing as a 401 on the
// first user sign-in. Mirrors the `OidcAuthValidator` discovery probe.
//
// Companion auto-registration: a deployment that enables GitHub auth
// calls `GitHubAuthValidator.tryFromEnv` at compose time. When
// `TOOLUP_GITHUB_AUTH` is unset/false, `tryFromEnv` returns `None` and no
// validator is registered (GP 13 — deployments without GitHub pay nothing).
// When enabled, wire it in via `ServerApp.withConfigValidator`.

let private httpClient = lazy (new HttpClient())

let private metaUrl (apiBaseUrl: string) : string = apiBaseUrl.TrimEnd('/') + "/meta"

/// Validator implementation. Hidden behind `create` / `tryFromEnv` so the
/// outer module name doesn't shadow the type at call sites.
type private Impl(apiBaseUrl: string, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout
    let url = metaUrl apiBaseUrl

    interface IConfigValidator with
        member _.Name = sprintf "github-auth (%s)" apiBaseUrl
        member _.Timeout = timeout

        member _.Validate() = async {
            use cts = new CancellationTokenSource(timeout)

            try
                use req = new HttpRequestMessage(HttpMethod.Get, url)
                // GitHub rejects requests with no User-Agent even on the
                // public /meta endpoint.
                req.Headers.TryAddWithoutValidation("User-Agent", "ToolUp-Platform-GitHubAuth")
                |> ignore

                let! response = httpClient.Value.SendAsync(req, cts.Token) |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    return Ok
                else
                    return
                        Error(sprintf "GitHub API meta endpoint at %s returned HTTP %d" url (int response.StatusCode))
            with
            | :? OperationCanceledException ->
                return
                    Error(
                        sprintf
                            "GitHub API meta endpoint at %s did not respond within %dms"
                            url
                            (int timeout.TotalMilliseconds)
                    )
            | ex -> return Error(sprintf "GitHub API meta endpoint at %s unreachable: %s" url ex.Message)
        }

/// Construct a validator for an explicit GitHub API base URL. Apps with
/// per-deployment custom config build the URL themselves and call this.
let create (apiBaseUrl: string) : IConfigValidator = Impl(apiBaseUrl) :> IConfigValidator

/// Return a reachability validator when GitHub auth is enabled
/// (`TOOLUP_GITHUB_AUTH` truthy), probing the `TOOLUP_GITHUB_API_BASE_URL`
/// override or the public github.com default. `None` when GitHub auth is
/// not enabled — a deployment that doesn't use it is not punished for
/// referencing the companion.
let tryFromEnv () : IConfigValidator option =
    let enabled =
        match Environment.GetEnvironmentVariable "TOOLUP_GITHUB_AUTH" with
        | null
        | "" -> false
        | raw ->
            match raw.Trim().ToLowerInvariant() with
            | "1"
            | "true"
            | "yes"
            | "on"
            | "enabled" -> true
            | _ -> false

    if not enabled then
        None
    else
        let apiBaseUrl =
            match Environment.GetEnvironmentVariable "TOOLUP_GITHUB_API_BASE_URL" with
            | null
            | "" -> GitHubAuthConfig.GitHubAuthConfig.DefaultApiBaseUrl
            | v -> v

        Some(create apiBaseUrl)