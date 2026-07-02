module ToolUp.Platform.Tests.InProcess.HostCapabilityRegistryTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.Tests.Contracts

// ─── Phase 266 — host-capability registry tests ──────────────────────
//
// The registry gates EVERY `Invoke` through the Phase 113 action
// authorizer (default-deny). This pack pins the invariants the phase
// promises:
//   * a registered capability runs ONLY when the authorizer grants it;
//   * an unregistered id denies (even under an allow-all authorizer);
//   * a cross-scope invoke denies structurally (GP 4);
//   * an empty registry (nothing registered / no authorizer rule) is
//     deny-all (GP 13);
//   * the forge-public built-ins (clipboard-write / file-read) reach
//     their injected side-effect through the gate.

let private teamCtx (userId: string) (teamId: string) : AccessContext = {
    UserId = userId
    TeamId = Some teamId
    Subject = TeamMember(userId, teamId)
    ModulePermissions = Map.empty
    ModuleExposure = Map.empty
    PlatformRole = None
}

let private mkStore () : IPermissionStore =
    PermissionStore(InMemoryBlobStorage.InMemoryBlobStorage()) :> IPermissionStore

/// An `IActionAuthorizer` that allows every action (dev-only helper).
let private allowAll = ActionAuthorizer.allowAll

/// An `IActionAuthorizer` that denies every action (the absent-seam floor).
let private denyAll = ActionAuthorizer.denyAll

/// A policy that grants `invoke` on the two built-in ids unconditionally,
/// so the gating under test is the registry's routing, not the policy's
/// requirement evaluation (covered by ActionAuthorizerTests).
let private invokeAllowPolicy = {
    Rules = [
        {
            Kind = HostCapabilityRegistry.InvokeActionKind
            Target = "*"
            Requirement = ActionRequirement.Unrestricted
        }
    ]
}

/// A registry over the PermissionStore-backed authorizer with an allow-all
/// policy — still enforces the structural cross-scope gate.
let private scopeGatingRegistry () : IHostCapabilityRegistry =
    HostCapabilityRegistry.create (PermissionStoreActionAuthorizer.create invokeAllowPolicy (mkStore ()) None)

/// A capability id used across the pack.
let private echoCap = CapabilityId "test.echo"

