# Phase 62 — Premium-claim recognition (consumer migration)

**What changes.** SDK ships the anonymous-first premium model: `IUserClaims` server-side seam (`GetPremiumStatus / ListPremiumUsers / GrantPremium / RevokePremium`) + `NoOpUserClaims` default + `PremiumGate` server-side guard helpers (`requirePremium` / `ifPremium`) + `usePremium` Feliz hook + `/api/_platform/users/{userId}/premium` operator grant/revoke endpoints + `/api/_platform/users/me/premium-status` open read endpoint + `PremiumGranted / PremiumRevoked` audit cases + `FeatureFlagSourceRegistry` with a `PremiumOnly` source consumed by `FlagEvaluator.createWithSources` + `IUserClaimsContract` portable test pack.

Conceptual overview lives in [`docs/platform/premium.md`](../platform/premium.md); this file is the consumer-side migration checklist.

**Scope.** Opt-in. Default `ClientConfig.PremiumModel = AnonymousFirst` + the default `NoOpUserClaims` DI registration preserve today's behaviour byte-for-byte — `usePremium` returns `NotPremium`, `requirePremium` denies, and writes succeed without touching any provider.

## Diff to apply

Consumers gating module surfaces on premium status:

```fsharp
// Server-side handler — gate a premium-only endpoint:
open ToolUp.Platform.Premium

let handler =
    fun next ctx -> task {
        match! PremiumGate.requirePremium ctx |> Async.StartAsTask with
        | Error msg ->
            ctx.Response.StatusCode <- 402  // or 403, consumer choice
            return! ctx.WriteTextAsync msg
        | Ok () ->
            // ... render premium response
    }
```

```fsharp
// Client-side module — gate UI on premium:
open ToolUp.Platform.Premium.PremiumHook

[<ReactComponent>]
let MyView () =
    let status, _refresh = usePremium ()
    match status with
    | Premium _ -> Html.div [ ... premium content ... ]
    | NotPremium -> Html.div [ ... upgrade prompt ... ]
```

Consumers swapping in a provider-specific `IUserClaims` (Clerk / OIDC / custom):

```fsharp
// Server.fs composition — DI override:
ServerApp.empty
|> ServerApp.withServiceConfig (fun services ->
    services.AddSingleton<IUserClaims>(MyClerkUserClaims(clerkApiKey)) |> ignore)
```

The provider impl ships as a separate companion package; this phase only delivers the seam + the NoOpUserClaims default.

Consumers composing premium gating into the feature-flag substrate:

```fsharp
// Server.fs composition — register PremiumOnly flag keys + use the
// premium-aware evaluator overload:
open ToolUp.Platform

let premiumGatedKeys = [
    "calculator.comparative-analysis"
    "viewer.history-mode"
]

let sources = FeatureFlagSourceRegistry.premiumOnly premiumGatedKeys

let evaluator =
    FlagEvaluator.createWithSources
        flagStore
        declaredFlags
        sources
        userClaims
        (Some logger)
```

Keys not registered fall through to the standard `User → Team → Platform → declared default` scope walk. `createWithSources` with `FeatureFlagSourceRegistry.empty` is byte-for-byte equivalent to `FlagEvaluator.create`. See [`docs/platform/premium.md`](../platform/premium.md) "Composition with feature flags" for the three composable shapes (enabled-for-all / enabled-for-some / enabled-only-for-premium).

External `IUserClaims` impls (Clerk / OIDC / custom) bind the SDK's portable contract pack from their own test project:

```fsharp
// In your impl's test project:
open ToolUp.Platform.Tests.Contracts

let myImplTests =
    let factory () = MyClerkUserClaims(testApiKey) :> IUserClaims
    // `persists = true` exercises the grant + read round-trip cases.
    IUserClaimsContract.tests "MyClerkUserClaims" true factory
```

The contract pack ships two binding factories out of the box (`NoOpUserClaims` non-persisting + `InMemoryUserClaims` persisting) in `ToolUp.Platform.Tests/InProcess/UserClaimsTests.fs`. Per-provider impls add a third binding without modifying the contract pack itself.

## Verification

1. `dotnet build` clean; Fable transpile clean.
2. `GET /api/_platform/users/me/premium-status` with anonymous caller → 200 with `NotPremium` body.
3. `POST /api/_platform/users/{userId}/premium` from non-Platform-Admin → 403.
4. `POST /api/_platform/users/{userId}/premium` from Platform-Admin (default `NoOpUserClaims`) → 204; audit log shows `PremiumGranted` event under `_platform` scope.
5. `usePremium ()` Feliz hook → returns `(NotPremium, refresh)` until a real `IUserClaims` is wired + a logged-in user has the claim set.
6. `IUserClaimsContract` tests — `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` runs the portable pack against the SDK-shipped `NoOpUserClaims` + `InMemoryUserClaims` bindings (passing exit code).
7. `FlagEvaluator.createWithSources` with `FeatureFlagSourceRegistry.empty` produces identical results to `FlagEvaluator.create` for the existing FlagEvaluatorTests scenarios — premium gating is purely additive.

## Rollback

Revert any premium-gated handler additions; restore module surfaces to unconditional renders. The audit cases (`PremiumGranted` / `PremiumRevoked`) are additive — no rollback needed; if no consumer invokes the endpoints, no events flow.

## Consumers

The substrate is **N-A** for any consumer that does not layer a premium tier on top of anonymous traffic. Public-utility apps wanting to flip from anonymous-only to anonymous-first-with-operator-granted-premium adopt the seam by composing `PremiumGate` + `usePremium` + (optionally) `FeatureFlagSourceRegistry.premiumOnly`.

## Deferred follow-up

- Clerk-specific `IUserClaims` companion (`ToolUp.AuthProviders.ClerkClaims`) — writes through to Clerk's admin API. Companion's own test project will bind `IUserClaimsContract.tests "ClerkUserClaims" true factory` against a mock HttpClient.
- OIDC-specific `IUserClaims` companion — reads a `toolup.premium` custom claim. Same contract-pack binding pattern.
- `usePremium` auth-context-change subscription so the hook re-fetches when sign-in/out happens without a manual `refresh ()` call.

These ship as separate phases / companions; this migration covers the substrate seam, the feature-flag composition surface, and the portable contract pack.
