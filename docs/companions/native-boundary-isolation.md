# Native-boundary isolation (Phase 687)

A native companion is memory-unsafe C/C++ reached through P/Invoke. When it parses **host-authored**
input that is a maintenance cost; when it parses **untrusted bytes** — an upload, a connector payload,
a knowledge-base document — a parser bug in the native layer is in-process code execution, and
nothing above the FFI helps once control has crossed it. `ToolUp.Companions.Isolation` moves the
native call out of the host process for exactly those paths; this page is the inventory that says
which paths those are, the classification behind it, the wasm alternative assessed per companion, and
what the fuzz corpus found. The seam itself is documented in
[`src/ToolUp.Companions.Isolation/README.md`](../../src/ToolUp.Companions.Isolation/README.md); the
consumer recipe is [`docs/migrations/687-companion-native-boundary-isolation.md`](../migrations/687-companion-native-boundary-isolation.md).

## Inventory — which native boundaries receive untrusted bytes today

Measured against the tree at 2026-09-16 by reading every P/Invoke-backed companion back to the
request that feeds it. **Untrusted** means a caller outside the deployment's trust boundary chooses
the bytes; **host-authored** means the deployment produced them.

| Companion / native library | Reached from | Bytes chosen by | Class | Isolated path |
|---|---|---|---|---|
| `ToolUp.OcrProviders.Tesseract` — Leptonica `Pix.LoadFromMemory` + Tesseract | KB document upload / reprocess → `Extractors` → `TesseractOcrProvider` | the uploading user | **Untrusted** | `TesseractOcrIsolation.createIsolated` |
| `ToolUp.AssetStore` — SkiaSharp `SKBitmap.Decode` / `SKCodec.Create` | `POST /api/assets/upload` → `DefaultAssetStore` upload + derivative render | the uploading user | **Untrusted** | `IsolatedSkiaDerivativeRenderer.create` via `AssetCompose.withRenderer` |
| `ToolUp.Media.FFmpeg` — the `ffmpeg` executable | media transcode | the uploading user | Untrusted, **already a child process** (no cap / timeout / typed crash before 687) | seam-shaped follow-up: wrap the launch in `IsolationLimits` |
| Svg.Skia rasteriser (Phase 576) | server-produced figures | the host | Host-authored | none needed |
| ONNX Runtime — local reranker / local embeddings | operator-installed model; user text is tokenised managed-side before it reaches native | the operator | Host-authored | none needed |
| SQLite (via the platform's stores) | operator connection string | the operator | Host-authored | none needed |
| ClamAV (Phase 515) | TCP client to a scanner daemon | — | Managed-only (no P/Invoke) | n/a |
| Parquet.Net, local embeddings' tokeniser | — | — | Managed-only | n/a |

**Outside forge — the Companions estate.** `Verovio.NET` (libverovio 6.2.0), `Kuzu.NET`, `Manifold.NET`
are consumed by applications, not by forge. Their untrusted paths, from a read of the consumers:
`Verovio.NET` — ScaleMastery's public `/tools/humdrum-to-scale` posts pasted Humdrum straight into
`Toolkit.LoadData` (**untrusted**); a downstream music-notation host feeds internal MEI only (host-authored). `Kuzu.NET`
and `Manifold.NET` had no upload entry found at the time of the inventory. The seam is generic across
all three: an application composes `IsolationMode.OutOfProcess` and exposes each native operation as an
`IIsolatedEntryPoint` in its own assembly, exactly as the fuzz harness does for Verovio.

## Composition, and the profile mandate

In-process remains the default everywhere (GP 11 / GP 13): a deployment that composes nothing here is
byte-for-byte unchanged. `CompanionIsolation.withIsolation` registers the `ICompanionIsolation`
singleton and a boot preflight; the two forge companions above take it as an additive factory. Under
`CompositionProfile.Verified` (Phase 657) `CompanionIsolation.forProfile` refuses `InProcess` at
composition. A `CompositionProfileRefusal` case inside `Platform.Server` was deliberately **not** added
under the 0.23.0 release freeze (it widens a public DU and needs a manifest facet) — the gate lives at
the seam's own compose function until the next cut.

## Memory cap — three lines, and the incident that ordered them

The first draft of the fuzz corpus ran in-process; one case grew the runner to 126 GB and took the
operator's machine four times in one night (Windows has no OOM killer). The cap is therefore:

1. **Windows — a kernel-enforced Job Object** (commit limits = the cap, `KILL_ON_JOB_CLOSE`); the
   kernel refuses the crossing allocation, and a host that cannot apply the job refuses the call.
2. the host's resident-set sampler (a poll; can overshoot between samples);
3. the worker's `GCHeapHardLimit`.

On a non-Windows host, lines 2–3 are the whole cap and the container's cgroup is the hard bound.

## wasm alternative — assessed per companion, not built

The shard asked for the wasm route to be assessed before building a process host. Findings:

- **Verovio.NET ships no wasm build.** The repo declares `Wasmtime 22.0.0` in
  `Directory.Packages.props` as unreferenced residue; its `ci.yml` packs `src/Verovio.NET.Wasm` and
  `.Native` projects that do not exist (stale rows from before the single-package pivot). Upstream
  Verovio's emscripten build is a browser/Node artefact — not WASI-standalone — so a .NET wasm host
  would need Wasmtime **plus a custom `STANDALONE_WASM` upstream build**, a native build pipeline of its
  own, and a second copy of every resource file. Assessed, not built.
- **Tesseract / Leptonica and SkiaSharp** have no maintained WASI builds that expose the C surfaces
  the companions bind; both would be new native build pipelines.
- The process host is one implementation covering every companion today, resolves the host's full
  dependency closure (native runtimes included) with no second artefact, and is what the corpus was
  proven against. wasm remains the right answer where a companion **already** ships a WASI artefact;
  none does.

## What the corpus found

Run through capped children (`tests/CompanionFuzz/`, 199 cases, Verovio.NET 0.2.2 / libverovio
6.2.0): `malformed/forward-past-end` allocates without bound at ~1 GB/s (the incident's true cause —
**not** entity expansion; pugixml expands no DTD entities and the entity cases are benign);
`malformed/chord-with-no-first-note` is a native access violation on both load paths (pinned in the
harness's known-findings ledger, contained by the seam); `oversized/5000-notes-in-one-measure` does
not answer inside 30 s. Every other case answers or is refused cleanly. Full table and run recipe:
[`tests/CompanionFuzz/README.md`](../../tests/CompanionFuzz/README.md).

## Not a sandbox

The seam bounds the blast radius of a native crash, the wall-clock a parse may take, and the memory
it may commit. It does **not** bound the worker's system access — it runs as the same OS user on the
same host. A deployment that needs that composes the worker inside a container or an OS sandbox,
which is Phase 478's `Isolated` execution profile's territory.
