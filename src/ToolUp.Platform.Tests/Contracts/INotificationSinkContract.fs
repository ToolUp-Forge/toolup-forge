module ToolUp.Platform.Tests.Contracts.INotificationSinkContract

open System
open Expecto
open ToolUp.Platform

/// Contract assertions every `INotificationSink` implementation must
/// satisfy. Bound by an `InProcess` test that supplies a factory
/// returning a fresh sink + an envelope shape appropriate for the
/// sink's `Kind`. Cross-cutting properties — identity by value,
/// async at every method, idempotent skip semantics, no audit on
/// `Skipped` — are enforced at the test level so future companions
/// (SendGrid / Twilio / WebPush) bind to the same pack with a vendor-
/// specific factory.
///
/// Phase 6f. Mirrors `INotificationChannelContract` in shape; both
/// packs accept a display name and a factory and return an Expecto
/// `testList` the runner aggregates with the rest.
let tests (name: string) (factory: unit -> INotificationSink) (sampleEnvelope: string -> NotificationEnvelope) =
    testList $"{name} — INotificationSink contract" [
        testCaseAsync "Kind round-trips through SinkKind.toWireString"
        <| async {
            let sink = factory ()

            // Phase 11.C.5 Tier 3 — Kind is a SinkKind DU. The
            // wire-format string must round-trip through tryParse so
            // dispatch / audit / uniqueness all see the same key.
            let wire = NotificationKind.SinkKind.toWireString sink.Kind

            match NotificationKind.SinkKind.tryParse wire with
            | Some parsed ->
                Expect.equal
                    parsed
                    sink.Kind
                    "SinkKind.toWireString >> tryParse must round-trip every shipped sink Kind"
            | None ->
                failtestf
                    "SinkKind.tryParse returned None for wire-string %s — every shipped sink Kind must round-trip"
                    wire
        }

        testCaseAsync "Provider is non-empty"
        <| async {
            let sink = factory ()

            Expect.isFalse (String.IsNullOrWhiteSpace sink.Provider) "Sink.Provider must be a non-empty vendor label"
        }

        testCaseAsync "Send returns a SinkResult (not a thrown exception) for a well-formed envelope"
        <| async {
            let sink = factory ()
            let envelope = sampleEnvelope "scope-test"

            // The point of the contract is that Send classifies its
            // own outcome — vendor failures surface as
            // TransientFailure / PermanentFailure, not as exceptions
            // bubbling up the dispatcher. Both Skipped and the two
            // failure modes are valid results for a fake/offline
            // factory; only thrown exceptions fail the assertion.
            let! result = sink.Send(envelope.ScopeId, envelope)

            match result with
            | SinkResult.Delivered _
            | SinkResult.Skipped _
            | SinkResult.TransientFailure _
            | SinkResult.PermanentFailure _ -> ()
        }
    ]

/// Whether a WhatsApp sink sends approved templates. A sink that cannot
/// must refuse a template message as `PermanentFailure` — the same rule
/// the email sinks follow for `TemplatedEmail` — so a deployment that
/// swaps adapters fails fast rather than silently sending nothing.
type WhatsAppTemplateSupport =
    /// The sink sends template messages.
    | SupportsTemplates
    /// The sink sends free-form messages only.
    | FreeFormOnly

/// Phase 827 — the cases every `SinkKind.WhatsApp` sink must satisfy, on
/// top of `tests`. Bound by each vendor companion with a factory whose
/// sink talks to a FAKE transport, and `recipient` a recipient that
/// sink's address book resolves. The 24-hour-window refusal is not a
/// sink concern — the server refuses a free-form send outside the window
/// before any sink runs — so a free-form send reaching a sink is one the
/// server already admitted, and the sink must accept it.
let whatsAppTests
    (name: string)
    (factory: unit -> INotificationSink)
    (recipient: RecipientId)
    (support: WhatsAppTemplateSupport)
    =
    let envelopeOf (whatsApp: WhatsAppEnvelope) =
        NotificationEnvelope.create "scope-test" (TransactionalWhatsApp whatsApp)

    let template = {
        Recipients = [ recipient ]
        TemplateName = Some "appointment_reminder"
        TemplateLanguage = Some "en_GB"
        TemplateParameters = [ "Alex"; "10:00" ]
        Body = None
        Metadata = Map.empty
        CorrelationId = Some "corr-827-template"
    }

    let freeForm = {
        template with
            TemplateName = None
            TemplateLanguage = None
            TemplateParameters = []
            Body = Some "Thanks — see you tomorrow."
            CorrelationId = Some "corr-827-free-form"
    }

    let templateCase =
        match support with
        | SupportsTemplates ->
            testCaseAsync "a template message round-trips to Delivered against the fake transport"
            <| async {
                let sink = factory ()
                let envelope = envelopeOf template
                let! result = sink.Send(envelope.ScopeId, envelope)

                match result with
                | SinkResult.Delivered _ -> ()
                | other -> failtestf "a template-capable WhatsApp sink must deliver a template message; got %A" other
            }
        | FreeFormOnly ->
            testCaseAsync "a sink without template support refuses a template message as PermanentFailure"
            <| async {
                let sink = factory ()
                let envelope = envelopeOf template
                let! result = sink.Send(envelope.ScopeId, envelope)

                match result with
                | SinkResult.PermanentFailure _ -> ()
                | other ->
                    failtestf
                        "a WhatsApp sink that cannot send templates must say so with PermanentFailure (as the email sinks do for TemplatedEmail); got %A"
                        other
            }

    testList $"{name} — INotificationSink WhatsApp contract" [
        testCaseAsync "Kind is SinkKind.WhatsApp"
        <| async {
            let sink = factory ()

            Expect.equal
                sink.Kind
                NotificationKind.SinkKind.WhatsApp
                "a WhatsApp sink registers under SinkKind.WhatsApp"
        }

        templateCase

        testCaseAsync "a free-form Body inside the window is accepted by the sink"
        <| async {
            let sink = factory ()
            let envelope = envelopeOf freeForm
            let! result = sink.Send(envelope.ScopeId, envelope)

            match result with
            | SinkResult.Delivered _ -> ()
            | other ->
                failtestf
                    "a free-form WhatsApp message reaching the sink was admitted by the server's window rule; the sink must deliver it, got %A"
                    other
        }
    ]