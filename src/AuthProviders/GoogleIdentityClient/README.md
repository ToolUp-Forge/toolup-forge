# ToolUp.AuthProviders.GoogleIdentity.Client

Client-side **Google Identity Services** (GIS) sign-in UI for `ToolUp.Platform`: Google's rendered
branded button, an opt-in One Tap prompt, and a credential bridge that admits the returned
`id_token` to the same OIDC token store the redirect flow writes.

## When to bother

**You do not need this package to sign in with Google.** `ToolUp.AuthProviders.Oidc.Client` plus
`OidcPresets.google` is a complete, functional Google sign-in — redirect, PKCE, callback, session,
sign-out. This companion exists for two things that specifically require loading Google's own
JavaScript library:

- **The branded button.** Google's brand guidelines ask for their rendered button rather than a
  look-alike, and the rendered button only comes from the GIS library.
- **One Tap.** The prompt that offers a returning visitor their Google account without a click.

If you want neither, compose the redirect flow and skip this package entirely — a deployment that
does not compose it carries zero GIS bytes.

## Composition

```fsharp skip=fragment
open ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityConfig
open ToolUp.AuthProviders.GoogleIdentity

let googleUi =
    GoogleIdentityUIConfig.create "1234567890-abcdefg.apps.googleusercontent.com"

Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        AuthUI = GoogleIdentityRegister.authUI googleUi
        Handlers = {
            ClientHandlerRegistry.empty with
                AuthUIHandlers = [ GoogleIdentityRegister.handler ]
                SignOutHandler = Some(GoogleIdentityRegister.signOutHandler googleUi)
        } }
    modules
```

One Tap is **off** unless asked for — auto-prompting a deployment's users is a product decision the
SDK does not make on its behalf:

```fsharp skip=fragment
let withOneTap = GoogleIdentityUIConfig.withOneTap googleUi
```

### Content-Security-Policy

The library is fetched from `accounts.google.com`, so a deployment with security hardening on must
widen its policy or the button silently never renders — the browser blocks the fetch and the script
tag simply errors. Composition, not a hand-edited header, is the supported way:

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withCspContributor (GoogleIdentityServicesCspContributor())
|> ServerApp.withConfigValidator (
    GoogleIdentityCspValidator.GoogleIdentityCspValidator(serverConfig, services) :> IConfigValidator)
```

The second line is a preflight: registering it declares "this deployment renders the Google button",
and it warns at startup if no contributor covers Google's origins. Nothing on the server can observe
a client-tier composition by itself, which is why the registration is explicit — and why a
redirect-flow deployment, which needs none of these origins, is never nagged.

## What the session is

GIS returns exactly one value: an `id_token` JWT signed by Google. There is no access token and no
refresh token — the credential flow has no token endpoint to exchange against.

The bridge validates the credential's `iss` / `aud` / `exp` (and `nonce`, when one was configured)
and then stores it through `OidcTokenStore.persistTokens`, i.e. the same slot the redirect flow
writes and the same bearer every API request carries. `classifyStoredToken`, `signOut` and the
pre-expiry refresh timer all operate on the projected `OidcUIConfig`, so a GIS session and a
redirect-flow session are the same session to everything downstream.

Two consequences follow from Google's flow rather than from this code:

1. The bearer is a **real JWT**, so `classifyStoredToken` reports `FreshJwt` where the Google
   redirect flow reports `OpaqueToken` (Google's access tokens are always opaque). The server
   validates it against Google's JWKS with no extra wiring.
2. There is **no refresh token**, so the session cannot be renewed silently. The refresh timer arms
   as usual, finds nothing to refresh at expiry, and the shell returns to the sign-in screen — a
   re-prompt roughly hourly. A deployment needing long-lived sessions uses the redirect flow with
   `access_type=offline`, which is where Google issues refresh tokens.

## Packaging

Client-tier Fable source-in-nupkg: the `.fs` files ship under `fable/` and compile as part of the
consumer's Fable project. No npm dependency — the GIS library is loaded at runtime from Google's
origin, which is what Google requires for the credential flow.
