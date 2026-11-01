# Composition as declared contributions — the builders' writes survive `withConfig`

Version: 0.39.0 (breaking, a 0.x minor).

## What changes

1. **A builder that writes `ServerConfig` records an override.** `withAlertRule`, `withNotificationCategory`,
   `withPeerRoutePrefix`, `withSeedData`, `withSecurityHardening`, `withDurableLineage` and the other builders that
   append to or set one field of the configuration, and `addModule`, no longer edit `ServerApp.Config`. They add a
   `ConfigOverride` to `ServerApp.Overrides`, and `run` folds the overrides onto `Config` once. `withConfig` sets
   `Config` and nothing else, so `app |> addModules ms |> withConfig cfg` now composes the same application as
   `app |> withConfig cfg |> addModules ms`. It used to discard every module declaration and builder write.
2. **Read a builder's write back through `ServerApp.effectiveConfig app`**, never `app.Config`, which holds only
   what `withConfig` set.
3. **The middleware builders contribute.** `withPreMiddleware`, `withPostMiddleware` and `withDeliveryCompleted`
   add `Middleware` contributions, so a later `withExtensions` keeps them. The record `run` hands the pipeline is
   `ServerApp.composedExtensions app`: the consumer's `Extensions`, followed by the contributed registrations,
   routes and middleware in plan order.
4. **A `Contribution` declares the seams it requires and provides.** `Contribution.requires [ SeamId "IFoo" ]` and
   `Contribution.provides [ SeamId "IFoo" ]` add them. `ContributionPlan.order` runs every provider of a seam before
   every contribution that requires it, and leaves the rest in the order added. An unmet requirement or a cycle
   refuses the composition with a message naming both ends. Two kinds are new, `Routes` and `Validator`
   (`Contribution.routes`, `Contribution.validator`), alongside `Middleware`.
5. **Hosted units contributed through `Contribution.hosted` are admitted against the final configuration.**
   The AI chat worker and the conversation-retention sweep now use that route.
