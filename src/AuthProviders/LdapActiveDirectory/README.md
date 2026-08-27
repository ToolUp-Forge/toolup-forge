# ToolUp.AuthProviders.LdapActiveDirectory

Server-side `IAuthProvider` for **Active Directory** and **generic LDAP** directories. For
regulated / on-premise / air-gapped deployments whose identity store is AD or LDAP and which
cannot reach an external OIDC issuer.

Identity is proven by **binding to the directory as the user** — a proof-of-possession of the
password against the authoritative store, not the trust of a request header. It slots into the
`IAuthProvider` family alongside the OIDC and GitHub providers.

`System.DirectoryServices.Protocols` only — no vendor SDK. That package is a managed API that
P/Invokes the OS LDAP client (`wldap32` on Windows, OpenLDAP `libldap` on Linux / macOS), so the
directory dependency never reaches `ToolUp.Platform.*` (GP 1) and a deployment that does not
compose the provider pays nothing (GP 11 / GP 13).

## How it authenticates

Credentials arrive as HTTP **Basic** auth (`Authorization: Basic base64(user:pass)`). Per request:

1. Extract the username + password. An **empty password is rejected outright** — a bind with a DN
   and an empty password is an *unauthenticated bind* that succeeds anonymously on many
   directories (a classic LDAP auth bypass).
2. **Service-bind + search** for the user by the configured login attribute
   (`sAMAccountName` / `userPrincipalName` / `mail` / `uid`). The username is RFC-4515-escaped, so
   `*)(uid=admin)`-style **LDAP injection** cannot rewrite the filter. Exactly one match is required.
3. **Bind as the user's DN** with the presented password — the authoritative password check.
4. **Resolve group membership** (direct `memberOf` + optional nested expansion via the AD in-chain
   matching rule `1.2.840.113556.1.4.1941`) and map to ToolUp roles via the `ldap.json` policy.
5. Build the `AuthenticatedUser`; cache the validated identity for `CacheTtlSeconds`.

All directory access goes through the `ILdapConnectionFactory` seam, so the pipeline is
unit-tested in-process against an in-memory fake directory with no live LDAP server.

## Configuration

`LdapConfig` (see `LdapConfig.fs`). LDAPS on port 636 with strict certificate validation and
nested-group resolution are the defaults. Build over a host with `LdapConfig.defaults "dc.example.com"`,
override fields, or read the whole config from the environment with `LdapConfig.fromEnv ()`.

| Env var | Meaning |
|---|---|
| `TOOLUP_LDAP_AUTH` | **Opt-in** — truthy enables the provider / health-check / validator. |
| `TOOLUP_LDAP_HOST` | Directory host (required). |
| `TOOLUP_LDAP_PORT` | Override the port derived from the channel binding. |
| `TOOLUP_LDAP_CHANNEL` | `ldaps` (default) / `starttls` / `plaintext`. |
| `TOOLUP_LDAP_ALLOW_PLAINTEXT` | **Required** to permit a `plaintext` bind — otherwise refused. |
| `TOOLUP_LDAP_ALLOW_UNTRUSTED_CERT` | Truthy ⇒ accept any server cert (**dev/test only**). |
| `TOOLUP_LDAP_CERT_THUMBPRINT` | Pin the server certificate (strict validation). |
| `TOOLUP_LDAP_BIND_DN` | Service-account DN used for the user search (not secret). |
| `TOOLUP_LDAP_BIND_SECRET_KEY` | `ISecretStore` key for the bind password (default `auth/ldap/bind`). |
| `TOOLUP_LDAP_SEARCH_BASE` | User-search subtree, e.g. `OU=Users,DC=example,DC=com` (required). |
| `TOOLUP_LDAP_LOGIN_ATTR` | Login attribute (default `sAMAccountName`). |
| `TOOLUP_LDAP_USER_ID_ATTR` | Stable id attribute (default `objectGUID`). |
| `TOOLUP_LDAP_DISPLAY_ATTR` / `_EMAIL_ATTR` / `_MEMBEROF_ATTR` / `_USER_OBJECTCLASS` | Attribute schema. |
| `TOOLUP_LDAP_NESTED_GROUPS` | Truthy (default) enables nested-group resolution. |
| `TOOLUP_LDAP_CACHE_TTL_SECONDS` | Validated-identity cache TTL (default 300; `0` disables). |
| `TOOLUP_LDAP_TIMEOUT_SECONDS` | Per-operation directory timeout (default 10). |

