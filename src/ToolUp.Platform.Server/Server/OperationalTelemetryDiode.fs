// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text.Json

// ─── Phase 488.C — the operational-telemetry diode ────────────────────
//
// An appliance runs inside someone else's infrastructure, on their data,
// and the party operating it still needs to know whether it is healthy —
// which version is running, whether preflight passed, roughly how much
// work it is doing. That is a legitimate need and a dangerous channel:
// every "send us some diagnostics" pipe ever built has, sooner or later,
// carried a row of customer data out with it, because the pipe's payload
// was a string and a string will hold anything.
//
// This diode closes that structurally rather than by policy. The wire
// shape — `OperationalTelemetryFrame` — has **no `string` field anywhere
// in its transitive closure**. Every value is an integer, a boolean, or a
// case of a closed discriminated union whose cases are all nullary. There
// is no field a caller could put a row, a name, an identifier, a message,
// or a stack trace into, so "the diode never carries content" is not a
// review item that has to hold across every future edit — it is a
// property of the type, asserted by reflection in the test pack.
//
// **Why a closed enumeration and not a redaction pass.** The 9n bundle
// (and its 488.D appliance variant) redacts: it takes arbitrary content
// and masks what it recognises. Redaction is the right tool when the
// payload is genuinely open and an operator inspects the result before it
// leaves. It is the wrong tool for an automated outbound channel, where
// nobody reads each frame and an unrecognised field name means content
// ships. A closed schema inverts the default: a field that does not exist
// cannot leak, and adding one is a visible, reviewable type change that
// the closure test fails on until it is either an enumerated case or a
// number.
//
// **Consent-gated, default off (GP 13).** `DiodeWithheld` is the identity
// and the default: `transmit` never invokes the outbound function, so a
// deployment that has not consented produces literally zero outbound
// traffic — not a filtered request, not an empty POST. Consent is not a
// boolean either: `DiodeGranted` carries the SECTIONS the operator agreed
// to, so consenting to health reporting does not silently also consent to
// counters.
//
// **Every transmission is logged locally (GP 6).** `transmit` journals
// the frame, the exact payload bytes that left, and the outcome, through
// `IDiodeTransmissionLog` — so the operator can answer "what has this
// appliance sent?" from their own side, against the real bytes rather
// than a description of them. A suppressed frame is journalled too, with
// `Payload = None`: "the diode is off and 400 frames were withheld" is
// exactly the reassurance an operator wants, and it costs no egress.
//
// **Generic substrate (GP 1).** No vendor, no transport, no HTTP client.
// The outbound side is the structural `DiodeTransmit` function seam — the
// same decoupling Phase 182's `Sbom.SignArtefact` uses to keep a crypto
// stack out of the Build package. Whatever ships the payload (an HTTPS
// POST, a file drop an operator forwards by hand, a message on a queue
// the customer already runs) is the deployment's choice.

/// A subsystem the diode can report health for — a CLOSED vocabulary,
/// deliberately coarser than the Phase 279 `ComponentId` space.
///
/// `ComponentId` is a string, and a component id is not obviously content
/// until a deployment names one after a customer ("tenant-northwind
/// -store"). Reporting at subsystem granularity gives the operating party
/// what they actually need — which *kind* of thing is unhealthy — while
/// keeping the frame free of any value the deployment chose.
type DiodeSubsystem =
    /// The platform composition itself: startup, configuration, routing.
    | PlatformSubsystem
    /// Blob / object storage (`IBlobStorage`).
    | StorageSubsystem
    /// Identity and authorization (`IAuthProvider`, the classifier).
    | AuthSubsystem
    /// Background work (`IJobScheduler`, `IJobStore`).
    | SchedulerSubsystem
    /// Outbound and in-app notification (`INotificationChannel`, sinks).
    | NotificationSubsystem
    /// The append-only event / audit substrate (`IEventStore`, `IAuditLog`).
    | EventStoreSubsystem
    /// Retrieval and indexing (`IVectorStore`, `IRetrievalPipeline`).
    | SearchSubsystem
    /// Model inference (`IAIProvider`, `IEmbeddingProvider`).
    | InferenceSubsystem

