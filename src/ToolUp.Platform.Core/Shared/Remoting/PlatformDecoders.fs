// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting

open System
open ToolUp.Platform
open ToolUp.Platform.DeploymentVerification

// ─── Phase 785 — the named, bounded set of hand-written decoders ─────
//
// **The set is named here and nowhere else, and a record outside it
// keeps the reflection path BY DESIGN rather than by omission.** Two
// API records are covered — both of them platform-owned `_platform.*`
// surfaces that every deployment mounts, which is the only traffic
// claim this repository can actually support (see the paragraph below)
// — together with every wire type their methods carry:
//
//   1. **`IHealthMonitorApi`** (`Shared/HealthMonitorApi.fs`). The
//      Owner/Admin health surface behind the built-in HealthMonitorUI
//      panel: five methods, each polled on a refresh, and the one
//      platform record an operator holds open while watching a
//      deployment. Wire types: `HealthProbeView`,
//      `PreflightOutcomeView`, `HealthSnapshot`, `PreflightSnapshotView`,
//      `JobSchedulerTelemetryView`, `DegradedCapability`,
//      `AIDenialGroupCount`, `AIDenialToolModulePair`,
//      `RecentAIDenial`, `AIDenialRollup`, and the five `Result<_,
//      string>` returns over them.
//   2. **`IDeploymentVerificationApi`** (`Shared/DeploymentVerification.fs`).
//      The Phase 686 report. Covered deliberately rather than
//      incidentally: the Phase 785 composition-profile facet reports
//      through this very record, so a facet claiming algebra coverage
//      over a report that decodes by reflection would be asserting
//      something about itself it had not done. Wire types:
//      `VerificationSectionVerdict`, `DeploymentVerificationOutcome`,
//      `ReportSection`, `NotProvedStatement`,
//      `DeploymentVerificationReport`, and the `Result<_, string>`
//      return over it.
//
// **How the set was chosen, and the claim that was NOT available.**
// Phase 785's task text says the set is enumerated "from the remoting
// audit/telemetry counts". There is no such artefact in this
// repository: the remoting tier records per-REQUEST metrics and audit
// envelopes, and nothing anywhere counts calls per API record, so no
// traffic ranking could be read. The measurable proxy is the one used
// instead — the platform's OWN always-mounted surfaces, whose traffic
// is a property of the SDK rather than of any consumer's application.
// A consumer wanting its own records on the algebra path registers
// them the same way this file does, which is what the doc page shows.
//
// **Hand-writing is deliberately not extended further.** The Phase 785
// facet's `Reflection` count under the verified composition profile,
// once this set is exhausted, is the measured trigger for Phase 69k's
// generator — the first trigger that phase has ever had that is
// counted rather than anticipated. Adding a twelfth hand-written
// decoder here makes that number smaller and the case for the
// generator weaker, which is the opposite of what the count is for.

