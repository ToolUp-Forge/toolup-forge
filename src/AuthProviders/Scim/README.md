# ToolUp.AuthProviders.Scim

SCIM 2.0 inbound provisioning for ToolUp.Platform — the standards-based path by
which an enterprise identity provider (Microsoft Entra ID, Okta, OneLogin, or
anything else that speaks [RFC 7644](https://datatracker.ietf.org/doc/html/rfc7644))
**pushes** user and group lifecycle into a deployment.

Provisioning on hire and deprovisioning on offboard is a compliance requirement in
most enterprise procurement, not a convenience. The other two auth companions cover
the *pull* direction — a user arrives and authenticates — and neither can remove an
account when HR ends someone's employment. This one closes that leg.

Protocol-only: no vendor SDK, no vendor-specific type. BCL, `System.Text.Json` and
Giraffe, all of which arrive transitively through `ToolUp.Platform.Server` (GP 1).

## What it does

| Endpoint | Behaviour |
|---|---|
| `GET /scim/v2/Users` | Lists the configured team's members. `startIndex` / `count` pagination; `filter=userName eq "..."`. |
| `POST /scim/v2/Users` | Provisions a member. `409 uniqueness` when already present — the shape both Entra and Okta expect, and what makes them switch to an update. |
| `GET /scim/v2/Users/{id}` | One member. |
| `PUT /scim/v2/Users/{id}` | Replace. Only `active` is actionable; other attributes are accepted and ignored (RFC 7644 §3.5.1). |
| `PATCH /scim/v2/Users/{id}` | `active: false` deprovisions **within the request**. |
| `DELETE /scim/v2/Users/{id}` | Deprovisions. |
| `GET /scim/v2/Groups`, `GET /scim/v2/Groups/{id}` | The configured team, projected as a SCIM Group. |
| `PATCH /scim/v2/Groups/{id}` | Add / remove members; a group assignment carries the role. |
| `PUT /scim/v2/Groups/{id}` | Full membership replace, applied as an add/remove delta. |
| `POST /scim/v2/Groups`, `DELETE /scim/v2/Groups/{id}` | `501` — see [Deliberate refusals](#deliberate-refusals). |
| `GET /scim/v2/{ServiceProviderConfig,Schemas,ResourceTypes}` | Discovery documents (RFC 7644 §4). |

## Composing it

```fsharp
open ToolUp.Platform
open ToolUp.AuthProviders.ScimHandler
open ToolUp.AuthProviders.ScimRoutes

let scimConfig =
    ScimConfig.create "acme-engineering"
    |> ScimConfig.withBaseUrl "https://app.example.com"

ServerApp.empty
|> ServerApp.withConfig myConfig
|> ScimServerApp.ofServerApp
|> ScimServerApp.withScim scimConfig
|> ScimServerApp.run
```

A deployment that never calls `withScim` mounts no routes, registers no services,
and is byte-for-byte the deployment it was before this package existed (GP 13).
`ScimServerApp.run` on a `NoScim` app is literally `ServerApp.run app.Base`.

### The bearer token

The endpoint reads its token from `ISecretStore` on **every request**, so rotating
it takes effect immediately with no restart — the same convention the audit sinks
use. By default it looks under scope `team-{teamId}`, key `SCIM_BEARER_TOKEN`;
`ScimConfig.withSecret` moves it.

```fsharp
do! secrets.SetSecret("team-acme-engineering", "SCIM_BEARER_TOKEN", generatedToken) |> Async.Ignore
```

Comparison is constant-time (`CryptographicOperations.FixedTimeEquals`). The gate is
**fail-closed at every branch**: no `Authorization` header, a non-Bearer scheme, no
`ISecretStore` composed, or no token stored all produce `401`. There is no
"unconfigured means open" path.

Generate a token with at least 256 bits of entropy from a CSPRNG. It is a
long-lived credential that can add and remove members of a team, so treat it as one:

```powershell
[Convert]::ToBase64String((New-Object byte[] 32 | ForEach-Object { $_ } ; [System.Security.Cryptography.RandomNumberGenerator]::Fill($b); $b))
```

> **On the credential's lifetime.** A dedicated long-lived token is the current
> shape. When scoped service-account tokens ship, they are the better bearer
> credential for this endpoint — same gate, a credential with an owner, an expiry
> and a revocation path.

### Attribute mapping

Declared as data, so the join key between the IdP's directory and platform
membership is visible in the composition rather than buried in a handler:

```fsharp
let mapping =
    { ScimAttributeMapping.defaults with
        Identity = FromPrimaryEmail
        Roles =
            ScimRoleMapping.defaults
            |> ScimRoleMapping.withGroup "Acme Engineering Admins" Admin }

let scimConfig = ScimConfig.create "acme-engineering" |> ScimConfig.withMapping mapping
```

- **`Identity`** — which SCIM attribute becomes the platform `userId`:
  `FromUserName` (default), `FromPrimaryEmail`, or `FromExternalId`. `FromExternalId`
  is the most stable, being the only one that survives a rename in the IdP.
  **Changing this after provisioning has begun orphans every member added under the
  old rule** — they become unmatchable on the deprovision leg.
- **`Roles`** — SCIM has no role attribute, so the role a member takes is decided by
  the **group's** `displayName`. An unmapped group name takes `Roles.Default`, which
  is `Member`: a misconfiguration degrades to least privilege, never to most (GP 4).

## Guarantees worth knowing

**Scope isolation (GP 4).** One token provisions exactly one team. Every operation is
expressed against `config.TeamId`; there is no parameter by which a request can name
another team. A `Groups` request carrying any other id gets a plain `404` rather than
a `403`, so the endpoint is not a team-id oracle.

**Audit fires unchanged (GP 6).** Nothing here writes a membership row. Every change
goes through `ITeamStore`, so `MemberAdded` / `MemberRemoved` / `MemberRoleChanged`
land exactly as they do for a human admin — along with the `MembershipChanged`
notification that evicts the scope-resolver cache, and the last-Owner safeguard. A
SCIM push is a different **actor**, not a different code path.

The actor is stamped as `_scim`, mirroring the platform's own `_bootstrap`
convention: an underscore-prefixed id cannot collide with a real user id, so an
auditor separates IdP-driven lifecycle from human administration by reading the trail,
with no join.

**Deactivation is immediate.** `PATCH active:false` and `DELETE` both remove the
membership inside the request. No sweep, no deferred job — "access is gone within one
round-trip" is a property of the endpoint, not of an operator's cron.

**The last Owner is still protected.** `ITeamStore.RemoveMember` refuses to strip the
last Owner, and that refusal surfaces as `400 invalidValue` naming the reason. It is
deliberately *not* swallowed: an IdP told the removal succeeded would report the user
as deprovisioned while their access remained.

## Deliberate refusals

Each of these is a `501` naming the situation, rather than a silent success. An IdP
that receives a `200` for something the provider did not do reports the tenant as in
sync when it is not.

- **`POST /scim/v2/Groups` / `DELETE /scim/v2/Groups/{id}`.** A team is a platform
  concept with an Owner, a scope and a storage container; a SCIM push cannot mint one
  meaningfully. Create the team first, then bind an endpoint to it.
- **Filters other than `<attr> eq "<value>"`.** RFC 7644 §3.4.2.2 explicitly permits
  `501` here, and it is the honest answer: silently mis-parsing a filter returns a
  *wrong result set*, which an IdP acts on.
- **`externalId eq`.** The platform persists no `externalId`, so this filter can only
  ever answer "no match" — and an empty list would tell the IdP the user does not
  exist and provoke a duplicate create. The `501` steers it to `userName`, which is
  answered.
- **Nested groups.** A `members` entry of `type: "Group"` is refused by name.

## The one asymmetry to configure your IdP around

The platform stores no directory attributes of its own — a membership row is a user
id, a role and a join date. So a row that exists **is** an active membership, and
deprovisioning removes the row rather than flagging it.

Consequence: a `GET` after a successful deactivation returns **404, not an inactive
user**. There is no tombstone. An IdP configured to expect the
inactive-tombstone shape will read that 404 as an error; both Entra and Okta handle
"deleted after deprovision" correctly out of the box, but a custom SCIM client may
need to be told.

For the same reason `PATCH` that deprovisions answers `204`, not `200` — returning a
fabricated inactive user would claim a tombstone this provider does not keep.

## Conformance

`ToolUp.Platform.Tests` carries a fixture pack replaying recorded Entra and Okta
provisioning sequences — create user, assign group, change role, deactivate — against
an in-memory `ITeamStore`, asserting the resulting membership state and the audit
trail. No live IdP is needed, and the pack runs under `VerifyAll`.

## Wire-format notes

The JSON codec is hand-rolled rather than record-mapped, for three reasons a record
mapping cannot address: the `schemas` URN envelope is decided by the resource's
*type*; `emails[].primary` is a decode-time selection rule IdPs disagree about; and
`Operations[].value` in a PATCH is polymorphic, keyed off a sibling `path`. The
codec also ignores unknown attributes, which is what RFC 7644 §3.5.2 requires of a
service provider.

Two details that bite interop and are handled here: `startIndex` is **1-based**, and
`Error.status` is a **string** on the wire — an IdP parsing `"status": 404` as a
number fails.

## See also

- [RFC 7643](https://datatracker.ietf.org/doc/html/rfc7643) — SCIM Core Schema
- [RFC 7644](https://datatracker.ietf.org/doc/html/rfc7644) — SCIM Protocol
- `docs/companions/auth-providers.md`
