namespace ToolUp.Reporting

open System.Collections.Concurrent

// ─── Phase 534.A — the report-producer registry ──────────────────────
//
// A module registers a *report producer* the way it registers a data
// type: a key, a display name, the parameters it takes, and the code
// that turns a parameter map into something renderable. The SDK holds
// the registry and names no report (GP 9).
//
// **What a producer returns is a render REQUEST, not bytes.** The
// obvious alternative — let a producer render its own document — would
// have put a second rendering path beside the Phase 23 pipeline, and
// the two would agree until they did not: only one of them would apply
// the disclosure export door, honour `ReportApiConfig`, or route a
// deck-tier format to its typed refusal. A producer therefore answers
// the question it is actually the expert on — *which template, filled
// with which values* — and the subscription job renders that through
// the same registry every interactive render goes through.
//
// **The resolve function is server-tier, the descriptor is not.**
// `ReportProducerDescriptor` (Core) is what an admin surface needs to
// offer the report and build a valid subscription against it. The
// resolve function reads the deployment's own data and could never
// cross the wire, so it lives here and never leaks into the Fable-safe
// tier (GP 10).

/// What a producer resolved to: a template to render and the values to
/// fill it with. Both halves are ordinary Phase 23 vocabulary, so the
/// subscription path and the interactive `Render` path converge on the
/// same renderer call.
type ReportRenderRequest = {
    /// Template the producer wants rendered. Resolved through the
    /// deployment's `IReportTemplateStore` at the subscription's scope,
    /// so a producer cannot reach another scope's templates.
    TemplateId: TemplateId
    /// Placeholder values. A `NarrativeValue` here rides the same
    /// disclosure door and the same structural-expansion routing an
    /// interactive render does.
    Values: Map<string, PlaceholderValue>
    /// Optional filename stem for the delivered artefact, without an
    /// extension. `None` falls back to the subscription's display name.
    FileNameStem: string option
}

/// A registered report producer: the descriptor an admin surface reads,
/// plus the resolve function the subscription job calls.
///
/// **Stateless between invocations** (GP 12 rule 4). `Resolve` derives
/// its result from `(scopeId, parameters)` plus whatever infrastructure
/// the module closed over at registration. A scheduled run may execute
/// on any node, hours apart, with no memory of the previous one.
type ReportProducer = {
    Descriptor: ReportProducerDescriptor
    /// Turn a scope and a validated parameter map into a render
    /// request. `Error` carries a human-readable reason recorded on the
    /// subscription's last-run outcome and surfaced to the operator; it
    /// is treated as a *permanent* failure, because a producer that
    /// cannot answer for these parameters will not answer differently
    /// on the next attempt.
    Resolve: string -> Map<string, string> -> Async<Result<ReportRenderRequest, string>>
}

module ReportProducer =
    /// Build a producer from its parts. The fluent path for the common
    /// case; construct the record directly when a field needs a shape
    /// this does not offer.
    let create
        (key: ReportProducerKey)
        (displayName: string)
        (parameters: ReportParameterSchema list)
        (resolve: string -> Map<string, string> -> Async<Result<ReportRenderRequest, string>>)
        : ReportProducer =
        {
            Descriptor = {
                Key = key
                DisplayName = displayName
                Description = None
                Parameters = parameters
                Formats = []
            }
            Resolve = resolve
        }

    let withDescription (description: string) (producer: ReportProducer) = {
        producer with
            Descriptor = {
                producer.Descriptor with
                    Description = Some description
            }
    }

    /// Restrict the formats this producer may be subscribed in. Leaving
    /// it unset means "no restriction" — see
    /// `ReportSubscription.validateFormat`.
    let withFormats (formats: TemplateFormat list) (producer: ReportProducer) = {
        producer with
            Descriptor = {
                producer.Descriptor with
                    Formats = formats
            }
    }

/// In-process registry of report producers, populated at compose time.
///
/// Mirrors `RendererRegistry`: a small mutable holder written once
/// during composition and read-only thereafter. `ConcurrentDictionary`
/// rather than `Dictionary` because the read side is every scheduled
/// run on every worker thread, and a compose that registers a producer
/// late (a deferred declaration resolving from DI at `StartAsync`)
/// would otherwise race the first tick.
type ReportProducerRegistry() =
    let producers = ConcurrentDictionary<ReportProducerKey, ReportProducer>()

    /// Register a producer. Returns `Error` naming both claimants when
    /// the key is already taken — the same posture
    /// `AlgorithmProviderRegistry` takes on a contested algorithm id,
    /// and for the same reason: two modules quietly contesting one key
    /// means a subscription renders whichever registered last, which is
    /// a composition-order dependency nobody declared.
    member _.Register(producer: ReportProducer) : Result<unit, string> =
        let key = producer.Descriptor.Key

        if producers.TryAdd(key, producer) then
            Ok()
        else
            let existing = producers[key]

            Error(
                $"report producer key '{key}' is already registered by '{existing.Descriptor.DisplayName}'; "
                + $"'{producer.Descriptor.DisplayName}' cannot also claim it"
            )

    /// Resolve a producer by key. `None` for an unregistered key — a
    /// data condition (a subscription outliving the module that
    /// registered its producer), never an exception.
    member _.TryResolve(key: ReportProducerKey) : ReportProducer option =
        match producers.TryGetValue key with
        | true, producer -> Some producer
        | _ -> None

    /// Every registered descriptor, ordered by display name so an admin
    /// surface renders a stable list.
    member _.Descriptors: ReportProducerDescriptor list =
        producers.Values
        |> Seq.map _.Descriptor
        |> Seq.sortBy _.DisplayName
        |> List.ofSeq

    /// How many producers are registered. Read by the compose-time
    /// diagnostic and the `/dev/inspect` panel.
    member _.Count = producers.Count