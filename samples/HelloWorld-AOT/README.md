# HelloWorld-AOT — the generated remoting path under native AOT

Two projects that together prove what the remoting generator was minted for: a binary-response
decode and a typed argument parse that are **reflection-free at runtime** and survive
`PublishAot`.

| Project | Role |
|---|---|
| `HelloWorld.AOT.Contract` | The project whose built assembly the generator reads. It links the remoting wire corpus's declarations from `src/ToolUp.Platform.Tests/Remoting/WireCorpus.fs` (one declaration, two encodings — never a copy) and declares `ICorpusApi`, one echo method per corpus type. Its `.fsproj` carries the whole adoption: two items naming what to emit and where. |
| `HelloWorld.AOT` | The console host. `Generated/` holds what the contract's build emits — the closed-algebra decoders and the typed argument table — committed and compiled like any other source. `Program.fs` runs every pinned fixture under `tests/remoting-corpus/` through both and exits non-zero on any disagreement with the corpus's own declaration. |

The generator's build integration is the same `.targets` a consumer gets from the
`ToolUp.Remoting.Generator` package; the repository imports it in-tree so this sample dogfoods it.
See [`src/ToolUp.Remoting.Generator/README.md`](../../src/ToolUp.Remoting.Generator/README.md).

## Run under the JIT

```pwsh
dotnet run --project samples/HelloWorld-AOT/HelloWorld.AOT
```

Every fixture the generator can express decodes through the generated decoder; every fixture
parses through the generated argument table. The seven fixtures the generator **refuses** a decoder
for are named with their reason: `DateOnly` / `TimeOnly`, which have no cross-host combinator, and
the two records holding one. (Until Phase 800 the list also carried the tuple and multi-field
union-case fixtures — the two combinator gaps that phase closed; `tuple-pair`, `tuple-triple`, the
`union-*` fixtures and `record-consignment` now decode through the generated decoders.) The program
also fails if that refused set changes, so a widening of the algebra is noticed here rather than
silently un-checked.

## Publish natively

```pwsh
dotnet publish samples/HelloWorld-AOT/HelloWorld.AOT -c Release -r win-x64
.\samples\HelloWorld-AOT\HelloWorld.AOT\bin\Release\net10.0\win-x64\publish\HelloWorld.AOT.exe
```

The first line prints `dynamic code supported: False` under the native host. On a machine where
the Visual Studio installer did not register the C++ workload with `vswhere` (Build Tools with the
MSVC component present but unlisted), the ILC pipeline reports `Platform linker not found`; run
the publish from a `vcvarsall.bat amd64` shell with `-p:IlcUseEnvironmentalTools=true`, which
takes `link.exe` from `PATH` instead. Note that `vcvarsall` also sets `Platform=x64`, so the
output then lands under `bin\x64\Release\...`.

## What the publish proves, and what only the run proves

`TrimmerSingleWarn` is off in the `.fsproj`, so every trim / AOT warning is itemised with the
method it came from. Measured on 2026-09-15 (.NET 10.0.8, MSVC 14.44): **161 warnings, none in
this assembly, the contract, either generated module, or `ToolUp.Platform.Core`** — 104 are in
`ToolUp.Platform.Server`, every one on the System.Text.Json converter set the argument table's
typed seam calls into, and 57 are FSharp.Core's upstream `printf` / reflection annotations.

The publish is not the proof; the run is. F#'s `printf` family builds its formatter through
`MethodInfo.MakeGenericMethod`, which native AOT refuses **at runtime** — a `printfn "%d"`
publishes with only a blanket warning and fail-fasts on its first call. The first native run of
this program found exactly one such call on the algebra's happy path (an eagerly-built path
segment in the `index` combinator, fixed in this phase); `Program.fs` itself contains none.

**Measured result of the native run** (`dynamic code supported: False`, exit 0):

| Path | Result |
|---|---|
| Generated decoders (binary response) | 58 of 58 expressible fixtures decode to their declaration; the 10 refusals are the algebra's recorded reach (measured before Phase 800 — under the JIT the same run now reads 64 of 71 with 7 refusals, the six fixtures Phase 800 made expressible plus Phase 803's three boundary fixtures; the native run has not been re-measured) |
| Generated argument table (JSON request) | 46 of 68 parse; **22 are reflection-bound** — every option, list, set, map, tuple, union and record fixture. The table's one call is the typed STJ seam, and that seam runs the reflection converter set: generic converters over value types have no native instantiation, and record / union construction goes through a reflective invoke the AOT runtime refuses |

So the reflection-free claim holds for the **decode** path end to end, and the argument path is
reflection-free only up to the seam it calls — which is the boundary Phase 785 recorded (785.F):
the argument side decodes bytes that arrive as JSON, and the algebra's JSON extension is a separate
phase. The program tallies those 22 as `reflection-bound` under the native host and exits 0; under
the JIT the same 68 parse and any failure is fatal.
