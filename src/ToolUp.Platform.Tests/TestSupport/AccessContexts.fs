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

    /// The principal `ScopeResolutionMiddleware` would stamp over items a test
    /// set by hand (`ToolUp.Subject`, else a subject read from `ToolUp.UserId`
    /// and the `ToolUp.StorageScope`'s team; `ToolUp.PlatformRole`), restricted
    /// to `permissions` — or `Unrestricted` when the list is empty, as a team
    /// that never configured permissions is. Stamp it under
    /// `AccessContext.ItemsKey`.
    let ofItems
        (items: System.Collections.Generic.IDictionary<obj, obj>)
        (permissions: (string * ModulePermission list) list)
        : AccessContext =
        let tryItem (key: string) =
            match items.TryGetValue(box key) with
            | true, value -> Some value
            | _ -> None

        let userId =
            match tryItem "ToolUp.UserId" with
            | Some(:? string as id) -> id
            | _ -> "anonymous"

        let subject =
            match tryItem "ToolUp.Subject" with
            | Some(:? Subject as subject) -> subject
            | _ ->
                let team =
                    match tryItem "ToolUp.StorageScope" with
                    | Some(:? StorageScope as scope) -> StorageScope.tryTeamId scope
                    | _ -> None

                match team with
                | Some teamId -> TeamMember(userId, teamId.Value)
                | None when userId <> "anonymous" -> AuthenticatedUser userId
                | None -> AnonymousSession userId

        let ctx =
            match tryItem "ToolUp.PlatformRole" with
            | Some(:? PlatformRole as role) -> {
                forSubject subject with
                    PlatformRole = Some role
              }
            | _ -> forSubject subject

        {
            ctx with
                ModulePermissions = permissionsOf permissions
        }