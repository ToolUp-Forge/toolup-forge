# Phase 3d migration — Entra External ID + team-invitation substrate

Operator walkthrough for adopting two co-shipped pieces from Phase 3d:

1. The `ToolUp.AuthProviders.EntraExternalId{,.Client}` companion pair.
2. The team-invitation substrate (`ITeamInviteApi`, `/invite/{token}` accept page, pending-invite blob).

The pieces are decoupled — you can adopt either independently — but they are intentionally co-shipped because the just-shipped Phase 5f team-creation policy makes team creation Platform-Admin-only by default, and an operationally usable closed-roster deployment needs an invitation flow to onboard people.

## Part 1 — Entra External ID provider pair

### What changes

A consumer that previously wired `ToolUp.AuthProviders.Oidc` against a Microsoft Entra External ID issuer (with manually-constructed issuer URL + `aud` claim manually mapped to `client_id`) can instead use the dedicated companion. The companion bakes in:

- Constructed v2.0 issuer URL from a `tenant` parameter (`<tenant>.ciamlogin.com/<tenant>/v2.0` by default; custom domain override available).
- Claim mapping: `oid` -> `UserId` (more stable than `sub` in External ID), `tid` -> `TenantId`, `idp` exposed via `readIdpClaim` for audit decorators.
- Browser-side default scope including `offline_access` (required for refresh tokens against External ID).
- Optional sign-up / sign-in user-flow policy routing.

### Tenant + app-registration setup

1. **Create a customer-facing tenant.** Entra portal → External Identities → Create external tenant. Note the short tenant name (used in `TOOLUP_ENTRA_EXTERNAL_ID_TENANT`); the GUID form is also acceptable.
2. **App registration.** External Identities → Applications → New registration:
    - Platform: Single-page application.
    - Redirect URI: matches what you wire client-side (typically `https://<app>/auth/callback`).
    - Authentication blade → enable both ID tokens and Access tokens.
3. **User-flow policies (optional but recommended).** Identity providers → User flows. Create two if you want sign-up + sign-in to be separately configurable:
    - Sign-up flow: choose attributes to collect (display name, email, …) and which IdPs to surface.
    - Sign-in flow: choose which IdPs to accept.
    - Record the policy ids for `TOOLUP_ENTRA_EXTERNAL_ID_SIGN_UP_POLICY` / `_SIGN_IN_POLICY`.
4. **Federated identity providers (optional).** Identity providers → Custom identity providers → add Google / Apple / Facebook / Microsoft consumer accounts. Each federated IdP emits a distinct `idp` claim on issued tokens.
5. **API permissions.** Application → API permissions:
    - Microsoft Graph: `openid`, `profile`, `email`, `offline_access` (delegated).
6. **Claim emission.** Per-user-flow blade → Application claims. Ensure `oid`, `tid`, `email`, `idp` are checked. External ID emits these by default; the audit lives here.

### Server-side wiring

```fsharp
open ToolUp.AuthProviders

// Option A — explicit config record
let entraConfig: EntraExternalIdConfig = {
    Tenant = "contoso"
    CustomDomain = None              // or Some "login.contoso.com"
    Audience = "<client-id GUID>"
    ClockSkewSeconds = None
    SignUpPolicyId = Some "B2C_SignUp"
    SignInPolicyId = None
}

let authProvider = EntraExternalIdAuthProvider.create None entraConfig

// Option B — fromEnv (reads TOOLUP_ENTRA_EXTERNAL_ID_*)
let authProvider =
    EntraExternalIdAuthProvider.fromEnv None
    |> Option.defaultWith (fun () ->
        failwith "TOOLUP_ENTRA_EXTERNAL_ID_TENANT / _AUDIENCE not set")

// Auto-register the preflight validator:
let authValidator =
    EntraExternalIdAuthValidator.tryFromEnv ()
    |> Option.toList

ServerApp.empty
|> ServerApp.withConfig serverConfig
|> ServerApp.withAuth authProvider
|> (fun app ->
    authValidator
    |> List.fold (fun s v -> ServerApp.withConfigValidator v s) app)
|> ServerApp.run
```

