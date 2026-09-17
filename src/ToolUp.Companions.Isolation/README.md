# ToolUp.Companions.Isolation

The native-boundary isolation seam. A companion that wraps a memory-unsafe native library
(Tesseract + Leptonica, SkiaSharp, Verovio, any P/Invoke-backed parser) runs that library **in a
sacrificial child process** when the bytes it parses are untrusted — an upload, a connector
payload, a knowledge-base document — so a parser bug in the native layer is a typed refusal in
the host rather than in-process code execution. In-process stays the default for trusted,
host-authored input; nothing changes until a path opts in (GP 11 / GP 13).

## The seam

```fsharp skip=fragment
open ToolUp.Companions.Isolation

/// A companion exposes one entry point per native operation: public, parameterless
/// constructor, bytes in / bytes out. It runs INSIDE the worker.
type MusicXmlToSvgEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            use toolkit = Toolkit.Create()
            match toolkit.LoadData(Text.Encoding.UTF8.GetString request.Input) with
            | Error err -> Error(string err)                    // → IsolationRefusal.EntryFailed
            | Ok() -> toolkit.RenderToSvg 1 |> Result.map Text.Encoding.UTF8.GetBytes

// Compose once. OutOfProcess resolves the worker from the host's own layout.
let isolation = CompanionIsolation.ofMode (IsolationMode.OutOfProcess IsolationLimits.defaults)

// Call per untrusted payload. A native crash is WorkerCrashed; a hang is TimedOut;
// a memory burst is MemoryCapExceeded; the host process is unaffected in every case.
let! outcome = CompanionIsolation.run<MusicXmlToSvgEntry> isolation { Args = []; Input = uploadBytes }
```

`IsolationRefusal` is the whole failure vocabulary: `WorkerCrashed` / `TimedOut` /
`MemoryCapExceeded` / `EntryFailed` / `ProtocolViolation` / `WorkerUnavailable`.

## How the worker is started

`dotnet exec --runtimeconfig <host>.runtimeconfig.json --depsfile <host>.deps.json
ToolUp.Companions.Isolation.dll --worker`. The child is handed the **host's** runtime config and
dependency manifest, so it resolves exactly the host's closure — every companion assembly and every
`runtimes/<rid>/native/*` the host can load — with no second artefact to ship and no hook in the
consumer's `main`. One request per process over a length-prefixed binary pipe on stdin / stdout.
A layout the resolver cannot see (a single-file publish) is `WorkerUnavailable` with the reason
named; `IsolationMode.OutOfProcessWith` takes an explicit `WorkerLauncher`.

## Limits — what is bounded and what is not

- **Timeout** — wall-clock per call, start-up included; the worker is killed past it.
- **Memory cap** — three lines, and which one holds depends on the host:
  1. **Windows — a kernel-enforced Job Object (the first line).** The worker is assigned to a job
     whose per-process and job-wide commit limits are the cap, with `KILL_ON_JOB_CLOSE` so a host
     that dies takes its worker with it. The kernel refuses the allocation that would cross the cap
     in the allocating thread, so a native parser cannot overshoot it; the refused commit reaches
     the host on the job's completion port and the call answers `MemoryCapExceeded`, whatever the
     worker said afterwards (a managed `OutOfMemoryException` is caught inside the worker and would
     otherwise read as a clean rejection). A Windows host that cannot apply the job refuses the
     call (`WorkerUnavailable`, naming the Win32 error) rather than running under the softer lines
     alone. `ProcessIsolation.kernelMemoryCapSupported` reports whether this line exists.
  2. **The resident-set sampler** — every 10 ms, killed on the first sample over the cap. A poll,
     so a burst can overshoot between samples.
  3. **The worker's managed heap** — hard-limited to the same figure through the runtime's
     `GCHeapHardLimit`.

  **On a non-Windows host lines 2 and 3 are the whole cap**; a hard bound on native allocation
  there is the container runtime's cgroup. The reason the first line exists is the recorded
  instance: a hostile-entity (billion-laughs) XML case that libverovio expanded to **126 GB
  in-process**, four times over one night, taking the whole machine each time because Windows has
  no OOM killer — a sampler is exactly as fast as the host's scheduler lets it be, and that is not
  fast enough. Never run hostile input against a native parser in a process without a
  kernel-enforced memory limit.
- **Not bounded:** the worker's system access. It runs as the same OS user on the same host. A
  deployment that needs that composes the worker inside a container — Phase 478's `Isolated`
  execution profile's territory.

## Composition profile

Under `CompositionProfile.Verified` (Phase 657) an in-process isolation is refused at composition:
`CompanionIsolation.forProfile CompositionProfile.Verified IsolationMode.InProcess` is
`Error InProcessUnderVerifiedProfile`. `CompanionIsolation.withIsolation` registers the
`ICompanionIsolation` singleton and a boot preflight (`IConfigValidator`) that fails the start when
the composed mode cannot be honoured on the host.

## Companions that offer an isolated path

- `ToolUp.OcrProviders.Tesseract` — `TesseractOcr.createIsolated` runs Leptonica's image decode
  and Tesseract's recognition of uploaded documents in the worker.
- `ToolUp.AssetStore` — `IsolatedSkiaDerivativeRenderer.create` runs SkiaSharp's decode of
  uploaded images in the worker, composed through `AssetCompose.withRenderer`.

See `docs/companions/native-boundary-isolation.md` for the inventory of native boundaries, the
classification of each, and the migration recipe.
