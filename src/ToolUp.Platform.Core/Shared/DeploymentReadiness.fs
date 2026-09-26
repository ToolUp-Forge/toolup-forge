// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 177 — Deployment-readiness scorecard ──────────────────────
//
// One operator-facing "is this deployment operationally ready?" read
// that consolidates the four already-shipped operability signals into a
// single verdict, instead of the operator stitching four independent
// admin surfaces together:
//
//   * Preflight   — `IConfigValidator` outcomes (Phase 9m).
//   * SmokeTests  — `ISmokeTest` results        (Phase 9o).
//   * Drift       — `ConfigDrift` finding       (Phase 9q).
//   * Health      — `IHealthCheck` aggregate    (Phase 9k).
//
// **Pure projection (GP 13).** This file declares only the wire-shape
// records + the pure `summarise` verdict aggregation. No new gate, no
// new control-plane behaviour, no I/O — the server-side
// `DeploymentReadinessReport` gathers the four signals and calls
// `summarise`. Kept in Shared (GP 10) so a future Fable admin panel
// renders the same shape without re-deriving the wire format.
//
// **Naming.** The four sub-summary records live inside the
// `DeploymentReadiness` module (qualified access) because the bare
// names `PreflightSummary` / `DriftSummary` / `HealthSummary` already
// exist at `ToolUp.Platform` scope as the Phase 9p.A ServiceStatusBoard
// section abbreviations. The scorecard is a distinct, narrower surface
// (a single go/no-go verdict, not the six-section board), so it carries
// its own record shapes rather than reusing `SectionSummary`.

/// Per-source roll-up feeding the verdict. `NotComposed` means the
/// source's substrate is not wired in this deployment — there is no
/// signal to read (never a fabricated pass). `Clean` / `Warned` /
/// `Failed` describe a wired source's worst observed state:
///   * `Failed`  — a hard go/no-go failure (preflight `Error`, a failed
///                 smoke test, an `Unhealthy` probe).
///   * `Warned`  — a soft signal (preflight `Warning`, drift detected, a
///                 `Degraded` probe) with no hard failure.
///   * `Clean`   — wired and all-green.
[<RequireQualifiedAccess>]
type SourceStatus =
    | NotComposed
    | Clean
    | Warned
    | Failed

/// Top-line go/no-go verdict.
///   * `Ready`         — every wired readiness signal is green and at
///                       least one signal is composed.
///   * `DegradedReady` — ready to serve, but with caveats: a soft signal
///                       fired (warning / drift / degraded probe), OR no
///                       signal at all is composed (an empty scorecard
///                       cannot attest full readiness — confidence is
///                       degraded, not green).
///   * `NotReady`      — at least one hard go/no-go failure; do not route
///                       production traffic.
[<RequireQualifiedAccess>]
type ReadinessVerdict =
    | Ready
    | DegradedReady
    | NotReady

