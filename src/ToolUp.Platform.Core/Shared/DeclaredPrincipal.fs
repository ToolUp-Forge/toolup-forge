// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 1029 — one declared-principal shape ───────────────────────
//
// Every kind of declared work — a standing report subscription (Phase
// 1005), a data source's declared delivery (Phase 1015) — names the
// principal it runs as the same way: a name the declarations refer to, the
// user id the deployment's scope resolver resolves, and a label. This is
// that one record. It lives in Core so every tier that declares work can
// name it (three strings: Fable-safe); the readings that need the
// server-tier identity type — the user handed to
// `DeclaredPrincipalResolver.Resolve`, and the grant a refusal says the
// principal lacks — stay with the resolver in ToolUp.Platform.Server's
// `DeclaredPrincipal` module.

/// A principal a deployment declares in its composition: the identity
/// declared work runs as, never a scope. The deployment's scope resolver
/// resolves it exactly as it resolves a request signed in as `UserId`: a
/// team-mode deployment resolves its active team and checks its
/// membership, a per-user deployment resolves its own scope. The grants it
/// holds ARE that resolution.
type DeclaredPrincipal = {
    /// The name declarations refer to it by. Unique within a deployment's
    /// declarations of one kind.
    Name: string
    /// The identity the scope resolver resolves — the user id a request
    /// signed in as this principal would carry.
    UserId: string
    /// Label the audit trail and an admin surface show for it.
    DisplayName: string
}