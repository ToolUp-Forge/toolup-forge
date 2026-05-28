# Migration — Phase 11.C.5 Public-API stability cluster (0.3.0 pre-public-flip API renames)

**Version range:** every `ToolUp.*` package bumps from `0.2.3` → `0.3.0` in lockstep via the `ToolUp.Sdk` meta-manifest. Per-package SemVer means a consumer that pins via `<ToolUpSdkVersion>0.3.0</ToolUpSdkVersion>` and applies the rename inventory below restores clean.

**Why this lands now.** Every item below is a name or shape change that is far cheaper before the package set lands at v0.1.0 in a public feed than after. Tier 2 affects every consumer's `<PackageReference>` list (one coordinated minor bump rewrites them all). Tier 3 affects external impls (a forked `IAuthProvider` repeats the unsafe `obj` cast forever if shipped at 0.1.0).

**Scope discipline.** Tier 2 is **package-id and assembly-name only**; F# namespaces inside the packages are deliberately NOT renamed. Renaming the namespace is a semantic refactor with much wider consumer-side blast radius (`open ...` statements, fully-qualified calls in module wiring) and was not what the phase scoped. Consumers therefore update their `<PackageReference Include="...">` lines and `Directory.Packages.props` `<PackageVersion>` entries; their F# source that does `ToolUp.Platform.NotificationChannels.Redis.RedisNotificationChannel.connect ...` continues to compile unchanged. This decoupling matches existing precedent in the SDK (e.g. the AzureBlobStorage assembly already publishes namespace `ToolUp.Storage.AzureBlobStorage` while shipping as package `ToolUp.Storage.Azure`).

---

## Tier 2 — package-ID renames

11 source packages rename. A consumer pinning any of these package IDs updates its `Directory.Packages.props` to the new IDs in the same coordinated change; consumers that don't pin the renamed packages are unaffected.

### T2.1 AuditSinks — drop `Platform.`

| Old ID | New ID |
|---|---|
| `ToolUp.Platform.AuditSinks.SplunkHec` | `ToolUp.AuditSinks.SplunkHec` |
| `ToolUp.Platform.AuditSinks.DatadogLogs` | `ToolUp.AuditSinks.DatadogLogs` |
| `ToolUp.Platform.AuditSinks.S3Archive` | `ToolUp.AuditSinks.S3Archive` |

These three fsprojs have no explicit `<PackageId>` — the package id derives from the `.fsproj` filename. Rename is by **fsproj-file rename**: e.g. `src/AuditSinks/SplunkHec/ToolUp.Platform.AuditSinks.SplunkHec.fsproj` → `src/AuditSinks/SplunkHec/ToolUp.AuditSinks.SplunkHec.fsproj`. The solution file's project path + display name update in the same change.

**Consumer-side example (`<consumer-app>/Directory.Packages.props`):**

```diff
- <PackageVersion Include="ToolUp.Platform.AuditSinks.S3Archive" Version="$(ToolUpSdkVersion)" />
- <PackageVersion Include="ToolUp.Platform.AuditSinks.SplunkHec" Version="$(ToolUpSdkVersion)" />
- <PackageVersion Include="ToolUp.Platform.AuditSinks.DatadogLogs" Version="$(ToolUpSdkVersion)" />
+ <PackageVersion Include="ToolUp.AuditSinks.S3Archive" Version="$(ToolUpSdkVersion)" />
+ <PackageVersion Include="ToolUp.AuditSinks.SplunkHec" Version="$(ToolUpSdkVersion)" />
+ <PackageVersion Include="ToolUp.AuditSinks.DatadogLogs" Version="$(ToolUpSdkVersion)" />
```

### T2.2 NotificationChannels — drop `Platform.`

| Old ID | New ID |
|---|---|
| `ToolUp.Platform.NotificationChannels.Redis` | `ToolUp.NotificationChannels.Redis` |
| `ToolUp.Platform.NotificationChannels.Email.Smtp` | `ToolUp.NotificationChannels.Email.Smtp` |
| `ToolUp.Platform.NotificationChannels.Email.SendGrid` | `ToolUp.NotificationChannels.Email.SendGrid` |
| `ToolUp.Platform.NotificationChannels.Sms.Twilio` | `ToolUp.NotificationChannels.Sms.Twilio` |
| `ToolUp.Platform.NotificationChannels.Push.WebPush` | `ToolUp.NotificationChannels.Push.WebPush` |

