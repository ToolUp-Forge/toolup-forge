// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.RAGCitationDevEndpoint

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.AI

// ─── Phase 6q follow-up C — `/dev/rag-citation` rolling-window stats ─
//
// Reads `ICitationCounters.Snapshot()` and renders one
// `CitationCounterRow` per (provider, model) bucket. Mirrors the
// Phase 6i.A `/dev/ai-latency` shape: read-only, JSON, scoped via
// the `EnableDevEndpoints` runtime flag (no compile-time gate —
// `composeWithRAG` registers the route conditionally).
//
// Wire format intentionally avoids F# DUs so the report is
// Fable-free; primitives + lists only. Recent rewrites surface up
// to a small per-bucket sample so operators can spot drift trends.

let private jsonOptions = FableConverters.create ()

type private RewriteRow = {
    OccurredAt: DateTime
    EventCount: int
    SampleVariants: string list
}

type private BucketRow = {
    ProviderName: string
    ProviderModel: string
    TotalTurns: int
    Normalisations: int
    Strips: int
    UnverifiedTags: int
    RecentRewrites: RewriteRow list
}

type private Report = {
    GeneratedAt: DateTime
    Buckets: BucketRow list
    TotalTurns: int
    TotalNormalisations: int
    TotalStrips: int
    TotalUnverifiedTags: int
}

let private toRewriteRow (rw: CitationRecentRewrite) : RewriteRow = {
    OccurredAt = rw.OccurredAt
    EventCount = rw.EventCount
    SampleVariants = rw.SampleVariants
}

let private toBucketRow (row: CitationCounterRow) : BucketRow = {
    ProviderName = row.ProviderName
    ProviderModel = row.ProviderModel
    TotalTurns = row.TotalTurns
    Normalisations = row.Normalisations
    Strips = row.Strips
    UnverifiedTags = row.UnverifiedTags
    RecentRewrites = row.RecentRewrites |> List.map toRewriteRow
}

let private buildReport (rows: CitationCounterRow list) : Report =
    let buckets = rows |> List.map toBucketRow

    {
        GeneratedAt = DateTime.UtcNow
        Buckets = buckets
        TotalTurns = buckets |> List.sumBy _.TotalTurns
        TotalNormalisations = buckets |> List.sumBy _.Normalisations
        TotalStrips = buckets |> List.sumBy _.Strips
        TotalUnverifiedTags = buckets |> List.sumBy _.UnverifiedTags
    }

let private citationHandler: HttpHandler =
    fun next ctx -> task {
        let counters =
            match ctx.RequestServices.GetService(typeof<ICitationCounters>) with
            | :? ICitationCounters as c -> c
            | _ -> NoOpCitationCounters() :> ICitationCounters

        let report = buildReport (counters.Snapshot())
        let json = JsonSerializer.Serialize(report, jsonOptions)

        ctx.SetHttpHeader("Cache-Control", "no-store")
        ctx.SetContentType "application/json"
        return! ctx.WriteStringAsync json
    }

/// Route registered conditionally by `composeWithRAG`. The activation
/// gate is `ServerConfig.EnableDevEndpoints` as the master switch, with
/// `ServerConfig.EnableCitationDevEndpoint` (default `None`) as a
/// suppress-only per-endpoint override — `Some false` suppresses this
/// endpoint specifically while leaving other dev endpoints enabled.
/// The override cannot force the endpoint on while the master switch
/// is off (2026-06-12 audit, RAG Gap 8 — the former
/// force-on arm broke the "master off ⇒ no dev surface" audit
/// invariant for an unauthenticated endpoint carrying
/// conversation-derived rewrite samples).
let routes: HttpHandler list = [ route "/dev/rag-citation" >=> citationHandler ]