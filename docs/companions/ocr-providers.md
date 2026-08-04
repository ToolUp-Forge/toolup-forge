# OCR-provider companions

The Platform's `IOcrProvider` interface sits in front of the KnowledgeBase extraction path. When a document has no text layer to extract — a scanned PDF, a photographed page, an image upload — the extractor routes it through the composed provider instead. A deployment substitutes one implementation for another with a single composition line; nothing above the interface changes.

For the interface itself and the rest of the extraction pipeline, see [`knowledge-base/extending.md`](../knowledge-base/extending.md) and [`rag/concepts.md`](../rag/concepts.md).

## What an uncomposed deployment sees

This is the first thing to understand about OCR, because the default is not "OCR is off" — it is "OCR is impossible", and that has a user-visible consequence.

| Upload | No OCR companion (default) | With a companion |
|---|---|---|
| text PDF, DOCX, XLSX, CSV, PPTX, TXT | indexed, unchanged | indexed, unchanged |
| **scanned PDF** (pages are images) | stored, **`OcrUnavailable`** | **indexed**, one chunk per page, citable by page |
| **image** (`.png` `.jpg` `.tiff` `.bmp` `.gif` `.webp`) | stored, **`OcrUnavailable`** | **indexed**, citable as page 1 |

`OcrUnavailable` is an `IngestionStatus` case in its own right, distinct from its two neighbours, and the distinction is the point:

- `UnsupportedFormat` — *no extractor recognises this type*. The user's remedy is to upload something else.
- `Complete 0` — *the file was read and genuinely contained nothing*. No remedy needed.
- `OcrUnavailable` — *the type is recognised and the content is there, but reading it needs a capability this deployment does not have*. The remedy belongs to the **operator**, not the user.

Before this existed, a scanned upload landed as `Complete 0`: the KB reported a successful index of zero chunks, the user believed their scan was searchable, and nothing anywhere said that OCR was the missing piece. The core still costs nothing when no companion is composed (GP 13) — but a deployment can now *see* what it is not paying for.

The client badges it "Scanned · OCR unavailable" in amber, with the reason as the tooltip. The reason names a concrete companion, because "OCR unavailable" on its own tells an operator the symptom and nothing else.

## What's shipped

| Provider | Engine | Runs where | Cost |
|---|---|---|---|
| `NoOpOcrProvider` (default, in `ToolUp.RAG.Server`) | none | — | zero |
| `ToolUp.OcrProviders.Tesseract` | Tesseract 5 (LSTM), in-process | your own hardware | none per page |

Hosted document-understanding services (Azure Document Intelligence, AWS Textract, Google Document AI) fit the same interface and would each be a companion of their own; none is shipped. They bill per page, so none of them could ever be a default (GP 2).

## `ToolUp.OcrProviders.Tesseract`

Use when: the corpus contains scanned documents or photographed pages; OCR must run on your own hardware with no per-page cost and nothing leaving the deployment.

Don't use when: no scanned content exists — a deployment that never composes it loads no native library and pays nothing.

Tesseract and its trained language data are Apache-2.0, so this is not a paid-by-default dependency (GP 2). The wrapper and the native libraries stay inside the companion and never reach `ToolUp.Platform.*` (GP 1).

### Prerequisites

Two things the package does not ship:

1. **Language data.** `.traineddata` files for the languages you index — [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) is the usual choice. Put them in one directory.
2. **The native engine on non-Windows.** `win-x64` / `win-x86` binaries are vendored by the upstream package. Elsewhere install the platform's own (`apt-get install -y libtesseract5 libleptonica-dev`, `brew install tesseract`).

### Setup

```fsharp skip=fragment
open ToolUp.RAG.OcrProviders.Tesseract

// Probes the tessdata directory, the language files, and the native
// library — every one of them at `create`, never at first call.
let ocr = TesseractOcrProvider.createForTessData "/var/lib/tessdata"
```

Register it in DI **before** `composeWithRAG` runs. The RAG composition probes for an already-registered `IOcrProvider` and only falls back to the no-op when it finds none, so registration is the whole of the wiring:

```fsharp skip=fragment
services.AddSingleton<ToolUp.Platform.IOcrProvider.IOcrProvider>(ocr)
```

The tessdata path arrives from the deployment's own configuration or `ISecretStore` — like every companion, this one never reads environment variables itself.

Register the readiness probe alongside it, so an operator sees a tessdata volume that has gone away before every scanned upload starts reporting `OcrUnavailable`:

```fsharp skip=fragment
Health.create (TesseractOcrOptions.forTessData "/var/lib/tessdata")
```

### Tuning

```fsharp skip=fragment
let ocr =
    TesseractOcrProvider.create {
        TesseractOcrOptions.forTessData "/var/lib/tessdata" with
            Language = "eng+deu"
            MaxPages = 50
            MaxConcurrency = 2
            DocumentTimeout = TimeSpan.FromMinutes 2.0
    }
```

Full option table in the companion's own [README](https://github.com/ToolUp-Forge/toolup-forge/blob/main/src/OcrProviders/Tesseract/README.md). Two of the levers deserve mention here because they are the ones that bite:

- **`MaxConcurrency` is the memory ceiling.** Each concurrent slot holds one engine with an LSTM model loaded — tens of MB. It and the RAG ingestion concurrency (`withIngestionConcurrency`) bound the same machine, and OCR is by far the more expensive of the two, so size this one first.
- **`DocumentTimeout` is per document, checked between pages** — not per page. `TesseractEngine.Process` is a synchronous native call with no cancellation token, so a per-page timeout could only abandon a thread that keeps running and keeps holding native memory. A between-pages deadline bounds the work honestly.

`MaxPages` truncates (a partial index of a 900-page scan is still useful); `MaxDocumentBytes` raises (a half-OCR'd document reported as complete is worse than a visible refusal).

### Fail-loud at `create`, not at first call

Every misconfiguration a native companion can carry is raised at composition, as a `TesseractOcrException` naming the operator action:

| Failure | What the message says |
|---|---|
| invalid options | every invalid lever at once, not just the first |
| tessdata directory absent | the path it looked for, and where to download the data |
| `<language>.traineddata` absent | the specific file, for each language named in `Language` |
| no native library for this RID | the RID, plus the install command for Linux and macOS |

The alternative — discovering at first P/Invoke, inside a background ingestion — surfaces as a document stuck at `Failed` with a native error in a log, which tells nobody that the deployment was never going to work.

## Uploading images

Image extensions are deliberately **not** in the KB's `supportedExtensions` set, which is what the Phase 119 upload policy reads. A deployment whose policy rejects unrecognised types therefore keeps rejecting images exactly as before, composed OCR or not (GP 11). To accept them, name them in the policy's `AllowedExtensions`, or leave the default permissive policy in place.

What changed is only what happens once an image *has* been stored: with a companion it now indexes, and without one it reports `OcrUnavailable` rather than "no extractor for `.png`".

## Writing your own

Implement `IOcrProvider` — `Name`, `IsScanned`, `ExtractText` — and register it in DI before `composeWithRAG`. See [`rag/extending.md`](../rag/extending.md). Two contract notes:

- `IsScanned` is a **cheap heuristic** and is allowed to be wrong. The extractor carries a fallback: when native text extraction produces nothing at all and a real companion is composed, it calls `ExtractText` anyway. A provider does not have to be certain.
- `ExtractText` returns 1-based page numbers, which round-trip into the citation locator. Pages that recovered nothing may be omitted; callers do not assume the numbers are contiguous.
