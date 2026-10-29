# 1052 — `ServiceConfig.append`, `Contribution.service`, `ServerApp.addServiceConfig`

**What changes.** A companion or builder no longer folds its DI registrations into
`ComposeExtensions.ServiceConfig`. It adds them to `ServerApp.Contributions`: a companion as
`ServerApp.addContribution (Contribution.service owner register)` under its own `ComponentId`, a hook or
an application as `ServerApp.addServiceConfig register`. `ServerApp.run` applies the consumer's own
`Extensions.ServiceConfig` first and then every contribution, in the order added, at the same fixed point
as before, so a deployment's descriptor list is unchanged (pinned by the composition baseline's
`default-service-descriptors.txt`). Two consequences:

1. **A later `withExtensions` no longer discards a companion's registrations.** It still sets the whole
   record — handlers, middleware and the consumer's own `ServiceConfig` — but contributions live beside it.
2. **`app.Extensions.ServiceConfig` now holds only the consumer's own registrations.** Code that read it to
   build a provider from a composed app (typically a test) must read `ServerApp.serviceConfig app`.

`ServerApp` gains a field (`Contributions`), so a full record literal of `ServerApp` no longer compiles; build
from `ServerApp.empty` with a copy-and-update. Rides the 0.38.0 draft, which is already breaking.

**Diff per file.**

```fsharp skip=fragment
// A hand-written fold — refused by `VerifyServiceConfigFold` inside this repository.
{ app with
    Extensions = {
        app.Extensions with
            ServiceConfig =
                match app.Extensions.ServiceConfig with
                | None -> Some register
                | Some existing -> Some(fun s -> register (existing s)) } }
// becomes
app |> ServerApp.addServiceConfig register

// Reading what a composed app registers:
match app.Extensions.ServiceConfig with ...
// becomes
match ServerApp.serviceConfig app with ...
```

A function that appends to an option of a function directly uses `ServiceConfig.append register current`.

**Verify.** Build; a test that applies `ServerApp.serviceConfig app` to a `ServiceCollection` sees the
companion's registrations; a `withExtensions` placed after a companion no longer loses them.

**Rollback.** Revert to the previous SDK version; consumer code written against `addServiceConfig` /
`serviceConfig` must go back to folding `Extensions.ServiceConfig` by hand.
