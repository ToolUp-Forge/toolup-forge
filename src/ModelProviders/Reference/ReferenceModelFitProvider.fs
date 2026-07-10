// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ModelProviders.Reference

open System
open System.Text
open System.Security.Cryptography
open ToolUp.Platform

// ─── Phase 449 — reference IModelFitProvider ────────────────────────────
//
// A trivial deterministic fitter that binds the model-fit contract pack
// and proves the provider seam. It is **explicitly not statistics** — it
// computes a mean/identity-class value as a pure, seeded function of the
// request's identity (spec hash + dataset key + seed) and reports it as
// two diagnostics. There is no estimator, no optimisation, no dataset read.
//
// **Why it does not read the dataset.** A production provider receives an
// `IDatasetStore` through its `create` function and fits against the
// vintage's rows. The reference provider deliberately derives its outcome
// from the request identity alone: it keeps the contract pack free of a
// composed dataset store, and it makes seeded reproducibility exact and
// obvious (identical request → byte-identical outcome). Forge stores and
// compares the diagnostics; it never interprets them (plan D10). All real
// modelling math lives outside forge (GP 1).
//
// **Determinism (plan D4).** The outcome is a pure function of the
// `FitRequest` — no wall-clock, no RNG. `DurationMs` / `CostUnits` are
// reported as fixed zeros so the whole `FitOutcome` is byte-identical
// across re-runs of the same seeded request.

module ReferenceModelFitProvider =

    [<Literal>]
    let Kind = "reference"

    [<Literal>]
    let Version = "1.0.0"

    /// Diagnostic keys the reference provider always populates. A consumer
    /// builds `GateSpec`s against these; the envelope still fails-closed on
    /// any requested gate whose diagnostic is absent.
    let DeclaredGates = [ "mean"; "abs_mean" ]

    /// A stable unit float in `[0, 1)` derived from a string's SHA-256. The
    /// determinism source — pure, no RNG.
    let private deterministicUnit (s: string) : float =
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes s)
        // First 8 bytes → uint64 → scaled into [0, 1).
        let u = BitConverter.ToUInt64(hash, 0)
        float u / float UInt64.MaxValue

    /// Lowercase SHA-256 hex of a UTF-8 string.
    let private sha256Hex (s: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes s) |> Convert.ToHexStringLower

    let private fit (request: FitRequest) : Async<FitOutcome> = async {
        let datasetKey = DatasetVersionRef.key request.DatasetVersion

        // Seeded identity string — the sole determinism input.
        let identity =
            FitCompositeKey.canonical request.SpecRef.SpecHash datasetKey request.Seed Kind Version

        // A mean/identity-class value — NOT a real fit. Two diagnostics
        // so gate evaluation can be exercised in both directions.
        let mean = deterministicUnit identity
        let absMean = abs (mean - 0.5)

        let diagnostics = Map [ "mean", mean; "abs_mean", absMean ]

        // Deterministic artifact reference — a hash of the identity, no
        // real bytes. A production provider would persist the fitted
        // artifact and reference the stored blob here.
        let artifactHash = sha256Hex ("artifact:" + identity)

        let key =
            FitCompositeKey.compute request.SpecRef.SpecHash datasetKey request.Seed Kind Version

        return {
            // The envelope re-computes + overwrites CompositeKey +
            // GateVerdicts; we set a consistent key so a provider used
            // outside the envelope still returns a coherent outcome.
            CompositeKey = key
            ArtifactRef = {
                ArtifactId = key.Hash
                ContentHash = artifactHash
                ByteLength = int64 artifactHash.Length
            }
            Diagnostics = diagnostics
            GateVerdicts = []
            DurationMs = 0L
            CostUnits = 0.0
        }
    }

    /// The reference provider as an `IModelFitProvider`. Takes no substrate
    /// dependencies — it is deliberately trivial (a production provider
    /// receives an `IDatasetStore` here and reads the vintage's rows).
    let create () : IModelFitProvider =
        { new IModelFitProvider with
            member _.Kind = Kind
            member _.ProviderVersion = Version
            member _.DeclareGates() = DeclaredGates
            member _.Fit(request) = fit request
        }