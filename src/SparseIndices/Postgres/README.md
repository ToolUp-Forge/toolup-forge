# ToolUp.SparseIndices.Postgres

A PostgreSQL full-text `ISparseIndex` for `ToolUp.RAG`: the keyword leg of hybrid retrieval, held in the
database instead of in one process.

The in-process default, `InMemoryBM25Index`, keeps its postings in process memory and persists them as a blob.
On more than one replica, each replica therefore has its own keyword index. A chunk ingested on replica A is
missing from replica B's keyword leg until a flush-and-reload, so hybrid retrieval gives different results
depending on which replica served the turn. With this companion the index is the database: every replica reads
and writes the same rows, so the keyword leg is the same on every replica. Pair it with
`ToolUp.VectorStores.Pgvector` for the dense leg and the whole retrieval path spans replicas.

**Distributed-readiness: production-ready / distributed-ready.** Two or more replicas may share one database.
Server-only.

## Compose

```fsharp skip=fragment
open ToolUp.RAG.RAGCompose
open ToolUp.SparseIndices.Postgres

rag
|> RAGServerApp.withSparseAnalyzer (SnowballAnalyzer.english ())        // optional
|> RAGServerApp.withAnalyzedSparseIndex (
    PostgresFullTextIndex.factory connectionString PostgresFullTextOptions.defaults (Some logger))
```

`withAnalyzedSparseIndex` hands the index the analyzer the composition composed (the identity analyzer when
none), at composition time. If you build the index yourself, pass it the analyzer and compose it with
`withSparseIndex`:

```fsharp skip=fragment
let index =
    PostgresFullTextIndex.create connectionString PostgresFullTextOptions.defaults SparseAnalysis.identity (Some logger)

rag
|> RAGServerApp.withSparseIndex index
|> RAGServerApp.withHealthCheck (PostgresFullTextIndex.health index)
```

`withSparseIndex` together with `withSparseAnalyzer` is refused at composition. A supplied index tokenises
however it was built to, so the analyzer would otherwise be silently ignored.

## The analyzer maps to a text-search configuration

Tokenisation happens inside the database, so the composed `ISparseAnalyzer` does not run here. The companion
maps the analyzer's id to the text-search configuration that tokenises the same way, and uses that configuration
for both the rows and the queries:

| Analyzer | Configuration |
|---|---|
| `SparseAnalysis.identity` (the default) | `simple` |
| `ToolUp.SparseIndices.Snowball` English with its built-in stemmer and stop words (`snowball+en+porter2+stop+…`) | `english` |
| anything else: the CJK n-gram analyzer, Snowball without stemming or with a custom stemmer, a hand-written analyzer | **refused** |

A refused analyzer fails at construction (so at composition, through `withAnalyzedSparseIndex`) with a
`PostgresFullTextIndexException` that names it. The companion never falls back to a configuration that
tokenises differently from what the deployment asked for.

To choose a configuration yourself, set `PostgresFullTextOptions.TextSearchConfiguration = Some "french"` (or
any configuration in `pg_ts_config`). It is then used on both sides and the analyzer is not consulted. A
configuration the database does not have is refused at construction.

`simple` and the identity analyzer agree on ordinary prose. They can differ on tokens PostgreSQL's parser treats
specially: a hyphenated word is indexed whole and in its parts, which only ever adds matches.

## Scoring is PostgreSQL's, not BM25

Rows are ranked with `ts_rank(tsv, query, 1)`: term frequency, normalised by document length. **It has no
inverse-document-frequency term.** The in-process index is BM25, which weighs a rare term above a common one.
Two things follow:

- **The scale differs.** Scores sit roughly in [0, 1], not [0, ~20]. Reciprocal Rank Fusion reads ranks, not
  scores, so the scale does not reach the fused result: a hybrid retrieval's score is in the fused space,
  min-max normalised onto [0, 1] (Phase 866), whichever keyword index is composed. The raw scale does reach
  anything that calls `ISparseIndex.Search` and reads the score directly.
