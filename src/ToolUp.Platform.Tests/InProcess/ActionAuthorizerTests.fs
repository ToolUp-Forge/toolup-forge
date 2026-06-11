module ToolUp.Platform.Tests.InProcess.ActionAuthorizerTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.Tests.Contracts

// ─── Phase 113 — action-authorizer tests ─────────────────────────────
//
// Three layers:
//   1. `ActionPolicy` matching unit tests (wildcards, registration
//      order, default-deny floor).
//   2. `PermissionStoreActionAuthorizer` semantics over a real
//      blob-backed `PermissionStore` (in-memory storage): permission
//      grant → Allow, absence → Deny, tier gating via a stub
//      `IUserClaims`, cross-scope structural denial, `All`
//      composition, fail-closed on a throwing backing store.
//   3. The `IActionAuthorizerContract` pack bound to the shipped
//      default (the conformance bar a second impl validates against).

let private run a = a |> Async.RunSynchronously

let private teamCtx (userId: string) (teamId: string) : AccessContext = {
    UserId = userId
    TeamId = Some teamId
    Subject = TeamMember(userId, teamId)
    ModulePermissions = Map.empty
    PlatformRole = None
}

let private userCtx (userId: string) (perms: Map<string, ModulePermission list>) : AccessContext = {
    UserId = userId
    TeamId = None
    Subject = AuthenticatedUser userId
    ModulePermissions = perms
    PlatformRole = None
}

let private action kind target : ActionDescriptor = {
    Kind = kind
    Target = target
    Scope = None
}

let private premiumClaims (isPremium: bool) : IUserClaims =
    { new IUserClaims with
        member _.GetPremiumStatus _ = async {
            return
                if isPremium then
                    Premium(DateTimeOffset.UtcNow, "test", None)
                else
                    NotPremium
        }

        member _.ListPremiumUsers() = async { return Ok [] }

        member _.GrantPremium(_, grantor, reason) = async { return Ok(Premium(DateTimeOffset.UtcNow, grantor, reason)) }

        member _.RevokePremium(_, _, _) = async { return Ok() }
    }

let private mkStore () : IPermissionStore =
    PermissionStore(InMemoryBlobStorage.InMemoryBlobStorage()) :> IPermissionStore

/// Store with `alice` granted Write on `reports` in team `t1`.
let private seededStore () : IPermissionStore =
    let store = mkStore ()

    store.SetMemberPermissions("t1", "alice", "reports", [ ModulePermission.Write ])
    |> run
    |> ignore

    store

// ─── 1. Policy matching ──────────────────────────────────────────────

let private policyTests =
    testList "ActionPolicy — rule matching" [
        testCase "empty policy matches nothing (default-deny floor)"
        <| fun _ -> Expect.isNone (ActionPolicy.tryFindRule (action "call" "X") ActionPolicy.empty) "no rule"

        testCase "exact / wildcard / prefix matching"
        <| fun _ ->
            Expect.isTrue (ActionPolicy.matches "call" "call") "exact"
            Expect.isFalse (ActionPolicy.matches "call" "dispatch") "exact mismatch"
            Expect.isTrue (ActionPolicy.matches "*" "anything") "wildcard"
            Expect.isTrue (ActionPolicy.matches "Report*" "ReportApi.get") "prefix"
            Expect.isFalse (ActionPolicy.matches "Report*" "OtherApi.get") "prefix mismatch"

        testCase "first matching rule wins (registration order)"
        <| fun _ ->
            let policy = {
                Rules = [
                    {
                        Kind = "call"
                        Target = "A*"
                        Requirement = ActionRequirement.PremiumTier
                    }
                    {
                        Kind = "*"
                        Target = "*"
                        Requirement = ActionRequirement.Unrestricted
                    }
                ]
            }

            let rule = ActionPolicy.tryFindRule (action "call" "Api.get") policy

            Expect.equal
                (rule |> Option.map _.Requirement)
                (Some ActionRequirement.PremiumTier)
                "the earlier, narrower rule wins"
    ]

// ─── 2. PermissionStoreActionAuthorizer semantics ────────────────────

let private allowAllPolicy = {
    Rules = [
        {
            Kind = "*"
            Target = "*"
            Requirement = ActionRequirement.Unrestricted
        }
    ]
}

let private reportsPolicy = {
    Rules = [
        {
            Kind = "dispatch"
            Target = "reports/*"
            Requirement = ActionRequirement.Permission("reports", ModulePermission.Write)
        }
    ]
}

