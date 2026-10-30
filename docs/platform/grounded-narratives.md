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

The value a number must hold to be emitted is a **`GroundedRead`** whose `Verdict` is
`FactDisclosable`: the disclosure gate's one answer per fact (`FactDisclosureGate.Read`), carrying
what the verdict was decided from — the fact's own classification, the purpose, the taint walk of
its derivation and the declassification budget. Condition 2 is that read's verdict, asked through
`IFactDisclosureGate.Check`, which is its projection; no door re-derives it (see the fact store
guide, "Four stages, one read, and an index the writes keep").

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
        // Tables are projected deterministically from Facts —
        // NarrativeFromData's fact-bearing grids — never written by the model
        // (see "Projections" below for the context form and requested tables).
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
the tools passed in — the fact tier's `NarrativeGrounding.factTools` (`query_facts`,
`query_metric_population`, `list_metric_coverage`, `list_fact_tables`) plus any reference tools — and answers in a fixed format:

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
`GroundedNarrativeRefused` (naming each offence) or `GroundedNarrativeFailed` (whose outcome field
also tells `unfunded` and `over-budget` apart).

**The run's call budget (Phase 1109).** No client waits on a run, so it is not held to the
interactive assistant's 50-second call cap. Every model call the run makes runs under
`GroundedNarrativeRunOptions.CallBudget` — by default `defaultCallBudget`: 300 s a call and a
16,384-token reply, passed through the call as `AIProviderCallOptions.MaxTokens`. A deployment sets
its own at composition:

```fsharp skip=fragment
GroundedNarrativeRunOptions.create NarrativeGrounding.factTools
|> GroundedNarrativeRunOptions.withCallBudget
    { CallTimeout = TimeSpan.FromSeconds 600.0; MaxTokens = Some 32_000; RaiseWith = None }
```

The timeout is clamped to the transport's 600 s per-call ceiling, so the budget stays finite. A
reply still streaming when a call reaches the budget is `GroundedNarrativeOverBudget`, whose reason
names the budget and how to raise it. It is written to the audit trail as a failed row with the
outcome `over-budget`. A trigger treats it as permanent, because another attempt over the same facts
meets the same budget and pays for another partial reply.

**Why the scope must be resolved.** The model reads facts through the request-path fact tools,
which take only a scope the platform resolved (Phase 797). A run is handed one — a job passes its
`JobContext.Scope` — and the anonymous scope is refused rather than read.

## Projections: the tables a report draws (Phase 1136)

A narrative's tables are never the model's rows. They come from Facts by one of two routes, and both
routes are built under the same **projection context** (`GroundedProjectionContext`):

- `Scope`, the run's resolved scope;
- `Principal`, the definition's principal;
- `Surface`, the surface the narrative is checked at;
- `Request`, the run's request context. This is the context the model's fact tools read through
  (the resolved scope carried, the principal as the request's user, the run's own DI scope). It is the
  **gated fact reader**. A projection reads through the fact tier's per-request API over it,
  `(FactBrowseHandler.factBrowseApi context.Request).QuerySeries`. The disclosure gate judges every
  point for the narrative's principal, and a withheld point arrives marked as withheld, never as a
  number.

**Deterministic projections** are deployment code that runs on every run. They are appended after
the section's prose. `withProjectionContext` hands the hook the context. `withProjection` keeps the
scope-id form unchanged for hooks that read nothing:

```fsharp skip=fragment
|> GroundedNarrativeDefinition.withProjectionContext (fun context -> async {
    let read = (FactBrowseHandler.factBrowseApi context.Request).QuerySeries
    let! table = PresentationNarrative.project read dailyBySiteRequestJson
    return table |> Result.map (fun elements -> [ "summary", elements ]) })
```

**Requested projections** let the model ask for the table a finding calls for. It can see that
yesterday's drop sits in one category at one site, and it can ask for that breakdown. A section in
the run's answer may carry `"projections"`. Each entry is a presentation request in Phase 1129's
shape: a kind, a fact table, its metrics, a subject level and a period, and never a row. The
deployment registers a builder per kind it allows:

```fsharp skip=fragment
|> GroundedNarrativeDefinition.withRequestBuilder "breakdown" (fun context request ->
    PresentationNarrative.project (FactBrowseHandler.factBrowseApi context.Request).QuerySeries request.Json)
```

A definition with a registered builder tells the model the request form and the registered kinds.
The deployment's own prompt names the tables, metrics and levels the model may ask for, as it names
the metrics a report is written from. Each request is built through its kind's builder under the
projection context. Then it is checked by the grounding gate **on its own**. Only a grounded table
is appended, after the deterministic projections. A request is refused by name on the run's audit
row (`ProjectionRefusals`), and the rest of the run still publishes, when:

- its kind has no registered builder (the refusal names the registered kinds);
- the builder could not draw it (malformed, a refused read, a trend, annotations, nothing on the day);
- the gate refuses what was drawn. This includes a table whose facts the narrative's disclosure
  policy does not permit at `FactNarrativePublication`, which is refused like an ungrounded figure.
  The refusal names each offence by location and reason, never by the value drawn;
- the run asked for more than `MaxRequestedProjections` (eight).

The row also counts what was drawn (`Projections`).

**A projected number must survive the gate, so a projected section is a `Table`.**
`PresentationNarrative` draws a request as a heading, a `Table` and a note per point it leaves
out. Each cell is a `Metric` span that cites the Fact it states, so the gate checks each one as it
checks the model's citations. A withheld point leaves its cell empty, and the note names the subject
and metric with the reason, never a count or a value. A chart is **not** projected into a narrative.
The gate reads a chart component's props as prose, so a chart's points would be refused. A trend's
rows are days, and the gate reads a date as a figure. Narratives therefore project tables, breakdowns
and comparisons until a chart-shaped narrative element exists. For the same reason, a subject or
metric whose name contains a digit draws a table that the gate refuses.

**Why one context, and why it carries a request rather than a typed reader.** The deterministic
hook and the request builders take the same context. Two mechanisms would diverge in what a read
is judged under. The context names no fact-tier type, like every seam in `GroundedNarrative.fs`.
The reader is the fact tier's existing per-request API over the run's own request, the one the
model's tools and a reader's browser use, so nothing new decides disclosure. Two alternatives were
rejected:

- A typed series reader on the context would put the fact tier's types in `ToolUp.Platform.Server`.
- A static holder filled from another seam would be a service locator. It also breaks after a
  restart when a scheduled run precedes any request.

## Conjecture (Phase 1136)

The model writes facts and never conjecture, unless it marks the conjecture as conjecture. A
conjecture can be a whole paragraph, written `{"conjecture":"..."}` in place of the paragraph
string, or a span, written `{{...}}` inside a paragraph. Either form renders as a callout labelled
*Conjecture — not a recorded fact* (`ConjectureLabel`). A span is lifted out of its paragraph and
placed after it.

**A conjecture carries no number.** Its text stays plain text, so a citation token inside it is not
read as a figure. Any digit in it reaches the gate as an `UnreferencedNumber`, and so does a number
in words. The narrative is then refused, because a number is either a fact or it is not written.
`GroundedNarrativeDefinition.withConjecture` tells the model it may write a conjecture; it is off by
default. Whatever the setting, the run renders a conjecture the model writes as a labelled callout,
never as plain prose. The audit row counts the conjectures (`Conjectures`).

A definition with no request builder and no conjecture gets the instruction it always had, byte for
byte (`rulesFor`), and renders exactly as before.

## The triggers

Every trigger is live, and each runs the narrative under a scope the platform resolved — never one
it was told.

| Trigger | Runs under |
|---|---|
| Scheduled report | the scope of the request that created the subscription, re-minted by the scheduler on every run |
| Standing report | the scope the deployment's resolver resolves the subscription's declared principal to, at startup |
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

