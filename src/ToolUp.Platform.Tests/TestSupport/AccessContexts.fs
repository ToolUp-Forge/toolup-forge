// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// The test fixture for access contexts: explicit builders, never
/// a fallback. Production code has no context to fall back on — a request with
/// no resolved principal is served under `AccessContext.unresolved`, which
/// grants nothing — so a test that reaches a handler without running
/// `ScopeResolutionMiddleware` builds the context it means here and registers
/// or stamps it. Core-only, so every test pack can link it.
[<RequireQualifiedAccess>]
module AccessContexts =
    /// A context for `subject` to which module RBAC does not apply
    /// (`ModulePermissions.Unrestricted`) — what a member of a team that never
    /// configured permissions holds.
    let forSubject (subject: Subject) : AccessContext =
        AccessContext.ofSubject ModulePermissions.Unrestricted subject

    /// An anonymous session.
    let anonymous (sessionId: string) : AccessContext = forSubject (AnonymousSession sessionId)

    /// A signed-in user outside any team.
    let user (userId: string) : AccessContext = forSubject (AuthenticatedUser userId)

    /// A member of `teamId` whose team never configured module permissions.
    let teamMember (userId: string) (teamId: string) : AccessContext = forSubject (TeamMember(userId, teamId))

    /// A named system context (`AccessContext.system`).
    let system (reason: string) : AccessContext = AccessContext.system reason

    /// The permission value a test's grant list stands for: `Unrestricted`
    /// when it names no module (a team that never configured permissions),
    /// otherwise restricted to exactly those grants.
    let permissionsOf (permissions: (string * ModulePermission list) list) : ModulePermissions =
        if List.isEmpty permissions then
            ModulePermissions.Unrestricted
        else
            ModulePermissions.Restricted(Map.ofList permissions)

    /// `ctx` restricted to exactly `permissions` (module name, permissions held).
    let restrictedTo (permissions: (string * ModulePermission list) list) (ctx: AccessContext) : AccessContext = {
        ctx with
            ModulePermissions = ModulePermissions.Restricted(Map.ofList permissions)
    }

    /// What a test said about a request's principal, piece by piece, the way
    /// `ScopeResolutionMiddleware` resolves it. Kept under a test-only item so
    /// the stamps compose in any order.
    type Draft = {
        UserId: string option
        Subject: Subject option
        User: ToolUp.Platform.Auth.AuthenticatedUser option
        Scope: StorageScope option
        Role: PlatformRole option
        Permissions: (string * ModulePermission list) list
    }

    [<Literal>]
    let private DraftKey = "ToolUp.Tests.AccessContextDraft"

    /// The principal a draft stands for: its subject, else one read from its
    /// user id and its scope's team (a team scope is a `TeamMember`, a named
    /// user an `AuthenticatedUser`, otherwise an anonymous session), holding
    /// `permissionsOf` its grants.
    let ofDraft (draft: Draft) : AccessContext =
        let userId = draft.UserId |> Option.defaultValue "anonymous"

        let subject =
            match draft.Subject with
            | Some subject -> subject
            | None ->
                match draft.Scope |> Option.bind StorageScope.tryTeamId with
                | Some teamId -> TeamMember(userId, teamId.Value)
                | None when userId <> "anonymous" -> AuthenticatedUser userId
                | None -> AnonymousSession userId

        {
            AccessContext.ofSubject (permissionsOf draft.Permissions) subject with
                PlatformRole = draft.Role
                StorageScope = draft.Scope
                User =
                    draft.User
                    |> Option.defaultValue ToolUp.Platform.Auth.AuthenticatedUser.anonymous
        }

    let private edit (items: System.Collections.Generic.IDictionary<obj, obj>) (change: Draft -> Draft) : unit =
        let draft =
            match items.TryGetValue(box DraftKey) with
            | true, (:? Draft as draft) -> draft
            | _ -> {
                UserId = None
                Subject = None
                User = None
                Scope = None
                Role = None
                Permissions = []
              }

        let next = change draft
        items[box DraftKey] <- box next
        AccessContext.stamp items (ofDraft next)

    /// Stamp the caller's user id (as the auth provider asserted it).
    let stampUserId items (userId: string) =
        edit items (fun d -> { d with UserId = Some userId })

    /// Stamp the resolved subject.
    let stampSubject items (subject: Subject) =
        edit items (fun d -> { d with Subject = Some subject })

    /// Stamp the auth provider's user (and its id).
    let stampUser items (user: ToolUp.Platform.Auth.AuthenticatedUser) =
        edit items (fun d -> {
            d with
                User = Some user
                UserId = Some user.UserId.Value
        })

    /// Stamp the derived storage scope.
    let stampScope items (scope: StorageScope) =
        edit items (fun d -> { d with Scope = Some scope })

    /// Stamp the platform role.
    let stampPlatformRole items (role: PlatformRole) =
        edit items (fun d -> { d with Role = Some role })

    /// Stamp the module grants (`permissionsOf`).
    let stampPermissions items (permissions: (string * ModulePermission list) list) =
        edit items (fun d -> { d with Permissions = permissions })