6. **A post-save hook is a DI registration.** `FileManagement.configurePostSaveHooks` and the list it filled are gone:
   register a `FileManagement.PostSaveHook` (by factory, to build it from the running app's services), and compose
   folds every registered hook into the `FileManagementRuntime`.
7. **`PromptContext` carries the request's services** (`Services`, `HttpContext.RequestServices`), so a system-prompt
   builder reads what the running app composed. A context built without a request uses `PromptContext.noServices`.

## Diff, per file

A test or tool that read a builder's write off `Config`:

```diff
-let config = (ServerApp.empty |> ServerApp.withDurableLineage true).Config
+let config = ServerApp.empty |> ServerApp.withDurableLineage true |> ServerApp.effectiveConfig
```

A test that applied the pre-middleware itself:

```diff
-for thunk in app.Extensions.PreMiddleware do
+for thunk in (ServerApp.composedExtensions app).PreMiddleware do
```

A record literal of `Contribution` (the constructors are unchanged, so most code needs nothing):

```diff
-{ Owner = owner; Kind = Service register }
+Contribution.service owner register
```

A record literal of `ServerApp` adds `Overrides = []`. Build it from `ServerApp.empty` instead.

A post-save hook:

```diff
-FileManagement.configurePostSaveHooks [ hook ]
+services.AddSingleton<FileManagement.PostSaveHook>(FileManagement.PostSaveHook hook)
```

A `PromptContext` record literal (tests) adds the provider:

```diff
     Scope = ResolvedScope.anonymous
+    Services = PromptContext.noServices
```

## Verification

`dotnet build` your composition root. If a test asserts on `app.Config` after a builder, or on
`app.Extensions.PreMiddleware` after `withPreMiddleware`, move it to `effectiveConfig` / `composedExtensions`.

## Rollback

Pin 0.38.0. Nothing is persisted differently. The composed application is the same in the order the builders'
docs used to require (`withConfig` first). Only compositions that broke that order behave differently, and they
now get the writes they asked for.

## The companion roots are options records

Each companion root that wrapped `ServerApp` (`{ Base: ServerApp; … }`) and mirrored its builders is now an options
record composed onto the `ServerApp` by one `withX`, through `ServerApp.withCompanion`. The companion is applied once,
when the deployment resolves, so every `ServerApp` builder works on either side of it. The delegators, `createFrom` and
the wrapper's `run` are gone: base configuration goes on the `ServerApp`, and the pipeline ends in `ServerApp.run`. A
second `withX` replaces the first. The codemod's review report names every site (rules `876-*-options`).

### Forms

```diff
-FormsServerApp.create ()
-|> FormsServerApp.withConfig config
-|> FormsServerApp.withFormSchema schema
-|> FormsServerApp.run
+ServerApp.empty
+|> ServerApp.withConfig config
+|> FormsCompose.withForms (FormsOptions.create () |> FormsOptions.withFormSchema schema)
+|> ServerApp.run
```

The configurator form `withForms (fun f -> f |> …)` becomes `withForms (FormsOptions.create () |> …)`.

### Scheduling

```diff
-SchedulingServerApp.create ()
-|> SchedulingServerApp.withConfig config
-|> SchedulingServerApp.withCalendarBridge bridge
-|> SchedulingServerApp.run
+ServerApp.empty
+|> ServerApp.withConfig config
+|> SchedulingCompose.withScheduling (SchedulingOptions.create () |> SchedulingOptions.withCalendarBridge bridge)
+|> ServerApp.run
```

`GoogleCalendarChannels.withChannelRenewal` and `MicrosoftGraphSubscriptions.withSubscriptionRenewal` take the
`ServerApp`. `MicrosoftGraphSubscriptions.compose` takes and returns `ServerApp * SchedulingOptions`.

### Peer substrate

```diff
-PeerServerApp.create ()
-|> PeerServerApp.withConfig config
-|> PeerServerApp.withLocalPeer identity
-|> PeerServerApp.withContract host
-|> PeerServerApp.run
+ServerApp.empty
+|> ServerApp.withConfig config
+|> PeerCompose.withPeer (PeerOptions.create () |> PeerOptions.withLocalPeer identity |> PeerOptions.withContract host)
+|> ServerApp.run
```

`withCommutativeCipher` is `PeerCompose.withCommutativeCipher` over the `ServerApp`. The audits
(`PeerCompose.auditAudienceBinding`, `enforce*`), `PeerSurface.describe` and `PeerGateway.surface` take the
`(app, options)` pair, and `PeerGateway.withAggregate` takes and returns it. The option-only helpers
(`cleanRoomTemplateMap`, `noisedReleaseMap`, `jobResultStore`, `peerAuthProvider`, `roundOrchestrationServices`,
`federationPreflightInput`, `auditFederationGraph`) take `PeerOptions`.

### Public rendering

```diff
-PublicRenderingServerApp.create ()
-|> PublicRenderingServerApp.withConfig config
-|> PublicRenderingServerApp.withLayout (LayoutName "page") layout
-|> PublicRenderingServerApp.run
+ServerApp.empty
+|> ServerApp.withConfig config
+|> withPublicRendering (PublicRenderingOptions.create () |> PublicRenderingOptions.withLayout (LayoutName "page") layout)
+|> ServerApp.run
```

The configurator form `withPublicRendering (fun pr -> pr |> …)` becomes
`withPublicRendering (PublicRenderingOptions.create () |> …)`. The static export is
`PublicRenderingExport.exportStatic outputDir (app, options)` (and `exportStaticWith` / `exportStaticAll` /
`exportStaticAllWith`). `HostRouteRegistration.register` takes and returns `PublicRenderingOptions`.

### Asset store

```diff
-AssetStoreServerApp.create ()
-|> AssetStoreServerApp.withConfig config
-|> AssetStoreServerApp.withRenderer renderer
-|> AssetStoreServerApp.run
+ServerApp.empty
+|> ServerApp.withConfig config
+|> AssetCompose.withAssetStore (AssetStoreCompanionOptions.create () |> AssetStoreCompanionOptions.withRenderer renderer)
+|> ServerApp.run
```

The options record is `AssetStoreCompanionOptions`: `AssetStoreOptions` already names the store's limits record, which it
carries as its `Options` field.

### Media library

```diff
-MediaLibraryServerApp.create ()
-|> MediaLibraryServerApp.withConfig config
-|> MediaLibraryServerApp.withTranscoder transcoder
-|> MediaLibraryServerApp.run
+ServerApp.empty
+|> ServerApp.withConfig config
+|> MediaCompose.withMediaLibrary (MediaLibraryCompanionOptions.create () |> MediaLibraryCompanionOptions.withTranscoder transcoder)
+|> ServerApp.run
```

The options record is `MediaLibraryCompanionOptions`, for the same reason: `MediaLibraryOptions` is its `Options` field.

### Algorithms

```diff
-app |> AlgorithmsCompose.withAlgorithms (fun a -> a |> AlgorithmsServerApp.withProvider provider)
+app |> AlgorithmsCompose.withAlgorithms (AlgorithmsOptions.create () |> AlgorithmsOptions.withProvider provider)
```

`MathNetAlgorithms.withMathNetAlgorithmProvider` is `AlgorithmsOptions -> AlgorithmsOptions`.

### Passkeys

```diff
-app |> PasskeyCompose.create config |> PasskeyCompose.run
+app |> PasskeyCompose.withPasskey config |> ServerApp.run
```

### SCIM

```diff
-app |> ScimServerApp.ofServerApp |> ScimServerApp.withScim config |> ScimServerApp.run
+app |> ScimRoutes.withScim config |> ServerApp.run
```

`ScimMode` is gone: a deployment that does not call `withScim` mounts nothing.

## The composition manifest records the plan (schema 4)

`ServerApp.compositionManifest` now carries `Plan: PlanEntry list`: one entry per contribution, in
the order `run` applies them (after companions are resolved). An entry is data with no closure in
it: the owner's `ComponentId`, a kind label (`service`, `staged`, `routes`, `validator:<name>`,
`middleware:pre` / `middleware:post`, or `hosted:<unit id>:<placement>`, with `:outbound` appended
for an outbound unit), and the required and provided seam ids, sorted. The manifest's
`SchemaVersion` moves from 3 to 4 (`CompositionManifest.PlanSchemaVersion`). Read the field
through `CompositionManifest.plan` and only when `CompositionManifest.recordsPlan` holds: a
manifest deserialised from an older document carries `null` there, and is silent about the plan
rather than claiming an empty one.

