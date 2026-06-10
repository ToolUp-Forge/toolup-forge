// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.Support.InMemoryStores

open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Secrets

/// Trivial in-memory `ISecretStore`. Mirrors the inline doubles used
/// across the other test packs — kept local so each pack owns its
/// isolated state.
type InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

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
        member _.Record(scopeId, audit) = async { events.Add(scopeId, audit) }

        member this.GetAuditTrail(scopeId, _, eventTypeFilter) = async {
            return
                this.EventsForScope scopeId
                |> List.filter (fun e ->
                    match eventTypeFilter with
                    | None -> true
                    | Some t -> AuditEvent.eventTypeName e = t)
        }