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
- **Memory cap** — a **soft** cap: the host samples the worker's resident set every 10 ms and kills
  it on the first sample over the cap, and the worker's managed heap is hard-limited to the same
  figure through the runtime's `GCHeapHardLimit`. A hard bound on native allocation is an OS
  facility (Job Object, cgroup) and belongs to the deployment's container runtime.
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