[<RequireQualifiedAccess>]
module DiodeSubsystem =

    /// Stable wire token. This is SDK-declared vocabulary — the only
    /// strings that reach the wire are these, and they are fixed in this
    /// source file rather than supplied by a deployment.
    let toWireString (subsystem: DiodeSubsystem) : string =
        match subsystem with
        | PlatformSubsystem -> "platform"
        | StorageSubsystem -> "storage"
        | AuthSubsystem -> "auth"
        | SchedulerSubsystem -> "scheduler"
        | NotificationSubsystem -> "notification"
        | EventStoreSubsystem -> "event-store"
        | SearchSubsystem -> "search"
        | InferenceSubsystem -> "inference"

    /// Every subsystem, in wire-token order — the vocabulary a receiving
    /// party can pin its own schema against.
    let all: DiodeSubsystem list = [
        PlatformSubsystem
        StorageSubsystem
        AuthSubsystem
        SchedulerSubsystem
        NotificationSubsystem
        EventStoreSubsystem
        SearchSubsystem
        InferenceSubsystem
    ]

/// A three-valued health state, matching the `IHealthCheck` vocabulary
/// without the accompanying message — the message is free text, which is
/// exactly what cannot ride this channel.
type DiodeHealthState =
    | DiodeHealthy
    | DiodeDegraded
    | DiodeUnhealthy

[<RequireQualifiedAccess>]
module DiodeHealthState =

    let toWireString (state: DiodeHealthState) : string =
        match state with
        | DiodeHealthy -> "healthy"
        | DiodeDegraded -> "degraded"
        | DiodeUnhealthy -> "unhealthy"

/// The class of preflight validator a reading summarises — the same
/// closed Phase 585 classification the Phase 9m aggregator derives from
/// its markers (`ISecurityClassValidator` / `IStructuralClassValidator` /
/// the unmarked external-probe default).
///
/// Reporting by CLASS rather than by validator name is the same choice
/// `DiodeSubsystem` makes: a validator's `Name` is deployment-chosen
/// (`"oidc-auth (https://login.northwind.example)"` is a documented
/// naming convention, and it is an internal hostname), whereas its class
/// is SDK vocabulary.
type DiodeValidatorClass =
    | DiodeSecurityClass
    | DiodeStructuralClass
    | DiodeExternalProbeClass

[<RequireQualifiedAccess>]
module DiodeValidatorClass =

    let toWireString (cls: DiodeValidatorClass) : string =
        match cls with
        | DiodeSecurityClass -> "security-class"
        | DiodeStructuralClass -> "structural-class"
        | DiodeExternalProbeClass -> "external-probe-class"

    let all: DiodeValidatorClass list = [ DiodeSecurityClass; DiodeStructuralClass; DiodeExternalProbeClass ]

/// A preflight outcome, carrying no message. `ValidationResult.Warning`
/// and `.Error` both carry a `message: string` that routinely quotes a
/// connection string, a hostname, or a sentinel blob name; the diode
/// reports that a validator class produced N warnings, never what they
/// said.
type DiodePreflightOutcome =
    | DiodePreflightOk
    | DiodePreflightWarning
    | DiodePreflightError

[<RequireQualifiedAccess>]
module DiodePreflightOutcome =

    let toWireString (outcome: DiodePreflightOutcome) : string =
        match outcome with
        | DiodePreflightOk -> "ok"
        | DiodePreflightWarning -> "warning"
        | DiodePreflightError -> "error"

/// A coarse counter the diode can report — a CLOSED enumeration, so a
/// deployment cannot invent `Counter = "rows-in-orders-table"` and turn a
/// count into a disclosure. Adding a counter is a deliberate SDK change,
/// reviewed as one.
type DiodeCounter =
    /// Requests the appliance served (any status).
    | RequestsServed
    /// Requests refused before reaching a handler (auth, rate limit).
    | RequestsRefused
    /// Background jobs that ran to completion.
    | JobsRun
    /// Background jobs that ended in failure.
    | JobsFailed
    /// Artefact-provenance verifications performed (488.B).
    | UpgradeVerifications
    /// Artefact-provenance verifications that REFUSED (488.B) — the
    /// number the operating party most wants to see rise.
    | UpgradeRefusals
    /// Support bundles the operator generated (488.D). A count, not the
    /// bundles: the vendor never pulls one.
    | SupportBundlesGenerated

