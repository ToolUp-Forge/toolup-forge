# Grounded narratives — model-written reports whose every number is a Fact

A deployment with a fact tier can ask the model for a narrative report — a daily trading summary, a
period review — and **publish it only if every number in it is a reference to a Fact**. A model can
state a figure that is plausible, well formatted and wrong; a grounded narrative cannot reach a
reader that way, because the narrative is refused before publication and the refusal is logged.

Four parts take part, and they meet only through the seams in `ToolUp.Platform.Server`
(`GroundedNarrative.fs`), so no package references the AI, fact and reporting tiers together:

| Part | Lives in | What it does |
|---|---|---|
| The grounding gate | `ToolUp.Facts.Server` (`FactNarrativeGroundingGate`) | Checks a `NarrativeDocument`; certifies a published one |
| The generation run | `ToolUp.AI.Server` (`GroundedNarrativeRun`) | Runs the agent loop over the fact tools only, assembles, checks, publishes |
| The scheduled trigger | `ToolUp.Reporting.Server` (`GroundedNarrativeProducer`) | A grounded run as a report producer |
| The data-arrival trigger | `ToolUp.Facts.Server` (`GroundedNarrativeTrigger`) | Enqueues a run when the facts it depends on are invalidated |

## The gate

`INarrativeGroundingGate.Check` refuses a document when any numeric claim in its **content** is not
a `Metric` span whose reference:

1. **resolves** — a Fact id the scope holds, or `<kind>:<id>` for a kind the deployment registered
   as an `ICitableReferenceKind`; an unregistered kind is refused by name;
2. **may be disclosed** at the publishing surface (the Phase 525 predicate, through
   `IFactDisclosureGate` — the deny is audited like every other door's);
3. **is current** — the head of its lineage, never a superseded Fact;
4. **states its value** — compared by number at the precision stated, so `1.25m` for 1,248,300
   passes and `1.3m` does not. A verbatim rendering always passes.

A numeral anywhere in prose is a claim (any Unicode number character), and so is a number in words
(`twelve`, `million`, `doubled`; the list is `GroundingPolicy.NumberWords`). A metric's label is
prose, too — only its value may carry a figure. Every element is checked recursively: tables cell
by cell, lists, captions, alt text, code. A `Component` block's props are prose, except `factRefs`
— a comma-separated list of references a chart draws from, checked as citations. The document
title, subtitle and section headings are structure the deployment authors, and are not inspected;
the grounded run never lets the model write them.

A refusal is `Ungrounded` with **every** offending claim, in document order:

| Reason | When |
|---|---|
| `UnreferencedNumber` | a figure in prose, or in a metric's label |
| `MetricWithoutReference` | a metric span with no reference |
| `UnresolvedReference` | the reference names nothing the scope holds |
| `SupersededReference` | the Fact has a newer head (named) |
| `UndisclosableReference` | the surface may not disclose it (policy named, value never) |
| `MisstatedValue` | the stated value is not the reference's value |
| `UnregisteredReferenceKind` | `<kind>:` names a kind nobody registered |

**A percentage computed from two Facts is refused.** As prose it is an unreferenced number; as a
span citing one of its inputs it misstates that input. To publish a derived figure, assert it as a
Fact first (a `Computed` method naming its inputs): it then has an id, a lineage, a disclosure stance
and a supersession edge — everything the gate checks. Admitting "a declared derivation of named
Facts" instead would put a second arithmetic engine inside the gate, whose formula and rounding a
reader would have to trust with no Fact to trace it to.

## Declared citable references

A narrative may cite dated references other than Facts — a calendar event, a published price list.
Register one `ICitableReferenceKind` per kind; a span cites it as `<kind>:<id>`:

```fsharp skip=fragment
let calendar =
    { new ICitableReferenceKind with
        member _.Kind = "calendar"
        member _.Resolve(scopeId, principal, id) = async {
            match! events.TryGet(scopeId, id) with
            | Some e -> return CitableReferenceCurrent([ e.Date.ToString "d MMM" ], None)
            | None -> return CitableReferenceUnresolved
        } }

services.AddSingleton<ICitableReferenceKind>(calendar)
```

The fact tier's gate picks up every registered kind. Give the model a tool to look the references
up by adding it to the run's tools.

## The run

A deployment registers runs; neither trigger names a report.

```fsharp skip=fragment
let tradingSummary =
    GroundedNarrativeDefinition.create
        "daily-trading-summary"            // the key both triggers address
        "Daily trading summary"
        "trading"                          // the module the narrative is attributed to
        "narrator"                         // the principal it reads and publishes as
        "Daily trading summary"            // the title — the model never writes it
        "Summarise yesterday's trading by brand."
        [ { Id = "summary"; Heading = "Summary"; Guidance = "Revenue and units by brand." } ]
    |> GroundedNarrativeDefinition.dependingOn [ "revenue"; "units" ]
    |> GroundedNarrativeDefinition.withProjection (fun scopeId -> async {
        // Tables and charts are projected deterministically from Facts —
        // NarrativeFromData's fact-bearing grids — never written by the model.
        let! grid = brandRevenueGrid scopeId
        return Ok [ "summary", [ grid ] ] })

RAGServerApp.create factory providerProfile embedder
|> RAGServerApp.withFacts FactsCompose.withFactTier
|> composeRAG
|> GroundedNarratives.compose
    (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools)
    [ tradingSummary ]
```

`IGroundedNarrativeRun.Run` takes the run key and a **resolved** scope. The model is offered exactly
the tools passed in — the fact tier's `NarrativeGrounding.factTools` (`list_metric_coverage`,
`query_facts`, `query_metric_population`) plus any reference tools — and answers in a fixed format:

```json
{"sections":[{"id":"summary","paragraphs":["Acme revenue was [[Revenue|1,250|<fact id>]] yesterday."]}]}
```

Each `[[label|value|reference]]` becomes a `Metric` span. The run appends the projections, stamps
provenance, and checks the document at the run's surface (`FactNarrativePublication` by default).
Then:

- **Grounded** — published through `NarrativePublisher.publishForScope` (tagged `grounded`), indexed
  into the knowledge base through `INarrativeIngestor` when one is composed, and certified: the
  gate issues a grounding certificate rooted at the narrative (a `NarrativeDocument` node) with a
  `CitesFact` edge to every cited Fact, verifiable offline. Because the narrative's spans carry
  their Fact ids, superseding a cited Fact flags it stale through
  `NarrativeSupersession.findStaleNarratives` (the Phase 521 join).
- **Ungrounded** — `GroundedNarrativeRefused` with every offence; nothing is published or indexed.
- **Anything else** (no gate composed, no provider, the anonymous scope, output not in the format)
  — `GroundedNarrativeFailed`; nothing is published.

Every outcome writes one audit row, source `_narratives`: `GroundedNarrativePublished`,
`GroundedNarrativeRefused` (naming each offence) or `GroundedNarrativeFailed`.

**Why the scope must be resolved.** The model reads facts through the request-path fact tools,
which take only a scope the platform resolved (Phase 797). A run is handed one — a job passes its
`JobContext.Scope` — and the anonymous scope is refused rather than read.

## The triggers

Both triggers are live, and each runs the narrative under a scope the platform resolved — never one
it was told.

| Trigger | Runs under |
|---|---|
| Scheduled report | the scope of the request that created the subscription, re-minted by the scheduler on every run |
| Data arrival | the scope of the request whose write invalidated the facts |

**Scheduled report.** Register a producer over the run; the narrative fills a template placeholder
as a `NarrativeValue`, so the render goes through the ordinary pipeline and its export door. A
refused narrative is the producer's `Error`, recorded on the subscription's last-run outcome.

```fsharp skip=fragment
GroundedNarrativeProducer.create
    "daily-trading-summary" "Daily trading summary"
    run "daily-trading-summary" "trading-summary-template" "body"
|> producers.Register
```

Mount the subscription API with the scope the platform resolved for the managing request:

```fsharp skip=fragment
ReportSubscriptionApiHandler.createUnder apiDeps principal (ScopeResolution.forRequest ctx) scopeId
```

Saving a subscription then schedules its job through the typed `IJobScheduler.Schedule(scope, …)`
overload: the scheduler persists a token the platform's `ScopeCarrier` issued for that job (Phase
935), and every run re-mints the scope from it onto `JobContext.Scope`, which the subscription job
hands to the producer on the async chain (`ReportProducerScope`). So a subscription created by a
request resolved to a team produces its narrative from that team's Facts, publishes it to that
team's narrative store, and certifies it there; a subscription created for another team runs under
that team's scope and can read none of the first team's Facts.

The scope is carried only when it names the shard the subscription is stored in. A subscription
created under the anonymous scope, through `ReportSubscriptionApiHandler.create` (which carries no
request scope), or with a resolved scope for some other shard runs **anonymous**, and the grounded
producer refuses it rather than run over the anonymous shard. Producers that read no Facts are
unaffected either way.

**When the scope can no longer be re-minted, the run fails closed and says why.** A token issued
over a key ring the deployment no longer holds, a scheduler restarted with no carrier bound to the
deployment's ring, or a job first scheduled before subscriptions carried their scope (a re-save does
not re-stamp a job its idempotency key recovers) all run under the anonymous scope, read nothing
scoped, and record a last-run failure carrying
`ReportSubscriptionJobHandler.ScopeNotReMinted`. Re-create the subscription from a request resolved
to the scope it reports on.

**Data arrival.** Where the fact tier is composed, the reactive data-change hook also enqueues every
registered run whose `DependsOnMetrics` include the metric of a fact the change invalidated. Only a
change made under a resolved scope enqueues a run (the job is scheduled under it); a carried change
— a boot seed, an import job's write — has no resolved scope to give the run, and enqueues nothing.
A metric whose recompute policy is `Eager` recomputes in its own job; the narrative job waits for
it (a transient failure, backed off by its retry policy), and on its last attempt runs regardless.

## In conversation

The same fact tools answer the assistant in a chat turn, under the scope the platform resolved for
the chat request. The turn's agent loop runs on a background context built from the request, which
carries the request's `ResolvedScope` (`ScopeResolution.carry`); a request the platform resolved no
scope for reads the anonymous shard, exactly as the request itself would.

## Tests

[`GroundedNarrativeTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/GroundedNarrativeTests.fs)
tries to defeat the gate claim by claim, runs the fact-tier gate and certificate over a real store,
drives the run end to end on a RAG app composed with `RAGServerApp.withFacts FactsCompose.withFactTier`
(a scripted model that reads a fact, then writes each defeat case — every one refused, unpublished
and logged), and exercises both triggers.
[`ReportSubscriptionScopeTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/ReportSubscriptionScopeTests.fs)
runs subscriptions on the shipped in-process scheduler: each runs under the scope that created it,
an anonymous or foreign scope is never carried, a token that no longer redeems fails closed with
its reason, and the scheduled grounded report runs end to end under its team's scope and reads none
of another team's Facts.
[`AssistantFactScopeTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/AssistantFactScopeTests.fs)
drives a chat turn whose `query_facts` call reads the requesting team's Fact and no other.