Same shape as T2.1 — five fsproj-file renames, no `<PackageId>` overrides in any of them.

### T2.3 Metrics — drop `Platform.`

| Old ID | New ID |
|---|---|
| `ToolUp.Platform.Metrics.OpenTelemetry` | `ToolUp.Metrics.OpenTelemetry` |

Same fsproj-file rename shape.

### T2.4 Storage specificity — `Azure` → `AzureBlob`

| Old ID | New ID |
|---|---|
| `ToolUp.Storage.Azure` | `ToolUp.Storage.AzureBlob` |

`AzureBlobStorage.fsproj` already has explicit `<PackageId>` and `<AssemblyName>` — both flip from `ToolUp.Storage.Azure` to `ToolUp.Storage.AzureBlob`. The fsproj filename and folder name (`AzureBlobStorage/`) stay — the folder name already implies "Blob"; the assembly/package id picks up the same specificity. Future `.AzureFiles` / `.AzureTables` companions claim the orthogonal slots without colliding.

Sister stores (`ToolUp.Storage.AwsS3`, `ToolUp.Storage.GoogleCloud`) are out of scope per phase body — there is no near-term `.S3Glacier` / `.GcsArchive` collision that warrants matching the `.AzureBlob` precedent today.

### T2.5 `.Client` suffix unification — `OidcClient` → `Oidc.Client`

| Old ID | New ID |
|---|---|
| `ToolUp.AuthProviders.OidcClient` | `ToolUp.AuthProviders.Oidc.Client` |

Settled convention: `<companion>.Client` (matches `ToolUp.AIProviders.Claude.Client`, `ToolUp.AuthProviders.EntraExternalId.Client`, and the four-core `.Client` tier package). `OidcClient.fsproj` has explicit `<PackageId>` + `<AssemblyName>` — both flip. Fsproj filename and folder (`OidcClient/`) stay — only the metadata renames.

`ClerkUI` is intentionally **not** caught by this unification — its `UI` suffix is a deliberate descriptor (client-side UI shim for Clerk OAuth integration), not the `.Client` tier-discriminator pattern.

### Files touched per Tier 2 group

For each rename group:
- The renamed `.fsproj` (or its `<PackageId>` + `<AssemblyName>` metadata for the two that had explicit values).
- `toolup-forge/ToolUp.Forge.sln` — project path + display name.
- `toolup-forge/Directory.Packages.props` — `<PackageVersion>` entries under the matching comment.
- `toolup-forge/src/ToolUp.Sdk/build/ToolUp.Sdk.props` — `<PackageVersion>` entries in the meta-manifest.
- `toolup-forge/src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — `<ProjectReference>` paths where the fsproj filename changed.
- The companion's `*.Server.props` empty-marker comment (cosmetic only; the comment cites the old package name).
- `toolup-forge/templates/platformsdk-solution/.template.config/template.json` — references the meta-manifest version; no per-package id touches because the template scaffold inherits everything through `ToolUpSdkVersion`. No edit required for the package renames specifically; the template's scaffold `Directory.Packages.props` (if it pins any of the renamed packages directly) would update — none does today.
- Consumer-side: `<consumer-app>/Directory.Packages.props` plus the server / client `.fsproj` files of the affected consumer. No source-file edits — F# namespaces are unchanged.

### Verification

```pwsh
# Forge side
dotnet build toolup-forge/ToolUp.Forge.sln
dotnet run --project toolup-forge/Build.fsproj -- Pack
ls toolup-forge/../local-nuget-feed/ToolUp.AuditSinks.SplunkHec.*.nupkg
ls toolup-forge/../local-nuget-feed/ToolUp.NotificationChannels.Redis.*.nupkg
ls toolup-forge/../local-nuget-feed/ToolUp.Metrics.OpenTelemetry.*.nupkg
ls toolup-forge/../local-nuget-feed/ToolUp.Storage.AzureBlob.*.nupkg
ls toolup-forge/../local-nuget-feed/ToolUp.AuthProviders.Oidc.Client.*.nupkg