### Browser-side wiring

```fsharp
open ToolUp.AuthProviders.EntraExternalId

let entraClientConfig =
    EntraExternalIdClientConfig.create
        "contoso"
        "<client-id GUID>"
        "https://app.example.com/auth/callback"

// Optional sign-up policy:
let entraClientConfig =
    { entraClientConfig with SignUpPolicyId = Some "B2C_SignUp" }

Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        Mode = MultiTeam
        AuthUI = CustomAuthUI { Wrap = EntraExternalIdAuthUI.wrap entraClientConfig } }
    modules
```

### Verification

1. `dotnet build` succeeds.
2. Server preflight log shows `entra-external-id-auth (https://<tenant>.ciamlogin.com/<tenant>/v2.0) ... Ok` (or `... Error ...unreachable...` if the tenant id is wrong — fix and retry).
3. Open the app in a clean browser. The sign-in screen renders with the Entra "Welcome" panel; if `SignUpPolicyId` is set, a "Sign up" button appears alongside "Sign in".
4. Sign in via the deployment's IdP. The shell loads after the callback.
5. Open `/dev/inspect` and confirm the authenticated user carries the External ID `oid` as `UserId` (not the `sub`) and the `tid` as `TenantId`.
6. The audit trail's `UserLoggedIn` row for the test user carries the federated `idp` value (when a federated IdP was used) — audit decorators that emit per-IdP rows pick up the `EntraExternalIdAuthProvider.readIdpClaim` helper.

### Rolling back

Revert to `ToolUp.AuthProviders.Oidc` + manually-constructed config. The two packages are wire-compatible at the share-token / audit level.

### Raw-OIDC opt-out

Consumers who want a raw OIDC pair (e.g. targeting workforce Entra ID / Azure AD, Auth0, Keycloak) skip this companion entirely and use `ToolUp.AuthProviders.Oidc` + `ToolUp.AuthProviders.Oidc.Client` directly. The Entra companion is opt-in; both pairs can be referenced in the same solution if a deployment mixes IdPs (the generic pair handles every non-Entra provider).

## Part 2 — Team-invitation substrate

### What changes

A `ITeamInviteApi` ToolUp.Remoting endpoint set + a `/invite/{token}` accept page + an email-keyed pending-invite blob. Together they unlock "Platform-Admin-only team creation + Owner/Admin-issued invitations for the rest" — the operational shape the just-shipped Phase 5f `TeamCreationPolicy = PlatformAdminOnly` default targets.

The substrate composes over the existing `IShareTokenStore` (Phase 21b) — no new persistence layer; tokens live in the same blob layout as publishable Forms surveys, scoped by team.

### Server-side wiring

`ITeamInviteApi` is constructed per-request via `TeamInvitationHandler.teamInvitationApi`. Wire the route into the existing ToolUp.Remoting handler block:

```fsharp
open ToolUp.Remoting.Giraffe
open ToolUp.Platform.Teams.TeamInvitationHandler

// alongside the existing Remoting.fromContext wirings:
let teamInviteHandler : HttpHandler =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder TeamInviteApi.routeBuilder
    |> Remoting.fromContext (fun ctx ->
        let sts = ctx.RequestServices.GetService<IShareTokenStore>()
        let ts = ctx.RequestServices.GetService<ITeamStore>()
        let audit = ctx.RequestServices.GetService<IAuditLog>()
        let cfg = ctx.RequestServices.GetService<ServerConfig>()
        teamInvitationApi sts ts audit cfg ctx)
    |> Remoting.buildHttpHandler
```

`ServerConfig.PublicBaseUrl` must be set (e.g. `Some "https://app.example.com"`) — the issue handler builds the invitation URL as `<PublicBaseUrl>/invite/<token>`.

