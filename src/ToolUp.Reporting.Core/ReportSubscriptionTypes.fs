namespace ToolUp.Reporting

open System

// ─── Phase 534 — scheduled report subscriptions: shared types ────────
//
// "Email me this report every Monday" is composition, not new
// infrastructure: Reporting (Phase 23) renders, the job scheduler
// (Phase 9b) fires, transactional sinks (Phase 6f) deliver. What was
// missing is the *subject* those three seams operate on — a persisted
// record saying which report, with which parameters, on what schedule,
// to whom, in what format.
//
// This file is that record and the vocabulary around it. It lives in
// Core, and is therefore **Fable-safe by construction**: BCL primitives
// and F# records/DUs only, no `System.Security.Cryptography`, no server
// tier, no interface carrying a live handle. An admin surface renders
// exactly the shapes the server persists (GP 10).
//
// ─── Why the SDK names no report (GP 9) ──────────────────────────────
//
// A subscription does not carry a report; it carries a *producer key*.
// Modules register report producers the way they register data types —
// key + display name + parameter schema + a resolve function — and the
// SDK holds the registry without ever knowing what a "quarterly revenue
// summary" is. The consequence worth stating: `ProducerKey` is an
// opaque string to everything in this package, and a subscription
// naming a producer the deployment no longer registers is a *data*
// condition (`UnknownProducer`), never a compile-time one.
//
// ─── Why the schedule is a bare cron string ──────────────────────────
//
// `ToolUp.Platform.Trigger` also admits `OnEvent` and `Manual`, neither
// of which a *subscription* means: a subscription that never fires on a
// clock is a report, not a subscription, and run-now already covers the
// manual case without a second schedule shape. Storing the expression
// itself keeps this type free of the server-tier cron parser while
// leaving exactly one place — `ReportSubscriptionStore.validate` — that
// decides whether a string is a schedule.

/// Stable identifier for a subscription. Minted server-side at create;
/// clients never choose one.
type SubscriptionId = string

/// Opaque identifier for a registered report producer. Namespaced by
/// the registering module by convention (`"sales.pipeline-summary"`),
/// so two modules cannot silently contest one key.
type ReportProducerKey = string

/// Kind of a producer parameter, for a management UI to render an input
/// for and for the server to validate a supplied value against.
///
/// Deliberately *not* `PlaceholderKind`: a placeholder is what a
/// template slot accepts (including `Image` bytes and whole tables),
/// whereas a subscription parameter is what a human types into a
/// subscription form and what survives JSON round-tripping in a
/// persisted record. Reusing the richer type would have implied that a
/// subscription can carry an image payload in its stored parameters,
/// which it cannot and should not.
type ReportParameterKind =
    /// Free text.
    | TextParameter
    /// A number. Values are held as their round-trip string form and
    /// parsed at resolve time by the producer.
    | NumberParameter
    /// A date. Round-tripped as ISO-8601.
    | DateParameter
    /// One of a closed set. A value outside the set is rejected at
    /// create/update rather than at the first tick.
    | ChoiceParameter of options: string list

/// One declared parameter on a report producer.
type ReportParameterSchema = {
    /// Key the subscription's parameter map is keyed by.
    Key: string
    /// Label an admin surface renders.
    DisplayName: string
    Kind: ReportParameterKind
    /// A required parameter with no supplied value is refused at
    /// create/update — never discovered at the first scheduled run.
    Required: bool
}

/// What a module publishes when it registers a report producer: enough
/// for an admin surface to offer the report and build a valid
/// subscription against it, and nothing about how it is produced.
///
/// The resolve function lives with the registry in the server tier —
/// this descriptor is the half that crosses the wire.
type ReportProducerDescriptor = {
    Key: ReportProducerKey
    /// Human-readable name shown in a subscription form.
    DisplayName: string
    /// Optional one-line description of what the report contains.
    Description: string option
    Parameters: ReportParameterSchema list
    /// Formats this producer can be rendered to. A subscription naming
    /// a format outside this list is refused at create/update; an empty
    /// list means the producer declares no restriction and any format
    /// the renderer registry serves is permitted.
    Formats: TemplateFormat list
}

