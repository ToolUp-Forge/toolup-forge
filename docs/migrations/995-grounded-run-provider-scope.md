# A grounded narrative run is funded by the scope it carries

**Ships in:** ToolUp.Platform.Server (`GroundedNarrativeOutcome.GroundedNarrativeUnfunded`,
`GroundedNarrativeRequest.Access`), ToolUp.Platform.Core (`AccessContext.forConfigScope`), ToolUp.AI.Server
(`GroundedNarrativeRunner`), ToolUp.Facts.Server (`GroundedNarrativeTrigger`), ToolUp.Reporting.Server
(`GroundedNarrativeProducer`). Breaking, MINOR: 0.25.0 (Phase 995).

**Affected:** a deployment that composes grounded narratives (`GroundedNarratives.compose`), and any code that
matches exhaustively on `GroundedNarrativeOutcome`.

## What changes

**1. The run resolves its AI provider under the scope it carries.** Both triggers pass
`GroundedNarrativeRequest.Access = None`. `None` used to resolve the provider as the definition's principal,
which holds no AI configuration of its own. Under `AIFallbackPolicy.StrictBYOK` every run therefore failed,
however the team had configured its key. `None` now resolves under the run's scope. A team scope
(`team-{id}`) resolves as a member of that team, and a user scope (`user-{id}`) resolves as that user
(`AccessContext.forConfigScope`). The key configured at that scope funds the run. The definition's principal is
still the actor the narrative is read, checked, certified and indexed as. A scope that names no team or user
still resolves as the principal. An explicit `Access = Some ctx` is unchanged.

**2. A scope with no usable key is a typed, permanent outcome.** When the provider factory answers that the
scope holds no provider its policy allows (`ProviderResolutionError`), the run returns
`GroundedNarrativeUnfunded reason`, not `GroundedNarrativeFailed`. It records a `GroundedNarrativeFailed` audit
row whose outcome is `unfunded`. The data-arrival job returns `PermanentFailure`, so it does not retry. A
subscription run already treated a producer error as terminal. A factory that THROWS (for example, a secret
store outage) is still `GroundedNarrativeFailed`, which the data-arrival job retries.

**3. `PlatformOnly` and `PermissiveWithPlatformFallback` with no team configuration are unchanged.** The
platform key funds the run whatever the scope. Since the run now resolves as a team member, a team-scoped
platform key (`IPlatformAIKeyStore`) or a team's platform-provider choice applies to that team's runs, as it
already did to its interactive requests.

## What to do

- **If you match on `GroundedNarrativeOutcome`, add the case:**

  ```diff
   | GroundedNarrativeFailed reason -> retryLater reason
  +| GroundedNarrativeUnfunded reason -> askForAKey reason
  ```

- **Under strict bring-your-own-key, store the key at the scope the runs belong to.** That is the team's AI
  settings for a team deployment. A key stored for the run's principal is no longer read.
- **If you relied on the principal resolution,** pass it explicitly:
  `Access = Some(AccessContext.unrestricted (AuthenticatedUser principal))`.

## Verify

`GroundedNarrativeTests`, list "Phase 995 E", runs the real `DefaultAIProviderFactory`. Under strict
bring-your-own-key, a key at the team scope funds the data-arrival job, which publishes. With no key at the
scope the result is `PermanentFailure`, with no model call and nothing published, and another team's key is
never read. Under platform-only, the platform key funds the run. A named `Access` still takes precedence.

## Rollback

Pin 0.24.1. Or keep 0.25.0 and pass the principal's context explicitly, as above. That restores the previous
resolution for every run.
