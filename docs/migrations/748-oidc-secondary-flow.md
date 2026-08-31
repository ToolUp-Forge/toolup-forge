# Migration — OIDC secondary-flow ("Sign up") affordance

`OidcAppConfig` and `OidcUIConfig` each gain `SecondaryFlow: OidcSecondaryFlow option` — an optional second button on the sign-in screen that starts the same OIDC sign-in with extra authorize-request parameters.

**Default behaviour is unchanged (GP 11).** `SecondaryFlow = None` renders the single-button sign-in screen exactly as before: no element is hidden, none is emitted. Every `OidcPresets.*` constructor, `OidcAppConfig.create`, `OidcUIConfig.defaults` and every companion projection supplies `None`. An existing deployment that upgrades and changes nothing is byte-for-byte identical.

## What you have to change

**Only if you construct `OidcUIConfig` or `OidcAppConfig` as a full record literal.** Add one field:

```fsharp skip=fragment
let cfg: OidcUIConfig = {
    Issuer = "https://your-issuer.example.com"
    ClientId = "<client-id>"
    RedirectUri = "https://your-app.example.com/auth/callback"
    Scopes = [ "openid"; "profile"; "email" ]
    PostLogoutRedirectUri = None
    ValidateIdToken = Some true
    BearerToken = None
    SecondaryFlow = None        // ← new; None = today's single-button screen
}
```

Consumers using `OidcUIConfig.defaults`, `OidcAppConfig.create`, a preset, or a `{ cfg with … }` record update need no change at all.

## What you can now do

A provider that hosts more than one journey behind the same app registration — an Entra External ID sign-up user flow, a Google re-consent path — is expressed by declaring the second flow:

```fsharp skip=fragment
// Entra External ID: the dual-button "Sign in / Sign up" shell.
let cfg =
    OidcPresets.entraExternalId "<tenant-subdomain>" "<client-id>" "<redirect-uri>"
    |> OidcPresets.withEntraSignUpUserFlow "<sign-up-user-flow-policy-id>"

// Anything else: label + the parameters that route the journey.
let googleCfg =
    OidcPresets.google "<client-id>" "<redirect-uri>"
    |> OidcPresets.withSecondaryFlow "Re-consent" [ "prompt", "consent" ]
```

Both buttons run one sign-in: same client id, same redirect URI, same freshly-minted `state` / `nonce` / PKCE challenge, same callback, same token path. Only the appended authorize parameters differ, which is what the provider routes on.

**Keys must be provider-specific.** Extras are appended, not merged, so repeating a parameter the client emits itself (`response_type`, `client_id`, `redirect_uri`, `scope`, `state`, `nonce`, `code_challenge`, `code_challenge_method`) sends it twice, and an issuer's handling of a duplicate is undefined. `OidcCoherenceValidator` rule 16 refuses such a config at preflight (Error), and warns on a flow declared with a blank label or no parameters at all.

## Migrating off the `EntraExternalId.Client` companion

The companion's dual-button shell was one of the two reasons [its deprecation note](0.4.0-entra-external-id-deprecation.md) told consumers to stay on it. The preset path now issues the same authorize request — the standard OAuth / PKCE set plus `p=<policyId>` — so:

```fsharp skip=fragment
// Before — CustomAuthUI wrapping the companion shell.
let entraCfg =
    { EntraExternalIdClientConfig.create "<tenant>" "<client-id>" "<redirect>" with
        SignUpPolicyId = Some "<policy-id>" }

ClientConfig.compose {| … AuthUI = CustomAuthUI { Wrap = EntraExternalIdAuthUI.wrap entraCfg } |}

// After — the standard shell.
let cfg =
    OidcPresets.entraExternalId "<tenant>" "<client-id>" "<redirect>"
    |> OidcPresets.withEntraSignUpUserFlow "<policy-id>"

ClientConfig.compose {| … AuthUI = OidcAuthUI (OidcAppConfig.toClientConfig cfg) |}
```

**One gap remains.** `SignInPolicyId` — routing the *primary* sign-in button through its own user-flow policy — has no generic equivalent, because the slot is by definition a *second* flow and the primary flow carries no extras. A deployment relying on an explicit sign-in policy stays on the companion for now.

## Verification

1. **`dotnet build`** — a full-record-literal construction that has not been updated fails to compile (FS0764, missing field). There is no silent path.
2. **Sign-in smoke test** on a deployment that declares nothing: the screen, the request and the session are unchanged.
3. **Sign-up smoke test** where a flow is declared: the second button reaches the provider's second journey and lands on the same callback, and the session that results is indistinguishable from a primary-flow one.

## Rollback

Drop the `|> OidcPresets.withSecondaryFlow …` / `withEntraSignUpUserFlow …` pipe, or set `SecondaryFlow = None`. The button disappears; nothing else in the flow was ever conditioned on it. No stored state is involved.

## Related

- [Secondary flow — the "Sign up" affordance](../companions/auth-providers.md#secondary-flow--the-sign-up-affordance) — the reference documentation.
- [`0.4.0-entra-external-id-deprecation.md`](0.4.0-entra-external-id-deprecation.md) — the companion deprecation this closes a reason for.
- [`746-oidc-bearer-token-strategy.md`](746-oidc-bearer-token-strategy.md) — the previous widening of the same two records.
