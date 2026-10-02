module ToolUp.Platform.Tests.InProcess.Phase971PlatformTenantTests

open System
open System.Collections.Concurrent
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

// ─── Phase 971 — PlatformTenant: provisioning clears the offboard ledger first ──
//
// A provision of a scope supersedes any prior offboard: its Deprovisioning
// done-set must go, or a later re-offboard of the re-provisioned scope
// SKIPS every hook the stale set names — tenant data left un-erased.
// `ILifecycleLedger.Clear` raises when storage refuses the delete. The
// provision path therefore clears FIRST, before any provisioning hook
// runs, and a refusal answers `Error` with nothing provisioned, so a re-run
// retries cleanly.

/// A provisioning hook that counts its runs.
type private CountingHook() =
    let runs = ConcurrentQueue<string>()
    member _.Runs = List.ofSeq runs

    interface ITenantLifecycle with
        member _.Name = "phase-971-counting"

        member _.OnProvisioned(scopeId, _) = async {
            runs.Enqueue scopeId
            return LifecycleHookResult.Completed
        }

        member _.OnDeprovisioned(_, _) = async { return LifecycleHookResult.Completed }

let private request: ProvisioningRequest = {
    Slug = "acme"
    OwnerUserId = "owner-971"
    Region = "eu-west"
    Tier = "standard"
    DisplayName = "Acme"
}

let private handlerOver (ledger: ILifecycleLedger) (hook: CountingHook) : IPlatformTenantApi =
    let services = ServiceCollection()

    services.AddSingleton<AccessContext>(
        {
            AccessContext.unrestricted (AuthenticatedUser "admin-971") with
                PlatformRole = Some PlatformRole.PlatformAdmin
        }
    )
    |> ignore

    services.AddSingleton<ILifecycleLedger>(ledger) |> ignore
    services.AddSingleton<ITenantLifecycle>(hook :> ITenantLifecycle) |> ignore

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    PlatformTenantApiHandler.platformTenantApi ctx

/// A ledger over `blob` with a stale Deprovisioning done-set for `scopeId`.
let private staleLedger (blob: IBlobStorage) (scopeId: string) = async {
    let ledger = BlobBackedLifecycleLedger.create blob
    do! ledger.Record(scopeId, Deprovisioning, "crypto-shred", LedgerDisposition.Completed)
    return ledger
}

let private scope () =
    "team-971-" + Guid.NewGuid().ToString("N").Substring(0, 8)

let tests =
    testList "Phase 971 - PlatformTenant" [
        testAsync "ProvisionTenant answers Error and runs no hook when the offboard-ledger clear is refused" {
            let scopeId = scope ()

            let blob =
                DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> n.StartsWith "_tenant-lifecycle-ledger/")
                :> IBlobStorage

            let! ledger = staleLedger blob scopeId
            let hook = CountingHook()
            let api = handlerOver ledger hook

            match! api.ProvisionTenant(scopeId, "wire-actor", request) with
            | Error message -> Expect.stringContains message scopeId "the failure names the scope"
            | Ok summary -> failtestf "a refused ledger clear must not read as provisioned, got %A" summary

            Expect.isEmpty hook.Runs "nothing was provisioned, so a re-run retries cleanly"

            let! stillRecorded = ledger.GetCompleted(scopeId, Deprovisioning)
            Expect.contains stillRecorded "crypto-shred" "the stale done-set is still there"
        }

        testAsync "ProvisionTenant over healthy storage clears the offboard ledger and runs the hooks" {
            let scopeId = scope ()
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let! ledger = staleLedger blob scopeId
            let hook = CountingHook()
            let api = handlerOver ledger hook

            match! api.ProvisionTenant(scopeId, "wire-actor", request) with
            | Ok _ -> ()
            | Error message -> failtestf "expected the provision to succeed, got %s" message

            Expect.equal hook.Runs [ scopeId ] "the provisioning hook ran once"

            let! recorded = ledger.GetCompleted(scopeId, Deprovisioning)
            Expect.isEmpty recorded "the stale offboard done-set is gone"
        }
    ]