# Consumer side
dotnet restore <consumer-app>/<consumer-app>.sln
dotnet build <consumer-app>/<consumer-app>.sln
```

The `dotnet restore` MUST surface the renamed package ids resolved through the new meta-manifest. No `ToolUp.Platform.AuditSinks.*` / `ToolUp.Platform.NotificationChannels.*` / `ToolUp.Platform.Metrics.OpenTelemetry` / `ToolUp.Storage.Azure` / `ToolUp.AuthProviders.OidcClient` reference may remain in any consumer fsproj or `Directory.Packages.props` after this phase.

### Rollback

Revert the corresponding commits. The renames are mechanical and self-contained per package group — reverting the AuditSinks commit alone leaves the other Tier 2 groups in their renamed state, which is fine because nothing cross-references the AuditSinks package id from another rename group.

---

## Tier 3 — public-API renames (interface-shape changes)

5 separate commits, one per item, to keep blame readable per the phase's coordination block.

### T3.1 `IAuthProvider.{GetUser,ValidateRequest}` — `obj` → `RequestContext` newtype

**Today** ([`src/ToolUp.Platform.Core/Shared/Interfaces/IAuthProvider.fs:43,51`](../../src/ToolUp.Platform.Core/Shared/Interfaces/IAuthProvider.fs)):

```fsharp
type IAuthProvider =
    abstract GetUser: obj -> Async<AuthenticatedUser>
    abstract ValidateRequest: obj -> Async<Result<AuthenticatedUser, string>>
```

Every shipped impl repeats `let httpCtx = ctx :?> HttpContext` immediately on entry — confirmed in [`OidcAuthProvider.fs:361,380`](../../src/AuthProviders/Oidc/OidcAuthProvider.fs) and [`EntraExternalIdAuthProvider.fs:207,222`](../../src/AuthProviders/EntraExternalId/EntraExternalIdAuthProvider.fs).

**After.** Introduce `RequestContext` as a Core-tier opaque newtype whose construction lives only in the server tier (where `HttpContext` is available). Core declares the type; `ToolUp.Platform.Server` declares the constructor.

```fsharp
// src/ToolUp.Platform.Core/Shared/Auth/RequestContext.fs (new)
namespace ToolUp.Platform.Auth

/// Opaque server-request context handed to `IAuthProvider` impls. The
/// underlying value is a `Microsoft.AspNetCore.Http.HttpContext` on the
/// server tier; Core declares the wrapper so the interface can live in
/// the shared (Fable-compatible) layer without referencing
/// `Microsoft.AspNetCore.*`. Construct via
/// `ToolUp.Platform.Server.RequestContext.ofHttpContext` (server tier).
type RequestContext = private RequestContext of obj

module RequestContext =
    /// Unwrap to the underlying value. Server-tier impls cast it to
    /// `HttpContext`; the wrapper exists to keep the cast localised
    /// to one site per impl rather than scattered through Core.
    let inline value (RequestContext o) = o
```

```fsharp
// src/ToolUp.Platform.Server/Auth/RequestContext.fs (new)
namespace ToolUp.Platform.Server.Auth

open Microsoft.AspNetCore.Http
open ToolUp.Platform.Auth

module RequestContext =
    /// Wrap a live `HttpContext` for hand-off to an `IAuthProvider`.
    let ofHttpContext (ctx: HttpContext) : RequestContext =
        RequestContext.RequestContext(ctx :> obj)
```

```fsharp
// IAuthProvider.fs — after
type IAuthProvider =
    abstract GetUser: RequestContext -> Async<AuthenticatedUser>
    abstract ValidateRequest: RequestContext -> Async<Result<AuthenticatedUser, string>>
```

**Impl migration:**

```fsharp
// Before
let httpCtx = ctx :?> HttpContext

