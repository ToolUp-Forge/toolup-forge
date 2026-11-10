// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.Support.InMemoryStores

open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Secrets

/// Read-only `ISecretStore` that rejects every write — models an
/// env-var-only / KMS-fronted store where the signer cannot auto-
/// provision a key. Used to assert the `KeyUnavailable` failure path.
type ReadOnlySecretStore() =
    interface ISecretStore with
        member _.GetSecret(_, _) = async { return None }
        member _.SetSecret(_, _, _) = async { return Error "read-only" }
        member _.DeleteSecret(_, _) = async { return Error "read-only" }
        member _.ListKeys(_) = async { return [] }

/// In-memory `IAuditLog` recording every event for assertion.
type InMemoryAuditLog() =
    let events = ConcurrentBag<string * AuditEvent>()

    member _.AllEvents: (string * AuditEvent) list = events |> List.ofSeq

    member this.EventsForScope(scopeId: string) : AuditEvent list =
        this.AllEvents
        |> List.choose (fun (s, e) -> if s = scopeId then Some e else None)

    interface IAuditLog with
        member _.Record(AuditScope.StreamKey scopeId, _actor, audit) = async {
            events.Add(scopeId, audit)
            return Ok()
        }

        member this.GetAuditTrail(AuditScope.StreamKey scopeId, _, eventTypeFilter) = async {
            return
                this.EventsForScope scopeId
                |> List.filter (fun e ->
                    match eventTypeFilter with
                    | None -> true
                    | Some t -> AuditEvent.eventTypeName e = t)
        }