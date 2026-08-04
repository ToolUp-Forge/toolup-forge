# Anchor-highlighting original-document preview seam (Phase 200)

**Ships in:** `ToolUp.KnowledgeBase.Core` (new shared types) + `ToolUp.KnowledgeBase.Server`
(new `IOriginalPreviewSeam` + compose hook).

**Consumer action required:** none. Everything here is additive and opt-in — a
deployment that never composes a preview seam registers nothing and runs the
pre-200 original-retrieval path byte-for-byte (GP 11 / GP 13).

## What changes

Phase 106 gave a citation a `SourceLocator` (*where* in the original the answer
came from). Phases 102/104 gave the caller the original's bytes. Nothing joined
them, so every consumer that wanted "open the PDF at page 4 and highlight it"
re-derived the join by hand.

Phase 200 ships that join as a typed seam, and nothing else. **The SDK still
ships no viewer** — which PDF / PPTX / HTML renderer to use stays a consumer
choice (GP 1). What is new is the descriptor a viewer reads.

| File | Change |
|---|---|
| `src/ToolUp.KnowledgeBase.Core/Shared/SharedTypes.fs` | **New types only:** `PreviewAnchor` (+ its `ofLocator` / `ofCharSpan` projections), `PreviewContent`, `PreviewTarget` (+ `ofOriginal` / `withSignedUrl`). No existing record widened. |
| `src/ToolUp.KnowledgeBase.Server/Server/IOriginalPreviewSeam.fs` | **New file.** `IOriginalPreviewSeam`, `IPreviewUrlSigner`, `PreviewSignedUrlOptions`, the two impls, `previewOriginal` (scope gate), `withOriginalPreviewSeam` (compose hook). |
| `src/ToolUp.KnowledgeBase.Server/Server/OriginalSourceResolver.fs` | Comment only — a pointer to the seam that composes it. No code change. |
| `src/ToolUp.KnowledgeBase.Server/Server/Server.fs` | Re-export of `withOriginalPreviewSeam` so it sits with the other compose-time hooks. |
| `src/ToolUp.KnowledgeBase.Server/ToolUp.KnowledgeBase.Server.fsproj` | One `<Compile>` entry for the new file. |

`getOriginalDocument` (Phase 102) is **not** touched and does not call the seam.

## The anchor contract

`PreviewAnchor` names a location in *document* terms — never a viewer API, a
DOM selector, or a URL-fragment convention — so one anchor serves a PDF.js
viewer, a server-side render, and a plain download alike.

| Case | What the viewer does |
|---|---|
| `Page n` | Scroll to 1-based page `n` (the `#page=n` PDF open-parameter). |
| `Slide n` | Open 1-based slide `n`. |
| `Sheet name` | Activate the named worksheet. |
| `Section heading` | Locate the heading **text** and scroll to it. Text, not an id — the SDK does not control the viewer's id scheme. |
| `Rows(from, to)` | Highlight the inclusive 1-based *source* row range (header row included in the numbering). |
| `CharRange(from, to)` | Highlight the half-open `[from, to)` document-relative character span. |
| `WholeDocument` | No precise anchor is known: open at the top, highlight nothing. |

`WholeDocument` is load-bearing, not a fallback of convenience: a citation with
no locator must not be turned into a guessed "page 1" (GP 9). A degenerate
character span (negative start, or an end at or before the start) degrades to it
for the same reason.

## Diff to apply

### 1. Compose the seam (opt-in)

```fsharp
open KnowledgeBase

let app =
    ServerApp.create ()
    // … existing composition …
    |> KnowledgeBase.Server.withOriginalPreviewSeam (
        KnowledgeBase.ServerOriginalPreviewSeam.createDefault (
            KnowledgeBase.ServerOriginalSourceResolver.createDefault ()))
```

Pass whatever `IOriginalSourceResolver` the deployment already composed rather
than a fresh default, so there is one resolution rule and not two.

### 2. Serve a preview from your own endpoint

The seam is server-side substrate; the API surface it hangs off is the
consumer's, so the endpoint shape (and its auth attributes) stay yours:

```fsharp
let previewHandler (seam: IOriginalPreviewSeam) (deps: KnowledgeApiDeps) docId locator =
    KnowledgeBase.ServerOriginalPreviewSeam.previewOriginal
        seam deps.Storage deps.Scope.Container docId locator
```

`previewOriginal` does the scope lookup. An id belonging to another team and an
id that does not exist both return `NotInScope`, byte-identically, so the
surface is not an existence oracle (GP 4). An in-scope document whose source
kind has no retrievable original returns `NoOriginalAvailable`. Both are typed
results; nothing throws (GP 9).

### 3. Byte-light delivery (optional)

To keep large originals out of the API response, supply a signer and use the
signed-URL seam. The SDK ships no signer — minting a URL needs the deployment's
storage backend, public host and signing key (GP 1). Deployments already running
the media tier typically delegate to the same machinery (`IMediaLibrary.SignedUrl`,
Phase 88):

```fsharp
let signer =
    { new IPreviewUrlSigner with
        member _.Sign(doc, container, ttl) = async {
            let! url = myStorage.PresignGet(container, $"knowledge/{doc.Id}/{doc.FileName}", ttl)
            return Ok url
        } }

let seam =
    KnowledgeBase.ServerOriginalPreviewSeam.createSignedUrl
        resolver
        signer
        { PreviewSignedUrlOptions.defaults with Ttl = TimeSpan.FromMinutes 15.0 }
```

`PreviewSignedUrlOptions.defaults.Ttl` is one hour, matching the media tier's
`SignedUrlDefaultTtl`. A non-positive TTL raises at **construction** (compose
time), not on a request path — an already-expired link is a deployment defect
that should fail the boot. A signer that returns `Error` surfaces as
`OriginalRetrievalFailed`; it never silently falls back to inlining the bytes
the deployment deliberately chose to keep out of the response.

### 4. Anchoring from a character-offset citation span

`PreviewAnchor.ofCharSpan` takes a plain offset pair rather than any
retrieval-side span record, so the preview seam never grows a dependency on the
citation tier. A caller holding a span maps it at its own boundary — the same
shape `SourceLocation.toLocator` uses at the producer boundary:

```fsharp
let anchor =
    match span with
    | Some s -> PreviewAnchor.ofCharSpan s.StartOffset s.EndOffset
    | None -> PreviewAnchor.ofLocator locator
```

## Known limitation

The signed-URL mode makes the **response** byte-light, not the server-side read:
the resolver still runs, because it is what establishes that the document has an
original and what its content type and size are. A metadata-only resolution path
would mean widening `IOriginalSourceResolver`, which is a larger change than this
seam should carry. Tracked as a tidy-up rather than left as a silent compromise.

## Verification

- `dotnet build ToolUp.Forge.sln` — 0 errors. Nothing existing was retyped, so
  no consumer construction site breaks.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`
  — the `Phase 200 …` lists in `KnowledgeOriginalRetrievalTests` cover the anchor
  projection (including both `WholeDocument` absences), both delivery modes, the
  TTL bound, the scope gate's indistinguishable refusal, and the GP 13 pin that a
  deployment composing no seam registers nothing.
- Compose the seam in a scratch app and confirm `previewOriginal` returns a
  `PreviewTarget` whose `Anchor` matches the citation's `SourceLocator`.

## Rollback

Remove the `withOriginalPreviewSeam` call. Nothing else is wired to the seam,
so the deployment returns to its pre-200 behaviour with no data migration, no
persisted-record change, and no wire-format change.
