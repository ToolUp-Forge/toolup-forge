namespace ToolUp.Reporting

open ToolUp.Platform

// ─── Phase 534.C — the operator view ─────────────────────────────────
//
// "Which subscriptions exist at this scope, and is each one working?"
// answered without reading the composition root or querying the job
// store — the `/dev/inspect` panel an operator already looks at, the
// same surface `AlgorithmCatalogContributor` uses for the same purpose.
//
// It deliberately carries the LAST-RUN OUTCOME verbatim, including the
// failure reason. A subscription that silently stopped delivering is
// the failure mode this whole substrate has to be answerable about, and
// "when did it last work, and what did it say when it stopped" is the
// question an operator actually arrives with.
//
// No recipient identity leaves this panel — recipients appear as a
// count, never as user ids. The panel is a diagnostics surface, not an
// export door, and a `/dev/inspect` page is not the place a scope's
// membership should become readable.

/// `IDevDiagnosticsContributor` over the subscriptions at one scope,
/// plus the registered producer set. Registered as a DI singleton by
/// `ReportingCompose.withReportSubscriptions`.
type ReportSubscriptionsContributor
    (subscriptions: IReportSubscriptionStore, producers: ReportProducerRegistry, scopeId: string) =

    interface IDevDiagnosticsContributor with

        member _.Contribute() = async {
            let! records = subscriptions.List scopeId

            let renderLastRun =
                function
                | NeverRun -> {|
                    state = "never-run"
                    at = None
                    detail = None
                    deliveredTo = None
                  |}
                | RunSucceeded(at, key, version, deliveredTo) -> {|
                    state = "succeeded"
                    at = Some(at.ToString "o")
                    detail = Some $"{key} (version {version})"
                    deliveredTo = Some deliveredTo
                  |}
                | RunFailed(at, reason, terminal) -> {|
                    state = (if terminal then "failed-terminal" else "failed-retrying")
                    at = Some(at.ToString "o")
                    detail = Some reason
                    deliveredTo = None
                  |}

            let payload = {|
                scope = scopeId
                producers =
                    producers.Descriptors
                    |> List.map (fun d -> {|
                        key = d.Key
                        displayName = d.DisplayName
                        parameters = d.Parameters |> List.map _.Key
                        formats = d.Formats |> List.map (sprintf "%A")
                    |})
                count = List.length records
                enabled = records |> List.filter _.Enabled |> List.length
                subscriptions =
                    records
                    |> List.map (fun s -> {|
                        id = s.Id
                        displayName = s.DisplayName
                        producerKey = s.ProducerKey
                        schedule = s.Schedule
                        format = sprintf "%A" s.Format
                        enabled = s.Enabled
                        // A count, never the ids — see the header.
                        recipients = List.length s.RecipientUserIds
                        lastRun = renderLastRun s.LastRun
                    |})
            |}

            return "Report subscriptions", box payload
        }