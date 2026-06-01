# Migration — Phase 70: Multi-platform AI provider + Platform-Admin-managed key store

**Shape:** Breaking SDK contract change to `IAIProviderFactory` + `AIPlatformProvider` + `AISettingsApi`. Additive new substrate `IPlatformAIKeyStore` + `PlatformAIKeysApi`. Single-platform-provider deployments preserve byte-identical runtime behaviour after the composition-root edit.

## What changes

1. **`AIPlatformProvider` record reshape.** The bundle no longer carries a pre-built `Provider: IAIProvider`; instead it carries a `Build: ApiKey -> Model -> IAIProvider` curried closure and an optional `BootstrapKeyFromEnv: string option` fallback. The factory resolves the API key per-request (team-scope → platform-scope → bootstrap) and invokes `Build` with the resolved (key, model). This unlocks runtime key rotation and per-team key overrides without redeploy.

2. **`IAIProviderFactory.PlatformDescriptors`** (new — plural). `PlatformDescriptor: AIProviderDescriptor option` remains as a derived `List.tryHead` accessor for backward compatibility, but new callers should prefer the list. New `IAIProviderFactory.BuildPlatform(providerId, apiKey, model)` returns an `IAIProvider option` so the Platform Admin keys module can build a provider for the test-key path.

3. **`DefaultAIProviderFactory.create` signature.** Last two parameters change:
   - Was: `… (platformProvider: AIPlatformProvider option)`
   - Now: `… (platformProviders: AIPlatformProvider list) (platformKeyStore: IPlatformAIKeyStore option)`

4. **`IPlatformAIKeyStore`** (new). 8-method substrate over `ISecretStore` for runtime-managed AI keys. Default impl `BlobPlatformAIKeyStore.create` ships in `ToolUp.AI.Server`. Two scopes:
   - Platform-scope: `_platform-ai-keys` container, key name = providerId.
   - Team-scope: `team-ai-keys-{teamId}` container, key name = providerId.

5. **`composeWithAI` gains `withPlatformAIKeyStore`** for custom backing stores. When omitted, `composeAI` registers `IPlatformAIKeyStore` in DI via a Func-resolver that lazily builds `BlobPlatformAIKeyStore.create secretStore` from whichever `ISecretStore` is in DI.

6. **`AISettingsApi` wire-surface changes** (Stream B):
   - `GetPlatformDescriptor: unit -> Async<AIProviderDescriptor option>` → `GetPlatformDescriptors: unit -> Async<AIProviderDescriptor list>`.
   - New `SetPlatformProviderOverride: string option -> Async<Result<unit, string>>` alongside `SetPlatformModelOverride`.
   - `AIUserConfigView` gains `PlatformProviderOverride: string option`.
   - `SetPlatformModelOverride` validation widens to accept any wired descriptor's models (was: only the single descriptor's models).

7. **New `PlatformAIKeysApi`** Fable.Remoting contract (10 methods) + `PlatformAIKeysHandler` server-side handler. Gated on `AccessContext.canModifyPlatformConfig`. The current key value is never returned by any read path.

8. **New `PlatformAIKeysAdminUI`** client module appended automatically by `AIClientConfig.appendAssistantModule`. Sits under the "Platform Admin" sidebar group with id `_ai.PlatformAIKeys`. Hidden from non-admin sidebars via the shell's role gate.

## Diff to apply

### Composition root — required for every consumer that wires a platform provider

A consumer who previously wired one provider as the `Some bundle` arg to `DefaultAIProviderFactory.create`:

```fsharp
// Before — single platform provider, key baked in at startup
let claudeProvider = ClaudeAIProvider.createWithApiKey claudeKey

let platformBundle = {
    Descriptor = ClaudeAIProvider.descriptor
    Provider = claudeProvider
    Rebuild = Some (fun model -> ClaudeAIProvider.createWithApiKeyAndModel claudeKey model)
}

let factory =
    DefaultAIProviderFactory.create
        [ ClaudeAIProvider.builder; OpenAIProvider.builder ]
        providerProfile
        secretStore
        PlatformOnly
        (Some platformBundle)
```

becomes:

```fsharp
// After — request-time key resolution; bootstrap env-var key is the back-compat shim
let claudeBundle : AIPlatformProvider = {
    Descriptor = ClaudeAIProvider.descriptor
    Build = ClaudeAIProvider.createWithApiKeyAndModel  // (apiKey -> model -> IAIProvider)
    BootstrapKeyFromEnv = Some claudeKey  // optional — fall-through when the key store has nothing
}

let factory =
    DefaultAIProviderFactory.create
        [ ClaudeAIProvider.builder; OpenAIProvider.builder ]
        providerProfile
        secretStore
        PlatformOnly
        [ claudeBundle ]   // list, not Some _
        None               // IPlatformAIKeyStore option — None lets composeAI auto-promote
```

