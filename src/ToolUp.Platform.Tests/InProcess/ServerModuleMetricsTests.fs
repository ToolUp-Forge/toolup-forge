module ToolUp.Platform.Tests.InProcess.ServerModuleMetricsTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.Platform.Metrics

// ─── ServerModule.withMetrics accumulation — Phase 9e tail ──────────
//
// The sink-level auto-namespacing + reserved-prefix rejection is
// covered by `IMetricsSinkContract` (tests 8 + 9). This pack covers
// the composition-root half: `ServerModule.withMetrics` →
// `ServerApp.addModule` accumulation into `ServerApp.MetricRegistrations`
// with `Module = Some moduleName`, and the end-to-end render through a
// `PrometheusMetricsSink` built from the accumulated list.

let private noopLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

let private billingMetric: MetricDefinition = {
    Name = "invoices.total"
    Kind = Counter
    Description = "Invoices issued"
    Unit = "1"
    Tags = [ "status" ]
}

let tests =
    testList "ServerModule.withMetrics" [

        testCase "accumulates a Module=Some registration via addModule"
        <| fun _ ->
            let app =
                ServerApp.empty
                |> ServerApp.addModule (ServerModule.create "Billing" |> ServerModule.withMetrics [ billingMetric ])

            Expect.equal app.MetricRegistrations.Length 1 "one registration accumulated"
            let reg = app.MetricRegistrations.Head
            Expect.equal reg.Module (Some "Billing") "owning module carried as Some"
            Expect.equal reg.Definition.Name "invoices.total" "pre-namespace name unchanged on the registration"

        testCase "accumulated module metric renders auto-namespaced end-to-end"
        <| fun _ ->
            let app =
                ServerApp.empty
                |> ServerApp.addModule (ServerModule.create "Billing" |> ServerModule.withMetrics [ billingMetric ])

            let sink =
                PrometheusMetricsSink(
                    MetricsSinkConfig.defaults,
                    StandardMetrics.registrations @ app.MetricRegistrations,
                    noopLogger
                )

            (sink :> IMetricsSink).Increment("toolup.billing.invoices.total", Map.ofList [ "status", "paid" ])
            let output = sink.Render()

            Expect.stringContains
                output
                "toolup_billing_invoices_total"
                "module metric auto-namespaced to toolup.billing.*"

            Expect.stringContains output "status=\"paid\"" "declared tag rendered"

            Expect.stringContains
                output
                "toolup_requests_total"
                "SDK standard metrics still present alongside module metrics"

        testCase "withMetrics is additive and accumulation is per-module"
        <| fun _ ->
            let billing =
                ServerModule.create "Billing"
                |> ServerModule.withMetrics [ billingMetric ]
                |> ServerModule.withMetrics [
                    {
                        billingMetric with
                            Name = "refunds.total"
                    }
                ]

            Expect.equal billing.MetricDefinitions.Length 2 "two definitions appended across two withMetrics calls"

            let app =
                ServerApp.empty
                |> ServerApp.addModule billing
                |> ServerApp.addModule (
                    ServerModule.create "Reporting"
                    |> ServerModule.withMetrics [
                        {
                            billingMetric with
                                Name = "exports.total"
                        }
                    ]
                )

            Expect.equal app.MetricRegistrations.Length 3 "three registrations across two modules"

            let owners =
                app.MetricRegistrations |> List.map _.Module |> List.distinct |> List.sort

            Expect.equal owners [ Some "Billing"; Some "Reporting" ] "each registration tagged with its owning module"

        testCase "a module declaring no metrics contributes nothing"
        <| fun _ ->
            let app = ServerApp.empty |> ServerApp.addModule (ServerModule.create "Plain")

            Expect.isEmpty app.MetricRegistrations "no withMetrics call ⇒ no accumulated registrations"
    ]