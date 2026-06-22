# Migration — Phase 176: transient-fault-handling decorator substrate

**Type:** additive, opt-in, default-off (GP 11 / GP 13) — no production behaviour change until a
deployment opts in.

## What changed

A portable retry / circuit-breaker / per-call-timeout **decorator** layer over the cloud-latency
SDK interfaces, so every cloud companion (Azure Blob / S3 / GCS for storage; Key Vault / Secrets
Manager / Secret Manager / Vault for secrets) inherits one consistent, contract-tested resilience
policy instead of reinventing transient-fault handling per-vendor.

Three new files in `ToolUp.Platform.Server`:

- `Server/Infra/TransientFaultPolicy.fs` — the policy as **data** (GP 12 rule 3, retry-as-data):
  - `TransientFaultPolicy` record: `MaxRetries`, `Backoff` (`BackoffSchedule` DU —
    `NoBackoff | Fixed | Exponential`), `Breaker` (`BreakerPolicy` DU —
    `NoBreaker | Threshold of failures * cooldown`), `PerCallTimeout: TimeSpan option`,
    `RetryClassifier: exn -> bool`.
  - `TransientFaultPolicy.identity` — the no-op policy (a decorator built on it is a literal
    pass-through).
  - `TransientFaultPolicy.defaultTransientClassifier` — a conservative cloud-fault classifier
    (timeouts / socket / HTTP / IO / cancellation), walking the inner-exception chain.
  - `TransientFaultRunner` — the shared engine (breaker → timeout → retry/backoff). One instance per
    decorated store; the only mutable state is the breaker tally, lock-guarded (GP 5 exception). The
    `now` clock is injectable for deterministic breaker-cooldown tests.
  - `ResilienceMode` (`NoResilience | WithResiliencePolicy`) — the compose opt-in.
- `Server/Infra/ResilientBlobStorage.fs` — `ResilientBlobStorage(inner, policy)` routing every
  `IBlobStorage` method through the runner, plus `applyStorageResilience : ResilienceMode ->
  IBlobStorage -> IBlobStorage`.
- `Server/Infra/ResilientSecretStore.fs` — the same over `ISecretStore`, plus
  `applySecretResilience`.

Two `ServerApp` builder helpers + fields (`StorageResilience` / `SecretResilience`, default
`NoResilience`): `ServerApp.withStorageResilience policy` / `withSecretResilience policy`. `compose`
applies the decorator **outermost** (over any envelope-encryption decorator), so a transient fault
retries the whole operation. `composeWithRAG` forwards both through to `compose`.

**Faults are exceptions, not `Result.Error` values.** The classifier only ever sees *thrown*
exceptions (vendor-SDK transient network/timeout faults). A deterministic domain outcome an interface
returns as `Result.Error` — a missing-blob `Download`, an idempotent `Delete` of an absent key, a
`SetSecret` returning `"read-only"` — is a *value*, never retried. The idempotency + deterministic-
error contracts of `IBlobStorage` / `ISecretStore` are preserved by construction.

## How to adopt

```fsharp
let storagePolicy =
    { TransientFaultPolicy.identity with
        MaxRetries = 3
        Backoff = Exponential(TimeSpan.FromMilliseconds 200.0, TimeSpan.FromSeconds 5.0)
        Breaker = Threshold(failures = 5, cooldown = TimeSpan.FromSeconds 30.0)
        PerCallTimeout = Some(TimeSpan.FromSeconds 10.0)
        RetryClassifier = TransientFaultPolicy.defaultTransientClassifier }

ServerApp.empty
|> ServerApp.withStorage azureBlobStorage
|> ServerApp.withStorageResilience storagePolicy
|> ServerApp.withSecretResilience storagePolicy
|> ServerApp.run
```

## Do I need to do anything?

No. `NoResilience` is the default; a deployment that calls neither `withStorageResilience` nor
`withSecretResilience` resolves to the bare implementation — no decorator object in the hot path,
byte-for-byte unchanged (GP 13). Proven by re-running the existing `IBlobStorageContract` /
`ISecretStoreContract` packs through an identity-decorated in-memory impl (see
`InProcess/TransientFaultPolicyTests.fs`). All consumers are ⛔ N-A in `SDK-ADOPTION.md` until one
opts into resilience.