[<RequireQualifiedAccess>]
module DiodeCounter =

    let toWireString (counter: DiodeCounter) : string =
        match counter with
        | RequestsServed -> "requests-served"
        | RequestsRefused -> "requests-refused"
        | JobsRun -> "jobs-run"
        | JobsFailed -> "jobs-failed"
        | UpgradeVerifications -> "upgrade-verifications"
        | UpgradeRefusals -> "upgrade-refusals"
        | SupportBundlesGenerated -> "support-bundles-generated"

    let all: DiodeCounter list = [
        RequestsServed
        RequestsRefused
        JobsRun
        JobsFailed
        UpgradeVerifications
        UpgradeRefusals
        SupportBundlesGenerated
    ]

/// The running build's version, as three integers.
///
/// **Deliberately not a string.** A version is the one field where a
/// string looks unavoidable and is not: `"0.9.4"` parses to three numbers,
/// whereas a string field is a hole in the closure guarantee that every
/// future reader would have to be told not to widen. Pre-release / build
/// metadata is dropped rather than carried — the operating party needs to
/// know which release is running, not which CI run produced it.
type DiodeVersion = { Major: int; Minor: int; Patch: int }

[<RequireQualifiedAccess>]
module DiodeVersion =

    let zero: DiodeVersion = { Major = 0; Minor = 0; Patch = 0 }

    let create (major: int) (minor: int) (patch: int) : DiodeVersion = {
        Major = max 0 major
        Minor = max 0 minor
        Patch = max 0 patch
    }

    /// Read the three numeric components out of a dotted version string,
    /// discarding any pre-release / build suffix and any component that
    /// is not an integer. Total — an unparseable input yields `zero`
    /// rather than raising, because a diode frame must never be the thing
    /// that crashes an appliance.
    ///
    /// This is the ONE place a string touches the diode, and it is on the
    /// way IN, in-process, before the frame exists. Nothing it reads
    /// reaches the wire except as three integers.
    let parse (version: string) : DiodeVersion =
        if String.IsNullOrWhiteSpace version then
            zero
        else
            let numeric =
                version.Split([| '-'; '+' |]).[0].Split('.')
                |> Array.map (fun part ->
                    match Int32.TryParse part with
                    | true, v -> max 0 v
                    | _ -> 0)

            let at index =
                if index < numeric.Length then numeric[index] else 0

            {
                Major = at 0
                Minor = at 1
                Patch = at 2
            }

/// One subsystem's health, as it rides the wire.
type DiodeHealthReading = {
    Subsystem: DiodeSubsystem
    State: DiodeHealthState
}

/// One validator class's preflight summary: the worst outcome the class
/// produced and how many readings contributed to it.
type DiodePreflightReading = {
    Class: DiodeValidatorClass
    Outcome: DiodePreflightOutcome
    /// How many validators of this class ran. A count, never a name.
    Validators: int
}

/// One coarse counter's value.
type DiodeCounterReading = { Counter: DiodeCounter; Value: int64 }

/// **The closed schema.** Everything the diode can ever say about an
/// appliance.
///
/// Every field is an `int`, an `int64`, or a list of records built from
/// integers and nullary DU cases. There is no `string`, no `obj`, no
/// `Map`, no free-form bag — verified by reflecting over this type's
/// transitive closure in the test pack, so a future field that would open
/// the channel fails the build rather than passing review.
///
/// The section lists may be empty: an operator who consented only to
/// health reporting transmits `Counters = []`, and `project` is what
/// empties them.
type OperationalTelemetryFrame = {
    /// Wire-schema version of this frame shape. Bumped when a field is
    /// added or removed, so a receiving party can reject a frame it does
    /// not understand instead of guessing.
    Schema: int
    /// The running build.
    Version: DiodeVersion
    /// Process uptime. Answers "did it restart?" without a timestamp
    /// series.
    UptimeSeconds: int64
    /// When the frame was assembled, as a Unix timestamp — a number, so
    /// no format string and no locale reach the wire.
    AtUnixSeconds: int64
    Health: DiodeHealthReading list
    Preflight: DiodePreflightReading list
    Counters: DiodeCounterReading list
}

/// A consentable section of the frame. Consent is per-section rather than
/// a single boolean: an operator who is content to report health states
/// has not thereby agreed to report throughput.
type DiodeSection =
    | HealthSection
    | PreflightSection
    | CounterSection

