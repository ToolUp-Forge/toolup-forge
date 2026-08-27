# ToolUp.Rerankers.Local

A CPU-bound ONNX **cross-encoder** `IReranker` for `ToolUp.RAG`. It
rescoring the fused candidate pool with joint `(query, chunk)`
attention, which lifts precision-at-N over the embedding-only ranking
(the best chunk often sits at position 3 instead of position 1).

Opt-in companion. Per Guiding Principles 1 / 11 the ONNX runtime and
tokenizer are **companion-only** dependencies — the SDK core never
references them, and a deployment that doesn't wire a reranker pulls
none of this weight.

## Model is operator-provisioned (no weights in this repo)

This package ships **code only**. The ONNX model and its WordPiece
`vocab.txt` are provided by the operator at deploy time — no multi-MB
third-party weight is committed to the SDK repo, and there is no
network fetch at query time (offline-by-default, GP 13).

Recommended model: **`mixedbread-ai/mxbai-rerank-xsmall-v1`**
(size-budget fallback: **`BAAI/bge-reranker-base`**). Export to ONNX
with HuggingFace `optimum`:

```bash
pip install "optimum[exporters]" onnx
optimum-cli export onnx \
  --model mixedbread-ai/mxbai-rerank-xsmall-v1 \
  --task text-classification \
  ./mxbai-rerank-xsmall-v1-onnx
# → produces model.onnx + vocab.txt in that directory
```

Drop the two files somewhere the deployment can read (a mounted
volume, a `runtimes/` directory, an init-container download — your
choice) and pass the paths to `create`.

## Wiring

```fsharp skip=fragment
open ToolUp.Rerankers.Local

let reranker =
    LocalRerankerOptions.create
        "/models/mxbai-rerank-xsmall-v1/model.onnx"
        "/models/mxbai-rerank-xsmall-v1/vocab.txt"
    |> LocalReranker.create

RAGServerApp.create factory configStore
|> RAGServerApp.withReranker reranker
|> RAGServerApp.run
```

Optionally register the readiness probe so `/dev/inspect` shows the
reranker and an operator notices if the mounted model later vanishes:

```fsharp skip=fragment
LocalRerankerHealth.create modelPath vocabPath   // : IHealthCheck
```

## Tensor contract

The implementation follows the standard HuggingFace cross-encoder
ONNX export:

| Graph input | Default name | Notes |
|---|---|---|
| token ids | `input_ids` | `[1, seqLen]`, int64 |
| attention mask | `attention_mask` | `[1, seqLen]`, int64 |
| segment ids | `token_type_ids` | `[1, seqLen]`, int64; set `TokenTypeIdsName = None` for single-segment exports |

Output: a single relevance logit (`[1]` or `[1,1]`). All names are
overridable on `LocalRerankerOptions` for exports that differ.
`MaxSequenceLength` (default 512) truncates the document side first,
then the query, to fit the model's positional budget.

## Behaviour contract

- Every input candidate is preserved (no filtering — that's the
  caller's `topK` job); only `VectorMatch.Score` is replaced and the
  list is returned sorted descending.
- `create` fails loud if the model or vocab path is missing — a
  misconfigured reranker surfaces at composition time, not as a silent
  query-time no-op.
- A transient inference fault degrades to "no rerank" (the pool is
  returned untouched) so a reranker problem never fails the whole
  retrieval.

See `src/ToolUp.RAG/TECHNICAL_GUIDE.md` for where rerank sits in the
retrieval pipeline and how to measure its quality lift with the eval
harness.