**Secrets never sit in config.** The service-account *bind DN* is a directory path (not secret) and
rides the config; the *bind password* is read from `ISecretStore("_platform", "auth/ldap/bind")`
on every service bind, so a rotated password flows through without a recompose.

### Channel binding (LDAPS / StartTLS / certificate validation)

- **`Ldaps`** (default) — implicit TLS on 636.
- **`StartTls`** — plain LDAP on 389 upgraded to TLS via the StartTLS extended operation before any
  bind.
- **`Plaintext`** — unencrypted. **Refused at construction** unless `TOOLUP_LDAP_ALLOW_PLAINTEXT` is
  set; the security-class config validator also aborts startup on an un-acknowledged plaintext bind.
- Certificate validation is `Strict` by default (full chain, or a pinned thumbprint). `AllowUntrusted`
  disables it for dev/test and is flagged by the config validator as MITM-vulnerable.

## Group → role mapping (`ldap.json`)

Stored under `_platform/auth/ldap.json` (read via `ISecretStore`). Maps LDAP groups to ToolUp roles;
nested membership is expanded before mapping.

```json
{
  "matchByCommonName": true,
  "defaultRoles": ["member"],
  "mappings": [
    { "group": "ToolUp-Admins",   "roles": ["admin", "member"] },
    { "group": "ToolUp-Analysts", "roles": ["analyst"] }
  ]
}
```

`matchByCommonName: true` matches the group's CN (`ToolUp-Admins`); `false` matches the full group DN.

## Wiring

```fsharp skip=fragment
open ToolUp.AuthProviders

// From the environment (returns None unless TOOLUP_LDAP_AUTH is set).
let! ldap = LdapAuthProvider.fromEnv secretStore (Some logger)

// Register the health-check + config validator alongside (both env-gated):
LdapHealthCheck.tryFromEnv secretStore   |> Option.iter (ServerApp.withHealthCheck app)
LdapConfigValidator.tryFromEnv ()        |> Option.iter (ServerApp.withConfigValidator app)
```

### Hybrid fallback (AD staff + OIDC contractors)

For a deployment mixing AD-resident staff with OIDC-federated contractors, chain the providers —
OIDC first, LDAP second for a user the OIDC store does not know:

```fsharp skip=fragment
let provider = LdapAuthProvider.withFallback oidcProvider ldapProvider
// or an arbitrary chain: LdapAuthProvider.chain [ oidc; ldap ]
```

`IsCryptographicallyVerified` on the chain is the conjunction of its members, so the startup
unverified-provider gate treats the composite as strong as its weakest admitting path.

## Health check

`LdapHealthCheck` (Readiness) binds and runs a probe search. It reports **`Degraded`** — reachable
but misconfigured — when the bind succeeds but the probe search returns **0 users**, the common
silent misconfiguration (wrong search base / login attribute) that otherwise fails every sign-in
while `/ready` stays green.

## Testing against a real directory

The seam lets the provider be unit-tested with no server. To exercise the real
`System.DirectoryServices.Protocols` adapter, point `TOOLUP_LDAP_*` at a local OpenLDAP / AD
instance (e.g. an `osixia/openldap` container) seeded with a user and nested groups, and run the
`IAuthProviderContract` pack against `LdapAuthProvider.create`.

## Licence

Apache-2.0. `System.DirectoryServices.Protocols` is an MIT-licensed Microsoft package; it links the
OS LDAP client dynamically at the P/Invoke boundary.
