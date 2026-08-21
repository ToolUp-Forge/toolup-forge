# Vendor-neutral `ClientConfig.AuthUI` — the `ProviderAuthUI` case (Phase 494)

## The problem

The auth-UI *behaviour* has been properly seamed since Phase 13a: sign-in UI companions export
`(tag, handler)` values (`AuthUIHandler = obj -> ReactElement -> ReactElement`), consumers add them
to `ClientConfig.Handlers.AuthUIHandlers`, and `AuthUIProvider.gate` dispatches on the tag at
render time. Missing handlers fail loud at `Client.run`. The registry never names a vendor — it is
a `Map<string, AuthUIHandler>`.

But the core selector type contradicted its own registry: `AuthUIMode` carried a vendor-named case,
`ClerkAuthUI of ClerkUIConfig`. Every consumer reading the core config saw Clerk privileged over
any other provider, and a third-party sign-in companion could not be selected by a first-class case
at all — it had to squat on `CustomAuthUI` (bypassing the registry) even though the registry would
dispatch its tag perfectly well.

## The decision

Add one vendor-neutral case that mirrors the registry's own keying:

```fsharp skip=fragment
| ProviderAuthUI of tag: string * config: obj
```

and demote `ClerkAuthUI` to a deprecated alias (`[<System.Obsolete>]`, removal at a later major
version). `NoAuthUI`, `OidcAuthUI` (OIDC is a protocol, not a vendor) and `CustomAuthUI` are
unchanged.

### Why the payload is `obj` and not a neutral record

Three candidate payload shapes were considered:

1. **A typed neutral record** (e.g. `{ Tag: string; Settings: Map<string, string> }`) — rejected.
   Provider configs are not stringly-shaped: `OidcUIConfig` carries `string list`, `option`s, and
   future providers may carry callbacks. A string map cannot express what `ClerkUIConfig` (or any
   richer config) carries without a lossy encoding, and widening the record for each new need
   re-creates the vendor-coupling problem one field at a time.
2. **A generic case** (`ProviderAuthUI of tag: string * config: 'T`) — impossible; a DU case cannot
   introduce its own type parameter without making `AuthUIMode` (and therefore `ClientConfig`)
   generic.
3. **`obj`, erased at the existing sanctioned boundary** — chosen. The handler the tag selects
   *already* receives its config as `obj` (`AuthUIHandler`'s first argument) and unboxes it —
   `AuthUIProvider` handler dispatch is sanctioned type-erasure boundary #6 in the repo's erasure
   list. `ProviderAuthUI` moves the box from the SDK's vendor-named arm to the consumer's config
   literal — the erasure surface is unchanged, symmetric (companion boxes, same companion's handler
   unboxes), and no new boundary is introduced.

### Consumer ergonomics — companions export a typed smart constructor

Consumers never box by hand. Each sign-in companion exports a typed constructor next to its
handler value:

```fsharp
// ClerkUI companion (src/AuthProviders/ClerkUI/ClerkRegister.fs)
[<Literal>]
let Tag = "clerk"

let authUI (cfg: ClerkUIConfig) : AuthUIMode = ProviderAuthUI(Tag, box cfg)
```

so a Clerk deployment writes:

```fsharp
open ToolUp.AuthProviders

{ ClientConfig.defaults with
    AuthUI = ClerkRegister.authUI { PublishableKey = key }
    Handlers = { ClientHandlerRegistry.empty with AuthUIHandlers = [ ClerkRegister.handler ] } }
```

The type safety lives at the companion seam (the constructor and the handler agree on the concrete
config type by construction), which is exactly where it lives for every other handler in
`ClientHandlerRegistry`.

### How `ClerkUIConfig` maps

`ClerkUIConfig` stays in core, un-deprecated — it is the payload type, not the vendor coupling
(same posture as `OidcUIConfig`). `ClerkAuthUI cfg` and `ProviderAuthUI("clerk", box cfg)` are
dispatched identically: `AuthUIProvider.gate` looks up tag `"clerk"` and hands the boxed
`ClerkUIConfig` to the registered handler. `Client.run`'s fail-loud validator covers the neutral
case by validating the caller-supplied tag against the handler list.

### What a third-party companion registers

Exactly what ClerkUI registers: a `(tag, handler)` value for `AuthUIHandlers`, plus a typed
`authUI : MyConfig -> AuthUIMode` smart constructor returning `ProviderAuthUI("mytag", box cfg)`.
No SDK edit, no `CustomAuthUI` squatting, first-class selection in the core config.

## Migration

See [`../migrations/494-vendor-neutral-auth-ui.md`](../migrations/494-vendor-neutral-auth-ui.md).
Additive in this phase — `ClerkAuthUI` still compiles (with a deprecation warning); removal is a
later major-version act.
