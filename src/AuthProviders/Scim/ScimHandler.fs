// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.ScimHandler

open System
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.PermissionStore
open ToolUp.AuthProviders.ScimTypes

// ─── SCIM lifecycle mapping ──────────────────────────────────────────
//
// The transport-free half of the companion: inbound SCIM
// create/update/deactivate turned into `ITeamStore` (and, where a role
// carries module grants, `IPermissionStore`) writes. `ScimRoutes.fs`
// holds the Giraffe surface; everything semantic lives here, which is
// what lets the conformance fixtures replay whole IdP sequences without
// standing an HTTP host up.
//
// **Every write goes through the shipped stores.** Nothing here reaches
// blob storage, and no membership row is written by any path this file
// owns — which is the point (GP 6): the `MemberAdded` / `MemberRemoved`
// / `MemberRoleChanged` audit events, the `MembershipChanged`
// notification that evicts the scope-resolver cache, and the last-Owner
// safeguard all fire exactly as they do for a human admin. A SCIM push
// is a different ACTOR, not a different code path.
//
// **Scope isolation (GP 4).** A `ScimConfig` binds one token to one
// `TeamId`. Every operation in this file is expressed against
// `config.TeamId`; there is no parameter by which a request can name a
// different team, so a compromised or misconfigured SCIM token cannot
// reach another team's space. A `Groups` request naming any other id is
// a 404 — deliberately indistinguishable from a group that does not
// exist, so the endpoint is not a team-id oracle.

// ─── SCIM origin ─────────────────────────────────────────────────────

/// The actor id stamped on every audit event a SCIM push produces.
/// Mirrors the `"_bootstrap"` convention `BootstrapTeam` uses for the
/// platform's own writes: an underscore-prefixed id cannot collide with
/// a real user id, and an auditor reading the trail can separate
/// IdP-driven lifecycle from human administration without joining
/// against anything.
[<Literal>]
let ScimActorId = "_scim"

// ─── Configuration ───────────────────────────────────────────────────

/// One SCIM endpoint's configuration. Constructed by the deployment and
/// handed to `ScimServerApp.withScim`; the companion never reads an env
/// var or a config file itself (companion-authoring rule).
type ScimConfig = {
    /// The single team this endpoint provisions into. See the
    /// scope-isolation note above.
    TeamId: string
    /// `ISecretStore` scope the bearer token is read from. Use the
    /// team's own scope (`team-{teamId}`) unless the deployment
    /// deliberately holds provisioning credentials at platform level.
    SecretScope: string
    /// `ISecretStore` key holding the bearer token.
    TokenKey: string
    /// SCIM ↔ platform attribute mapping.
    Mapping: ScimAttributeMapping
    /// Base path the emitted `meta.location` values are rooted at.
    /// Purely cosmetic — IdPs use it for logging and follow-up reads.
    BaseUrl: string option
}

module ScimConfig =
    /// The default key a deployment stores the provisioning token under.
    [<Literal>]
    let DefaultTokenKey = "SCIM_BEARER_TOKEN"

    let create (teamId: string) : ScimConfig = {
        TeamId = teamId
        SecretScope = $"team-{teamId}"
        TokenKey = DefaultTokenKey
        Mapping = ScimAttributeMapping.defaults
        BaseUrl = None
    }

    let withMapping (mapping: ScimAttributeMapping) (c: ScimConfig) = { c with Mapping = mapping }

    let withSecret (scope: string) (key: string) (c: ScimConfig) = {
        c with
            SecretScope = scope
            TokenKey = key
    }

    let withBaseUrl (baseUrl: string) (c: ScimConfig) = { c with BaseUrl = Some baseUrl }

// ─── Dependencies ────────────────────────────────────────────────────

/// The substrate one SCIM request runs against. A record rather than
/// constructor parameters so the fixture replay can supply fakes for
/// exactly the seams it exercises, and so a later dependency is
/// additive.
type ScimDeps = {
    Teams: ITeamStore
    /// Present when the deployment wants role changes to also rewrite
    /// module grants. `None` leaves permissions entirely to the
    /// platform's own defaults — the conservative posture, and the
    /// default.
    Permissions: IPermissionStore option
    Audit: IAuditLog option
    Config: ScimConfig
}

