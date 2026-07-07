# Migration: vendor-neutral `ClientConfig.AuthUI` (Phase 494)

**What changed.** `AuthUIMode` gains a vendor-neutral case, `ProviderAuthUI of tag: string *
config: obj`, that selects a sign-in companion by the same tag the
`ClientConfig.Handlers.AuthUIHandlers` registry dispatches on. The vendor-named `ClerkAuthUI of
ClerkUIConfig` case is now `[<System.Obsolete>]` — it still compiles and behaves identically, but
emits warning FS0044 at every use site. Removal happens at a later major version. `NoAuthUI`,
`OidcAuthUI`, and `CustomAuthUI` are unchanged. Design rationale:
[`../platform/auth-ui-vendor-neutrality.md`](../platform/auth-ui-vendor-neutrality.md).

## Config diff per consumer

Only deployments that set `AuthUI = ClerkAuthUI …` need to change anything. One line:

```fsharp
open ToolUp.AuthProviders   // ClerkRegister — already imported for the handler value

{ ClientConfig.defaults with
-     AuthUI = ClerkAuthUI { PublishableKey = BundleConstants.clerkPublishableKey }
+     AuthUI = ClerkRegister.authUI { PublishableKey = BundleConstants.clerkPublishableKey }
      Handlers =
          { ClientHandlerRegistry.empty with
              AuthUIHandlers = [ ClerkRegister.handler ] } }   // unchanged
```

`ClerkRegister.authUI` is the companion's typed smart constructor — it returns
`ProviderAuthUI ("clerk", box cfg)` so no boxing (and no vendor-named SDK case) appears in the
consumer's config. Writing `ProviderAuthUI ("clerk", box { PublishableKey = … })` directly is
equivalent. The handler wiring, the Vite `__CLERK_PUBLISHABLE_KEY__` define, and the server-side
auth provider are all untouched.

Deployments on `NoAuthUI` / `OidcAuthUI` / `CustomAuthUI`: no change, byte-for-byte identical.

## Verification

1. `dotnet build` the client project — the FS0044 deprecation warning disappears.
2. `dotnet fable -o output` — bundle compiles; boot summary still logs `authUI=clerk` (the neutral
   case reports its tag).
3. Sign-in flow behaves identically — `AuthUIProvider.gate` dispatches `ProviderAuthUI ("clerk", _)`
   through the same `"clerk"` registry entry `ClerkAuthUI` used.

## Rollback

Keep (or revert to) `AuthUI = ClerkAuthUI { … }`. The case remains fully functional for the rest of
this major version; the only cost is the FS0044 warning (suppressible with `#nowarn "44"` if a
consumer must pin the old form warning-free).
