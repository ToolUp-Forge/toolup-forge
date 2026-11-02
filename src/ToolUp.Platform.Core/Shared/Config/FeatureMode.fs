// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Phase 877 — the one on/off mode every payload-free `ServerConfig`
/// toggle shares.
///
/// Before this type Core declared one nominal two-case union per toggle
/// (`FeatureMode.Disabled | FeatureMode.Enabled`, `FeatureMode.Disabled | FeatureMode.Enabled`
/// and thirty-five more of the same shape), each with its own parser arm,
/// descriptor and token mapping, for one boolean fact: whether the
/// subsystem is composed. A mode that carries a payload (`EventStoreMode`,
/// `ConversationStoreMode`), or that has a third case
/// (`SecurityHardeningMode`), keeps its own type — the shape it has is
/// the information it carries. A choice between two SUBSTRATES rather
/// than on/off (`GraphStoreMode`, `ServerlessHostMode`,
/// `DeployPlaneMode`) keeps its own type too: neither case is "off".
///
/// `RequireQualifiedAccess` because `Disabled` and `Enabled` are the
/// spellings several other unions already use for their own cases
/// (`DataSubjectRequestMode.Disabled`), and because a bare `Enabled` in a
/// record literal says nothing about what it enables.
[<RequireQualifiedAccess>]
type FeatureMode =
    /// The subsystem is not composed. The default for every toggle.
    | Disabled
    /// The subsystem is composed.
    | Enabled

[<RequireQualifiedAccess>]
module FeatureMode =
    /// `true` when the mode is `Enabled`.
    let isEnabled (mode: FeatureMode) : bool =
        match mode with
        | FeatureMode.Enabled -> true
        | FeatureMode.Disabled -> false

    /// `Enabled` for `true`, `Disabled` for `false`.
    let ofBool (enabled: bool) : FeatureMode =
        if enabled then
            FeatureMode.Enabled
        else
            FeatureMode.Disabled