// ─── Audit ───────────────────────────────────────────────────────────

/// Fire-and-forget audit emission, matching `PlatformApiHandler`'s own
/// shape: emission failures never cascade into the primary state
/// change. `Async.Start` rather than `do!` for the same reason it is
/// there — a slow audit sink must not add latency to a provisioning
/// round-trip an IdP will time out.
let private audit (deps: ScimDeps) (event: AuditEvent) =
    match deps.Audit with
    | Some log -> log.Record(deps.Config.TeamId, event) |> Async.Start
    | None -> ()

// ─── Projection: platform membership → SCIM User ─────────────────────

let private metaFor (deps: ScimDeps) (resourceType: string) (id: string) (lastModified: DateTime option) : ScimMeta = {
    ResourceType = resourceType
    Created = lastModified
    LastModified = lastModified
    Location =
        deps.Config.BaseUrl
        |> Option.map (fun b -> $"""{b.TrimEnd '/'}/scim/v2/{resourceType}s/{Uri.EscapeDataString id}""")
    Version = None
}

/// Project a platform membership row into the SCIM `User` an IdP reads
/// back. The platform stores no directory attributes of its own — it
/// holds a user id, a role and a join date — so `userName` echoes the
/// id and `active` is `true` by construction: a row that exists IS an
/// active membership, and deprovisioning removes the row rather than
/// flagging it. That asymmetry is deliberate and is the reason a
/// `GET` after a `PATCH active:false` returns 404 rather than an
/// inactive user; the README states it, because an IdP that expects the
/// inactive-tombstone shape will otherwise read the 404 as an error.
let toScimUser (deps: ScimDeps) (m: TeamMembership) : ScimUser = {
    Id = m.UserId
    ExternalId = None
    UserName = m.UserId
    Name = ScimName.empty
    DisplayName = Some m.UserId
    Emails =
        if m.UserId.Contains "@" then
            [
                {
                    Value = m.UserId
                    Type = Some "work"
                    Primary = true
                }
            ]
        else
            []
    Active = true
    Meta = Some(metaFor deps "User" m.UserId (Some m.JoinedAt))
}

let private toScimGroup (deps: ScimDeps) (team: TeamInfo) (members: TeamMembership list) : ScimGroup = {
    Id = team.TeamId
    ExternalId = None
    DisplayName = team.Name
    Members =
        members
        |> List.map (fun m -> {
            Value = m.UserId
            Display = Some m.UserId
            Type = Some "User"
        })
    Meta = Some(metaFor deps "Group" team.TeamId (Some team.CreatedAt))
}

// ─── Reads ───────────────────────────────────────────────────────────

let private members (deps: ScimDeps) =
    deps.Teams.GetTeamMembers deps.Config.TeamId

/// `GET /scim/v2/Users`. The filter is applied BEFORE pagination, which
/// is what RFC 7644 §3.4.2 requires and what makes the IdP's
/// `filter=userName eq "x"` probe correct at any page size.
let listUsers (deps: ScimDeps) (page: ScimPage) (filter: ScimFilter) : Async<Result<string, ScimError>> = async {
    match filter with
    | UnsupportedFilter expr -> return Error(ScimError.invalidFilter expr)
    | ExternalIdEquals _ ->
        // The platform stores no `externalId`, so this filter can only
        // ever answer "no match". Answering it as an empty list would
        // tell the IdP the user does not exist and provoke a duplicate
        // create; a 501 tells it to fall back to `userName`, which this
        // provider does answer.
        return
            Error(
                ScimError.invalidFilter
                    "externalId eq — this service provider does not persist externalId; filter on userName"
            )
    | _ ->
        let! all = members deps

        let matched =
            match filter with
            | UserNameEquals name ->
                all
                |> List.filter (fun m -> String.Equals(m.UserId, name, StringComparison.OrdinalIgnoreCase))
            | DisplayNameEquals name ->
                all
                |> List.filter (fun m -> String.Equals(m.UserId, name, StringComparison.OrdinalIgnoreCase))
            | _ -> all

        let ordered = matched |> List.sortBy _.UserId
        let pageItems = ordered |> ScimPage.apply page |> List.map (toScimUser deps)
        return Ok(ScimJson.encodeUserList page (List.length ordered) pageItems)
}

