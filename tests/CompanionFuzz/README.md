# Companion native-parser fuzz corpus (Phase 687)

Hostile MusicXML — malformed, truncated, hostile-entity, oversized — fed to Verovio.NET's two
untrusted-input entry points (`LoadData`, `LoadZipBuffer`), **one case per capped child process**
through the `ToolUp.Companions.Isolation` seam. The corpus is generated deterministically in
[`FuzzCorpus.fs`](FuzzCorpus.fs); the harness and its known-findings ledger are in
[`FuzzTests.fs`](FuzzTests.fs).

## Run it — deliberately, not as a side effect

```powershell
# From the forge repo root. Verovio.NET restores off the shared local feed (../../local-nuget-feed).
dotnet build tests/CompanionFuzz/ToolUp.Companions.Fuzz.Tests.fsproj
dotnet tests/CompanionFuzz/bin/Debug/net10.0/ToolUp.Companions.Fuzz.Tests.dll
# One case, under a larger cap, to tell "over the cap" from "unbounded":
$env:TOOLUP_FUZZ_MEMORY_CAP_MB = '2048'
dotnet tests/CompanionFuzz/bin/Debug/net10.0/ToolUp.Companions.Fuzz.Tests.dll --filter-test-case forward-past-end
$env:TOOLUP_FUZZ_MEMORY_CAP_MB = $null
```

~90 seconds for 199 cases on a 2026 workstation; the two 30-second timeouts are most of it. It is
**not** in `ToolUp.Forge.sln`, not in `BuildConfig.TestPacks`, and not run by `VerifyAll`, `verify.ps1`
or CI — see the project file's header for the two reasons — so running it is an operator's act.

**Never run this corpus in-process, and never in a process without a kernel-enforced memory
limit.** The harness refuses (cases report PENDING) on a host where
`ProcessIsolation.kernelMemoryCapSupported` is false, unless `TOOLUP_FUZZ_ALLOW_SOFT_CAP=1` opts into
the resident-set sampler alone — a Linux host with an OOM killer and a cgroup of its own, never a
Windows desktop. The first draft of this corpus ran inside Verovio.NET's own Expecto runner, one case
grew it to **126 GB**, and Windows — which has no OOM killer — lost the whole machine, four times in
one night.

## Outcomes, and what is red

| Seam answer | Reading | Test |
|---|---|---|
| `Ok` / `EntryFailed` | **answered** — rendered, loaded, or refused cleanly; the shape is not asserted | green |
| `MemoryCapExceeded` / `TimedOut` | **contained** — a resource-exhaustion finding the bounds absorbed | green, reported |
| `WorkerCrashed` | **crashed** — a native fault, the memory-safety class the seam exists for | red unless in the ledger |
| `ProtocolViolation` / `WorkerUnavailable` | the harness measured nothing | red |

Plus: any exported SVG/MEI carrying the planted external-entity marker is red — a resolved entity is
a file read the parser must never perform.

The **known-findings ledger** (`knownCrashes` in `FuzzTests.fs`) pins each crash the corpus has
already surfaced, in both directions: a listed case that still crashes passes, a listed case that now
answers is red until its entry is retired, an unlisted crash is red as a new finding.

## Findings — measured 2026-09-17, Verovio.NET 0.2.2 / libverovio 6.2.0

| Case | Both paths | Reading |
|---|---|---|
| `malformed/forward-past-end` — `<forward><duration>2147483647</duration></forward>` | **contained: memory cap** in 0.5 s at 512 MiB; reaches a 2 GiB cap in < 2 s | **Unbounded allocation, ~1 GB/s.** This is the case that took the machine in-process, not the entity one. |
| `malformed/chord-with-no-first-note` — `<note><chord/>…` with no preceding note | **crashed**: access violation `0xC0000005` | Memory-safety fault in libverovio's MusicXML import. In the ledger. |
| `oversized/5000-notes-in-one-measure` | **contained: timed out** at 30 s | Layout pathology; a bounded-work outcome behind the seam. |
| `hostile/billion-laughs-entity-expansion`, both XXE shapes, `external-dtd-fetch` | **answered** (rendered) in 0.11 s, ~75 MB, no leak | **Benign.** pugixml expands no DTD entities and resolves no external ones; the original "billion laughs" attribution of the incident was an inference from the case name and is refuted by measurement. |

Every other case answers or is refused cleanly, in well under a second, with a peak resident set
under 470 MB across the whole run.

## Why the corpus lives here and not in Verovio.NET or a CI leg

- **Not in Verovio.NET's test project**: that project's `run.ps1` is what every consumer of the
  package runs, and it runs in-process. A hostile corpus does not belong on that path.
- **Not in forge's solution or CI**: forge's CI restores from nuget.org over an empty local feed,
  and Verovio.NET is not on nuget.org; Verovio.NET's CI restores `ToolUp.*` from nuget.org, where
  `ToolUp.Companions.Isolation` is not yet released. A CI leg becomes possible the day either package
  is published on the other's restore path; until then this is an operator-run harness, which is
  also the right posture for a corpus that must never run without a kernel cap.
