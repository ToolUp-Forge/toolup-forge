module ToolUp.Platform.Tests.InProcess.ComponentHealthRollupTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.HealthChecks

// ─── Phase 290 — component health rollup by id ────────────────────────
//
// Covers the acceptance shape: a degraded companion's status appears
// under its Phase 279 ComponentId (companion:IHealthCheck/<Name>), the
// same id the manifest's IHealthCheck companion entries carry; an unkeyed
// (blank-Name) probe is retained not dropped; the rollup is read-only and
// only built on demand (GP 13).

/// A stub `IHealthCheck` with a fixed name + outcome.
let private probe (name: string) (result: HealthResult) : IHealthCheck =
    { new IHealthCheck with
        member _.Name = name
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout
        member _.Check() = async { return result }
    }

let tests =
    testList "ComponentHealthRollup" [

        // ── a degraded probe surfaces under its ComponentId ───────────
        testCase "a degraded companion's status appears under its ComponentId"
        <| fun _ ->
            let rollup =
                ComponentHealthRollup.build [
                    probe "redis" Healthy, Healthy
                    probe "oidc-discovery" (Degraded "slow"), Degraded "slow"
                ]

            let redisId = ComponentId.forCompanionImpl "IHealthCheck" "redis"
            let oidcId = ComponentId.forCompanionImpl "IHealthCheck" "oidc-discovery"

            Expect.equal
                (rollup.ByComponent |> Map.tryFind redisId)
                (Some Healthy)
                "redis rolls up Healthy under its id"

            Expect.equal
                (rollup.ByComponent |> Map.tryFind oidcId)
                (Some(Degraded "slow"))
                "the degraded probe rolls up under its own id, not a flat list"

        // ── the id lines up with the manifest companion-slot id ───────
        testCase "the rollup key matches the manifest IHealthCheck companion id (id-join)"
        <| fun _ ->
            let app = ServerApp.empty |> ServerApp.withHealthCheck (probe "redis" Healthy)

            let manifest = ServerApp.compositionManifest app
            let rollup = ComponentHealthRollup.forApp app |> Async.RunSynchronously

            let healthEntry =
                manifest.CompanionSlots
                |> List.find (fun e -> e.Label = "IHealthCheck" && e.Impl = Some "redis")

            Expect.isTrue
                (rollup.ByComponent |> Map.containsKey healthEntry.Id)
                "the manifest health-check entry id is a key in the rollup"

        // ── an unkeyed (blank-name) probe is retained, not dropped ────
        testCase "a probe with a blank name is retained under Unkeyed, not dropped"
        <| fun _ ->
            let rollup =
                ComponentHealthRollup.build [
                    probe "keyed" Healthy, Healthy
                    probe "  " (Unhealthy "down"), Unhealthy "down"
                ]

            Expect.equal rollup.ByComponent.Count 1 "only the keyable probe is keyed"

            Expect.equal
                rollup.Unkeyed
                [ "  ", Unhealthy "down" ]
                "the unkeyable probe is retained under Unkeyed rather than dropped"

        // ── run executes each probe and rolls the outcomes up ─────────
        testCase "run executes each probe and keys its live outcome"
        <| fun _ ->
            let rollup =
                ComponentHealthRollup.run [ probe "a" Healthy; probe "b" (Unhealthy "boom") ]
                |> Async.RunSynchronously

            Expect.equal
                (rollup.ByComponent
                 |> Map.tryFind (ComponentId.forCompanionImpl "IHealthCheck" "b"))
                (Some(Unhealthy "boom"))
                "the probe's live Check() outcome is rolled up"

        // ── worst collapses the keyed outcomes to the single worst ────
        testCase "worst reports the single worst keyed outcome"
        <| fun _ ->
            let rollup =
                ComponentHealthRollup.build [
                    probe "a" Healthy, Healthy
                    probe "b" (Degraded "slow"), Degraded "slow"
                    probe "c" (Unhealthy "down"), Unhealthy "down"
                ]

            Expect.equal (ComponentHealthRollup.worst rollup) (Unhealthy "down") "Unhealthy outranks Degraded / Healthy"

        // ── GP 13: an app with no probes rolls up empty ───────────────
        testCase "an app with no health probes rolls up empty"
        <| fun _ ->
            let rollup = ComponentHealthRollup.forApp ServerApp.empty |> Async.RunSynchronously
            Expect.equal rollup ComponentHealthRollup.empty "nothing composed → empty rollup"
            Expect.equal (ComponentHealthRollup.worst rollup) Healthy "an empty rollup is Healthy"
    ]