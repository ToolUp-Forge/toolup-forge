# Migration — Phase 687: run untrusted-upload native parsing out of process

**Status.** Additive. Nothing changes for a deployment that does not opt in (GP 11 / GP 13): every
existing composition is byte-for-byte unchanged and pays nothing. Do this migration if your
deployment feeds **uploads** to Tesseract OCR (knowledge-base documents) or to the asset store's Skia
derivative renderer (`POST /api/assets/upload`) — or if it composes under
`CompositionProfile.Verified`, where in-process native parsing of untrusted bytes is refused at
compose.

**Why.** A native parser fault on an uploaded file is in-process code execution today. Behind the seam
it is a typed `IsolationRefusal.WorkerCrashed` from a sacrificial child that ran under a wall-clock
bound and a memory cap — on Windows a kernel-enforced Job Object, after a fuzz case grew an
in-process parser to 126 GB. Inventory and classification:
[`docs/companions/native-boundary-isolation.md`](../companions/native-boundary-isolation.md).

## 1. Compose the seam once

```fsharp skip=fragment
open ToolUp.Companions.Isolation

// OutOfProcess resolves the worker from the host's own deps.json / runtimeconfig.json;
// nothing to ship, no hook in main. Defaults: 30 s, 512 MiB.
let isolation = CompanionIsolation.ofMode (IsolationMode.OutOfProcess IsolationLimits.defaults)

// Registers the ICompanionIsolation singleton + a boot preflight that fails the start
// when the host cannot honour the mode (single-file publish, no dotnet muxer).
let app = app |> CompanionIsolation.withIsolation isolation
```

Under `CompositionProfile.Verified`, use `CompanionIsolation.forProfile profile mode` instead of
`ofMode`: it returns `Error InProcessUnderVerifiedProfile` for `InProcess`, so the refusal is a
compile-visible `Result`, not a runtime surprise.

## 2. Tesseract OCR — swap the factory

```diff
- let ocr = TesseractOcrProvider.create options
+ let ocr = TesseractOcrIsolation.createIsolated options isolation
```

Same `IOcrProvider`; Leptonica's decode and Tesseract's recognition run in the worker. A crash,
timeout or cap hit surfaces as the provider's ordinary failure with the refusal's `describe` text —
your extraction pipeline sees a failed page, not a dead process.

## 3. Asset store — compose the isolated renderer

```diff
  AssetStoreServerApp.create ()
+ |> AssetCompose.withRenderer (IsolatedSkiaDerivativeRenderer.create isolation)
```

Same `IDerivativeRenderer`; the SkiaSharp decode of uploaded images runs in the worker.

## 4. Your own native companion

Expose each native operation as a public `IIsolatedEntryPoint` with a parameterless constructor
(args + bytes in, bytes + typed `Error` out) and call `CompanionIsolation.run<YourEntry> isolation
request`. The worker resolves the type by name inside the host's dependency closure, so the entry
lives in an assembly the host already references. `tests/CompanionFuzz/VerovioEntries.fs` is a
complete example over Verovio.NET.

## Verification

- Boot: the preflight passes (`WorkerUnavailable` at start means the layout is unresolvable — set
  `DOTNET_HOST_PATH`, or compose `IsolationMode.OutOfProcessWith` an explicit `WorkerLauncher`).
- Upload a valid document / image: identical result to before (the seam tests pin in-process and
  out-of-process byte-for-byte alike).
- Windows: `ProcessIsolation.kernelMemoryCapSupported` is `true`; a Windows host that cannot apply
  a Job Object answers `WorkerUnavailable` naming the Win32 error rather than running uncapped.
- Non-Windows: the cap is the resident-set sampler plus the worker's `GCHeapHardLimit`; put the
  container's cgroup limit above `MemoryCap` and treat the cgroup as the hard bound.

## Rollback

Remove the `withIsolation` / `createIsolated` / `withRenderer` lines. The in-process factories are
untouched and remain the default.
