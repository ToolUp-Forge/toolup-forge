# Sparse analyzers

The lexical (BM25) leg of hybrid retrieval matches **terms**. What counts as a
term is decided by an `ISparseAnalyzer`, and the shipped default does the least
possible: Unicode letter/digit runs, lower-cased. This page covers what that
costs, what the companions do about it, and the one guarantee the seam exists to
provide.

## The problem the default has

The default tokenisation is right for English business prose and code-like
identifiers (`SKU-1234` → `sku`, `1234`). It is blind to two things.

**Morphology.** A document that says *"future **renewals** are cancelled"* does
not answer *"how do I cancel a **renewal**"* on the lexical leg — `renewals` and
`renewal` are different terms. The dense leg often rescues it. When it does not,
nothing reports a problem: retrieval returns *something*, just not the passage
that answers the question.

**Non-space-delimited scripts.** A Chinese or Japanese sentence contains no
spaces, so the whole clause arrives as one term and is reachable only by a query
that reproduces it verbatim. On such a corpus the sparse leg is not degraded, it
is inert.

## What ships

| Package | For | Does |
|---|---|---|
| *(none — the default)* | any corpus | Unicode word runs, lower-cased. The pre-Phase-501 behaviour, unchanged. |
| `ToolUp.SparseIndices.Snowball` | space-delimited European languages | Snowball/Porter2 English stemming; stop-word removal and diacritic folding for seven languages. |
| `ToolUp.SparseIndices.Cjk` | Chinese / Japanese / Korean | Character n-gram segmentation of CJK runs; word tokenisation everywhere else. |

Neither companion carries a third-party dependency. Details, options and the
per-language table are in each package's `README.md`.

## Composing one

```fsharp skip=fragment
open ToolUp.SparseIndices.Snowball

let app =
    RAGServerApp.create factory profile embedder
    |> RAGServerApp.withSparseAnalyzer (SnowballAnalyzer.english ())
    |> RAGServerApp.withStorage storage
```

Directly, for a bespoke composition root or a test:

```fsharp skip=fragment
let index =
    new InMemoryBM25Index(storage, logger, analyzer = CjkAnalyzer.bigrams ())
```

A deployment with one local rule and no need for a package can build an analyzer
from a function — the `id` must change whenever the function's output would:

```fsharp skip=fragment
let analyzer =
    SparseAnalysis.create "acme-synonyms-1" (fun text ->
        SparseAnalysis.tokeniseWords text |> List.map Synonyms.canonicalise)
```

Composing nothing keeps the pre-501 behaviour byte-for-byte, and a deployment
that never composes an analyzer pays nothing for the seam's existence
(GP 11, GP 13).

## The guarantee: index-time and query-time analysis cannot diverge

This is the whole reason the seam is shaped the way it is.

An index built with stemming and queried without it does not fail. It returns
fewer results, looks like a corpus problem, and is typically noticed weeks
later. So the agreement is structural rather than conventional, in three parts:

1. **One analyzer per index.** `InMemoryBM25Index` binds a single
   `ISparseAnalyzer` and exposes a single internal analysis path. Both the
   ingestion path and the query path call it; there is no second route.
2. **Terms are a type, not a list.** The index's term-bearing entry points
   accept only `AnalysedText`, whose constructor is internal to
   `ToolUp.RAG.Core`. The only way to obtain one is `SparseAnalysis.analyse`,
   which takes an analyzer. A caller cannot hand-roll terms for one side,
   because there is no overload that would accept them.
3. **The persisted snapshot records which analyzer wrote it.** This is the case
   the type system cannot reach: the corpus was indexed *before* the analyzer
   was composed. Every analyzer reports a stable `Id` covering its whole
   configuration; the index stamps that id into `_rag/{scope}/bm25.json`, and on
   load a snapshot whose id disagrees is **re-analysed from the retained chunk
   text** rather than trusted.

Composing an analyzer over an existing corpus therefore costs one re-analysis
pass at startup — logged once per scope, at Info, naming the count and both
analyzer ids — and never a silent recall collapse. Changing an *option* on an
analyzer does the same thing, because the option is part of the id.

The corollary for anyone writing an analyzer: **`Id` must change whenever
`Analyse` would produce different terms.** An id that ignores its own options is
the one way to defeat all of the above.

## Writing an analyzer

`ISparseAnalyzer` is two members:

```fsharp skip=signature
type ISparseAnalyzer =
    abstract Id: string
    abstract Analyse: text: string -> string list
```

It is **synchronous**, which is a deliberate exception to GP 12 rule 2 ("async
at every boundary"). An analyzer is a pure function on a string — no I/O, no
identity, no state between calls — on the per-chunk ingestion path and the
per-query path. It takes the same exemption `IMetricsSink` does. An analyzer
that genuinely needs I/O (a hosted segmenter, a remote dictionary) does not
belong here: it would put a network round-trip inside every chunk indexed.

Everything else follows the standard companion rules — its own `.fsproj` with a
`<PackageId>`, its own vendor `<PackageReference>` items if any, a `README.md`
packed into the nupkg. A pure analyzer contributes
`CompanionCapability.identity` and needs no posture descriptor.

## Choosing

- **English-dominant corpus** → `ToolUp.SparseIndices.Snowball`. The stemming
  win is real and the cost is nil.
- **Other European languages** → the same package for stop words and folding;
  supply a `CustomStemmer` for morphology, since only English has a built-in
  stemmer.
- **CJK anywhere in the corpus** → `ToolUp.SparseIndices.Cjk`. It handles
  mixed-script text in one pass, so it is not an either/or with Latin content.
  Read its README on index size and precision before making it the default for
  a CJK-primary deployment.
- **Measuring the difference** → the retrieval-evaluation harness takes
  `--analyzer identity | snowball-en | cjk`, so a change can be measured on your
  own fixture rather than assumed:

```powershell
dotnet run --project src/ToolUp.RAG.Evaluation -- src/ToolUp.RAG.Evaluation/fixtures/eval-morphology.json
dotnet run --project src/ToolUp.RAG.Evaluation -- --analyzer snowball-en src/ToolUp.RAG.Evaluation/fixtures/eval-morphology.json
```

Both arms score the whole hybrid pipeline, so the reported delta is the
end-to-end effect a deployment sees, not the isolated BM25 delta.

## See also

- [`src/SparseIndices/Snowball/README.md`](../../src/SparseIndices/Snowball/README.md)
- [`src/SparseIndices/Cjk/README.md`](../../src/SparseIndices/Cjk/README.md)
- [RAG concepts](../rag/concepts.md) — where the sparse leg sits in the pipeline.
- [Vector stores](vector-stores.md) — the dense leg's companion family.