/// Phase 785 — algebra decoders for the platform's own `_platform.*`
/// wire types, composed from `Decode`'s combinators. Every one is a
/// closed positional match over the record's declaration order, which
/// is the order `Write.writeRecord` emits.
[<RequireQualifiedAccess>]
module PlatformDecoders =

    // ─── IHealthMonitorApi ───────────────────────────────────────────

    let healthProbeView: Decoder<HealthProbeView> =
        Decode.succeed (fun name kind timeoutMs status message elapsedMs -> {
            Name = name
            Kind = kind
            TimeoutMs = timeoutMs
            Status = status
            Message = message
            ElapsedMs = elapsedMs
        })
        |> Decode.apply (Decode.field "Name" 0 Decode.asString)
        |> Decode.apply (Decode.field "Kind" 1 Decode.asString)
        |> Decode.apply (Decode.field "TimeoutMs" 2 Decode.asInt32)
        |> Decode.apply (Decode.field "Status" 3 Decode.asString)
        |> Decode.apply (Decode.field "Message" 4 Decode.asString)
        |> Decode.apply (Decode.field "ElapsedMs" 5 Decode.asInt64)

    let preflightOutcomeView: Decoder<PreflightOutcomeView> =
        Decode.succeed (fun name status message elapsedMs -> {
            Name = name
            Status = status
            Message = message
            ElapsedMs = elapsedMs
        })
        |> Decode.apply (Decode.field "Name" 0 Decode.asString)
        |> Decode.apply (Decode.field "Status" 1 Decode.asString)
        |> Decode.apply (Decode.field "Message" 2 Decode.asString)
        |> Decode.apply (Decode.field "ElapsedMs" 3 Decode.asInt64)

    let healthSnapshot: Decoder<HealthSnapshot> =
        Decode.succeed (fun generatedAt probes -> {
            GeneratedAt = generatedAt
            Probes = probes
        })
        |> Decode.apply (Decode.field "GeneratedAt" 0 Decode.asDateTime)
        |> Decode.apply (Decode.field "Probes" 1 (Decode.list healthProbeView))

    let preflightSnapshotView: Decoder<PreflightSnapshotView> =
        Decode.succeed (fun hasSnapshot outcomes -> {
            HasSnapshot = hasSnapshot
            Outcomes = outcomes
        })
        |> Decode.apply (Decode.field "HasSnapshot" 0 Decode.asBool)
        |> Decode.apply (Decode.field "Outcomes" 1 (Decode.list preflightOutcomeView))

    let jobSchedulerTelemetryView: Decoder<JobSchedulerTelemetryView> =
        Decode.succeed (fun hasScheduler missed lastDrift lastMissedAt generatedAt -> {
            HasScheduler = hasScheduler
            TickMissedCount60Min = missed
            LastDriftMs = lastDrift
            LastTickMissedAt = lastMissedAt
            GeneratedAt = generatedAt
        })
        |> Decode.apply (Decode.field "HasScheduler" 0 Decode.asBool)
        |> Decode.apply (Decode.field "TickMissedCount60Min" 1 Decode.asInt32)
        |> Decode.apply (Decode.field "LastDriftMs" 2 (Decode.option Decode.asInt64))
        |> Decode.apply (Decode.field "LastTickMissedAt" 3 (Decode.option Decode.asDateTime))
        |> Decode.apply (Decode.field "GeneratedAt" 4 Decode.asDateTime)

    let degradedCapability: Decoder<DegradedCapability> =
        Decode.succeed (fun capability since reason impact remediation -> {
            Capability = capability
            DegradedSince = since
            Reason = reason
            Impact = impact
            Remediation = remediation
        })
        |> Decode.apply (Decode.field "Capability" 0 Decode.asString)
        |> Decode.apply (Decode.field "DegradedSince" 1 Decode.asDateTimeOffset)
        |> Decode.apply (Decode.field "Reason" 2 Decode.asString)
        |> Decode.apply (Decode.field "Impact" 3 Decode.asString)
        |> Decode.apply (Decode.field "Remediation" 4 Decode.asString)

    let aiDenialGroupCount: Decoder<AIDenialGroupCount> =
        Decode.succeed (fun key count -> { Key = key; Count = count })
        |> Decode.apply (Decode.field "Key" 0 Decode.asString)
        |> Decode.apply (Decode.field "Count" 1 Decode.asInt32)

    let aiDenialToolModulePair: Decoder<AIDenialToolModulePair> =
        Decode.succeed (fun toolName activeModule count -> {
            ToolName = toolName
            ActiveModule = activeModule
            Count = count
        })
        |> Decode.apply (Decode.field "ToolName" 0 Decode.asString)
        |> Decode.apply (Decode.field "ActiveModule" 1 Decode.asString)
        |> Decode.apply (Decode.field "Count" 2 Decode.asInt32)

    let recentAIDenial: Decoder<RecentAIDenial> =
        Decode.succeed (fun toolName activeModule reason occurredAt -> {
            ToolName = toolName
            ActiveModule = activeModule
            Reason = reason
            OccurredAt = occurredAt
        })
        |> Decode.apply (Decode.field "ToolName" 0 Decode.asString)
        |> Decode.apply (Decode.field "ActiveModule" 1 Decode.asString)
        |> Decode.apply (Decode.field "Reason" 2 Decode.asString)
        |> Decode.apply (Decode.field "OccurredAt" 3 Decode.asDateTime)

    let aiDenialRollup: Decoder<AIDenialRollup> =
        Decode.succeed
            (fun generatedAt scopeId windowMinutes allTime inWindow perMinute byTool byModule byScope topPairs recent -> {
                GeneratedAt = generatedAt
                ScopeId = scopeId
                WindowMinutes = windowMinutes
                TotalDenialsAllTime = allTime
                TotalDenialsInWindow = inWindow
                DenialsPerMinute = perMinute
                ByToolName = byTool
                ByActiveModule = byModule
                ByScopeId = byScope
                TopToolModulePairs = topPairs
                RecentDenials = recent
            })
        |> Decode.apply (Decode.field "GeneratedAt" 0 Decode.asDateTime)
        |> Decode.apply (Decode.field "ScopeId" 1 Decode.asString)
        |> Decode.apply (Decode.field "WindowMinutes" 2 Decode.asInt32)
        |> Decode.apply (Decode.field "TotalDenialsAllTime" 3 Decode.asInt32)
        |> Decode.apply (Decode.field "TotalDenialsInWindow" 4 Decode.asInt32)
        |> Decode.apply (Decode.field "DenialsPerMinute" 5 Decode.asFloat)
        |> Decode.apply (Decode.field "ByToolName" 6 (Decode.list aiDenialGroupCount))
        |> Decode.apply (Decode.field "ByActiveModule" 7 (Decode.list aiDenialGroupCount))
        |> Decode.apply (Decode.field "ByScopeId" 8 (Decode.list aiDenialGroupCount))
        |> Decode.apply (Decode.field "TopToolModulePairs" 9 (Decode.list aiDenialToolModulePair))
        |> Decode.apply (Decode.field "RecentDenials" 10 (Decode.list recentAIDenial))

    // ─── IDeploymentVerificationApi ──────────────────────────────────

    /// Five cases, each carrying exactly one string, so the writer emits
    /// `[tag; text]` and the tags are declaration order. An unrecognised
    /// tag refuses rather than falling back — the reflection reader
    /// resolves a tag with `Array.find`, which throws.
    let verificationSectionVerdict: Decoder<VerificationSectionVerdict> =
        Decode.union "VerificationSectionVerdict" (function
            | 0 -> Some(Decode.payload (Decode.asString |> Decode.map VerificationSectionVerdict.NotComposed))
            | 1 -> Some(Decode.payload (Decode.asString |> Decode.map VerificationSectionVerdict.Verified))
            | 2 -> Some(Decode.payload (Decode.asString |> Decode.map VerificationSectionVerdict.Observed))
            | 3 -> Some(Decode.payload (Decode.asString |> Decode.map VerificationSectionVerdict.Failed))
            | 4 -> Some(Decode.payload (Decode.asString |> Decode.map VerificationSectionVerdict.Unreadable))
            | _ -> None)

    /// Four cases, none carrying a field.
    ///
    /// **Still `[tag]`, not a case name.** The writer emits a string enum
    /// only for a union attributed `[<StringEnum>]`
    /// (`Write.makeSerializerAux` tests for that attribute and nothing
    /// else); a field-less union without it goes through `writeUnion`
    /// like any other, as `fixarr 1` plus the tag. Reading this as a
    /// string would refuse every payload the writer emits.
    let deploymentVerificationOutcome: Decoder<DeploymentVerificationOutcome> =
        Decode.union "DeploymentVerificationOutcome" (function
            | 0 -> Some(Decode.case0 DeploymentVerificationOutcome.NothingComposed)
            | 1 -> Some(Decode.case0 DeploymentVerificationOutcome.AllComposedVerified)
            | 2 -> Some(Decode.case0 DeploymentVerificationOutcome.PartiallyVerified)
            | 3 -> Some(Decode.case0 DeploymentVerificationOutcome.FailuresPresent)
            | _ -> None)

    let reportSection: Decoder<ReportSection> =
        Decode.succeed (fun id title verdict findings -> {
            Id = id
            Title = title
            Verdict = verdict
            Findings = findings
        })
        |> Decode.apply (Decode.field "Id" 0 Decode.asString)
        |> Decode.apply (Decode.field "Title" 1 Decode.asString)
        |> Decode.apply (Decode.field "Verdict" 2 verificationSectionVerdict)
        |> Decode.apply (Decode.field "Findings" 3 (Decode.list Decode.asString))

    let notProvedStatement: Decoder<NotProvedStatement> =
        Decode.succeed (fun id statement narrowing -> {
            Id = id
            Statement = statement
            Narrowing = narrowing
        })
        |> Decode.apply (Decode.field "Id" 0 Decode.asString)
        |> Decode.apply (Decode.field "Statement" 1 Decode.asString)
        |> Decode.apply (Decode.field "Narrowing" 2 (Decode.option Decode.asString))

    let deploymentVerificationReport: Decoder<DeploymentVerificationReport> =
        Decode.succeed (fun schemaVersion actor generatedAt sections notProved outcome digest -> {
            SchemaVersion = schemaVersion
            Actor = actor
            GeneratedAt = generatedAt
            Sections = sections
            NotProved = notProved
            Outcome = outcome
            VerdictDigest = digest
        })
        |> Decode.apply (Decode.field "SchemaVersion" 0 Decode.asInt32)
        |> Decode.apply (Decode.field "Actor" 1 Decode.asString)
        |> Decode.apply (Decode.field "GeneratedAt" 2 Decode.asDateTime)
        |> Decode.apply (Decode.field "Sections" 3 (Decode.list reportSection))
        |> Decode.apply (Decode.field "NotProved" 4 (Decode.list notProvedStatement))
        |> Decode.apply (Decode.field "Outcome" 5 deploymentVerificationOutcome)
        |> Decode.apply (Decode.field "VerdictDigest" 6 Decode.asString)

    // ─── Registration ────────────────────────────────────────────────

    /// The wire types this file covers, in the order they are
    /// registered. Public so the Phase 785 facet can name the set
    /// without re-deriving it, and so a test can assert that every name
    /// here is actually registered by `registerAll` — a decoder written
    /// and never registered is the silent failure this list closes.
    let covered: string list = [
        typeof<HealthProbeView>.FullName
        typeof<PreflightOutcomeView>.FullName
        typeof<HealthSnapshot>.FullName
        typeof<PreflightSnapshotView>.FullName
        typeof<JobSchedulerTelemetryView>.FullName
        typeof<DegradedCapability>.FullName
        typeof<AIDenialGroupCount>.FullName
        typeof<AIDenialToolModulePair>.FullName
        typeof<RecentAIDenial>.FullName
        typeof<AIDenialRollup>.FullName
        typeof<VerificationSectionVerdict>.FullName
        typeof<DeploymentVerificationOutcome>.FullName
        typeof<ReportSection>.FullName
        typeof<NotProvedStatement>.FullName
        typeof<DeploymentVerificationReport>.FullName
        typeof<Result<HealthSnapshot, string>>.FullName
        typeof<Result<PreflightSnapshotView, string>>.FullName
        typeof<Result<JobSchedulerTelemetryView, string>>.FullName
        typeof<Result<DegradedCapability list, string>>.FullName
        typeof<Result<AIDenialRollup option, string>>.FullName
        typeof<Result<DeploymentVerificationReport, string>>.FullName
    ]

    /// Register every decoder above.
    ///
    /// **An explicit, idempotent call rather than a module-level `do`.**
    /// A static initialiser fires when the module is first touched,
    /// which under Fable means "when something imports it" and on .NET
    /// means "at a moment nothing states" — so a deployment could not
    /// say whether the algebra path was live, which is precisely what
    /// the Phase 785 facet exists to report. The client's binary
    /// response path calls this itself (the only MsgPack consumer in
    /// the tree); a server composition root that wants the facet to
    /// read `Algebra` calls it once at composition.
    ///
    /// Registering the METHOD RETURN TYPES as well as the records they
    /// carry is what makes the client branch fire at all: the response
    /// serializer is handed the method's return type
    /// (`Result<HealthSnapshot, string>`), not the record inside it.
    let registerAll () : unit =
        RemotingDecoders.register<HealthProbeView> healthProbeView
        RemotingDecoders.register<PreflightOutcomeView> preflightOutcomeView
        RemotingDecoders.register<HealthSnapshot> healthSnapshot
        RemotingDecoders.register<PreflightSnapshotView> preflightSnapshotView
        RemotingDecoders.register<JobSchedulerTelemetryView> jobSchedulerTelemetryView
        RemotingDecoders.register<DegradedCapability> degradedCapability
        RemotingDecoders.register<AIDenialGroupCount> aiDenialGroupCount
        RemotingDecoders.register<AIDenialToolModulePair> aiDenialToolModulePair
        RemotingDecoders.register<RecentAIDenial> recentAIDenial
        RemotingDecoders.register<AIDenialRollup> aiDenialRollup
        RemotingDecoders.register<VerificationSectionVerdict> verificationSectionVerdict
        RemotingDecoders.register<DeploymentVerificationOutcome> deploymentVerificationOutcome
        RemotingDecoders.register<ReportSection> reportSection
        RemotingDecoders.register<NotProvedStatement> notProvedStatement
        RemotingDecoders.register<DeploymentVerificationReport> deploymentVerificationReport

        RemotingDecoders.register<Result<HealthSnapshot, string>> (Decode.result healthSnapshot Decode.asString)

        RemotingDecoders.register<Result<PreflightSnapshotView, string>> (
            Decode.result preflightSnapshotView Decode.asString
        )

        RemotingDecoders.register<Result<JobSchedulerTelemetryView, string>> (
            Decode.result jobSchedulerTelemetryView Decode.asString
        )

        RemotingDecoders.register<Result<DegradedCapability list, string>> (
            Decode.result (Decode.list degradedCapability) Decode.asString
        )

        RemotingDecoders.register<Result<AIDenialRollup option, string>> (
            Decode.result (Decode.option aiDenialRollup) Decode.asString
        )

        RemotingDecoders.register<Result<DeploymentVerificationReport, string>> (
            Decode.result deploymentVerificationReport Decode.asString
        )

    /// The API records this file covers, and the wire types each one
    /// carries. The Phase 785 facet classifies a record `Algebra` when
    /// every type named for it is registered.
    ///
    /// Stated as DATA rather than as prose in the header above because
    /// the facet reads it: a set described only in a comment is a set
    /// the boot check cannot enumerate.
    let coveredApiRecords: (string * string list) list = [
        "IHealthMonitorApi",
        [
            typeof<Result<HealthSnapshot, string>>.FullName
            typeof<Result<PreflightSnapshotView, string>>.FullName
            typeof<Result<JobSchedulerTelemetryView, string>>.FullName
            typeof<Result<DegradedCapability list, string>>.FullName
            typeof<Result<AIDenialRollup option, string>>.FullName
        ]
        "IDeploymentVerificationApi", [ typeof<Result<DeploymentVerificationReport, string>>.FullName ]
    ]