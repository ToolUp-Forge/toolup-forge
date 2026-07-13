# ToolUp.AuthProviders.Passkey.Client

Client-side WebAuthn / passkey sign-in UI for `ToolUp.Platform` — the browser half of the passkey flow. It drives the browser-native `navigator.credentials` API against the [server companion](../Passkey/README.md)'s ceremony endpoints, with **zero npm dependencies** (WebCrypto-era browser primitives via `[<Emit>]`, mirroring the `OidcClient` precedent).

This is a Fable / Feliz client-tier package: it ships its `.fs` source under `fable/` in the nupkg and is compiled as part of the consuming app's Fable build. Pair it with the server-side `ToolUp.AuthProviders.Passkey` companion, which runs the ceremonies and mints the session token this UI stores.

## What it provides

- **`PasskeyRegister.handler`** — the companion-exported `AuthUIHandler` value (tag `"passkey"`). Adding it to `ClientConfig.Handlers.AuthUIHandlers` pulls the module into the Fable import graph and wires the shell's `AuthUIProvider` to dispatch passkey sign-in — a pure value export, no module-load side effect or `init ()` anchor.
- **`PasskeyShell`** — the shell wrapper handed to the SDK via that handler. It holds `PasskeyAuthState` (`Checking | SignedIn | SignedOut | Failed`) in React-local state (no Elmish model pollution) and renders the sign-in / register / loading / error screens. There is **no password field anywhere** — passwordless is the point.
- **`PasskeyClient`** — the ceremony orchestration: `register`, `signIn`, `hasSession`, `signOut`, and an `isSupported ()` feature probe. On a successful ceremony the minted platform session JWT is stored via `UserSession.setAuthToken`, exactly as a bearer token obtained by any other means.

## How a consumer enables it

Configure the client `AuthUI` mode and register the handler value in `ClientConfig`:

```fsharp
open ToolUp.Platform
open ToolUp.AuthProviders

let clientConfig =
    { ClientConfig.defaults with
        AuthUI = PasskeyAuthUI PasskeyUIConfig.defaults
        Handlers =
            { ClientHandlerRegistry.empty with
                AuthUIHandlers = [ PasskeyRegister.handler ] } }
```

`PasskeyUIConfig` is small:

| Field | Meaning | Default |
|---|---|---|
| `ApiBase` | Base path for the server ceremony endpoints — the UI posts to `{ApiBase}/register/*` and `{ApiBase}/assert/*`. | `/api/passkey` |
| `AllowRegistration` | Whether the sign-in screen offers a "Register a passkey" affordance (plus a bootstrap-token field for first-time setup). Registration is still gated server-side; this only controls the UI. | `true` |

`PasskeyUIConfig.defaults` matches the server companion's default route mounting, so an app that composes `PasskeyServerApp` with SDK defaults needs no `ApiBase` override.

## The ceremony flow

`register` and `signIn` share one orchestration:

1. `POST {ApiBase}/register/begin` (or `/assert/begin`) → the server's Fido2 options JSON plus a `challengeId`.
2. Convert the options' base64url buffers (`challenge`, `user.id`, `excludeCredentials[].id`) to the `Uint8Array` form the browser API requires, then call `navigator.credentials.create` (registration) or `.get` (assertion).
3. Serialise the returned `PublicKeyCredential` into the attestation / assertion wire shape (base64url buffers).
4. `POST {completePath}?challenge={challengeId}` with that body → the minted session JWT → `UserSession.setAuthToken`.

The base64url ⇄ `ArrayBuffer` conversions and the response serialisation are the only `[<Emit>]` shims — they operate on the live `ArrayBuffer` / `PublicKeyCredential` objects the WebAuthn API hands back; everything above them is ordinary F# orchestration. A user-cancelled ceremony, an absent authenticator, or a timeout surfaces as a non-probing `Failed` message.

`signIn` accepts an empty username for a discoverable-credential (usernameless) flow. `signOut` clears the stored token; the `UserMenu` header button pairs it with a page reload.

## See also

- [`../Passkey/README.md`](../Passkey/README.md) — the server companion (`IAuthProvider` + ceremony routes + session-token issuance) this UI drives.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
