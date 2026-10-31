// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Grounded narratives (Phase 985) — the seams ─────────────────────
//
// A deployment with a fact tier can ask the model for a narrative report
// and publish it ONLY if every number in it is a reference to a Fact. Four
// parts take part, and no package sees all four:
//
//   * the grounding gate — beside the fact tier, because only the fact
//     tier can say whether a Fact resolves, is current, may be disclosed
//     and has the value a narrative states for it;
//   * the generation run — beside the agent loop, because only the AI tier
//     can run the model with a chosen tool set;
//   * the scheduled-report trigger — beside the report producer registry;
//   * the data-arrival trigger — beside the reactive data-change seam.
//
// The types below are what joins them, declared here because every one of
// those packages references this assembly and none references another
// (the shape `IFactDisclosureGate` and `INarrativeIngestor` already take).
// They name Facts by id string and carry no fact-tier type.

/// What a declared citable reference resolves to (see
/// `ICitableReferenceKind`).
type CitableReferenceResolution =
    /// The reference is current. `Renderings` are the forms a narrative
    /// may state it as, its canonical rendering first; `Value` is its
    /// numeric value when it has one, so a stated figure is compared by
    /// number rather than by spelling.
    | CitableReferenceCurrent of renderings: string list * value: decimal option
    /// The scope holds no reference with this id.
    | CitableReferenceUnresolved
    /// The reference has been replaced; `supersededBy` names its successor
    /// when the kind knows it.
    | CitableReferenceSuperseded of supersededBy: string option
    /// The reference exists and the publishing surface may not disclose it.
    | CitableReferenceWithheld of policyRef: string

/// A kind of dated reference, other than a Fact, that a grounded narrative
/// may cite — a calendar event, a published price list, a filed document.
/// A deployment registers one per kind in DI; the gate resolves a span
/// reference written `<kind>:<id>` through the kind of that name and
/// refuses a kind nobody registered. Fact references are bare Fact ids and
/// never take this route.
///
/// GP 12 audit: identity by value (strings), async at the boundary, no
/// callbacks, no state between calls, no cross-scope ordering promise.
type ICitableReferenceKind =
    /// The kind's name — the prefix before the `:` in a reference. Letters,
    /// digits, `-` and `_`.
    abstract Kind: string
    /// Resolve one reference id (the part after the `:`) in `scopeId`, for
    /// `principal`.
    abstract Resolve: scopeId: string * principal: string * referenceId: string -> Async<CitableReferenceResolution>

/// A grounding certificate issued over a published grounded narrative: the
/// narrative id at its root and a `CitesFact` edge to every Fact it cites.
type NarrativeGroundingCertificate = {
    /// The narrative id the certificate is rooted at.
    Root: string
    /// Lowercase-hex SHA-256 over the certificate's canonical signed bytes
    /// — the identity the issuance log records it under.
    Digest: string
    /// The signing key the certificate names.
    KeyId: string
    IssuedAt: DateTimeOffset
    /// The Fact ids the certificate was issued over.
    CitedFactIds: string list
    /// The sealed certificate itself, in its open interchange JSON form, so
    /// a holder can verify it offline against the deployment's public key.
    CertificateJson: string
}

/// The fact tier's face toward narratives: the grounding gate and the
/// certificate that records what a published narrative cites. Registered by
/// the fact tier whenever its store is composed; absent, the grounded run
/// refuses every narrative, because a gate it cannot consult is a gate that
/// would have failed open.
type INarrativeGroundingGate =
    /// Check `document` for publication at `surface` in `scopeId` as
    /// `principal`. Every numeric claim in the document's content must be a
    /// `Metric` span whose reference resolves to a current Fact (or a
    /// current declared reference) the surface may disclose, and whose
    /// stated value is that reference's value. Refusals name every
    /// offending span.
    abstract Check:
        scope: ResolvedScope * principal: string * surface: FactEgressSurface * document: NarrativeDocument ->
            Async<NarrativeGroundingVerdict>

    /// Issue a grounding certificate over a published narrative. `Error`
    /// carries the reason (for example, no signing substrate composed).
    abstract Certify:
        scope: ResolvedScope * principal: string * narrativeId: string * citedFactIds: string list ->
            Async<Result<NarrativeGroundingCertificate, string>>