The plan is **sealed**. `BootVerificationPreflight.compositionCanonicalForm` frames it, in plan
order, under its own framing version (`toolup.compositionmanifest.plan.v1`), and `compare` reports
`ContributionAdded`, `ContributionRemoved`, `ContributionChanged` (same owner and kind, different
seams) and `ContributionMoved` (same entry, different position). Two deployments that differ only in
what a companion contributes, or in the order contributions run, now seal to different bytes.

**What a sealed deployment sees on upgrade.** Nothing breaks. A binding sealed at schema 3 or below
has no plan, so the plan block is not framed for it and it re-canonicalises to exactly the bytes it
was sealed over: its seal still verifies. Its plan is never compared (an absent recording is not
evidence that nothing moved), and the boot reports `VerifiedUnrecorded` with a
`ContributionPlanUnrecorded` item listing the running contributions. That verdict is affirmative,
so a `RefuseOnDrift` deployment keeps serving. Re-seal the composition binding to close the gap;
from then on the plan is compared, and a contribution that moves is drift. A deployment whose plan
is empty sees plain `Verified`, since there is nothing to be silent about.

Code that matches `CompositionDrift` or `CompositionUnrecorded` exhaustively gains the new cases.
A manifest built by full record literal adds `Plan = []` (or goes through `CompositionManifest.build`,
which already does).
