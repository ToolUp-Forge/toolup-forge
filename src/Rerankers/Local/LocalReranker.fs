// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Rerankers.Local.LocalReranker

open System
open System.IO
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open Microsoft.ML.Tokenizers
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IReranker

// ─── Phase 14f — local ONNX cross-encoder reranker companion ────
//
// A CPU-bound cross-encoder `IReranker`. Scores each (query, chunk)
// pair jointly with a BERT-family cross-encoder exported to ONNX,
// replaces `VectorMatch.Score` with the cross-encoder logit, and
// returns the pool sorted descending (every input candidate
// preserved — filtering is the caller's job).
//
// GP 1 / 11: the ONNX runtime + tokenizer are companion-only deps;
// the SDK core never references them. The model + vocab are
// OPERATOR-PROVISIONED (paths passed to `create`) — no multi-MB
// third-party weight is committed to this OSS repo. The README
// documents the recommended models
// (`mixedbread-ai/mxbai-rerank-xsmall-v1`, fallback
// `BAAI/bge-reranker-base`) and the one-line `optimum` export.
//
// Phase 9c portability: `create` builds the `InferenceSession` once
// and treats it as immutable infrastructure (sanctioned by the
// `IReranker` doc, rule 4 — model weights are not per-call state);
// every `Rerank` is fully parameterised and independent.
//
// Verification note: ONNX inference cannot be exercised without a
// model file (and is not run in the SDK CI). The tensor I/O follows
// the standard HF cross-encoder export contract; tensor names are
// configurable for exports that differ.

type LocalRerankerOptions = {
    /// Path to the exported cross-encoder `.onnx` model. Required.
    ModelPath: string
    /// Path to the model's WordPiece `vocab.txt`. Required.
    VocabPath: string
    /// Stable identifier surfaced to the eval harness / traces.
    Name: string
    /// Advisory cap the SDK pipeline truncates to before calling.
    MaxBatchSize: int
    /// Pair sequences longer than this are truncated (document side
    /// first, then query) to fit the model's positional budget.
    MaxSequenceLength: int
    /// ONNX graph input tensor names. Defaults match the standard HF
    /// export. `TokenTypeIdsName = None` for single-segment exports
    /// that omit token_type_ids.
    InputIdsName: string
    AttentionMaskName: string
    TokenTypeIdsName: string option
    /// ONNX graph output tensor name. `None` → use the first output.
    OutputName: string option
}

module LocalRerankerOptions =
    /// Minimal construction — only the two operator-provisioned paths
    /// are required; everything else takes a sensible default.
    let create (modelPath: string) (vocabPath: string) = {
        ModelPath = modelPath
        VocabPath = vocabPath
        Name = "local-cross-encoder"
        MaxBatchSize = 32
        MaxSequenceLength = 512
        InputIdsName = "input_ids"
        AttentionMaskName = "attention_mask"
        TokenTypeIdsName = Some "token_type_ids"
        OutputName = None
    }

type private LocalReranker(options: LocalRerankerOptions, session: InferenceSession, tokenizer: BertTokenizer) =

    let i64 (xs: int seq) : int64[] = xs |> Seq.map int64 |> Seq.toArray

    /// Encode the `(query, document)` pair into the model's
    /// `input_ids` / `attention_mask` / `token_type_ids` tensors.
    ///
    /// The tokenizer's own `BuildInputsWithSpecialTokens` /
    /// `CreateTokenTypeIdsFromSequences` add the `[CLS] q [SEP] d
    /// [SEP]` layout and matching segment ids — so we never hand-pick
    /// special-token ids (robust across vocab configs). The document
    /// side is truncated first, then the query, so the assembled
    /// length (raw + 3 specials) stays within `MaxSequenceLength`.
    /// One sequence per call → no padding (batch of one).
    let encodePair (query: string) (document: string) : int64[] * int64[] * int64[] =
        // Named arg forces the `addSpecialTokens` overload (F# can't
        // otherwise disambiguate it from the optional-bool overload).
        // `false` → raw subwords; BuildInputsWithSpecialTokens adds the
        // [CLS]/[SEP] layout.
        let qIds = tokenizer.EncodeToIds(query, addSpecialTokens = false)
        let dIds = tokenizer.EncodeToIds(document, addSpecialTokens = false)

        let budget = max 0 (options.MaxSequenceLength - 3)
        let q = qIds |> Seq.truncate budget |> Seq.toArray
        let docBudget = max 0 (budget - q.Length)
        let d = dIds |> Seq.truncate docBudget |> Seq.toArray

        let inputIds = tokenizer.BuildInputsWithSpecialTokens(q, d)
        let segments = tokenizer.CreateTokenTypeIdsFromSequences(q, d)

        let ids = i64 inputIds
        let typeIds = i64 segments
        let mask = Array.create ids.Length 1L
        ids, mask, typeIds

    let tensor (data: int64[]) =
        DenseTensor<int64>(System.Memory<int64>(data), System.ReadOnlySpan<int>([| 1; data.Length |]))

    let scorePair (query: string) (document: string) : float =
        let ids, mask, segments = encodePair query document

        let inputs = ResizeArray<NamedOnnxValue>()
        inputs.Add(NamedOnnxValue.CreateFromTensor(options.InputIdsName, tensor ids))
        inputs.Add(NamedOnnxValue.CreateFromTensor(options.AttentionMaskName, tensor mask))

        match options.TokenTypeIdsName with
        | Some name -> inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor segments))
        | None -> ()

        use results =
            match options.OutputName with
            | Some name -> session.Run(inputs, [| name |])
            | None -> session.Run inputs

        let first = Seq.head results
        // Cross-encoders in the recommended set emit a single relevance
        // logit ([1] or [1,1]). Flatten and take the first element.
        let t = first.AsTensor<float32>()
        float (t.GetValue 0)

    interface IReranker with
        member _.Name = options.Name
        member _.MaxBatchSize = options.MaxBatchSize

        member _.Rerank (query: string) (candidates: VectorMatch list) : Async<VectorMatch list> = async {
            try
                return
                    candidates
                    |> List.map (fun c -> {
                        c with
                            Score = scorePair query c.Content
                    })
                    |> List.sortByDescending _.Score
            with _ ->
                // A reranker that cannot run degrades to "no rerank":
                // return the pool untouched rather than fail the whole
                // retrieval. Construction-time misconfiguration (missing
                // model / vocab) already failed loud in `create`; this
                // guards only transient inference faults.
                return candidates
        }

/// Build a local ONNX cross-encoder `IReranker`. Fails loud if either
/// operator-provisioned path is missing — a misconfigured reranker
/// must surface at composition time, not silently no-op at query time.
let create (options: LocalRerankerOptions) : IReranker =
    if not (File.Exists options.ModelPath) then
        invalidArg
            (nameof options)
            $"LocalReranker: ONNX model not found at '{options.ModelPath}'. See the companion README for the export command."

    if not (File.Exists options.VocabPath) then
        invalidArg (nameof options) $"LocalReranker: tokenizer vocab not found at '{options.VocabPath}'."

    let session = new InferenceSession(options.ModelPath)
    let tokenizer = BertTokenizer.Create(options.VocabPath, BertOptions())
    LocalReranker(options, session, tokenizer) :> IReranker