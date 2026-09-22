# Phase 191 — one consent gate, three call sites

**What changes.** The client tier had two consent-gated side effects — the
AdSense bundle's script load (`AdSlot`, Phase 159) and product telemetry
(`Telemetry.trackVia`, Phase 163) — and each carried its own hand-written
fail-closed gate at its own call site. Both were correct; neither was
reusable, so a third-party embed you add yourself (a video player, a support
widget, a marketing pixel) got nothing for free.

This phase adds the seam both now run through, and makes it public so your
own script is the third call site rather than a fourth re-derivation.

New surface (additive — nothing was removed, nothing changed shape):

- `Components.ConsentGatedScript.ConsentGated` — the transport-free gate. No
  React, no DOM:
  - `isPermitted: ConsentDecision -> bool` — only an explicit `Granted`.
  - `permits: IConsentProvider -> ConsentCategory list -> Async<bool>` —
    every required category, short-circuiting on the first refusal.
  - `run: IConsentProvider -> ConsentCategory list -> (unit -> Async<unit>)
    -> Async<unit>` — a one-shot gated effect.
  - `watch: IConsentProvider -> ConsentCategory list -> (bool -> unit) ->
    IDisposable` — the gate for a mounted surface: fires once with the
    current answer and again on every transition, including a withdrawal.
- `Components.ConsentGatedScript.ConsentGatedScript` — the Feliz wrapper over
  `watch`: `Html.none` and no load until every required category is granted.

**Consumer action: none.** `AdScriptLoader.ensureLoaded` and every Phase 163
telemetry entry point keep their exact signatures and behaviour; the defaults
are unchanged (`ConsentProvider = NoConsentProvider` grants only `Necessary`,
`AdPanel = NoAdPanel` renders nothing and loads nothing). This is worth
adopting only when you have a consent-categorised script of your own.

## Gating your own script (the third call site)

Before — the shape this replaces, re-derived by hand at every embed:

```fsharp
[<ReactComponent>]
let SupportWidget () =
    let granted, setGranted = React.useState false

    React.useEffectOnce (fun () ->
        let provider = ConsentProvider.current ()
        let mutable cancelled = false
        let evaluate state = if not cancelled then setGranted (ConsentState.hasAll [ Functional ] state)
        async { let! s = provider.GetCurrentState() in evaluate s } |> Async.StartImmediate
        let sub = provider.OnStateChanged evaluate
        FsReact.createDisposable (fun () -> cancelled <- true; sub.Dispose()))

    React.useEffect ((fun () -> if granted then loadSupportWidget ()), [| box granted |])
    if granted then Html.div [ prop.id "support-root" ] else Html.none
```

After:

```fsharp
open Components

let SupportWidget () =
    ConsentGatedScript.ConsentGatedScript
        [ Functional; ThirdPartyEmbeds ]
        loadSupportWidget
        (Html.div [ prop.id "support-root" ])
```

`loadSupportWidget` is not reached, and the `div` is not rendered, until
every listed category is granted. A withdrawal closes the gate again.

For an effect that happens at a point in time rather than for as long as a
component is mounted — an emission, a one-off beacon — use `ConsentGated.run`
directly:

```fsharp
ConsentGated.run (ConsentProvider.current ()) [ Analytics ] (fun () ->
    postConversion payload)
|> Async.StartImmediate
```

And for a surface that needs the answer itself (to size a placeholder, to
show a "consent required" affordance), subscribe with `ConsentGated.watch`
and dispose the handle when the surface unmounts.

## What the gate guarantees

- **Fail-closed, always.** Only `Granted` opens it: `Denied` and
  `NotYetDecided` both suppress (the pre-banner state is not consent), a
  provider that throws suppresses, and the default `NoOpConsentProvider`
  grants nothing but `Necessary` — so a deployment with no CMP wired loads
  no gated script at all. There is no configuration that opens it on
  anything else.
- **It never throws at your call site.** A gated effect that throws resolves
  to `unit`; a third-party loader cannot break the surface that declared it.
- **An empty category list permits**, matching `ConsentState.hasAll []` and
  the render-side `ConsentGate` — it is the declaration "this effect is not
  consent-categorised", not a hole.
- **It is a gate, not an interception.** `AdScriptLoader.ensureLoaded` and
  the other script loaders remain public and callable directly: a caller
  that reaches past the seam is still ungated, which is why the seam is the
  documented path rather than a claim about what is possible. Making the
  loaders unreachable would be a public-API removal, i.e. breaking.

## Verification

```powershell
dotnet src\ToolUp.Platform.Tests\bin\Debug\net10.0\ToolUp.Platform.Tests.dll --filter-test-list "Phase 191"
```

16 cases: the no-op before consent, activation on a later grant, withdrawal,
disposal, the fail-closed arms, the shipped defaults loading nothing, and a
consumer-registered third script obeying the same gate. Phase 163's pack runs
unchanged beside it.

## Rollback

Revert the commits. The two refactored call sites are behaviour-preserving —
their own tests pass unchanged either way — and nothing outside this tier
references the new module, so removing it costs only the consumers that have
adopted it.
