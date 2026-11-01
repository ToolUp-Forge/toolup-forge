// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PeerTestComposition

open ToolUp.Platform
open ToolUp.InterPlatform.PeerCompose

// ─── A peer composition under test ───────────────────────────────────
//
// The peer substrate is a companion: the deployment is a `ServerApp`, and
// the peer settings are `PeerOptions` composed onto it by
// `PeerCompose.withPeer`. The compose-time audits, `PeerSurface.describe`
// and the gateway helpers read both, as a `ServerApp * PeerOptions` pair.
// These lifters build that pair in one pipeline: `app` applies a
// `ServerApp` builder to the deployment, `options` a `PeerOptions` setter
// to the peer options.

/// A deployment and its peer options, as the audits read them.
type PeerComposition = ServerApp * PeerOptions

module PeerComposition =

    /// An empty deployment and default peer options.
    let create () : PeerComposition = ServerApp.empty, PeerOptions.create ()

    /// Apply a `ServerApp` builder to the deployment.
    let app (f: ServerApp -> ServerApp) ((app, options): PeerComposition) : PeerComposition = f app, options

    /// Apply a `PeerOptions` setter to the peer options.
    let options (f: PeerOptions -> PeerOptions) ((app, options): PeerComposition) : PeerComposition = app, f options

    /// The deployment with the peer companion composed onto it.
    let compose ((app, options): PeerComposition) : ServerApp = app |> withPeer options