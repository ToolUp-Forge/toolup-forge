# ToolUp.AuthProviders.EntraDirectory

Microsoft Graph directory-search companion for ToolUp Platform. Implements
the `IUserDirectory` substrate against Microsoft Entra (Azure AD) tenants
so the SDK's team-invite UI can surface a typeahead of matching directory
entries instead of asking the operator to memorise an email.

## When to use it

Wire this companion when:

- Your deployment authenticates users via Microsoft Entra (OIDC against
  `login.microsoftonline.com` or `ciamlogin.com`).
- Operators inviting members to teams know colleagues by name, not by
  full email.
- The deploying tenant is willing to grant the calling identity the
  `User.ReadBasic.All` (or `User.Read.All`) Graph **application**
  permission.

Without this companion, the typeahead degrades to a plain text input
and the operator types the full email — the existing
invite-by-email flow still works.

## Wiring

`Server.fs` composition:

```fsharp
open ToolUp.AuthProviders

ServerApp.empty
|> ServerApp.withConfig config
// …other compose calls…
|> ServerApp.withUserDirectory (EntraDirectory.fromManagedIdentity ())
|> ServerApp.run
```

`fromManagedIdentity ()` uses the commercial Microsoft Graph cloud
(`https://graph.microsoft.com`) and Azure Identity's
`DefaultAzureCredential` for token acquisition. The credential chain
resolves in the following order:

1. Environment variables (`AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` /
   `AZURE_TENANT_ID`).
2. Managed identity (IMDS on App Service / Container Apps / AKS / VMs).
3. Visual Studio sign-in.
4. Azure CLI (`az login`).
5. Azure PowerShell.

In production on Azure App Service: the system-assigned managed
identity is the recommended path — no client secret to rotate.

For local development: run `az login` once; the cached token is reused
for the lifetime of the session.

National-cloud deployments override the endpoint:

```fsharp
EntraDirectory.create { GraphEndpoint = "https://graph.microsoft.us" }
```

## Granting the Graph permission

The calling identity needs the `User.ReadBasic.All` Graph application
permission. For a system-assigned managed identity on Azure App
Service:

```powershell
$msi = Get-AzADServicePrincipal -DisplayName "<app-service-name>"
$graph = Get-AzADServicePrincipal -ApplicationId "00000003-0000-0000-c000-000000000000"
$role = $graph.AppRole | ? { $_.Value -eq "User.ReadBasic.All" }

New-AzADAppRoleAssignment `
    -ObjectId $msi.Id `
    -PrincipalId $msi.Id `
    -ResourceId $graph.Id `
    -AppRoleId $role.Id
```

The tenant admin must consent to the permission. Once consented, the
companion picks it up automatically — no app restart needed.

## What the companion returns

`UserSummary { UserId; DisplayName; Email }`:

- `UserId` — the directory's `id` (Entra `oid`). This matches the
  `oid` claim in the user's access token, so the team-membership
  records the companion's UserId can later be resolved back to a
  signed-in user without re-querying Graph.
- `DisplayName` — Graph `displayName`. Falls back to
  `userPrincipalName` for the rare directory entry without one.
- `Email` — Graph `mail`. Falls back to `userPrincipalName` when
  `mail` is unset (common for newer Entra users).

The companion enforces a minimum prefix length of 2 characters
server-side; shorter queries return `Ok []` without hitting Graph.

## Environment-driven wiring

`EntraDirectory.fromEnv ()` reads two env vars:

| Variable | Purpose |
|---|---|
| `TOOLUP_ENTRA_DIRECTORY_ENABLED` | `1` / `true` to enable the companion. |
| `TOOLUP_ENTRA_DIRECTORY_GRAPH_ENDPOINT` | Override the Graph endpoint (default `https://graph.microsoft.com`). |

Returns `None` when both are unset — the composition root falls back to
wiring the typeahead without a directory companion. Useful for the
"same code on dev + prod, different behaviour per env" pattern.

## Errors

Transient Graph 429 / 5xx surface as
`Error "directory unavailable: …"`. The SDK's typeahead UI renders
the string under the input and continues to accept full email entry.
Permanent failures (missing role assignment, deleted application
registration) surface the same way; check the App Service logs for
the Graph response body.

## Privacy posture

The companion only ever calls `GET /users` with a `startsWith` filter
against the operator's query — it never enumerates the directory, never
downloads bulk data, and never writes to Graph. The only directory
data leaving Graph is the matched entries' `id` / `displayName` /
`mail` / `userPrincipalName`.

Operator queries are not logged by the companion. Audit-trail
configuration in the consuming SDK's `IAuditLog` applies to the
team-invite action itself, not to the directory lookups that informed
it.
