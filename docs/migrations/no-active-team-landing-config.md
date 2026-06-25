# Parameterized no-active-team landing — `ClientConfig.NoActiveTeamLanding`

**Ships in:** `ToolUp.Platform.Client` (`NoActiveTeamLandingConfig`, `NoActiveTeamLanding`,
`NoActiveTeamLandingUI`, `ClientConfig.NoActiveTeamLanding`,
`ClientConfig.effectiveNoActiveTeamLandingId`).

## What changes

The no-active-team gate ([no-active-team-landing.md](no-active-team-landing.md)) previously had a
single opt-in: `ClientConfig.NoActiveTeamLandingModuleId`, which points the gate at a landing module
the **consumer hand-rolls** (an Elmish `Model`/`Msg`/`init`/`update` + a Feliz view + `register`,
~40 lines of boilerplate around two strings). This adds a second, lightweight opt-in for the common
case where a deployment only needs to customise the copy:

```fsharp
type NoActiveTeamLandingConfig = {
    Label: string                 // sidebar entry label + group, e.g. "Welcome"
    Title: string                 // page heading, e.g. "You're not on a team yet"
    Body: string                  // body paragraph beneath the heading
    Icon: ReactElement option     // None ⇒ Icons.home
}

// On ClientConfig:
NoActiveTeamLanding: NoActiveTeamLandingConfig option   // default None
```

When `Some cfg` on a deployment that declares a `Team` surface, the SDK **registers the landing
module for you** (stable id `NoActiveTeamLanding.moduleId = "AwaitingTeam"`, group/label from
`cfg.Label`, `Visibility.visibleTo [ UserKind ]`) and wires the existing gate to it — no
consumer-side module, `register`, or `NoActiveTeamLandingModuleId` wiring needed.

### Behaviour

- The gate (sidebar collapse, content-surface routing, admin-group preservation, server-side
  `[<TenantScoped>]` authority) is **unchanged** — this only adds a second way to supply the landing
  module. Both paths resolve through the new `ClientConfig.effectiveNoActiveTeamLandingId`.
- **Precedence:** `NoActiveTeamLandingModuleId` wins. If a deployment sets both, the explicit custom
  module is used and the built-in is NOT injected (the consumer fully owns the landing).
- Inert on non-team surfaces and off by default (`None`) — byte-identical behaviour for every
  deployment that doesn't opt in (GP 13).
- Once an active team upgrades the subject to `TeamMemberKind`, `Visibility.visibleTo [ UserKind ]`
  drops the entry, exactly as a hand-rolled landing would.

## Diff to apply

**Nothing for existing consumers** — additive and default-`None`. A deployment that today hand-rolls
an `AwaitingTeam` module purely for copy can delete it and replace the wiring with:

```fsharp
let clientConfig =
    { ClientConfig.create handlers with
        NoActiveTeamLanding =
            Some {
                Label = "Welcome"
                Title = "You're not on a team yet"
                Body = "An administrator needs to add you to a team before the tools become available."
                Icon = None
            } }
```

Drop the consumer's landing module file, its `register()` call in the module list, and the
`NoActiveTeamLandingModuleId = Some "…"` line. Deployments that need bespoke layout/behaviour keep
hand-rolling and using `NoActiveTeamLandingModuleId` — that path is unchanged.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- Full Expecto suite green, including the new `NoActiveTeamLanding` pack
  (`resolveLandingId` precedence: explicit-id-wins / config-only → built-in id / neither → None).
- `cd samples/MinimalClient && dotnet fable -o output --noCache` — the client tier (the new
  `NoActiveTeamLanding.fs` + `NoActiveTeamLandingUI.fs` factory + the `prepareModules` injection)
  compiles.
- Public-API baseline for `ToolUp.Platform.Client` regenerated (additive surface).

## Rollback

Additive throughout — revert the change; no consumer that leaves `NoActiveTeamLanding = None` (every
existing one) is affected. A consumer that adopted the config path reverts to its hand-rolled module
+ `NoActiveTeamLandingModuleId`.
