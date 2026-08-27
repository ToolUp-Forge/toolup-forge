# ToolUp.SparseIndices.Cjk

Character **n-gram segmentation** `ISparseAnalyzer` for non-space-delimited
scripts — Chinese, Japanese, Korean.

## Why

The default analyzer splits text on Unicode letter/digit runs. A CJK sentence
contains no spaces, so the whole clause arrives as **one term**: the document is
reachable only by a query that reproduces the clause verbatim. The sparse leg of
hybrid retrieval is not degraded on such a corpus — it is inert, and the dense
leg carries the whole load without anyone noticing.

This companion segments CJK runs into overlapping n-grams so BM25 has terms to
work with, with no dictionary, no model and no native binary.

## Install and compose

```xml
<PackageReference Include="ToolUp.SparseIndices.Cjk" Version="..." />
```

```fsharp skip=fragment
open ToolUp.SparseIndices.Cjk

let app =
    RAGServerApp.create factory profile embedder
    |> RAGServerApp.withSparseAnalyzer (CjkAnalyzer.bigrams ())
```

Or with explicit options:

```fsharp
CjkAnalyzer.create { NGramSize = 2; IncludeUnigrams = true }
```

## Behaviour

`東京都の人口` analyses to `東京 / 京都 / 都の / の人 / 人口` — a query for `東京`
or `人口` now matches.

**Mixed-script text is handled in one pass.** Runs are split at the CJK
boundary: CJK runs are n-grammed, everything else falls through to the shipped
word tokenisation. `Windows 11 の設定` yields `windows / 11 / の設 / 設定`. You
do not need a second analyzer for a corpus that mixes languages.

| Option | Default | Effect |
|---|---|---|
| `NGramSize` | `2` | Width of the sliding window over a CJK run. 1 indexes single characters (close to indexing letters); 3+ misses two-character words, which are most of them. |
| `IncludeUnigrams` | `false` | Also emit single characters. Buys recall for one-character queries at the cost of long posting lists. |

A CJK run shorter than `NGramSize` always emits its characters, so a
single-character token is never dropped.

## Posture: production-viable, not best-in-class

Stated plainly, because the alternative is discovering it in production.

**What it costs**

- **Index size** — roughly one posting per character rather than per word;
  expect the sparse index to grow 2–3× on CJK-heavy corpora.
- **Precision** — bigrams cross word boundaries, so the phrase `東京都` also
  generates `京都`, which names a different city. IDF absorbs much of this and
  RRF fusion with the dense leg absorbs more, but a bigram index does return
  matches a word segmenter would not.

**When to use this**: you want CJK retrieval to work today with zero
operational surface, or CJK is a secondary language in a mixed corpus.

**When to reach past it**: CJK is the primary corpus language and precision
matters more than deployment simplicity. A dictionary or model-based segmenter
(MeCab / Kuromoji for Japanese, jieba for Chinese) produces word tokens, a
smaller index and better precision — and carries a dictionary file or a native
binary, which is a different companion with a different supply-chain posture.
That is deliberately out of scope here; the seam is the same, so such a
companion drops in without touching the index.

Pure, deterministic, stateless, no I/O — `CompanionCapability.identity`, safe in
any distributed topology.

## See also

- `docs/companions/sparse-analyzers.md` — the seam and the symmetry guarantee.
- `ToolUp.SparseIndices.Snowball` — stemming / stop words for space-delimited
  European languages.