// After
let httpCtx = RequestContext.value ctx :?> HttpContext
```

The cast survives, but in **exactly one site per impl** — the entry point of `GetUser` / `ValidateRequest`. Any further use of `httpCtx` inside the impl reuses the unwrapped value. The contract that `RequestContext` wraps `HttpContext` is documented on the Core newtype.

**Shipped impls touched:** `OidcAuthProvider.fs`, `EntraExternalIdAuthProvider.fs`, `HeaderAuthProvider.fs`, `StaticJwtAuthProvider.fs` (4 impls — the phase body's mention of `ClerkAuthProvider` reflects an out-of-date inventory; Clerk is shipped today as a client-side UI companion `ClerkUI` that wires through the OIDC provider, not as a separate server-side `IAuthProvider`).

**Call-site migration.** Every caller that today passes `ctx :> obj` updates to pass `RequestContext.ofHttpContext ctx`. Callers identified in `toolup-forge/src/ToolUp.Platform.Server/Server/Compose/ComposeAuth.fs` and the integration-tests in `toolup-forge/src/ToolUp.Platform.Tests/` plus consumer callsites if any.

### T3.2 RetryPolicy consolidation

**Today** — two policies with different shapes:

```fsharp
// src/ToolUp.Platform.Core/Shared/Interfaces/IAIProvider.fs:201
type RetryPolicy = {
    MaxRetries: int   // attempts AFTER the first; 0 = fail fast
    BackoffMs: int    // base backoff; provider may apply exponential growth
}

// src/ToolUp.Platform.Server/Server/AuditReplicatorTypes.fs:9
type AuditReplicatorRetryPolicy = {
    MaxAttempts: int          // total attempts INCLUDING the first
    InitialBackoff: TimeSpan
    MaxBackoff: TimeSpan
}
```

**After** — single Core-level definition at [`src/ToolUp.Platform.Core/Shared/Types/RetryPolicy.fs`](../../src/ToolUp.Platform.Core/Shared/Types/RetryPolicy.fs):

```fsharp
namespace ToolUp.Platform

open System

/// Cross-cutting retry policy for any SDK-level operation that retries
/// on transient failure. Phase 9c portability rule 3 — retry expressed
/// as data, not callbacks.
type RetryPolicy = {
    /// Total attempts including the first. `MaxAttempts = 1` is "no
    /// retries"; `MaxAttempts = 0` is invalid (validators reject).
    MaxAttempts: int
    /// Backoff before the second attempt. Subsequent attempts grow
    /// exponentially up to `MaxBackoff`.
    InitialBackoff: TimeSpan
    /// Cap on exponential growth so a wedged dependency doesn't park
    /// indefinitely.
    MaxBackoff: TimeSpan
}

module RetryPolicy =
    /// Conservative default: 3 attempts, 500ms initial, 30s cap.
    let defaults: RetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromMilliseconds 500.0
        MaxBackoff = TimeSpan.FromSeconds 30.0
    }

    /// `attemptNumber` is 1-indexed. `delayFor _ 1 = TimeSpan.Zero`
    /// (first attempt fires immediately).
    let delayFor (policy: RetryPolicy) (attemptNumber: int) : TimeSpan =
        if attemptNumber <= 1 then
            TimeSpan.Zero
        else
            let exponent = attemptNumber - 2
            let scaled = policy.InitialBackoff.TotalMilliseconds * (2.0 ** float exponent)
            let capped = min scaled policy.MaxBackoff.TotalMilliseconds
            TimeSpan.FromMilliseconds capped