- **The order can differ, and fusion does see order.** In a multi-term query, a chunk that repeats a common
  term can outrank one that contains the rare, discriminating term. That chunk then enters fusion at a higher
  sparse rank than BM25 would give it. The dense leg is unchanged, so the effect is bounded to the keyword
  leg's contribution to the fused rank.

Query semantics match the in-process index: the query's terms are OR-ed, so a chunk that contains any one of
them is a candidate. Equal scores are ordered by `(Scope, ChunkId)`, as in the in-process index. Both
properties are pinned by the `ISparseIndex` contract pack, which runs against this companion and the in-process
index alike.

## Measured: the retrieval evaluation, side by side

The `ToolUp.RAG.Evaluation` harness (hybrid: the local dense embedder, plus the keyword leg under test) over
every shipped retrieval fixture, with both analyzers. It was first run on PostgreSQL 17 on 2026-09-29 and
re-run on PostgreSQL 17.11 on 2026-10-01 (Phase 943), with identical figures.

```
dotnet run --project src/ToolUp.RAG.Evaluation -- --sparse-index postgres --analyzer snowball-en --out <report.json> <fixture>
```

| Fixture | Analyzer | Keyword index | Recall@1 | Recall@5 | Recall@10 | nDCG@10 | MRR |
|---|---|---|---|---|---|---|---|
| platform-readme (10 queries) | identity | in-process BM25 | 0.800 | 0.900 | 1.000 | 0.892 | 0.860 |
| | | **PostgreSQL (`simple`)** | 0.800 | 0.900 | **0.900** | **0.863** | **0.850** |
| | snowball-en | in-process BM25 | 0.800 | 1.000 | 1.000 | 0.902 | 0.870 |
| | | **PostgreSQL (`english`)** | 0.800 | **0.900** | 1.000 | **0.899** | **0.867** |
| eval-morphology (8 queries) | identity | in-process BM25 | 0.500 | 0.625 | 0.875 | 0.660 | 0.596 |
| | | PostgreSQL (`simple`) | 0.500 | 0.625 | 0.875 | 0.660 | 0.596 |
| | snowball-en | in-process BM25 | 0.875 | 1.000 | 1.000 | 0.954 | 0.938 |
| | | PostgreSQL (`english`) | 0.875 | 1.000 | 1.000 | 0.954 | 0.938 |
| eval-filtered (4 queries) | identity | in-process BM25 | 1.000 | 1.000 | 1.000 | 0.971 | 1.000 |
| | | PostgreSQL (`simple`) | 1.000 | 1.000 | 1.000 | 0.971 | 1.000 |
| | snowball-en | in-process BM25 | 1.000 | 1.000 | 1.000 | 0.987 | 1.000 |
| | | PostgreSQL (`english`) | 1.000 | 1.000 | 1.000 | 0.987 | 1.000 |

Figures in bold differ from the in-process index. The `english` configuration reproduces the Snowball
analyzer's lift on the morphology fixture exactly (Recall@1 0.500 → 0.875). There were no filter leaks in any
arm. (`eval-mixed-dim` is a vector-store fixture with no query text, and BEIR runs only after a download, so
neither is in the table.)

**What the fused results show.** Comparing each query's fused top 10 across the two indexes (the `Found` list
in the `--out` reports), 11 of the 44 query runs differ in order and 5 differ in membership. In only 2 of the 44
did a relevant chunk move, and both are the same query, `q-knowledge-base-formats` on the README fixture: rank
10 → absent under the identity analyzer, rank 5 → 6 under Snowball. That one query accounts for every bold
figure in the table. The fixture mixes common and rare terms, which is where the missing IDF term shows.

## Why the ranking stays `ts_rank` (Phase 943)

Phase 943 asked whether a BM25 score computed in SQL should replace `ts_rank`. It measured the question both
ways and kept `ts_rank`.

**A SQL BM25 does close the gap.** As a probe, the search was rewritten to compute BM25 in SQL, with the
in-process index's constants (k1 = 1.2, b = 0.75, Lucene IDF), per-scope document frequencies, and the
average document length. Over the same 44 query runs, every metric in the table above matched the in-process
index, no fused top 10 differed in membership, and 3 differed in order only. So the gap is the missing IDF term,
and the fix is known.

