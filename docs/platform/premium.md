# Premium tier (operator-granted)

The Platform ships an opt-in **premium-claim substrate** so consumer apps offering an anonymous-first public utility can selectively grant a "premium" status to specific logged-in users and gate features on it. The substrate is **narrow by design**: it reads a `premium` claim from the active auth provider, exposes a server-side gate (`PremiumGate.requirePremium`), a client-side hook (`usePremium`), and an operator-grant API (`POST` / `DELETE` on `/api/_platform/users/{userId}/premium`, Platform-Admin gated). It carries no opinion about billing, self-serve signup, multi-tier pricing, or per-feature metering — those are out of scope for this phase.

## When to use

| Deployment shape | Default | When to flip on |
|---|---|---|
| Anonymous-only public utility (calculators, viewers, converters) | `AnonymousFirst` | Use as-is; no premium tier — every user reads as `NotPremium`. |
| Anonymous-first with operator-granted premium for selected users | `AnonymousFirst` | The shape this phase exists for. Anonymous-mode is the default for new visitors; the operator grants premium to specific users via PlatformAdmin. |
| Paid-default SaaS (every user pays) | — | Don't use this substrate. The `IUserClaims` seam still works, but the "operator-granted" framing fits poorly; a future self-serve / billing substrate will land for that shape. |
| Multi-tier pricing (Bronze / Silver / Gold) | — | This phase ships binary `Premium | NotPremium`. Multi-tier composes via the feature-flag substrate (see "Composition with feature flags" below) for richer cases. |

The default `ClientConfig.PremiumModel = AnonymousFirst` preserves the SDK's pre-Phase-62 behaviour byte-for-byte: every user reads as `NotPremium`, `requirePremium` denies cleanly, and `usePremium` returns `NotPremium` without ever calling the server. Deployments that don't opt in pay zero overhead.

## Substrate shape

Six files participate. The minimal set:

