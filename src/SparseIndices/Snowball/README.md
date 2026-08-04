# ToolUp.SparseIndices.Snowball

Language-aware `ISparseAnalyzer` for the ToolUp.RAG sparse (BM25) index:
**Snowball/Porter2 English stemming**, **stop-word removal** and **diacritic
folding** for seven space-delimited European languages.

## Why

The in-tree BM25 index tokenises on Unicode letter/digit runs and lower-cases —
correct for English business prose and code-like identifiers, and blind to
morphology. A corpus that says *"the policy governs recurring **payments**"*
does not answer *"how is a recurring **payment** processed"* on the lexical
leg, because `payments` and `payment` are different terms. The dense leg often
rescues it; when it does not, the failure is invisible — retrieval returns
*something*, just not the passage that answers the question.

This companion makes the lexical leg agree with the language.

## Install

```xml
<PackageReference Include="ToolUp.SparseIndices.Snowball" Version="..." />
```

## Compose

```fsharp
open ToolUp.SparseIndices.Snowball

let app =
    RAGServerApp.create factory profile embedder
    |> RAGServerApp.withSparseAnalyzer (SnowballAnalyzer.english ())
```

Constructing the index directly (tests, evaluation harnesses, a bespoke
composition root):

```fsharp
let index =
    new InMemoryBM25Index(storage, logger, analyzer = SnowballAnalyzer.english ())
```

Other languages:

```fsharp
// Stop-word removal + diacritic folding; no stemmer (see "What ships" below).
SnowballAnalyzer.forLanguage StopWords.French
```

Full control:

```fsharp
SnowballAnalyzer.create {
    Language = StopWords.German
    Stemming = CustomStemmer("cistem-1", MyGermanStemmer.stem)
    RemoveStopWords = true
    FoldDiacritics = false
    MinTermLength = 2
}
```

## What ships

| Language | Stop words | Diacritic folding | Built-in stemmer |
|---|---|---|---|
| English (`en`) | yes | yes | **yes** — Snowball / Porter2 |
| French (`fr`) | yes | yes | no — supply `CustomStemmer` |
| German (`de`) | yes | yes | no — supply `CustomStemmer` |
| Spanish (`es`) | yes | yes | no — supply `CustomStemmer` |
| Italian (`it`) | yes | yes | no — supply `CustomStemmer` |
| Dutch (`nl`) | yes | yes | no — supply `CustomStemmer` |
| Portuguese (`pt`) | yes | yes | no — supply `CustomStemmer` |

`SnowballOptions.forLanguage` picks `BuiltInStemmer` for English and
`NoStemming` elsewhere, so the obvious call is always valid. Asking for
`BuiltInStemmer` on a non-English language is **refused at `create`**
(`SnowballAnalyzerConfigurationException`) rather than silently degrading —
an analyzer that quietly does less than it was asked shows up much later as
unexplained recall.

Non-space-delimited scripts (Chinese, Japanese, Korean) are a different
problem and are served by the sibling **`ToolUp.SparseIndices.Cjk`**.

## No vendor dependency

This package has **no third-party `PackageReference`**. The English Snowball
algorithm is implemented in-package (`Porter2.fs`) against the published
algorithm description: it is a closed, fully-specified string transformation
with no data file, no native binary and no protocol that can move underneath
us. Vendoring a package to obtain it would add a supply-chain edge to an
Apache-2.0 SDK in exchange for ~300 lines of suffix arithmetic that the test
pack pins with explicit vectors. That trade would read differently for anything
stateful or versioned.

## The analyzer id, and why it matters

Every analyzer reports a stable `Id`; this one encodes its whole configuration:

```
snowball+en+porter2+stop+fold+min1
```

The BM25 index stamps that id into its persisted snapshot. On load, a snapshot
whose recorded id differs from the composed analyzer's is **re-analysed from
the retained chunk text** rather than trusted. That is the point: index-time
terms and query-time terms that disagree do not raise anything — they simply
stop matching. So changing an option here costs a re-analysis pass at startup
(logged once per scope, at Info) and never a silent recall collapse.

The corollary for `CustomStemmer`: its `id` argument must change whenever the
stemmer's behaviour does.

## Behaviour notes

- **Stop-word removal runs before folding**, so the lists can be written in
  each language's own orthography and a folding deployment drops the same
  words as a non-folding one.
- **A query made entirely of stop words keeps them.** `"to be or not to be"`
  would otherwise analyse to nothing and return zero results, which is worse
  than returning low-value matches. The rule lives inside `Analyse`, so it
  applies identically on both sides.
- **Folding is on by default**, including for German (`schön` → `schon`). A
  deployment that wants German umlaut expansion (`schön` → `schoen`) turns
  folding off and supplies a stemmer that does it.
- **`MinTermLength` applies to the query side too.** Raising it makes short
  legitimate terms unsearchable, not merely unindexed.

## Posture

Pure, deterministic, stateless between calls, no I/O — so it contributes
`CompanionCapability.identity` and declares no posture descriptor (the
undeclared default is already correct). Safe in any distributed topology:
two replicas configured identically produce identical terms.

## See also

- `docs/companions/sparse-analyzers.md` — the seam, the symmetry guarantee, and
  how to choose between the shipped analyzers.
- `ToolUp.SparseIndices.Cjk` — segmentation for non-space-delimited scripts.
