# Migration — Phase 771: the hardening baseline, observed at boot

**Status:** additive, one new case on a closed union. A deployment that neither mounts the verification
route nor declares a floor is byte-for-byte unchanged: nothing is probed and nothing is registered. No
consumer action is required to upgrade.

## What changed

- **`HostingBaseline` (Server, `Server/HostingBaseline.fs`).** A closed list of ten request-edge
  controls (`HostingControl`), each observed on the composed pipeline at boot rather than read from
  `ServerConfig`: two probe requests through `IServerHost.Invoke` for the header, CORS and
  forwarded-scheme controls, and the container plus the preflight's record for the rest.
  `HostingBaseline.classify` judges an observation against a `HostingBaselinePolicy`;
  `HostingBaseline.atBoot` observes, records and enforces. The control table and the
  "what this does not claim" paragraph: [`../platform/security.md`](../platform/security.md#hardening-baseline).
- **`ServerApp.withHostingBaselinePolicy`** declares a floor. Under `CompositionProfile.Verified` a
  floor control observed off refuses the start with the new
  `CompositionProfileRefusal.HostingBaselineBelowFloor`; under `Standard` it only marks report rows.
- **`ServerApp.withDeploymentVerification`** now also asks `run` to observe the baseline before it
  serves (two probe requests at boot), so the report has something to say. It refuses nothing on its
  own.
- **The report gains a thirteenth section, `hardening-baseline`**, with one row per control, the
  verdict (`verified` / `observed` / `failed`), and the not-proved statement
  `hardening-baseline-is-presence-not-adequacy` carried as data. Appended, so every earlier section
  keeps its canonical line; the verdict digest moves once, as it does whenever the report grows. The
  evidence reaches the report through `IHostingBaselineEvidence` / `withHostingBaseline` and the boot
  record `HostingBaselineRecord`.

## Adopting the floor

```fsharp skip=fragment
app
|> ServerApp.withHostingBaselinePolicy (
    HostingBaselinePolicy.verified [ HostingControl.StrictTransportSecurity; HostingControl.ContentSecurityPolicy ]
)
|> ServerApp.run
```

A serverless root that composes with `compose` and drives the host itself calls
`HostingBaseline.atBoot host config` after composing and before serving its first invocation.

## What can break

- **An exhaustive `match` on `CompositionProfileRefusal`** stops compiling (FS0025 as an error under
  warnings-as-errors) until it handles `HostingBaselineBelowFloor`. `CompositionProfileRefusal.describe`
  already does; most callers use it.
- **A test asserting the report's last section id** now sees `hardening-baseline`. Read a section by id,
  not by position.

## Verifying

Mount the route, boot, and read `GET` the verification report: the `hardening-baseline` section lists
every control. Under the verified profile with HSTS in the floor, bind to a non-loopback address without
`RequireHttps`: the start is refused with a message naming `strict-transport-security`.

## Rollback

Drop `withHostingBaselinePolicy`. The section stays informational while the route is mounted.
