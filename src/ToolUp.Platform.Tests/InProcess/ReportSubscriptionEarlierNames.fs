// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ReportSubscriptionEarlierNames

// ─── Phase 1029 — the reporting tier's earlier principal name ─────────
//
// `StandingSubscriptionPrincipal` is an obsolete abbreviation of the shared
// `DeclaredPrincipal` record, so a consumer naming it still compiles (with
// FS0044) and builds exactly the value the declaration path takes. Naming
// it is the point of this file, so the deprecation warning is suppressed
// here — and only here, rather than in the pack that exercises the path.
#nowarn "44"

open Expecto
open ToolUp.Platform
open ToolUp.Reporting

let earlierPrincipalNameTest = test "the standing-subscription principal IS the shared declared principal (Phase 1029)" {
    let earlier: StandingSubscriptionPrincipal = {
        Name = "ops"
        UserId = "svc-ops"
        DisplayName = "Ops"
    }

    Expect.equal typeof<StandingSubscriptionPrincipal> typeof<DeclaredPrincipal> "one type, two names"

    let standing: ReportingCompose.StandingSubscriptions = {
        Principals = [ earlier ]
        Subscriptions = []
    }

    Expect.equal standing.Principals [ earlier ] "the earlier name declares a principal as before"
}