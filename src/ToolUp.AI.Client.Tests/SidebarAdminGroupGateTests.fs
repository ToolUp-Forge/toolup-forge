// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarAdminGroupGateTests

// ─── Sidebar admin-group role gate (Phase 4b / commit 4f.2) ──────────
//
// Pins `ClientConfig.isPlatformAdminSidebarGroup` — the predicate behind
// stage 2 of `SidebarVisibility.visible` (Phase 570; the shell's former
// inline `adminGroupFiltered` in `SDK.Client.fs`) — and its relationship
// to the pre-existing `isAdminSidebarGroup` union used by stage 4's
// no-active-team landing collapse. Since Phase 570 both predicates are
// defined in `SidebarVisibility` and re-exported here unchanged; this
// pack keeps exercising the `ClientConfig` names because those are the
// established call sites, and the .NET-tier matrix pack
// (`SidebarVisibilityContractTests`) pins the same sets directly.
//
// Regression context: the filter originally matched only the literal
// group `Some "Platform Admin"`, while the SDK's own admin built-ins
// declare "Platform Management" (PlatformAdminUI / HealthMonitorUI /
// ServiceStatusBoardUI / …) and "Team Management" (TeamManagerUI /
// PermissionsAdminUI / …) — so every authenticated non-admin saw the
// full admin rail. The fix gates the platform-scoped groups
// ("Platform Admin" + "Platform Management") on `PlatformRole` and
// deliberately leaves the team-scoped "Team Management" ungated: the
// shell model carries no team role (`TeamInfo` has no role field), and
// blanket-hiding it would strip team Owners/Admins of their own
// management tools. Server-side guards remain the enforcement (GP 12).

open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

let tests =
    testList "Sidebar admin-group role gate (4f.2)" [
        testList "isPlatformAdminSidebarGroup — platform-scoped set" [
            testCase "\"Platform Admin\" is platform-scoped (consumer `withGroup` convention preserved)" (fun () ->
                Expect.isTrue
                    (ClientConfig.isPlatformAdminSidebarGroup (Some "Platform Admin"))
                    "a consumer module declaring withGroup \"Platform Admin\" keeps its role gate")

            testCase "\"Platform Management\" is platform-scoped — non-admins lose the SDK admin built-ins" (fun () ->
                Expect.isTrue
                    (ClientConfig.isPlatformAdminSidebarGroup (Some "Platform Management"))
                    "the SDK admin built-ins' declared group must be role-gated")

            testCase "\"Team Management\" is NOT platform-scoped — team Owners/Admins keep their tools" (fun () ->
                Expect.isFalse
                    (ClientConfig.isPlatformAdminSidebarGroup (Some "Team Management"))
                    "team-scoped management must not be gated on PlatformRole")

            testCase "an app-domain group is not gated" (fun () ->
                Expect.isFalse (ClientConfig.isPlatformAdminSidebarGroup (Some "Reports")) "app groups pass freely")

            testCase "ungrouped modules are not gated" (fun () ->
                Expect.isFalse (ClientConfig.isPlatformAdminSidebarGroup None) "no group ⇒ never admin-gated")
        ]

        testList "isAdminSidebarGroup — no-team landing-gate union unchanged" [
            testCase "all three admin/management groups remain in the union" (fun () ->
                for g in [ "Platform Admin"; "Platform Management"; "Team Management" ] do
                    Expect.isTrue
                        (ClientConfig.isAdminSidebarGroup (Some g))
                        $"\"{g}\" must stay visible to a team-less platform admin under the landing gate")

            testCase "non-admin groups stay outside the union" (fun () ->
                Expect.isFalse (ClientConfig.isAdminSidebarGroup (Some "Reports")) "app group is not in the union"
                Expect.isFalse (ClientConfig.isAdminSidebarGroup None) "no group is not in the union")
        ]

        testList "shell filter semantics (adminGroupFiltered predicate)" [
            // The shell's filter is
            //   isAdmin || not (ClientConfig.isPlatformAdminSidebarGroup m.Group)
            // — replicated here over a group-shaped fixture list.
            let visibleTo isAdmin groups =
                groups
                |> List.filter (fun g -> isAdmin || not (ClientConfig.isPlatformAdminSidebarGroup g))

            let rail = [
                None
                Some "Reports"
                Some "Team Management"
                Some "Platform Management"
                Some "Platform Admin"
            ]

            testCase "non-admin sees app modules + Team Management, loses both platform-scoped groups" (fun () ->
                Expect.equal
                    (visibleTo false rail)
                    [ None; Some "Reports"; Some "Team Management" ]
                    "non-admin rail must drop Platform Management and Platform Admin only")

            testCase "platform admin sees the full rail" (fun () ->
                Expect.equal (visibleTo true rail) rail "the role reveals every group")
        ]
    ]