- **`Shared/SDK.Shared.fs`** — `PremiumStatus` DU (`NotPremium | Premium of grantedAt: DateTimeOffset * grantedBy: string * reason: string option`) and `PremiumModel` DU (`AnonymousFirst`, the v1 shipping case). `ClientConfig.PremiumModel` defaults to `AnonymousFirst`.
- **[`Server/IUserClaims.fs`](../../src/ToolUp.Platform.Server/Server/IUserClaims.fs)** — read / write surface for per-user metadata claims. Four methods: `GetPremiumStatus`, `ListPremiumUsers`, `GrantPremium`, `RevokePremium`. Default `NoOpUserClaims` returns `NotPremium` everywhere and records grants without writing through to any provider (audit trail captures the operator's intent regardless). The six-rule portability audit lives at the top of the file; the matching contract pack is [`IUserClaimsContract`](../../src/ToolUp.Platform.Tests/Contracts/IUserClaimsContract.fs).
- **[`Server/Premium/PremiumGate.fs`](../../src/ToolUp.Platform.Server/Server/Premium/PremiumGate.fs)** — server-side guard helpers: `requirePremium` returns `Async<Result<unit, string>>` so handlers can render a structured 402-shaped response without intercepting an exception; `ifPremium` runs a body only when premium.
- **[`Server/Premium/GrantPremiumApiHandler.fs`](../../src/ToolUp.Platform.Server/Server/Premium/GrantPremiumApiHandler.fs)** — Giraffe routes (`POST` / `DELETE` on `/api/_platform/users/{userId}/premium`) gated by `IPlatformAdminStore.IsPlatformAdmin`. Emits `PremiumGranted` / `PremiumRevoked` audit events on the success path.
- **[`Client/Premium/PremiumHook.fs`](../../src/ToolUp.Platform.Client/Client/Premium/PremiumHook.fs)** — `usePremium ()` React hook. Fetches `/api/_platform/users/me/premium-status` on first render and returns `(currentStatus, refresh)`.
- **[`Server/FeatureFlagSources.fs`](../../src/ToolUp.Platform.Server/Server/FeatureFlagSources.fs)** — additive flag-source registry: register a flag key as `PremiumOnly` and `FlagEvaluator.createWithSources` short-circuits non-premium evaluators to `false`. Composable with the existing scope walk — see "Composition with feature flags" below.

## Minimum wiring

```fsharp
// Server.fs — wire a provider-specific IUserClaims into compose:
open ToolUp.Platform

[<EntryPoint>]
let main _ =
    ServerApp.empty
    |> ServerApp.withConfig { ServerConfig.defaults with Port = 5000 }
    // Default IUserClaims is NoOpUserClaims — every user reads as
    // NotPremium. A provider-backed companion swaps this via DI.
    |> ServerApp.run
```

```fsharp
// Client.fs — opt in to the AnonymousFirst premium model:
open ToolUp.Platform

let clientConfig = {
    ClientConfig.defaults with
        PremiumModel = AnonymousFirst
}
```

The default `ClientConfig.PremiumModel = AnonymousFirst` is already in effect, so the explicit field assignment above is only needed when you want the doc-comment in-place to flag your intent.

## Gating a server-side handler

```fsharp
open ToolUp.Platform
open Giraffe

let handler : HttpHandler =
    fun next ctx -> task {
        // Resolve AccessContext, IUserClaims via DI (per the
        // composition-root pattern in docs/platform/composition-roots.md).
        let userClaims = ctx.GetService<IUserClaims>()
        let accessCtx = AccessContextResolver.resolve ctx

        match! PremiumGate.requirePremium userClaims accessCtx with
        | Ok () ->
            // Authoritative premium-only work goes here.
            return! json { Result = "premium-only payload" } next ctx
        | Error message ->
            ctx.SetStatusCode 402
            return! json {| Error = message |} next ctx
    }
```

`requirePremium` returns `Result<unit, string>` rather than throwing — the caller renders the 402 (or whatever shape the consumer prefers) without an exception cascade through the request pipeline.

## Gating a client-side surface

```fsharp
open Feliz
open ToolUp.Platform

[<ReactComponent>]
let PremiumOnlyChart () =
    let status, refresh = usePremium ()

    match status with
    | Premium _ ->
        // The premium-only view goes here.
        Html.div [ Html.text "Premium analysis surface" ]
    | NotPremium ->
        // Empty state. Consumers choose the UX — a disabled-with-
        // upgrade-prompt button, a "request premium" CTA, or a
        // simple Html.none. The SDK provides the hook; the consumer
        // owns the messaging.
        Html.div [ Html.text "Sign in with a premium account to view this." ]
```

The hook subscribes once on mount and caches the result; call `refresh ()` after a sign-in / sign-out transition that the consumer detects out-of-band. (Reactive auth-context binding is a follow-up — see the Phase 62 task list.)

## Composition with feature flags

Premium gating composes with the SDK's feature-flag substrate (see [`events.md`](events.md) for the flag-store lineage). A flag can be:

1. **Enabled for all** — declared default `Bool true`, no overrides, no `PremiumOnly` source.
2. **Enabled for some via the scope walk** — declared default `Bool false`, with per-User / per-Team / Platform-scope overrides flipping it on per the existing scope precedence.
3. **Enabled only for premium users** — same as (1) or (2), but registered in the `FeatureFlagSourceRegistry` as `PremiumOnly`. Anonymous and non-premium logged-in users see the flag as `false` regardless of any scope override; premium users fall through to the normal scope walk.

The three are composable: a `PremiumOnly` flag can still be turned off for a specific premium user via a User-scope override of `Bool false` (the floor only blocks the `true` direction).

```fsharp
// Compose-root wire:
let premiumGatedKeys = [ "analytics.advanced-export"; "viewer.history-mode" ]

let sources = FeatureFlagSourceRegistry.premiumOnly premiumGatedKeys

let evaluator =
    FlagEvaluator.createWithSources
        flagStore
        declaredFlags
        sources
        userClaims
        (Some logger)
```

A flag key not in the registry uses the standard scope walk — `createWithSources` with an empty registry is byte-for-byte equivalent to `FlagEvaluator.create`. Deployments that don't use premium gating need not call `createWithSources` at all.

## Operator-grant flow

The grant flow rides on the SDK's Platform-Admin role substrate. The operator must hold `PlatformRole.PlatformAdmin` (verified via `IPlatformAdminStore.IsPlatformAdmin`); without it, the grant endpoint returns 403.

`POST /api/_platform/users/{userId}/premium` with body `{ Reason: "..." }` writes through to the active `IUserClaims` and emits a `PremiumGranted` audit event under the `_platform.users` scope. `DELETE` on the same path emits `PremiumRevoked`. Both events carry grantor + grantee + reason for the GDPR DSAR pathway.

A typical admin-UI surface for this lives in the PlatformAdmin premium-user list widget (Phase 61); the widget calls `ListPremiumUsers()` to enumerate current premium holders and exposes a single grant / revoke button per row.

## Auth-provider note

Platform-Admin role on the SDK side is **distinct** from the auth provider's own admin concept. The grant flow uses ToolUp's role-gating to decide who may call the endpoint; the active `IUserClaims` implementation is responsible for writing through to the provider using whatever credentials it was configured with (Clerk's admin API key, OIDC management-API client credentials, etc.). The Platform never inherits the operator's user-session credentials when writing through.

Per-provider configuration walkthroughs live in [`auth-providers.md`](companions/auth-providers.md) (with a paragraph each on the Clerk and OIDC paths' premium write-through expectations).

## Worked example — public-utility consumer with a premium analysis surface

A representative public-utility consumer: an anonymous-first calculator app. Default mode is `AnonymousSession` for every visitor; the calculator works for everyone. The consumer wants to layer a premium-only "year-on-year comparative analysis" surface accessible only to operator-granted premium accounts.

Steps:

1. **Wire `ClientConfig.PremiumModel = AnonymousFirst`** in the client composition root. (Default — no change needed unless you want the field to flag intent.)
2. **Register a feature flag** declaring the comparative-analysis surface gate: `{ Key = "calculator.comparative-analysis"; DefaultValue = FlagValue.Bool true; ... }`. The `Bool true` default means "enabled for premium users by default" — flip to `Bool false` if you want to keep it dark behind an explicit per-user opt-in.
3. **Register the key in `FeatureFlagSourceRegistry.premiumOnly`** so `FlagEvaluator.createWithSources` returns `false` for anonymous / non-premium subjects.
4. **Gate the server-side route** that produces the comparative-analysis payload: `match! PremiumGate.requirePremium userClaims ctx with | Ok () -> ... | Error _ -> 402`.
5. **Gate the client-side view** with `usePremium ()` so non-premium users see a clean upgrade prompt rather than a 402 error.
6. **Grant premium** via the PlatformAdmin premium-user list widget. The operator adds the user, the grant endpoint emits `PremiumGranted`, and the next time that user's auth context refreshes, `usePremium` returns `Premium _` and the comparative-analysis surface renders.

End state: anonymous visitors and free-tier logged-in users see the standard calculator; operator-granted premium users see the calculator plus the comparative-analysis surface. No code path changes per-user; the gate is uniformly applied at three layers (server route, feature flag evaluator, client view).

## Out of scope (today)

- **Self-serve premium signup + billing.** The grant flow is operator-driven; consumers wanting Stripe / billing integration plug into the SDK at a different seam.
- **Multi-tier premium (Bronze / Silver / Gold).** Binary `Premium | NotPremium`. Multi-tier composes via the feature-flag substrate — a flag declared per tier with the source registry gating each appropriately.
- **Per-feature pricing / usage metering.** Composes with the existing usage-metering substrate (see [`events.md`](events.md)) when a paid-default wave lands.
- **Per-team premium status.** Per-user only in v1. Multi-user teams needing per-team upgrade use the Team substrate's existing per-team config flags.