/// One section of a grounded narrative's shape. The heading is the
/// deployment's, never the model's; `Guidance` tells the model what the
/// section's prose should cover.
type GroundedSectionShape = {
    Id: string
    Heading: string
    Guidance: string
}

/// Phase 1136 — what a grounded run's projections are built under: the
/// run's resolved scope, the principal it reads and publishes as, the
/// surface its narrative is checked at, and the run's request context.
///
/// `Request` is the context the model's fact tools read through: the
/// resolved scope carried (never minted), the principal as the request's
/// user, over the run's own DI scope. It is the gated fact reader: a
/// projection reads facts through the fact tier's per-request API over it
/// (`FactBrowseHandler.factBrowseApi context.Request`), so every point it
/// reads has passed the disclosure gate for the narrative's principal, and
/// a withheld point arrives marked as withheld rather than as a number. The
/// type names no fact-tier type, like every seam in this file. Valid only
/// while the projection it is handed to runs.
type GroundedProjectionContext = {
    /// The scope the run reads and publishes in, as the platform resolved it.
    Scope: ResolvedScope
    /// The definition's principal: the actor every read is judged for.
    Principal: string
    /// The surface the narrative is checked at.
    Surface: FactEgressSurface
    /// The run's request context — the gated fact reader.
    Request: Microsoft.AspNetCore.Http.HttpContext
}

/// Phase 1136 — one projection a grounded run's model asked for: the
/// request's `kind` and the request itself, the JSON object the model wrote.
/// The run reads nothing else of it; the builder registered for the kind
/// does. It names what to draw and carries no number.
type GroundedProjectionRequest = {
    /// The request's `kind`, lower-cased; empty when it names none.
    Kind: string
    /// The request object as the model wrote it.
    Json: string
}

/// Phase 1136 — builds the elements one requested projection draws, under
/// the run's projection context. `Error` names why the request cannot be
/// drawn; the run refuses that request on its audit row and publishes the
/// rest. Whatever it builds is checked by the grounding gate before it is
/// published, so a builder cannot put an ungrounded number on the page.
type GroundedRequestBuilder =
    GroundedProjectionContext -> GroundedProjectionRequest -> Async<Result<NarrativeElement list, string>>

/// How a grounded run answers a draft the gate refused: it repairs the draft
/// while the repair makes progress, then publishes what is grounded and
/// reports the rest.
///
/// A repair round sends the model the gate's offences in the same
/// conversation, splices the elements it rewrites into the draft, and
/// re-checks the WHOLE document. A round is progress when the offence set
/// shrinks, or keeps its size with different members. The loop stops at
/// the first of: no offence left, `MaxRoundsWithoutProgress` rounds in a
/// row without progress, `MaxRepairRounds` rounds, or the run's model calls
/// reaching `MaxRunTokens`. What is still refused then is withheld from the
/// published report, with a note naming each withheld claim's place and
/// reason, never its value — when `PublishGroundedRemainder` is set.
/// Otherwise the run is refused, as a run that is not repaired always was.
type GroundedRepairPolicy = {
    /// The most repair rounds a run takes. `0` repairs nothing.
    MaxRepairRounds: int
    /// Consecutive rounds without progress after which the loop stops.
    MaxRoundsWithoutProgress: int
    /// The most tokens (prompt plus output, as the provider reports them)
    /// the run's model calls may spend before the loop stops; `None` sets no
    /// budget. A provider that reports no usage never reaches it.
    MaxRunTokens: int64 option
    /// Whether a run that ends with offences publishes its grounded
    /// remainder. `false` refuses the whole narrative instead.
    PublishGroundedRemainder: bool
}

module GroundedRepairPolicy =
    /// Repair up to twelve rounds, stop after three without progress or at
    /// half a million tokens, and publish the grounded remainder.
    let defaults: GroundedRepairPolicy = {
        MaxRepairRounds = 12
        MaxRoundsWithoutProgress = 3
        MaxRunTokens = Some 500_000L
        PublishGroundedRemainder = true
    }

    /// No repair, and a refusal on the first offence: the behaviour of a run
    /// before the repair loop existed.
    let refuseOnOffence: GroundedRepairPolicy = {
        defaults with
            MaxRepairRounds = 0
            PublishGroundedRemainder = false
    }

