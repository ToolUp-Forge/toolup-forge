// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Toolup.NarrativeCommit

open ToolUp.Platform

// ─── Per-tab handler cache ────────────────────────────────────────
//
// `NarrativeRenderer` shows a "Save to Knowledge Base" button next to
// its copy-markdown button. The renderer lives in ToolUp.Platform and
// must not know anything about KnowledgeBase types — so ingestion is
// brokered through `NarrativeCommitHandler` (defined in
// `SDK.ClientTypes.fs`), whose concrete implementation is supplied
// by the KnowledgeBase companion package via
// `KnowledgeBaseView.narrativeCommitHandler` and added to
// `ClientConfig.Handlers.NarrativeCommitHandler`.
//
// `SDK.Client.program` reads `config.Handlers.NarrativeCommitHandler`
// at boot and calls `setHandler` here exactly once. `NarrativeRenderer`
// reads `current ()` per render to decide whether the Save-to-KB
// button appears.
//
// Phase 13a — replaces the legacy module-load `install` side effect.
// Apps without a Knowledge Base leave `ClientConfig.Handlers
// .NarrativeCommitHandler = None` and the button hides itself.

/// Per-tab cache of the config-supplied handler. Populated once by
/// `SDK.Client.program` from `config.Handlers.NarrativeCommitHandler`;
/// read by `NarrativeRenderer`. The mutable is the implementation
/// shape of "snapshot the config-supplied value once at boot" — its
/// sole writer is the SDK boot path; companions never touch it.
let mutable private installed: NarrativeCommitHandler option = None

/// Called once by `SDK.Client.program` with the value from
/// `ClientConfig.Handlers.NarrativeCommitHandler`. Idempotent
/// re-installs are supported but in practice only happen if a test
/// harness re-runs the boot sequence.
let setHandler (handler: NarrativeCommitHandler option) : unit = installed <- handler

/// Currently-installed handler, if any. `NarrativeRenderer` reads
/// this to decide whether to show the Save-to-KB button.
let current () : NarrativeCommitHandler option = installed