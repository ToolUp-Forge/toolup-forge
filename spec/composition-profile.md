# forge composition profile — `server-app-host`

**Profile version 7.0.** forge's evaluator profile of the **App-composition wire specification, format version 1**
(<https://github.com/Fuaran-Build/fuaran-app-composition-specification>, cited throughout as *core*). Sections §P1–§P8 are **normative** within this profile. §P0 and Appendix P-A
are not.

Registered under core §8.5, which names this repository as the profile's home. This document states the token
vocabularies, the profile-only node kinds and the composition rules for the evaluator whose surface it registers; it
changes nothing about the document, the canonical encoding or the core refusals (§P0.1).

## §P0. The profile's subject *(non-normative)*

`server-app-host` is the profile of **forge** — the ToolUp Platform SDK in this repository — as an evaluator that
reduces a composition to a **server-hosted application**: a base host process, capability layers applied over it,
named substrate seams bound to implementations, registered data types, referenced modules, and — optionally — a
bundled client served by a development-time toolchain during authoring. forge is the evaluator Fuaran app compositions
reduce to, and this document is the vocabulary such a composition carries when forge is its evaluator.

It is published and versioned by forge's owner, ToolUp Analytics Ltd, beside the surface it describes: a forge
release that changes its composable surface changes this profile in the same change, and the surface check (§P7)
fails a release in which the two disagree. Every token is registered **as found** on that surface rather than renamed,
because those tokens are wire bytes and renaming a shipped token is a breaking change with nothing to show for it.

The profile's identifier stays `server-app-host`. Gate records, placement claims and pinned references written
against 6.x read the same name at 7.0, and core §8.5 registers the profile under it. Another evaluator of the same
shape may implement this profile; one of a different shape registers a profile of its own.

### §P0.1 What this profile owes the core

Core §2.3 binds every evaluator profile, and this one states its obligations rather than leaving them implied. This
profile:

- changes **no encoding**: core §4's canonical encoding is the core's alone;
- **removes and re-shapes no core member** of any core node kind (core §5), and adds its own node kinds only as
  profile-only kinds (§P3, core §8.3);
- **weakens no refusal** core §9 marks `core`, and adds its own classes only as profile classes (§P4, core §9.2);
- registers **no vocabulary for an opaque slot** — `companionSelection.impl` (core §5.3, §5.4) stays opaque — and
  constrains the `value` of no `configKnob` whose `key` it does not register (§P2.5, core §5.8).

Each shape this profile fills in is the core's, cited by its section: the root (core §5.1), the host configuration
(core §5.2), seam bindings (core §5.3), referenced units (core §5.4), settings (core §5.8), versioned citations
(core §6), extension (core §7), token vocabularies, profile-only kinds and composition rules (core §8.2–§8.4), refusals
(core §9), versioning (core §10) and runtime facets (core §11, §12).

## §P1. Version and scope

| | |
|---|---|
| Profile identifier | `server-app-host` |
| Profile version | 7.0 |
| Profile kind | vocabulary (core §8.6) |
| Core | App-composition wire specification, format version 1 — <https://github.com/Fuaran-Build/fuaran-app-composition-specification> |
| Core version | 1 |
| Registry, as data | [`registry.json`](registry.json) |
| Vectors | [`fixtures/manifest.json`](fixtures/manifest.json) and the `fixtures/profile-server-app-host/` family it lists |
| Conformance families | Every `core`-scope family of the core's corpus, plus `profile-server-app-host` |
| Facet families (§11.2) | `kill-and-replay`, `partition-and-fold`, `attestation-quote`, `incremental-recompute` |

**The registry file is normative and is the machine-readable form of §P2–§P3 and §P8.** Where this prose and
the registry disagree, that is a defect in this document and neither is authoritative over the other
until it is fixed — the two are one artefact maintained in two renderings, and the gate compares them
(§P7).

The profile version moves independently of the core format version (core §10.3). It moves when this
vocabulary moves, which is far more often than the format does — precisely, when §P2–§P4 or a
refusal class changes, or a normative section is introduced (core §10.3's test). It does **not** move
when §P8's facet declaration widens: honouring a new axis or value changes what an implementation of
this profile MAY claim and nothing it MUST do. That is why 1.0 → 1.1 accompanied §P8's introduction
and the `concurrent-authorship-unsupported` refusal class, and why the `locality` and `recompute`
axes were honoured at 1.1 without moving it. 1.1 → 1.2 registered the **fact tier**: the `FactStore`
setting family (§P2.5), the grain vocabulary of a declared fact table (§P2.6), and the two rules that
say where a declared table is written (§P4.7, §P4.8) — additive throughout, because every composition
valid at 1.1 declares no fact table and sets no `FactStore` value outside the one it already could not.
1.2 → 1.3 registered the **knowledge-base layer**: the `knowledgeBase` token (§P2.2) and the rule that
says what it composes onto (§P4.9) — additive, because every composition valid at 1.2 carries no
`knowledgeBase` layer and so meets the new rule vacuously. 1.3 → 1.4 registered **published pages**:
the `publicRendering` layer token (§P2.2), the page-audience vocabulary (§P2.7), the `pageRef` node
kind (§P3.3) and its place in the containment table (§P4.1), and the three rules that say where a page
and the layer may stand (§P4.10–§P4.12) — additive, because every composition valid at 1.3 carries no
`publicRendering` layer and no `pageRef`, and so meets every new rule vacuously. 1.4 → 1.5 registered
the **fact store's backend**: the `FactStoreBackend` setting family (§P2.5) and the rule that says
where a non-default backend may stand (§P4.13) — additive, because every composition valid at 1.4 sets
no `FactStoreBackend` value, and so meets the new rule vacuously.

**1.5 → 2.0 is a BREAKING move, and the major number says so.** It registered the **fact tier's
definitions**: the closed vocabularies a fact table's `definition` and a unit's `metricDefinitions`
range over (§P2.8), and the rules that make a declared table one a host can declare and write
(§P4.14–§P4.20). §P4.15–§P4.20 constrain only the new core members, so a 1.x composition meets them
vacuously. **§P4.14 does not**: it requires every declared fact table to carry its `definition`, so a
1.x composition declaring a table by its references alone — the only way 1.2 to 1.5 could declare one —
is refused at 2.0 until it states the definition. The break is deliberate: a table named only by its
references cannot be declared to a host without the evaluator inventing its subject level, value
shapes, grain, cadence, history mode, disclosure and requirement, and an invented definition is a worse
outcome than a refusal. A 1.x document is still a well-formed **core** document — the core format did
not move (core §10.3), and every member added at 2.0 is an OPTIONAL core member a 1.x reader ignores
(core §9.3) — so what changes for its author is this profile's verdict, not whether the document
reads. A 1.x composition migrates by adding a `definition` to each declared table and registering the
metrics and subject hierarchies the definitions name (`metricDefinitions`, `subjectDefinitions`).

**2.0 → 2.1 registered grounded narratives, additively.** A grounded narrative is a model-written
report the host publishes only when every number in it is a reference to a recorded fact. 2.1 registers
the `groundedNarrative` node kind (§P3.4), the closed vocabulary of the surface a narrative is checked at
(§P2.9), its place in the containment table (§P4.1), and the four rules that say what it composes over
and what it may name (§P4.21–§P4.24). Every composition valid at 2.0 carries no `groundedNarrative`, and
so meets every new rule vacuously.

**2.1 → 2.2 registered who pays for model inference, additively.** A composition that binds the
`aiProvider` seam can now say whether the host pays for its model calls, each caller supplies their
own key, or both. 2.2 registers the `aiFunding` node kind (§P3.5), the closed vocabulary of its
policy (§P2.10), its place in the containment table — the one child an `aiProvider`
`companionSelection` may hold (§P4.1) — and the three rules that say what a funding composes over and
what it must name (§P4.25–§P4.27). Every composition valid at 2.1 carries no `aiFunding`, and so meets
every new rule vacuously.

**2.2 → 2.3 registered what a page says about itself, additively.** A `pageRef` may now carry its
title, the layout it is framed by and the type of each `{name}` capture in its route, and a `layoutRef`
may carry a typed-tree view. 2.3 registers the closed vocabulary of capture types (§P2.11), the four
OPTIONAL members (§P3.2, §P3.3), what a capture type and route precedence oblige an evaluator to do
when serving (§P3.3), and the three rules that say what those members may name (§P4.28–§P4.30). Every
composition valid at 2.2 carries none of the new members, and so meets every new rule vacuously.

**2.3 → 2.4 registered a publication audience, additively.** A page may now be published to named
readers who are not the application's users: 2.4 registers the `publication` audience token (§P2.7), its
two argument members `readers` and `publishingScope` (§P3.3), what serving such a page obliges (§P3.3),
and the rule that says what the two members may hold (§P4.31). Every composition valid at 2.3 carries no
`publication` audience, and so meets the new rule vacuously.

**2.4 → 3.0 is a BREAKING move, and the major number says so.** It registers **reference data**: the
`referenceData` node kind (§P3.6) — a dataset the composition declares by its source, the content hash of
its bytes and its typed columns, which a host verifies before it serves — the closed vocabulary of a
column's type (§P2.12), its place in the containment table (§P4.1), the rule that a declaration is one a
host can load (§P4.32), and the rule that a subject definition's `calendar` names a declared
`referenceData` node (§P4.33). §P4.32 constrains only the new kind, so a 2.x composition meets it
vacuously. **§P4.33 does not**: at 2.x a subject definition's `calendar` (core §5.4) was a tag nothing
had to declare, so a 2.x composition tagging a hierarchy with a calendar is refused at 3.0 until it
declares the dataset the tag names. The break is deliberate: a period counted in a calendar nobody
declared cannot be traced to anything, and a fact whose period labels trace to nothing is a weaker record
than a refusal. A 2.x document is still a well-formed **core** document — the core format did not move,
`referenceData` is a profile-only kind (§P3) and a subject definition's members are unchanged — so what
changes for its author is this profile's verdict, not whether the document reads. A 2.x composition
migrates by adding one `referenceData` node per distinct calendar its subject definitions name, with that
calendar as the node's `id`; a subject definition that names no calendar is untouched.

**3.0 → 3.1 registered what a unit owns and whether a model may reach it, additively.** A `moduleRef`
may now declare the entities its unit owns, each with a disclosure class and its classified fields, and
its exposure to the host's cross-unit AI surface (core §5.4's OPTIONAL `entities` and `aiExposure`).
3.1 registers the closed vocabularies a classified field's `level` and a unit's `aiExposure` range over
(§P2.13, §P2.14; an owned entity's disclosure `class` ranges over §P2.8.3), what an undeclared exposure
means and what serving obliges (§P3.7), the rule that a declaration is well formed and an entity has one
owning unit (§P4.34), and the rule that a unit owning an entity no model may see declares no exposure
that carries it to a model (§P4.35). Every composition valid at 3.0 carries neither member, and so meets
both rules vacuously. A route from a page, a store or a model-facing tool to one named entity is not
registered at 3.1; at 3.1 the unit is the granularity of the AI route, as it is for the host this
profile reduces to.

**3.1 → 3.2 registered how a data source's data arrives, additively.** It registers the `dataDelivery`
node kind (§P3.8) — a declaration that a `connectorRef`'s data is delivered by `upload` through a named
unit's own route — the closed vocabulary of its `mode` (§P2.15), its place in the containment table
(§P4.1), and four rules: what a delivery names is declared (§P4.36), a source is delivered one way
(§P4.37), an upload has a blob store to land in (§P4.38), and an upload arrives through a connector that
reads files (§P4.39). Each rule constrains only the new kind, and no rule requires a delivery, so every
composition valid at 3.1 meets all four vacuously and every `connectorRef` keeps its verdict. A scheduled
pull and a watched location are not registered: neither has a host composition to reduce to at this
version, and a mode a host cannot honour would be a declaration that reads as served and is not.

**3.2 → 3.3 registered the surfaces a restricted fact may reach, additively.** A `moduleRef` may now
declare the disclosure policies its unit owns (core §5.4's OPTIONAL `disclosurePolicies`): each names the
policy reference a `restricted` disclosure carries, a mode, and the egress surfaces at which a fact
classified under it may be disclosed. 3.3 registers the closed vocabularies a policy's `mode` and its
`permitSurfaces` range over (§P2.16, §P2.17), what a declared and an undeclared policy oblige (§P3.9), the
rule that a declaration is well formed and each policy reference is declared once in a composition
(§P4.40), and the rule that a grounded narrative does not publish at a surface every policy over a metric
it cites denies (§P4.41). A policy reference no unit declares is **not** refused: it keeps the
conservative reading, denied at every surface, so every composition valid at 3.2 — one restricting a
fact under a policy it never declares included — meets both rules. The surfaces are the host's egress
doors and nothing wider; "never on a public page" is expressed by not permitting `narrativePublication`,
which covers publication to a page, because no separate public-page door exists.

**3.3 → 3.4 registered the entities a surface reads, additively.** A page, a layout, a grounded narrative
and a model-readable store (a `vectorStore` or `embeddingProvider` selection) may now declare the owned
entities they read, each as a typed edge naming a unit and an entity it owns. 3.4 registers the
`entityReads` node kind (§P3.10), its place in the containment table — the one child each of those
surfaces may hold (§P4.1) — and three rules: a declaration is well formed (§P4.42), an edge names a
declared entity (§P4.43), and no surface reads an entity whose disclosure class is not `surfaceable`
(§P4.44). Each rule constrains only the new kind, and no rule requires an edge, so every composition valid
at 3.3 meets all three vacuously. The edges are the composition's own: a host enforces none of them at
this version, because none of the surfaces carries a read scope a host could hold it to, so the
composition is where an edge is checked. At 3.4 the AI route into a unit through the host's cross-unit AI
surface is still the unit's (§P4.35); an edge refines what one named surface reads, never what that
surface may reach.

**3.4 → 3.5 registered standing report subscriptions and the transactional sink, additively.** A
composition may now declare a report a model writes on a schedule and the host delivers to named
recipients out of band. 3.5 registers the `transactionalSink` seam (§P2.3) — out-of-band delivery to a
recipient (email, SMS, push, a messaging app), which is not `notificationChannel`'s publish-and-subscribe
transport — and four node kinds, each a leaf under `appRoot` (§P4.1): a `reportTemplate` (§P3.11), the
text a report is rendered into, with its typed placeholders; a `reportProducer` (§P3.12), which fills one
placeholder of a template with a grounded narrative's latest run; a `subscriptionPrincipal` (§P3.13), the
identity a subscription runs as; and a `standingSubscription` (§P3.14), which renders a producer's report
on a cron schedule and delivers it to its recipients. It registers the closed vocabularies a template's and
a subscription's `format`, a placeholder's `kind` and a column's `kind` range over (§P2.18–§P2.20), and
seven rules: a subscription's key is one a host can store under (§P4.45), what a subscription and a
producer name is declared (§P4.46), a producer fills a placeholder a narrative can fill and leaves no other
required (§P4.47), a subscription names a recipient (§P4.48), its schedule is a cron expression in the
registered grammar (§P4.49), the composition selects an email sink and a scheduler (§P4.50), and a
placeholder's members are those its kind carries (§P4.51). Each rule constrains only the new kinds and the
new seam, and no rule requires a reporting node, so every composition valid at 3.4 meets all seven
vacuously. The formats are text formats only: a declared template's body is text, and the host's default
renderers serve Markdown and HTML.

**3.5 → 3.6 registered the `pdf` report format, additively.** A template's and a subscription's `format`
may now be `pdf` (§P2.18): a template whose body is HTML written for print (page size, margins, page
breaks under print CSS), which the host fills exactly as it fills an `html` template and then prints to a
paginated PDF. The body is still text, so no member and no node kind moves. 3.6 adds one rule: a
subscription and the template it names agree on `pdf` — a `pdf` subscription names a `pdf` template and a
`pdf` template is subscribed to as `pdf` (§P4.52). No composition valid at 3.5 carries the token, so every
one meets the rule vacuously. Other binary formats (a word-processor document, a spreadsheet) stay
unregistered: their body is a vendor document's bytes, not text.

**3.6 → 3.7 registered the binary report formats `docx` and `xlsx`, additively.** A template's and a
subscription's `format` may now be `docx` (a word-processor document) or `xlsx` (a spreadsheet workbook)
(§P2.18). Their body is a vendor document's bytes, which no string holds, so `reportTemplate` gains a
second body form: a binary template carries `source` and `contentHash` — a file under the application's
root and the SHA-256 of its raw bytes, `referenceData`'s spelling (§P3.6) — in place of `body` (§P3.11).
A host verifies the file before it serves, as it verifies a reference dataset. 3.7 adds two rules — a
template's body is the form its format takes (§P4.53), and a binary body names a file a host can load
(§P4.54) — and widens one: a subscription and its template disagree only between `markdown` and `html`
(§P4.52). Every composition valid at 3.6 carries a text body in a text format, so it meets each rule as
before. A presentation (`pptx`) stays unregistered.

**3.7 → 4.0 is a BREAKING move, and the major number says so.** It registers six settings a composed
application runs on — `AuditLog`, `ShareTokenStore`, `TeamCreationPolicy`, `EventStore`,
`IncludePlatformDefaults` and `PublicBaseUrl` — and the **value grammars** a registered key is held to
(§P2.5). Until 3.7 a registered key's values were a closed list of names, which cannot state a retention
period, a boolean or an origin, so the registry now gives each registered key one grammar from a closed
set: `enum` (a list of names, which every key registered before 4.0 keeps with its values unchanged),
`retention`, `boolean` and `origin`. Registering a key closes its values, and that is the break: the six
keys were unregistered at 3.7, so any value on them was legitimate and carried no rule, and a 3.7-valid
composition carrying one of them with a value its grammar does not admit is refused at 4.0 as
`knob-value-unregistered`. **Nothing else changes for its author.** No node kind, member, token
vocabulary or other rule moved, and a composition carrying none of the six keys — or carrying them with
admissible values — is valid at 4.0 exactly as it was at 3.7. A 3.7 document is still a well-formed
**core** document: the core format did not move and a setting is still a `(key, value)` pair of strings,
so what changes is this profile's verdict, not whether the document reads. A 3.7 composition migrates by
re-spelling those six keys' values and nothing else: the conventional flag value `enabled` becomes
`EnabledAuditLog` or `EnabledShareTokenStore`, a team-creation value becomes `PlatformAdminOnly` or
`AnyAuthenticatedUser`, an event-store value becomes `InMemoryOnly` or a retention form (a named period
becomes `byAge:<n>d` with the period in days, and an unbounded one `unlimited`), a platform-defaults value
becomes `true` or `false`, and a public base URL loses any path and trailing slash. The break is
deliberate: a host that reads one of these settings has to know what its value means, and a value two
implementations read differently — `enabled` here and `yes` there, a URL with and without its trailing
slash — is a worse outcome than a refusal. Keeping `enabled` as a permanent alias was rejected, because an
alias registered to stay additive is a second spelling every later implementation inherits.

**4.0 → 5.0 is a BREAKING move, on the 4.0 rule.** It registers three more settings a composed
application runs on — `DataIngestion`, `DataSubjectRequests` and `EncryptedBlobStorage` — each under the
`enum` grammar (§P2.5.1), and it adds no grammar. The three keys were unregistered at 4.0, so any value on
them was legitimate and carried no rule, and a 4.0-valid composition carrying one of them with a value its
list does not hold is refused at 5.0 as `knob-value-unregistered`. One vector of this corpus migrates:
`profile-server-app-host/connector-host`, whose `DataIngestion` value `enabled` becomes
`EnabledDataIngestion`. **Nothing else changes for its author.** No node kind, member, token vocabulary,
grammar or other rule moved, and a composition carrying none of the three keys — or carrying them with
listed values — is valid at 5.0 exactly as it was at 4.0. A 4.0 composition migrates by re-spelling those
three keys' values and nothing else: the conventional flag value `enabled` becomes `EnabledDataIngestion`
on `DataIngestion` and `SingleKeyResolver` on `EncryptedBlobStorage`, and on `DataSubjectRequests` it
becomes the `sync:` value naming the erasure policy the author means (§P2.5). `DataSubjectRequests` is a
closed set of four spellings, so it is an `enum` rather than a grammar of its own: a grammar earns its
place where a value carries an open parameter, as a retention period does, and `sync:<policy>` ranges over
three names. `EncryptedBlobStorage` lists an explicit `NoEncryptedBlobStorage` first, because an `enum`'s
first value is its default and a composition that does not carry the key encrypts nothing. A value a later
version admits on one of the three — a request mode answered in the background, a second key arrangement —
is appended to the key's list, which is an additive move.

**5.0 → 6.0 is a BREAKING move, and the major number says so.** It adds one rule: a composition carrying a
`clientShell` declares the application's machine name, core §5.1's OPTIONAL `shortName` (§P4.55,
`short-name-required`). The member itself is the core's and is additive there — the format version did not
move, and a 5.0 reader meets it as an unknown member and ignores it (core §9.3). The rule is what breaks: a
5.0-valid composition with a `clientShell` and no `shortName` is refused at 6.0. Four vectors of this
corpus migrate by declaring one — `profile-server-app-host/client-shell`, `every-layer`,
`layered-client-app` and `connector-host` — and `reject-client-shell-unsupported-base` declares
one so that it exhibits its own class and no other. **Nothing else changes for its author.** No node kind,
token vocabulary, grammar or other rule moved, and a composition with no `clientShell` is valid at 6.0
exactly as it was at 5.0, with or without the member. A 5.0 composition migrates by adding a `shortName` to
its root and nothing else. The break is deliberate: an evaluator of this profile names the solution it
builds, the launcher that builds it and the bundled client after the application, and an application with
a client is always one somebody names; without a declared name every such evaluator invents one, from the
title (opaque copy), from the root's `id` (tree identity) or from a constant (one name for every
application), and two evaluators inventing differently is a worse outcome than a refusal. A composition
with no `clientShell` may leave the member out: what it reduces to is a host with nothing to name for a
person, and a profile that required a name nobody reads would be refusing for tidiness.

**6.0 → 6.1 states what an evaluator builds from the root's declared projects, additively.** Core §5.1 gained
the OPTIONAL `projects` member — the build units an application carries that no node produces — and 6.1 adds
one composition rule saying what an evaluator of this profile does with it: it builds each declared project
with the application, in the solution it names after the application (§P4.56). It adds no refusal class, no
token and no node kind: the member's shape, and its three refusals, are the core's. A composition that declares no
project meets the new rule vacuously and builds exactly what it built at 6.0.

**6.1 → 6.2 registers the two deliveries 3.2 deferred, additively.** §P2.15 gains `scheduledPull` and `watched`:
a source pulled on a cadence, and a source fed by whatever lands at a watched location, each run as a declared
`subscriptionPrincipal` (§P3.13), the one principal shape a composition declares for every kind of work that runs
as one. §P3.8 gains four OPTIONAL members — `principal`, `cadence`, `location` and `tables` — and its `module`
becomes OPTIONAL, since only an `upload` names a unit's route. §P4.36, §P4.38 and §P4.39 widen to the new modes,
and §P4.57–§P4.61 add five rules, each refusing at the composition what a host would otherwise refuse at startup:
a delivery carries exactly its mode's members, its principal can be resolved, a pull's cadence is a cron, a watched
location stays in the principal's scope, and a pull or a watch has a scheduler. Every 6.1 composition carries
`upload` deliveries only, each with its `module`, so it meets every rule as before.

**6.2 → 6.3 gives a grounded narrative a cadence, additively.** Until 6.3 a run was enqueued only by a change — a
fact of a metric it depends on, or a commit of a table it names — so a report wanted on a calendar (a briefing every
Monday morning) had no way to say so. §P3.4 gains two OPTIONAL members: `cadence`, a five-field cron expression on
which the host runs the narrative, and `runsAs`, the `subscriptionPrincipal` (§P3.13) whose scope a scheduled run
reads and publishes in, since a schedule has no request to resolve a scope from. §P4.22 widens to `runsAs`, §P4.24
requires the two together, and §P4.62–§P4.63 add two rules, each refusing at the composition what a host would
otherwise refuse at startup: the cadence is a cron, and the principal can be resolved. Every 6.2 composition carries
neither member, so it meets every rule as before.

**6.3 → 6.4 registers a fact browse, additively.** Until 6.4 a composition could select a fact tier and declare what
it writes, but had no way to say that the people it serves read it back: the host's direct reading of its fact base —
the declared tables, their runs, their rows a page at a time, and one metric's series over a period range, the
numbers a chart is drawn from — had no node to reduce to. 6.4 registers the `factBrowse` node kind (§P3.15), the
closed vocabulary of its `mount` (§P2.21), its place in the containment table — a leaf under `appRoot`, at most one
(§P4.1) — and four rules: a browse has a fact tier (§P4.64), its client module has a client to join (§P4.65), it is
well formed (§P4.66), and it does not stand over facts every declared policy denies at `browse` (§P4.67). Every 6.3
composition carries no `factBrowse`, and so meets every new rule vacuously.

**6.4 → 6.5 lets a client shell link its home page to published pages, additively.** Until 6.5 a composition could
publish pages (§P3.3) and carry a bundled client (§P3.1), but nothing connected the two: a reader who landed on the
client's home page reached a published page only by knowing its route. 6.5 gives `clientShell` its first kind member,
the OPTIONAL `home` (§P3.1) — an ordered list of entries, each naming a `pageRef` with a title and an optional
description — and four rules: an entry names a declared page (§P4.68), served at a literal route (§P4.69), whose
audience a user of the client can hold (§P4.70), and it is well formed (§P4.71). The member is omitted when a shell
declares no entry, so every 6.4 composition encodes byte for byte as before and meets every new rule vacuously.

**6.5 → 6.6 registers a house theme, additively.** Until 6.6 a composition could bundle a client and publish pages,
but had no way to say what they look like: a brand's colours, type and spacing reached a host only through an edit to
a file the host generates, which the next reduction overwrites. 6.6 registers the `theme` node kind (§P3.16) — a
declared, content-addressed theme manifest, the same shape as a reference dataset's file (§P3.6) — its place in the
containment table, a leaf under `appRoot`, at most one (§P4.1), and one rule: its `source` and `contentHash` are well
formed (§P4.72). Every 6.5 composition carries no `theme`, and so encodes byte for byte as before and meets the new
rule vacuously.

**6.6 → 6.7 lets a grounded narrative say that it may mark a conjecture, additively.** Until 6.7 whether a report
could state a hypothesis was decided by host code a composition never saw, so a reader of the attested composition
could not tell a report that may carry a labelled guess from one that carries only what the facts support. 6.7 gives
`groundedNarrative` the OPTIONAL `allowsConjecture` (§P3.4), the format's boolean string, written `"true"` when the
narrative may mark a conjecture and omitted when it may not. No rule is added. Every 6.6 composition carries no
`allowsConjecture`, and so encodes byte for byte as before and means what it meant: no conjecture.

**6.7 → 6.8 lets a composition say what an assistant may operate, additively.** Until 6.8 a composition could say
what a unit exposes for an assistant to read (§P3.7) and nothing about what the assistant may drive: which fields it
may set, which buttons it may press, which pages it may open. That allowlist was host code a reader of the attested
composition never saw, so what an assistant could change on a user's screen was not signed with what it could read.
6.8 registers the `uiControl` node kind (§P3.17) — the allowlist, and the switches for the paths a host resolves an
instruction on — its place in the containment table, a leaf under `appRoot`, at most one (§P4.1), and three rules:
it needs a client to operate (§P4.73), its allowlist is well formed and names only what the composition declares
(§P4.74), and a named triage provider is one the composition selects (§P4.75). Every 6.7 composition carries no
`uiControl`, and so encodes byte for byte as before and meets every new rule vacuously.

**6.8 → 7.0 moves the profile to its evaluator's owner, and nothing normative changes.** The document, its registry
and its vectors now live in the `spec/` folder of this repository, published by ToolUp Analytics Ltd under the
repository's Apache-2.0 licence, and §P0 names forge as the evaluator whose surface they register. The version is a
MAJOR for the new home, owner and subject, and for nothing else: every token vocabulary, node kind, composition rule,
refusal class, value grammar and facet value is what it was at 6.8, the registry's token content is unchanged, and a
composition valid at 6.8 is valid at 7.0 with the same verdict. Three things moved with the document. §P0.1 states the
core §2.3 obligations explicitly. §P7's gate is this repository's surface check, which also holds the registry against
forge's composable surface. And three vectors that had been captured from built applications are replaced by synthetic
vectors exercising the same tokens, kinds and rules — `layered-client-app`, `connector-host` and
`server-rendered-host` — and a module name the fact-tier vectors shared is renamed; the history above names vectors
by their 7.0 identifiers.

## §P2. Token vocabularies

### §P2.1 `appRoot.base` — **closed**

| Token | Meaning |
|---|---|
| `serverApp` | A host that serves an application and bundles a client. |
| `publicRenderingServerApp` | A host that renders its surfaces server-side and bundles **no** client. |

A token outside this set is `token-unregistered`.

### §P2.2 `appRoot.layers` element — **closed**

| Token | Meaning |
|---|---|
| `ai` | Model-inference capability: binds the model-provider seam. |
| `rag` | Retrieval-augmented capability: `ai`, plus the embedding and vector-store seams. |
| `forms` | Form-definition and submission capability. |
| `peer` | Inter-deployment federation capability. |
| `extensions` | The host's own container-extension escape. |
| `knowledgeBase` | Document-ingestion capability: a knowledge base the model-inference tier answers from. Composes over `ai` (§P4.9). |
| `publicRendering` | Server-side rendering of published pages, stacked onto a `serverApp` host's own pipeline so one deployment serves its application and its pages (§P4.10). |

A token outside this set is `token-unregistered`.

### §P2.3 `companionSelection.slot` — **closed**

| Token | Seam |
|---|---|
| `aiProvider` | Model inference. |
| `embeddingProvider` | Text embedding. |
| `blobStorage` | Opaque object storage. |
| `authProvider` | Caller identity. |
| `notificationChannel` | Outbound notification delivery. |
| `vectorStore` | Vector retrieval. |
| `secretStore` | Secret material. |
| `auditSink` | Audit record emission. |
| `metricsSink` | Metric emission. |
| `jobScheduler` | Deferred and recurring work. |
| `dataSource` | External data retrieval. |
| `transactionalSink` | Out-of-band delivery of a message to a named recipient (email, SMS, push, a messaging app). Not `notificationChannel`, which is a publish-and-subscribe transport. |

A token outside this set is `token-unregistered`. `companionSelection.impl` is opaque and this
profile registers nothing for it (core §2.3).

### §P2.4 `moduleRef.view` — **closed**

| Token | Citation-bearing | Meaning |
|---|---|---|
| `bareFeliz` | No | The host's default client view technology. |
| `fuaranUI` | **Yes** | A typed-tree view, authored against a separately-specified UI contract. |
| `custom` | No | A view composed outside anything this composition types. |

`fuaranUI` is the profile's only citation-bearing token: a composition carrying it MUST carry a
`viewSpec` (core §5.4, §6). `bareFeliz` and `custom` MUST NOT carry one (`citation-unexpected`).

The asymmetry is deliberate and is a statement about what is knowable, not a convenience. A typed-tree
view is written against a contract that moves, so *which revision* is a question every such view
answers — badly, when it answers implicitly. The other two make no cross-specification claim at all,
so there is nothing for them to cite and an absent citation is a fact rather than a gap.

### §P2.5 `configKnob.key` — **open**, with a registered subset held to value grammars

The key space is **open**. A composition carries whatever settings its author needs, and enumerating
them would be specifying a host's configuration semantics in a wire document.

What this profile registers is the subset whose value it gives a meaning: settings that select between
composition modes, and settings a host of this profile reads and whose value two implementations must
read alike. Each registered key is held to exactly one **value grammar** from the closed set below, and a
registered key carrying a value its grammar does not admit is `knob-value-unregistered`. A value is
compared byte for byte: there is no case folding, trimming or other normalisation, so each admissible
value has exactly one spelling.

| Key | Grammar | Values |
|---|---|---|
| `ProcessProfile` | `enum` | `AllInOne`, `WebOnly`, `WorkerOnly` |
| `ConfigDriftDetection` | `enum` | `NoConfigDriftDetection`, `EnabledConfigDriftDetection` |
| `JobScheduler` | `enum` | `NoJobScheduler`, `InProcessJobScheduler` |
| `PeerSubstrate` | `enum` | `NoPeerSubstrate`, `EnabledPeerSubstrate` |
| `FactStore` | `enum` | `NoFactStore`, `EnabledFactStore` |
| `FactStoreBackend` | `enum` | `Blob`, `Postgres` |
| `AuditLog` | `enum` | `NoAuditLog`, `EnabledAuditLog` |
| `ShareTokenStore` | `enum` | `NoShareTokenStore`, `EnabledShareTokenStore` |
| `TeamCreationPolicy` | `enum` | `PlatformAdminOnly`, `AnyAuthenticatedUser` |
| `EventStore` | `retention` | §P2.5.2 |
| `IncludePlatformDefaults` | `boolean` | §P2.5.3 |
| `PublicBaseUrl` | `origin` | §P2.5.4 |
| `DataIngestion` | `enum` | `NoDataIngestion`, `EnabledDataIngestion` |
| `DataSubjectRequests` | `enum` | `Disabled`, `sync:HardDelete`, `sync:Tombstone`, `sync:RetainPerCompliance` |
| `EncryptedBlobStorage` | `enum` | `NoEncryptedBlobStorage`, `SingleKeyResolver` |

**An unregistered key is legitimate and carries none of these rules** (core §8.2). It is a setting
the profile does not give a meaning, it is not refused, and an implementation preserves it exactly.
Flag-shaped settings conventionally carry the value `"enabled"`; nothing reads that on an unregistered
key, and no registered key admits it.

`configKnob.value` is opaque for an unregistered key and this profile registers nothing for it
(core §2.3, §5.8). A grammar is part of what recognising one key means (core §8.4): it constrains the value
of that key, and of no other.

#### §P2.5.1 `enum`

A value is exactly one of the names the key's row lists. The first name listed is the default: a
composition that does not carry the key is read as carrying it.

#### §P2.5.2 `retention`

Where the host's event log lives and how long it keeps an event. A value is exactly one of these forms:

| Form | Bound | Meaning |
|---|---|---|
| `InMemoryOnly` | — | The log is held in the host's process and does not survive a restart. The default. |
| `unlimited` | — | The log is persistent and keeps every event. |
| `ninetyDays` | — | The log is persistent and discards an event older than ninety days. |
| `byAge:<n>d` | 10675199 | The log is persistent and discards an event older than `<n>` days. |
| `byCount:<n>` | 2147483647 | The log is persistent and keeps the `<n>` most recent events. |

`<n>` is a positive decimal integer: one or more of the digits `0`–`9`, the first of which is not `0`,
and at most the form's bound. The bounds are the largest whole number of days a signed 64-bit count of
100-nanosecond ticks holds, and the largest signed 32-bit integer, so an implementation on either
representation holds every admissible value. Every form other than `InMemoryOnly` is persistent.

#### §P2.5.3 `boolean`

A value is exactly one of:

| Value |
|---|
| `true` |
| `false` |

#### §P2.5.4 `origin`

An absolute web origin: the scheme, host and port at which something is reached, and nothing else.

```
origin = scheme "://" host [ ":" port ]
host   = label *( "." label )        ; at most 253 characters
label  = 1 to 63 of a-z, 0-9 and "-", neither first nor last "-"
port   = a positive decimal integer with no leading zero, at most 65535,
         and not the scheme's default port
```

| Scheme | Default port |
|---|---|
| `http` | 80 |
| `https` | 443 |

So an origin carries no user information, no path — not even a lone trailing `/` — no query and no
fragment; its scheme and host are lowercase; and it states a port only when the port is not the scheme's
default. Each origin therefore has exactly one spelling. A host written as an address in the version 6
notation is not an origin under this grammar.

**What the six keys registered at 4.0 mean.** `AuditLog` selects whether the host keeps an audit trail
of the changes its users make. `ShareTokenStore` selects whether the host issues share tokens — signed,
revocable grants of access to one resource for a reader who need not be a user. `TeamCreationPolicy`
selects who may create a team: a platform administrator only, or any signed-in user. `EventStore` is the
retention of the host's event log (§P2.5.2). `IncludePlatformDefaults` selects whether the host composes
its own default modules beside the composition's; its default is `true`. `PublicBaseUrl` is the origin the
application's absolute links are built on; it has no default, and a composition that does not carry it
states no origin. Where a deployment's environment supplies or overrides a public origin, that is
deployment configuration the composition does not carry.

**What the three keys registered at 5.0 mean.** `DataIngestion` selects whether the host runs its
data-ingestion substrate: the ingestor that pulls records in from the data sources a deployment connects,
and the store of their configuration. Its default composes none.

`DataSubjectRequests` selects whether the host serves **data-subject requests** — a person's request to
export, or to erase, the personal data the application holds about them — and what erasure does.
`Disabled` serves none and is the default. A `sync:<policy>` value serves them, answering each request in
the call that makes it, and erases under `<policy>`:

| Policy | What erasure does |
|---|---|
| `HardDelete` | Removes each of the person's records entirely, breaking the history those records were part of. |
| `Tombstone` | Replaces each identifying field with a marker, keeping the record's shape and history, so the erasure itself stays discoverable. |
| `RetainPerCompliance` | Keeps every audit and event record a retention obligation requires, and redacts identifying content where it can. |

A request is authorised as the composition authenticates every other request the host serves.

`EncryptedBlobStorage` selects whether the host encrypts the blobs it stores, at rest, and under which key
arrangement. `NoEncryptedBlobStorage` stores them as written and is the default. `SingleKeyResolver`
encrypts every blob under one application-wide key that the host keeps in its secret store and creates on
first use. That store must outlive the deployment: losing the key leaves every encrypted blob unreadable.

`FactStore` selects the host's **fact tier** — a store of facts about subjects, which module-declared
fact tables (core §5.4) are written into. Its first value is the default, and the default composes
none: a composition that sets nothing has no fact tier, exactly as it had none before the key was
registered.

`FactStoreBackend` selects which store **backs** the fact tier `FactStore` composes: `Blob`, the
host's default store over its blob storage, or `Postgres`, a store over a relational database the
deployment provides. Its first value is the default, and a composition that sets nothing is backed
exactly as it was before the key was registered. Where the database is, and how the host reaches it,
is deployment configuration the composition does not carry.

### §P2.6 `moduleRef.factTables` grain element — **closed**

| Token | Meaning |
|---|---|
| `day` | One row per subject per calendar day. |
| `week` | One row per subject per week. |
| `month` | One row per subject per calendar month. |
| `quarter` | One row per subject per calendar quarter. |
| `year` | One row per subject per calendar year. |
| `snapshot` | One row per subject, describing its current state rather than a period. |

A token outside this set is `token-unregistered`. The calendar each grain is counted in is the
subject hierarchy's, which the composition does not carry; the grain says only which period a row
describes.

### §P2.7 `pageRef.audience` — **closed**

| Token | Who may read the page | Argument member |
|---|---|---|
| `public` | Anyone, an anonymous reader included. Such a page is discoverable: an evaluator may list it wherever it lists pages. | none |
| `authenticated` | Any reader the host's identity seam has identified. | none |
| `scope` | An identified reader holding at least one of the named roles. | `roles` |
| `client` | The one identified reader whose own scope is the named relationship. | `relationship` |
| `publication` | Only an identified reader holding at least one of the named reader roles, who reads the named publishing scope's content and is admitted to nothing else the application serves. | `readers`, `publishingScope` |

A token outside this set is `token-unregistered`. A page whose audience is anything but `public` is
**gated**, and a gated page is never discoverable: an evaluator that lists pages to anonymous readers
(an index, a feed, an export) does not list it. That is part of what the audience MEANS, which is why it
is stated here rather than left to each evaluator's enumeration surfaces.

`scope` names at least one role. An empty role set would be `authenticated` spelled a second way, and
a writer has one spelling for one meaning.

`publication` (2.4) is for readers who must never be the application's users: a client's board, an
external group, the readers of one team's reports. Both of its members are NAMES, never identifiers. A
reader is a role name as the host's identity seam asserts it, so a directory group's identifier stays in
the deployment that maps it onto the role. The publishing scope is the composition's name for the scope
whose content the page reads, so the scope's identifier, which only a deployment knows, is bound there.
Unlike `scope`, holding an application role admits nobody: a `publication` page is read by its readers
and by no one else.

### §P2.8 The fact tier's definitions — **closed**

The vocabularies a fact table's `definition` (core §5.4) and a unit's `metricDefinitions` range over.
Every slot below is closed: a token outside it is `token-unregistered`. A definition's `periodGrain`
ranges over §P2.6.

#### §P2.8.1 A column's `shape`

| Token | Every cell of the column is |
|---|---|
| `scalar` | A single decimal value. |
| `interval` | A low–high interval. |
| `distribution` | A named distribution of values. |
| `categorical` | A categorical label. |
| `series` | A reference to a stored series. |

A cell may always record that its value is absent; a shape constrains the values that are present.

#### §P2.8.2 A definition's `historyMode`

| Token | Across refreshes, the table |
|---|---|
| `appendByRun` | Keeps every committed refresh as a complete, attributable snapshot. |
| `replace` | Holds the latest values, rewriting a cell only when it changed. |

#### §P2.8.3 A disclosure's `class`

| Token | The cells are | `policy` |
|---|---|---|
| `surfaceable` | Admissible to an answer or an external door, subject to the composition's policy. | absent |
| `internal` | First-class inside computation and internal audit, and never an egress output. | absent |
| `restricted` | Governed by the named policy. | REQUIRED |

#### §P2.8.4 A definition's `requirement`

| Token | The table is |
|---|---|
| `required` | Part of the unit's output: the output is incomplete without it. |
| `optional` | Produced when the unit can; its absence is not a failure. |

#### §P2.8.5 A metric definition's `direction`

| Token | A movement is better when the value |
|---|---|
| `higherIsBetter` | Rises. |
| `lowerIsBetter` | Falls. |
| `neutral` | Has no natural polarity. |

#### §P2.8.6 A staleness object's `policy`

| Token | A value of the metric is fresh | `windowSeconds` |
|---|---|---|
| `freshFor` | For the window after it was recorded. | REQUIRED |
| `untilSuperseded` | Until a newer value supersedes it. | absent |
| `untilUpstreamChange` | Until an input it was computed from changes. | absent |

#### §P2.8.7 A metric definition's `recomputePolicy`

| Token | When an input of a recorded value changes, the host |
|---|---|
| `eager` | Recomputes it straight away. |
| `onQuery` | Recomputes it at the next read. |
| `manual` | Reports the change and recomputes nothing unbidden. |

Absent is the composition stating no policy, which a host reads as recomputing nothing unbidden.

#### §P2.8.8 A roll-up object's `kind`

| Token | Across one level of a hierarchy, the metric | `tolerance` |
|---|---|---|
| `additive` | Sums: a parent's value is its children's total, within the tolerance. | REQUIRED |
| `nonAdditive` | Does not sum (a ratio, an average, a share). | absent |

### §P2.9 `groundedNarrative.surface` — **closed**

The egress surface a grounded narrative is checked at before it is published: the door a fact's
disclosure stance is evaluated against for this narrative.

| Token | The surface |
|---|---|
| `retrieval` | Fact resolution into retrieval results or a prompt's context. |
| `toolResult` | A fact-reading tool returning facts to a model. |
| `narrativePublication` | Publishing a narrative that references facts to a surfaceable store. The surface a narrative is checked at unless the composition says otherwise. |
| `export` | A rendered document leaving the deployment. |
| `webhook` | An outbound fact-event payload. |
| `peerEgress` | An answer leaving the deployment across a federation seam. |
| `browse` | A reader browsing the fact base directly. |
| `teamPublication` | Publication into a team's shared space. |

A token outside this set is `token-unregistered`.

### §P2.10 `aiFunding.policy` — **closed**

Who pays for the model calls an `aiProvider` binding makes: the policy a host resolves each request's
provider under.

| Token | Who pays |
|---|---|
| `platformOnly` | The host. Every request is served by the host's own provider, on the host's key; a caller's own configuration is not consulted. |
| `permissiveWithPlatformFallback` | The caller where they have configured their own key, the host otherwise. |
| `strictBYOK` | The caller, always. A request whose caller has configured no key of their own is served by no provider; the host never pays. |

A token outside this set is `token-unregistered`.

### §P2.11 `pageRef.captures` value — **closed**

The type a page route's `{name}` capture declares. Each is a grammar over one path segment, free of
locale and culture.

| Token | A segment of this type is |
|---|---|
| `date` | An ISO 8601 calendar date `YYYY-MM-DD` that exists: four digits, `-`, two, `-`, two, naming a day of the proleptic Gregorian calendar from year 1. `2024-02-29` is one; `2023-02-29` and `2026-13-01` are not. |
| `slug` | One or more runs of lowercase ASCII letters and digits, joined by single `-`. No leading, trailing or doubled `-`. |
| `int` | An optional `-` then one or more ASCII digits, whose value is within the signed 32-bit range. No `+`, no point, no exponent; leading zeros are admitted. |

A token outside this set is `token-unregistered`.

### §P2.12 `referenceData.columns` type — **closed**

The type a reference dataset's column declares: how every cell of that column is read. Each is a grammar
over one field of the dialect (§P3.6), free of locale and culture.

| Token | A cell of this type is |
|---|---|
| `date` | An ISO 8601 calendar date `YYYY-MM-DD` that exists, by §P2.11's `date` grammar. |
| `integer` | An optional `-` then one or more ASCII digits, whose value is within the signed 64-bit range. No `+`, no point, no exponent; leading zeros are admitted. |
| `text` | Any field the dialect admits, the empty one included. |

A token outside this set is `token-unregistered`.

### §P2.13 `moduleRef.entities` field `level` — **closed**

The sensitivity of an owned entity's classified field (core §5.4). Ordered loosely from least to most
sensitive.

| Token | The field holds |
|---|---|
| `public` | Non-sensitive data, readable by anyone who can read the entity. |
| `confidential` | Internal or commercially confidential data that is not special-category data. |
| `financial` | Monetary or financial figures. |
| `regulatory` | Data attracting a specific compliance regime that is not itself personal data. |
| `pii` | Personally identifiable information, a pseudonymous identifier included. |
| `spi` | Special-category personal information. |

A token outside this set is `token-unregistered`. A level refuses nothing in a composition by itself: a
host carries every declared classification to the gates that act on a field when it is read and when it
leaves (§P3.7).

### §P2.14 `moduleRef.aiExposure` — **closed**

Whether a unit's data is reachable by a model through the host's cross-unit AI surface (core §5.4).

| Token | The unit is |
|---|---|
| `notQueryable` | Outside the AI surface: the surface lists none of its data and refuses every reach into it. |
| `queryable` | Inside the AI surface, for every caller the host's access control already admits to the unit. |

A token outside this set is `token-unregistered`. An absent `aiExposure` is `notQueryable` (§P3.7).

### §P2.15 `dataDelivery.mode` — **closed**

How a delivered source's data arrives (§P3.8).

| Token | The data arrives |
|---|---|
| `upload` | As a file a caller hands the named unit's own route, which stores it in the host's blob store and runs the host's ingestion over it. |
| `scheduledPull` | Pulled by the host, each listed table on the delivery's cadence, run as the delivery's principal (6.2). |
| `watched` | Ingested by the host once per object that lands at the delivery's location, a prefix inside the principal's own scope in the host's blob store, run as the delivery's principal (6.2). |

A token outside this set is `token-unregistered`.

### §P2.16 `moduleRef.disclosurePolicies` `mode` — **closed**

How a disclosure policy governs facts derived from the facts classified under it (§P3.9).

| Token | The policy |
|---|---|
| `plain` | Governs only the facts directly classified under it. |
| `taintPropagating` | Also governs every fact derived from one classified under it, until a declassification the host recognises clears it. A composition declares no declassification, so under this profile a derived fact stays governed. |

A token outside this set is `token-unregistered`.

### §P2.17 `moduleRef.disclosurePolicies` `permitSurfaces` (element) — **closed**

The egress surfaces at which a fact classified under the policy may be disclosed directly. The set is
§P2.9's, read the same way:

| Token | The surface |
|---|---|
| `retrieval` | Fact resolution into retrieval results or a prompt's context. |
| `toolResult` | A fact-reading tool returning facts to a model. |
| `narrativePublication` | Publishing a narrative that references facts to a surfaceable store. The surface a narrative is checked at unless the composition says otherwise. |
| `export` | A rendered document leaving the deployment. |
| `webhook` | An outbound fact-event payload. |
| `peerEgress` | An answer leaving the deployment across a federation seam. |
| `browse` | A reader browsing the fact base directly. |
| `teamPublication` | Publication into a team's shared space. |

A token outside this set is `token-unregistered`. A surface absent from a policy's list is denied there;
the empty list is a sealed policy, denied at every surface. No surface means "a public page": publishing
to a page is `narrativePublication`.

### §P2.18 `reportTemplate.format` and `standingSubscription.format` — **closed**

The format a template's body is written in (§P3.11), and the format a subscription's report is rendered
to (§P3.14). The two slots range over one set.

| Token | The body is |
|---|---|
| `markdown` | Markdown text. |
| `html` | HTML text. |
| `pdf` | HTML text written for print, rendered to a paginated PDF (3.6). |
| `docx` | A word-processor document's bytes, in the Office Open XML WordprocessingML package (3.7). |
| `xlsx` | A spreadsheet workbook's bytes, in the Office Open XML SpreadsheetML package (3.7). |

A token outside this set is `token-unregistered`. `markdown`, `html` and `pdf` are the **text formats**: a
`pdf` template's body is HTML — filled exactly as an `html` template's is, then printed — so a PDF is a
rendering of a text body, not a body of its own. `docx` and `xlsx` are the **binary formats**: the body is
a vendor document's bytes, held as a content-addressed file (§P3.11), and a host fills each `{{key}}` token
in the document's own text. A host honouring a registered format composes a renderer for it; one that
cannot print, or cannot fill a word-processor document or a workbook, does not claim this profile version.

### §P2.19 `reportTemplate.placeholders` `kind` — **closed**

The kind of value a template's placeholder takes (§P3.11). What other members a placeholder carries
depends on its kind (§P4.51).

| Token | The placeholder takes |
|---|---|
| `text` | Prose. The one kind a producer fills with a narrative (§P4.47). |
| `number` | A number, rendered under the placeholder's OPTIONAL format hint. |
| `date` | A date, rendered under the placeholder's OPTIONAL format hint. |
| `image` | An image, whose MIME type is the placeholder's REQUIRED hint. |
| `table` | Rows of the placeholder's declared columns. |

A token outside this set is `token-unregistered`.

### §P2.20 `reportTemplate.placeholders` `columns` `kind` — **closed**

The kind of value a `table` placeholder's column takes. §P2.19's set less `table`: a table is never
nested in a table.

| Token | The column takes |
|---|---|
| `text` | Prose. |
| `number` | A number, rendered under the column's OPTIONAL format hint. |
| `date` | A date, rendered under the column's OPTIONAL format hint. |
| `image` | An image, whose MIME type is the column's REQUIRED hint. |

A token outside this set — `table` included — is `token-unregistered`.

### §P2.21 `factBrowse.mount` — **closed**

Which halves of a fact browse a host mounts (§P3.15).

| Token | The host mounts |
|---|---|
| `serverOnly` | The browse surface on the server and nothing in the client: a view of the application's own reads it. |

A token outside this set is `token-unregistered`. An absent `mount` is the surface and the client module that
browses it, together. A client module without the surface is not registered: it would call routes nothing serves.

## §P3. Profile-only node kinds

All of them are declared by this profile and none exists in the core (core §5.9). A `core` reader
encountering one refuses it as `node-kind-unknown`, which is correct (core §8.3).

### §P3.1 `clientShell`

| Member | Type | Presence |
|---|---|---|
| `home` | array of home entry objects | OPTIONAL (6.5); absent when the shell declares none, never empty |

A home entry object:

| Member | Type | Presence |
|---|---|---|
| `page` | string — the `id` of a `pageRef` in the same composition | REQUIRED |
| `title` | string | REQUIRED |
| `description` | string | OPTIONAL |

The composition's client-side shell: the navigation surface and toolkit the bundled client is framed
by. It is a leaf. A composition carrying one declares the root's `shortName` (§P4.55).

`home` is what the client's home page links to (6.5): one entry per published page, in the composition's order, each
shown under its `title` with its `description` and leading to the page it names. The array carries the composition's
order (core §4.6), and a host shows the entries in it. An absent member is a shell whose home page links nothing of the
composition's own; an empty array is refused, because "declares none" has one spelling. An entry is a link and never an
access grant: a reader who follows it meets the page's own audience (§P3.3).

### §P3.2 `layoutRef`

| Member | Type | Presence |
|---|---|---|
| `name` | string | REQUIRED |
| `view` | profile token (§P2.4), `fuaranUI` only | OPTIONAL |
| `viewSpec` | citation object (core §6) | per §P2.4, when `view` is present |

Names a server-side rendering layout the composition selects. `name` MUST be non-empty. Its one
admissible child is the `entityReads` naming what it reads (§P3.10, §P4.1).

A layout carrying `view` is a typed tree: its frame is a view written against the cited UI contract,
rendered on the server, and the page it frames is rendered within it. `view` is `fuaranUI` when present
— a layout is rendered before any client runs, and a view composed outside anything this composition
types is what an absent `view` already says — and any other token is a malformed node. An absent `view`
is a layout the deployment writes itself, exactly as before 2.3.

The value space is deliberately **not** registered: which layouts exist is a property of a particular
deployment's own content, not of the evaluator family, and a closed list here would go stale on
somebody else's schedule.

### §P3.3 `pageRef`

| Member | Type | Presence |
|---|---|---|
| `route` | string | REQUIRED |
| `audience` | profile token (§P2.7) | REQUIRED |
| `roles` | array of string | REQUIRED when `audience` is `scope`; absent otherwise |
| `relationship` | string | REQUIRED when `audience` is `client`; absent otherwise |
| `readers` | array of string | REQUIRED when `audience` is `publication`; absent otherwise |
| `publishingScope` | string | REQUIRED when `audience` is `publication`; absent otherwise |
| `view` | profile token (§P2.4) | REQUIRED |
| `viewSpec` | citation object (core §6) | per §P2.4, exactly as on a `moduleRef` |
| `title` | string | OPTIONAL |
| `layout` | string | OPTIONAL |
| `captures` | object: capture name → profile token (§P2.11) | OPTIONAL; non-empty when present |

A page the composition **publishes**: rendered server-side and served at `route`, to the readers
`audience` names. Its one admissible child is the `entityReads` naming what it reads (§P3.10, §P4.1). A `layoutRef` names a template a page is framed by; a `pageRef` is the
content and who may read it.

`route` is a `/`-separated path with no leading or trailing `/`. A segment written `{name}` captures one
path segment, so one page may serve a family of paths; every other segment is literal. `audience` is
REQUIRED rather than defaulting: who may read a published page is the question an access rule asks of
it, and a composition that never answered would read exactly like one that answered `public`.

A member the audience token does not take — `roles` beside anything but `scope`, `relationship` beside
anything but `client`, `readers` or `publishingScope` beside anything but `publication` — makes the
document say two things, and is refused as a malformed node.

`title` is the page's title. `layout` is the `name` of the `layoutRef` the page is framed by; a page
naming none is framed by the first `layoutRef` the composition declares. `captures` declares the type
of each named capture, its members in the order written: a capture it does not name is an untyped
segment, and an empty `captures` object is a malformed node — "no typed capture" has one spelling, the
absent member.

**What serving a page obliges.** Two things, both stated here because an evaluator that left them to
its own route matching would serve pages the composition never declared:

- **A typed capture is a match condition.** A path whose segment at a typed capture is not of the
  declared type (§P2.11) is not served by that page: matching continues with the next page, and a path
  no declared page serves is served by whatever the host serves for an unknown path — never by a page
  rendered for a value that does not exist.
- **A literal route segment takes precedence over a capture.** Where two pages' routes have the same
  number of segments, the evaluator tries them so that, at the first segment where they differ, the one
  whose segment is literal is tried before the one whose segment captures; pages equal on that test are
  tried in declaration order. So `insights/latest` is served by its own page whatever order it is
  declared in beside `insights/{date}`, and the capture still serves every other segment.

**What serving a `publication` page obliges (2.4).** Three things, because each is what separates a
reader from an application user:

- **Only the readers are admitted.** A reader holding none of `readers` is refused, whatever else it
  holds — an application role, a team membership, an administrative role.
- **The page reads the publishing scope, never the reader's.** The content a `publication` page serves
  is the content of the scope `publishingScope` binds, read on the reader's behalf.
- **A reader admitted only by a publication is confined to it.** A reader the host admits because it
  holds a publication reader role, and for no other reason, is refused every other gated page and every
  other surface the application serves to an identified caller. It is served the `public` pages and the
  `publication` pages naming one of its roles, and nothing else.

### §P3.4 `groundedNarrative`

| Member | Type | Presence |
|---|---|---|
| `displayName` | string | REQUIRED |
| `moduleId` | string | REQUIRED |
| `pageRoute` | string | OPTIONAL |
| `principal` | string | REQUIRED |
| `title` | string | REQUIRED |
| `prompt` | string | REQUIRED |
| `dependsOnMetrics` | array of string | REQUIRED; may be empty |
| `sections` | array of section objects | REQUIRED |
| `allowsConjecture` | the format's boolean string, `"true"` or `"false"` | OPTIONAL (6.7); omitted when `"false"` |
| `tags` | array of string | REQUIRED; may be empty |
| `surface` | profile token (§P2.9) | REQUIRED |
| `onCommit` | array of string | REQUIRED; may be empty |
| `page` | string | OPTIONAL |
| `cadence` | string | OPTIONAL; carried exactly when `runsAs` is (6.3, §P4.24) |
| `runsAs` | string | OPTIONAL; carried exactly when `cadence` is (6.3, §P4.24) |

A section object is `{ "id": string, "heading": string, "guidance": string }`, all three REQUIRED, in
that order.

A **grounded narrative run** the composition registers: a report a model writes from the facts the
host holds, published only if every numeric claim in it is a reference to a current fact that the
surface may disclose. The node's `id` is the run's stable key. Its one admissible child is the
`entityReads` naming the owned entities it reads (§P3.10, §P4.1).

- `displayName` names the run to an operator; `title` is the published narrative's title and each
  section's `heading` its heading — structure the composition authors, never the model. `prompt` is the
  instruction the model works from, and each section's `guidance` what that section's prose covers.
- `moduleId` is the `id` of the `moduleRef` the narrative is attributed to, and `principal` the identity
  the run reads facts and publishes as. `pageRoute` is a route within that unit the stored narrative is
  attributed to; it is provenance and serves nothing.
- `dependsOnMetrics` are the ids of the registered metrics the narrative is written from. A host
  re-runs the narrative when a fact of one of them changes under a resolved request scope. Empty means
  the run reacts to no fact change.
- `onCommit` are the ids of declared fact tables whose committed write enqueues the run, under the
  scope the commit was made in. A commit made off any resolved scope enqueues nothing: a run over an
  unresolved scope could cite nothing a reader cares about. Empty means no commit enqueues it.
- `page`, when present, is the `id` of a `pageRef` that serves the run's latest published narrative to
  the readers that page's audience admits, in each reader's own scope.
- `cadence`, when present, is a five-field cron expression (§P4.62) on each tick of which the host runs the
  narrative, as the `subscriptionPrincipal` named by `runsAs` (6.3): the run reads and publishes in the scope
  the host's resolver maps that principal to, exactly as a pull or a watch runs as one (§P3.8). A schedule has
  no request whose scope it could inherit, so a cadence without a principal names no scope to run in, and a
  principal without a cadence names nothing to run. A cadence is a trigger beside `dependsOnMetrics` and
  `onCommit`, never instead of them: a run may be scheduled and triggered by a change at once.
- `allowsConjecture`, when `"true"`, permits the run to state a hypothesis the facts do not establish (6.7). A host
  publishes such a statement only marked as a conjecture, distinct from the narrative's grounded prose, and the mark
  does not relax the publication check: a conjecture carries no number, and every number the narrative states
  elsewhere is still a reference to a current fact the surface may disclose. Absent, the run states only what its
  facts support and a host offers the model no way to mark a conjecture. Whether a report may carry a hypothesis is
  part of what it promises its readers, so it is the composition's to state and is attested with the rest of the
  node, never switched on by host code the composition does not show.

  The member has one written spelling for each meaning. A writer writes `"true"`, and omits the member when the
  narrative may not conjecture; it never writes `"false"`. A reader reads an absent member as `"false"`, and reads a
  present `"false"` — one of the two spellings §4.5 of the core admits for a boolean-valued member — as the same
  absence, so it re-encodes without the member. Any other spelling, a JSON literal among them, is refused as
  `boolean-spelling-invalid` (core §4.5).

Neither `dependsOnMetrics` nor `onCommit` narrows what the run READS. The model reads every fact its
scope holds through the host's fact-reading tools, under each fact's disclosure stance; a member that
claimed a narrower read would claim a restriction this profile cannot oblige a host to enforce.

An absent required array is a malformed node, and so is a non-string entry: "depends on no metric" is
written `[]`, one spelling for one meaning.

### §P3.5 `aiFunding`

| Member | Type | Presence |
|---|---|---|
| `policy` | profile token (§P2.10) | REQUIRED |
| `keyEnv` | string | OPTIONAL |

**Who pays** for the model inference the `companionSelection` holding it binds. It is the child of an
`aiProvider` selection and of nothing else (§P4.1), and a leaf.

- `policy` says who pays (§P2.10).
- `keyEnv`, when present, names the **environment variable** the host's own key is read from when the
  host starts — the entry a deployment's environment contract declares. It names a place, never a
  value: no key is ever carried by a composition. A caller's own key is read from the composition's
  secret store, under the name that caller's provider configuration records, so no member names it.

`keyEnv` is absent exactly when the host never pays. Its presence beside a policy under which the host
pays is §P4.26's rule rather than this table's, because the two members are read together.

### §P3.6 `referenceData`

| Member | Type | Presence |
|---|---|---|
| `source` | string | REQUIRED |
| `contentHash` | string | REQUIRED |
| `columns` | array of column objects | REQUIRED |

A column object is `{ "name": string, "type": profile token (§P2.12) }`, both REQUIRED, in that order.

A **reference dataset** the composition declares: authoritative tabular data the host loads, verifies and
reads, and never writes. The node's `id` is the dataset's identifier — the name a subject definition's
`calendar` gives it (§P4.33). It is a leaf.

- `source` is the dataset's file: a relative path under the application's root (§P4.32).
- `contentHash` is `sha256:` followed by the 64 lower-case hexadecimal digits of the SHA-256 of the
  file's **raw bytes** — before any decoding, so two files that differ in a byte order mark or a line
  separator are two datasets.
- `columns` are the dataset's columns, in order.

**The dialect.** The file is UTF-8 text, one record per line, lines separated by LF or CR LF, the last
line's separator optional. Lines BEFORE the header whose first character is `#` are comments: they are
hashed with the rest of the file and otherwise ignored, so a dataset can say where its bytes came from
inside the bytes the hash pins. The first line that is not a comment is the header: the column names, in
`columns` order, separated by `,`. Every further line is one record of one field per column, separated by
`,` — a `#` there is an ordinary character. There is no quoting, so no field carries a `,`, a `"` or a
line separator. A blank line is a record of one empty field.

**What serving obliges.** Before it serves anything, a host reads every declared dataset and refuses to
start when the file is absent, its bytes do not hash to `contentHash`, its header is not the declared
names in order, a record's field count is not the column count, or a cell does not read as its column's
type (§P2.12). A host never serves over a dataset other than the one the composition declares, so
replacing a dataset is a change to the composition — the file, and the hash that pins it — and to
nothing else.

### §P3.7 What a unit's owned entities and AI exposure oblige *(the `moduleRef` members of 3.1)*

`entities` and `aiExposure` are core `moduleRef` members (core §5.4); this section states what a host of
this profile does with them. An **absent `aiExposure` is `notQueryable`**: a unit reaches a model through
the host's cross-unit AI surface only when its composition says so, never as a side effect of being
composed. A host honours the declared exposure exactly: a `notQueryable` unit's data is listed to no model
and every reach into it is refused; a `queryable` unit's data is reachable, under the host's access
control, by every caller already admitted to the unit. A host carries every classified field to its read
and egress gates with the field's level, audit flag and key, and invents no classification the
composition does not state. A host that would let the AI surface reach a unit owning an entity whose
disclosure class is not `surfaceable` refuses to start, naming the unit and the entity; a composition
meeting §P4.35 never reduces to one.

### §P3.9 What a unit's disclosure policies oblige *(the `moduleRef` member of 3.3)*

`disclosurePolicies` is a core `moduleRef` member (core §5.4); this section states what a host of this
profile does with it. A host registers every declared policy with the gate that decides a fact's
disclosure at each egress surface, so a fact whose disclosure is `restricted` under a declared policy is
disclosed at exactly the surfaces that policy permits and denied at every other. A **`restricted` policy
reference no unit declares is denied at every surface**: an unrecognised policy never fails open, and a
host neither refuses the composition for it nor invents a stance for it. A policy's `contributorScope`
names the contributing party it belongs to, a data-provenance fact a caller can never assert; a host
carries it unchanged. A host registers no policy for a composition that selects no fact tier, because
there is no gate for it to govern.

### §P3.8 `dataDelivery`

| Member | Type | Presence |
|---|---|---|
| `source` | string | REQUIRED |
| `mode` | profile token (§P2.15) | REQUIRED |
| `module` | string | OPTIONAL; carried by an `upload` and no other mode (6.2, §P4.57) |
| `principal` | string | OPTIONAL; carried by a `scheduledPull` and a `watched` delivery (6.2) |
| `cadence` | string | OPTIONAL; carried by a `scheduledPull` (6.2) |
| `location` | string | OPTIONAL; carried by a `watched` delivery (6.2) |
| `tables` | array of string | OPTIONAL; non-empty when present; carried by a `scheduledPull` (6.2) |

A **delivery**: how the data of the source named by `source` — the `id` of a `connectorRef` — arrives. An
`upload` names the `moduleRef` whose own route serves it in `module`. A `scheduledPull` and a `watched`
delivery are run by the host itself, as the `subscriptionPrincipal` named by `principal`: a pull reads each of
`tables` from the source on `cadence`, and a watch ingests, once, each object that lands at `location` — a prefix
inside the principal's own scope in the host's blob store. A host seeds the source's configuration at the
principal's scope, so neither carries a scope of its own. It is a leaf.

The route is the unit's: it lives in the unit's body, which the composition carries opaquely, and a host
emits no endpoint for a delivery. What the declaration obliges is the composition the route needs — the
source configured for the host's ingestion and, for an `upload`, the blob store the file lands in and a
connector that reads it; for a pull or a watch, the principal, the scheduler it runs on and a connector the
host can run it through — which §P4.36–§P4.39 and §P4.57–§P4.61 check. A host that cannot honour a delivery's mode says so
rather than serving it as declared.

A delivery says how data arrives and nothing about what its arrival triggers: what a committed arrival
enqueues is stated by the node that consumes it, never by the delivery.

### §P3.10 `entityReads`

| Member | Type | Presence |
|---|---|---|
| `reads` | array of edge objects | REQUIRED; may be empty |

An edge object is `{ "module": string, "entity": string }`, both REQUIRED, in that order: `module` is the
`id` of a `moduleRef` and `entity` the `name` of an entity that unit owns (core §5.4's `entities`).

The **owned entities a surface reads**, as typed edges. It is the child of the surface it describes — a
`pageRef`, a `layoutRef`, a `groundedNarrative`, or a `companionSelection` whose `slot` is `vectorStore`
or `embeddingProvider` (§P4.1) — and a leaf. An empty `reads` declares that the surface reads no owned
entity, which is a real stance; the node's absence is the one spelling of "the surface says nothing about
what it reads", exactly as before 3.4.

**What it obliges.** An edge is checked by the composition (§P4.42–§P4.44) and honoured by the
composition's author: none of the surfaces it hangs from carries a read scope a host of this profile
could hold it to, so a host reads the node, enforces nothing from it, and invents no restriction it does
not state. A model-facing tool a unit's body registers states its edges in that body (the module
document's `reads`), where the composition meets the body rather than in this tree.

### §P3.11 `reportTemplate`

| Member | Type | Presence |
|---|---|---|
| `displayName` | string | REQUIRED |
| `format` | profile token (§P2.18) | REQUIRED |
| `body` | string | REQUIRED for a text body; absent for a binary one |
| `source` | string | REQUIRED for a binary body; absent for a text one (3.7) |
| `contentHash` | string | REQUIRED for a binary body; absent for a text one (3.7) |
| `placeholders` | array of placeholder objects | REQUIRED; may be empty |

A placeholder object is, in this order:

| Member | Type | Presence |
|---|---|---|
| `key` | string | REQUIRED |
| `displayName` | string | REQUIRED |
| `kind` | profile token (§P2.19) | REQUIRED |
| `hint` | string | OPTIONAL |
| `columns` | array of column objects | OPTIONAL; non-empty when present |
| `required` | the format's boolean string, `"true"` or `"false"` | REQUIRED |

A column object is `{ "key": string, "displayName": string, "kind": profile token (§P2.20), "hint":
string }`, in that order, the first three REQUIRED and `hint` OPTIONAL.

A **report template** the composition declares: the document a report is rendered into. The node's `id`
is the template's id. It is a leaf. Its body is written in `format` and holds each placeholder as a
`{{key}}` token; a host renders a report by substituting each placeholder's value for its token. A
`required` placeholder is one a render refuses to proceed without.

The body takes one of two forms, and a node carries exactly one of them; a node carrying both, or
neither, is a malformed node.

- A **text body** is `body`, the template's text.
- A **binary body** (3.7) is a file: `source`, a relative path under the application's root, and
  `contentHash`, `sha256:` followed by the 64 lower-case hexadecimal digits of the SHA-256 of the file's
  **raw bytes** — the spelling of a reference dataset's (§P3.6). Which form a template takes is its
  format's (§P4.53).

**What serving obliges, for a binary body.** Before it serves anything, a host reads every binary body a
standing subscription renders and refuses to start when the file is absent or its bytes do not hash to
`contentHash` — the check §P3.6 obliges of a reference dataset. A host never renders a template other than
the one the composition declares, so replacing a binary template is a change to the composition — the
file, and the hash that pins it — and to nothing else.

`placeholders` is written `[]` when the template declares none; its absence is a malformed node. A
placeholder that declares no column omits `columns`, and an empty `columns` array is a malformed node:
one spelling for one meaning. Which of `hint` and `columns` a placeholder carries is its kind's
(§P4.51).

### §P3.12 `reportProducer`

| Member | Type | Presence |
|---|---|---|
| `displayName` | string | REQUIRED |
| `narrative` | string | REQUIRED |
| `template` | string | REQUIRED |
| `placeholder` | string | REQUIRED |

A **report producer**: what fills a report. The node's `id` is the producer's key. It is a leaf.
`narrative` is the `id` of a `groundedNarrative` (§P3.4) whose latest run is the producer's value;
`template` is the `id` of the `reportTemplate` it renders into, and `placeholder` the `key` of the one
placeholder of that template the narrative fills (§P4.46, §P4.47).

### §P3.13 `subscriptionPrincipal`

| Member | Type | Presence |
|---|---|---|
| `userId` | string | REQUIRED |
| `displayName` | string | REQUIRED |

A **subscription principal**: an identity declared work runs as — a standing subscription, a pull or a watch
(§P3.8), or a scheduled grounded narrative (§P3.4, 6.3). The node's `id` is the
principal's name. It is a leaf. `userId` is the identity a host's resolver maps to a scope; whether it
resolves, and to what, is the deployment's question, which a host answers at startup and not the
composition's.

### §P3.14 `standingSubscription`

| Member | Type | Presence |
|---|---|---|
| `displayName` | string | REQUIRED |
| `producer` | string | REQUIRED |
| `template` | string | REQUIRED |
| `parameters` | array of parameter objects | OPTIONAL; non-empty when present |
| `schedule` | string | REQUIRED |
| `principal` | string | REQUIRED |
| `recipients` | array of string | REQUIRED |
| `format` | profile token (§P2.18) | REQUIRED |

A parameter object is `{ "name": string, "value": string }`, both REQUIRED, in that order.

A **standing report subscription**: a report rendered on a schedule and delivered to named recipients.
The node's `id` is the subscription's key (§P4.45). It is a leaf. On each tick of `schedule` (§P4.49) a
host runs the `reportProducer` named by `producer`, as the `subscriptionPrincipal` named by `principal`,
renders the `reportTemplate` named by `template` to `format`, and delivers the result as a transactional
message to each of `recipients` — identities, resolved to an address by the host — through the selected
`transactionalSink` (§P4.50). `parameters` are the named values the subscription binds for its run.

A subscription binding no parameter omits `parameters`, and an empty `parameters` array is a malformed
node. `recipients` is REQUIRED, so its absence and a non-string entry are malformed nodes; an empty
array reads, and is refused by §P4.48 — "deliver to nobody" is a declaration error, not a spelling.

### §P3.15 `factBrowse`

| Member | Type | Presence |
|---|---|---|
| `mount` | profile token (§P2.21) | OPTIONAL |
| `label` | string | OPTIONAL; absent under `serverOnly` |
| `group` | string | OPTIONAL; absent under `serverOnly` |

A **fact browse**: the host's direct reading of its fact tier (§P2.5's `FactStore`), composed into the
application (6.4). A host mounts the browse surface on its server — the declared fact tables, each table's run
history, a table's rows a page at a time with one fact's provenance, and one metric's series over a subject branch
across a period range, the numbers a chart is drawn from — and, unless `mount` is `serverOnly`, adds to the bundled
client the module that browses it. It is a leaf, and a composition carries at most one (§P4.1).

`label` is the client module's name and `group` the navigation group it is filed under. Each is a display fact and
never an access gate, and an absent one leaves the host's own. A `group` no other unit is filed under is a group of
its own, not a defect: an application names its groups by filing modules in them. A `serverOnly` browse adds no
client module, so it carries neither member (§P4.66).

The two halves are one declaration because a client module without the surface calls routes nothing serves. The
surface without the module is a real composition — a view of the application's own reads the series to draw a
chart, and the navigation lists no tables — and is spelled `serverOnly`.

A host serves the surface only to callers its access control admits, and discloses each fact there exactly as the
fact's disclosure admits the `browse` surface (§P2.17, §P3.9). A declaration composes a reading; it never widens what
may be read.

### §P3.16 `theme`

| Member | Type | Presence |
|---|---|---|
| `source` | string | REQUIRED |
| `contentHash` | string | REQUIRED |

A **house theme** the composition declares (6.6): the presentation values — colours, type, spacing — every surface
the host renders takes, from one file. It is a leaf, and a composition carries at most one (§P4.1).

- `source` is the theme's file: a relative path under the application's root, in §P4.32's grammar.
- `contentHash` is `sha256:` followed by the 64 lower-case hexadecimal digits of the SHA-256 of the file's **raw
  bytes**, as §P3.6 states it for a dataset.

**The file.** A theme manifest: a JSON object whose `tokens` member is an object of groups, nested to any depth,
whose leaves are design tokens — each an object carrying a string `$value`. A token's name is the dotted path of
member names from `tokens` to the leaf (`tone.brand.fg`). Every other member of the manifest, and every other member
of a token, is the theme format's own and states nothing a host is obliged to apply.

**What applying obliges.** A host reads the declared theme when it reduces the composition, and refuses to reduce when
the file is absent, its bytes do not hash to `contentHash`, or it is not a theme manifest. It then applies each
declared token to every surface it renders — the bundled client and every page it renders on the server — so one
declaration styles them all. A token the manifest does not declare keeps the host's own value, so a theme states only
what differs. Which presentation value a token name governs is the host's vocabulary, not this profile's; a host
refuses a token name it governs no value for only where that name is inside a family it does style, since a misspelt
token there would otherwise be lost without a word. Replacing a theme is a change to the composition — the file and
the hash that pins it — and to nothing else, exactly as for a dataset (§P3.6).

The theme is a declaration and never a file an application author edits beside the host's output: the surfaces a host
generates read it, and a hand-written stylesheet reaches only the code that imports it.

### §P3.17 `uiControl`

| Member | Type | Presence |
|---|---|---|
| `fastPath` | the format's boolean string, `"true"` or `"false"` | REQUIRED |
| `triage` | the format's boolean string, `"true"` or `"false"` | OPTIONAL; omitted when `"false"` |
| `triageProvider` | string | OPTIONAL |
| `caseBase` | the format's boolean string, `"true"` or `"false"` | OPTIONAL; omitted when `"false"` |
| `voice` | the format's boolean string, `"true"` or `"false"` | OPTIONAL; omitted when `"false"` |
| `allow` | array of allow entries | REQUIRED |

**What an assistant may operate** in the application's client (6.8): the allowlist of the controls an assistant may
drive on a user's behalf, and the switches for the ways a host may resolve an instruction to them. It is a leaf, and
a composition carries at most one (§P4.1). The allowlist is POLICY — it belongs with the composition and is attested
with it. The controls themselves are not: the views that render them declare them, and a composition names them by
identifier.

- `fastPath` — when `"true"`, a host resolves an instruction that names an allowed control to that control without a
  round trip to a model, and dispatches it in the client. When `"false"`, the allowlist still bounds what an
  assistant may drive, and every instruction takes the host's ordinary path.
- `triage`, when `"true"`, permits a host to consult a lower-cost model to resolve an instruction the direct path did
  not, before its ordinary path; `triageProvider` names the selection that serves it (§P4.75), and when it is absent
  the host's own provider for the turn serves. `triageProvider` is written only beside `triage` (§P4.74).
- `caseBase`, when `"true"`, permits a host to resolve an instruction from paraphrases it has learned or been given.
- `voice`, when `"true"`, offers the user a spoken instruction beside the typed one, resolved exactly as a typed one.

Each `allow` entry is an object of one of two shapes, its members in the order below:

| Member | Type | Presence |
|---|---|---|
| `tree` | string | REQUIRED in a tree entry |
| `affordances` | array of strings | REQUIRED in a tree entry |
| `navigate` | the format's boolean string | OPTIONAL in a tree entry; omitted when `"false"` |
| `module` | string | REQUIRED in a unit entry |
| `fields` | array of strings | REQUIRED in a unit entry |
| `buttons` | array of strings | REQUIRED in a unit entry |
| `pages` | the format's boolean string | OPTIONAL in a unit entry; omitted when `"false"` |

A **tree entry** names a rendered tree by identifier and the controls of it an assistant may operate, each by the
identifier its view derives for it; `navigate` permits the assistant to open the tree's own targets. A **unit entry**
names a `moduleRef` and, by name, the fields of it an assistant may set and the buttons it may press; `pages` permits
it to move between the unit's pages. An entry carrying both `tree` and `module`, or neither, is a malformed node.

**What it obliges.** A host enforces the allowlist as DEFAULT-DENY: a control no entry names is refused, a unit entry
grants exactly the fields and buttons it names and no other member of the unit, and the host's own administrative
surfaces are refused whatever an entry says. The host enforces it on every path by which an assistant reaches a
control — the resolution in the client and the assistant's own tool calls on the server alike — from this one
declaration, so the two cannot disagree. A switch a host does not implement is one it leaves off, and never one it
widens: a host that cannot resolve an instruction directly takes its ordinary path for every instruction, and the
allowlist still binds it.

## §P4. Composition rules

Each rule names the class it is refused under. All are profile classes (core §9.2) and none is
numbered into the core set.

### §P4.1 Containment — `containment-invalid`

| Parent | Admissible children |
|---|---|
| `appRoot` (root) | `serverConfig`, `dataType`, `moduleRef`, `clientShell`, `portAllocation`, `layoutRef`, `pageRef`, `groundedNarrative`, `referenceData`, `dataDelivery`, `reportTemplate`, `reportProducer`, `subscriptionPrincipal`, `standingSubscription`, `factBrowse`, `theme`, `uiControl`, `extensionRef` |
| `serverConfig` | `companionSelection`, `connectorRef`, `configKnob` |
| `companionSelection` whose `slot` is `aiProvider` | `aiFunding` |
| `companionSelection` whose `slot` is `vectorStore` or `embeddingProvider` | `entityReads` |
| `pageRef`, `layoutRef`, `groundedNarrative` | `entityReads` |
| everything else | none — every other kind is a leaf |

At most one `serverConfig`, at most one `clientShell`, at most one `portAllocation`, at most one
`factBrowse`, at most one `theme` and at most one `uiControl` per composition, at most one `aiFunding` per `companionSelection`, and at most one `entityReads` per
surface.

### §P4.2 Layers — `layer-duplicate`

A layer token appears **at most once** in `layers`. Order is the application order and is preserved
(core §5.1); this profile states no required ordering, because the layers it registers commute.

`rag` **subsumes** `ai`: a composition carrying both is well-formed and the `ai` layer is redundant.
This profile does not refuse it — a redundant layer is a tidiness question, and refusing it would
break compositions that are correct.

### §P4.3 The client shell needs a base that bundles a client — `client-shell-unsupported-base`

A `clientShell` node is admissible only under base `serverApp`. Under
`publicRenderingServerApp` there is no client to shell, and a composition carrying one is stating
something the host cannot do.

The same applies to `portAllocation.client`: under `publicRenderingServerApp` there is no
development-time client toolchain to serve, so the member SHOULD be absent. It is a SHOULD rather
than a MUST because a port reservation is a deployment fact that may be held for a reason outside
the composition, and refusing a reservation is a poor trade for a fact nobody has to act on.

### §P4.4 An undeclared surface kind derives as `view`

Where a `moduleRef` carries no `surfaceKind` (core §5.4), this profile **derives** `view`.

The derivation is the profile's, and it is not the same thing as the composition having said so. It
is `view` because every module reference in this profile carries a client view technology and nothing
else, so what an unannotated surface is read as comes off the client tier. A composition that means
`webhook` or `api` declares it; the derivation is what a reader does with silence, not a default the
document contains.

### §P4.5 Citation obligation

Restating §P2.4 as a rule so it is in one list with the others: `fuaranUI` requires a `viewSpec`;
`bareFeliz` and `custom` MUST NOT carry one (`citation-unexpected`). A `fuaranUI` node carrying no
`viewSpec` is **accepted** as a migration read (core §9.3) and re-emitted with the reader's current
revision; a writer MUST NOT produce one.

### §P4.6 Identity

This profile requires that a `moduleRef`'s `id` is the identity by which the composed unit is known
to the build — the same string, not a derivation of it. Nothing enforces it at read time, and it is
stated because an implementation that synthesises module ids from tree position will produce
compositions that stop resolving the first time a module moves.

### §P4.7 A declared fact table needs a fact tier — `fact-store-absent`

A `moduleRef` carrying `factTables` (core §5.4) is admissible only in a composition whose `FactStore`
setting (§P2.5) is `EnabledFactStore`. A unit that declares tables in a composition with no fact tier
is declaring writes the host has nowhere to put, and reducing it would produce a unit whose output
silently goes nowhere.

### §P4.8 One writer per fact table — `fact-table-duplicate`

A fact-table `id` is declared **at most once** in a composition, across every `moduleRef`. A table
with two declared writers has no single account of what produced its rows.

### §P4.9 A knowledge base composes over model inference — `knowledge-base-without-ai`

A `knowledgeBase` layer is admissible only in a composition whose `layers` also carry `ai` or `rag`
(which subsumes `ai`, §P4.2). The knowledge base is applied to the model-inference tier — it is what
that tier answers from — so a composition carrying it with no such tier names a capability with
nothing to attach to, however completely its seams are bound.

The rule constrains layers, not seams. Which seams a `knowledgeBase` composition binds (the
model-provider and embedding seams it ingests and answers through) is the evaluator's completeness
question, exactly as it is for `ai` and `rag`; this profile states no seam requirement for any layer.

### §P4.10 The rendering layer composes onto a client-bearing base — `public-rendering-layer-unsupported-base`

A `publicRendering` layer is admissible only under base `serverApp`. The base
`publicRenderingServerApp` already renders server-side; carrying the layer as well would compose
rendering a second time onto a host that has it.

### §P4.11 A page needs a rendering tier — `page-without-rendering-tier`

A `pageRef` is admissible only in a composition that renders server-side: base
`publicRenderingServerApp`, or a `publicRendering` layer. A page in any other composition is a page
nothing serves.

### §P4.12 A page is rendered on the server — `page-view-client-only`

A `pageRef`'s `view` is `fuaranUI` or `custom`. `bareFeliz` names the host's client view technology,
and a page is rendered before any client runs.

Whether a gated page has an identity seam bound to gate WITH is the evaluator's completeness question,
as every seam requirement is (§P4.9); this profile states the audience, and the evaluator says what
honouring it takes.

### §P4.13 A fact-store backend needs a fact tier — `fact-store-backend-without-tier`

A `FactStoreBackend` setting (§P2.5) whose value is not the default `Blob` is admissible only in a
composition whose `FactStore` setting is `EnabledFactStore`. A backend chosen for a fact tier the
composition does not compose names a store nothing writes to.

### §P4.14 A declared fact table states its definition — `fact-table-undefined`

Every fact table a `moduleRef` declares carries a `definition` (core §5.4). A table named only by its
references leaves a host that declares tables to be written with nothing to declare it from, and an
evaluator that reduced it anyway would have to invent the subject level, the shapes, the grain, the
cadence and the rest — which is the outcome this rule exists to refuse.

### §P4.15 A definition agrees with its references — `fact-table-definition-disagrees`

A defined table's reference members restate its definition: `subjects` is exactly the definition's
`hierarchy`, `metrics` is the columns' `metric`s in column order, and `grains` is exactly its
`periodGrain`. The references are kept because the core requires them; a table that says two
different things about what it covers has said neither.

### §P4.16 A case that takes an argument has it, and no other does — `fact-definition-argument`

A disclosure carries `policy` exactly when its `class` is `restricted`; a staleness object carries
`windowSeconds` exactly when its `policy` is `freshFor`; a roll-up object carries `tolerance` exactly
when its `kind` is `additive`, and the tolerance is a decimal literal (an optional `-`, digits, and
optionally a `.` followed by digits).

### §P4.17 A definition is well-formed — `fact-table-malformed`

A defined table names a non-empty `producingOperation`, declares at least one column, names no metric
in two columns, and has a positive `refreshCadenceSeconds`.

### §P4.18 A column's metric is registered — `fact-table-metric-unregistered`

Every column's `metric` is the `id` of a metric some `moduleRef` in the composition registers in its
`metricDefinitions`. A column for a metric nothing defines has no unit, direction or staleness to be
written under.

### §P4.19 A table's hierarchy and level are registered — `fact-table-subject-level-unknown`

A defined table's `hierarchy` is the `id` of a subject hierarchy some `moduleRef` registers in its
`subjectDefinitions`, and its `level` is one of that hierarchy's `levels`. A row's subject is a path
from the hierarchy's root down to the table's level, so a level the hierarchy does not have places no
row anywhere.

### §P4.20 One unit per definition — `grounding-definition-duplicate`

A metric `id` is registered by at most one `moduleRef`, and so is a subject hierarchy `id`. Two units
defining one identity give a host two definitions with nothing to choose between them. One unit
registering an identity twice is not refused here, and a writer has no reason to produce it.

### §P4.21 A grounded narrative composes over model inference and a fact tier — `narrative-tier-absent`

A `groundedNarrative` is admissible only in a composition whose `layers` carry `ai` or `rag` (the
narrative is written by a model) and whose `FactStore` setting is `EnabledFactStore` (its numbers are
references to facts, and its check is the fact tier's). Which seams a run that reacts to a change needs
bound — a scheduler to enqueue it on — is the evaluator's completeness question (§P4.9).

### §P4.22 What a narrative names is declared — `narrative-reference-undeclared`

A `groundedNarrative`'s `moduleId` is the `id` of a `moduleRef` in the composition; every `onCommit`
entry is the `id` of a fact table some `moduleRef` declares; every `dependsOnMetrics` entry is the `id`
of a metric some `moduleRef` registers in its `metricDefinitions`; `page`, when present, is the `id`
of a `pageRef`; and `runsAs`, when present, is the `id` of a `subscriptionPrincipal` (6.3). A narrative naming
something the composition does not declare would read, or publish to, something that is not there, or run as
nobody.

### §P4.23 A narrative serves one page at a literal route — `narrative-page-invalid`

The `pageRef` a `groundedNarrative` names as its `page` has a route with no `{name}` capture, and no two
`groundedNarrative`s name the same `page`. The page serves the run's LATEST narrative: a captured route
names a family of pages that no single narrative chooses between, and two runs serving one page would
give it two latest narratives.

### §P4.24 A narrative is well-formed — `narrative-malformed`

A `groundedNarrative` has a non-empty `displayName`, `principal`, `title` and `prompt`; at least one
section; a non-empty `id` and `heading` on every section, and no section `id` twice; no empty tag; no
entry twice in `dependsOnMetrics` or `onCommit`; and `cadence` and `runsAs` both present or both absent (6.3,
§P3.4).

### §P4.25 A funding composes over model inference — `ai-funding-tier-absent`

An `aiFunding` is admissible only in a composition whose `layers` carry `ai` or `rag`. Who pays for model
calls is a question only a host that makes them can answer.

### §P4.26 A funding names the host's key exactly when the host pays — `ai-funding-malformed`

Under `platformOnly` and `permissiveWithPlatformFallback` the `aiFunding` carries `keyEnv`; under
`strictBYOK` it does not. The permissive policy is host-paid for this rule: the host pays for every
caller who has configured no key of their own, so its key is named exactly as under `platformOnly`. `keyEnv` is an environment-variable name: a letter or `_`, then letters,
digits and `_`. A host that pays with no named key boots with a provider it cannot call; a `keyEnv`
under `strictBYOK` names a key nothing reads.

### §P4.27 A caller's own key needs a secret store — `ai-funding-secret-store-absent`

A composition carrying an `aiFunding` whose `policy` is `strictBYOK` or
`permissiveWithPlatformFallback` binds the `secretStore` seam. A caller's own key is read from that
store, and a composition that binds none has nowhere to read it from.

### §P4.28 A page's layout is one the composition registers — `page-layout-unregistered`

A `pageRef`'s `layout`, when present, is the `name` of a `layoutRef` in the same composition. A host
asked to frame a page in a layout nothing registered frames it in some other one, or in none, and
neither is what the composition said.

### §P4.29 A typed capture is a capture of its own route — `page-capture-undeclared`

Each member name of a `pageRef`'s `captures` is the name of a `{name}` segment of that page's `route`.
A type for a segment the route does not capture constrains nothing, and would read as a guard that is
not there.

### §P4.30 A declared title is not blank — `page-title-blank`

A `pageRef`'s `title`, when present, carries a character other than white space. A blank title is "no
title" spelled a second way, and the absent member already says it.

### §P4.31 A publication names its readers and its scope — `page-publication-malformed`

A `publication` page's `readers` is non-empty, names each reader once, and each reader, like its
`publishingScope`, is a NAME: non-empty, and carrying no white space, no control character, no `,` and
no `:`. An empty reader set is a publication nobody may read, and a blank scope is a page with nothing to
read; a host refuses both readers silently, so the composition refuses them first. The two separator
characters are excluded so that every name round-trips through the textual form hosts commonly give an
audience (`publication:<scope>:<reader>,<reader>`).

### §P4.32 A reference dataset is one a host can load — `reference-data-malformed`

A `referenceData` node's `source` is a relative path: one or more segments separated by `/`, no segment
empty, `.` or `..`, and no `\`, `:` or control character anywhere — so it names one file under the
application's root on every host, and cannot name one outside it. Its `contentHash` is `sha256:`
followed by exactly 64 lower-case hexadecimal digits. Its `columns` is non-empty, and each column's
`name` is non-empty, carries no leading or trailing white space, no `,`, no `"` and no control character,
and is named once; the first column's name does not begin with `#`, which would make the header read as a
comment (§P3.6). Each is a declaration a host could only meet at start, as a refusal to serve; the
composition refuses it first. A column type outside §P2.12 is `token-unregistered`.

### §P4.33 A subject's calendar is a declared dataset — `subject-calendar-unresolved`

Every subject definition (core §5.4) that states a `calendar` names the `id` of a `referenceData` node in
the same composition. That dataset is the calendar the subject's periods are counted in, so a fact's
period labels trace to a dataset pinned by its hash rather than to a tag nothing declares. A subject
definition stating no calendar is untouched: its periods are the deployment's default. This is the rule
that made 3.0 a major version (§P1).

### §P4.34 An owned entity is declared once, by one unit, in a form a host can carry — `entity-malformed`

An owned entity's `name` is not blank, and no two `moduleRef`s declare an entity of the same `name`: an
entity's record belongs to the unit that owns it, so a second owner is two declarations of one record. A
unit declares each entity once. An entity's disclosure takes a `policy` exactly when its `class` is
`restricted`, and a stated `policy` is not blank (the §P4.16 rule, applied to an entity). A classified
field's `fieldPath` is not blank and is classified once per entity, and a stated `encryptionKeyId` is not
blank — the absent member already says "the deployment's master key". A `level` outside §P2.13, an
`aiExposure` outside §P2.14 and a disclosure `class` outside §P2.8.3 are `token-unregistered`.

### §P4.35 A unit owning an entity no model may see is not exposed to a model — `entity-exposure-forbidden`

A `moduleRef` whose `aiExposure` is `queryable` owns no entity whose disclosure `class` is other than
`surfaceable`. `internal` data is never an egress output and `restricted` data is governed by a policy a
composition cannot read, so a model reaching either through the AI surface is a route the entity's own
class forbids. At 3.1 the AI route is the unit's: a host reaches a unit's data through the cross-unit AI
surface or not at all, so the refusal names the unit, the entity and the exposure. A unit stating no
`aiExposure`, or `notQueryable`, meets the rule whatever it owns.

### §P4.36 What a delivery names is declared — `delivery-reference-undeclared`

A `dataDelivery` node's `source` is the `id` of a `connectorRef` in the same composition, its `module`
(when present) the `id` of a `moduleRef` in it, and its `principal` (when present, 6.2) the `id` of a
`subscriptionPrincipal` in it. A delivery naming a source nothing declares delivers nothing, and one
naming a unit nothing composes names a route no host runs.

### §P4.37 A source is delivered one way — `delivery-duplicate`

No two `dataDelivery` nodes name the same `source`. Two declarations of how one source's data arrives are
two answers to one question; the composition states one.

### §P4.38 An upload has a blob store to land in — `delivery-store-absent`

A composition carrying a `dataDelivery` whose `mode` is `upload` or (6.2) `watched` selects `blobStorage`
(§P2.3). An uploaded file is stored before it is ingested, and a watched location is a prefix in that store;
where either lands is the composition's choice, not a host default a deployment discovers.

### §P4.39 An upload arrives through a connector that reads files — `delivery-source-unfit`

The `connectorRef` an `upload` delivery names is one the host ingests by reading a file from the selected
blob store. Which connectors those are is the host's population, so a host refuses a delivery through a
connector it does not know to read files — a database, a warehouse or an API reads no uploaded file, and
an upload delivered through one would be stored and never read. The same holds for a `watched` delivery
(6.2), whose objects are files in that store. A `scheduledPull` names a `connectorRef` the host composes a
connector for: a source no composed connector answers is one nothing can pull.

### §P4.40 A disclosure policy is declared once, in a form a host can carry — `disclosure-policy-malformed`

A policy's `policyRef` is not blank, and no two declarations — in one `moduleRef` or in two — name the same
`policyRef`: a host keeps one stance per reference, so a second declaration would silently replace the
first. A policy lists each surface at most once, and a stated `contributorScope` is not blank — the absent
member already says "no per-party claim". A `mode` outside §P2.16 and a surface outside §P2.17 are
`token-unregistered`. A `restricted` disclosure naming a reference no unit declares is **not** refused by
this rule or any other (§P3.9); an implementation MAY report it as a warning.

### §P4.41 A grounded narrative publishes only what its facts' policies permit — `narrative-surface-forbidden`

For each metric a `groundedNarrative` names in `dependsOnMetrics`, consider every fact table whose
`definition` carries a column for that metric, and that column's disclosure (its own, else its table's).
When there is at least one such column and every one is `restricted` under a **declared** policy whose
`permitSurfaces` exclude the narrative's `surface`, the composition is refused, naming the narrative, the
metric, each table and the surface: the host's gate would deny every fact the narrative could cite for
that metric. One column whose disclosure admits the surface — `surfaceable`, or a declared policy that
permits it — meets the rule, because a narrative reads every fact its scope holds. A column under an
undeclared policy, or `internal`, cites no declared policy and is outside this rule.

### §P4.42 A surface's edges are declared once, in a form a composition can check — `entity-reads-malformed`

An edge's `module` and `entity` are not blank, and an `entityReads` lists each edge at most once: one edge
is one statement, and a second copy is the same statement said twice.

### §P4.43 An edge names a declared entity — `entity-read-undeclared`

An edge's `module` is the `id` of a `moduleRef` in the same composition, and its `entity` the `name` of an
entity that unit declares in `entities`. An edge to anything else names a record whose class nothing
states, so nothing could say whether the surface may read it.

### §P4.44 A surface reads no entity its class forbids — `surface-entity-forbidden`

No edge names an entity whose disclosure `class` is other than `surfaceable`. Every surface an
`entityReads` may describe is a route to a model or to a published page, and a class other than
`surfaceable` forbids the entity every such route (§P4.35 states the same of the unit's own AI exposure).
A `restricted` entity's policy is not consulted: a disclosure policy (§P3.9) governs the facts a host's
gate discloses, never a unit's own records. The refusal names the surface, the entity, its unit and the
class. A surface reading a unit that owns such an entity while declaring no `entityReads` is **not**
refused by this rule or any other; the unit's boundary is §P4.35's, and an implementation MAY report the
undeclared edges as a warning.

### §P4.45 A subscription's key is one a host can store under — `subscription-key-invalid`

A `standingSubscription`'s `id` — its key — holds only letters and digits (each a character of Unicode
general category L or Nd), `.`, `-` and `_`. The key becomes part of a storage path and of the
idempotency key of the job that runs it, so a space, a `/` or any other character is refused rather than
escaped.

### §P4.46 What a subscription and a producer name is declared — `subscription-reference-undeclared`

A `standingSubscription`'s `producer` is the `id` of a `reportProducer`, its `template` the `id` of a
`reportTemplate`, and its `principal` the `id` of a `subscriptionPrincipal`, each in the same
composition. A `reportProducer`'s `narrative` is the `id` of a `groundedNarrative` and its `template` the
`id` of a `reportTemplate`. A reference to anything else would run, render or deliver as something that
is not there. Each undeclared reference is one refusal.

### §P4.47 A producer fills a placeholder a narrative can fill, and leaves none required — `producer-placeholder-unfit`

A `reportProducer`'s `placeholder` is the `key` of a placeholder its template declares, and that
placeholder's `kind` is `text`: a narrative is prose, and is supplied to no other kind. No OTHER
placeholder of that template is `required`: the producer supplies one value, so a template requiring a
second could never render under it. (A producer whose `template` names no `reportTemplate` is
§P4.46's, and is not also refused here.)

### §P4.48 A subscription names a recipient — `subscription-recipients-empty`

A `standingSubscription`'s `recipients` holds at least one entry that is not blank. A subscription
delivering to nobody is a declaration error, refused here rather than run on schedule to no effect.

### §P4.49 A subscription's schedule is a cron expression in the registered grammar — `subscription-schedule-invalid`

A `standingSubscription`'s `schedule` is five fields separated by one or more spaces or tabs, in the
order `Minute Hour DayOfMonth Month DayOfWeek`. Each field is one of:

- `*` — every value;
- `*/N` — every Nth value, `N` a positive integer;
- a comma-separated list of integers; or
- one integer.

Every integer is within its field's range: Minute 0–59, Hour 0–23, DayOfMonth 1–31, Month 1–12,
DayOfWeek 0–7, where both 0 and 7 are Sunday. Nothing else is registered: no range (`1-5`), no name
(`MON`, `JAN`), no `?`, `L`, `W` or `#`, and no seconds or years field. `0 6 * * 1` is 06:00 every Monday;
`0 6 * *` (four fields) and `0 6 * * MON` are refused.

### §P4.50 A subscription has an email sink to deliver through and a scheduler to run on — `subscription-sink-absent`, `subscription-scheduler-absent`

A composition declaring a `standingSubscription` selects:

- an **email** `transactionalSink` — a `companionSelection` whose `slot` is `transactionalSink` and whose
  `impl` is `Smtp` or `SendGrid`. The report job delivers a transactional email; a sink of another kind
  (SMS, push, a messaging app) cannot carry it. Its absence is `subscription-sink-absent`. This rule reads
  those two `impl` spellings and registers nothing else about `impl` (§P2.3).
- a `jobScheduler` — the schedule runs on the host's scheduler. Its absence is
  `subscription-scheduler-absent`.

Each is refused once for each subscription that lacks it.

### §P4.51 A placeholder carries what its kind carries — `template-placeholder-malformed`

A placeholder's members are those its `kind` carries, and nothing else:

- `hint` appears only on a `number` or `date` placeholder (a format hint, OPTIONAL there) or an `image`
  placeholder (its MIME type, REQUIRED there). A `text` or `table` placeholder carries no `hint`, and an
  `image` placeholder without one is refused.
- `columns` appears only on a `table` placeholder.

A column is held to the same `hint` rule by its own `kind`. A column whose `kind` is `table` is not this
rule's: §P2.20 does not register it, so it is `token-unregistered`, as is a placeholder or column kind
outside §P2.19 or §P2.20, and a template or subscription `format` outside §P2.18.

### §P4.52 A subscription and its template agree on their format — `subscription-format-unfit`

A `standingSubscription` and the `reportTemplate` it names have the same `format`, unless both are
`markdown` or `html`. A subscription's `format` is the format its report is rendered to (§P3.14), and a
host renders the named template's body in it: a `pdf` subscription over a Markdown body would print
Markdown source as HTML, a Markdown or HTML subscription over a `pdf` template would deliver print HTML as
something else, and a subscription and template disagreeing over `docx` or `xlsx` would read a document's
bytes as text, or text as a document. None fails at the host — each renders, wrongly — so the disagreement
is refused here. A subscription and template that disagree between `markdown` and `html` are not this
rule's, as at 3.5. (A subscription whose `template` names no `reportTemplate` is §P4.46's, and a format
outside §P2.18 is `token-unregistered`'s; neither is also refused here.) Refused once per subscription. At 3.6 the rule was stated for `pdf` alone; 3.7 extends it
to the binary formats it registers.

### §P4.53 A template's body is the form its format takes — `template-body-unfit`

A `reportTemplate` whose `format` is a text format (`markdown`, `html`, `pdf`) carries a text body, and one
whose `format` is a binary format (`docx`, `xlsx`) carries a binary body (§P3.11). A host fills a text body
as text and a binary body as a document, so a Word document held as a string, or a Markdown body held as a
file, is a template no host renders as declared. A template whose `format` is unregistered is §P2.18's
`token-unregistered` alone. Refused once per template.

### §P4.54 A binary body names a file a host can load — `template-source-malformed`

A binary body's `source` is a relative path under the application's root, and its `contentHash` is
`sha256:` followed by exactly 64 lower-case hexadecimal digits — the grammar §P4.32 holds a reference
dataset's `source` and `contentHash` to, so a template file names one file under the application's root on
every host and cannot name one outside it. Each is a declaration a host could only meet at start, as a
refusal to serve; the composition refuses it first. Refused once per malformed member.

### §P4.55 A composition with a client shell names the application — `short-name-required`

A composition carrying a `clientShell` carries `shortName` on its root (core §5.1). An evaluator of this
profile names the solution it builds after the application — `<shortName>` with the build tool's solution
suffix, verbatim — and an application that bundles a client is one an operator builds, launches and tells
apart from its neighbours by that name. A composition with no `clientShell` MAY omit the member, and an
evaluator then names its solution by a fixed default; one carrying the member is named by it whether or
not it carries a client shell. Refused once, at the root. A `shortName` that is present and malformed is
the core's refusal (`short-name-invalid`), not this one.

### §P4.56 A declared project is built with the application

An evaluator of this profile builds each project the root's `projects` member declares (core §5.1) with the
application: the solution it names after the application (§P4.55) lists every declared path, verbatim and in
declaration order, beside the projects the evaluator produces from the tree. The list is the whole of what is
added — an evaluator does not discover a project the member does not name, by walking the references inside
another project or by any other route, because then what it builds would be a function of files the
composition does not name. A declared path that names no file is a composition the evaluator cannot build, and
it refuses to build it, naming the path, before it builds anything; that is a check against the application's
files, which a reader of the document does not hold, so no refusal class is registered for it. A composition
declaring no project meets the rule vacuously.

### §P4.57 A delivery carries the members its mode takes — `delivery-member-absent`, `delivery-member-unfit`

An `upload` carries `module`; a `scheduledPull` carries `principal`, `cadence` and `tables`, no entry of which is
blank; a `watched` delivery carries `principal` and `location` (6.2). A delivery missing a member its mode takes is
`delivery-member-absent`; one carrying a member its mode does not take is `delivery-member-unfit`, because a host
would have to ignore it, and a member a host ignores is one the author believes is honoured. A delivery whose
`mode` is unregistered is `token-unregistered` and no more.

### §P4.58 A delivery runs as a principal a host can resolve — `delivery-principal-unresolvable`

The `subscriptionPrincipal` a pull or a watch names carries a `userId` that is not blank. A host resolves a
principal as it resolves a request signed in as its `userId`; a blank one resolves to nothing, at every host.
Whether a non-blank one resolves, and to a scope whose data persists, is the deployment's question, which a host
answers at startup (§P3.13).

### §P4.59 A pull's cadence is a cron expression in the registered grammar — `delivery-cadence-invalid`

A `scheduledPull`'s `cadence` is a five-field cron expression in §P4.49's grammar.

### §P4.60 A watched location stays inside the principal's scope — `delivery-location-invalid`

A `watched` delivery's `location`, read as a `/`-separated prefix (a `\` read as `/`, surrounding whitespace and
slashes trimmed), has no `..` segment. A location that climbs out of the principal's scope names objects the
principal does not own; the empty location is the principal's whole scope.

### §P4.61 A pull and a watch have a scheduler to run on — `delivery-scheduler-absent`

A composition carrying a `dataDelivery` whose `mode` is `scheduledPull` or `watched` selects `jobScheduler`
(§P2.3): a pull runs on its cadence and a watch polls its location, each as a scheduled job.

### §P4.62 A narrative's cadence is a cron expression in the registered grammar — `narrative-cadence-invalid`

A `groundedNarrative`'s `cadence`, when present, is a five-field cron expression in §P4.49's grammar (6.3). A
`groundedNarrative` carrying a `cadence` is a run that reacts to something, so which seams it needs bound — a
scheduler to run it on — is the evaluator's completeness question, as it is for a run triggered by a change
(§P4.21).

### §P4.63 A scheduled narrative runs as a principal a host can resolve — `narrative-principal-unresolvable`

The `subscriptionPrincipal` a `groundedNarrative`'s `runsAs` names carries a `userId` that is not blank (6.3). A
host resolves the principal as it resolves a request signed in as its `userId`, and a blank one resolves to
nothing at every host. Whether a non-blank one resolves, and to which scope, is the deployment's question, which a
host answers at startup (§P3.13). (A `runsAs` naming no `subscriptionPrincipal` is §P4.22's, and is not also
refused here.)

### §P4.64 A fact browse needs a fact tier — `fact-browse-without-tier`

A `factBrowse` is admissible only in a composition whose `FactStore` setting (§P2.5) is `EnabledFactStore` (6.4).
Without a fact tier a host has nothing to mount, and a composition declaring a browse it would not get reads as
served and is not.

### §P4.65 A fact browse's client module needs a client — `fact-browse-client-absent`

A `factBrowse` with no `mount` — the surface and the client module together — is admissible only in a composition
carrying a `clientShell` (6.4). Without one there is no bundled client to add the module to. A composition that
wants the surface alone says `serverOnly`.

### §P4.66 A fact browse is well formed — `fact-browse-malformed`

A stated `label` or `group` is not blank: the absent member already says "the host's own" (6.4). A `serverOnly`
browse carries neither, because it adds no client module, and a name or a group for one makes the document say two
things. A `mount` outside §P2.21 is `token-unregistered`.

### §P4.67 A fact browse shows what its facts' policies permit — `fact-browse-surface-forbidden`

Consider every column of every fact table a `moduleRef` defines, and that column's disclosure (its own, else its
table's). When there is at least one such column and every one is `restricted` under a **declared** policy whose
`permitSurfaces` exclude `browse`, a composition carrying a `factBrowse` is refused, naming each table and its policy
(6.4): the host's gate would deny every fact the browse could show. One column whose disclosure admits `browse` —
`surfaceable`, or a declared policy that permits it — meets the rule, because a reader browses every fact the scope
holds. A column under an undeclared policy, or `internal`, cites no declared policy and is outside this rule, exactly
as under §P4.41. A composition that defines no fact table meets the rule vacuously: nothing it states is denied.

### §P4.68 A home entry names a declared page — `home-entry-page-unknown`

Each `home` entry's `page` is the `id` of a `pageRef` in the same composition (6.5). An entry naming no node, or a node
of another kind, is refused, naming the pages there are: a link to a page the composition does not publish leads
nowhere.

### §P4.69 A home entry links one page — `home-entry-route-captured`

The page a `home` entry names is served at a route with no `{name}` capture (6.5). A captured route names a family of
pages, one per value of its capture, and a link must name one; a composition that wants the latest of a family
publishes it at a literal route beside the family, as §P3.3's precedence allows.

### §P4.70 A home entry links a page its readers can hold — `home-entry-audience-unreadable`

The client's home page is read by the application's users, so a `home` entry links a page whose audience (§P2.7) one of
them can hold (6.5). `public` and `authenticated` admit every one; `scope` admits the holders of a role the deployment
grants its users; `publication` admits a holder of a reader role, whom the deployment's directory or the publishing
scope's own grants name, so a user of the application can be made one. A `client` page is read by the one client whose
own scope is its relationship, and no user of the application is that reader, so an entry linking one is refused,
naming the relationship.

### §P4.71 A home entry is well formed — `home-entry-malformed`

A `home` entry's `title` is not blank, a stated `description` is not blank (the absent member already says "none"),
and no two entries of one shell name the same page (6.5): one page has one link on the home page.

### §P4.72 A theme is one a host can read — `theme-malformed`

A `theme` node's `source` is a relative path in §P4.32's grammar — one or more segments separated by `/`, no segment
empty, `.` or `..`, and no `\`, `:` or control character anywhere — so it names one file under the application's
root on every host and cannot name one outside it. Its `contentHash` is `sha256:` followed by exactly 64 lower-case
hexadecimal digits (6.6). Each is a declaration a host could only meet when it reduces, as a refusal; the composition
refuses it first. Whether the file is present, hashes to `contentHash` and is a theme manifest is the host's to
answer when it reduces (§P3.16), because the composition does not carry the file.

### §P4.73 An assistant operates a client — `ui-control-client-absent`

A composition carrying a `uiControl` carries a `clientShell` (6.8). The controls an allowlist names are rendered by
the bundled client, and a composition with no client has nothing for an assistant to operate, so its allowlist
would grant nothing while reading as though it granted something.

### §P4.74 An allowlist is well formed and names what the composition declares — `ui-control-allow-malformed`

A `uiControl` node's `allow` is not empty: an assistant permitted to operate nothing is a composition with no
`uiControl`, and the empty declaration is refused rather than read as either. Each entry names a tree or a unit that
is not blank; a unit entry names the `id` of a `moduleRef` in the composition; no two entries name the same tree, nor
the same unit; a tree entry names at least one control; a unit entry grants at least one field, button or page
navigation; no identifier is blank and none appears twice in one list. `triageProvider` appears only where `triage`
is `"true"` (6.8). Each is a grant a host could only refuse when an assistant reached for it; the composition refuses
it first.

### §P4.75 A triage provider is one the composition selects — `ui-control-triage-provider-unknown`

A `uiControl` node's `triageProvider`, when present, is the `id` of a `companionSelection` whose `slot` is
`aiProvider` (6.8). A host cannot route an instruction to a provider the composition does not compose.

## §P5. Extension

This profile registers no extension declaration of its own. The core registry (core §7.4) applies
unchanged, including the reserved `fuaran.program/logic-tree` pair, and an `extensionRef` under this
profile is refused or carried exactly as the core states.

Stated explicitly because "this profile adds none" and "extensions do not apply here" are different
claims, and only the first is true.

## §P6. Conformance

A claim to this profile runs **every `core`-scope family plus `profile-server-app-host`**, and reports
the profile by name. It carries every `core` obligation (core §2.1) and additionally:

- refuses an unregistered token in each **closed** slot of §P2;
- refuses, on a **registered** key of §P2.5, a value its grammar does not admit;
- reads and writes `clientShell`, `layoutRef`, `pageRef`, `groundedNarrative`, `aiFunding`,
  `referenceData`, `dataDelivery`, `entityReads`, `reportTemplate`, `reportProducer`,
  `subscriptionPrincipal`, `standingSubscription`, `factBrowse`, `theme` and `uiControl` (§P3);
- enforces §P4.1–§P4.3, §P4.5, §P4.7–§P4.55 and §P4.57–§P4.75, builds each project the root declares as §P4.56 obliges,
  serves a page as §P3.3 obliges, a fact browse as §P3.15 obliges and a client shell's home entries as §P3.1 obliges,
  applies a declared theme as §P3.16 obliges, enforces a declared allowlist as §P3.17 obliges,
  verifies every
  reference dataset as §P3.6 obliges and every binary template body as §P3.11 obliges before it serves,
  honours each unit's AI exposure and
  classified fields as §P3.7 obliges, and registers each declared disclosure policy as §P3.9 obliges.

An implementation that reads compositions written for this profile but implements none of the above
claims `core`, not this profile, and that claim is both honest and useful (core Appendix B.4).

**A facet claim is separate and additional** (core §11.2). Claiming this profile says nothing about
runtime behaviour; an implementation that also claims facet values names them from §P8's declaration
and runs the vectors demonstrating exactly those. An evaluator conforming to this profile while
guaranteeing nothing at runtime is a coherent and reportable position — it is what an emitter, a
generator or a preview tool is.

## §P7. Forward coupling

A change to this profile updates, in the same change-set: this document, [`registry.json`](registry.json), the
`profile-server-app-host` vectors and their digests in [`fixtures/manifest.json`](fixtures/manifest.json), the facet
declaration in §P8 and its renderings in the registry and that manifest, and forge's binding of the registry to its
surface ([`surface-binding.json`](surface-binding.json)). When the change is one §P1 names as a version event, the
profile version moves in all four of its renderings — the header, the §P1 table, the registry's `profileVersion` and
the manifest's — in that same change-set. A forge change that moves the composable surface this profile registers is
such a change, and lands with it.

The gate is the composition-profile surface check in forge's Platform test pack, run by `verify.ps1`. It compares
this document's tables against the registry in both directions; holds every fixture to its recorded digest; certifies
§P8 against the core's facet-family vectors pinned in `fixtures/core/`; and holds the registry against forge's
reflected composable surface through `surface-binding.json`. It fails when any of them disagree — the renderings of
one artefact, and the surface it describes, held together by something other than care.

## §P8. Facet declaration

Core §11 registers the facet axes and the rule that a declared value must be demonstrated. This is
what evaluators of this family provide, **captured from what they do and not from what would be
better to say**.

| Axis | Honoured |
|---|---|
| `delivery` | `atMostOnce`, `atLeastOnce` |
| `idempotency` | `idempotent`, `nonIdempotent` |
| `restart` | `lostOnRestart`, `retriedAfterRestart` |
| `convergence` | `singleWriter` |
| `attestation` | `attestedAtLaunch`, `attestedWithinWindow` |
| `recompute` | `fullOnChange`, `rowLocal`, `groupLocal` |

Each value is demonstrated by a vector in a family this profile is in scope of (§P1) — the core's own
facet-family vectors for this profile, which stay with the core (core §8.5) and are pinned by digest in
`fixtures/core/` — and every value
**not** listed is a statement rather than a gap (core §11.3). What each absence says:

- **`exactlyOnceEffective` is not honoured.** An operation interrupted before it takes effect is
  either retried or lost; nothing in a composition of this family suppresses the repeat. The value is
  the sharpest sentence on the delivery axis and this profile does not get to say it.
- **`idempotentWithStore` is not honoured.** The store an effective-exactly-once claim rests on is not
  composed by evaluators of this family — where one exists it is wired outside the composition, so
  nothing the composition describes can be relied on to remember a replay. A setting may record that
  a deployment wired one; that is a note about somewhere else, not a guarantee this profile makes.
- **`survivesRestart` is not honoured.** No durable work queue is composed, so in-flight work is
  either dropped or re-attempted. (Core §11.5 additionally records that the value is not yet
  discriminable at all, so no profile may declare it.)
- **`convergentFold` and `divergentMerge` are not honoured**, because an evaluator of this family is
  handed a composition **value**, not an authorship stream. It folds nothing, and the honest posture
  is `singleWriter`: divergent streams are refused rather than linearised silently.
- **`unattested` is not honoured, and could not be.** Core §11.5 records that no family states a
  discriminator for it, because it is the absence of evidence and a family of quote vectors cannot
  exhibit an absence. A profile that attests nothing says so by declaring the axis empty with a
  reason (core §11.4), not by claiming this value.

### §P8.1 Concurrent authorship — `concurrent-authorship-unsupported`

Divergent authorship streams over one composition, presented to an evaluator of this family, are
**refused** under this class. The refusal is the `singleWriter` demonstration and is detected at the
`runtime` layer (core §12.4).

It is stated as a refusal rather than as silence for the reason core §12.2 gives about interference: a
holder that linearises two streams by arrival order produces a composition neither author wrote, and
does so without any symptom. Refusing hands the resolution back to something that has the authority
to make it.

Note this profile's declaration and `core`'s differ on the `convergence` axis and are not in conflict.
Core §11.2 is why: a facet declaration is not part of the decode obligations an evaluator profile
inherits from `core` (§2.2), so an implementation of this profile is a complete `core` reader without
thereby making `core`'s facet claim.

### §P8.2 What honouring the two attestation values does and does not say

**It says an evaluator of this family CAN carry the evidence, not that every one of them does.** Core
§11.2 declares per value what an implementation of a profile **may** claim, and an implementation's
own facet claim names a subset. So an evaluator of this family hosted in a measured environment
claims `attestedAtLaunch`, one that re-establishes the evidence inside a declared window claims
`attestedWithinWindow`, and one hosted in an ordinary process claims **neither** — which is the same
posture as a deployment that wires no idempotency store, one axis along.

The contrast with `exactlyOnceEffective` is what makes this a declaration rather than an
overstatement, and it is worth stating because the two look alike from a distance. That value is
declined because **no** implementation of this family could claim it: the store it rests on is not
composed by evaluators of this family at all, so the sentence has nothing behind it in any
deployment. Here the evidence is a property of where the evaluator is hosted, which some deployments
have and most do not, and §11.2's per-value declaration is exactly the construct for that difference.

Two consequences a reader should not have to infer. **The window is checked by the relying party, not
by the attester** — a quote is a fact about a moment, and whether that moment is still current is a
judgement made with a policy and an observation instant, which is why core §12.5 puts both in the
vector rather than in the quote. And **a lapsed window downgrades rather than fails open**: the
evidence still says what was launched, so the honest reading is `attestedAtLaunch`'s and never
`attestedWithinWindow`'s. An implementation that let a lapsed quote keep answering the second
question has not honoured the value; it has stopped checking it.

### §P8.3 What honouring the whole `recompute` axis does and does not say

**The ordinary reduction of a composition of this family is `fullOnChange`, and that is stated first
because it is what the family does rather than as a fallback.** An evaluator of this family reduces a
composition to an application serving requests over its registered data types; a request evaluates the
pipeline behind the data type it asks about, over the data as it stands. Nothing from the previous
request is carried, and the answer is correct every time without a cache having to be right about
anything.

**`rowLocal` and `groupLocal` are honoured on core §11.2's per-value reading**, exactly as the two
attestation values are one axis along: they say an implementation of this family **may** claim them,
not that every one does. An implementation that composes an incremental evaluation of a registered
data type's pipeline claims them; one that evaluates per request claims the first value and neither of
the others, and that is a complete and honest claim rather than a partial one.

The contrast with `exactlyOnceEffective` is again the discriminating one, and it is worth writing down
because the three look alike from a distance. That value is declined because **no** implementation of
this family could claim it: the store it rests on is not composed by evaluators of this family at all.
These two are declared because an implementation of this family **can** compose the thing they rest
on — the pipeline is already in the composition, and what is missing is an evaluator that maintains it
rather than a substrate nobody has.

**Which is why the vectors matter more here than the declaration does.** Core §11.3 forbids declaring
a value no family demonstrates, and the `incremental-recompute` family (core §12.7) is that
demonstration: it fixes what the answer must be, what counts as work, and what a decline owes by way
of a reason, **before** any implementation of this family maintains anything. An instrument written
after the engine it measures is an instrument shaped by that engine.

---

## Appendix P-A — Why the profile was a role, and why it names forge now *(non-normative)*

Every token registered above is a byte some composition already carries, so the vocabulary is inherited rather than
designed. Until 6.8 the document described "the vocabulary of an evaluator that does a particular job" rather than a
named product, because it lived beside the core, and a specification that names its first implementation makes that
implementation the definition.

Core §8.5 resolves that differently: a vocabulary profile is published by the owner of the evaluator whose surface it
registers, beside that implementation and versioned with it, while the core stays neutral. Published here, the profile
can name its evaluator without making the core that evaluator's — the core still names no implementation, and still
registers this profile by identifier. A second evaluator of the same shape still implements this profile rather than
negotiating with it, and one of a different shape still registers its own.

---

## Licence

Apache-2.0 — see this repository's [`LICENSE`](../LICENSE) and [`NOTICE.md`](../NOTICE.md). Copyright 2024–2026 ToolUp Analytics Ltd.
