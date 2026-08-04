# Phase 538 — Typed `IsLiveInterface` flag on `AIToolDefinition`

**Ships in:** `ToolUp.Platform.Core` (`AIToolDefinition`), `ToolUp.RAG.Server` (`RAGPromptBuilder.ToolFraming`).
**Breaking for anyone who constructs an `AIToolDefinition` record literal** — one new field, one line
per construction site. **Behaviourally backward-compatible** — every tool defaulting the new field
produces byte-identical framing (GP 11).

## What changes

[Phase 14r](14r-tool-aware-rag-framing.md)'s `RAGPromptBuilder.ToolFraming.fromTools` flagged a tool as
*live-interface* when

```
def.Location = ClientResident
|| def.Name.StartsWith "_platform.ui."
```

The second arm keyed the SDK's own prompt framing off a **tool-naming convention the SDK never emits** —
the concrete `_platform.ui.*` tools are contributed by an external host-adapter layer. Two latent
defects followed:

- a live-interface tool named anything else — a third-party adapter's, or a future forge-native one —
  **silently missed** the framing;
- a **server-resident** tool that merely happened to start with `_platform.ui.` **wrongly tripped** it,
  softening KB-first framing for a tool that only reads persisted data.

Phase 538 replaces the name match with an explicit, typed, provider-agnostic declaration.

### New surface

`AIToolDefinition` gains one field:

```
IsLiveInterface: bool
```

`true` declares that the tool reads or drives the **live interface** — the browser-resident module
state the user is currently looking at — so a question about on-screen state is answerable without the
knowledge base. It exists for the cases `Location` cannot express: a *server-resident* tool that
nonetheless projects live interface state, or a host-adapter tool whose intent must be declared rather
than inferred from its name.

The derivation is now:

```
def.IsLiveInterface || def.Location = ClientResident
```

**No tool name is inspected.** The `ClientResident` implication is retained unchanged, so a
client-resident tool need not set the flag.

## How to adopt

Add `IsLiveInterface = false` to every `AIToolDefinition` record literal you construct. F# records
require every field, so this is a compile error until you do — the compiler names each site:

```diff
     SourceModule = "my_module"
     EmitsActions = None
     Location = ServerResident
     Surface = Both
+    IsLiveInterface = false
 }
```

`false` is the pre-538 behaviour for every existing tool, so a mechanical sweep is correct: no tool in
the SDK today declares `true` (the `_platform.ui.*` family that the old prefix arm matched is not
shipped by forge). Set `true` only on a tool that genuinely projects or drives live on-screen state
**and** is not already `ClientResident`.

Tools reached through a helper rather than a literal — `ToolHelpers.wireTools`,
`AlgorithmAITools.definitionFor`, `AIToolRegistry.createTool` — need no change; they take a
already-constructed definition.

## Verification

- `dotnet build ToolUp.Forge.sln` — the compiler enumerates every construction site that needs the
  field.
- `dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Tool-aware RAG framing"` —
  13 cases, including the two Phase 538 additions: framing fires on a **server-resident** tool
  declaring `IsLiveInterface = true`, and does **not** fire on a server-resident tool *named*
  `_platform.ui.*` (the retired false positive).

## Rollback

There is no opt-in to remove and no runtime switch. A deployment whose tools all default the field is
byte-for-byte identical to pre-538. To restore the old name-prefix behaviour for a specific tool
without changing its name, set `IsLiveInterface = true` on it — that is the declaration the prefix was
standing in for.