let getUser (deps: ScimDeps) (userId: string) : Async<Result<string, ScimError>> = async {
    let! role = deps.Teams.GetMemberRole(deps.Config.TeamId, userId)

    match role with
    | None -> return Error(ScimError.notFound $"No SCIM User resource with id '{userId}'")
    | Some _ ->
        let! all = members deps

        match all |> List.tryFind (fun m -> m.UserId = userId) with
        | Some m -> return Ok(ScimJson.encodeUser (toScimUser deps m))
        | None -> return Error(ScimError.notFound $"No SCIM User resource with id '{userId}'")
}

let listGroups (deps: ScimDeps) (page: ScimPage) (filter: ScimFilter) : Async<Result<string, ScimError>> = async {
    match filter with
    | UnsupportedFilter expr -> return Error(ScimError.invalidFilter expr)
    | ExternalIdEquals _ ->
        return
            Error(
                ScimError.invalidFilter
                    "externalId eq — this service provider does not persist externalId; filter on displayName"
            )
    | _ ->
        let! team = deps.Teams.GetTeam deps.Config.TeamId

        match team with
        | None -> return Ok(ScimJson.encodeGroupList page 0 [])
        | Some t ->
            let matches =
                match filter with
                | DisplayNameEquals name -> String.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                | UserNameEquals _ -> false
                | _ -> true

            if not matches then
                return Ok(ScimJson.encodeGroupList page 0 [])
            else
                let! all = members deps
                let group = toScimGroup deps t (all |> List.sortBy _.UserId)
                let shown = ScimPage.apply page [ group ]
                return Ok(ScimJson.encodeGroupList page 1 shown)
}

let getGroup (deps: ScimDeps) (groupId: string) : Async<Result<string, ScimError>> = async {
    // Scope isolation (GP 4): only the configured team is addressable,
    // and any other id is a plain 404 rather than a 403 — a 403 would
    // confirm the team exists.
    if groupId <> deps.Config.TeamId then
        return Error(ScimError.notFound $"No SCIM Group resource with id '{groupId}'")
    else
        let! team = deps.Teams.GetTeam groupId

        match team with
        | None -> return Error(ScimError.notFound $"No SCIM Group resource with id '{groupId}'")
        | Some t ->
            let! all = members deps
            return Ok(ScimJson.encodeGroup (toScimGroup deps t (all |> List.sortBy _.UserId)))
}

// ─── Writes ──────────────────────────────────────────────────────────

/// Add a member, emitting the same audit event a human admin's add
/// produces, stamped with the SCIM actor. Idempotent from the IdP's
/// point of view: a create for an existing member is answered `409
/// uniqueness`, which is the shape RFC 7644 §3.3 defines and which
/// Entra and Okta both handle by switching to an update.
let private addMember (deps: ScimDeps) (userId: string) (role: TeamRole) : Async<Result<unit, ScimError>> = async {
    let! existing = deps.Teams.GetMemberRole(deps.Config.TeamId, userId)

    match existing with
    | Some _ -> return Error(ScimError.uniqueness $"User '{userId}' is already provisioned in this group")
    | None ->
        let! result = deps.Teams.AddMember(deps.Config.TeamId, userId, role)

        match result with
        | Error e -> return Error(ScimError.invalidValue e)
        | Ok() ->
            audit
                deps
                (MemberAdded {
                    UserId = ScimActorId
                    TeamId = deps.Config.TeamId
                    AffectedUserId = userId
                    Role = TeamRoles.displayName role
                })

            return Ok()
}

/// Remove a member — the deprovision leg. `RemoveMember` refuses to
/// strip the last Owner, and that refusal is surfaced as a `400
/// invalidValue` naming the reason rather than being swallowed: an IdP
/// that silently "succeeded" at removing the last Owner would report a
/// user as deprovisioned while their access remained.
let private removeMember (deps: ScimDeps) (userId: string) : Async<Result<unit, ScimError>> = async {
    let! existing = deps.Teams.GetMemberRole(deps.Config.TeamId, userId)

    match existing with
    | None -> return Error(ScimError.notFound $"No SCIM User resource with id '{userId}'")
    | Some _ ->
        let! result = deps.Teams.RemoveMember(deps.Config.TeamId, userId)

        match result with
        | Error e -> return Error(ScimError.invalidValue e)
        | Ok() ->
            audit
                deps
                (MemberRemoved {
                    UserId = ScimActorId
                    TeamId = deps.Config.TeamId
                    AffectedUserId = userId
                })

            return Ok()
}

