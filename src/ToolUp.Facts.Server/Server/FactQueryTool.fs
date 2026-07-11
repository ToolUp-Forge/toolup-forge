// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── FactQueryTool (Phase 559) ───────────────────────────────────────
//
// The ServerResident `query_facts` AI tool — the fact base goes
// conversational. The model asks the store a (subject, metric, period)
// question mid-turn; the executor runs the scope-resolved
// `IFactStore.Query` and projects each returned fact to a typed result
// carrying its fact id, the canonical rendering (`FactRendering.render`
// under the metric registry's `DisplayFormat`), derived freshness
// (`Freshness.derive` — never stored), a supersession pointer when a
// later assertion corrected it, and the method identity — never raw
// store internals (no evidence hashes, no disclosure classification, no
// raw value DU).
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

    let private jsonOptions = FableConverters.create ()

    let private serialize (value: obj) : string =
        JsonSerializer.Serialize(value, jsonOptions)

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
    }

    // ── Argument parsing + validation (559.A / 559.D) ─────────────

    type private QueryArgs = {
        Subject: SubjectRef
        Metric: string
        PeriodOverlaps: TemporalExtent option
        AsOf: DateTime option
        IncludeSuperseded: bool
    }

    let private tryProperty (root: JsonElement) (name: string) : JsonElement option =
        match root.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
        | _ -> None

    /// A required / optional string argument; `Error` when present with a
    /// non-string shape.
    let private stringArg (root: JsonElement) (name: string) : Result<string option, string> =
        match tryProperty root name with
        | None -> Ok None
        | Some v when v.ValueKind = JsonValueKind.String -> Ok(Some(v.GetString()))
        | Some _ -> Error(sprintf "Argument '%s' must be a string." name)

    let private boolArg (root: JsonElement) (name: string) : Result<bool option, string> =
        match tryProperty root name with
        | None -> Ok None
        | Some v when v.ValueKind = JsonValueKind.True -> Ok(Some true)
        | Some v when v.ValueKind = JsonValueKind.False -> Ok(Some false)
        | Some _ -> Error(sprintf "Argument '%s' must be a boolean." name)

    let private instantArg (root: JsonElement) (name: string) : Result<DateTime option, string> =
        match stringArg root name with
        | Error e -> Error e
        | Ok None -> Ok None
        | Ok(Some s) when String.IsNullOrWhiteSpace s -> Ok None
        | Ok(Some s) ->
            match
                DateTime.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )
            with
            | true, t -> Ok(Some t)
            | false, _ -> Error(sprintf "Argument '%s' is not a parseable ISO-8601 instant: '%s'." name s)

    let private parseArgs (argsJson: string) : Result<QueryArgs, string> =
        let parsed =
            try
                Ok((JsonDocument.Parse argsJson).RootElement.Clone())
            with _ ->
                Error "Arguments are not valid JSON."

        match parsed with
        | Error e -> Error e
        | Ok root when root.ValueKind <> JsonValueKind.Object -> Error "Arguments must be a JSON object."
        | Ok root ->
            let required name =
                match stringArg root name with
                | Ok(Some s) when not (String.IsNullOrWhiteSpace s) -> Ok s
                | Ok _ ->
                    Error(
                        sprintf
                            "Required argument '%s' is missing. Subject and metric ids come from the deployment's grounding registry."
                            name
                    )
                | Error e -> Error e

            match required "subject_hierarchy" with
            | Error e -> Error e
            | Ok hierarchy ->

                match required "metric" with
                | Error e -> Error e
                | Ok metric ->

                    match stringArg root "subject_path" with
                    | Error e -> Error e
                    | Ok pathArg ->
                        let path =
                            match pathArg with
                            | None -> []
                            | Some s ->
                                s.Split('>')
                                |> Array.map _.Trim()
                                |> Array.filter (fun seg -> seg <> "")
                                |> Array.toList

                        match instantArg root "period_from", instantArg root "period_to" with
                        | Error e, _
                        | _, Error e -> Error e
                        | Ok from, Ok to' ->

                            let period =
                                match from, to' with
                                | None, None -> Ok None
                                | Some f, Some t when f >= t ->
                                    Error "Argument 'period_from' must be strictly before 'period_to'."
                                | f, t ->
                                    Ok(
                                        Some {
                                            From = f |> Option.defaultValue DateTime.MinValue
                                            To = t |> Option.defaultValue DateTime.MaxValue
                                            Label = None
                                        }
                                    )

                            match period with
                            | Error e -> Error e
                            | Ok periodOverlaps ->

                                match instantArg root "as_of" with
                                | Error e -> Error e
                                | Ok asOf ->

                                    match boolArg root "include_superseded" with
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
        (scopeId: string)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        async {
            match parseArgs argsJson with
            | Error message -> return serialize {| error = message |}
            | Ok args ->
                let query: FactQuery = {
                    Subject = Some args.Subject
                    Metric = Some(MetricRef args.Metric)
                    PeriodOverlaps = args.PeriodOverlaps
                    Method = None
                    AsOf = args.AsOf
                    IncludeSuperseded = args.IncludeSuperseded
                }

                let! facts = store.Query(scopeId, query)

                // Disclosure at the door (559.B): one gate check over
                // every returned id at the FactToolResult surface. The
                // gate audits each deny (GP 6).
                let! verdicts = gate.Check(scopeId, principal, FactToolResult, facts |> List.map _.FactId)

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

                let displayFormat =
                    metricDef |> Option.map _.DisplayFormat |> Option.defaultValue ""

                let now = clock().ToUniversalTime()

                let! projected =
                    disclosable
                    |> List.map (fun (fact, _) -> async {
                        // The fact's successor, if a later assertion
                        // superseded it (an AsOf reconstruction, or an
                        // include_superseded history read).
                        let! chain = store.QuerySupersessionChain(scopeId, fact.FactId)

                        let successor = chain |> List.tryFind (fun g -> g.Supersedes = Some fact.FactId)

                        let freshness, staleSince =
                            match Freshness.derive policy fact successor.IsNone now with
                            | Fresh -> "Fresh", None
                            | Stale since -> "Stale", Some(since.ToUniversalTime().ToString "o")

                        return {|
                            factId = fact.FactId
                            subject = SubjectRef.toString fact.Subject
                            metric = fact.Metric.Value
                            rendering = FactRendering.render displayFormat fact.Value
                            periodFrom = fact.Period.From.ToUniversalTime().ToString "o"
                            periodTo = fact.Period.To.ToUniversalTime().ToString "o"
                            periodLabel = fact.Period.Label
                            asOf = fact.AsOf.ToUniversalTime().ToString "o"
                            freshness = freshness
                            staleSince = staleSince
                            supersededBy = successor |> Option.map _.FactId
                            method = Fact.methodIdentity fact.Method
                        |}
                    })
                    |> Async.Sequential

                return
                    serialize {|
                        facts = Array.toList projected
                        withheld = withheldMarkers
                    |}
        }

    // ── HttpContext adapter (the registered executor) ─────────────

    /// The caller's resolved storage-scope id — the same tenant boundary
    /// the store shards by and the gate resolves fact ids within (GP 4).
    let private scopeIdOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as scope) -> scope.ScopeId
        | _ ->
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

    let private userIdOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    let private serviceOf<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
        match ctx.RequestServices.GetService(typeof<'T>) with
        | :? 'T as service -> Some service
        | _ -> None

    /// The registered `HttpContext` executor. Resolves the fact store,
    /// the disclosure gate, and the optional metric registry from DI —
    /// all registered by the same `FactsCompose.withFactStore` knob that
    /// declares this tool, so a composed deployment always finds them.
    let execute (ctx: HttpContext) (argsJson: string) : Async<string> =
        match serviceOf<IFactStore> ctx, serviceOf<IFactDisclosureGate> ctx with
        | Some store, Some gate ->
            let registry = serviceOf<Grounding.IMetricRegistry> ctx

            executeWith store gate registry (fun () -> DateTime.UtcNow) (scopeIdOf ctx) (userIdOf ctx) argsJson
        | _ -> async {
            return
                serialize {|
                    error = "The fact store is not composed in this deployment — `query_facts` is unavailable."
                |}
          }