/// What the repair loop did for a published narrative: the rounds it took
/// and the claims it withheld, each as the gate refused it. A narrative the
/// gate passed whole withholds nothing.
type GroundedRepairSummary = {
    /// Repair rounds taken; `0` when the first draft passed.
    Rounds: int
    /// The claims withheld from the published narrative, as the gate
    /// reported them.
    Withheld: GroundingOffence list
}

module GroundedRepairSummary =
    /// A narrative published whole after `rounds` repair rounds.
    let complete (rounds: int) : GroundedRepairSummary = { Rounds = rounds; Withheld = [] }

    /// Whether the narrative was published whole.
    let isComplete (summary: GroundedRepairSummary) : bool = List.isEmpty summary.Withheld

    /// The warning a job that published with withheld claims carries: the
    /// count, the rounds and each claim with its reason.
    let warning (summary: GroundedRepairSummary) : string =
        sprintf
            "published with %d claim(s) withheld after %d repair round(s): %s"
            (List.length summary.Withheld)
            summary.Rounds
            (summary.Withheld |> List.map GroundingOffence.describe |> String.concat "; ")

/// A registered grounded narrative run — what a deployment declares. The
/// SDK holds the registry and names no report (GP 9): the deployment says
/// which runs exist, and both triggers address a run by its `Key`.
///
/// **Stateless between invocations** (GP 12 rule 4): `Project` and every
/// request builder derive their result from the context they are handed
/// plus whatever the deployment closed over at registration.
type GroundedNarrativeDefinition = {
    /// Stable key both triggers address the run by.
    Key: string
    DisplayName: string
    /// The module the published narrative is attributed to.
    ModuleId: string
    PageRoute: string option
    /// The principal the run reads facts and publishes as.
    Principal: string
    /// The document title. Structure, not prose: the model never writes it.
    Title: string
    /// What the report is for — the instruction the model works from.
    Prompt: string
    /// The metric ids whose facts the narrative is written from. The prompt
    /// names them to the model, and the data-arrival trigger enqueues the
    /// run when a fact of one of them is invalidated. Empty means the run
    /// reacts to no data change.
    DependsOnMetrics: string list
    Sections: GroundedSectionShape list
    /// The deterministic projections — tables — built from Facts by
    /// deployment code (for example `NarrativeFromData`'s fact-bearing
    /// grids), keyed by section id, under the run's projection context
    /// (Phase 1136; `withProjection` adapts a scope-id form). They are
    /// appended after the section's prose, before any requested projection,
    /// and checked by the same gate.
    Project: GroundedProjectionContext -> Async<Result<(string * NarrativeElement list) list, string>>
    /// Phase 1136 — the builders for the projections the model may
    /// request, by request kind. Empty (the default) means the model is
    /// told nothing of projections and a request it writes anyway is
    /// refused as an unregistered kind.
    RequestBuilders: Map<string, GroundedRequestBuilder>
    /// Phase 1136 — whether the model is told it may write a hypothesis
    /// marked as conjecture. A conjecture renders as a labelled callout
    /// and carries no figure; whether or not this is set, a conjecture the
    /// model writes is rendered so, never as plain prose. Off by default.
    AllowsConjecture: bool
    /// Tags the published narrative carries.
    Tags: string list
    /// The egress surface the narrative is checked for.
    Surface: FactEgressSurface
    /// How a refused draft is repaired and what is published when the
    /// repair stops (`GroundedRepairPolicy.defaults` unless set with
    /// `withRepairPolicy`).
    RepairPolicy: GroundedRepairPolicy
}

