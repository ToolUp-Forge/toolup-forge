// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarAreaTests

// ─── Two-surface Admin Area (Phase 567) ─────────────────────────────
//
// Pins `ClientConfig.effectiveArea` — the derivation behind the
// `AdminSurface = SeparateArea` sidebar split in `SDK.Client.fs` — and
// the switcher-gating / partition semantics the shell layers on top.
//
// Design: a module's *effective* area is `Administration` if it declares
// it (`ClientModule.withArea Administration`) OR sits in an admin sidebar
// group (`isAdminSidebarGroup`) — so the SDK's admin built-ins move to
// the admin area with NO registration change (GP 9). The switcher appears
// only when the current (product) rail has an admin area to switch to;
// because the shell's `adminGroupFiltered` already strips platform-scoped
// groups from non-admins, a plain user's admin set is empty and no
// switcher renders (GP 12 — server enforcement is authoritative). The
// default `InlineGroups` never consults any of this (byte-identical to
// pre-567).

open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

let tests =
    testList "Two-surface Admin Area (Phase 567)" [
        testList "effectiveArea — derivation" [
            testCase "admin sidebar group derives Administration (GP 9 — no registration change)" (fun () ->
                Expect.equal
                    (ClientConfig.effectiveArea Product (Some "Platform Management"))
                    Administration
                    "an SDK admin built-in (Platform Management) lands in the admin area from its group alone")

            testCase "team-scoped management also derives Administration" (fun () ->
                Expect.equal
                    (ClientConfig.effectiveArea Product (Some "Team Management"))
                    Administration
                    "Team Management is an admin-area group")

            testCase "explicit withArea Administration wins with no group" (fun () ->
                Expect.equal
                    (ClientConfig.effectiveArea Administration None)
                    Administration
                    "a consumer module opting into the admin area is honoured")

            testCase "explicit Administration wins even over a product-looking group" (fun () ->
                Expect.equal
                    (ClientConfig.effectiveArea Administration (Some "Reports"))
                    Administration
                    "declared area is authoritative over group derivation")

            testCase "product module in an app group stays Product" (fun () ->
                Expect.equal
                    (ClientConfig.effectiveArea Product (Some "Reports"))
                    Product
                    "an ordinary app module is not pulled into the admin area")

            testCase "ungrouped product module stays Product" (fun () ->
                Expect.equal (ClientConfig.effectiveArea Product None) Product "no group + no opt-in ⇒ Product")
        ]

        testList "SeparateArea partition + switcher gating (shell semantics)" [
            // The shell partitions the already-role-filtered visible modules
            // by effectiveArea, then shows an "Administration" switcher iff
            // the admin partition is non-empty. Replicated here over
            // (area, group) fixtures — the same shape the render pipeline uses.
            let effective (area, group) = ClientConfig.effectiveArea area group

            let partition modules =
                modules |> List.partition (fun m -> effective m = Administration)

            let switcherShown (adminModules: _ list) = not (List.isEmpty adminModules)

            testCase "platform admin (admin groups present) ⇒ admin partition non-empty, switcher shown" (fun () ->
                // adminGroupFiltered kept the platform-scoped groups for an admin.
                let rail = [
                    (Product, None)
                    (Product, Some "Reports")
                    (Product, Some "Platform Management")
                    (Product, Some "Team Management")
                ]

                let admin, product = partition rail
                Expect.equal (List.length admin) 2 "Platform + Team Management are the admin partition"
                Expect.equal (List.length product) 2 "None + Reports are the product partition"
                Expect.isTrue (switcherShown admin) "an admin area exists to switch to")

            testCase "plain non-admin (platform groups stripped) ⇒ empty admin partition, no switcher" (fun () ->
                // adminGroupFiltered already removed "Platform Management" for a
                // non-admin; a plain user's remaining rail carries no admin group.
                let rail = [ (Product, None); (Product, Some "Reports") ]
                let admin, _ = partition rail
                Expect.isTrue (List.isEmpty admin) "no admin-area modules remain"
                Expect.isFalse (switcherShown admin) "no switcher for a plain user (GP 12)")

            testCase "consumer withArea Administration surfaces the switcher" (fun () ->
                let rail = [ (Product, None); (Administration, Some "Reports") ]
                let admin, product = partition rail
                Expect.equal (List.length admin) 1 "the opted-in module is in the admin partition"
                Expect.equal (List.length product) 1 "the plain module stays in product"
                Expect.isTrue (switcherShown admin) "an explicit admin module is enough to show the switcher")
        ]
    ]