module DeploymentReadiness =

    /// `IConfigValidator` preflight roll-up. `Errors` / `Warnings` carry
    /// the offending validator names so a `NotReady` / `DegradedReady`
    /// verdict can name the failing item(s). `Total` is the count of
    /// validators in the last preflight run.
    type PreflightSummary = {
        Status: SourceStatus
        Total: int
        Errors: string list
        Warnings: string list
    }

    /// `ISmokeTest` roll-up. Smoke tests are two-valued (`Pass` / `Fail`)
    /// — there is no soft state — so the worst status is `Failed` or
    /// `Clean`. `Failures` names the failing probes.
    type SmokeSummary = {
        Status: SourceStatus
        Total: int
        Failures: string list
    }

    /// `ConfigDrift` roll-up. Drift is never a hard failure — a drifted
    /// deployment can still serve — so a detected drift maps to `Warned`.
    /// `DriftEventCount` is the number of `ConfigDrift` audit rows in the
    /// lookback window.
    type DriftSummary = {
        Status: SourceStatus
        DriftEventCount: int
    }

    /// `IHealthCheck` roll-up. `Unhealthy` / `Degraded` carry the
    /// offending probe names. `Total` is the count of registered probes.
    type HealthSummary = {
        Status: SourceStatus
        Total: int
        Unhealthy: string list
        Degraded: string list
    }

    /// Phase 541 — the deployment's live-interface posture: whether any
    /// loaded AI tool reads or drives the user's on-screen module state
    /// (a tool declaring `IsLiveInterface`, or a `ClientResident` one —
    /// the Phase 538 predicate), and whether Phase 14r's tool-aware RAG
    /// framing is consequently active.
    ///
    /// **Informational only — never part of the verdict (GP 13).** A
    /// deployment with no live-interface tools is not less ready; it
    /// simply answers "what is on my screen?" from the knowledge base.
    /// The line exists so an operator can read "UI-awareness: on/off"
    /// instead of discovering it by reading source — in particular the
    /// orphaned state where the framing stance that WOULD add the
    /// companion is chosen but no tool exists for it to point at.
    ///
    /// `Composed = false` means nothing on this deployment reported a
    /// posture (the tool-aware framing is a RAG composition concern, so
    /// a deployment without RAG has none to report) — never a fabricated
    /// `false`.
    type LiveInterfaceSummary = {
        /// Whether a composition surface reported the posture at all.
        Composed: bool
        /// True when at least one loaded AI tool is a live-interface tool.
        HasLiveInterfaceTools: bool
        /// The names of the live-interface tools, sorted and distinct.
        LiveInterfaceTools: string list
        /// The RAG grounding stance, as its case name (`Permissive` /
        /// `Preferred` / `StrictlyGrounded`); empty when not composed.
        GroundingMode: string
        /// True when the tool-aware framing companion is actually added
        /// to the system prompt: live-interface tools are loaded AND the
        /// grounding stance is one that carries it.
        ToolAwareFramingActive: bool
        /// A plain-language note when the posture is lopsided — framing
        /// eligible but no tool to point at, or tools loaded under a
        /// stance that adds no framing. `None` when there is nothing to
        /// say.
        Note: string option
    }

    /// The consolidated scorecard. Each sub-summary is independently
    /// `NotComposed` when its substrate isn't wired, so a deployment that
    /// composes a subset (or none) of the signals gets an honest report
    /// rather than a fabricated pass or an error (GP 13).
    type DeploymentReadinessReport = {
        Verdict: ReadinessVerdict
        Preflight: PreflightSummary
        SmokeTests: SmokeSummary
        Drift: DriftSummary
        Health: HealthSummary
        GeneratedAt: DateTime
        /// Phase 541 — informational live-interface posture. Deliberately
        /// excluded from `Verdict`.
        LiveInterface: LiveInterfaceSummary
    }

    /// `NotComposed` sub-summaries — the gatherer uses these when a
    /// source's substrate is absent, and the truth-table tests use them
    /// to assert a missing signal never inflates the verdict to `Ready`.
    module PreflightSummary =
        let notComposed: PreflightSummary = {
            Status = SourceStatus.NotComposed
            Total = 0
            Errors = []
            Warnings = []
        }

    module SmokeSummary =
        let notComposed: SmokeSummary = {
            Status = SourceStatus.NotComposed
            Total = 0
            Failures = []
        }

    module DriftSummary =
        let notComposed: DriftSummary = {
            Status = SourceStatus.NotComposed
            DriftEventCount = 0
        }

    module HealthSummary =
        let notComposed: HealthSummary = {
            Status = SourceStatus.NotComposed
            Total = 0
            Unhealthy = []
            Degraded = []
        }

    module LiveInterfaceSummary =
        /// Nothing reported a live-interface posture.
        let notComposed: LiveInterfaceSummary = {
            Composed = false
            HasLiveInterfaceTools = false
            LiveInterfaceTools = []
            GroundingMode = ""
            ToolAwareFramingActive = false
            Note = None
        }

        /// The note for a stance that WOULD carry the tool-aware framing
        /// but has no live-interface tool for it to point at.
        [<Literal>]
        let FramingInertNote =
            "Tool-aware RAG framing is inert: the grounding stance would add the live-interface companion, but no live-interface tool is loaded, so questions about the user's current screen are answered from the knowledge base only. Load a tool that declares IsLiveInterface (or a ClientResident tool) to enable UI inspection."

        /// The note for live-interface tools loaded under a stance that
        /// adds no tool-aware framing.
        let framingNotCarriedNote (groundingMode: string) : string =
            sprintf
                "Live-interface tools are loaded, but grounding mode %s adds no tool-aware framing: only Preferred carries the live-interface companion, so the model is not told to inspect the user's screen before answering from the knowledge base."
                groundingMode

        /// Pure derivation. `liveInterfaceTools` are the names of the
        /// loaded live-interface tools; `framingEligible` is whether the
        /// grounding stance is one that carries the tool-aware companion
        /// (the caller owns that rule — it is a RAG composition fact).
        let derive
            (liveInterfaceTools: string list)
            (groundingMode: string)
            (framingEligible: bool)
            : LiveInterfaceSummary =
            let tools = liveInterfaceTools |> List.distinct |> List.sort
            let hasTools = not tools.IsEmpty

            {
                Composed = true
                HasLiveInterfaceTools = hasTools
                LiveInterfaceTools = tools
                GroundingMode = groundingMode
                ToolAwareFramingActive = hasTools && framingEligible
                Note =
                    match hasTools, framingEligible with
                    | false, true -> Some FramingInertNote
                    | true, false -> Some(framingNotCarriedNote groundingMode)
                    | _ -> None
            }

        /// A one-line operator rendering — "UI-awareness: on/off" plus
        /// the framing state and any note.
        let render (summary: LiveInterfaceSummary) : string =
            if not summary.Composed then
                "UI-awareness: not reported (no RAG composition)"
            else
                let head =
                    if summary.HasLiveInterfaceTools then
                        sprintf
                            "UI-awareness: on (%d live-interface tool(s): %s); tool-aware framing %s"
                            summary.LiveInterfaceTools.Length
                            (String.concat ", " summary.LiveInterfaceTools)
                            (if summary.ToolAwareFramingActive then
                                 "active"
                             else
                                 "inactive")
                    else
                        "UI-awareness: off (no live-interface tools); tool-aware framing inactive"

                match summary.Note with
                | Some note -> head + " — " + note
                | None -> head

    /// Pure verdict aggregation over the four source statuses.
    /// Deterministic, side-effect-free, unit-testable in isolation:
    ///   * any `Failed`            ⇒ `NotReady`;
    ///   * else any `Warned`       ⇒ `DegradedReady`;
    ///   * else any `Clean`        ⇒ `Ready` (≥1 wired-and-green source,
    ///                               none failed / warned — remaining
    ///                               `NotComposed` sources do not block);
    ///   * else (all `NotComposed`) ⇒ `DegradedReady` — an empty scorecard
    ///                               cannot attest readiness, and a
    ///                               `NotComposed` source must never
    ///                               inflate the verdict to `Ready`.
    let verdictOf (statuses: SourceStatus list) : ReadinessVerdict =
        if List.contains SourceStatus.Failed statuses then
            ReadinessVerdict.NotReady
        elif List.contains SourceStatus.Warned statuses then
            ReadinessVerdict.DegradedReady
        elif List.contains SourceStatus.Clean statuses then
            ReadinessVerdict.Ready
        else
            ReadinessVerdict.DegradedReady

    /// Assemble the report from the four gathered sub-summaries. `summarise`
    /// owns only the verdict roll-up; gathering the signals (DI reads,
    /// running probes) is the server-side `DeploymentReadinessReport`'s job.
    let summarise
        (generatedAt: DateTime)
        (preflight: PreflightSummary)
        (smoke: SmokeSummary)
        (drift: DriftSummary)
        (health: HealthSummary)
        : DeploymentReadinessReport =
        let verdict =
            verdictOf [ preflight.Status; smoke.Status; drift.Status; health.Status ]

        {
            Verdict = verdict
            Preflight = preflight
            SmokeTests = smoke
            Drift = drift
            Health = health
            GeneratedAt = generatedAt
            LiveInterface = LiveInterfaceSummary.notComposed
        }

    /// Phase 541 — attach the informational live-interface posture. Never
    /// touches `Verdict`: the posture is not a readiness signal.
    let withLiveInterface
        (liveInterface: LiveInterfaceSummary)
        (report: DeploymentReadinessReport)
        : DeploymentReadinessReport =
        {
            report with
                LiveInterface = liveInterface
        }

/// Platform-Admin-gated read-only ToolUp.Remoting surface returning the
/// consolidated scorecard. Mirrors `IHealthMonitorApi` /
/// `IServiceStatusBoardApi`: Anonymous-mode and non-admin callers both
/// receive `Error` (deployment-wide dependency state is a reconnaissance
/// gift to surface to every visitor); `Result<_, string>` is the
/// established ToolUp.Remoting failure shape. Deployment-wide, never
/// per-tenant (GP 4) — the read carries no tenant-scoped data.
///
/// Mounted only when `ServerConfig.DeploymentReadiness =
/// EnabledReadinessReport` (default `NoReadinessReport`, GP 11/13) — an
/// unopted deployment 404s the route and pays nothing.
type IDeploymentReadinessApi = {
    [<RequiresRole "PlatformAdmin">]
    GetReadinessReport: unit -> Async<Result<DeploymentReadiness.DeploymentReadinessReport, string>>
}