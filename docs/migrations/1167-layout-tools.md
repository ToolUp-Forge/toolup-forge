# 1167 — The layout tools: page edits proposed as ops, saved under the layer gate

**Ships in:** ToolUp.Facts.Presentation.Server and ToolUp.Platform.Server 0.38.0. It rides the 0.38.0 draft, and
its class is additive: new types and functions, with nothing existing changing shape.

**Affected:** nobody until they opt in. A deployment that lets its assistant edit stored pages composes the tools
with one call.

## What changes

1. **Eight proposing tools and a save** (`LayoutTools`, ToolUp.Facts.Presentation.Server), all server-resident:

   | Tool | What it proposes |
   |---|---|
   | `layout_add_block` | a chart or table block drawing a request (a Phase 1166 `PageRequest`), into a section or before or after a node |
   | `layout_remove_block` | removing a block |
   | `layout_move_block` | moving a block before or after a node, or into a section |
   | `layout_set_request` | changing one request's period, comparison baseline, metrics, subject level or site |
   | `layout_retitle` | a new title for a block, section, chart or heading |
   | `layout_set_section` | adding a section, or renaming one |
   | `layout_set_default` | the page's standing site and range: every request takes them, and a block added later starts from them |
   | `layout_reset` | dropping one layer's edits |
   | `layout_save` | landing a proposal on a layer |

2. **Each proposing tool writes nothing.** It reads the caller's folded page from the page store (Phase 1165),
   computes the edit as language-tier tree ops, applies them to a copy with `Fuaran.UI.Ops`, validates the copy,
   and answers two things. The model reads a text summary with no values in it, and the assistant message carries
   a preview block whose provenance `Kind` is `proposed`. That provenance names the proposal id. Only
   `layout_save`, given that id and a layer, writes. It lands the ops through `IPageDocumentStore.AppendOps`, or a
   reset through `ResetLayer`, under the store's layer gate and audit. A refused save is a typed refusal the model
   reads. No proposing tool carries a save argument.
3. **Validation, in order, each a refusal naming what to repair:**
   - The ops apply to the folded page.
   - The pre-emit validator (`PreEmitValidate.validateForTransport`) finds no Error-severity defect that the edit
     introduced.
   - Every chart the edit touches types against its request's resolved schema (`QueryBinding.check`). The schema
     comes from resolving the request under the caller's gate, through the Phase 1166 resolver.
   - No chart or grid on the page draws inline rows. Each one names, by a `Binding.Query`, a request the page
     holds. An argument carrying rows (`rows`, `data`, `staticRows`, …) is refused by name before anything is read.
   - The page's read-set names only tables, metrics and subject levels the composition declares.
4. **A tool can never type a number.** Every data-bearing node a tool adds or changes names a request key. When a
   request changes, it gets a new key (the stem, then `~` and eight hex digits of the canonical request's SHA-256),
   and the nodes bound to the old key are rebound with `ReplaceBinding`. So one key always names one request, and
   every request change is an op the page store can save.
5. **The request sidecar and the read-set are interim.** The page store keeps one opaque sidecar per layer. These
   tools write it as the canonical JSON of the page's request map plus its standing defaults (`PageRequestSidecar`,
   format `toolup.page-requests/1`). The read-set is derived from that map: tables, metrics and subject levels per
   presentation request, and `Fuaran.Compute` verbs per pipeline (`PageReadSet`, version `toolup.page-read-set/1`).
   When the language tier publishes its typed page document and demanded-document projection, those replace both,
   and the versions above mark the step. The layer whose sidecar applies is the highest folded layer that carries
   one.
6. **Proposals belong to their caller.** They live in a process-local `LayoutProposalStore` keyed by the caller's
   scope and principal, bounded at 256 callers, 32 proposals each and an hour of age. A proposal is refused at save
   if the page changed after it was made (`stale-proposal`). Another caller can never save it.
7. **There is no language profile yet.** The pre-emit validator runs under its base rules. Once a deployment
   registers a language profile for the assistant (Phase 1138), that profile becomes the grammar this step
   validates against.
8. **Composition** (`PresentationCompose.withLayoutTools` on `AIServerApp`) is opt-in, beside `withRenderTools`.
   It registers the nine tools only under `EnabledFactStore`, and only when the page store was composed through
   `PageDocumentStoreCompose.withPageDocumentStore` before it. It finds that store by the contribution recorded
   under `PageDocumentStoreCompose.componentId`. Without a fact store or a page store, the app is returned
   unchanged and no tool is registered, so the tool list is byte-identical to before. A page store registered by
   hand through `addServiceConfig` is not seen; compose it with its builder. The call also composes the page
   request resolvers (`withPageRequests`, which is now idempotent) when they are missing, and registers a
   `LayoutToolsRuntime` (the page store, the resolvers and the proposals) in a registration factory.

## What to do

```fsharp skip=fragment
let app =
    ServerApp.empty
    |> FactsCompose.withFactStore
    |> PageDocumentStoreCompose.withPageDocumentStore PageStoreOptions.defaults

AIServerApp.createFrom factory providerProfile app
|> PresentationCompose.withRenderTools
|> PresentationCompose.withLayoutTools
|> composeAI
```

Who may save what is the page store's policy (docs/migrations/1165-page-store.md). Any viewer can propose. Saving
to the team layer needs the team's Owner or Admin role, or `SaveTeam`. Saving to the platform layer needs a
platform admin.

## Semantics worth knowing

- **Saving to a layer under your own.** A proposal's ops were computed against the caller's whole fold. Saving
  them to a lower layer than the caller's own applies them to that layer's fold. If they do not apply there, the
  store refuses the save by name (`OpsDoNotApply`), and nothing is stored.
- **A standing default changes the requests it covers.** `layout_set_default` re-keys every request whose site or
  range moves, and records the default for later blocks. On a page where no request would move, there is nothing
  to save, because the store refuses a save with no ops.
- **A preview resolves the page's requests under the caller's gate.** A request that resolves to nothing the
  caller may see shows as a note naming why. The preview is never stored.

## Not in this release

- **A comparison against a day offset** ("against last year" rather than "against the delivery before"). The
  presentation request names one comparison baseline, the previous delivery, and `layout_set_request` offers what
  the request vocabulary admits. A day-offset baseline is a change to that vocabulary and to the read it implies,
  so it is a separate phase.
- **Tabbed sections.** `UpdateProp` cannot resize a tab-header list. An `EditNode` on a tabs node replaces its
  children wholesale, which would silently override lower-layer edits on a re-fold, so tabs wait for an op that can
  express them.

## Verify

`dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter "ToolUp.Platform.Tests.Layout tools"`
covers:

- the two edits proposed, previewed and saved by a team owner;
- inline rows refused by name;
- the binding check;
- a member's team save refused and audited;
- a reset that needs the layer's grant;
- proposals never shared between callers, and a stale proposal refused;
- composition only with a fact store and a page store.

The tool schema census ("Phase 508 rich AI tool parameter schemas") covers the nine definitions and the
byte-identical tool list of a deployment that composes no layout tools.

## Rollback

Remove the `withLayoutTools` call. Saved layers stay in the page store, and their sidecars stay readable by any
later build that reads `toolup.page-requests/1`.
