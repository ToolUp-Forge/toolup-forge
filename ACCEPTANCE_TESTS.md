<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK) -->

# Acceptance tests

Operator-run verification procedures for behaviour the automated suites
cannot reach — anything needing a real identity provider, a real
browser, or two of them at once.

**What this file is not.** It is not a substitute for a test. Where a
property is assertable in-tree it belongs in an Expecto pack, and every
procedure below opens by naming the automated coverage that already
exists so the manual part stays as small as it can be. A procedure here
earns its place only by needing something a CI runner does not have: a
tenant at an external issuer, a browser that stores cookies, a second
tab.

**How to read a procedure.** Each has four parts, in this order:

| Part | What it is for |
|---|---|
| **Automated first** | What to run before touching a browser. If any of it is red, stop — the manual run will only confuse the diagnosis. |
| **Prerequisites** | Accounts, configuration and environment. Everything here is the operator's to arrange. |
| **Procedure** | Numbered steps. Commands are PowerShell 7. |
| **Pass criteria** | What must be true. A criterion is written so it can be read off an artefact, not judged. |

**Record the result.** A run that is not written down did not happen.
Note the SDK version, the date, the issuers exercised, and the pass /
fail of each criterion. Where a step produces a log excerpt, keep it —
the negative controls are the part a later reader will want.

---

## AT-1 — Cross-provider OIDC sign-in with cookie-authenticated SSE