```

**Migration consequences:**
- The AI-side `RetryPolicy` field rename — `{ MaxRetries; BackoffMs }` → `{ MaxAttempts; InitialBackoff; MaxBackoff }`. Semantic change: `MaxAttempts` includes the first attempt; callers that today pass `MaxRetries = 3` (meaning "3 retries after first" = 4 total attempts) migrate to `MaxAttempts = 4`. **Pre-public-rename window** — accepted as breaking per the operator-level 0.3.0 carve.
- `AuditReplicatorRetryPolicy` is **deleted**; `AuditReplicatorOptions.RetryPolicy` now has type `RetryPolicy`. `AuditReplicatorRetryPolicy.defaults` becomes `RetryPolicy.defaults`. The field shape was already `{ MaxAttempts; InitialBackoff; MaxBackoff }`, so no field renames are needed on the audit side — only the type name changes.

Other retry-policy types in the SDK (`JobRetryPolicy` in `JobTypes.fs`, `WebhookRetryPolicy` in `WebhookTypes.fs`, `TransactionalRetryPolicy` in `INotificationSink.fs`) carry domain-specific extra fields and stay separate — they're explicitly **out of scope** for this consolidation per the phase body's "two RetryPolicy records" scoping.

### T3.3 `NotificationKind` widening — per-platform sub-kinds

**Today** ([`src/ToolUp.Platform.Core/Shared/Types/NotificationTypes.fs:312-320`](../../src/ToolUp.Platform.Core/Shared/Types/NotificationTypes.fs)):

```fsharp
module NotificationKind =
    // ...
    module SinkKind =
        [<Literal>] let Email = "Email"
        [<Literal>] let Sms = "Sms"
        [<Literal>] let Push = "Push"
```

The compose-time uniqueness check ([`ComposeNotifications.fs`](../../src/ToolUp.Platform.Server/Server/Compose/ComposeNotifications.fs)) rejects two sinks registering the same string `Kind`. Two push companions (e.g. WebPush + future FCM) cannot coexist.

**After** — promote `SinkKind` to a discriminated type that admits per-platform variants:

```fsharp
[<RequireQualifiedAccess>]
type PushVariant =
    | WebPush
    | Fcm
    | Apns
    /// Open extension point for vendor-specific variants. Validators
    /// reject empty / whitespace values; uniqueness check key includes
    /// the discriminator string verbatim.
    | Other of name: string

[<RequireQualifiedAccess>]
type SinkKind =
    | Email
    | Sms
    | Push of PushVariant

module SinkKind =
    /// Stable wire-format string used by `INotificationSink.Kind`.
    /// Round-trips via `tryParse`. The Push variant emits
    /// `"Push.WebPush"` / `"Push.Fcm"` / `"Push.Apns"` / `"Push.<Other>"`.
    let toWireString =
        function
        | Email -> "Email"
        | Sms -> "Sms"
        | Push PushVariant.WebPush -> "Push.WebPush"
        | Push PushVariant.Fcm -> "Push.Fcm"
        | Push PushVariant.Apns -> "Push.Apns"
        | Push (PushVariant.Other name) -> sprintf "Push.%s" name
```

**`INotificationSink.Kind` becomes `SinkKind` (not `string`).** Compose-time uniqueness checks `(toWireString sink.Kind)`, which keys `Push.WebPush` distinct from `Push.Fcm`. The wire format stays a stable string so the audit log + subscriber dispatch don't break.

**Shipped sink updates:**
- `SmtpNotificationSink` / `SendGridNotificationSink`: `Kind = SinkKind.Email`.
- `TwilioNotificationSink`: `Kind = SinkKind.Sms`.
- `WebPushNotificationSink`: `Kind = SinkKind.Push PushVariant.WebPush` (was `"Push"`).

The `module NotificationKind.SinkKind` literals retire — replaced by the `SinkKind` DU + `toWireString`. The narrower `NotificationKind` literal block (the kind-of-Notification strings, `SystemMessage` / `JobCompleted` / etc.) is unchanged — those describe the DU on the wire, not sink registration.

### T3.4 `ClientHandlerRegistry` required on `ClientConfig.create`

**Today** ([`src/ToolUp.Platform.Client/Client/SDK.ClientTypes.fs:1035`+]( ../../src/ToolUp.Platform.Client/Client/SDK.ClientTypes.fs)):

```fsharp
module ClientConfig =
    let create (...) : ClientConfig = {
        // ...
        Handlers = ClientHandlerRegistry.empty
    }
```

Default `empty` registry means forgetting to wire `OidcClient.handler` produces a runtime "no handler registered" failure on first sign-in, not a compile error.

**After.** Promote `Handlers` to a positional required parameter:

```fsharp
module ClientConfig =
    let create
        (...)
        (handlers: ClientHandlerRegistry)
        : ClientConfig =
        {
            // ...
            Handlers = handlers
        }
