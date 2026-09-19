# Migration — Phase 772: server-side egress policy, the outbound-call seam

**Status:** additive. Every default path is byte-for-byte unchanged: the permit-all policy is what a
composition runs under until it installs another, and every in-tree client it migrates keeps the
primary handler `new HttpClient()` would have given it, with one pass-through hop in front. No
consumer action is required to upgrade. Public surface grows (Core: `EgressPolicyTypes`,
`AuditEvent.EgressDenied`; Server: `EgressPolicyHandler`, `EgressEnforcement`, `PlatformHttpClient`,
`ComposeRuntimeServices.registerPlatformHttpClient`, the report's `IEgressEvidence` /
`withEgress`); nothing is removed or retyped.

## What changed

- **`IEgressPolicy` (Core, `Shared/Types/EgressPolicyTypes.fs`).** A pure decision over
  `(ComponentId, EgressDestination, EgressSurface)` → `Permit | Deny of reason`. A destination is an
  **origin** (`scheme://host[:port]`), never the URL — `EgressDestination.tryParse` is the one
  construction path and discards path, query, fragment and userinfo. Two policies ship:
  `EgressPolicy.permitAll` (the default) and `EgressPolicy.declaredDestinations`, resolved from a
  per-component `EgressGrantSignature` (`Map<ComponentId, EgressGrant>`; `UnrestrictedEgress` vs
  `DeclaredDestinations set` — the `SeamGrant` shape). `EgressPolicy.bind mandatory grants` picks
  the policy for a profile's stance and returns it with its `EgressPosture` (`Unenforced` /
  `DenyAll` / `Declared`).
- **`EgressPolicyHandler` (Server).** A `DelegatingHandler` that consults the installed policy with
  the ambient component before every request. A deny raises `EgressDeniedException`, records one
  `EgressDenied` audit row (origin + component + surface + reason — never the URL) under the
  `_platform` scope, and the request **never reaches the inner handler**. A permit forwards the
  request untouched.
- **`EgressComponentContext` (Server).** The ambient calling component — `AsyncLocal`, the
  `FactPurposeContext` shape. Unclaimed chains are attributed to `EgressPolicy.platformComponent`
  (`module:_platform`). A module's handler wraps its outbound work in
  `EgressComponentContext.runAs (ComponentId.ofModule "reports") (fun () -> …)`.
- **`EgressEnforcement` (Server).** The process-wide install seam — `install`, `current`,
  `bindAudit`, the bounded since-boot denial ledger, and `deploymentVerificationEvidence`.
  Process-wide because the in-tree clients are built from module-level lazies with no container in
  hand; the same binding serves the DI-named client, so there is one policy, not two.
- **`PlatformHttpClient` (Server).** `create surface` ≡ `new HttpClient()` + the handler;
  `createWith surface inner` ≡ `new HttpClient(inner)` + the handler; `wrap surface inner` for a
  client constructed with extra arguments. `PlatformHttpClient.Name` is the named
  `IHttpClientFactory` client `registerPlatformHttpClient` registers (surface `ModuleHandler`).
- **The report gains an eleventh section, `egress`** — declared origins per component, refusals
  since boot, and `Unenforced` stated plainly when the permit-all default is composed. Supplied by
  `DeploymentVerificationEvidence.withEgress (Some (EgressEnforcement.deploymentVerificationEvidence
  (CompositionProfile.label profile)))`. Appended, so every earlier section keeps its canonical
  line; the verdict digest moves once, as it does whenever the report grows.

## Moving your own clients onto the factory

```fsharp
// before
let private client = lazy (new HttpClient())
// after — identical behaviour under the default policy
let private client = lazy (PlatformHttpClient.create EgressSurface.Other)

// a custom primary handler
new HttpClient(PlatformHttpClient.wrap EgressSurface.Webhook myHandler, Timeout = t)

// from DI, in a module handler
let http = factory.CreateClient PlatformHttpClient.Name
```

Set `BaseAddress` / `Timeout` on the result exactly as before. Pick the `EgressSurface` that names
what the client is (`AIProvider` / `AuthProvider` / `Notification` / `Webhook` / `ModuleHandler` /
`Other`); the surface is a coordinate of the decision and an audit label, nothing more.

## Declaring destinations (the verified profile)

```fsharp
let grants: EgressGrantSignature =
    Map.ofList [
        ComponentId.ofModule "reports", EgressGrant.ofOrigins [ "https://api.anthropic.com" ]
        ComponentId.forCompanionSlot "INotificationChannel", EgressGrant.ofOrigins [ "https://api.sendgrid.com" ]
    ]

// before `ServerApp.compose` / `build`
EgressEnforcement.install (EgressPolicy.bind (CompositionProfile.requiresSeamGrants profile) (Some grants))
```

Under `Standard` (`mandatory = false`) an undeclared component stays unrestricted and only declared
components are bound. Under `Verified` (`mandatory = true`) an undeclared component is refused, and
**a verified composition that declares nothing denies every outbound call** — `registerPlatformHttpClient`
logs the deny-all posture at boot so that state is never silent.

## What is and is not covered

- Covered: every client obtained from `PlatformHttpClient` or the named factory client. In-tree
  today: the four AI providers, the OpenAI embedding provider, the AI probe validator, the GitHub
  and OIDC auth providers and validators, the SendGrid and Twilio sinks, the KnowledgeBase bulk
  importer, the peer transport and the Docker scheduler.
- **Not covered: a client constructed by hand.** `new HttpClient()` anywhere else never meets the
  handler. In-tree, companions that reference only `ToolUp.Platform.Core` (the Vault and GCP secret
  stores, the Stripe billing client, the Whisper and Azure Speech transcription providers, the Entra
  and Google directory providers) cannot reach the Server-tier factory and are a documented hatch
  until the seam's tier is decided; making that construction a compile-time finding is Phase 776's
  analyser rule.
- Not covered: non-HTTP egress — SMTP, brokers, database drivers, raw sockets.
- The seam decides on **origin only**. It inspects nothing the request carries; a permitted call is a
  call the policy did not refuse. Payload-label checks are Phase 796.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `ToolUp.Platform.Tests` — `EgressPolicyTests`: origin parsing; the four `bind` stances; a deny is
  refused before the inner handler is reached and raises `EgressDeniedException`; a permit hands the
  inner handler a byte-identical request; the audit row carries origin + component and no path or
  query; the ambient component flows and is restored; the DI-named client answers to the installed
  policy; the deny-all posture is logged at boot; the report section's five verdicts and its
  appended position.

## Rollback

Additive throughout: delete `EgressPolicyHandler.fs` and `EgressPolicyTypes.fs`, drop the
`registerPlatformHttpClient` call, the `EgressDenied` case + codec, the `egress` section and its
literal, and revert each migrated client to its prior `new HttpClient(…)` line. No persisted shape
changes; an `EgressDenied` audit row already written decodes as `AuditEventDecodeFailed` after a
rollback, which is the existing posture for any retired event type.