/// Outcome of the most recent run, carried on the subscription itself
/// so a management surface answers "is this working?" without a second
/// query against the job store.
///
/// The success case carries the artefact's `IDataObjectStore` key and
/// version rather than its bytes: the run history is browsable through
/// the ordinary versioned-object surface, and a subscription record
/// stays small enough to list a scope's worth of them in one call.
type SubscriptionRunOutcome =
    /// Never dispatched — a freshly created subscription.
    | NeverRun
    /// Rendered, persisted and handed to the sink.
    | RunSucceeded of
        at: DateTimeOffset *
        dataObjectKey: string *
        version: int *
        /// Recipients the address book could resolve an address for.
        /// Fewer than the subscription's recipient list means some
        /// recipients have no address registered at this scope — a
        /// normal condition the address-book contract calls a skip, and
        /// one an operator should still be able to see.
        deliveredTo: int
    /// The run failed. `terminal` distinguishes "the scheduler will try
    /// again" from "this attempt was the last one" — the latter is what
    /// raises the warning notification and the audit event.
    | RunFailed of at: DateTimeOffset * reason: string * terminal: bool

/// A scheduled report subscription. Persisted per scope under
/// `_platform/report-subscriptions/{scopeId}/`.
type ReportSubscription = {
    Id: SubscriptionId
    /// Owning scope. Stamped server-side from the resolved caller — a
    /// client-supplied value is always overwritten (GP 4).
    ScopeId: string
    /// Label the owner gave this subscription.
    DisplayName: string
    /// The registered producer this subscription renders.
    ProducerKey: ReportProducerKey
    /// Parameter values, keyed by `ReportParameterSchema.Key`. Held as
    /// strings; the producer parses them.
    Parameters: Map<string, string>
    /// Five-field cron expression, validated against the scheduler's
    /// supported subset at create/update.
    Schedule: string
    /// Users the rendered report is delivered to. Addresses are
    /// resolved at delivery time via `INotificationAddressBook`, so no
    /// PII is persisted here.
    RecipientUserIds: string list
    /// Output format. Must be one the producer declares (when it
    /// declares any) and one the renderer registry serves.
    Format: TemplateFormat
    /// Paused subscriptions keep their schedule and their history; the
    /// handler skips them and the scheduler job is disabled.
    Enabled: bool
    LastRun: SubscriptionRunOutcome
    CreatedBy: string
    CreatedAt: DateTimeOffset
}

/// Create/update request. Carries only the fields a caller may choose —
/// `Id`, `ScopeId`, `LastRun`, `CreatedBy` and `CreatedAt` are all
/// server-stamped, so there is no shape in which a client can assert
/// them and be silently ignored.
type NewReportSubscription = {
    DisplayName: string
    ProducerKey: ReportProducerKey
    Parameters: Map<string, string>
    Schedule: string
    RecipientUserIds: string list
    Format: TemplateFormat
    Enabled: bool
}

/// Why a subscription operation was refused. Every case names the thing
/// the caller can act on; none carries a policy internal or another
/// scope's data.
type SubscriptionError =
    /// No subscription with that id at the resolved scope. Also the
    /// answer for an id belonging to a different scope — a caller must
    /// not be able to distinguish "does not exist" from "is not yours"
    /// (GP 4).
    | SubscriptionNotFound of SubscriptionId
    /// No producer is registered under that key.
    | UnknownProducer of ReportProducerKey
    /// The cron expression did not parse against the scheduler's
    /// supported subset. Carries the parser's reason verbatim so an
    /// admin surface can point at the offending field.
    | InvalidSchedule of expression: string * reason: string
    /// A required parameter was not supplied, or a `ChoiceParameter`
    /// value sat outside its declared options.
    | InvalidParameter of key: string * reason: string
    /// The requested format is not one the producer declares.
    | UnsupportedFormat of TemplateFormat * supported: TemplateFormat list
    /// A subscription with no recipients would render on schedule and
    /// deliver to nobody, which is a configuration mistake rather than
    /// a posture.
    | NoRecipients
    /// The caller is not permitted to manage subscriptions at this
    /// scope. See `ReportSubscriptionApiHandler.withManagementGate`.
    | SubscriptionNotAuthorised of reason: string
    /// Persistence failed. Wraps the store's message verbatim.
    | SubscriptionStorageFailure of string
    /// Run-now could not reach the scheduler — either none is composed,
    /// or the job for this subscription is not registered.
    | SchedulerUnavailable of reason: string

