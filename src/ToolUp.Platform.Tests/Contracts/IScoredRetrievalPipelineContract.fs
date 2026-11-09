module ToolUp.Platform.Tests.Contracts.IScoredRetrievalPipelineContract

open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.IRetrievalPipeline

// ─── IScoredRetrievalPipeline contract pack ───────────────────────
//
// Parametrised laws for any pipeline that returns the space its scores
// are in. The factory builds a pipeline already holding a corpus the
// request retrieves from, and names the request and the caller; `expected`
// is the space the binding says the pipeline declares.
//
// The laws:
//   - the sibling is EXPLICIT: the pipeline implements
//     `IScoredRetrievalPipeline` (a reader probing for it finds it, so it
//     never falls back to the composed space);
//   - it declares the space the binding expects;
//   - `Matches` is exactly what `Retrieve` returns for the same request:
//     the same chunks, in the same order, with the same scores;
//   - the pool is not empty for a request the corpus answers, so the
//     equality above is not vacuous;
//   - the boost unit is a positive, finite number;
//   - `Relevance`, when present, holds every returned chunk: it is the
//     pool MMR reordered, before truncation.

/// A scored pipeline under test, with a request its corpus answers and the
/// caller it is asked as.
type Subject = {
    Pipeline: IRetrievalPipeline
    Request: RetrievalRequest
    Access: AccessContext
}

let tests (name: string) (expected: RetrievalScoreSpace) (factory: unit -> Async<Subject>) =

    let scored (subject: Subject) =
        match subject.Pipeline with
        | :? IScoredRetrievalPipeline as pipeline -> pipeline
        | _ -> failtest $"{name} does not implement IScoredRetrievalPipeline"

    let identity (m: VectorMatch) = m.Scope, m.ChunkId, m.Score

    testList $"{name} — IScoredRetrievalPipeline contract" [

        testCaseAsync "implements the scored sibling and declares the expected space"
        <| async {
            let! subject = factory ()
            let! pool = (scored subject).RetrieveScored subject.Request subject.Access
            Expect.equal pool.Space expected "declared space"
        }

        testCaseAsync "Matches is exactly what Retrieve returns, and is not empty"
        <| async {
            let! subject = factory ()
            let! pool = (scored subject).RetrieveScored subject.Request subject.Access
            let! matches = subject.Pipeline.Retrieve subject.Request subject.Access
            Expect.isNonEmpty pool.Matches "the corpus answers the request"

            Expect.equal
                (pool.Matches |> List.map identity)
                (matches |> List.map identity)
                "same chunks, order and scores"
        }

        testCaseAsync "the boost unit is positive and finite"
        <| async {
            let! subject = factory ()
            let! pool = (scored subject).RetrieveScored subject.Request subject.Access
            Expect.isTrue (pool.Unit > 0.0 && System.Double.IsFinite pool.Unit) $"unit {pool.Unit}"
        }

        testCaseAsync "Relevance, when present, holds every returned chunk"
        <| async {
            let! subject = factory ()
            let! pool = (scored subject).RetrieveScored subject.Request subject.Access

            match pool.Relevance with
            | None -> ()
            | Some relevance ->
                let held = relevance |> List.map (fun m -> m.Scope, m.ChunkId) |> Set.ofList

                for m in pool.Matches do
                    Expect.isTrue (held.Contains(m.Scope, m.ChunkId)) $"{m.ChunkId} is in the relevance order"
        }
    ]