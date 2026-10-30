// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── FactQueryTool (Phase 559) ───────────────────────────────────────
//
// The ServerResident `query_facts` AI tool — the fact base goes
// conversational. The model asks the store a (subject, metric, period)
// question mid-turn; the executor runs the scope-resolved
// `IFactStore.Query` and projects each returned fact to a typed result
// carrying its fact id, the canonical rendering (`FactRendering.renderFor`
// under the metric registry's declaration), derived freshness
// (`Freshness.derive` — never stored), a supersession pointer when a
// later assertion corrected it, the method identity, and (Phase 994) the
// content hashes of the inputs it was computed from (`InputHash.named`) —
// never raw store internals (no value hashes or run tokens, no disclosure
// classification, no raw value DU).
//
// **Disclosure at the door (Phase 525).** Every fact the query returns
// passes the `IFactDisclosureGate` at the `FactToolResult` surface
// before projection. A denied fact projects to the typed
// not-disclosable marker — fact id + policy ref + the canonical refusal
// wording (`FactDisclosureVerdict.refusalText`) — never the value,
// never a silent omission: the model can explain *that* a value exists
// but is restricted, and every deny is audited by the gate (GP 6 /
// 525.E). The gate re-resolves each id through the scope-filtered
// store, so even a store implementation that leaked a cross-scope fact
// into query results is caught at the door: an id the scope cannot see
// is denied (`"unknown-fact"`), not disclosed.
//
// Scope isolation is structural (GP 4): the executor forwards the
// caller's resolved storage scope to both the store query and the gate,
// so another tenant's facts are unreachable, and disclosure never
// widens scope.
//
// **Registration (GP 13).** `FactsCompose.withFactStore` declares the
// tool on `ServerApp.AITools` when `EnabledFactStore`; the declaration
// reaches the AI tool registry only when the AI companion composes
// (`composeAI` reads the accumulated `AITools`). A deployment without
// the fact store never declares the tool; one without AI never builds a
// registry — without either, nothing arms.

