// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IHostCapabilityRegistryContract

open Expecto
open ToolUp.Platform

// Phase 266 / 773 — contract for the host-capability registry seam. Any
// `IHostCapabilityRegistry` — the shipped authorizer-gated registry, the
// same registry while a declared route's boundary is in force, or a
// consumer's own — must satisfy the laws a hosted action relies on:
//   * DEFAULT-DENY — an empty registry, or an id nothing registered, is a
//     `Denied` with a reason, never an exception and never a pass;
//   * the AUTHORIZER decides — a deny from it is a `Denied`, and the
//     handler never runs;
//   * an allowed, registered invoke runs the handler once and returns its
//     result bag as `Completed`;
//   * the LAST registration for an id wins.

let private ctx: AccessContext = {
    UserId = "alice"
    TeamId = Some "team-1"
    Subject = TeamMember("alice", "team-1")
    ModulePermissions = Map.empty
    TokenRoles = []
    Admission = ToolUp.Platform.Auth.PrincipalAdmission.Member
    ModuleExposure = Map.empty
    PlatformRole = None
}

let private probe = CapabilityId.ofString "contract.probe"

let private invoke (registry: IHostCapabilityRegistry) (capability: CapabilityId) =
    registry.Invoke capability (Map [ "k", "v" ]) ctx |> Async.RunSynchronously

/// The pack. `name` labels the implementation under test; `create` builds
/// a fresh registry over the authorizer it is handed. Bind it once per
/// implementation.
let tests (name: string) (create: IActionAuthorizer -> IHostCapabilityRegistry) =
    testList (sprintf "IHostCapabilityRegistry contract — %s" name) [
        test "an empty registry denies every id with a reason" {
            match invoke (create ActionAuthorizer.allowAll) probe with
            | HostCapabilityOutcome.Denied reason -> Expect.isNotEmpty reason "a deny has a reason"
            | other -> failtestf "an empty registry must deny, got %A" other
        }

        test "an id nothing registered is denied even when the authorizer allows" {
            let registry = create ActionAuthorizer.allowAll
            registry.Register probe (fun _ _ -> async { return Map.empty })

            match invoke registry (CapabilityId.ofString "contract.unregistered") with
            | HostCapabilityOutcome.Denied reason -> Expect.isNotEmpty reason "a deny has a reason"
            | other -> failtestf "an unregistered id must deny, got %A" other
        }

        test "an authorizer deny is a Denied and the handler never runs" {
            let ran = ref 0
            let registry = create ActionAuthorizer.denyAll

            registry.Register probe (fun _ _ -> async {
                ran.Value <- ran.Value + 1
                return Map.empty
            })

            match invoke registry probe with
            | HostCapabilityOutcome.Denied _ -> Expect.equal ran.Value 0 "the handler must not run"
            | other -> failtestf "an authorizer deny must be a Denied, got %A" other
        }

        test "an allowed, registered invoke runs the handler once and returns its result" {
            let ran = ref 0
            let registry = create ActionAuthorizer.allowAll

            registry.Register probe (fun args _ -> async {
                ran.Value <- ran.Value + 1
                return Map [ "echo", Map.find "k" args ]
            })

            match invoke registry probe with
            | HostCapabilityOutcome.Completed result ->
                Expect.equal result (Map [ "echo", "v" ]) "the handler's result bag comes back"
                Expect.equal ran.Value 1 "the handler ran exactly once"
            | other -> failtestf "an allowed, registered invoke must complete, got %A" other
        }

        test "the last registration for an id wins" {
            let registry = create ActionAuthorizer.allowAll
            registry.Register probe (fun _ _ -> async { return Map [ "which", "first" ] })
            registry.Register probe (fun _ _ -> async { return Map [ "which", "second" ] })

            match invoke registry probe with
            | HostCapabilityOutcome.Completed result ->
                Expect.equal (Map.tryFind "which" result) (Some "second") "the later registration answers"
            | other -> failtestf "expected a completed invoke, got %A" other
        }
    ]