let private authorizerTests =
    testList "PermissionStoreActionAuthorizer" [

        testCaseAsync "empty policy denies every action (default-deny)"
        <| async {
            let auth =
                PermissionStoreActionAuthorizer.create ActionPolicy.empty (mkStore ()) None

            match! auth.Authorize (action "dispatch" "anything") (teamCtx "alice" "t1") with
            | AuthorizationDecision.Deny reason -> Expect.stringContains reason "default-deny" "names the floor"
            | AuthorizationDecision.Allow -> failtest "empty policy must deny"
        }

        testCaseAsync "permission grant in IPermissionStore → Allow for the team member"
        <| async {
            let auth =
                PermissionStoreActionAuthorizer.create reportsPolicy (seededStore ()) None

            match! auth.Authorize (action "dispatch" "reports/refresh") (teamCtx "alice" "t1") with
            | AuthorizationDecision.Allow -> ()
            | AuthorizationDecision.Deny reason -> failtestf "granted member must be allowed; got: %s" reason
        }

        testCaseAsync "no grant → Deny (a member without the permission)"
        <| async {
            let auth =
                PermissionStoreActionAuthorizer.create reportsPolicy (seededStore ()) None

            match! auth.Authorize (action "dispatch" "reports/refresh") (teamCtx "bob" "t1") with
            | AuthorizationDecision.Deny _ -> ()
            | AuthorizationDecision.Allow -> failtest "ungranted member must be denied"
        }

        testCaseAsync "granted permission implies weaker requirement (Write covers Read)"
        <| async {
            let readPolicy = {
                Rules = [
                    {
                        Kind = "dispatch"
                        Target = "reports/*"
                        Requirement = ActionRequirement.Permission("reports", ModulePermission.Read)
                    }
                ]
            }

            let auth = PermissionStoreActionAuthorizer.create readPolicy (seededStore ()) None

            match! auth.Authorize (action "dispatch" "reports/view") (teamCtx "alice" "t1") with
            | AuthorizationDecision.Allow -> ()
            | AuthorizationDecision.Deny reason -> failtestf "Write grant must imply Read; got: %s" reason
        }

        testCaseAsync "non-team subject resolves against AccessContext.ModulePermissions"
        <| async {
            let auth = PermissionStoreActionAuthorizer.create reportsPolicy (mkStore ()) None
            let granted = userCtx "carol" (Map[("reports", [ ModulePermission.Admin ])])
            let ungranted = userCtx "dave" Map.empty

            match! auth.Authorize (action "dispatch" "reports/refresh") granted with
            | AuthorizationDecision.Allow -> ()
            | AuthorizationDecision.Deny reason -> failtestf "ctx-granted user must be allowed; got: %s" reason

            match! auth.Authorize (action "dispatch" "reports/refresh") ungranted with
            | AuthorizationDecision.Deny _ -> ()
            | AuthorizationDecision.Allow -> failtest "empty ctx permissions must deny (no unrestricted exception here)"
        }

        testCaseAsync "tier-locked action denied below the tier, allowed at the tier"
        <| async {
            let tierPolicy = {
                Rules = [
                    {
                        Kind = "call"
                        Target = "premium/*"
                        Requirement = ActionRequirement.PremiumTier
                    }
                ]
            }

            let below =
                PermissionStoreActionAuthorizer.create tierPolicy (mkStore ()) (Some(premiumClaims false))

            let at =
                PermissionStoreActionAuthorizer.create tierPolicy (mkStore ()) (Some(premiumClaims true))

            match! below.Authorize (action "call" "premium/export") (teamCtx "alice" "t1") with
            | AuthorizationDecision.Deny reason -> Expect.stringContains reason "premium" "names the tier"
            | AuthorizationDecision.Allow -> failtest "below-tier must deny"

            match! at.Authorize (action "call" "premium/export") (teamCtx "alice" "t1") with
            | AuthorizationDecision.Allow -> ()
            | AuthorizationDecision.Deny reason -> failtestf "at-tier must allow; got: %s" reason
        }

        testCaseAsync "tier requirement with no tier provider registered → Deny (fail closed)"
        <| async {
            let tierPolicy = {
                Rules = [
                    {
                        Kind = "*"
                        Target = "*"
                        Requirement = ActionRequirement.PremiumTier
                    }
                ]
            }

            let auth = PermissionStoreActionAuthorizer.create tierPolicy (mkStore ()) None

            match! auth.Authorize (action "call" "x") (teamCtx "alice" "t1") with
            | AuthorizationDecision.Deny _ -> ()
            | AuthorizationDecision.Allow -> failtest "no provider must deny, not silently allow"
        }

        testCaseAsync "cross-scope action denied structurally (GP 4)"
        <| async {
            let auth = PermissionStoreActionAuthorizer.create allowAllPolicy (mkStore ()) None

            let crossScope = {
                action "dispatch" "x" with
                    Scope = Some "t2"
            }

            let ownScope = {
                action "dispatch" "x" with
                    Scope = Some "t1"
            }

            match! auth.Authorize crossScope (teamCtx "alice" "t1") with
            | AuthorizationDecision.Deny reason -> Expect.stringContains reason "scope" "names the scope gate"
            | AuthorizationDecision.Allow -> failtest "cross-scope must deny even under an allow-all rule"

            match! auth.Authorize ownScope (teamCtx "alice" "t1") with
            | AuthorizationDecision.Allow -> ()
            | AuthorizationDecision.Deny reason -> failtestf "own-scope must pass the gate; got: %s" reason
        }

        testCaseAsync "All composition — every requirement must grant"
        <| async {
            let composite = {
                Rules = [
                    {
                        Kind = "dispatch"
                        Target = "reports/*"
                        Requirement =
                            ActionRequirement.All [
                                ActionRequirement.Permission("reports", ModulePermission.Write)
                                ActionRequirement.PremiumTier
                            ]
                    }
                ]
            }

            let bothMet =
                PermissionStoreActionAuthorizer.create composite (seededStore ()) (Some(premiumClaims true))

            let tierMissing =
                PermissionStoreActionAuthorizer.create composite (seededStore ()) (Some(premiumClaims false))

            match! bothMet.Authorize (action "dispatch" "reports/x") (teamCtx "alice" "t1") with
            | AuthorizationDecision.Allow -> ()
            | AuthorizationDecision.Deny reason -> failtestf "both requirements met must allow; got: %s" reason

            match! tierMissing.Authorize (action "dispatch" "reports/x") (teamCtx "alice" "t1") with
            | AuthorizationDecision.Deny _ -> ()
            | AuthorizationDecision.Allow -> failtest "one unmet requirement must deny"
        }

        testCaseAsync "a throwing backing store fails closed (Deny, no exception)"
        <| async {
            let throwing =
                { new IPermissionStore with
                    member _.GetTeamPermissions _ = async { return failwith "storage blip" }
                    member _.SetTeamPermissions(_, _) = async { return Ok() }
                    member _.GetEffectivePermissions(_, _) = async { return failwith "storage blip" }
                    member _.SetMemberPermissions(_, _, _, _) = async { return Ok() }
                    member _.SetTeamDefaults(_, _) = async { return Ok() }
                }

            let auth = PermissionStoreActionAuthorizer.create reportsPolicy throwing None

            match! auth.Authorize (action "dispatch" "reports/x") (teamCtx "alice" "t1") with
            | AuthorizationDecision.Deny reason -> Expect.stringContains reason "fail-closed" "names the posture"
            | AuthorizationDecision.Allow -> failtest "a storage blip must never grant"
        }

        testCase "denyAll / allowAll built-ins"
        <| fun _ ->
            match ActionAuthorizer.denyAll.Authorize (action "x" "y") (teamCtx "a" "t") |> run with
            | AuthorizationDecision.Deny _ -> ()
            | AuthorizationDecision.Allow -> failtest "denyAll must deny"

            match ActionAuthorizer.allowAll.Authorize (action "x" "y") (teamCtx "a" "t") |> run with
            | AuthorizationDecision.Allow -> ()
            | AuthorizationDecision.Deny _ -> failtest "allowAll must allow"
    ]

// ─── 3. Contract binding ─────────────────────────────────────────────

let private contractTests =
    let auth =
        PermissionStoreActionAuthorizer.create reportsPolicy (seededStore ()) None

    IActionAuthorizerContract.tests {
        Name = "PermissionStoreActionAuthorizer"
        Authorizer = auth
        AllowedCall = action "dispatch" "reports/refresh", teamCtx "alice" "t1"
        DeniedCall = action "dispatch" "reports/refresh", teamCtx "bob" "t1"
    }

let tests =
    testList "ActionAuthorizer (Phase 113)" [ policyTests; authorizerTests; contractTests ]