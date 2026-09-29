# CLAUDE.md — samples

Conventions shared by the runnable samples under `samples/`. Each sample's
own README carries its run + verify procedure; this file holds only what
crosses samples.

## Every sample builds under a gate

`samples/` is not in `ToolUp.Forge.sln`, so `dotnet build ToolUp.Forge.sln` never compiled it. The
`Samples` leg of `VerifyAll` (also `dotnet run --project Build.fsproj -- VerifySamples`) does:

- It enumerates every `*.fsproj` under `samples/` (skipping `obj/`, `bin/`, `node_modules/`,
  `output/`) and `dotnet build`s each one, so a red run names every broken sample.
- A sample that Fable compiles (it declares `Fable.Core` or references `ToolUp.Platform.Client`) is
  also **transpiled** with `dotnet fable`, in the **full lane only** — about two minutes each. It runs
  IN the sample's directory, so **a Fable sample carries its own `.config/dotnet-tools.json`** pinning
  `fable`; one without it fails the leg by name.
- The leg asserts what it built against the directory. A project deliberately not built is listed in
  `SampleGate.excluded` (root `SampleGate.fs`) with its reason; today the list is empty.
- **Adding a sample needs no edit here** — it is discovered. Do keep it building: a new sample that
  does not is a red gate, which is the point.

## Port allocation (samples band)

Every sample that binds a fixed network listener claims a **10-port band**
recorded here, so concurrently-running samples never collide. Rules:

- New samples allocate from the **13xxx range**: it is browser-safe (no entry
  on Chromium's restricted-ports list) and sits well above the dynamic ranges
  Windows services squat on (CDPSvc is known to grab ports in the upper
  5000s, breaking dev binds).
- Take the next free band **above the highest one listed**, skipping any band
  a downstream deployment has already taken (gaps in the table below are
  deliberate — do not backfill them).
- A sample uses the first port of its band by default; the remaining nine are
  headroom for auxiliary listeners.

| Sample | Band | Notes |
|---|---|---|
| `PublicSite` | `4010–4019` | Single-site SSR reference (uses 4010). Historical band — predates the 13xxx convention; allocate no new samples from the 4000s. |
| `PublicSiteWithModules` | `4020–4029` | PublicRendering + modules hybrid (uses 4020). Historical band, same caveat. |
| `PrerenderApp` | `13930–13939` | Declared prerender routes + sitemap.xml worked example (uses 13930). |
| `MultiSitePublic` | `13950–13959` | Multi-host public rendering (uses 13950) — one process, default site + two Host-header-matched satellites. |

Samples without a fixed listener need no entry: `HelloWorld` (Vite dev server
on 8080 + server default), `MinimalApp` / `MixedMode` /
`ProviderProfileBYOK.Server` (boot on `TOOLUP_PORT` or the SDK default when
run manually), and the non-binding `MinimalClient` / `FormsAndAI` /
`ProviderProfileBYOK.Client`.
