# ToolUp AI Providers

`IAIProvider` implementations for the `ToolUp.AI` companion. Each provider is its own NuGet package, BYOK-capable, and resolves API keys per-call from the injected `ISecretStore` rather than from environment variables.

## Shipped providers

| Package | Vendor | Notes |
|---|---|---|
| [`ToolUp.AIProviders.Claude`](Claude/) | Anthropic Claude | Messages endpoint, prompt caching, multi-turn tool calling, SSE streaming. Client-side glyph ships separately as `ToolUp.AIProviders.Claude.Client`. |
| [`ToolUp.AIProviders.OpenAI`](OpenAI/) | OpenAI | `chat.completions`, multi-turn tool calling, SSE streaming with `stream_options.include_usage = true` for accurate token reporting. |

Each subfolder packs a `Server` package and (where applicable) a separate `Client` package. The full provider-authoring contract lives in [`docs/ai/extending.md`](../../docs/ai/extending.md); use this directory as the example for new providers.

## Rate-limiting outbound calls — the `IRateLimiter` pattern (Phase 9v)

AI vendors enforce tier-scoped RPM and TPM ceilings — Anthropic per-organisation throughput, OpenAI tier-based RPM, etc. — that apply across an entire deployment rather than per-tenant. Providers gate every outbound request through the SDK's `IRateLimiter` (rather than authoring per-provider throttles) so:

- The same primitive backs every connector + AI provider — observability (`toolup.ratelimit.waited_total`, `wait_ms` histogram, `refused_total`) is uniform across the deployment.
- The pattern is portable: a Redis-backed `IRateLimiter` (Phase 9c half-2) replaces the in-process default at compose time without any provider-code change, which is what unlocks correct quota accounting in load-balanced multi-instance deployments.
- The default `NoRateLimiter` short-circuits to `Proceed`, so a deployment that hasn't opted in pays nothing.

### Descriptor — declared at compose time

Each tier-quota the provider consumes is declared once via `ServerApp.withRateLimitDescriptor`:

```fsharp
let claudeRateLimit: RateLimitDescriptor = {
    // String label — must match `RateLimitKey.Provider` at the call site.
    Provider = "anthropic-claude"
    // Tier-2 example: 1000 requests / minute, soft-ceilinged at 95%
    // inside the in-process limiter to leave headroom for retries.
    ShortWindow = (1000, TimeSpan.FromMinutes 1.0)
    // No long-window quota declared — Anthropic enforces a separate
    // monthly spend ceiling we don't gate here.
    LongWindow = None
    // PerProvider — organisation-tier limits apply across the whole
    // deployment; one tenant can't get its own bucket because the
    // upstream doesn't either.
    FairnessMode = PerProvider
}

let app =
    AIServerApp.create config dataTypes
    |> AIServerApp.withRateLimitDescriptor claudeRateLimit
    // …other companion wiring…
```

Models with materially different RPM (Opus vs. Haiku vs. Sonnet) use distinct `Provider` strings (`"anthropic-claude-opus-4"`, `"anthropic-claude-haiku-4-5"`) so each gets an independent window. Don't conflate them into one descriptor — the upstream's limit is the floor.

### Emission — `Wait` before every outbound request

Inside the provider's `SendMessage` (and `SendMessageStreaming`), call `Wait` before the HTTP send. The signature is `Wait: RateLimitKey -> Async<RateLimitDecision>`; the limiter holds the call inside `Wait` until the short window opens up (silent `Proceed` / `DelayedBy waited`) or returns `Refused reason` if a declared long-window quota is exhausted.

```fsharp
type MyVendorProvider(apiKey, model, httpClient: HttpClient, rateLimiter: IRateLimiter) =

    let rateLimitKey (scopeId: string) : RateLimitKey = {
        ScopeId = scopeId
        Provider = "myvendor"        // matches the descriptor above
        SubKey = Some model          // optional per-model sub-bucket
    }

    interface IAIProvider with
        member _.SendMessage(req) = async {
            // Phase 9v — gate every outbound call. `Refused` is data,
            // not an exception; surface it as a provider-level error
            // the agent loop can handle rather than letting the
            // request hit the wire and 429-bounce.
            match! rateLimiter.Wait(rateLimitKey req.ScopeId) with
            | Refused reason ->
                return AIProviderResponse.error (sprintf "Rate-limit refused: %s" reason)
            | Proceed
            | DelayedBy _ ->
                let wire = translateRequest req
                let! response = httpClient.PostAsJsonAsync(endpoint, wire) |> Async.AwaitTask
                return translateResponse response
        }
```

`DelayedBy waited` is purely observational — the limiter has already held the call inside `Wait`; by the time the match runs, the window has slack. The audit log and `IMetricsSink` capture the wait time automatically (waits past `ServerConfig.SlowRateLimitThreshold` emit a `RateLimitWaited` audit row).

### How the provider receives the limiter

The factory wires it in at compose time. Builders take `IRateLimiter` as a constructor parameter, the same way they take `ISecretStore`:

```fsharp
let builder: AIProviderBuilder = {
    Descriptor = descriptor
    Build = fun apiKey model -> MyVendorProvider(apiKey, model, sharedHttpClient, rateLimiter) :> IAIProvider
}
```

`rateLimiter` is resolved from DI at factory-construction time (`services.GetRequiredService<IRateLimiter>()`); when `ServerConfig.RateLimiter = NoRateLimiter`, that resolves to `NoOpRateLimiter` which short-circuits to `Proceed` — no per-call branch needed in the provider.

### Rules

- **One descriptor per (vendor, tier-distinct model).** Don't share a window between two models the vendor rate-limits independently.
- **`FairnessMode = PerProvider` for organisation-tier limits.** `PerScope` is wrong for AI providers — vendors don't partition their limits per-tenant.
- **Don't author a bespoke throttle.** A `SemaphoreSlim` + sliding window inside the provider is the anti-pattern Phase 9v exists to retire: it doesn't share observability with other connectors and it's not swappable for the distributed limiter when Phase 9c half-2 ships.
- **Don't retry into a `Refused`.** A long-window refusal usually means daily-quota exhaustion; retrying inside the same window only re-refuses. Surface it.

## License

Each provider package is licensed under Apache-2.0. See [LICENSE](../../LICENSE) at the repo root.
