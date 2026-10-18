# Seam-aware service provider: a module's grant binds what it resolves

**Ships in:** ToolUp.Platform.Server (`SeamConfinement`, `SeamAwareServiceProvider`, `SeamReachObserver`,
`SeamAuthorityEnforcement.confine` / `confineAudited`, `ComponentSeamAuthority.ObservedReach`,
`DeploymentVerificationEvidence.withObservedSeamReach`, `ModuleSurface.substrateSeams`). Ships in 0.28.0 (Phase 770).

**Who is affected:**
- Code that builds a `ComponentSeamAuthority` as a full record literal. Add `ObservedReach = None` (FS0764 otherwise).
- Deployments that declare seam grants (`SeamGrantSignature`) and call `confine` / `confineAudited`. Nothing changes for
  a deployment that does not: every module still gets the request's own provider, the same object as before.

## What a consumer sees

Every module's route handlers and AI tools now run through a per-module wrapper, built once in `ServerApp.addModule`.
Without a registered confinement the wrapper is a passthrough. Once a composition calls `confine`, the module's code
resolves services through a `SeamAwareServiceProvider` keyed by its `ComponentId`:

- **A type that is not a substrate seam** resolves exactly as before. The vocabulary is `ModuleSurface.substrateSeams`
  (`IAIProvider`, `IBlobStorage`, `IConfigStore`, `IEmbeddingProvider`, `IFactStore`, `IFactTableWriter`,
  `IJobScheduler`, `IMetricsSink`, `IModuleBindingVerifier`, `IModuleQueryBus`, `IVectorStore`), the same seams the
  Phase 691 derived reach and the verification report use.
- **A seam inside the module's grant** is the raw provider's own instance, with the same lifetime. It is also recorded
  as observed reach.
- **A seam outside the grant** resolves as absent. `GetService` returns `null` and `GetRequiredService` throws the
  container's usual `InvalidOperationException` ("No service for type … has been registered"). The gate's observer
  receives the Phase 688 denial. Under `confineAudited` (or a gate built with
  `VerifiedCompositionProfile.auditedSeamGate`), that denial is a `CompositionCapabilityRefused` audit row naming the
  component, the seam and the declared set.

The typical first refusal comes from a module that **asks** for a substrate its registrations don't imply. Examples
are an AI tool that enqueues work through `IJobScheduler` without declaring `JobHandlers`, and a handler that calls
the query bus without exposing `QueryHandlers`.

## How to declare the grant

Add the seam to the component's `SeamGrant` in the composition's `SeamGrantSignature`:

```fsharp skip=fragment
let grants =
    Map.ofList [
        ComponentId.ofModule "_platform.reporting", SeamGrant.ofInterfaces [ "IAIProvider"; "IJobScheduler" ]
    ]

match SeamAuthorityEnforcement.confineAudited auditLog "_platform" CompositionProfile.Verified (Some signature) (Some grants) app with
| Ok confined -> confined |> ServerApp.run
| Error refusal -> failwith (SeamAuthorityEnforcement.describeRefusal refusal)
```

To find out what to declare, first run under a gate that grants every seam. A `CompositionProfile.Standard` signature
with no grant signature refuses nothing and still observes. Then read the verification report. Each component line
reads `declared …, reaches …, observed …`. A `SeamAuthorityObservedBeyondDerived` finding names every seam a module
resolved that its registrations do not imply. Either declare the registration that needs it or remove the
hand-resolution.

## What is still outside

The report's `seam-reach-is-a-subset-claim` statement narrows once confinement is composed, and names what remains:
- A provider or substrate instance a module captured outside a request: at composition, from the root container, or in
  a hosted service of its own.
- Query handlers, job handlers, config migrators and vectorisation handlers. None of them is handed a provider.
- On serverless hosts that set `RequestServices` to the root provider, the wrapper still confines the module's
  resolutions, but what it wraps is the root container.

## Rollback

Stop calling `confine` / `confineAudited`. The wrappers go back to passthroughs and the report section renders as
before.
