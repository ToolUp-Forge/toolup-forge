# EntraDirectory.Probe

Local probe for the `ToolUp.AuthProviders.EntraDirectory` companion. Runs
the real `IUserDirectory.SearchUsers` and `IUserDirectory.NotifyInvitation`
methods against live Microsoft Graph using `DefaultAzureCredential`. Intended
as a fast iteration loop — under 5 seconds round-trip for `SearchUsers`,
no NuGet publish + CI + CD cycle required.

## Bug classes this catches

- **STJ deserialisation defects** — F# `type private` + `[<CLIMutable>]`
  records produce a non-public parameterless constructor that
  `JsonSerializer.Deserialize` rejects (the 0.5.12 fix). Surfaces as
  `Error "directory unavailable: ... is not supported"` on the very
  first `SearchUsers` call.
- **Graph filter / advanced-query shape** — `$filter=startswith(...) or
  startswith(...)` against `/users` requires `$count=true` + the
  `ConsistencyLevel: eventual` header (the 0.5.11 fix). Without
  `$count=true`, Graph silently returns only display-name matches;
  an email-prefix query that should match a known user returns `[]`.
- **`DefaultAzureCredential` misconfiguration** — e.g. `az login` not
  performed, wrong tenant, missing `User.ReadBasic.All` / `Mail.Send`
  grant. Surfaces as a `401 Unauthorized` or
  `403 Forbidden` body in the probe's error line.
- **Per-mailbox `Mail.Send` permission probes** — wrong `SENDER_OID`
  surfaces as `404 Not Found` from the `/users/{oid}/sendMail` URL.

## What it does NOT catch

Kestrel sync-IO defects — `AllowSynchronousIO=false` rejecting
`JsonSerializer.Serialize(stream, ...)` calls into `HttpResponse.Body`
(the 0.5.10 fix). That class only manifests when the response is
written through the HTTP pipeline; this probe never crosses an
outbound HTTP boundary on the response side. A separate end-to-end
probe (local `dotnet run` of a consuming app + curl) covers it.

## Auth on a dev machine

```powershell
az login            # default browser flow, pick the toolup tenant
az account show     # confirm tenantId matches TOOLUP_OIDC_ISSUER's tenant guid
```

`DefaultAzureCredential` walks its chain and finds the `az` session.

## Run

```powershell
$env:TOOLUP_ENTRA_DIRECTORY_ENABLED = "1"
$env:TOOLUP_ENTRA_DIRECTORY_SENDER_OID = "<sender-oid>"   # optional, enables notify

# Default exercise: SearchUsers("and", 5) + SearchUsers("andrew@", 5).
# The email-prefix arm is the case that was silently failing.
dotnet run --project probes\EntraDirectory.Probe

# Custom queries
dotnet run --project probes\EntraDirectory.Probe -- search "nat"
dotnet run --project probes\EntraDirectory.Probe -- search "user@example.com" 25

# Test the invitation-email path
dotnet run --project probes\EntraDirectory.Probe -- notify "you@example.com"
```

## Exit codes

- `0` — every operation returned `Ok`.
- `1` — at least one operation returned `Error`.
- `2` — usage error (unknown arg, env not set).

## Not packed

`<IsPackable>false</IsPackable>` — forge-internal developer aid, not a
shipped artefact.
