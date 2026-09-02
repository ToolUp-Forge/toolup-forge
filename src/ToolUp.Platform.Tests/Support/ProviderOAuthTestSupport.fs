module ToolUp.Platform.Tests.Support.ProviderOAuthTestSupport

open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Providers

// ─── Phase 43.B/43.C — fixtures for the provider-OAuth substrate ───
//
// Kept out of `Contracts/IProviderOAuthFlowContract.fs` on purpose:
// that pack is copy-adoptable by an external implementer, so it
// carries its own fakes rather than importing them. These are for
// forge's own tests.

/// In-memory `IProviderProfile`, one profile per scope container.
let memoryProviderProfile () : IProviderProfile =
    let profiles = ConcurrentDictionary<string, ProviderProfile>()

    { new IProviderProfile with
        member _.Get scope = async {
            match profiles.TryGetValue scope.Container with
            | true, p -> return Some p
            | _ -> return None
        }

        member _.Set(scope, profile) = async {
            profiles[scope.Container] <- profile
            return Ok()
        }

        member _.Clear scope = async { profiles.TryRemove scope.Container |> ignore }

        member _.ResolveEntry(scope, surface, context) = async {
            match profiles.TryGetValue scope.Container with
            | true, p -> return ProviderProfile.resolveEntry surface context p
            | _ -> return None
        }

        member _.SetEntryHealth(scope, label, health) = async {
            match profiles.TryGetValue scope.Container with
            | true, p ->
                profiles[scope.Container] <- {
                    p with
                        Entries =
                            p.Entries
                            |> List.map (fun e -> if e.Label = label then { e with Health = health } else e)
                }

                return Ok()
            | _ -> return Ok()
        }
    }

/// `IAuditLog` that keeps what it was handed, so a test can assert the
/// audit half of an outcome rather than only its state half.
type RecordingAuditLog() =
    let recorded = ResizeArray<AuditEvent>()

    member _.Recorded = recorded |> List.ofSeq

    interface IAuditLog with
        member _.Record(_scopeId, audit) = async { lock recorded (fun () -> recorded.Add audit) }

        member _.GetAuditTrail(_scopeId, _dateRange, _eventType) = async { return recorded |> List.ofSeq }