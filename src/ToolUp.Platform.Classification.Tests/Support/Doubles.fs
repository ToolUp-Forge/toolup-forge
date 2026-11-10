// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Classification.Tests.Support.Doubles

open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Audit

/// In-memory `IAuditLog` recording every event for assertion.
type InMemoryAuditLog() =
    let events = ConcurrentBag<string * AuditEvent>()

    member _.AllEvents: (string * AuditEvent) list = events |> List.ofSeq

    interface IAuditLog with
        member _.Record(AuditScope.StreamKey scopeId, _actor, audit) = async {
            events.Add(scopeId, audit)
            return Ok()
        }

        member this.GetAuditTrail(AuditScope.StreamKey scopeId, _, eventTypeFilter) = async {
            return
                this.AllEvents
                |> List.choose (fun (s, e) -> if s = scopeId then Some e else None)
                |> List.filter (fun e ->
                    match eventTypeFilter with
                    | None -> true
                    | Some t -> AuditEvent.eventTypeName e = t)
        }

/// The sample classification registry the contract + gate tests share.
/// A `Customer` entity with one field per classification axis.
module SampleRegistry =
    let customer: FieldClassification list = [
        FieldClassification.create "Customer" "Name" Confidential
        FieldClassification.create "Customer" "Email" Pii
        |> FieldClassification.withAuditOnRead
        |> FieldClassification.withEncryptionKey "pii-kek"
        FieldClassification.create "Customer" "RevenueUsd" Financial
        |> FieldClassification.withEncryptionKey "financial-kek"
        FieldClassification.create "Customer" "HealthNotes" Spi
        |> FieldClassification.withAuditOnRead
        FieldClassification.create "Customer" "DisplayHandle" Public
    ]

/// Test access-context constructors.
module Ctx =
    let private subject userId = AuthenticatedUser userId

    /// Unrestricted authenticated user — no reader capabilities, not admin.
    let plainUser (userId: string) : AccessContext =
        AccessContexts.forSubject (subject userId)

    /// Authenticated user holding `Read` on a per-level reader pseudo-module.
    let readerOf (level: ClassificationLevel) (userId: string) : AccessContext = {
        AccessContexts.forSubject (subject userId) with
            ModulePermissions =
                ModulePermissions.Restricted(
                    Map.ofList [ ClassificationGate.readerModule level, [ ModulePermission.Read ] ]
                )
    }

    /// Platform admin.
    let admin (userId: string) : AccessContext = {
        AccessContexts.forSubject (subject userId) with
            PlatformRole = Some PlatformRole.PlatformAdmin
    }