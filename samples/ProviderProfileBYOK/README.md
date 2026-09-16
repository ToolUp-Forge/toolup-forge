<!--
SPDX-License-Identifier: Apache-2.0
Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
-->

# ProviderProfileBYOK — a non-AI app composing the shared BYOK component

Phase 44's worked example, and its standing proof. A deployment here holds a
multi-provider BYOK catalogue — several keys, tagged, routed per surface, with a
fallback chain — and **never composes the AI assistant**. That is the whole point of
the Phase 42.B decoupling: the provider-profile substrate belongs to the platform,
not to `ToolUp.AI`, so the settings surface for it must too.

## The two halves

| Half | Project | In `ToolUp.Forge.sln`? |
|---|---|---|
| Server composition root | `src/ProviderProfileBYOK.Server/` | yes — `dotnet build ToolUp.Forge.sln` compiles it |
| Fable client composition | `src/ProviderProfileBYOK.Client/` | no — Fable-only, like `samples/MinimalClient` |

The client half is deliberately outside the solution for the same reason
`MinimalClient` is: it exists to be driven through the Fable compiler
(`dotnet fable -o output --noCache` from its own directory), and a `dotnet build`
of the solution cannot prove anything about transpilation.

## What the server half shows

Twelve executable lines. The composition act is one call:

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withProviderProfile (BlobProviderProfile.create storage)
|> ServerApp.run
```

`withProviderProfile` registers the `IProviderProfile` DI singleton **and** mounts the
`IProviderProfileApi` remoting handler — one gate, the shape every other optional route
in `BuildRouteHandlers` uses. An app that never calls it mounts no route and resolves no
store (GP 13).

## What the client half shows

The other three things, and nothing else:

```fsharp skip=fragment
ProviderProfileConfig.create [ "rental.gateway"; "rental.settlement" ]
```

That is the *whole* composition surface for the default case. `withVerify` adds a
"verify this key" delegate when the app has something that can check a key; this sample
deliberately supplies **none**, so the form renders the clearly-labelled "not verified"
state and entries still persist — which is exactly the case a non-AI consumer is in, and
exactly why verification is a delegate rather than a dependency.

## Why it is a proof and not only a demonstration

Neither project references `ToolUp.AI` in any tier, and
`ProviderProfileZeroAIDependencyTests` in `ToolUp.Platform.Tests` asserts the same thing
from the other end — that the built `ToolUp.Platform.Client` assembly, which is where the
component lives, carries no `ToolUp.AI*` assembly reference at all. A sample can drift;
the assembly-reference assertion cannot.
