module ToolUp.Platform.Tests.InProcess.DataSubjectRequestTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.DataSubjectRequestApi
open ToolUp.Platform.Tests.Contracts

[<Tests>]
let tests = IDataSubjectRequestContract.tests

// ─── Phase 229 — DSR Platform-Admin authorization gate ───────────────
//
// Every IDataSubjectRequestApi method gates on Platform-Admin: the
// `[<RequiresRole "PlatformAdmin">]` classifier attribute on the contract
// PLUS an in-handler `canModifyPlatformConfig` check. These cases prove
// the in-handler gate (a non-admin caller is denied export/preview/
// confirm even if the deployment auth middleware admitted them).

let private nonAdmin: AccessContext =
    AccessContext.unrestricted (AuthenticatedUser "regular-user")

let private admin: AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser "admin-user") with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private mkApi (accessContext: AccessContext) : IDataSubjectRequestApi =
    DataSubjectRequestApiHandler.create
        []
        []
        ErasurePolicy.Tombstone
        "team-x"
        accessContext.UserId
        accessContext
        DataSubjectRequestApiHandler.noOpAuditCallback
        None

let private exportInput: ExportRequestInput = {
    SubjectUserId = "victim"
    TeamId = None
    Reason = "test"
}

let private eraseInput: ErasureRequestInput = {
    SubjectUserId = "victim"
    TeamId = None
    Reason = "test"
    OverridePolicy = None
}

[<Tests>]
let authorizationTests =
    testList "Phase 229 — DSR Platform-Admin authorization" [

        testCaseAsync "non-admin RequestExport is denied"
        <| async {
            let api = mkApi nonAdmin

            match! api.RequestExport exportInput with
            | Error msg -> Expect.equal msg "platform admin role required" "non-admin export is denied"
            | Ok _ -> failtest "expected a non-admin export to be denied"
        }

        testCaseAsync "non-admin PreviewErasure is denied"
        <| async {
            let api = mkApi nonAdmin

            match! api.PreviewErasure eraseInput with
            | Error msg -> Expect.equal msg "platform admin role required" "non-admin preview is denied"
            | Ok _ -> failtest "expected a non-admin preview to be denied"
        }

        testCaseAsync "non-admin ConfirmErasure is denied (before the preview-cache lookup)"
        <| async {
            let api = mkApi nonAdmin

            match! api.ConfirmErasure "any-request-id" with
            | Error msg -> Expect.equal msg "platform admin role required" "non-admin confirm is denied"
            | Ok _ -> failtest "expected a non-admin confirm to be denied"
        }

        testCaseAsync "admin RequestExport passes the gate"
        <| async {
            let api = mkApi admin

            // Empty exporters → an Ok empty-segment envelope, NOT the gate error.
            match! api.RequestExport exportInput with
            | Ok _ -> ()
            | Error msg -> Expect.notEqual msg "platform admin role required" "an admin must pass the gate"
        }
    ]