module SubscriptionError =
    let toMessage =
        function
        | SubscriptionNotFound id -> $"No subscription '{id}' at this scope"
        | UnknownProducer key -> $"No report producer registered under '{key}'"
        | InvalidSchedule(expr, reason) -> $"Schedule '{expr}' is not a valid cron expression: {reason}"
        | InvalidParameter(key, reason) -> $"Parameter '{key}': {reason}"
        | UnsupportedFormat(format, supported) ->
            let names = supported |> List.map (sprintf "%A") |> String.concat ", "

            let tail =
                if List.isEmpty supported then
                    "the producer declares no formats"
                else
                    $"supported: {names}"

            $"Format %A{format} is not served by this producer ({tail})"
        | NoRecipients -> "A subscription needs at least one recipient"
        | SubscriptionNotAuthorised reason -> reason
        | SubscriptionStorageFailure e -> $"Subscription storage failed: {e}"
        | SchedulerUnavailable reason -> $"Scheduler unavailable: {reason}"

module ReportSubscription =

    /// Blob prefix a scope's subscriptions live under. Named here rather
    /// than in the store so a deployment auditing `_platform/` can find
    /// the layout from the shared types.
    [<Literal>]
    let StorePrefix = "report-subscriptions"

    /// Handler name the subscription job registers under. A single
    /// constant because the compose that registers the handler and the
    /// API handler that schedules against it must not be able to drift.
    [<Literal>]
    let JobHandlerName = "_platform.reporting.subscription"

    /// `IDataObjectStore` object id a subscription's run artefacts are
    /// versioned under. Every run appends a version, so the run history
    /// IS the object's version chain.
    let artefactObjectId (id: SubscriptionId) = $"{StorePrefix}/{id}"

    /// `IDataObjectStore` data-type tag for a run artefact.
    [<Literal>]
    let ArtefactDataType = "report-subscription-run"

    /// Validate supplied parameters against a producer's declared
    /// schema. Returns the first offending key — a management surface
    /// re-validates as the user types, so enumerating every failure
    /// buys nothing the caller uses.
    let validateParameters
        (schema: ReportParameterSchema list)
        (supplied: Map<string, string>)
        : Result<unit, SubscriptionError> =
        schema
        |> List.tryPick (fun p ->
            // An absent key and an empty value are the same condition:
            // a form posts "" for a field the user left alone, and a
            // required parameter satisfied by "" would fail later, at
            // the producer, where the caller is no longer present.
            let supplied =
                match supplied.TryFind p.Key with
                | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
                | _ -> None

            match supplied, p.Required with
            | None, true -> Some(InvalidParameter(p.Key, "required, but no value was supplied"))
            | None, false -> None
            | Some value, _ ->
                match p.Kind with
                | ChoiceParameter options when not (List.contains value options) ->
                    let rendered = String.concat ", " options
                    Some(InvalidParameter(p.Key, $"'{value}' is not one of: {rendered}"))
                | _ -> None)
        |> function
            | Some error -> Error error
            | None -> Ok()

    /// Validate a requested format against a producer's declaration. An
    /// empty declaration is "no restriction", not "nothing permitted" —
    /// a producer that does not care which format it renders to should
    /// not have to enumerate the DU.
    let validateFormat (declared: TemplateFormat list) (requested: TemplateFormat) : Result<unit, SubscriptionError> =
        if List.isEmpty declared || List.contains requested declared then
            Ok()
        else
            Error(UnsupportedFormat(requested, declared))