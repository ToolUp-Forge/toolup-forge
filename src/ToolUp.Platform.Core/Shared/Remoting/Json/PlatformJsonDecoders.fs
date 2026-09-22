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
/// this wire (69k.B), and chosen so that four platform records take
/// every argument through the algebra: `IPresenceApi`, `IAuditViewApi`,
/// `IProvenanceQueryApi` and `ITeamInviteApi`. Deliberately NOT
/// registered: `string`, `Guid` and the primitive tuples, which are
/// shared with every consumer's own API records — registering a decoder
/// for `string` here would change how a consumer's string arguments are
/// read on upgrade (a `null` string would refuse by name rather than
/// arrive as `null`), and a platform-set registration must stay a
/// statement about the platform's records (GP 11).
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

    /// `CreateTeamRequest` — the `CreateTeamWithOwner` argument.
    let createTeamRequest: JsonDecoder<CreateTeamRequest> =
        JsonDecode.succeed (fun name owner ->
            ({
                Name = name
                InitialOwnerUserId = owner
            }
            : CreateTeamRequest))
        |> JsonDecode.apply (JsonDecode.field "Name" JsonDecode.asString)
        |> JsonDecode.apply (JsonDecode.field "InitialOwnerUserId" JsonDecode.asString)

    /// The argument types this module covers, in registration order.
    let covered: string list = [
        typeof<TeamRole>.FullName
        typeof<PresenceLocation>.FullName
        typeof<EntityLockRef>.FullName
        typeof<AuditTrailQuery>.FullName
        typeof<WireProvenanceRef>.FullName
        typeof<WireProvenanceDirection>.FullName
        typeof<WireProvenanceChainRequest>.FullName
        typeof<TeamInviteIssueRequest>.FullName
        typeof<PinRequest>.FullName
        typeof<CreateTeamRequest>.FullName
    ]

    /// Register every decoder above. Idempotent and explicit — called
    /// by `ServerApp.run` beside `PlatformDecoders.registerAll`.
    let registerAll () : unit =
        JsonDecoders.register<TeamRole> teamRole
        JsonDecoders.register<PresenceLocation> presenceLocation
        JsonDecoders.register<EntityLockRef> entityLockRef
        JsonDecoders.register<AuditTrailQuery> auditTrailQuery
        JsonDecoders.register<WireProvenanceRef> wireProvenanceRef
        JsonDecoders.register<WireProvenanceDirection> wireProvenanceDirection
        JsonDecoders.register<WireProvenanceChainRequest> wireProvenanceChainRequest
        JsonDecoders.register<TeamInviteIssueRequest> teamInviteIssueRequest
        JsonDecoders.register<PinRequest> pinRequest
        JsonDecoders.register<CreateTeamRequest> createTeamRequest