let private changeRole (deps: ScimDeps) (userId: string) (newRole: TeamRole) : Async<Result<unit, ScimError>> = async {
    let! existing = deps.Teams.GetMemberRole(deps.Config.TeamId, userId)

    match existing with
    | None -> return Error(ScimError.notFound $"No SCIM User resource with id '{userId}'")
    | Some oldRole when oldRole = newRole -> return Ok()
    | Some oldRole ->
        let! result = deps.Teams.ChangeMemberRole(deps.Config.TeamId, userId, newRole)

        match result with
        | Error e -> return Error(ScimError.invalidValue e)
        | Ok() ->
            audit
                deps
                (MemberRoleChanged {
                    UserId = ScimActorId
                    TeamId = deps.Config.TeamId
                    AffectedUserId = userId
                    OldRole = TeamRoles.displayName oldRole
                    NewRole = TeamRoles.displayName newRole
                })

            return Ok()
}

/// `POST /scim/v2/Users`. `active: false` on a create is honoured as
/// "do not provision" rather than "provision then deactivate" — the
/// end state is identical and the intermediate grant never exists.
let createUser (deps: ScimDeps) (body: string) : Async<Result<string, ScimError>> = async {
    match ScimJson.decodeUser body with
    | Error e -> return Error e
    | Ok scimUser ->
        match ScimAttributeMapping.userId deps.Config.Mapping scimUser with
        | Error message -> return Error(ScimError.invalidValue message)
        | Ok userId ->
            if not scimUser.Active then
                let! existing = deps.Teams.GetMemberRole(deps.Config.TeamId, userId)

                match existing with
                | Some _ ->
                    let! removed = removeMember deps userId

                    match removed with
                    | Error e -> return Error e
                    | Ok() ->
                        return
                            Ok(
                                ScimJson.encodeUser {
                                    scimUser with
                                        Id = userId
                                        Active = false
                                }
                            )
                | None ->
                    return
                        Ok(
                            ScimJson.encodeUser {
                                scimUser with
                                    Id = userId
                                    Active = false
                            }
                        )
            else
                let role = deps.Config.Mapping.Roles.Default
                let! added = addMember deps userId role

                match added with
                | Error e -> return Error e
                | Ok() ->
                    let! all = members deps

                    let projected =
                        all
                        |> List.tryFind (fun m -> m.UserId = userId)
                        |> Option.map (toScimUser deps)
                        |> Option.defaultValue { scimUser with Id = userId }

                    return Ok(ScimJson.encodeUser projected)
}

/// `PUT /scim/v2/Users/{id}`. The one attribute a replace can move is
/// `active`; everything else in the resource is directory data the
/// platform does not own, so a replace that changes only a display name
/// is accepted and has no effect (RFC 7644 §3.5.1 permits a service
/// provider to ignore attributes it does not support).
let replaceUser (deps: ScimDeps) (userId: string) (body: string) : Async<Result<string, ScimError>> = async {
    match ScimJson.decodeUser body with
    | Error e -> return Error e
    | Ok scimUser ->
        let! existing = deps.Teams.GetMemberRole(deps.Config.TeamId, userId)

        match existing with
        | None -> return Error(ScimError.notFound $"No SCIM User resource with id '{userId}'")
        | Some _ ->
            if scimUser.Active then
                return! getUser deps userId
            else
                let! removed = removeMember deps userId

                match removed with
                | Error e -> return Error e
                | Ok() ->
                    return
                        Ok(
                            ScimJson.encodeUser {
                                scimUser with
                                    Id = userId
                                    Active = false
                            }
                        )
}

