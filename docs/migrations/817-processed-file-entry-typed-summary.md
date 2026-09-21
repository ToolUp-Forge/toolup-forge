# Phase 817 — `ProcessedFileEntry`: the typed summary envelope

**Applies to:** every module that declares a `DataType` (fills a `ProcessedFileEntry` in
`Process`) or a `DataTypeDisplay` (renders summaries in the Data Manager).
**Breaking:** at runtime, no — a module that changes nothing renders exactly as before. At
**compile** time, a **record literal** for either type stops compiling until it names the new
field, and then warns until it stops naming the deprecated one. Both are one-line edits, and
both go away for good by moving to the builders. Ships on the standing `0.23.0` draft; `Info` and
`RenderSummary` are removed in **1.0**.
**Action required:** two lines per data-producing module — one in `Process`, one in the display.

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
| Entry | `Info: obj option` (boxed summary) | `Summary: ProcessedData option` — `Info` kept, `[<Obsolete]`, removed in 1.0 |
| Server | module boxes its summary | `ProcessedDataCodec.encode summary` (System.Text.Json + `FableConverters`) |
| Client | `RenderSummary: obj list -> …` unboxes | `DataTypeDisplay.typed info (fun (xs: MySummary list) -> …)` decodes (`Fable.SimpleJson`) |
| Builders | record literals | `ProcessedFileEntry.summarised` / `.failed`; `DataTypeDisplay.typed` / `.legacy` / `.typedWith` |
| Persisted sidecars | `_processed_entry__*/v1.json` with `Info` | same file; an old one reads with `Summary = None` — no migration |

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

A module that is not ready to move: change the literals to name the new fields (`Summary = None`;
`RenderTyped = None`) or, better, call `ProcessedFileEntry.failed` / `DataTypeDisplay.legacy` —
the legacy path is unchanged and the shell hands each display only what its own module produced.
The `[<Obsolete>]` warning on a literal is the deprecation working: the literal breaks in 1.0, the
builder does not.

Consumers of another module's data (`ProcessedDataContext.ProcessedData.forType`) read
`entry.Summary` and decode with `DataTypeDisplay.tryDecode<TheirSummary>` — the producer's shared
types name the summary type, as they always did.

## Why a deprecation window and not a cut

Removing a record field is the break the [deprecation policy](../platform/deprecation-policy.md)
reserves for a major, and Phase 815 is clearing deprecations for the 1.0 cut — so this phase adds
the envelope now, deprecates the box with 1.0 as its named removal target, and the census keeps
`FileManagementApi` pinned as the one remaining refusal *with that removal date attached*: the
day `Info` goes, that pin inverts and 38 of 38 platform API records are on the proved path.

## Verification

- `FileManagement` in `ToolUp.Platform.Tests`: the envelope round-trips the sidecar store and
  decodes on the server; a pre-817 sidecar reads with `Summary = None`; the render step hands a
  typed display the envelopes and a legacy display the boxes; the encoder writes the pinned
  literal byte for byte.
- `ProcessedData envelope (Phase 817)` in the Fable-tier harness: the browser decoder reads the
  same literal to the same value, `None`/`[]` included, and refuses a wrong shape by name.
- `Phase 69k` census: one refusal remains (`System.Object`), and `Info` carries an `[<Obsolete>]`
  naming 1.0.
- `VerifyPackagedModuleTemplate`: the module template's `Process` uses the builder.

## Rollback

Revert the phase's commits. No wire bytes and no persisted sidecar changed shape; a sidecar
written with `Summary` reads under the previous SDK with the field ignored.