```

Apps that do not register any handler explicitly pass `ClientHandlerRegistry.empty` — the affordance is preserved, but the omission is now visible at the call site.

**Consumer migration** — every `ClientConfig.create` call site adds the handler argument (or `ClientHandlerRegistry.empty` if none). A consumer composition root that does not register handlers today (e.g. the Client `.fsproj` carries a commented-out `<PackageReference Include="ToolUp.AuthProviders.OidcClient" />`) migrates to passing `ClientHandlerRegistry.empty`.

### T3.5 `createWith*` argument-order lock — `(secretStore, model)`

**Today — drift inventory:**

| Function | Today | Conforming? |
|---|---|---|
| `Claude.createWithModel` | `(secretStore) (model)` | ✅ |
| `OpenAI.createWithModel` (AI) | `(secretStore) (model)` | ✅ |
| `OpenAI.createWithModel` (Embedding) | `(model) (dimensions) (secretStore)` | ❌ |
| `OpenAI.createWithBatchSize` (Embedding) | `(batchSize) (secretStore)` | ❌ (mirror — secretStore should be first) |

**Settled convention.** `(secretStore, …)` — matches the OAuth-flow factories that already take `secretStore` first and is consistent with the Claude / OpenAI AI factories.

**After:**

```fsharp
// src/EmbeddingProviders/OpenAI/OpenAIEmbeddingProvider.fs

// Before
let createWithModel (model: string) (dimensions: int) (secretStore: ISecretStore) : IEmbeddingProvider = ...
let createWithBatchSize (batchSize: int) (secretStore: ISecretStore) : IEmbeddingProvider = ...

// After
let createWithModel (secretStore: ISecretStore) (model: string) (dimensions: int) : IEmbeddingProvider = ...
let createWithBatchSize (secretStore: ISecretStore) (batchSize: int) : IEmbeddingProvider = ...
```

Auth-provider `createWith*` shapes (e.g. `EntraExternalId.createWith (httpClient) (logger) (config)`) are **out of scope** — they do not take a `secretStore` parameter (the secret is read from the `config` value). The convention is "secretStore first **when present**".

**Consumer migration** — re-order arguments at every `createWithModel` / `createWithBatchSize` call site on `OpenAIEmbeddingProvider`. Verified single-site change inside the SDK tests; most consumers do not reference these functions directly.

---

## Acceptance

- Every renamed package builds + restores clean from `local-nuget-feed/` under its new ID.
- `ToolUp.Sdk` meta-manifest resolves transitively to all renamed packages.
- A fresh `dotnet new platformsdk-solution` scaffold restores + builds clean against renamed IDs.
- `IAuthProvider.GetUser` / `ValidateRequest` signature compiles; every shipped impl uses `RequestContext`; the `obj` cast survives in exactly one site per impl (the unwrap at the entry point).
- `RetryPolicy` has exactly one definition in `ToolUp.Platform.Core`; the AI-provider and audit-replicator usages reference it.
- Two `Push` companions could register concurrently (`WebPush` + `Fcm`) without uniqueness-validator rejection.
- Omitting handlers from `ClientConfig.create` is a compile error; passing `ClientHandlerRegistry.empty` is the explicit no-op.
- Every `createWith*` overload that takes a `secretStore` argument places it first; grep confirms no `(model, _, secretStore)` survives.
- `dotnet build toolup-forge/ToolUp.Forge.sln` clean.
- `dotnet run --project toolup-forge/src/ToolUp.Platform.Tests/...` clean.
- Migration doc (this file) landed.

## Rollback

Tier 2 commits revert independently per rename group. Tier 3 commits revert independently per item (the 5 commits are deliberately small). Reverting one Tier 3 item does not affect another — the items touch disjoint files except where `T3.2` and `T3.3` both edit Core's `Shared/Types/` directory (different files within it).

## Cross-references

- Predecessor work (already shipped): CPM migration, pack metadata, server-tier source-layout symmetry.
- Successor: the OSS public flip consumes this renamed surface as the v0.1.0-going-public baseline.