**It costs three to six times the keyword leg's latency.** Measured on PostgreSQL 17.11 over a synthetic
corpus of 40–160 words per chunk with a skewed vocabulary, at the 10,000- and 100,000-chunk sizes of the
Phase 929 load harness. Median of 7 runs. The cheapest BM25 form tested reads document frequencies from the
candidate rows and needs a stored per-row document length (a schema change).

| Chunks in scope | Query | `ts_rank` | SQL BM25 |
|---|---|---|---|
| 10,000 | two rare terms | 0.6 ms | 3.5 ms |
| | common + mid + rare term | 20.8 ms | 68.6 ms |
| | three common terms (every chunk matches) | 15.8 ms | 83.3 ms |
| 100,000 | two rare terms | 4.7 ms | 28.2 ms |
| | common + mid + rare term | 143.0 ms | 396.6 ms |
| | three common terms (every chunk matches) | 316.4 ms | 1,468.9 ms |

The extra cost is reading each candidate's term frequencies out of its `tsvector` and the per-scope
average-length scan, which `ts_rank` does not need. Computing the stored document length on write added about
0.1 ms per row. Keeping document frequencies in a table on write would not remove the term-frequency cost, and
would make every write to a scope update shared rows.

**The decision.** One query in 22 per analyzer moved a relevant chunk, at the edge of a cut-off, while BM25 in
SQL costs up to 1.2 s per query at 100,000 chunks. That difference is not worth this cost. Re-open the
question if a larger labelled evaluation shows a material gap: the probe above is the design to start from.

## Schema

Under `SchemaMode = AutoMigrate` (the default), construction creates what it needs, idempotently. A PostgreSQL
advisory lock serialises the migration across processes. For a role with no DDL grant, provision this and use
`VerifyOnly`:

```sql
CREATE TABLE toolup_rag_fulltext (
    scope text NOT NULL,
    chunk_id text NOT NULL,
    content text NOT NULL,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    ts_config text NOT NULL,
    tsv tsvector NOT NULL,
    PRIMARY KEY (scope, chunk_id)
);
CREATE INDEX toolup_rag_fulltext_tsv_gin ON toolup_rag_fulltext USING gin (tsv);
```

`ts_config` records the configuration each row was analysed with. When the configuration changes (you compose
a different analyzer), `AutoMigrate` re-analyses the rows written under the old one at construction and logs how
many it re-analysed. `VerifyOnly` refuses to start instead, naming the count. Re-analyse by hand with:

```sql
UPDATE toolup_rag_fulltext
SET tsv = to_tsvector('english'::regconfig, content), ts_config = 'english'
WHERE ts_config <> 'english';
```

## Isolation, deletes and erasure

- **Scope isolation (GP 4) is structural.** Scope is part of the primary key. Every statement that reads or
  mutates rows carries `scope = @scope`, and the insert carries the scope in its row identity and its conflict
  target. `Sql.scopeBoundStatements` lists them, and the test pack asserts the binding with no database present.
- **Deletes are hard deletes.** A lexical index has no soft-delete tier. `DeleteChunk` and `DeleteByScope`
  remove the rows, and every `ErasurePolicy` of `Erase` is a hard delete, as in the in-process index. Erasure
  matches the subject id in the content or in any metadata value (byte-exact, case-sensitive), and `dryRun`
  counts without deleting.

## Health

`PostgresFullTextIndex.health index` is a `Readiness` probe: a pooled round-trip against the table. A replica
that cannot reach its keyword index should leave rotation rather than serve answers it believes are hybrid.

## Failure

Every companion-level failure is a `PostgresFullTextIndexException`. That covers invalid options, an analyzer
with no configuration, an unreachable database, a missing configuration, and, under `VerifyOnly`, a missing
table or stale rows. All of them are raised at construction, never at the first query.

## Dependencies

Npgsql (PostgreSQL licence), isolated to this package (GP 1). PostgreSQL's full-text search is built in, so no
extension is needed. There is no paid default (GP 2).