[<RequireQualifiedAccess>]
module DiodeSection =

    let toWireString (section: DiodeSection) : string =
        match section with
        | HealthSection -> "health"
        | PreflightSection -> "preflight"
        | CounterSection -> "counters"

    let all: DiodeSection list = [ HealthSection; PreflightSection; CounterSection ]

/// An operator's recorded consent: when it was given and which sections
/// it covers.
///
/// The version / schema / uptime header always rides when consent exists
/// at all — it is the irreducible "this appliance is alive, on this
/// build", and a grant covering none of the three sections still says
/// that much. A grant covering nothing at all is not expressible as
/// "consent"; that is `DiodeWithheld`.
type DiodeConsentGrant = {
    GrantedAtUnixSeconds: int64
    /// The sections the operator agreed to. An empty list transmits the
    /// header only.
    Sections: DiodeSection list
}

/// Whether the diode may transmit. **`DiodeWithheld` is the default and
/// the identity** (GP 11 / GP 13): `transmit` does not invoke the
/// outbound function at all, so an unconsented appliance produces zero
/// outbound traffic rather than a filtered or empty request.
type DiodeConsent =
    /// Default. Nothing leaves.
    | DiodeWithheld
    /// The operator has consented to the named sections.
    | DiodeGranted of DiodeConsentGrant

/// The outbound side, as a structural function rather than an interface
/// over a transport (GP 1). Receives the rendered payload; returns `Ok`
/// on delivery or an error description for the local journal.
///
/// The description is journalled LOCALLY and never transmitted — a
/// delivery failure is between the appliance and its operator.
type DiodeTransmit = string -> Async<Result<unit, string>>

/// What happened to one frame.
type DiodeTransmissionOutcome =
    /// Consent withheld for the whole diode. The outbound function was
    /// not invoked; nothing left the appliance.
    | DiodeSuppressed
    /// Delivered. Carries the payload size so the journal can be read as
    /// "how much has left" without re-measuring every entry.
    | DiodeSent of payloadBytes: int
    /// The outbound function reported a failure. The reason is local-only.
    | DiodeFailed of reason: string

[<RequireQualifiedAccess>]
module DiodeTransmissionOutcome =

    let describe (outcome: DiodeTransmissionOutcome) : string =
        match outcome with
        | DiodeSuppressed -> "suppressed (consent withheld) — nothing left the appliance"
        | DiodeSent bytes -> sprintf "sent (%d bytes)" bytes
        | DiodeFailed reason -> "failed: " + reason

/// One journal entry: the frame as PROJECTED for transmission, the exact
/// payload bytes that left, and the outcome.
///
/// `Payload` is `Some` only when something actually left — a suppressed
/// frame journals `None`, which is the difference between "we sent this
/// and it was empty" and "we sent nothing", and an operator auditing the
/// channel needs to be able to tell those apart.
type DiodeTransmissionRecord = {
    AtUnixSeconds: int64
    /// The projected frame — what consent allowed, not what was offered.
    Frame: OperationalTelemetryFrame
    /// The exact bytes that left, or `None` when nothing did.
    Payload: string option
    Outcome: DiodeTransmissionOutcome
}

/// The local transmission journal. Local-only by construction: there is
/// no method here that sends anything, and the diode writes to it on
/// every call including the suppressed ones.
///
/// **Six portability rules (GP 12).** Identity by value (records only);
/// no async surface because this is a write-only local sink on the same
/// hot-path footing as `IMetricsSink`, which carries the documented sync
/// exception; no retry semantics (a journal write that fails is a local
/// defect, not a delivery); stateless between calls from the caller's
/// point of view; no ordering claim beyond "entries are appended".
type IDiodeTransmissionLog =
    /// Append one record. Implementations must be thread-safe and must
    /// not throw — a journalling failure must never be what stops an
    /// appliance.
    abstract Record: DiodeTransmissionRecord -> unit

    /// The retained entries, oldest first.
    abstract Entries: DiodeTransmissionRecord list