/// `DELETE /scim/v2/Users/{id}` — the explicit deprovision. Okta
/// deactivates via PATCH; Entra can be configured for either.
let deleteUser (deps: ScimDeps) (userId: string) : Async<Result<unit, ScimError>> = removeMember deps userId

/// Read the `active` intent out of one PATCH operation, if it carries
/// one. Handles the three shapes seen in the field: `path: "active"`
/// with a boolean value, a path-less replace whose object holds
/// `active`, and Okta's stringly `"active": "false"` (normalised in the
/// decoder).
let private activeIntent (op: ScimPatchOperation) : bool option =
    let fromAttributes (pairs: (string * ScimPatchScalar) list) =
        pairs
        |> List.tryPick (fun (name, v) ->
            if String.Equals(name, "active", StringComparison.OrdinalIgnoreCase) then
                match v with
                | ScalarBool b -> Some b
                | _ -> None
            else
                None)

    match op.Path |> Option.map ScimPatchPath.parse, op.Value with
    | Some ActivePath, PatchBool b -> Some b
    | Some ActivePath, PatchAttributes pairs -> fromAttributes pairs
    | None, PatchAttributes pairs -> fromAttributes pairs
    | _ -> None

/// `PATCH /scim/v2/Users/{id}`. Deactivation removes the membership
/// within this one request — no sweep, no deferred job — which is what
/// makes "access is gone within one round-trip" a property of the
/// endpoint rather than of an operator's cron.
let patchUser (deps: ScimDeps) (userId: string) (body: string) : Async<Result<string option, ScimError>> = async {
    match ScimJson.decodePatch body with
    | Error e -> return Error e
    | Ok patch ->
        let! existing = deps.Teams.GetMemberRole(deps.Config.TeamId, userId)

        match existing with
        | None -> return Error(ScimError.notFound $"No SCIM User resource with id '{userId}'")
        | Some _ ->
            // Last write wins across the operation list, matching
            // RFC 7644 §3.5.2's sequential-application semantics for
            // repeated writes to one attribute.
            let intent = patch.Operations |> List.choose activeIntent |> List.tryLast

            match intent with
            | Some false ->
                let! removed = removeMember deps userId

                match removed with
                | Error e -> return Error e
                | Ok() -> return Ok None
            | _ ->
                let! projected = getUser deps userId

                match projected with
                | Error e -> return Error e
                | Ok json -> return Ok(Some json)
}