/// The `query_facts` AI tool: definition + executor over the composed
/// `IFactStore` / `IFactDisclosureGate`. Declared by
/// `FactsCompose.withFactStore`; consumers normally never call this
/// module directly.
module FactQueryTool =

    // ── Tool definition (559.A / 559.C) ───────────────────────────

    /// The `query_facts` tool declaration. `SourceModule` is the fact
    /// store's reserved `_facts` platform source (not a consumer
    /// module); `Surface = Both` — a verified number is as useful in
    /// the side panel as in the full-page surface.
    let definition: AIToolDefinition = {
        Name = "query_facts"
        Description =
            "Query the deployment's verified fact base for (subject, metric, period) assertions — the numbers an answer should quote verbatim. Each returned fact carries its stable fact id, the canonical rendering of its value (quote it exactly — do not recompute or reformat), its derived freshness (Fresh, or Stale with the instant it went stale), a supersession pointer when a later assertion corrected it, and the identity of the method that produced it. Facts that exist but are restricted come back in a separate `withheld` list as typed markers naming the restricting policy — you may tell the user such a value exists and why it is withheld, but you never receive the value. The subject-hierarchy and metric ids come from the deployment's grounding registry (the subjects and metrics its modules declared); use ids given in the conversation or system context — there is no discovery/listing parameter on this tool. Results are scoped to the current user/team."
        Parameters = [
            {
                Name = "subject_hierarchy"
                Type = "string"
                Description = "Stable id of the registered subject hierarchy the question is about (e.g. \"brand\")."
                Required = true
                Default = None
            }
            {
                Name = "subject_path"
                Type = "string"
                Description =
                    "The subject instance as ordered member ids from the hierarchy root, separated by \">\" (e.g. \"acme>widget-x\" under a brand>sku hierarchy). Omit for the hierarchy-root roll-up."
                Required = false
                Default = None
            }
            {
                Name = "metric"
                Type = "string"
                Description = "Registered metric id the facts assert a value for (e.g. \"revenue\")."
                Required = true
                Default = None
            }
            {
                Name = "period_from"
                Type = "string"
                Description =
                    "Optional ISO-8601 UTC instant — restrict to facts whose valid-time period overlaps [period_from, period_to). Either bound may be given alone for an open-ended window."
                Required = false
                Default = None
            }
            {
                Name = "period_to"
                Type = "string"
                Description = "Optional ISO-8601 UTC instant — the exclusive upper bound of the period window."
                Required = false
                Default = None
            }
            {
                Name = "as_of"
                Type = "string"
                Description =
                    "Optional ISO-8601 UTC instant — reconstruct the fact base as it was known at this transaction time (\"what did we believe then?\"). Omit for the current view."
                Required = false
                Default = None
            }
            {
                Name = "include_superseded"
                Type = "boolean"
                Description =
                    "Include superseded facts (the full correction history per lineage) instead of only the current heads. Defaults to false."
                Required = false
                Default = Some "false"
            }
        ]
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = ToolEffectDeclaration.readFacts
    }

    // ── Argument parsing + validation (559.A / 559.D) ─────────────

    type private QueryArgs = {
        Subject: SubjectRef
        Metric: string
        PeriodOverlaps: TemporalExtent option
        AsOf: DateTime option
        IncludeSuperseded: bool
    }

    let private parseArgs (argsJson: string) : Result<QueryArgs, string> =
        match FactTool.parseObject argsJson with
        | Error e -> Error e
        | Ok root ->
            let required =
                FactTool.requiredArg "Subject and metric ids come from the deployment's grounding registry." root

            match required "subject_hierarchy" with
            | Error e -> Error e
            | Ok hierarchy ->

                match required "metric" with
                | Error e -> Error e
                | Ok metric ->

                    match FactTool.stringArg root "subject_path" with
                    | Error e -> Error e
                    | Ok pathArg ->
                        let path = pathArg |> Option.map FactTool.pathSegments |> Option.defaultValue []

                        match FactTool.periodArg root with
                        | Error e -> Error e
                        | Ok periodOverlaps ->

                            match FactTool.instantArg root "as_of" with
                            | Error e -> Error e
                            | Ok asOf ->

                                match FactTool.boolArg root "include_superseded" with
                                | Error e -> Error e
                                | Ok includeSuperseded ->
                                    Ok {
                                        Subject = { Hierarchy = hierarchy; Path = path }
                                        Metric = metric
                                        PeriodOverlaps = periodOverlaps
                                        AsOf = asOf
                                        IncludeSuperseded = includeSuperseded |> Option.defaultValue false
                                    }

    // ── Execution (559.A + 559.B) ─────────────────────────────────

    /// The executor over explicit dependencies — the testable core
    /// `execute` adapts `HttpContext` onto. `registry` supplies each
    /// metric's display format + staleness policy (Phase 519); `None`
    /// renders verbatim and derives freshness under the
    /// `UntilSuperseded` default. Every returned fact passes `gate` at
    /// the `FactToolResult` surface; denied facts project to the typed
    /// withheld marker, never the value, never a silent omission.
    let executeWith
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (clock: unit -> DateTime)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        async {
            match parseArgs argsJson with
            | Error message -> return FactTool.serialize {| error = message |}
            | Ok args ->
                let query: FactQuery = {
                    SubjectBranch = None
                    Subject = Some args.Subject
                    Metric = Some(MetricRef args.Metric)
                    PeriodOverlaps = args.PeriodOverlaps
                    Method = None
                    AsOf = args.AsOf
                    IncludeSuperseded = args.IncludeSuperseded
                }

                let! facts = store.Query(scope, query)

                // Disclosure at the door (559.B): one gate check over
                // every returned id at the FactToolResult surface. The
                // gate audits each deny (GP 6).
                let! verdicts = gate.Check(scope, principal, FactToolResult, facts |> List.map _.FactId)

                // An id the gate returned no verdict for is denied,
                // conservatively — the door never fails open.
                let verdictFor (factId: string) =
                    verdicts
                    |> Map.tryFind factId
                    |> Option.defaultValue (FactNotDisclosable "unknown-fact")

                let disclosable, withheld =
                    facts
                    |> List.map (fun fact -> fact, verdictFor fact.FactId)
                    |> List.partition (fun (_, verdict) -> verdict = FactDisclosable)

                // The typed not-disclosable marker (Phase 525 vocabulary):
                // fact id + policy ref + the canonical refusal wording —
                // never the value.
                let withheldMarkers =
                    withheld
                    |> List.map (fun (fact, verdict) ->
                        let policyRef =
                            match verdict with
                            | FactNotDisclosable policyRef -> policyRef
                            | FactDisclosable -> "unknown-fact" // unreachable — partitioned above

                        {|
                            factId = fact.FactId
                            policyRef = policyRef
                            status = FactDisclosureVerdict.refusalText policyRef
                        |})

                let metricDef = registry |> Option.bind (fun r -> r.TryGetMetric args.Metric)

                let policy =
                    metricDef
                    |> Option.map _.Staleness
                    |> Option.defaultValue Grounding.UntilSuperseded

                let now = clock().ToUniversalTime()

                let! projected =
                    disclosable
                    |> List.map (fun (fact, _) -> async {
                        // The fact's successor, if a later assertion
                        // superseded it (an AsOf reconstruction, or an
                        // include_superseded history read).
                        let! chain = store.QuerySupersessionChain(scope, fact.FactId)

                        let successor = chain |> List.tryFind (fun g -> g.Supersedes = Some fact.FactId)

                        let freshness, staleSince =
                            match Freshness.derive policy fact successor.IsNone now with
                            | Fresh -> "Fresh", None
                            | Stale since -> "Stale", Some(since.ToUniversalTime().ToString "o")

                        return {|
                            factId = fact.FactId
                            subject = SubjectRef.toString fact.Subject
                            metric = fact.Metric.Value
                            rendering = FactRendering.renderFor metricDef fact.Value
                            periodFrom = fact.Period.From.ToUniversalTime().ToString "o"
                            periodTo = fact.Period.To.ToUniversalTime().ToString "o"
                            periodLabel = fact.Period.Label
                            asOf = fact.AsOf.ToUniversalTime().ToString "o"
                            freshness = freshness
                            staleSince = staleSince
                            supersededBy = successor |> Option.map _.FactId
                            method = Fact.methodIdentity fact.Method
                            // Phase 994 — the inputs the fact was computed
                            // from, by content hash, so a cited number
                            // reaches its source in one hop.
                            inputs = InputHash.named fact.Evidence
                        |}
                    })
                    |> Async.Sequential

                return
                    FactTool.serialize {|
                        facts = Array.toList projected
                        withheld = withheldMarkers
                    |}
        }

    // ── HttpContext adapter ───────────────────────────────────────

    /// The registered `HttpContext` executor.
    let execute (ctx: HttpContext) (argsJson: string) : Async<string> =
        FactTool.define
            "query_facts"
            (fun c argsJson ->
                executeWith c.Store c.Gate c.Registry (fun () -> DateTime.UtcNow) c.Scope c.UserId argsJson)
            ctx
            argsJson