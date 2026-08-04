# Phase 201 — redaction-aware original-document retrieval

**Applies to:** `ToolUp.KnowledgeBase.Server`.
**Breaking:** no. Everything is additive and defaults to prior behaviour (GP 11 / GP 13).
**Consumer action required:** none. Read on only if you want the capability.

## What changes

The Knowledge Base's original-document path was gated by **scope** and nothing else. Phase 41's
data-classification substrate redacts classified *fields* on the structured read path; Phase
102/104 return an original's *bytes* once the caller passes the scope check. Nothing joined them —
so a caller in scope for a document but lacking the per-level reader capability could read, verbatim
from the original, exactly the spans the field gate would have masked.

Phase 201 adds `IOriginalRedactor`, invoked **after** the scope gate and **before** the bytes leave
the server, driving Phase 41's own `ClassificationPolicy` / `ClassificationDecision` /
`RedactedPlaceholder`. One redaction verdict per deployment, not a second one that can drift.

New public surface (all in `KnowledgeBase.ServerOriginalRedactor`, re-exported from
`KnowledgeBase.Server` where a consumer composes it):

| Name | What it is |
|---|---|
| `IOriginalRedactor` | `Redact: OriginalDocument * AccessContext -> Async<OriginalRedaction>` |
| `OriginalRedaction` | `Deliver of document * redactedLevels` \| `Withhold of reason` |
| `ClassifiedSpan` | `{ Text; Level }` — a literal span of the decoded text and its sensitivity |
| `UnmaskableOriginalDisposition` | `Withhold` (default) \| `ServeAsIs` |
| `OriginalRedactorOptions` | `Policy` / `LocateSpans` / `Unmaskable` / `IsTextExtractable` |
| `KnowledgeBase.Server.withOriginalRedactor` | the compose-time opt-in |
| `getRedactedOriginalDocument` / `getOriginalDeliveryRedacted` | the redaction-aware handlers |
| `redactedPreviewOriginal` | the redaction-aware preview entry point |

`getOriginalDocument` and `getOriginalDelivery` are **untouched** — this phase adds surface beside
them rather than retyping either.

## Adopting it — one call

```fsharp
open KnowledgeBase.Server

let redactor =
    KnowledgeBase.ServerOriginalRedactor.createDefault (fun _original text -> async {
        // YOUR detector. Return the classified spans present in `text`.
        return
            myPiiScanner.Find text
            |> List.map (KnowledgeBase.ServerOriginalRedactor.ClassifiedSpan.create Pii)
    })

RAGServerApp.create ()
|> …
|> withOriginalRedactor redactor
```

The span locator is a **required argument** — there is no default. The SDK ships no PII detector
(GP 1: which spans of *your* corpus are `Pii` is your fact, not the SDK's), and a redactor that
silently located nothing would be worse than none at all, because it reads as protection.

To tune:

```fsharp
KnowledgeBase.ServerOriginalRedactor.OriginalRedactorOptions.create locate
|> OriginalRedactorOptions.withPolicy myPolicy              // default: ClassificationGate.defaultPolicy
|> OriginalRedactorOptions.withUnmaskableDisposition ServeAsIs
|> OriginalRedactorOptions.withTextExtractableTest myTest   // default: a `text/*` content type
|> KnowledgeBase.ServerOriginalRedactor.create
|> withOriginalRedactor
```

Pass the **same `ClassificationPolicy` value** you pass the Phase 41 field gate. Two policies that
happen to agree today are two policies that can disagree tomorrow.

## The three things to know before you compose it

### 1. It disables signed-URL delivery for originals

A signed URL hands the client a link to the **raw stored bytes**. The redactor never ran over those,
and no ordering at the API tier changes what object storage will serve — so a deployment composing
both a redactor and `withSignedOriginalUrls` would defeat the redactor entirely, silently, and only
for the deployments that took the most care.

So a composed redactor forces **inline** delivery on `GetOriginalDelivery`. Per fetch, and never
silent: the log names the interaction so an operator can see why their signed URLs stopped. This is
the shape Phase 105 already uses where object-store retention and signed delivery collide —
byte-efficiency degraded, correctness not.

Deliberately **not** decided per document. Whether a redactor *would* have acted on a given original
is only knowable by reading and decoding its bytes, which is exactly the work signed delivery exists
to avoid; a metadata-only guess ("it's a PDF, sign it") would be wrong for precisely the text-layer
PDFs a redactor gets composed for.

`redactedPreviewOriginal` takes the same position from a weaker footing: it owns only the seam it
was handed and cannot re-resolve inline, so a signed-URL target is **refused** there rather than
degraded. Compose `OriginalPreviewSeam.createDefault` (inline) if you want previews and redaction.

### 2. Binary originals are withheld by default

Span masking is defined on text. An image-only PDF, a `.docx`, an `.xlsx` — masking a span inside
one means re-authoring the container format, which is a document-processing capability the SDK does
not have and will not grow inside a redaction seam. The redactor **declines** such an original, and
`UnmaskableOriginalDisposition` decides what declining means:

* `Withhold` (**default**) — the caller gets `NoOriginalAvailable`. Fail-closed: an original the
  redactor could not inspect is one it cannot vouch for.
* `ServeAsIs` — the bytes go out unchanged. Correct only where you know your binary originals carry
  nothing the policy would mask. A deliberate, recorded choice.

What never happens is a silent unmasked delivery from a redactor that was asked to mask.

If your corpus is mostly binary and you need masking in it, extract a text layer at ingest and serve
that as the original, or supply a custom `IOriginalRedactor` that understands your format.

### 3. A withheld original is indistinguishable from an absent one

`NoOriginalAvailable`, exactly as a narrative-sourced document or a deleted blob returns (GP 4). The
caller cannot tell "you may not read this" from "there is nothing here" — that is the point.

The **audit trail** can. A withheld fetch records `KnowledgeOriginalRetrievalDenied` with
`Reason = "RedactionWithheld"`, distinct from `"NoOriginalAvailable"`.

## Audit

A redacted delivery emits, per fetch:

1. the Phase 107 `KnowledgeOriginalRetrieved` row, exactly once, unchanged; plus
2. one `ClassifiedFieldRead` row **per masked level**, `Redacted = true`,
   `EntityName = "KnowledgeOriginal"`, `FieldPath = <document id>`.

The policy is consulted per level once, not per span, so four hundred masked `Pii` spans produce one
row rather than four hundred saying the same thing. A caller the policy *allows* produces no
redaction row at all — an allowed read is the field gate's `Allow` arm and is not a redaction.

Reusing Phase 41's `ClassifiedFieldRead` rather than minting a KB-specific event is deliberate: a
reviewer asking "what did this caller get masked today" wants one query, not one per surface that
happens to mask. Identifiers and level names only — no spans, no content, no counts that could be
differenced into content.

## If you compose nothing

Nothing changes. No redactor is registered, `GetOriginalDocument` and `GetOriginalDelivery` run the
pre-201 code byte-for-byte, no decode is attempted, no allocation is made, and no classification
audit row exists (GP 11 / GP 13). A test pins that equivalence directly rather than asserting it.

## Rollback

Delete the `withOriginalRedactor` call. There is no persisted state, no migration, and no stored
artefact — the redactor acts on bytes in flight only. Originals at rest are never modified, so
removing the redactor restores unmasked delivery immediately and completely.