`ReportingCompose.withReportSubscriptions` hands out the subscription API factory, and since Phase
991 that factory carries the scope the platform resolved for the managing request — there is nothing
to opt in to:

```fsharp skip=fragment
let handler, subscriptionsFor = ReportingCompose.withReportSubscriptions missing jobDeps apiDeps
// per request:
subscriptionsFor principal (ScopeResolution.forRequest ctx) scopeId
```

The factory is `ReportSubscriptionApiHandler.createUnder`. Saving a subscription through it schedules
its job through the typed `IJobScheduler.Schedule(scope, …)` overload: the scheduler persists a token
the platform's `ScopeCarrier` issued for that job (Phase 935), and every run re-mints the scope from it
onto `JobContext.Scope`, which the subscription job hands to the producer on the async chain
(`ReportProducerScope`). So a subscription created by a request resolved to a team produces its
narrative from that team's Facts, publishes it to that team's narrative store, and certifies it there;
a subscription created for another team runs under that team's scope and can read none of the first
team's Facts.

The scope is carried only when it names the shard the subscription is stored in. A subscription
created under the anonymous scope, or with a resolved scope for some other shard, runs **anonymous**,
and the grounded producer refuses it rather than run over the anonymous shard. The one way to build an
API that never carries a scope is to ask for it by name — `ReportSubscriptionApiHandler.create`, the
explicit opt-out, for subscriptions whose producers read nothing scoped. Producers that read no Facts
are unaffected either way.

**Re-saving a subscription re-stamps its job.** A job found again by its idempotency key keeps the
token it was first issued — that is what makes a second submission a no-op — so an UPDATE through
`createUnder` re-issues the job's carried scope explicitly, through the scheduler's re-issue verb
(`JobScopeReissue.reissue`, the `IJobScopeReissue` capability the in-process scheduler and the Quartz
companion implement and the quota decorator forwards). The job keeps its id, its schedule and its run
history; only its token changes. A save that changes the CADENCE is the exception: a scheduler never
changes a job's cron, so the job on the old cadence is cancelled and one on the new cadence scheduled
under the same scope (Phase 1005 — before it, the stored schedule changed and the job kept firing on the
old one). The verb takes a `ResolvedScope` — which only the platform's scope
resolution mints, never a string — and refuses (`ScopeReissueError.ScopeDoesNotOwnJob`) the anonymous
scope or a scope resolved for another shard than the job's, before it reads the job. A scheduler that
cannot re-issue answers `Unsupported`; the save then refuses with
`ReportSubscriptionApiHandler.SubscriptionScopeNotReissued` when the job carries no token at all, so a
re-save never reports success over a job it left anonymous.

**When the scope can no longer be re-minted, the run fails closed and says why.** A token issued over
a key ring the deployment no longer holds, a scheduler restarted with no carrier bound to the
deployment's ring, or a job first scheduled before subscriptions carried their scope (before Phase
990, or through `create`) all run under the anonymous scope, read nothing scoped, and record a
last-run failure carrying `ReportSubscriptionJobHandler.ScopeNotReMinted`. Save the subscription
again from a request resolved to the scope it reports on: the save re-stamps the job in place. The
upgrade is [`docs/migrations/991-scoped-report-subscriptions-by-default.md`](../migrations/991-scoped-report-subscriptions-by-default.md).

### Standing subscriptions — declared, not created

A subscription created on a request needs somebody to sign in and create it, per scope, after every
fresh deployment. A **standing** subscription is declared in the composition instead (Phase 1005):
"run report R on cadence C as principal P, and deliver to D". It names a **principal**, never a scope.
At startup the deployment's own scope resolver — the one every request goes through — resolves that
principal exactly as it would a request signed in as it, and the subscription's job is scheduled under
that resolution through the same typed `Schedule` and carrier as a request-created one. In a team-mode
deployment the principal resolves to its active team, and only while it is a member, so a principal
names no team it does not belong to. Its run is audited as it: the row's `CreatedBy`, the artefact's
author and the run audit's `RunAs` are the principal's user id.

