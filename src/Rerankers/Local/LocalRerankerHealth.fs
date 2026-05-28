// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Rerankers.Local.LocalRerankerHealth

open System
open System.IO
open ToolUp.Platform.HealthChecks

// ─── Phase 14f local reranker health probe ──────────────────────
//
// The reranker is in-process once `create` succeeds (no network, no
// credentials). The useful readiness signal is "are the
// operator-provisioned model + vocab still present on disk" — a
// deployment that mounts the model from a volume that later
// disappears should show Unhealthy rather than fail opaquely on the
// next rerank. `create` already fails loud at startup; this probe
// catches post-startup disappearance.

type LocalRerankerHealthCheck(modelPath: string, vocabPath: string) =
    interface IHealthCheck with
        member _.Name = "reranker:local"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            if not (File.Exists modelPath) then
                return Unhealthy $"reranker model missing at '{modelPath}'"
            elif not (File.Exists vocabPath) then
                return Unhealthy $"reranker vocab missing at '{vocabPath}'"
            else
                return Healthy
        }

let create (modelPath: string) (vocabPath: string) : IHealthCheck =
    LocalRerankerHealthCheck(modelPath, vocabPath) :> IHealthCheck