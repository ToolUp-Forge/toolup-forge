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
        scopeId: string * principal: string * surface: FactEgressSurface * document: NarrativeDocument ->
            Async<NarrativeGroundingVerdict>

    /// Issue a grounding certificate over a published narrative. `Error`
    /// carries the reason (for example, no signing substrate composed).
    abstract Certify:
        scopeId: string * principal: string * narrativeId: string * citedFactIds: string list ->
            Async<Result<NarrativeGroundingCertificate, string>>

/// One section of a grounded narrative's shape. The heading is the
/// deployment's, never the model's; `Guidance` tells the model what the
/// section's prose should cover.
type GroundedSectionShape = {
    Id: string
    Heading: string
    Guidance: string
}

/// A registered grounded narrative run — what a deployment declares. The
/// SDK holds the registry and names no report (GP 9): the deployment says
/// which runs exist, and both triggers address a run by its `Key`.
///
/// **Stateless between invocations** (GP 12 rule 4): `Project` derives its
/// result from the scope it is handed plus whatever the deployment closed
/// over at registration.
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
    /// The deterministic projections — tables and charts — built from
    /// Facts by deployment code (for example `NarrativeFromData`'s
    /// fact-bearing grids), keyed by section id. The model writes prose
    /// only; projected elements are appended after it and checked by the
    /// same gate.
    Project: string -> Async<Result<(string * NarrativeElement list) list, string>>
    /// Tags the published narrative carries.
    Tags: string list
    /// The egress surface the narrative is checked for.
    Surface: FactEgressSurface
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
            Tags = []
            Surface = FactNarrativePublication
        }

    let dependingOn (metricIds: string list) (definition: GroundedNarrativeDefinition) = {
        definition with
            DependsOnMetrics = metricIds
    }

    let withProjection
        (project: string -> Async<Result<(string * NarrativeElement list) list, string>>)
        (definition: GroundedNarrativeDefinition)
        =
        { definition with Project = project }

    let withTags (tags: string list) (definition: GroundedNarrativeDefinition) = { definition with Tags = tags }

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
    /// resolves it as the definition's principal.
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
    /// could be issued.
    | GroundedNarrativePublished of
        narrativeId: NarrativeId *
        document: NarrativeDocument *
        citations: NarrativeCitation list *
        indexed: NarrativeIngestOutcome option *
        certificate: Result<NarrativeGroundingCertificate, string>
    /// The narrative failed the gate and was NOT published. Every offending
    /// claim is named, and the refusal is on the audit trail.
    | GroundedNarrativeRefused of offences: GroundingOffence list
    /// The run could not produce a narrative to check — no such run, no
    /// gate, no provider, no scope, or model output that is not the
    /// narrative format. Nothing was published; this is on the audit trail
    /// too.
    | GroundedNarrativeFailed of reason: string

module GroundedNarrativeOutcome =
    /// One-paragraph description, for a subscription's last-run outcome or
    /// a log line.
    let describe (outcome: GroundedNarrativeOutcome) : string =
        match outcome with
        | GroundedNarrativePublished(id, _, citations, _, certificate) ->
            let cert =
                match certificate with
                | Ok c -> sprintf "certificate %s" c.Digest
                | Error reason -> sprintf "no certificate (%s)" reason

            sprintf "published narrative %O citing %d reference(s); %s" id (List.length citations) cert
        | GroundedNarrativeRefused offences ->
            offences
            |> List.map GroundingOffence.describe
            |> String.concat "; "
            |> sprintf "refused, %d ungrounded claim(s): %s" (List.length offences)
        | GroundedNarrativeFailed reason -> sprintf "failed: %s" reason

/// The generation run (seam:GroundedNarrativeRun). Implemented beside the
/// agent loop; both triggers call it.
///
/// GP 12 audit: the request is a value, the one method is async, no state
/// is held between calls.
type IGroundedNarrativeRun =
    abstract Run: request: GroundedNarrativeRequest -> Async<GroundedNarrativeOutcome>

/// The audit trail's vocabulary for grounded runs. Every run that reaches
/// the gate records exactly one of these rows.
module GroundedNarrativeEvents =
    [<Literal>]
    let SourceModule = "_narratives"

    [<Literal>]
    let PublishedType = "GroundedNarrativePublished"

    [<Literal>]
    let RefusedType = "GroundedNarrativeRefused"

    [<Literal>]
    let FailedType = "GroundedNarrativeFailed"