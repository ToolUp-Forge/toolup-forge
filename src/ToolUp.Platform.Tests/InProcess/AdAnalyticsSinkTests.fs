module ToolUp.Platform.Tests.InProcess.AdAnalyticsSinkTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.AdPanel
open ToolUp.Platform.Tests.Contracts

// ─── Phase 60 — IAdAnalyticsSink in-process binding ──────────────────
//
// Two test lists exported:
//   * `tests` — drives the portable `IAdAnalyticsSinkContract` pack
//     against `RecordingAdAnalyticsSink` (an in-memory fake recording
//     every impression / click for the verifyDelivered callback).
//   * `noOpTests` — drives the same pack against
//     `NoOpAdAnalyticsSink` (the SDK's default-on impl) with a
//     verifyDelivered that asserts nothing was recorded — the
//     no-op contract is "accept the call, drop it on the floor".
//
// `ServerSinkAdAnalytics` ships in `ToolUp.Platform.Client` and runs
// only under Fable (`Fable.SimpleHttp`); it is not bound here. A
// future Server-side sink companion (if any) would bind to the same
// pack from its own InProcess file.

// ─── Recording fake ──────────────────────────────────────────────────

type private RecordingAdAnalyticsSink() =
    let impressions = ResizeArray<AdImpression>()
    let clicks = ResizeArray<AdClick>()

    member _.Impressions = impressions |> List.ofSeq
    member _.Clicks = clicks |> List.ofSeq

    interface IAdAnalyticsSink with
        member _.LogImpression(event: AdImpression) = async {
            impressions.Add event
            return ()
        }

        member _.LogClick(event: AdClick) = async {
            clicks.Add event
            return ()
        }

// ─── Portable contract binding — recording fake ──────────────────────

let tests =
    let factory () =
        RecordingAdAnalyticsSink() :> IAdAnalyticsSink

    let verifyDelivered
        (sink: IAdAnalyticsSink)
        (expectedImpressions: AdImpression list)
        (expectedClicks: AdClick list)
        =
        match sink with
        | :? RecordingAdAnalyticsSink as recording ->
            Expect.equal
                recording.Impressions
                expectedImpressions
                "recording fake's impression log must match the events the contract pack logged"

            Expect.equal
                recording.Clicks
                expectedClicks
                "recording fake's click log must match the events the contract pack logged"
        | _ -> failtest "verifyDelivered called with a non-recording sink — binding misconfigured"

    IAdAnalyticsSinkContract.tests "RecordingAdAnalyticsSink" factory verifyDelivered

// ─── Portable contract binding — no-op default ───────────────────────

let noOpTests =
    let factory () =
        NoOpAdAnalyticsSink() :> IAdAnalyticsSink

    // The no-op contract is "swallow every event silently" — the
    // verifyDelivered callback asserts nothing was retained. The
    // contract pack's positive-shape tests still pass because they
    // assert that `LogImpression` / `LogClick` complete without
    // throwing, not that a downstream record exists.
    let verifyDelivered (_sink: IAdAnalyticsSink) (_imps: AdImpression list) (_clicks: AdClick list) = ()

    IAdAnalyticsSinkContract.tests "NoOpAdAnalyticsSink" factory verifyDelivered