let tests =
    testList "HostCapabilityRegistry (Phase 266)" [

        testCaseAsync "a registered capability runs only when the authorizer grants it"
        <| async {
            // allow-all authorizer → the registered handler runs.
            let allowed = HostCapabilityRegistry.create allowAll

            allowed.Register echoCap (fun args _ -> async { return args })

            match! allowed.Invoke echoCap (Map [ "k", "v" ]) (teamCtx "alice" "t1") with
            | HostCapabilityOutcome.Completed result ->
                Expect.equal result (Map [ "k", "v" ]) "handler result flows back"
            | HostCapabilityOutcome.Denied reason -> failtestf "allow-all must run the handler; got: %s" reason

            // deny-all authorizer → the SAME registered handler is denied.
            let denied = HostCapabilityRegistry.create denyAll
            let mutable ran = false

            denied.Register echoCap (fun args _ -> async {
                ran <- true
                return args
            })

            match! denied.Invoke echoCap (Map [ "k", "v" ]) (teamCtx "alice" "t1") with
            | HostCapabilityOutcome.Denied _ -> ()
            | HostCapabilityOutcome.Completed _ -> failtest "deny-all must refuse even a registered capability"

            Expect.isFalse ran "a denied invoke must never reach the handler side-effect"
        }

        testCaseAsync "an unregistered id denies (even under an allow-all authorizer)"
        <| async {
            let registry = HostCapabilityRegistry.create allowAll

            match! registry.Invoke (CapabilityId "nope") Map.empty (teamCtx "alice" "t1") with
            | HostCapabilityOutcome.Denied reason ->
                Expect.stringContains reason "no capability registered" "names the missing-registration cause"
            | HostCapabilityOutcome.Completed _ ->
                failtest "an unregistered id must deny even when the authorizer allows"
        }

        testCaseAsync "a cross-scope invoke denies structurally (GP 4)"
        <| async {
            let registry = scopeGatingRegistry ()
            registry.Register echoCap (fun args _ -> async { return args })

            // Target a scope other than the caller's own team via the reserved arg.
            let crossScopeArgs = Map [ HostCapabilityRegistry.TargetScopeArg, "t2" ]

            match! registry.Invoke echoCap crossScopeArgs (teamCtx "alice" "t1") with
            | HostCapabilityOutcome.Denied reason -> Expect.stringContains reason "scope" "names the scope gate"
            | HostCapabilityOutcome.Completed _ ->
                failtest "a cross-scope invoke must deny even under an allow-all policy"

            // The same capability targeting the caller's OWN scope passes.
            let ownScopeArgs = Map [ HostCapabilityRegistry.TargetScopeArg, "t1" ]

            match! registry.Invoke echoCap ownScopeArgs (teamCtx "alice" "t1") with
            | HostCapabilityOutcome.Completed _ -> ()
            | HostCapabilityOutcome.Denied reason -> failtestf "own-scope invoke must pass the gate; got: %s" reason
        }

        testCaseAsync "an empty registry is deny-all (GP 13 — not composed = nothing reachable)"
        <| async {
            // Nothing registered; allow-all authorizer. Every id still denies
            // because no handler is reachable.
            let empty = HostCapabilityRegistry.create allowAll

            match! empty.Invoke echoCap Map.empty (teamCtx "alice" "t1") with
            | HostCapabilityOutcome.Denied _ -> ()
            | HostCapabilityOutcome.Completed _ -> failtest "an empty registry must reach no capability"

            // And with the deny-all authorizer, the floor is doubly closed.
            let closed = HostCapabilityRegistry.create denyAll

            match! closed.Invoke echoCap Map.empty (teamCtx "alice" "t1") with
            | HostCapabilityOutcome.Denied _ -> ()
            | HostCapabilityOutcome.Completed _ -> failtest "deny-all + empty registry must deny"
        }

        testCaseAsync "the clipboard-write built-in reaches its injected effect through the gate"
        <| async {
            let mutable written = ""
            let registry = HostCapabilityRegistry.create allowAll

            registry
            |> HostCapability.registerClipboardWrite (fun text -> async { written <- text })

            match!
                registry.Invoke
                    HostCapability.ClipboardWrite
                    (Map [ HostCapability.ClipboardTextArg, "hello" ])
                    (teamCtx "alice" "t1")
            with
            | HostCapabilityOutcome.Completed _ ->
                Expect.equal written "hello" "the injected clipboard effect ran with the arg"
            | HostCapabilityOutcome.Denied reason -> failtestf "granted clipboard-write must run; got: %s" reason

            // Denied under a deny-all authorizer — the effect must NOT run.
            let mutable written2 = false
            let closed = HostCapabilityRegistry.create denyAll

            closed
            |> HostCapability.registerClipboardWrite (fun _ -> async { written2 <- true })

            match!
                closed.Invoke
                    HostCapability.ClipboardWrite
                    (Map [ HostCapability.ClipboardTextArg, "x" ])
                    (teamCtx "alice" "t1")
            with
            | HostCapabilityOutcome.Denied _ ->
                Expect.isFalse written2 "a denied built-in must not reach its side-effect"
            | HostCapabilityOutcome.Completed _ -> failtest "deny-all must refuse the built-in"
        }

        testCaseAsync "the file-read built-in returns its content through the result bag"
        <| async {
            let registry = HostCapabilityRegistry.create allowAll

            registry
            |> HostCapability.registerFileRead (fun path -> async { return sprintf "content-of:%s" path })

            match!
                registry.Invoke
                    HostCapability.FileRead
                    (Map [ HostCapability.FilePathArg, "notes.txt" ])
                    (teamCtx "alice" "t1")
            with
            | HostCapabilityOutcome.Completed result ->
                Expect.equal
                    (result |> Map.tryFind HostCapability.FileContentArg)
                    (Some "content-of:notes.txt")
                    "the read content flows back in the result bag"
            | HostCapabilityOutcome.Denied reason -> failtestf "granted file-read must run; got: %s" reason
        }

        testCase "toActionDescriptor maps a capability to the neutral invoke descriptor"
        <| fun _ ->
            let d =
                HostCapabilityRegistry.toActionDescriptor echoCap Map.empty (teamCtx "alice" "t1")

            Expect.equal d.Kind HostCapabilityRegistry.InvokeActionKind "Kind is the invoke action kind"
            Expect.equal d.Target "test.echo" "Target is the capability id"
            Expect.equal d.Scope (Some "t1") "Scope defaults to the caller's own team when no target-scope arg"

            let targeted =
                HostCapabilityRegistry.toActionDescriptor
                    echoCap
                    (Map [ HostCapabilityRegistry.TargetScopeArg, "t9" ])
                    (teamCtx "alice" "t1")

            Expect.equal targeted.Scope (Some "t9") "the reserved target-scope arg overrides the caller's scope"
    ]