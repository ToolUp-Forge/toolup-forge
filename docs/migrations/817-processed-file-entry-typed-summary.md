# Phase 817 — `ProcessedFileEntry`: the typed summary envelope

**Applies to:** every module that declares a `DataType` (fills a `ProcessedFileEntry` in
`Process`) or a `DataTypeDisplay` (renders summaries in the Data Manager).
**Breaking:** yes — `ProcessedFileEntry.Info: obj option` and the `obj list` form of
`DataTypeDisplay.RenderSummary` are **removed**, not deprecated (operator decision 2026-09-22,
overriding the deprecation window: the field was the last open point in the platform's API type
graph, and removing it is what puts `FileManagementApi` on the proved decode path now). Ships on
the standing `0.23.0` draft, which already carries breaking surface since `v0.22.0`.
**Action required:** two lines per data-producing module — one in `Process`, one in the display.
A module that does not move stops compiling at those two sites; nothing else changes.

## What changes

The module's summary of a processed file crossed the wire **boxed**: `ProcessedFileEntry.Info:
obj option`, filled by the module's `DataType.Process` and cast back by its `RenderSummary: obj
list -> ReactElement`. The SDK carried it opaquely between the two — the type-erasure boundary
`CLAUDE.md` numbers 2. Two costs, one of them invisible: no decoder the SDK can generate or prove
can express a type only the module knows, so `FileManagementApi` was the one platform API record
left on the reflection decode path after Phases 800 and 816; and the MsgPack reader has no `obj`
arm, so what a boxed record decoded to was whichever host's reflection fallback ran — the one field
on the platform's API surface whose wire behaviour nobody could state.

The summary is now a **`ProcessedData` envelope** — the summary type's name and its JSON, the
shape Phase 1c introduced at the sibling seam for exactly this reason:

| | Before | After |
|---|---|---|
| Entry | `Info: obj option` (boxed summary) | `Summary: ProcessedData option` — `Info` removed |
| Server | module boxes its summary | `ProcessedDataCodec.encode summary` (System.Text.Json + `FableConverters`) |
| Client | `RenderSummary: obj list -> …` unboxes | `RenderSummary: ProcessedData list -> …`; `DataTypeDisplay.typed info (fun (xs: MySummary list) -> …)` decodes (`Fable.SimpleJson`) |
| Builders | record literals | `ProcessedFileEntry.summarised` / `.failed`; `DataTypeDisplay.typed` / `.typedWith` |
| Persisted sidecars | `_processed_entry__*/v1.json` with `Info` | same file; an old one loads with its `Info` ignored and `Summary = None` — no migration, no summary until the file is reprocessed |

The two codec halves are held to one pinned envelope from both sides in the SDK's packs (the .NET
pack holds the encoder to the literal, the Fable pack holds the browser decoder to the value), so a
converter-set drift between the hosts is a red gate, not a blank Data Manager.

## The diff, per module

```fsharp skip=fragment
// Server.fs — inside DataType.Process
// before
let entry = { FileName = fileName; DataType = MyId; ProcessedAt = now; Info = Some(box summary); Error = None }
// after
let entry = ProcessedFileEntry.summarised fileName MyId now (ProcessedDataCodec.encode summary)

// ClientView.fs — the DataTypeDisplay registration
// before
{ Info = myInfo; RenderSummary = fun infos -> render (infos |> List.map unbox<MySummary>) }
// after
DataTypeDisplay.typed myInfo (fun (summaries: MySummary list) -> render summaries)
```

There is no legacy path: a module that still boxes fails to compile at its `Info =` and its
`RenderSummary = fun (infos: obj list) -> …`, and the fix is the two lines above. A display
running on a host with a JSON codec of its own supplies it through `DataTypeDisplay.typedWith`.

Consumers of another module's data (`ProcessedDataContext.ProcessedData.forType`) read
`entry.Summary` and decode with `DataTypeDisplay.tryDecode<TheirSummary>` — the producer's shared
types name the summary type, as they always did.

## Why a cut and not a deprecation window

The phase first shipped the envelope beside a deprecated `Info` (removal named as 1.0, per the
[deprecation policy](../platform/deprecation-policy.md)). The operator overrode the window the
same day: the field was the one thing keeping `FileManagementApi` off the proved decode path,
`0.23.0` is an unreleased draft already carrying breaking surface, and a two-line migration is
cheaper than a release with a known open point. The removal landed as a second commit on the
same phase; the census pin now asserts **every** platform API record is expressible.

## Verification

- `FileManagement` in `ToolUp.Platform.Tests`: the envelope round-trips the sidecar store and
  decodes on the server; a pre-817 sidecar loads with `Summary = None`; the render step hands a
  display exactly the envelopes that decode as its type; the encoder writes the pinned literal
  byte for byte.
- `ProcessedData envelope (Phase 817)` in the Fable-tier harness: the browser decoder reads the
  same literal to the same value, `None`/`[]` included, and refuses a wrong shape by name.
- `Phase 69k` census: no refusal remains — 38 of 38 platform API records are expressible.
- `VerifyPackagedModuleTemplate`: the module template's `Process` uses the builder.

## Rollback

Revert the phase's commits. A sidecar written with `Summary` reads under the previous SDK with
the field ignored.