```fsharp skip=fragment
let standing: ReportingCompose.StandingSubscriptions = {
    Principals = [ { Name = "trading"; UserId = "svc-trading"; DisplayName = "Trading reports" } ]
    Subscriptions = [ {
        Key = "daily-trading-summary"
        DisplayName = "Daily trading summary"
        ProducerKey = "daily-trading-summary"
        TemplateId = "trading-summary-template"
        Parameters = Map.empty
        Schedule = "0 6 * * 1-5"
        Principal = "trading"
        RecipientUserIds = [ "alice"; "bob" ]
        Format = Markdown
    } ]
}

let standingDeps: ReportingCompose.StandingSubscriptionDeps = {
    Api = apiDeps                                        // the deps withReportSubscriptions was given
    Templates = templates
    Ledger = ReportSubscriptionStore.standingLedger blobs
}

services.AddSingleton<IHostedService>(
    Func<IServiceProvider, IHostedService>(ReportingCompose.withStandingSubscriptions standing standingDeps))
```

It sits **alongside** `withReportSubscriptions`, never instead of it: request-created subscriptions are
served and stored exactly as before, and the job handler that runs both is the one
`withReportSubscriptions` returns.

**Refused at startup, never at the first tick.** Before anything is written, every declaration is
checked, and any failure fails the host's start with `StandingSubscriptionsRefused`, naming each
failing subscription and what it lacks (`StandingSubscriptionRefusal`): a principal the deployment
does not declare; a principal its resolver does not resolve, with the grant it lacks (an active team,
membership of a team, an identity the resolver accepts); a principal resolving to a scope whose data
does not persist; a template absent at the principal's scope; or an invalid subscription (unknown
producer, cadence, parameters, format, recipients). A deployment not composed through the platform has
no `DeclaredPrincipalResolver` and refuses any declaration. None of them runs anonymous.

**Seeding the template is the deployment's job.** The declared `TemplateId` must already exist at the
scope the principal resolves to — save it there (an `IReportTemplateStore.Save` in a startup step, or
a template store that serves it at every scope) before the standing subscriptions start. Declare the
template the producer renders: the startup check reads the declaration, and a producer rendering some
other template fails its run terminally if that template is absent.

**Idempotent.** A declaration's row is keyed on its `Key` (`standing.<key>`) at its principal's scope.
Re-deploying the same declaration updates that row and re-issues its job's carried scope, never adds
a second; a changed declaration updates the row (and, for a new cadence, replaces the job); a
declaration removed from the composition — or whose principal now resolves to another scope — has its
old row retired, job cancelled and row deleted. The standing-subscription ledger records where rows
were seeded, so retirement works even when the principal itself is gone. An operator's pause through
the management API survives a re-deploy. Rows a request created never carry the `standing.` prefix and
are never touched. Moving a hand-created subscription to a declared one is
[`docs/migrations/1005-standing-report-subscriptions.md`](../migrations/1005-standing-report-subscriptions.md).

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
of another team's Facts. Its Phase 991 cases compose through `withReportSubscriptions` alone: the
subscription runs under its creator's scope, a pre-990 (anonymous-token) job is re-stamped in place by
a save and then reads its team's Fact, and the re-issue verb — on the in-process scheduler and the
Quartz companion — refuses a cross-shard or anonymous scope and leaves the job as it was. Its Phase
1005 cases seed standing subscriptions over the shipped team resolver: a declared subscription fires on
its cadence (the scheduler's tick) under its principal's team and is audited as the principal; the
grounded producer reads that team's Fact and no other; seeding is idempotent across a re-deploy, a
change, a removal and a principal that moved team, with request-created rows untouched; each refusal
fails the start naming the subscription and what it lacks; and neither the principal resolver nor the
reporting tier can build a resolved scope from a string.
[`AssistantFactScopeTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/AssistantFactScopeTests.fs)
drives a chat turn whose `query_facts` call reads the requesting team's Fact and no other.
