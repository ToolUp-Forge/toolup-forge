// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.ActiveAiProbe

open ToolUp.Platform
open ToolUp.AI

// ─── Phase 171 — IActiveAiProbe implementation ───────────────────
//
// Thin adapter exposing the deployment's active AI provider/model to
// the platform-tier Home overview through the Core `IActiveAiProbe`
// seam — so `Platform.Server` can surface "active AI" WITHOUT taking
// a dependency on `ToolUp.AI` (GP 1). Registered as a DI singleton by
// `AICompose.aiServiceConfig`; when no AI is composed the seam is
// absent from DI, so the overview's `ActiveAi` resolves to `None`
// (GP 13).
//
// v1 reports the deployment platform provider (`PlatformDescriptor`)
// — `DisplayName` as the label and `DefaultModel` as the model. The
// seam already carries `scopeId`, so per-user BYOK / model-override
// refinement (reading `IProviderProfile`) is a future enhancement
// without a contract change.

let create (factory: IAIProviderFactory) : IActiveAiProbe =
    { new IActiveAiProbe with
        member _.Describe(_scopeId: string) = async {
            return
                factory.PlatformDescriptor
                |> Option.map (fun d -> {
                    ProviderLabel = d.DisplayName
                    Model = Some d.DefaultModel
                })
        }
    }