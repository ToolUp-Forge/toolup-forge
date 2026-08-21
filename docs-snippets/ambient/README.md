# Per-page ambient preambles

Each file here supplies the surrounding context for one documentation page's
`fsharp` blocks, so those blocks compile **as written** under
`dotnet run --project Build.fsproj -- VerifyDocSnippets`.

The path mirrors the doc tree with `.md` swapped for `.fs`:

| Documentation page | Ambient file |
|---|---|
| `docs/platform/dynamic-ssr.md` | `docs-snippets/ambient/docs/platform/dynamic-ssr.fs` |
| `src/ToolUp.Platform/technical-guide/12-hosting-models.md` | `docs-snippets/ambient/src/ToolUp.Platform/technical-guide/12-hosting-models.fs` |

A page with no ambient file is unaffected. The file is inlined verbatim into
every generated block of its page, ahead of the block's own text.

## Why this exists

A doc block is an excerpt, not a program. Many blocks are perfectly accurate
and still cannot compile alone, because they read locals from a composition
root the page never shows in full — `config`, `providerProfile`, `secretStore`,
an Elmish `Model`, a page-local `loadCampaign`.

The only honest classification for those used to be `skip=fragment`, and a skip
buys silence: nothing then checks the block's SDK names, so the next rename rots
it invisibly. That is the exact drift class this gate exists to catch, occurring
in the blocks the gate cannot see.

An ambient file declares those locals once, out of band, so the block is checked
like any other — while the markdown a reader copies grows no `open`-ceremony.

## Shape

`open`s at the top level; everything else inside one auto-opened module.

```fsharp
// Ambient context for `docs/platform/dynamic-ssr.md`.
open ToolUp.PublicRendering
open Giraffe.ViewEngine

[<AutoOpen>]
module PageAmbient =

    type Campaign = { Name: string; Spend: decimal }

    let loadCampaign (ctx: CallContext) (client: string) : Async<Campaign option> =
        failwith "ambient"
```

Both halves are load-bearing:

- **`open`s must be top level.** An `open` inside the module would be scoped to
  the module, and the block would not see it.
- **Declarations must be inside it.** A page routinely introduces a type in its
  first block and reads it from its fifth. Flat declarations would *collide*
  with that first block; auto-opened ones are simply **shadowed** by it, so the
  page teaches the type once and every later block still compiles.

Nothing here runs, so `failwith "ambient"` is the conventional body.

## Rules

1. **Never redeclare an SDK name.** An ambient `type ServerConfig` would make
   the block compile against a mirror of the surface instead of the surface, and
   the gate would then certify nothing. Declare what the *page's own program*
   would provide, never what the SDK provides.
2. **Prefer real SDK types in signatures.** `let ctx: CallContext = failwith
   "ambient"` keeps `CallContext` under the gate; `let ctx: obj = ...` does not.
   Reach for `obj` only where the page genuinely does not say.
3. **An error inside an ambient file fails the target as a harness fault**, not
   as doc drift — it is reported against this file by name, because it is
   compiled under its own `#line` directive and so never attributes to a block.
4. **These are hand-written sources, so Fantomas formats them** like any other
   `.fs` in the repo (`dotnet fantomas docs-snippets/ambient`). Only
   `docs-snippets/generated/` is exempt, because nobody wrote it.
5. **Adding one usually retires a `skip=fragment` marker.** That is the point:
   drop the marker in the same commit, and the block joins the corpus (which
   advances the high-water mark in `../corpus-floor.txt`).
