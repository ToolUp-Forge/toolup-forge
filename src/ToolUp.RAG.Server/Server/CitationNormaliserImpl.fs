// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.CitationNormaliserImpl

open System
open System.Collections.Generic
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI

// ─── Phase 6q follow-up — RAG-side ICitationNormaliser bridge ────────
//
// Bridges the RAG-private `CitationNormaliser.normalise` substrate
// to the AI-side `ICitationNormaliser` seam (declared in
// `ToolUp.AI.Server/Server/ICitationNormaliser.fs`). RAGCompose
// registers a concrete instance closing over the active
// `RagCitationPolicy` + the rolling-window counter store;
// `AIAssistantHandler` resolves the seam interface from DI and
// invokes it after `runAgentLoop` settles.

/// Map a RAG-side `CitationAction` onto the AI-side equivalent.
/// Names are deliberately identical across the two modules so the
/// translation is one-to-one.
let private mapAction (action: CitationNormaliser.CitationAction) : CitationAction =
    match action with
    | CitationNormaliser.NormalisedToCanonical idx -> NormalisedToCanonical idx
    | CitationNormaliser.StrippedPhantom -> StrippedPhantom
    | CitationNormaliser.UnverifiedTagged -> UnverifiedTagged

let private mapEvent (event: CitationNormaliser.CitationEvent) : CitationEvent = {
    Variant = event.Variant
    Digit = event.Digit
    Action = mapAction event.Action
}

let private mapResult (result: CitationNormaliser.NormaliseResult) : CitationNormalisation = {
    Text = result.Text
    Events = result.Events |> List.map mapEvent
    Normalisations = result.Normalisations
    Strips = result.Strips
    UnverifiedTags = result.UnverifiedTags
}

// ─── Rolling-window counter store ────────────────────────────────────
//
// Fixed-size ring buffer per (provider, model) bucket. The
// `/dev/rag-citation` endpoint reads `Snapshot` and serialises the
// rows. Tuned conservatively so a chatty deployment doesn't blow up
// the process heap: 60 recent rewrites per bucket, no cross-bucket
// fan-out.

[<Literal>]
let private SampleRingSize = 60

[<Literal>]
let private RecentSampleCount = 8

type private BucketState = {
    mutable TotalTurns: int
    mutable Normalisations: int
    mutable Strips: int
    mutable UnverifiedTags: int
    /// Ring buffer of recent rewrites — newest entries overwrite
    /// oldest. Stored as `(timestamp, eventCount, sampleVariants)`
    /// so the snapshot path doesn't have to chase per-event lists.
    Samples: CitationRecentRewrite[]
    mutable SampleNextIndex: int
    mutable SampleCount: int
}

let private newBucket () : BucketState = {
    TotalTurns = 0
    Normalisations = 0
    Strips = 0
    UnverifiedTags = 0
    Samples = Array.zeroCreate SampleRingSize
    SampleNextIndex = 0
    SampleCount = 0
}

type RollingCitationCounters() =
    let buckets = Dictionary<string * string, BucketState>()
    let sync = obj ()

    let getOrCreate (provider: string) (model: string) =
        let key = provider, model

        match buckets.TryGetValue key with
        | true, b -> b
        | false, _ ->
            let b = newBucket ()
            buckets[key] <- b
            b

    interface ICitationCounters with
        member _.Record(provider, model, result) =
            // Skip turns that produced no events — recording an
            // empty Normalise call would pollute the per-(provider,
            // model) "TotalTurns" with no diagnostic value.
            if result.Events.IsEmpty then
                ()
            else
                lock sync (fun () ->
                    let bucket = getOrCreate provider model
                    bucket.TotalTurns <- bucket.TotalTurns + 1
                    bucket.Normalisations <- bucket.Normalisations + result.Normalisations
                    bucket.Strips <- bucket.Strips + result.Strips
                    bucket.UnverifiedTags <- bucket.UnverifiedTags + result.UnverifiedTags

                    let sample = {
                        OccurredAt = DateTime.UtcNow
                        EventCount = result.Events.Length
                        SampleVariants = result.Events |> List.truncate RecentSampleCount |> List.map _.Variant
                    }

                    bucket.Samples[bucket.SampleNextIndex] <- sample
                    bucket.SampleNextIndex <- (bucket.SampleNextIndex + 1) % SampleRingSize
                    bucket.SampleCount <- min (bucket.SampleCount + 1) SampleRingSize)

        member _.Snapshot() =
            lock sync (fun () ->
                buckets
                |> Seq.map (fun kvp ->
                    let provider, model = kvp.Key
                    let bucket = kvp.Value

                    let recent =
                        if bucket.SampleCount = 0 then
                            []
                        else
                            // Walk the ring buffer newest-first so the
                            // dev endpoint shows the latest activity at
                            // the top of the list.
                            let mutable idx = (bucket.SampleNextIndex - 1 + SampleRingSize) % SampleRingSize

                            [
                                for _ in 1 .. min RecentSampleCount bucket.SampleCount do
                                    yield bucket.Samples[idx]
                                    idx <- (idx - 1 + SampleRingSize) % SampleRingSize
                            ]

                    {
                        ProviderName = provider
                        ProviderModel = model
                        TotalTurns = bucket.TotalTurns
                        Normalisations = bucket.Normalisations
                        Strips = bucket.Strips
                        UnverifiedTags = bucket.UnverifiedTags
                        RecentRewrites = recent
                    })
                |> Seq.toList)

// ─── Normaliser bridge ───────────────────────────────────────────────

/// Captures the active `RagCitationPolicy` + the counter store at
/// composition time so `Normalise` calls don't have to re-resolve
/// either per request.
type CitationNormaliserImpl(policy: CitationNormaliser.RagCitationPolicy, counters: ICitationCounters) =
    interface ICitationNormaliser with
        member _.Normalise(sources, text, providerName, model) =
            let raw = CitationNormaliser.normalise sources policy text
            let mapped = mapResult raw
            counters.Record(providerName, model, mapped)
            mapped

/// Construct an `ICitationNormaliser` over the supplied policy +
/// counter store. Returned typed as the seam interface so DI
/// registrations don't leak the concrete type.
let create (policy: CitationNormaliser.RagCitationPolicy) (counters: ICitationCounters) : ICitationNormaliser =
    CitationNormaliserImpl(policy, counters) :> _