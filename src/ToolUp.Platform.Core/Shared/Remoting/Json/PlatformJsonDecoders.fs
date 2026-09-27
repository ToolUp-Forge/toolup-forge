// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open ToolUp.Platform

/// Phase 799 — JSON algebra decoders for the platform's own API-record
/// ARGUMENT types: the scoped first set the server's argument seam
/// consults before System.Text.Json.
///
/// Hand-written, in the shape the generator will emit once it learns
/// this wire (69k.B), and chosen so that six platform records take
/// their own arguments through the algebra: `IPresenceApi`,
/// `IAuditViewApi`, `IProvenanceQueryApi`, `ITeamInviteApi`,
/// `IHomeOverviewApi` and `TeamApi`.
///
/// Phase 839 — every registration below is now scoped to its owning
/// record (`JsonDecoders.registerFor`), not global: a decoder
/// registered for `IPresenceApi`'s arguments is never consulted for
/// another record's arguments of the same wire type, so this module's
/// registrations are a statement about the platform's OWN records
/// again, and stop being a statement about every consumer's types by
/// construction rather than by convention alone. Deliberately still NOT
/// registered for ANY record: `string`, `Guid` and the primitive
/// tuples — see `PlatformPrimitiveJsonDecoders` below.
///
/// Every decoder here is held beside the STJ converter set over drawn
/// values by `JsonDecoderAlgebraTests`, the same way `PlatformDecoders`
/// is held beside the reflection reader.
[<RequireQualifiedAccess>]
module PlatformJsonDecoders =

    /// `TeamRole` — a field-less union, written as its case name.
    let teamRole: JsonDecoder<TeamRole> =
        JsonDecode.union "TeamRole" (function
            | "Owner" -> Some(JsonDecode.case0 TeamRole.Owner)
            | "Admin" -> Some(JsonDecode.case0 TeamRole.Admin)
            | "Member" -> Some(JsonDecode.case0 TeamRole.Member)
            | _ -> None)

    /// `PresenceLocation` — the `IPresenceApi.Heartbeat` argument.
    let presenceLocation: JsonDecoder<PresenceLocation> =
        JsonDecode.succeed (fun module' page -> ({ Module = module'; Page = page }: PresenceLocation))
        |> JsonDecode.apply (JsonDecode.field "Module" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.optionalField "Page" JsonDecode.asString)

    /// `EntityLockRef` — the lock methods' argument on `IPresenceApi`.
    let entityLockRef: JsonDecoder<EntityLockRef> =
        JsonDecode.succeed (fun entityType entityId ->
            ({
                EntityType = entityType
                EntityId = entityId
            }
            : EntityLockRef))
        |> JsonDecode.apply (JsonDecode.field "EntityType" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.field "EntityId" JsonDecode.asString)

    /// `AuditTrailQuery` — the `IAuditViewApi.Query` / `ExportCsv` argument; five optional members and a page size.
    let auditTrailQuery: JsonDecoder<AuditTrailQuery> =
        JsonDecode.succeed (fun from to' eventType actor cursor pageSize ->
            ({
                From = from
                To = to'
                EventType = eventType
                Actor = actor
                Cursor = cursor
                PageSize = pageSize
            }
            : AuditTrailQuery))
        |> JsonDecode.apply (JsonDecode.optionalField "From" JsonDecode.asDateTime)
        |> JsonDecode.apply (JsonDecode.optionalField "To" JsonDecode.asDateTime)
        |> JsonDecode.apply (JsonDecode.optionalField "EventType" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.optionalField "Actor" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.optionalField "Cursor" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.field "PageSize" JsonDecode.asInt32)

    /// `WireProvenanceRef` — five one-field cases.
    let wireProvenanceRef: JsonDecoder<WireProvenanceRef> =
        JsonDecode.union "WireProvenanceRef" (function
            | "DataObjectRef" ->
                Some(JsonDecode.payload (JsonDecode.asString |> JsonDecode.map WireProvenanceRef.DataObjectRef))
            | "ResultRef" ->
                Some(JsonDecode.payload (JsonDecode.asString |> JsonDecode.map WireProvenanceRef.ResultRef))
            | "FactRef" -> Some(JsonDecode.payload (JsonDecode.asString |> JsonDecode.map WireProvenanceRef.FactRef))
            | "MessageRef" ->
                Some(JsonDecode.payload (JsonDecode.asString |> JsonDecode.map WireProvenanceRef.MessageRef))
            | "ModelArtifactRef" ->
                Some(JsonDecode.payload (JsonDecode.asString |> JsonDecode.map WireProvenanceRef.ModelArtifactRef))
            | _ -> None)

    /// `WireProvenanceDirection` — a field-less union.
    let wireProvenanceDirection: JsonDecoder<WireProvenanceDirection> =
        JsonDecode.union "WireProvenanceDirection" (function
            | "Upstream" -> Some(JsonDecode.case0 WireProvenanceDirection.Upstream)
            | "Downstream" -> Some(JsonDecode.case0 WireProvenanceDirection.Downstream)
            | _ -> None)

    /// `WireProvenanceChainRequest` — the `IProvenanceQueryApi.GetChain` argument.
    let wireProvenanceChainRequest: JsonDecoder<WireProvenanceChainRequest> =
        JsonDecode.succeed (fun root direction depth ->
            ({
                Root = root
                Direction = direction
                Depth = depth
            }
            : WireProvenanceChainRequest))
        |> JsonDecode.apply (JsonDecode.field "Root" wireProvenanceRef)
        |> JsonDecode.apply (JsonDecode.field "Direction" wireProvenanceDirection)
        |> JsonDecode.apply (JsonDecode.field "Depth" JsonDecode.asInt32)

    /// `TeamInviteIssueRequest` — the `ITeamInviteApi.IssueInvite` argument; the `TimeSpan` rides `asTimeSpan`, exactly.
    let teamInviteIssueRequest: JsonDecoder<TeamInviteIssueRequest> =
        JsonDecode.succeed (fun teamId role expiresIn emailHint maxUses ->
            ({
                TeamId = teamId
                Role = role
                ExpiresIn = expiresIn
                EmailHint = emailHint
                MaxUses = maxUses
            }
            : TeamInviteIssueRequest))
        |> JsonDecode.apply (JsonDecode.field "TeamId" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.field "Role" teamRole)
        |> JsonDecode.apply (JsonDecode.optionalField "ExpiresIn" JsonDecode.asTimeSpan)
        |> JsonDecode.apply (JsonDecode.optionalField "EmailHint" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.optionalField "MaxUses" JsonDecode.asInt32)

    /// `PinRequest` — the `IHomeOverviewApi.SetPinned` argument.
    let pinRequest: JsonDecoder<PinRequest> =
        JsonDecode.succeed (fun moduleId pinned -> ({ ModuleId = moduleId; Pinned = pinned }: PinRequest))
        |> JsonDecode.apply (JsonDecode.field "ModuleId" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.field "Pinned" JsonDecode.asBool)

    /// `CreateTeamRequest` — the `TeamApi.CreateTeamWithOwner` argument.
    let createTeamRequest: JsonDecoder<CreateTeamRequest> =
        JsonDecode.succeed (fun name owner ->
            ({
                Name = name
                InitialOwnerUserId = owner
            }
            : CreateTeamRequest))
        |> JsonDecode.apply (JsonDecode.field "Name" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.field "InitialOwnerUserId" JsonDecode.asString)

    /// `PendingInviteIssueRequest` — the `ITeamInviteApi.IssuePendingInviteByEmail`
    /// argument; the same shape as `teamInviteIssueRequest` minus the
    /// link-flow-only `EmailHint` / `MaxUses` members.
    let pendingInviteIssueRequest: JsonDecoder<PendingInviteIssueRequest> =
        JsonDecode.succeed (fun teamId email role expiresIn ->
            ({
                TeamId = teamId
                Email = email
                Role = role
                ExpiresIn = expiresIn
            }
            : PendingInviteIssueRequest))
        |> JsonDecode.apply (JsonDecode.field "TeamId" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.field "Email" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.field "Role" teamRole)
        |> JsonDecode.apply (JsonDecode.optionalField "ExpiresIn" JsonDecode.asTimeSpan)

    /// Phase 839 — `ITeamInviteApi`'s many string-only arguments
    /// (`AcceptInvite`, `RevokeInvite`, `ListPendingInvites`,
    /// `ListPendingInvitesByEmail`, `RevokePendingInviteByEmail`,
    /// `ListRecentlyExpiredInvites`) share this one decoder, registered
    /// scoped to `ITeamInviteApi` alone — see `registerAll`.
    let teamInviteApiString: JsonDecoder<string> = JsonDecode.asString

    /// Phase 839 — `IHomeOverviewApi.RecordVisit`'s bare `string` argument,
    /// registered scoped to `IHomeOverviewApi` alone.
    let homeOverviewApiString: JsonDecoder<string> = JsonDecode.asString

    /// Phase 839 — `TeamApi`'s bare `string` arguments (`CreateTeam`,
    /// `GetTeamMembers`, `SetActiveTeam`, `ArchiveTeam`, `RestoreTeam`,
    /// `DeleteTeamHard`), registered scoped to `TeamApi` alone.
    let teamApiString: JsonDecoder<string> = JsonDecode.asString

    /// Phase 839 — `TeamApi.RemoveTeamMember` / `TransferOwnership`'s
    /// `(string * string)` tuple argument.
    let teamApiStringPair: JsonDecoder<string * string> =
        JsonDecode.tuple2 JsonDecode.asString JsonDecode.asString

    /// Phase 839 — `TeamApi.AddTeamMember` / `ChangeMemberRole`'s
    /// `(string * string * TeamRole)` tuple argument.
    let teamApiStringStringRole: JsonDecoder<string * string * TeamRole> =
        JsonDecode.tuple3 JsonDecode.asString JsonDecode.asString teamRole

    /// The `(owning record, argument type)` pairs this module covers, in
    /// registration order. Phase 839 — every entry names the ONE record
    /// its decoder is scoped to (`None` would mean unscoped, and nothing
    /// here is); `PinRequest` and `CreateTeamRequest` are no longer a
    /// blanket statement about every consumer's own `IHomeOverviewApi`-
    /// or `TeamApi`-shaped record, because they are not registered for
    /// one.
    let covered: (string option * string) list = [
        Some "ITeamInviteApi", typeof<TeamRole>.FullName
        Some "IPresenceApi", typeof<PresenceLocation>.FullName
        Some "IPresenceApi", typeof<EntityLockRef>.FullName
        Some "IAuditViewApi", typeof<AuditTrailQuery>.FullName
        Some "IProvenanceQueryApi", typeof<WireProvenanceRef>.FullName
        Some "IProvenanceQueryApi", typeof<WireProvenanceDirection>.FullName
        Some "IProvenanceQueryApi", typeof<WireProvenanceChainRequest>.FullName
        Some "ITeamInviteApi", typeof<TeamInviteIssueRequest>.FullName
        Some "IHomeOverviewApi", typeof<PinRequest>.FullName
        Some "TeamApi", typeof<CreateTeamRequest>.FullName
        Some "ITeamInviteApi", typeof<PendingInviteIssueRequest>.FullName
        Some "ITeamInviteApi", typeof<string>.FullName
        Some "IHomeOverviewApi", typeof<string>.FullName
        Some "TeamApi", typeof<string>.FullName
        Some "TeamApi", typeof<string * string>.FullName
        Some "TeamApi", typeof<string * string * TeamRole>.FullName
    ]

    /// Register every decoder above, each scoped to the one API record it
    /// covers (`JsonDecoders.registerFor`) — Phase 839 widened every
    /// registration this module makes from unscoped to record-scoped, so
    /// the platform's own registrations are a statement about the
    /// platform's own records again, not about every consumer's types of
    /// the same shape. Idempotent and explicit — called by `ServerApp.run`
    /// beside `PlatformDecoders.registerAll`.
    let registerAll () : unit =
        JsonDecoders.registerFor<TeamRole> "ITeamInviteApi" teamRole
        JsonDecoders.registerFor<PresenceLocation> "IPresenceApi" presenceLocation
        JsonDecoders.registerFor<EntityLockRef> "IPresenceApi" entityLockRef
        JsonDecoders.registerFor<AuditTrailQuery> "IAuditViewApi" auditTrailQuery
        JsonDecoders.registerFor<WireProvenanceRef> "IProvenanceQueryApi" wireProvenanceRef
        JsonDecoders.registerFor<WireProvenanceDirection> "IProvenanceQueryApi" wireProvenanceDirection
        JsonDecoders.registerFor<WireProvenanceChainRequest> "IProvenanceQueryApi" wireProvenanceChainRequest
        JsonDecoders.registerFor<TeamInviteIssueRequest> "ITeamInviteApi" teamInviteIssueRequest
        JsonDecoders.registerFor<PinRequest> "IHomeOverviewApi" pinRequest
        JsonDecoders.registerFor<CreateTeamRequest> "TeamApi" createTeamRequest
        JsonDecoders.registerFor<PendingInviteIssueRequest> "ITeamInviteApi" pendingInviteIssueRequest
        JsonDecoders.registerFor<string> "ITeamInviteApi" teamInviteApiString
        JsonDecoders.registerFor<string> "IHomeOverviewApi" homeOverviewApiString
        JsonDecoders.registerFor<string> "TeamApi" teamApiString
        JsonDecoders.registerFor<string * string> "TeamApi" teamApiStringPair
        JsonDecoders.registerFor<string * string * TeamRole> "TeamApi" teamApiStringStringRole