/// `PATCH /scim/v2/Groups/{id}` — membership add / remove and the role
/// change that rides a group assignment.
///
/// The role a member takes is decided by the GROUP's `displayName`
/// through `ScimRoleMapping`, not by anything in the member entry: SCIM
/// has no role attribute, and "which group you are in" is the only
/// role signal an IdP actually sends. A deployment that maps no group
/// names gets `Member` for everyone, which is the least-privilege
/// default (GP 4).
let patchGroup (deps: ScimDeps) (groupId: string) (body: string) : Async<Result<string option, ScimError>> = async {
    if groupId <> deps.Config.TeamId then
        return Error(ScimError.notFound $"No SCIM Group resource with id '{groupId}'")
    else
        match ScimJson.decodePatch body with
        | Error e -> return Error e
        | Ok patch ->
            let! team = deps.Teams.GetTeam groupId

            match team with
            | None -> return Error(ScimError.notFound $"No SCIM Group resource with id '{groupId}'")
            | Some t ->
                let role = ScimRoleMapping.resolve t.Name deps.Config.Mapping.Roles

                // Apply operations in order and stop at the first
                // failure — RFC 7644 §3.5.2 makes a PATCH atomic in
                // intent, and a half-applied membership change is worse
                // than a refused one because the IdP will not retry a
                // 200.
                let rec apply (ops: ScimPatchOperation list) : Async<Result<unit, ScimError>> = async {
                    match ops with
                    | [] -> return Ok()
                    | op :: rest ->
                        let! outcome = async {
                            match op.Op, op.Path |> Option.map ScimPatchPath.parse, op.Value with
                            // `remove` with the target in the path —
                            // both Entra and Okta deprovision this way.
                            | PatchRemove, Some(MemberValuePath target), _ ->
                                let! existing = deps.Teams.GetMemberRole(groupId, target)

                                match existing with
                                | None -> return Ok() // already absent — idempotent
                                | Some _ -> return! removeMember deps target
                            | PatchRemove, Some MembersPath, PatchMembers targets ->
                                let rec removeAll (xs: ScimGroupMember list) = async {
                                    match xs with
                                    | [] -> return Ok()
                                    | m :: tail ->
                                        let! present = deps.Teams.GetMemberRole(groupId, m.Value)

                                        match present with
                                        | None -> return! removeAll tail
                                        | Some _ ->
                                            let! r = removeMember deps m.Value

                                            match r with
                                            | Error e -> return Error e
                                            | Ok() -> return! removeAll tail
                                }

                                return! removeAll targets
                            | (PatchAdd | PatchReplace), Some MembersPath, PatchMembers targets ->
                                let rec addAll (xs: ScimGroupMember list) = async {
                                    match xs with
                                    | [] -> return Ok()
                                    | m :: tail ->
                                        if m.Type = Some "Group" then
                                            return
                                                Error(
                                                    ScimError.invalidValue
                                                        $"Nested groups are not supported; member '{m.Value}' is of type Group"
                                                )
                                        else
                                            let! present = deps.Teams.GetMemberRole(groupId, m.Value)

                                            match present with
                                            | Some current when current = role -> return! addAll tail
                                            | Some _ ->
                                                // Already a member at a
                                                // different role: the
                                                // group re-assignment IS
                                                // the role change.
                                                let! r = changeRole deps m.Value role

                                                match r with
                                                | Error e -> return Error e
                                                | Ok() -> return! addAll tail
                                            | None ->
                                                let! r = addMember deps m.Value role

                                                match r with
                                                | Error e -> return Error e
                                                | Ok() -> return! addAll tail
                                }

                                return! addAll targets
                            // Anything else — a displayName rewrite, a
                            // decorated attribute — is accepted and
                            // ignored. See `ScimPatchPath`.
                            | _ -> return Ok()
                        }

                        match outcome with
                        | Error e -> return Error e
                        | Ok() -> return! apply rest
                }

                let! result = apply patch.Operations

                match result with
                | Error e -> return Error e
                | Ok() ->
                    let! projected = getGroup deps groupId

                    match projected with
                    | Error e -> return Error e
                    | Ok json -> return Ok(Some json)
}

/// `PUT /scim/v2/Groups/{id}` — a full membership replace. Computes the
/// add / remove delta against the current roster rather than
/// tearing down and rebuilding it, so a replace that changes one member
/// emits one audit event, not N.
let replaceGroup (deps: ScimDeps) (groupId: string) (body: string) : Async<Result<string, ScimError>> = async {
    if groupId <> deps.Config.TeamId then
        return Error(ScimError.notFound $"No SCIM Group resource with id '{groupId}'")
    else
        match ScimJson.decodeGroup body with
        | Error e -> return Error e
        | Ok group ->
            let! team = deps.Teams.GetTeam groupId

            match team with
            | None -> return Error(ScimError.notFound $"No SCIM Group resource with id '{groupId}'")
            | Some t ->
                let role = ScimRoleMapping.resolve t.Name deps.Config.Mapping.Roles
                let! current = members deps
                let desired = group.Members |> List.map _.Value |> Set.ofList
                let held = current |> List.map _.UserId |> Set.ofList

                let toAdd = Set.difference desired held |> Set.toList |> List.sort
                let toRemove = Set.difference held desired |> Set.toList |> List.sort

                let rec removeAll (xs: string list) = async {
                    match xs with
                    | [] -> return Ok()
                    | u :: tail ->
                        let! r = removeMember deps u

                        match r with
                        | Error e -> return Error e
                        | Ok() -> return! removeAll tail
                }

                let rec addAll (xs: string list) = async {
                    match xs with
                    | [] -> return Ok()
                    | u :: tail ->
                        let! r = addMember deps u role

                        match r with
                        | Error e -> return Error e
                        | Ok() -> return! addAll tail
                }

                let! added = addAll toAdd

                match added with
                | Error e -> return Error e
                | Ok() ->
                    let! removed = removeAll toRemove

                    match removed with
                    | Error e -> return Error e
                    | Ok() -> return! getGroup deps groupId
}