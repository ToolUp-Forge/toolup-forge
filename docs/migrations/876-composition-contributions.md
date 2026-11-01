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