/// Bounded in-memory journal — the default an appliance gets. Retains the
/// most recent `capacity` records and drops the oldest, so a long-running
/// appliance cannot journal itself out of memory.
///
/// An appliance that wants the journal to survive a restart wires an
/// `IDiodeTransmissionLog` over its own `IEventStore` / `IAuditLog`; the
/// diode does not care which, and deliberately does not reach for one
/// itself (a diode that required an audit substrate would be a diode a
/// minimal appliance could not compose).
[<Sealed>]
type DiodeTransmissionJournal(capacity: int) =
    let gate = obj ()
    let mutable entries: DiodeTransmissionRecord list = []
    let bound = max 1 capacity

    /// Default retention — 500 frames, enough to cover a day at a
    /// three-minute cadence without unbounded growth.
    new() = DiodeTransmissionJournal(500)

    member _.Capacity = bound

    interface IDiodeTransmissionLog with
        member _.Record(record: DiodeTransmissionRecord) =
            lock gate (fun () ->
                let appended = entries @ [ record ]

                entries <-
                    if List.length appended > bound then
                        appended |> List.skip (List.length appended - bound)
                    else
                        appended)

        member _.Entries = lock gate (fun () -> entries)

/// Frame assembly, consent projection, canonical rendering, and the
/// gated transmit.
[<RequireQualifiedAccess>]
module OperationalTelemetryDiode =

    /// Current wire-schema version of `OperationalTelemetryFrame`.
    [<Literal>]
    let Schema = 1

    /// A frame carrying only the header — no health, no preflight, no
    /// counters. What an operator who granted consent with no sections
    /// transmits, and the base every richer frame is built on.
    let header (version: DiodeVersion) (uptimeSeconds: int64) (atUnixSeconds: int64) : OperationalTelemetryFrame = {
        Schema = Schema
        Version = version
        UptimeSeconds = max 0L uptimeSeconds
        AtUnixSeconds = atUnixSeconds
        Health = []
        Preflight = []
        Counters = []
    }

    /// Attach subsystem health readings, de-duplicated by subsystem
    /// (last write wins) and ordered by the declared vocabulary, so two
    /// frames describing the same state render identically.
    let withHealth (readings: DiodeHealthReading list) (frame: OperationalTelemetryFrame) : OperationalTelemetryFrame =
        let bySubsystem = readings |> List.map (fun r -> r.Subsystem, r) |> Map.ofList

        {
            frame with
                Health = DiodeSubsystem.all |> List.choose (fun s -> Map.tryFind s bySubsystem)
        }

    /// Attach preflight readings, de-duplicated by class and ordered by
    /// the declared vocabulary.
    let withPreflight
        (readings: DiodePreflightReading list)
        (frame: OperationalTelemetryFrame)
        : OperationalTelemetryFrame =
        let byClass = readings |> List.map (fun r -> r.Class, r) |> Map.ofList

        {
            frame with
                Preflight = DiodeValidatorClass.all |> List.choose (fun c -> Map.tryFind c byClass)
        }

    /// Attach counter readings, de-duplicated by counter and ordered by
    /// the declared vocabulary. Negative values are clamped to zero — a
    /// counter is a count.
    let withCounters
        (readings: DiodeCounterReading list)
        (frame: OperationalTelemetryFrame)
        : OperationalTelemetryFrame =
        let byCounter =
            readings
            |> List.map (fun r -> r.Counter, { r with Value = max 0L r.Value })
            |> Map.ofList

        {
            frame with
                Counters = DiodeCounter.all |> List.choose (fun c -> Map.tryFind c byCounter)
        }

    /// Reduce a frame to the sections an operator consented to. The
    /// header (schema / version / uptime / timestamp) always survives —
    /// it is the irreducible "alive, on this build" the diode exists for,
    /// and it contains no deployment-chosen value at all.
    let project (grant: DiodeConsentGrant) (frame: OperationalTelemetryFrame) : OperationalTelemetryFrame =
        let consented section = List.contains section grant.Sections

        {
            frame with
                Health = (if consented HealthSection then frame.Health else [])
                Preflight = (if consented PreflightSection then frame.Preflight else [])
                Counters = (if consented CounterSection then frame.Counters else [])
        }

    /// Canonical JSON for a frame — the exact bytes that leave.
    ///
    /// Written field-by-field through `Utf8JsonWriter` rather than
    /// serialised reflectively, for the same reason the schema is closed:
    /// a reflective serialiser writes whatever the type happens to carry,
    /// so a future field would ship silently. Here every emitted value is
    /// a number or a wire token from a closed vocabulary declared in this
    /// file, and adding a field means writing a line here.
    ///
    /// Key order is declaration order and list order is the declared
    /// vocabulary order (see `withHealth` / `withPreflight` /
    /// `withCounters`), so the rendering is deterministic and two
    /// appliances in the same state produce byte-identical payloads.
    let render (frame: OperationalTelemetryFrame) : string =
        use buffer = new IO.MemoryStream()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))

        writer.WriteStartObject()
        writer.WriteNumber("schema", frame.Schema)

        writer.WriteStartObject("version")
        writer.WriteNumber("major", frame.Version.Major)
        writer.WriteNumber("minor", frame.Version.Minor)
        writer.WriteNumber("patch", frame.Version.Patch)
        writer.WriteEndObject()

        writer.WriteNumber("uptimeSeconds", frame.UptimeSeconds)
        writer.WriteNumber("atUnixSeconds", frame.AtUnixSeconds)

        writer.WriteStartArray("health")

        for reading in frame.Health do
            writer.WriteStartObject()
            writer.WriteString("subsystem", DiodeSubsystem.toWireString reading.Subsystem)
            writer.WriteString("state", DiodeHealthState.toWireString reading.State)
            writer.WriteEndObject()

        writer.WriteEndArray()

        writer.WriteStartArray("preflight")

        for reading in frame.Preflight do
            writer.WriteStartObject()
            writer.WriteString("class", DiodeValidatorClass.toWireString reading.Class)
            writer.WriteString("outcome", DiodePreflightOutcome.toWireString reading.Outcome)
            writer.WriteNumber("validators", reading.Validators)
            writer.WriteEndObject()

        writer.WriteEndArray()

        writer.WriteStartArray("counters")

        for reading in frame.Counters do
            writer.WriteStartObject()
            writer.WriteString("counter", DiodeCounter.toWireString reading.Counter)
            writer.WriteNumber("value", reading.Value)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

        Text.Encoding.UTF8.GetString(buffer.ToArray())

    /// Transmit one frame, gated on consent and journalled either way.
    ///
    /// **`DiodeWithheld` never invokes `send`.** Not "invokes it with an
    /// empty payload", not "invokes it and discards the result" — the
    /// outbound function is not called, so an appliance on the default
    /// opens no connection and resolves no name. That is the property
    /// the acceptance test asserts, by passing a `send` that fails the
    /// test if it is ever reached.
    ///
    /// `now` is injected so the journal is assertable without a clock.
    let transmit
        (consent: DiodeConsent)
        (log: IDiodeTransmissionLog)
        (send: DiodeTransmit)
        (now: unit -> int64)
        (frame: OperationalTelemetryFrame)
        : Async<DiodeTransmissionOutcome> =
        async {
            match consent with
            | DiodeWithheld ->
                // Journalled locally with no payload: the operator can
                // see the diode declined to speak, which is a different
                // fact from it having spoken and said nothing.
                log.Record {
                    AtUnixSeconds = now ()
                    Frame = frame
                    Payload = None
                    Outcome = DiodeSuppressed
                }

                return DiodeSuppressed
            | DiodeGranted grant ->
                let projected = project grant frame
                let payload = render projected

                let! result = async {
                    try
                        return! send payload
                    with ex ->
                        return Result.Error(ex.Message)
                }

                let outcome =
                    match result with
                    | Ok() -> DiodeSent(Text.Encoding.UTF8.GetByteCount payload)
                    | Result.Error reason -> DiodeFailed reason

                log.Record {
                    AtUnixSeconds = now ()
                    Frame = projected
                    Payload = Some payload
                    Outcome = outcome
                }

                return outcome
        }

    /// Total bytes this appliance has transmitted, per its own journal —
    /// the number an operator reads to confirm the channel is as quiet as
    /// they expect. Zero for an appliance on the default consent, and
    /// derived from the journal rather than a separate counter, so it
    /// cannot disagree with the record of what left.
    let bytesTransmitted (log: IDiodeTransmissionLog) : int64 =
        log.Entries
        |> List.sumBy (fun entry ->
            match entry.Outcome with
            | DiodeSent bytes -> int64 bytes
            | DiodeSuppressed
            | DiodeFailed _ -> 0L)