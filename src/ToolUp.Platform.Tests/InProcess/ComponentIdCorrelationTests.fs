module ToolUp.Platform.Tests.InProcess.ComponentIdCorrelationTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.Platform.Metrics

// ─── Phase 283 — component-id telemetry / audit correlation ──────────
//
// Covers the acceptance shape: a module metric and an audit source label
// each carry the component's stable Phase 279 `ComponentId`; renaming the
// display name preserves id-keyed correlation (when an explicit id is
// declared); and the id dimension is zero-cost when unused — a module
// metric that permits but never emits it renders byte-identical output,
// and re-permitting a definition is a no-op (GP 11 / GP 13).

let private noopLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

let private invoicesMetric: MetricDefinition = {
    Name = "invoices.total"
    Kind = Counter
    Description = "Invoices issued"
    Unit = "1"
    Tags = [ "status" ]
}

/// Compose a single-module app carrying one module metric, under a given
/// display `Name` + explicit `ComponentId` token. Exercises the real
/// `addModule` accumulation (which permits the id dimension on the metric)
/// rather than a re-derivation.
let private appWith (name: string) (declaredId: string) : ServerApp =
    ServerApp.empty
    |> ServerApp.addModule (
        ServerModule.create name
        |> ServerModule.withComponentId declaredId
        |> ServerModule.withMetrics [ invoicesMetric ]
    )

/// Build a sink over the SDK standard metrics + a composed app's module
/// registrations, emit one increment with the supplied tags, and render.
let private renderWith (app: ServerApp) (metricName: string) (tags: Map<string, string>) : string =
    let sink =
        PrometheusMetricsSink(
            MetricsSinkConfig.defaults,
            StandardMetrics.registrations @ app.MetricRegistrations,
            noopLogger
        )

    (sink :> IMetricsSink).Increment(metricName, tags)
    sink.Render()

let tests =
    testList "ComponentIdCorrelation" [

        // ── a module metric carries the stable id when emitted ───────
        testCase "a module metric carries the stable ComponentId dimension"
        <| fun _ ->
            let app = appWith "Orders" "orders-service"
            let id = ServerApp.componentIdForModule "Orders" app |> Option.get

            let tags =
                Map.ofList [ "status", "paid" ] |> ComponentCorrelation.withComponentId id

            let output = renderWith app "toolup.orders.invoices.total" tags

            Expect.stringContains
                output
                "component_id=\"module:orders-service\""
                "stable id emitted as a metric dimension"

            Expect.stringContains output "status=\"paid\"" "the pre-existing declared tag still renders"

        // ── rename the display name → id-keyed correlation preserved ─
        testCase "renaming the display name preserves id-keyed metric correlation"
        <| fun _ ->
            let before = appWith "Orders" "orders-service"
            let after = appWith "Sales" "orders-service"

            let idBefore = ServerApp.componentIdForModule "Orders" before |> Option.get
            let idAfter = ServerApp.componentIdForModule "Sales" after |> Option.get

            Expect.equal idBefore idAfter "the stable id is name-independent across the rename"

            // The metric *namespace* moves with the display name...
            let outBefore =
                renderWith
                    before
                    "toolup.orders.invoices.total"
                    (ComponentCorrelation.withComponentId idBefore Map.empty)

            let outAfter =
                renderWith after "toolup.sales.invoices.total" (ComponentCorrelation.withComponentId idAfter Map.empty)

            Expect.stringContains outBefore "toolup_orders_invoices_total" "pre-rename namespace"
            Expect.stringContains outAfter "toolup_sales_invoices_total" "post-rename namespace"

            // ...but the component_id dimension is identical on both, so a
            // dashboard grouping by component_id joins across the rename.
            Expect.stringContains outBefore "component_id=\"module:orders-service\"" "id dimension pre-rename"
            Expect.stringContains outAfter "component_id=\"module:orders-service\"" "id dimension post-rename"

        // ── audit source label is rename-stable when keyed by the id ─
        testCase "the audit source label is rename-stable when keyed by ComponentId"
        <| fun _ ->
            let before = appWith "Orders" "orders-service"
            let after = appWith "Sales" "orders-service"

            let labelBefore =
                ServerApp.componentIdForModule "Orders" before
                |> Option.get
                |> ComponentCorrelation.auditSourceLabel

            let labelAfter =
                ServerApp.componentIdForModule "Sales" after
                |> Option.get
                |> ComponentCorrelation.auditSourceLabel

            Expect.equal labelBefore "module:orders-service" "audit source labelled by the stable id"
            Expect.equal labelBefore labelAfter "the audit source label survives the display-name rename"

        // ── a name-derived id changes on rename (declare to correlate) ─
        testCase "a name-derived module id changes on rename (explicit id required to correlate)"
        <| fun _ ->
            let named = ServerApp.empty |> ServerApp.addModule (ServerModule.create "Orders")
            let renamed = ServerApp.empty |> ServerApp.addModule (ServerModule.create "Sales")

            let idNamed = ServerApp.componentIdForModule "Orders" named |> Option.get
            let idRenamed = ServerApp.componentIdForModule "Sales" renamed |> Option.get

            Expect.notEqual
                idNamed
                idRenamed
                "without an explicit id, rename changes the id — so correlation needs withComponentId"

        // ── resolver returns None for an unregistered / reserved source ─
        testCase "componentIdForModule is None for an unregistered source"
        <| fun _ ->
            let app = appWith "Orders" "orders-service"

            Expect.isNone
                (ServerApp.componentIdForModule "_platform.audit" app)
                "a reserved / unregistered source resolves to no module id"

        // ── GP 11: permitting but not emitting → byte-identical output ─
        testCase "permitting the id dimension without emitting it renders byte-identical output"
        <| fun _ ->
            // The addModule path (which permits the id dimension) vs. the
            // bare pre-283 definition — neither emitting the id.
            let app = appWith "Orders" "orders-service"

            let permitted =
                PrometheusMetricsSink(MetricsSinkConfig.defaults, app.MetricRegistrations, noopLogger)

            let bare =
                PrometheusMetricsSink(
                    MetricsSinkConfig.defaults,
                    [
                        {
                            Module = Some "Orders"
                            Definition = invoicesMetric
                        }
                    ],
                    noopLogger
                )

            (permitted :> IMetricsSink).Increment("toolup.orders.invoices.total", Map.ofList [ "status", "paid" ])
            (bare :> IMetricsSink).Increment("toolup.orders.invoices.total", Map.ofList [ "status", "paid" ])

            Expect.equal (permitted.Render()) (bare.Render()) "no emission of the id ⇒ identical render (GP 11)"

        // ── GP 13: idempotent permit — already-permitting def unchanged ─
        testCase "permitComponentIdDimension is idempotent and additive"
        <| fun _ ->
            let once = ComponentCorrelation.permitComponentIdDimension invoicesMetric
            let twice = ComponentCorrelation.permitComponentIdDimension once

            Expect.equal once.Tags [ "status"; "component_id" ] "id dimension appended once, existing tags preserved"
            Expect.equal twice once "re-permitting an already-permitting definition is a no-op"
    ]