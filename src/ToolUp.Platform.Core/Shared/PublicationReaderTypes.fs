// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 1107 — publication readers granted inside the platform ─────
//
// A `Publication` page admits a principal holding one of its audience's
// reader roles. Until this phase the only way to hold one was for the
// identity provider to assert it (a directory group). An Owner or Admin of
// the publishing scope can now grant a reader role to a principal from the
// admin client: the grant is explicit, names exactly one scope and one role
// the scope's publications declare, and is never implied by team
// membership, module permissions or the platform-admin role. A principal
// admitted on a grant alone is a publication reader, confined exactly as a
// directory-group reader is.

/// One in-platform reader grant in a publishing scope: `UserId` (the
/// principal's sign-in user id) holds reader role `Role`.
type PublicationReaderEntry = { UserId: string; Role: string }

/// The readers of the caller's publishing scope, as the admin client shows
/// them.
type PublicationReaderRoster = {
    /// The publishing scope — the caller's active team.
    PublishingScope: string
    /// The reader roles the scope's publication audiences declare — the only
    /// roles a grant may name. Empty when no publication in the scope
    /// declares readers (or no publication source is composed), in which
    /// case every grant is refused.
    DeclaredRoles: string list
    /// The in-platform grants, ordered by role then user id. Readers whose
    /// identity provider asserts the role are not listed: the directory
    /// administers those.
    Readers: PublicationReaderEntry list
}

/// Remoting API behind the publication-readers panel of the team admin
/// surface. Every method is scoped to the caller's active team (the
/// publishing scope), requires an Owner or Admin of that team
/// (`TeamRoles.canWriteTeamConfig`), and refuses machine callers. Each
/// grant and revocation is audited (`PublicationReaderGranted` /
/// `PublicationReaderRevoked`) under the publishing scope.
type IPublicationReaderApi = {
    /// The scope's declared reader roles and its in-platform grants.
    [<TenantScoped>]
    ListReaders: unit -> Async<Result<PublicationReaderRoster, string>>

    /// Grant `Role` to `UserId`. Refuses a role the scope's publications do
    /// not declare, and a blank user id. Idempotent.
    [<TenantScoped>]
    GrantReader: PublicationReaderEntry -> Async<Result<unit, string>>

    /// Revoke `Role` from `UserId`; it bites at the reader's next request.
    /// Idempotent.
    [<TenantScoped>]
    RevokeReader: PublicationReaderEntry -> Async<Result<unit, string>>
}

module PublicationReaderApi =
    /// Remoting endpoint prefix, as for the platform's other admin APIs.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"