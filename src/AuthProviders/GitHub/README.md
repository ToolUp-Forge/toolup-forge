# ToolUp.AuthProviders.GitHub

GitHub OAuth server-side `IAuthProvider` for ToolUp.Platform.

GitHub OAuth Apps mint an **opaque** access token (`gho_…`), not an OIDC
`id_token` — there is no local signature to verify. This provider establishes
identity by calling GitHub's authoritative REST API (`GET /user`) with the
presented bearer: a token GitHub did not mint fails that call, so identity is
proven remotely rather than trusted from a spoofable header. A short-TTL cache
keeps the round-trip off the per-request hot path.

Because OAuth Apps have no PKCE, the code→token exchange must run server-side —
so this single server package carries **both** the token validator and the
sign-in leg (authorize-URL + code exchange), unlike the OIDC stack which splits
into a server validator + a browser client package.

## What's in the box

| Module | Purpose |
|---|---|
| `GitHubAuthProvider` | The `IAuthProvider` — validates an inbound GitHub bearer, applies optional org allow-listing, caches the validated identity. |
| `GitHubAuthConfig` | Declarative config + `TOOLUP_GITHUB_*` env reader. |
| `GitHubOAuth` | The sign-in leg: `buildAuthorizeUrl` (pure) + `exchangeCode` (reads the client secret from `ISecretStore`). |
| `GitHubApi` | Narrow BCL-`HttpClient` facade over the GitHub REST calls used (`/user`, `/user/emails`, `/user/memberships/orgs/{org}`). |
| `GitHubAuthValidator` | Optional startup preflight probing GitHub API reachability. |

No `Octokit` / vendor SDK dependency — BCL `HttpClient` + `System.Text.Json` only.

## Server: validate inbound tokens

```fsharp
open ToolUp.Platform
open ToolUp.AuthProviders
open ToolUp.AuthProviders.GitHubAuthConfig

let authProvider =
    GitHubAuthProvider.fromConfig
        (Some logger)
        { GitHubAuthConfig.defaults with
            AllowedOrgs = [ "my-org" ]   // empty ⇒ any GitHub user is admitted
            FetchPrimaryEmail = true }   // second call to /user/emails when public email is absent

ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with Mode = Authenticated }
|> ServerApp.withAuth authProvider
|> ServerApp.run
```

Constructor variants (mirroring the OIDC provider):

- `fromConfig logger config` — production, process-wide shared `HttpClient`.
- `fromConfigWith httpClient logger config` — inject a custom / stub `HttpClient` (tests).
- `fromConfigMetered logger metrics config` — emit `toolup.auth.validate.*` counters tagged `provider=github`.
- `fromConfigWithMetrics httpClient logger metrics config` — both of the above.
- `fromEnv logger` / `fromEnvMetered logger metrics` — gated on `TOOLUP_GITHUB_AUTH`; returns `IAuthProvider option`.

### Identity mapping

- `UserId` — the numeric, immutable GitHub account id (the `@login` can be renamed, so it never keys identity), run through the SDK's `IdentitySanitiser`.
- `DisplayName` — the profile `name`, falling back to `login`.
- `Email` — the public email, or (with `FetchPrimaryEmail = true`) the primary verified address from `/user/emails`.
- `Roles` / `TenantId` — empty; org membership gates *admission*, and the SDK's permission model is team-membership driven.

### Caching trade-off

Each successful validation is cached for `CacheTtlSeconds` (default 300), keyed by
a SHA-256 of the token — the raw bearer is never used as a key. A busy client then
costs GitHub one API round-trip per TTL window instead of one per request (GitHub's
authenticated rate limit is 5000/hr/token). **The cost:** a token revoked at GitHub
is still honoured for up to the TTL. Set `CacheTtlSeconds = 0` to validate every
request live (correct-but-chatty; only safe under low traffic).

## Sign-in leg (authorize + code exchange)

The sign-in button redirects to `buildAuthorizeUrl`; your callback route calls
`exchangeCode` to turn the returned `code` into the bearer the client then presents
on every request. The CSRF `state` round-trip, the callback route, and session
issuance are the consumer's to wire.

```fsharp
open ToolUp.AuthProviders.GitHubOAuth

let oauth = GitHubOAuthAppConfig.create "your-client-id" [ "read:user"; "user:email"; "read:org" ]

// 1. Sign-in button → redirect here (generate + persist `state` first):
let signInUrl = buildAuthorizeUrl oauth "https://app.example.com/auth/github/callback" state

// 2. Callback route, after validating that `state` echoes the persisted value:
match! exchangeCode httpClient secretStore oauth code "https://app.example.com/auth/github/callback" with
| Ok token -> // issue a session carrying token.AccessToken; the client presents it as a bearer
| Error msg -> // surface GitHub's error (bad_verification_code, redirect_uri_mismatch, …)
```

The OAuth App **client secret** is read from `ISecretStore` under the reserved
`_platform` scope by `ClientSecretKey` (default `github-client-secret`) — never from
an env var or config field, so it stays out of logs and rotates without a redeploy.

## Configuration (`TOOLUP_GITHUB_*` env vars)

| Var | Purpose | Default |
|---|---|---|
| `TOOLUP_GITHUB_AUTH` | Opt-in flag for `fromEnv` (`1`/`true`/…). | unset ⇒ `fromEnv` returns `None` |
| `TOOLUP_GITHUB_ALLOWED_ORGS` | Comma-separated org allow-list. | empty ⇒ any GitHub user |
| `TOOLUP_GITHUB_API_BASE_URL` | Override for GitHub Enterprise Server (`https://<host>/api/v3`). | `https://api.github.com` |
| `TOOLUP_GITHUB_CACHE_TTL_SECONDS` | Identity-cache TTL; `0` disables. Set-but-invalid fails fast. | `300` |
| `TOOLUP_GITHUB_FETCH_PRIMARY_EMAIL` | Enable the `/user/emails` fallback (needs `user:email`). | `false` |
| `TOOLUP_GITHUB_USER_AGENT` | The API `User-Agent` (GitHub requires one). | `ToolUp-Platform-GitHubAuth` |

## GitHub OAuth App setup

1. **Register the app** — GitHub → Settings → Developer settings → OAuth Apps → New OAuth App.
2. **Authorization callback URL** — must equal the `redirectUri` you pass to `buildAuthorizeUrl` / `exchangeCode` (GitHub validates strict equality).
3. **Store the client secret** — `secretStore.SetSecret("_platform", "github-client-secret", "<secret>")`.
4. **Scopes** — `read:user` for identity; add `user:email` for the email fallback, `read:org` for org allow-listing.
5. **GitHub Enterprise Server** — set `TOOLUP_GITHUB_API_BASE_URL` and the OAuth App's `AuthorizeBaseUrl` / `TokenBaseUrl` to your GHES host.

## Notes

- `IsCryptographicallyVerified = true` — GitHub's remote-token validation is not header-trust, so the provider passes the SDK's unverified-provider startup gate.
- `ApiBaseUrl` must be `https` — the bearer rides every call; a cleartext base is refused at construction.
- This is a **server** companion; there is no browser client package (the code exchange needs the server-held client secret).