module GroundedNarrativeDefinition =
    /// A definition with no projections, no metric dependencies, no tags,
    /// checked at the narrative-publication surface.
    let create
        (key: string)
        (displayName: string)
        (moduleId: string)
        (principal: string)
        (title: string)
        (prompt: string)
        (sections: GroundedSectionShape list)
        : GroundedNarrativeDefinition =
        {
            Key = key
            DisplayName = displayName
            ModuleId = moduleId
            PageRoute = None
            Principal = principal
            Title = title
            Prompt = prompt
            DependsOnMetrics = []
            Sections = sections
            Project = fun _ -> async { return Ok [] }
            RequestBuilders = Map.empty
            AllowsConjecture = false
            Tags = []
            Surface = FactNarrativePublication
            RepairPolicy = GroundedRepairPolicy.defaults
        }

    /// Repair and publish under `policy` instead of
    /// `GroundedRepairPolicy.defaults`. **Sets** the policy.
    let withRepairPolicy (policy: GroundedRepairPolicy) (definition: GroundedNarrativeDefinition) = {
        definition with
            RepairPolicy = policy
    }

    let dependingOn (metricIds: string list) (definition: GroundedNarrativeDefinition) = {
        definition with
            DependsOnMetrics = metricIds
    }

    /// Deterministic projections from the scope id alone — the form that
    /// predates the projection context, kept for the projections that need
    /// nothing else.
    let withProjection
        (project: string -> Async<Result<(string * NarrativeElement list) list, string>>)
        (definition: GroundedNarrativeDefinition)
        =
        {
            definition with
                Project = fun context -> project context.Scope.ScopeId
        }

    /// Phase 1136 — deterministic projections built under the run's
    /// projection context, so they can read facts through its gated
    /// request (see `GroundedProjectionContext`).
    let withProjectionContext
        (project: GroundedProjectionContext -> Async<Result<(string * NarrativeElement list) list, string>>)
        (definition: GroundedNarrativeDefinition)
        =
        { definition with Project = project }

    /// Phase 1136 — let the model request projections of `kind`, built by
    /// `builder`. The run then tells the model the request form and the
    /// registered kinds; registering a kind again replaces its builder.
    let withRequestBuilder (kind: string) (builder: GroundedRequestBuilder) (definition: GroundedNarrativeDefinition) = {
        definition with
            RequestBuilders = definition.RequestBuilders |> Map.add (kind.Trim().ToLowerInvariant()) builder
    }

    /// Phase 1136 — tell the model it may write a hypothesis, marked as
    /// conjecture (see `AllowsConjecture`).
    let withConjecture (definition: GroundedNarrativeDefinition) = {
        definition with
            AllowsConjecture = true
    }

    let withTags (tags: string list) (definition: GroundedNarrativeDefinition) = { definition with Tags = tags }

    /// Phase 1111 — the definition with an administrator's wording in place
    /// of the declared wording: `prompt` replaces `Prompt`, and each
    /// `(section id, guidance)` pair replaces that section's `Guidance`.
    /// Nothing else moves: the key, the title, the section ids, headings and
    /// order, the metrics, the projections, the principal and the surface
    /// all stay the declaration's, and the grounding rules the run appends
    /// are not part of a definition at all. A section the definition does
    /// not declare, or one named twice, is refused by name and nothing is
    /// applied.
    let reword
        (prompt: string)
        (guidance: (string * string) list)
        (definition: GroundedNarrativeDefinition)
        : Result<GroundedNarrativeDefinition, string> =
        let declared = definition.Sections |> List.map _.Id |> Set.ofList
        let named = guidance |> List.map fst

        let undeclared = named |> List.filter (declared.Contains >> not) |> List.distinct

        let repeated =
            named |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

        if not (List.isEmpty undeclared) then
            Error(
                sprintf
                    "grounded narrative '%s' declares no section %s; its sections are %s"
                    definition.Key
                    (undeclared |> List.map (sprintf "'%s'") |> String.concat ", ")
                    (definition.Sections
                     |> List.map (fun s -> sprintf "'%s'" s.Id)
                     |> String.concat ", ")
            )
        elif not (List.isEmpty repeated) then
            Error(
                sprintf
                    "grounded narrative '%s': section %s is given guidance more than once"
                    definition.Key
                    (repeated |> List.map (sprintf "'%s'") |> String.concat ", ")
            )
        else
            let byId = Map.ofList guidance

            Ok {
                definition with
                    Prompt = prompt
                    Sections =
                        definition.Sections
                        |> List.map (fun shape ->
                            match byId.TryFind shape.Id with
                            | Some text -> { shape with Guidance = text }
                            | None -> shape)
            }

