# ToolUp.AuthProviders.ClerkUI

Client-side Clerk sign-in UI for `ToolUp.Platform`. Wraps Clerk's React components and surfaces them through the `AuthUIProvider` delegate registry; deployments select it through the vendor-neutral `ClientConfig.Auth.AuthUI` case via this package's typed smart constructor:

```fsharp skip=fragment
{ ClientConfig.defaults with
    Auth.AuthUI = ClerkRegister.authUI { PublishableKey = key }   // ProviderAuthUI ("clerk", …)
    Composition.Handlers =
        { ClientHandlerRegistry.empty with
            AuthUIHandlers = [ ClerkRegister.handler ] } }
```

(The vendor-named `ClerkAuthUI` core case was removed in Phase 1074; the neutral form is the only one.)

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
