# Phase 880 — Core returns to the consumer floor

**What changes for you: nothing, if your project references `ToolUp.Platform.Server`.** Every
member below keeps its namespace and module path, so `ServerConfig.fromEnv`, `JwtCrypto.computeHmac`,
`ConfigResolution.tryValue`, `DataVocabulary.pin` and `BlobStorage.trySignedUrl` read exactly as they
did. What moved is the ASSEMBLY: they compile into `ToolUp.Platform.Server` instead of
`ToolUp.Platform.Core`. Binary compatibility breaks, as at any 0.x minor; rebuild against the new
packages.

**What breaks, and for whom.** A project that references `ToolUp.Platform.Core` alone and calls one
of the moved members. Add the server-tier reference; nothing in the source changes, so there is no
codemod:

```xml
<PackageReference Include="ToolUp.Platform.Server" />
```

A Fable client never could call any of them: each sat behind `#if !FABLE_COMPILER` and transpiled
to nothing. The one Fable-visible member that moved is `DataVocabulary.pin`, whose Fable arm
returned a pin with an empty `Hash` that no counterparty could verify; a client receives pins from
the server instead.

## What moved

| Member | Core file it left | Server file it is in |
|---|---|---|
| `ServerConfig.fromEnv` and its ~40 private env parsers | `Shared/Config/ServerConfigFromEnv.fs` | `Server/Config/ServerConfigFromEnv.fs` |
| `ConfigResolution` (the whole module: the process-wide manifest and profile seam, its types) | `Shared/Types/ConfigResolution.fs` | `Server/Config/ConfigResolution.fs` |
| `JwtCrypto` (the whole module) | `Shared/JwtCrypto.fs` | `Server/JwtCrypto.fs` |
| `DataVocabulary.hash` / `load` / `pin` | `Shared/DataVocabulary.fs` | `Server/DataVocabularyPins.fs` |
| `BlobStorage.trySignedUrl` | `Shared/Interfaces/IBlobStorage.fs` | `Server/BlobStorageSignedUrl.fs` |

`ServerConfig.defaults` stays in Core, now in `Shared/Config/ServerConfigDefaults.fs`. The
`ServerConfig`, `DataVocabulary` and `BlobStorage` modules are split: Core keeps the half both tiers
compile and the server tier declares a module of the same name in the same namespace, which F#
resolves as one for qualified access and for `open`.

The manifest seam moved rather than being deleted because nothing in the shared tier read it: its
only Core reader was `fromEnv`, and its forty-odd other readers were already server-side. So the
task "remove the mutable process-wide manifest seam in Core" is done by the move — Core no longer
holds process-wide mutable configuration state.

## Reading the API baselines

`api-baselines/ToolUp.Platform.Core.approved.txt` loses 85 lines and
`ToolUp.Platform.Server.approved.txt` gains 88. They are the SAME members: the Core diff reads as
removals and the Server diff as additions. The three extra Server lines are the `(class)` entries of
the split modules (`ToolUp.Platform.ServerConfigModule`, `ToolUp.Platform.DataVocabulary`,
`ToolUp.Platform.BlobStorage`), which Server now declares too. `doc-coverage.approved.txt` moves the
same members' counts between the two assemblies.

## The gate that keeps it this way

`ArchitectureFitness` (Platform pack, "Phase 880 — no server-only region in the packed fable/
project") reads Core's compile list — the list Fable compiles, since the same `.fsproj` is packed
under `fable/` — and measures every `FABLE_COMPILER` conditional's .NET-only arm: the IF arm of
`#if !FABLE_COMPILER` (alone or `&&`-conjoined), the ELSE arm of `#if FABLE_COMPILER`. An arm over
**30 lines** fails. Thirty holds every dual implementation Core ships (the largest unpinned one is
24 lines) and nothing a module's worth of server code fits in.

The files still over the limit are pinned by name with a ceiling and a reason. A pin fails when its
file's largest arm grows past it, and fails as stale when the file drops within the limit or leaves
the compile list, so the list only shrinks. Shrinking an arm never fails.

## What did not move, and why

The census before the phase found 5,694 .NET-only lines in 50 arms across 16 files (the analysis
quoted about 3,800). This phase moved 1,240 of them and deleted 150 more (the dead `TYPESHAPE_EXPR`
branches). 4,304 remain in 44 arms across 11 files:

- **Dual implementations, which stay (~350 lines).** The MsgPack reader's .NET arms beside its Fable
  arms (`Read.fs`, 23 arms, largest 76 lines, pinned) and the JSON value / decode / encode arms
  (each under the limit). Both tiers run this code.
- **The reflection engine and its verify arms (~3,950 lines), pinned `deferred`.** `TypeShape.fs`,
  `TypeShapeUtils.fs`, the .NET MsgPack writer in `Write.fs`, the verify arms of `DecoderRegistry.fs`
  and `JsonDecoderRegistry.fs`, and the generator-emitted `verifyAll` / `registerAllVerified` in
  `PlatformDecoders.fs` and `PlatformJsonDecoders.fs`. Phase 853 did not retire any of it: it removed
  reflection from the Fable client's proxies, and TypeShape was never on the client. TypeShape is
  the server's MsgPack writer, and it is also what `RemotingDecoders.verify` serialises its draws
  with — and the generator emits calls to that verify into every generated decoder file, including
  consumers' files in projects that reference Core alone. Moving it therefore needs the verify gate
  to take its reflective writer as an argument, as the JSON wire's `verifyWith` already takes its
  oracle, and the generator to emit against that. That is a generator change with its own
  consumer-facing surface, and it is carried by a successor phase.

## Measured

| | before | after |
|---|---|---|
| .NET-only lines in Core's packed source | 5,694 in 50 arms, 16 files | 4,304 in 44 arms, 11 files |
| `#if !FABLE_COMPILER` directives in Core (bare / with conjunctions) | 32 / 38 | 28 / 34 |
| `TYPESHAPE_*` gates on undeclared constants | 15 | 0 |
| `fable/` source in the Core nupkg | 210 files, 73,390 lines, 3,578,384 bytes | 208 files, 71,548 lines, 3,489,042 bytes |
| Core nupkg | 4,527,656 bytes | 4,468,328 bytes |

The `fable/` figures are read from the packed nupkg (`dotnet pack -c Release`), not from the tree.

## Verify, and roll back

`ToolUp.Platform.Tests --filter-test-list "Phase 880"` (the gate and its fail-closed fixtures) and
`--filter-test-list "Phase 175"` (the baselines). The remoting wire corpus is unaffected: nothing
that encodes or decodes moved. To roll back, revert the phase's commits; there is no data or wire
format to migrate.