The `BootstrapKeyFromEnv` field is the migration shim — when the Platform-Admin-managed key store has no key recorded for a provider, the factory falls back to this value. Existing deployments that set `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `GEMINI_API_KEY` at boot continue to work without any new admin action.

### Multi-provider opt-in

To surface Anthropic + OpenAI + Gemini together (the v1 multi-platform shape), wire one bundle per vendor:

```fsharp
let factory =
    DefaultAIProviderFactory.create
        [ ClaudeAIProvider.builder; OpenAIProvider.builder; GeminiAIProvider.builder ]
        providerProfile
        secretStore
        PlatformOnly
        [
            { Descriptor = ClaudeAIProvider.descriptor
              Build = ClaudeAIProvider.createWithApiKeyAndModel
              BootstrapKeyFromEnv = Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY" |> Option.ofObj }
            { Descriptor = OpenAIProvider.descriptor
              Build = OpenAIProvider.createWithApiKeyAndModel
              BootstrapKeyFromEnv = Environment.GetEnvironmentVariable "OPENAI_API_KEY" |> Option.ofObj }
            { Descriptor = GeminiAIProvider.descriptor
              Build = GeminiAIProvider.createWithApiKeyAndModel
              BootstrapKeyFromEnv = Environment.GetEnvironmentVariable "GEMINI_API_KEY" |> Option.ofObj }
        ]
        None
```

The settings UI automatically surfaces a provider+model picker when `PlatformDescriptors.Length ≥ 2`; with exactly one descriptor the provider dropdown is hidden and only the model picker renders.

### Custom key store (advanced)

For a custom `IPlatformAIKeyStore` (Azure Key Vault wrapper, in-memory test double, etc.), pass it through `AIServerApp.withPlatformAIKeyStore` AND `DefaultAIProviderFactory.create`'s last argument:

```fsharp
let myKeyStore : IPlatformAIKeyStore = …

let factory =
    DefaultAIProviderFactory.create builders providerProfile secretStore PlatformOnly bundles (Some myKeyStore)

aiServerApp
|> AIServerApp.withPlatformAIKeyStore myKeyStore
```

### Test fakes / custom `IAIProviderFactory` implementations

Any code that implements `IAIProviderFactory` directly (test doubles, custom wrappers) gains two new abstract members:

```fsharp
member _.PlatformDescriptors = []                                        // or [single]
member _.BuildPlatform(_providerId, _apiKey, _model) = None              // None for tests that don't exercise key-test
```

The forge codebase's three test fakes (`AIProviderEnvValidatorTests`, `AIProviderProbeValidatorTests`, `SampleClientToolDispatchTests`) and two factory decorators (`MeteringProviderFactory`, `QuotaEnforcingProviderFactory`) were updated in the same commit; external test code needs the equivalent two-line addition.

## Verification steps

1. **Build clean.** `dotnet build src/ToolUp.AI.Server/ToolUp.AI.Server.fsproj` succeeds with 0 errors after the composition-root edit.
2. **Byte-identical single-provider behaviour.** A pre-Phase-70 deployment migrated to the new shape with `BootstrapKeyFromEnv = Some <prior env-var value>` resolves the same `IAIProvider` instance on every request as before. Specifically: with no Platform-Admin-managed keys configured, the request-time chain falls through to `BootstrapKeyFromEnv`, which equals the prior startup-baked key.
3. **Multi-provider picker.** With ≥2 bundles wired, the AI Settings module renders a provider dropdown above the model dropdown. Switching provider resets the model selection to the new provider's default.
4. **Platform Admin keys module.** Navigate to "Platform Admin" → "AI Keys" (visible only to Platform Admins). Two sections render: platform-wide keys + per-team keys. Each row shows status badge "Key stored" / "No key", a password input, and Save / Test / Remove buttons. The current key value is never displayed.
5. **Team-scope precedence.** Configure a platform-scope key for Anthropic + a team-scope override for Team A. A user in Team A's requests use Team A's key; users in other teams use the platform-scope key.
6. **Resolution-chain audit.** Tail server logs while exercising AI calls. The four-step chain (team → platform → bootstrap → MissingApiKey) should be exercisable by selectively clearing keys at each scope.

## Rollback

Revert to a single `AIPlatformProvider` bundle and pass `[bundle]` + `None` for the key store. Existing env-var-supplied keys continue to work via `BootstrapKeyFromEnv`. The `IPlatformAIKeyStore` substrate stays registered in DI (no harm; nothing reads it without bundles wired) and the Platform Admin keys module surfaces "no platform AI providers wired; nothing to manage" when `PlatformDescriptors = []`.

## Risks

- **Wire-contract change on `AISettingsApi`.** The `GetPlatformDescriptor` → `GetPlatformDescriptors` rename is a breaking Fable.Remoting wire change. Any external client that pinned the old method name needs to update. The built-in `AISettingsUI` was migrated in the same commit; no other forge consumer references the method.
- **Tests that construct `AIPlatformProvider` records by hand** need updating to the new field shape. The forge codebase has no such test today; downstream test suites may.
- **`IPlatformAIKeyStore` is a new portability-rule-bearing substrate.** External implementations (Azure Key Vault, AWS Secrets Manager) need to satisfy the six rules (identity-by-value, async-at-every-boundary, retry-as-data, stateless, per-scope sharding, no implicit timing). The default `BlobPlatformAIKeyStore` over `ISecretStore` is the conformance reference.