/// Compose-time registry of grounded narrative runs. Written during
/// composition, read by both triggers thereafter; the same posture as the
/// report producer registry, including refusing a contested key.
type GroundedNarrativeRegistry() =
    let definitions =
        ConcurrentDictionary<string, GroundedNarrativeDefinition>(StringComparer.Ordinal)

    /// Register a run. `Error` names both claimants when the key is taken.
    member _.Register(definition: GroundedNarrativeDefinition) : Result<unit, string> =
        if definitions.TryAdd(definition.Key, definition) then
            Ok()
        else
            let existing = definitions[definition.Key]

            Error(
                $"grounded narrative key '{definition.Key}' is already registered by '{existing.DisplayName}'; "
                + $"'{definition.DisplayName}' cannot also claim it"
            )

    member _.TryResolve(key: string) : GroundedNarrativeDefinition option =
        match definitions.TryGetValue key with
        | true, definition -> Some definition
        | _ -> None

    /// Every registered run, ordered by key.
    member _.Definitions: GroundedNarrativeDefinition list =
        definitions.Values |> Seq.sortBy _.Key |> List.ofSeq

    /// The runs that react to a change in any of `metricIds`.
    member this.DependentOn(metricIds: string list) : GroundedNarrativeDefinition list =
        let changed = Set.ofList metricIds

        this.Definitions
        |> List.filter (fun d -> d.DependsOnMetrics |> List.exists changed.Contains)

/// One request to run a registered grounded narrative.
type GroundedNarrativeRequest = {
    RunKey: string
    /// The scope the run reads, publishes and indexes in. A RESOLVED scope,
    /// because the model reads facts through the request-path fact tools,
    /// which take only a scope the platform resolved (Phase 797) — a job
    /// passes its `JobContext.Scope`. The anonymous scope is refused: it
    /// holds only what was asserted anonymously, so a run over it could
    /// cite nothing a reader cares about.
    Scope: ResolvedScope
    /// The access context the model provider is resolved under. `None`
    /// (what both triggers pass) resolves it under the scope the run
    /// CARRIES (Phase 995): a team scope as a member of that team, a user
    /// scope as that user (`AccessContext.forConfigScope`), so the AI key
    /// configured at the scope that triggered the run is the key that funds
    /// it. Only a scope that names no team or user — which holds no AI
    /// configuration of its own — resolves as the definition's principal.
    /// Either way the definition's principal stays the actor the narrative
    /// is read, checked, certified and indexed as.
    Access: AccessContext option
    /// What started the run (`"report-subscription"`, `"data-arrival"`, …),
    /// recorded on the audit event.
    Trigger: string
}

/// What one grounded run did.
type GroundedNarrativeOutcome =
    /// The narrative passed the gate and was published. `Indexed` is the
    /// knowledge-base commit's outcome (`None` when no knowledge base is
    /// composed); `Certificate` is the grounding certificate, or why none
    /// could be issued. `Repair` says whether it was published whole or
    /// with claims withheld, and how many repair rounds it took; the
    /// document is what was published, withheld-claims note included.
    | GroundedNarrativePublished of
        narrativeId: NarrativeId *
        document: NarrativeDocument *
        citations: NarrativeCitation list *
        indexed: NarrativeIngestOutcome option *
        certificate: Result<NarrativeGroundingCertificate, string> *
        repair: GroundedRepairSummary
    /// The narrative failed the gate and was NOT published. Every offending
    /// claim is named, and the refusal is on the audit trail.
    | GroundedNarrativeRefused of offences: GroundingOffence list
    /// The run could not produce a narrative to check — no such run, no
    /// gate, no provider, no scope, or model output that is not the
    /// narrative format. Nothing was published; this is on the audit trail
    /// too.
    | GroundedNarrativeFailed of reason: string
    /// The run's scope holds no AI provider it may use under the
    /// deployment's funding policy — no key configured there, a key for a
    /// provider the deployment does not offer, or a strict bring-your-own-
    /// key policy with nothing brought (Phase 995). Nothing was generated or
    /// published. Unlike `GroundedNarrativeFailed` this does not pass by
    /// waiting: another attempt resolves the same configuration the same
    /// way, so a trigger treats it as permanent. `reason` says what is
    /// missing; the failure is on the audit trail as a failed row whose
    /// outcome is `unfunded`.
    | GroundedNarrativeUnfunded of reason: string
    /// The model was still writing the narrative when a provider call
    /// reached the run's call budget (Phase 1109). Nothing was published.
    /// Like `GroundedNarrativeUnfunded` this does not pass by waiting:
    /// another attempt over the same facts meets the same budget, and each
    /// attempt is a paid, partial model call — so a trigger treats it as
    /// permanent. `reason` names the budget and how a deployment raises it;
    /// the failure is on the audit trail as a failed row whose outcome is
    /// `over-budget`.
    | GroundedNarrativeOverBudget of reason: string

