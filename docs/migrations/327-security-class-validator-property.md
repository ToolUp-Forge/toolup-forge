# Opt-in `ISecurityClassValidator` marker

**Status:** additive. External `IConfigValidator` implementers need **no change** — a validator opts
into security-class by *also* implementing the new marker interface.

## What changes

A new marker interface ships in `ToolUp.Platform.Core` (`Shared/IConfigValidator.fs`):

```fsharp
type ISecurityClassValidator = interface end
```

It has no members. An `IConfigValidator` that *also* implements `ISecurityClassValidator` is
**security-class**: its bypass under `ServerConfig.SkipPreflight = true` is a security hole
(identity-spoofing, unauthenticated-access, plaintext-secret, CSRF, or cross-instance-auth-state).

The preflight aggregator (`ConfigValidatorAggregator.fs`) now derives the set of validators that run
even under `SkipPreflight` by **type-testing** for the marker
(`match box v with :? ISecurityClassValidator -> true | _ -> false`), instead of a hand-maintained
`securityClassValidatorNames` name set. **That name set is removed.** Because the classification is a
type-test over the validators themselves, a newly-authored security validator can't drift out of the
always-run set: if it forgets the marker, a first-party guard test names it.

Three first-party validators that were security-class in behaviour but omitted from the old name set
now carry the marker: `forwarded-headers-trust`, `cors-config`, and
`share-token-signing-key-provenance`.

## Behaviour / compatibility

- **GP 11 — additive.** Absence of the marker is the non-security classification. An existing
  `IConfigValidator` — first-party or downstream — is unchanged and keeps its prior
  (bypassable-under-`SkipPreflight`) behaviour byte-for-byte. The three newly-marked first-party
  validators tighten `SkipPreflight` (they now survive it, as they always should have) — a deployment
  relying on `SkipPreflight` to bypass a real CORS / forwarded-headers / share-token-provenance
  finding was already misconfigured.
- **`SkipPreflight` still bypasses non-security validators.** The emergency-boot lever for a noisy
  companion probe is unchanged for every validator without the marker.

## Diff per consumer — external `IConfigValidator` implementers

**None required.** A validator that is not a security guard needs no change at all — the absence of
the marker is exactly its current (correct) classification.

To *opt* a validator into security-class (so `SkipPreflight` can't silently bypass it), add the
marker interface alongside the existing `IConfigValidator` implementation:

```fsharp
type MyAuthValidator(config, ?timeout) =
    // Security-class: SkipPreflight must not silently bypass this guard.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "my-auth-guard"
        member _.Timeout = timeout
        member _.Validate() = async { ... }
```

The marker has no members, so `interface ISecurityClassValidator` (no `with` block) is the whole
addition. An object-expression validator opts in the same way:

```fsharp
{ new IConfigValidator with
    member _.Name = "my-auth-guard"
    member _.Timeout = timeout
    member _.Validate() = async { ... }
  interface ISecurityClassValidator }
```

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `ToolUp.Platform.Tests` — `ConfigValidatorSecurityClass` pack: every shipped auth/secret/CSRF/
  provenance validator implements `ISecurityClassValidator`; a `SkipPreflight` run still executes
  every security-class validator and still aborts on a security-class `Error`; an unmarked probe is
  skipped.
- `ConfigValidatorAggregator` pack: the SkipPreflight partition now keys on the marker type-test,
  unchanged outcomes.

## Rollback

Re-add the `securityClassValidatorNames` set and restore the aggregator's `isSecurityClass` to a name
lookup; the `ISecurityClassValidator` marker can stay (it is inert if unused). External implementers
that added the marker are unaffected either way.
