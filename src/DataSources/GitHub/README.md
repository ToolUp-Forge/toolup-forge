# ToolUp.DataSources.GitHub

GitHub **App** OAuth credential flow (`IOAuthCredentialFlow`) for ToolUp.Platform.

This is the **connector-credential** companion — "connect a GitHub account so the
deployment can call the GitHub API on the user's behalf." It is distinct from
[`ToolUp.AuthProviders.GitHub`](../../AuthProviders/GitHub/README.md), which is the
**sign-in** companion ("prove who this user is" from an inbound bearer). Pick by intent:

| You want… | Companion |
|---|---|
| Users to **log in** with GitHub | `ToolUp.AuthProviders.GitHub` (`IAuthProvider`) |
| To **store a GitHub credential** and call the API for the user | `ToolUp.DataSources.GitHub` (this package) |

It plugs into the SDK's OAuth Authorization-Code substrate: the platform owns the
CSRF-state store, the `/api/oauth/{flowName}/authorize` + `/callback` HTTP endpoints,
refresh-token persistence in `ISecretStore`, and audit emission. This companion supplies
the GitHub-specific leg — build the authorize URL, exchange the code, refresh, revoke.

## Why GitHub Apps (not classic OAuth Apps)

The substrate is built around a **required refresh token** and `RefreshAccessToken`
minting short-lived access tokens from it (the OAuth "offline access" shape). Classic
GitHub OAuth Apps issue a single **non-expiring** access token and no refresh token —
that doesn't fit. A **GitHub App** (or an OAuth App with *"Expire user authorization
tokens"* enabled) issues `access_token` (~8h) + `refresh_token` (~6 months), the
canonical fit. The degenerate classic path is handled (the access token is stored as the
refresh token and handed back verbatim), but **GitHub Apps are the supported target**.

## Two GitHub-specific behaviours worth knowing

- **Refresh-token rotation.** GitHub rotates the refresh token on *every* refresh — the
  response carries a new `refresh_token` and invalidates the old one. Since the
  substrate's `RefreshAccessToken` only returns an access token, this flow writes the
  rotated refresh token back to its own `{flowName}-refresh-{dataSourceId}` secret slot
  as a side effect. Without it the *second* refresh would fail `invalid_grant`.
- **Revocation.** GitHub has no refresh-token-specific revoke endpoint, so `Revoke` mints
  a short-lived access token from the refresh token and then deletes the whole OAuth grant
  (`DELETE /applications/{client_id}/grant`, client Basic auth). Best-effort and never
  throws; the substrate deletes the local secret regardless.

## Wiring

```fsharp skip=fragment
open ToolUp.Platform
open ToolUp.DataSources.GitHubAppFlow

// httpClient + the app's ISecretStore come from your composition root.
let githubFlow =
    create httpClient secretStore (GitHubAppFlowConfig.create [ "read:user"; "repo" ])

ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with DataIngestion = EnabledDataIngestion }
|> ServerApp.withOAuthFlow githubFlow          // ← registers the flow into the substrate
|> ServerApp.run
```

`withOAuthFlow` registers the flow as an `IOAuthCredentialFlow` singleton; the substrate's
`/api/oauth/github/*` routes (mounted under `DataIngestion = EnabledDataIngestion`) resolve
it per-request by its `Name`. Register one flow per upstream provider.

### Per-connection credentials

The OAuth App client id/secret are per data source, read from `ISecretStore` under the
substrate's key convention (scope = the caller's resolved scope):

- `github-client-id-{dataSourceId}`
- `github-client-secret-{dataSourceId}`

Store them via the admin BYOK settings API (or `secretStore.SetSecret`) before a user
starts the connect flow. The substrate persists the resulting refresh token under
`github-refresh-{dataSourceId}` and this flow keeps it current across rotations.

## GitHub App setup

1. **Register a GitHub App** — Settings → Developer settings → GitHub Apps → New.
2. **Enable expiring user tokens** — *"Expire user authorization tokens"* must be **on**
   (this is what makes GitHub issue refresh tokens).
3. **Callback URL** — set it to `{your-host}/api/oauth/github/callback` (must match the
   redirect URI the substrate sends; GitHub validates strict equality).
4. **Permissions** — grant the App the repository/organization/account permissions your
   integration needs; the `Scopes` you pass to `GitHubAppFlowConfig.create` document intent
   and populate the admin-UI descriptor.
5. **GitHub Enterprise Server** — override `AuthorizeBaseUrl` / `TokenBaseUrl` / `ApiBaseUrl`
   on the config to your GHES host.

## Notes

- `SupportsPkce = false` — GitHub Apps / OAuth Apps have no PKCE; the substrate sends no PKCE parameters.
- No `Octokit` / vendor SDK (GP 1) — BCL `HttpClient` + `System.Text.Json` only.
- Stateless across calls (Phase 9c rule 4): `ISecretStore` + `HttpClient` arrive via `create`; per-call state rides `OAuthFlowContext`.