**Covers:** Phase 6k.A (provider-agnostic JWT auth scaffolding:
`IAuthBridge`, `ClientConfig.AuthBridge`, the JWT-as-cookie write, and
`ServerConfig.SseAuthMode`), against the generic OIDC shell of
[Phases 744–748](docs/companions/auth-providers.md#oidc-presets).

**What it proves, in one sentence:** that a deployment's server-side
identity is the one the identity provider issued — not a client-minted
`localStorage` id the server took on trust — and that the SSE stream
authenticates on the same identity without the query-parameter
fallback.

That pairing is the whole point. Either half alone can look correct
while the deployment is wrong: the API can authenticate a real `sub`
while the SSE handshake quietly re-identifies the connection from
`?userId=`, in which case events are published to one scope and
subscribed on another and the user sees a chat that accepts messages
and never answers. The negative control in step 7 is what separates the
two.

**Why it needs an operator.** The in-tree packs cover the preset
shapes, the token classification, the validator refusals and the
handshake mechanics, all against a mock issuer. What they cannot cover
is a real IdP's `sub` reaching a real browser's cookie jar and coming
back on an `EventSource` request the browser — not our client code —
constructs.

### Automated first

Run these before arranging anything external. All are offline.

```powershell
# One build; the packs read built DLLs.
dotnet build ToolUp.Forge.sln

$pack = "src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll"

# The preset surface, including the `google` preset's two quirks
# (opaque access tokens, refresh via authorize extras).
dotnet $pack --filter "ToolUp.Platform.Tests.OidcClient.OidcPresets"

# Which token the session presents as bearer, per preset.
dotnet $pack --filter "ToolUp.Platform.Tests.PresetKind.defaultBearerToken"

# The preflight refusal that fires when SseAuthMode and the auth
# provider's TokenLocation disagree.
dotnet $pack --filter "ToolUp.Platform.Tests.Phase 6l.I"

# The SSE handshake's own mechanics.
dotnet $pack --filter "ToolUp.Platform.Tests.SSE handshake regression (Phase 6i.F)"
```

**Read the case count, not the exit code.** Expecto joins a test path
with `.`, and a filter that matches nothing prints `0 tests run …
Success!` and exits 0. If a line above reports zero cases, the filter is
wrong — not the code.

### Prerequisites

1. **Two OIDC issuers**, exercised in turn:
   - **Google** — an OAuth 2.0 Client ID and redirect URI, wired with
     `OidcPresets.google`. Chosen because its access tokens are always
     opaque, so the deployment's bearer is the `id_token`; it is the
     shape most likely to be mis-wired.
   - **Any non-Google OIDC issuer** — Okta, Keycloak, Auth0, Entra, a
     self-hosted provider — wired with `OidcPresets.generic` (or the
     matching preset). The point of the second issuer is that nothing
     in the pass criteria may depend on which one it is.
2. **A composed deployment** with AI chat mounted and dev endpoints on
   (`ServerConfig.EnableDevEndpoints = true`), reachable over **HTTPS**.
   HTTPS is not optional: the JWT cookie is written with the `Secure`
   flag on anything that is not plain-http dev, and a mixed setup will
   fail in a way that looks like an auth bug.
3. **An `IAuthBridge` implementation** installed via
   `ClientConfig.AuthBridge`, returning the issuer's JWT from whichever
   client SDK the deployment loads. The SDK ships the seam, not a
   vendor binding — see the note under Phase 6k.A.
4. **A browser with devtools**, and the ability to clear one cookie
   without clearing site storage.

### Procedure

1. **Start the server in cookie-required SSE mode, with the agent trace
   on.**

   ```powershell
   $env:TOOLUP_SSE_AUTH        = 'cookie'      # SseAuthMode.CookieRequired
   $env:TOOLUP_LOG_LEVEL       = 'Trace'
   $env:TOOLUP_TRACE_CATEGORIES = 'ai.agent'
   dotnet run --project <the deployment's server project> 2>&1 | Tee-Object -FilePath at1-server.log
   ```

   `TOOLUP_SSE_AUTH=cookie` is the switch under test. `ai.agent` is the
   trace category Phase 6k.B added; it is what makes step 4 a five-second
   read instead of an inference.

2. **Sign in through the first issuer** and complete the redirect back.

3. **Confirm the cookie exists and is the JWT.** In devtools →
   Application → Cookies, find `toolup-auth-token`. Its value must be a
   three-segment JWT. Decode the middle segment (any offline decoder;
   do not paste a live token into a web service) and note the `sub` and
   `iss` claims.

4. **Send one chat message**, then read the server log for the turn's
   identity fingerprint:

   ```powershell
   Select-String -Path at1-server.log -Pattern 'bgWork start' |
       Select-Object -Last 5
   ```

   The line carries `userId=<value>`. Compare `<value>` with the `sub`
   you decoded in step 3.

5. **Check the value is not a client-minted id.** A `localStorage`
   fallback id is a .NET `Guid` — 8-4-4-4-12 lowercase hex. Every real
   OIDC `sub` is issuer-shaped and none of them look like that (Google's
   is a ~21-digit decimal string). Mechanise the check rather than
   eyeballing it:

   ```powershell
   $observed = '<the userId from step 4>'
   if ($observed -match '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') {
       Write-Error 'FAIL — the server is using a client-minted Guid, not the JWT sub'
   } else {
       Write-Host "OK — server identity is issuer-shaped: $observed"
   }
   ```

6. **Confirm the SSE stream is subscribed on the same identity.** Open
   `/dev/sse-trace`. The **Registered scopes** map must contain exactly
   the `sub` from step 3, and the recent **Broadcasts** for this turn
   must show `Dropped = false` against that same scope.

   A `Dropped = true` entry here is the userId/scopeId mismatch this
   panel exists to make instant: the turn published to a scope nobody is
   listening on.

7. **Negative control — remove the cookie and prove the handshake is
   refused.** This is the step that distinguishes "cookie auth works"
   from "cookie auth is not actually being required".

   Delete the `toolup-auth-token` cookie in devtools, leaving
   `localStorage` untouched, and reload.

   The `EventSource` request to `/api/ai/events` must be **refused**.
   It must NOT succeed by falling back to `?userId=` — the
   `localStorage` id is still present, so a fallback would silently
   re-establish the stream on an unauthenticated identity, which is
   exactly the posture `CookieRequired` exists to forbid.

8. **Restore the cookie** (sign in again) and confirm the stream
   re-establishes and a fresh message streams a reply end to end.

9. **Repeat steps 1–8 against the second issuer**, changing nothing but
   the preset and the credentials.

10. **Control run — confirm the fallback mode still works.** Restart
    with `TOOLUP_SSE_AUTH=fallback` (or unset) and repeat step 8. This
    guards the other direction: an existing deployment that has not
    opted in must be byte-for-byte unaffected (GP 11).

### Pass criteria

| # | Criterion | Read it off |
|---|---|---|
| 1 | The server-side `userId` equals the JWT `sub` | steps 3 + 4 |
| 2 | The server-side `userId` is not a `Guid` | step 5, mechanised |
| 3 | The SSE scope equals that same identity, with `Dropped = false` | `/dev/sse-trace`, step 6 |
| 4 | With the cookie removed, the SSE handshake is refused and does **not** fall back to `?userId=` | step 7 |
| 5 | Criteria 1–4 hold identically for a non-Google issuer | step 9 |
| 6 | `TOOLUP_SSE_AUTH` unset / `fallback` still streams | step 10 |

Criterion 4 is the one to re-read if a run is ambiguous. The others can
all be satisfied by a deployment that is authenticating correctly and
enforcing nothing.

### What is not covered here

- **Token refresh across an expiry boundary.** The bridge's 60 s
  refresh loop mirrors a new JWT into the cookie, but observing a real
  expiry means waiting out the issuer's lifetime; run it as a separate,
  longer session when a deployment's token lifetime is short enough to
  make it practical.
- **Multi-instance deployments.** The cancellation and client-tool
  dispatch registries are per-process, so a cancel POST must reach the
  instance holding the stream. That is a deployment-topology property,
  not an auth one, and the preflight validator already warns on it.
