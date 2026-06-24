# Migration — Wave 19: unverified auth-provider startup gate (capability, not type)

**Status:** **source-breaking interface widen** to `IAuthProvider` (a new abstract member, no default) + a fail-closed broadening of the existing `header-auth-mode` startup validator. In-tree first-party providers are updated and ship together. **External `IAuthProvider` implementers must add one member** (see Consumer action). The runtime behaviour change is fail-closed: an auth-requiring deployment whose composed provider does not affirmatively report cryptographic verification now refuses startup unless the existing escape hatch is set.

## What changes

`IAuthProvider` gains:

```fsharp
abstract IsCryptographicallyVerified: bool
```

The `header-auth-mode` config validator (`HeaderAuthProviderModeValidator`) previously identified the spoofable provider by exact type:

```fsharp
authProvider.GetType() = typeof<HeaderAuthProvider.HeaderAuthProvider>
```

A consumer subclass, decorator/wrapper, or hand-rolled header-trusting provider evaded that check and booted with spoofable auth in a production mode. The validator now reads the capability instead:

```fsharp
not authProvider.IsCryptographicallyVerified
&& not config.AcceptHeaderAuthWhenAuthRequired
```

so **any** provider that does not affirmatively assert verification is refused in an auth-requiring surface — fail-closed by capability (Auth-core audit, Mode-gating Finding 3).

First-party providers are wired accordingly: OIDC, Entra (delegates to its inner OIDC provider), and StaticJwt report `true`; `HeaderAuthProvider` reports `false`. The escape hatch `ServerConfig.AcceptHeaderAuthWhenAuthRequired` (env `TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE` — the field was renamed but the env-var name retains the `IN_AUTH_MODE` suffix; see `docs/operations/env-vars.md`) is unchanged and now covers *any* unverified provider behind a verified-identity proxy, not just `HeaderAuthProvider`.

## Consumer action

- **A deployment on a first-party provider (OIDC / Entra / StaticJwt / Clerk-hosted) — no action.** These report `true`; startup is unchanged.
- **A deployment on `HeaderAuthProvider` in an auth-requiring mode** already refused startup before this change (the old type check caught it). Unchanged: configure a verifying provider, or set the escape hatch behind an mTLS proxy.
- **An external / custom `IAuthProvider` implementation — source-breaking: add the member.**
  - If your provider proves identity cryptographically (verified JWT / OIDC / mTLS / token introspection), return `true`:
    ```fsharp
    member _.IsCryptographicallyVerified = true
    ```
  - If your provider trusts an unauthenticated request header or query param, return `false` and either run behind a verified-identity proxy with `AcceptHeaderAuthWhenAuthRequired = true`, or migrate to a verifying provider. A custom provider that omits the member no longer compiles (there is deliberately no default — identity verification must not be acquired by omission).
  - A decorator over another provider should delegate: `member _.IsCryptographicallyVerified = inner.IsCryptographicallyVerified`.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — `HeaderAuthProviderModeValidatorTests.fs`: a verified provider (StaticJwt) in Individual mode boots; `HeaderAuthProvider` refuses (message names the type + the `IsCryptographicallyVerified` capability + the escape hatch); an **unverified custom provider that is not `HeaderAuthProvider` by type** also refuses (the anti-evasion case); the escape hatch passes for any unverified provider; Anonymous mode is unaffected.
- `cd samples/MinimalClient && dotnet fable -o output --noCache` — the Core interface widen transpiles clean (no Fable implementer of `IAuthProvider`).

## Rollback

Revert the validator predicate to the `GetType() = typeof<HeaderAuthProvider>` check and remove the `IsCryptographicallyVerified` member from `IAuthProvider` and its five in-tree implementers. Rolling back re-opens the evasion: a non-`HeaderAuthProvider` header-trusting provider would again boot in a production auth mode with spoofable identity — roll back only with that exposure understood.