### Pending-invite middleware hook

`ScopeResolutionMiddleware` should call `TeamInvitationHandler.tryConsumePendingForUser` once per authenticated sign-in resolve. The function reads the `_platform/pending-invites.json` blob (30-second in-memory cache), and on a match removes the entry atomically while calling `ITeamStore.AddMember` and emitting `TeamInviteAcceptedFromPending`. Wire alongside the existing scope-resolution logic — the call is best-effort and returns `None` when no pending entry matches.

```fsharp
let! pending =
    TeamInvitationHandler.tryConsumePendingForUser
        blobStorage teamStore auditLog authenticatedUser

// pending : PendingInviteByEmail option — `Some entry` means a team
// membership was just applied; subsequent scope-resolution can rely
// on the membership being present.
```

### Browser-side wiring

`InviteAccept.fs` renders at `/invite/{token}` via the public-entry-dispatcher pattern:

```fsharp
Client.run
    { ClientConfig.defaults with
        ...
        PublicEntryDispatchers = [
            fun cfg ->
                if ToolUp.Platform.InviteAccept.isInviteUrl cfg then
                    ToolUp.Platform.InviteAccept.render ()
                    true
                else
                    false
        ] }
    modules
```

Authenticated visitors land directly on the accept page → `AcceptInvite` runs → success/failure. Unauthenticated visitors are shown a "please sign in" panel that returns them to the home page; they re-click the invite link after sign-in to complete the flow.

### Operator playbook

1. **Issue an invite.** Owner / Admin opens the team detail page in `TeamManagerUI` → clicks "Invite by link" → fills role + expiry + max-uses + optional email hint → copies the returned URL.
2. **Share the URL.** Email / IM / printed-on-paper — any channel. The URL is the secret; treat it as such.
3. **Recipient redeems.** Opens the URL → signs in if not already authenticated → joins the team.
4. **Revoke if needed.** Owner / Admin opens the team detail page → Pending Invites tab → Revoke. Subsequent attempts to redeem the same URL return "This invitation has been revoked".
5. **Inspect the audit trail.** Open `/dev/inspect` (Platform Admin) → audit trail under `team-{teamId}` scope → filter on `SourceModule = "_platform.team_invites"` for the per-team invitation history.

### Optional: email-send wiring

When an `INotificationSink` of `NotificationKind.Email` is wired (e.g. `ToolUp.Platform.NotificationChannels.Email.Smtp`) AND `IssueInvite` is called with an `EmailHint`, the issue handler can dispatch the invitation link via the email companion. This wiring is best-effort — failure to dispatch does NOT roll back the invitation issuance. (Phase 3d ships the substrate; the dispatch hook itself is a tracked follow-up.)

### Verification

1. `dotnet build ToolUp.Forge.sln` succeeds.
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — the `TeamInvitation` and `EntraExternalIdConfig` test packs pass with 0 failures.
3. Issue → accept → revoke flow exercised end-to-end against a running deployment (operator step, browser-driven).
4. `/dev/inspect` audit trail shows the expected `TeamInviteIssued` / `TeamInviteAccepted` / `TeamInviteRedeemed` / `TeamInviteRevoked` rows.

### Rolling back

The substrate is additive — to roll back, remove the `teamInviteHandler` route registration and the `InviteAccept` dispatcher entry. Outstanding invitation tokens persist in the share-token blob layout but become inert (no handler to redeem against).

## Cross-references

- [`docs/companions/auth-providers.md`](../companions/auth-providers.md) — full provider table + per-provider setup.
- [`docs/platform/auth.md`](../platform/auth.md) — claim-mapping conventions; SSE auth caveat.
- Phase 5f migration: [`05f-team-creation-policy.md`](05f-team-creation-policy.md) — the closed-roster posture this phase's invitation flow targets.
- Phase 11.G migration: [`11g-fromenv-helpers.md`](11g-fromenv-helpers.md) — the `fromEnv` convention this companion follows.