module GroundedNarrativeOutcome =
    /// One-paragraph description, for a subscription's last-run outcome or
    /// a log line.
    let describe (outcome: GroundedNarrativeOutcome) : string =
        match outcome with
        | GroundedNarrativePublished(id, _, citations, _, certificate, repair) ->
            let cert =
                match certificate with
                | Ok c -> sprintf "certificate %s" c.Digest
                | Error reason -> sprintf "no certificate (%s)" reason

            let withheld =
                if GroundedRepairSummary.isComplete repair then
                    ""
                else
                    sprintf
                        "; %d claim(s) withheld after %d repair round(s)"
                        (List.length repair.Withheld)
                        repair.Rounds

            sprintf "published narrative %O citing %d reference(s); %s%s" id (List.length citations) cert withheld
        | GroundedNarrativeRefused offences ->
            offences
            |> List.map GroundingOffence.describe
            |> String.concat "; "
            |> sprintf "refused, %d ungrounded claim(s): %s" (List.length offences)
        | GroundedNarrativeFailed reason -> sprintf "failed: %s" reason
        | GroundedNarrativeUnfunded reason -> sprintf "unfunded: no usable AI provider for the run's scope: %s" reason
        | GroundedNarrativeOverBudget reason -> sprintf "over budget: %s" reason

    /// The job result of a grounded run's outcome — the one place a narrative
    /// job decides whether another attempt is worth paying for, so the
    /// data-arrival and the regeneration handlers cannot disagree.
    let toJobResult (outcome: GroundedNarrativeOutcome) : JobResult =
        match outcome with
        | GroundedNarrativePublished(repair = repair) when GroundedRepairSummary.isComplete repair -> Success
        // Published with claims withheld: the job did its work, and the
        // withheld claims are a warning the jobs view shows, not a failure.
        | GroundedNarrativePublished(repair = repair) -> SuccessWithWarning(GroundedRepairSummary.warning repair)
        // A refusal is a decision, recorded on the audit trail by the run;
        // another attempt over the same facts would refuse again.
        | GroundedNarrativeRefused _ -> PermanentFailure(describe outcome)
        // A failure to produce a narrative (a provider outage, unparseable
        // model output) may not recur.
        | GroundedNarrativeFailed _ -> TransientFailure(describe outcome)
        // A scope with no usable AI key (Phase 995) has none on the next
        // attempt either: retrying would spend the backoff and publish
        // nothing.
        | GroundedNarrativeUnfunded _ -> PermanentFailure(describe outcome)
        // A reply cut at the run's call budget (Phase 1109) is cut at the
        // same budget next time, and each attempt is a paid, partial model
        // call: permanent, naming how to raise it.
        | GroundedNarrativeOverBudget _ -> PermanentFailure(describe outcome)

/// The generation run (seam:GroundedNarrativeRun). Implemented beside the
/// agent loop; both triggers call it.
///
/// GP 12 audit: the request is a value, the one method is async, no state
/// is held between calls.
type IGroundedNarrativeRun =
    abstract Run: request: GroundedNarrativeRequest -> Async<GroundedNarrativeOutcome>

/// The audit trail's vocabulary for grounded runs. Every run that reaches
/// the gate records exactly one outcome row (published, refused or failed),
/// and a repaired run one `RepairRoundType` row per round beside it.
module GroundedNarrativeEvents =
    [<Literal>]
    let SourceModule = "_narratives"

    [<Literal>]
    let PublishedType = "GroundedNarrativePublished"

    [<Literal>]
    let RefusedType = "GroundedNarrativeRefused"

    [<Literal>]
    let FailedType = "GroundedNarrativeFailed"

    /// One repair round of a run whose draft the gate refused: the offences
    /// in and out, whether it made progress, and the tokens spent so far.
    /// Written beside — never instead of — the run's one outcome row.
    [<Literal>]
    let RepairRoundType = "GroundedNarrativeRepairRound"