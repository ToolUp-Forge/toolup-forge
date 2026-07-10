# Migration — Phase 208: codified threat-lens security-regression suite

**Status:** **test-tier only — no public runtime surface, no behaviour change (GP 11/13).** No
consumer action. This migration doc exists so the SDK-adoption matrix carries a row; every consumer
cell is ⛔ N-A. A consumer that never runs the suite is byte-for-byte unchanged and pays nothing.

## What changes

The six manual audit lenses that established the Epoch-1 security posture become a single recurring,
automated regression pack, so the *next* regression over the auth seams is caught by
`Build.fsproj -- VerifyAll` rather than by the next human audit.

- **`ToolUp.Platform.Tests/InProcess/ThreatLensRegressionSuite.fs`** (NEW) — the registered suite
  (`ThreatLensRegressionSuite`), wired into `Program.fs`'s `allTests` (Expecto only runs the supplied
  list). Every symbol under test is reached through the shipped **public** surface — no production
  source is touched. Each lens asserts **both** directions (the secure path holds *and* the insecure
  variant is rejected), so reverting a production control flips a case from green to red:
  - **Lens 1 — JWT/JWKS crypto** (`OidcAuthProvider.Jwt` / `.Jwks`, via `IAuthProvider.ValidateRequest`
    over a self-contained RSA-signed-JWT + stub-JWKS fixture): a well-formed RS256 token verified
    against the matching JWKS is accepted; a tampered signature, a wrong audience, an expired token,
    and an unknown/forged kid are each rejected.
  - **Lens 2 — mode-gating / dev-bypass** (`HeaderAuthProviderModeValidator`,
    `AutoBootstrapDevAdminModeValidator`): a spoofable dev header-auth provider and a leaked
    `AutoBootstrapDevAdmin` field are each refused (`ValidationResult.Error`) in an auth-requiring
    (production) mode; the same config is tolerated in anonymous mode. The dev-admin opt-in env var is
    cleared around its case so the production-refusal path is deterministic in any CI environment.
  - **Lens 3 — tenant scope isolation** (`IdentitySanitiser.sanitiseScopeId`, the Phase 131 store-seam
    sanitiser): a benign GUID scope id passes unchanged; forward-slash / backslash traversal, an
    embedded NUL byte, and a cross-scope reserved-path id are each rejected before they can reach
    another tenant's key space.
  - **Lens 4 — authorization / RBAC fail-closed** (`AuthClassifier.evaluate`,
    `PlatformAdminAuthorizationMiddleware`, Phase 132): an UNCLASSIFIED method denies even a genuine
    admin (fail-closed by construction); the platform-admin path-prefix backstop 403s a non-admin
    before the handler runs and passes a stamped `PlatformAdmin` through.
  - **Lens 5 — request-edge auth** (`SseAuthModeValidator`, `ShareTokenAuth.tryReadToken`,
    `PeerBearerAuthMiddleware`, `AnonymousSessionBinding`, `Csrf`): a query-param SSE fallback is
    refused in a production mode while cookie auth is accepted; a share token is read from its header
    and absent when unpresented; a bearer header parses and a constant-time secret compare holds, while
    a non-bearer scheme, a mismatched secret, a traversal `X-Peer-Name`, and a missing peer-name header
    are rejected; an anonymous-session seal verifies only for its own session (replay + tamper
    rejected); a CSRF double-submit with a missing or unpaired token fails.
  - **Lens 6 — session/OAuth lifecycle** (`OAuthCrypto`, `InMemoryOAuthStateStore`,
    `IOAuthCredentialFlow`): the RFC 7636 S256 verifier→challenge vector is honoured and a substituted
    verifier cannot reproduce a bound challenge; a PKCE-enforcing exchange redeems a code *with* its
    verifier and refuses a verifier-less (intercepted) code; a single-use state token consumes once and
    the replay is refused.
- **`reverted-control proof`** (a sub-list of the same suite) — pairs the real production decision
  against a deliberately-reverted stand-in (a control that skips the check) and asserts the stand-in
  would fail the exact assertion the shipped code passes. This makes the acceptance self-evident:
  e.g. forcing the dev-admin bypass on in production mode, or an id sanitiser that echoes its input,
  fails the matching lens — proving the suite catches regressions, not just passes.

The middleware / validator / provider surfaces under test are exercised but **unchanged**.

## Consumer action

None. No package runtime surface changed; no recompile required beyond a normal SDK bump. The suite is
additive test-tier code in `ToolUp.Platform.Tests` — it ships in no consumer package.

## Verification

- `dotnet build ToolUp.Forge.sln` clean; `dotnet run --project Build.fsproj -- Pack` green.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `Platform` pack (which now includes
  `ThreatLensRegressionSuite`) is green; the suite gates the build.
- Reverted-control acceptance: the `reverted-control proof` cases demonstrate that a reverted security
  control fails the matching lens — the suite is proven to catch regressions, not merely to pass.

## Rollback

Remove the `<Compile Include="InProcess\ThreatLensRegressionSuite.fs" />` entry in
`ToolUp.Platform.Tests.fsproj` + the `ThreatLensRegressionSuite.tests` registration in `Program.fs`,
and delete the file. No production code is touched, so rollback is inert — it only drops the recurring
regression gate over the six auth-seam threat lenses.
