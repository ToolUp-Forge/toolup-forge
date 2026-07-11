module ToolUp.Platform.InternetReadinessScorecard

open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ConfigValidatorAggregator

// ─── Internet-readiness secure-default scorecard ─────────────────────
//
// One consolidated, read-side answer to the single operator question the
// secure-by-default validators leave unanswered individually: "is this
// deployment safe to expose to the internet?". The request-edge hardening
// work registered ten-plus independent `IConfigValidator`s (CSRF, security
// headers, CORS, forwarded-headers trust, dev-admin, SSE auth, share-token
// signing-key provenance, OAuth secret encryption + state store, request
// body cap, rate limiting, …); an operator reading the preflight log had to
// stitch those ten outcomes together by hand. This scorecard consolidates
// them into a single graded report.
//
// **Pure read-side projection (GP 11 / GP 13).** The scorecard runs entirely
// off the already-computed aggregated `ValidatorOutcome` list produced by the
// config-preflight aggregator. It introduces NO new validation logic and NO
// new failure semantics: a control's status is a projection of the outcome
// its own validator already reported (Ok → pass, Warning → warn, Error →
// fail). A control whose validator did not run in this deployment is
// `NotAssessed` — never a fabricated pass. A deployment that never asks for
// the scorecard pays nothing and is byte-for-byte unchanged.
//
// **Mirror of the Phase 177 deployment-readiness scorecard** (which
// consolidates the four operability signals — preflight / smoke / drift /
// health — into a go/no-go verdict). This is its security-lens twin, scoped
// to the request-edge secure-by-default control set and graded from the
// preflight outcomes alone.

/// The internet-readiness lens a control speaks to. The four categories are
/// the request-exposure surfaces the secure-by-default controls guard.
///   * `Edge`      — request-edge browser controls (CSRF, security headers, CORS).
///   * `Transport` — proxy-trust + redirect-base topology (forwarded headers,
///                   public base URL).
///   * `Auth`      — identity / secret / session controls (dev-admin bootstrap,
///                   SSE auth, share-token + OAuth secret provenance, secret
///                   store encryption).
///   * `Limits`    — abuse / DoS surface (request-body cap, rate limiting).
[<RequireQualifiedAccess>]
type ReadinessCategory =
    | Edge
    | Auth
    | Transport
    | Limits

module ReadinessCategory =
    /// Stable lowercase label — used in the rendered scorecard and any
    /// diagnostics surface. External monitors may key off these strings.
    let label =
        function
        | ReadinessCategory.Edge -> "edge"
        | ReadinessCategory.Auth -> "auth"
        | ReadinessCategory.Transport -> "transport"
        | ReadinessCategory.Limits -> "limits"

/// Per-control status — a projection of the underlying validator's
/// three-valued `ValidationResult`, plus `NotAssessed` for a catalog control
/// whose validator did not run in this deployment (its substrate is absent,
/// so there is no signal to read — never a fabricated pass).
[<RequireQualifiedAccess>]
type ControlStatus =
    | Pass
    | Warn
    | Fail
    | NotAssessed

/// Overall internet-readiness grade. Derived from the assessed controls'
/// worst status — the scorecard never grades stricter than the validators.
///   * `Ready`             — ≥1 control assessed and every assessed control passes.
///   * `ReadyWithWarnings` — no hard failure, but at least one control warns
///                           (OR nothing was assessed — an empty scorecard
///                           cannot attest full readiness).
///   * `NotReady`          — at least one assessed control failed (its
///                           validator returned `Error`).
[<RequireQualifiedAccess>]
type ReadinessGrade =
    | Ready
    | ReadyWithWarnings
    | NotReady

/// A catalog entry: the stable `.Name` of a secure-by-default
/// `IConfigValidator`, tagged with its internet-readiness category and a
/// weight (3 = critical, 2 = high, 1 = advisory). The weight feeds only the
/// informational `Score`; the grade is worst-status, not weighted.
type ControlDescriptor = {
    Name: string
    Category: ReadinessCategory
    Weight: int
}

/// One control's assessed line in the report. `Detail` carries the
/// underlying validator's message verbatim for a `Warn` / `Fail` (empty for
/// `Pass` / `NotAssessed`) — the scorecard adds no wording of its own.
type ControlAssessment = {
    Name: string
    Category: ReadinessCategory
    Weight: int
    Status: ControlStatus
    Detail: string
}

/// The consolidated internet-readiness report. `Failing` / `Warnings` /
/// `NotAssessed` name the controls in each bucket so a downgraded grade
/// always identifies its offenders. `Score` is an informational weighted
/// pass ratio (0–100) over the assessed controls.
type InternetReadinessReport = {
    Grade: ReadinessGrade
    Controls: ControlAssessment list
    Failing: string list
    Warnings: string list
    NotAssessed: string list
    Score: int
}

/// The internet-readiness control catalog — the secure-by-default
/// `IConfigValidator`s registered by the first-party compose set, each keyed
/// by its stable `.Name` and tagged with a category + weight. This is the
/// enumeration the scorecard reads: adding a control here is the only way the
/// scorecard grows its coverage, so it stays an explicit, reviewable list
/// rather than an opaque scan. Names are asserted to exist against the live
/// preflight set by the test pack.
let catalog: ControlDescriptor list = [
    // ── edge — request-edge browser controls (CSRF / headers / CORS) ──
    {
        Name = "csrf-default-mode"
        Category = ReadinessCategory.Edge
        Weight = 3
    }
    {
        Name = "csrf-hardening-split-origin"
        Category = ReadinessCategory.Edge
        Weight = 1
    }
    {
        Name = "security-headers-mode"
        Category = ReadinessCategory.Edge
        Weight = 2
    }
    {
        Name = "cors-config"
        Category = ReadinessCategory.Edge
        Weight = 3
    }
    // ── transport — proxy-trust + redirect-base topology ──
    {
        Name = "forwarded-headers-trust"
        Category = ReadinessCategory.Transport
        Weight = 3
    }
    {
        Name = "public-base-url-format"
        Category = ReadinessCategory.Transport
        Weight = 1
    }
    // ── auth — identity / secret / session controls ──
    {
        Name = "auto-bootstrap-dev-admin-mode"
        Category = ReadinessCategory.Auth
        Weight = 3
    }
    {
        Name = "sse-auth-mode"
        Category = ReadinessCategory.Auth
        Weight = 3
    }
    {
        Name = "share-token-signing-key-provenance"
        Category = ReadinessCategory.Auth
        Weight = 2
    }
    {
        Name = "oauth-secret-encryption-mode"
        Category = ReadinessCategory.Auth
        Weight = 3
    }
    {
        Name = "oauth-state-store-instance"
        Category = ReadinessCategory.Auth
        Weight = 2
    }
    {
        Name = "encrypted-secret-store-mode"
        Category = ReadinessCategory.Auth
        Weight = 3
    }
    // ── limits — abuse / DoS surface (request-body cap + rate limiting) ──
    {
        Name = "max-request-body-bytes"
        Category = ReadinessCategory.Limits
        Weight = 2
    }
    {
        Name = "rate-limit-mode"
        Category = ReadinessCategory.Limits
        Weight = 2
    }
]

/// Project a validator's three-valued result onto a control status.
let private statusOfResult =
    function
    | Ok -> ControlStatus.Pass
    | Warning _ -> ControlStatus.Warn
    | Error _ -> ControlStatus.Fail

/// Weighted informational pass ratio (0–100) over the assessed controls.
/// `NotAssessed` controls are excluded from both numerator and denominator
/// (no signal to weigh). A warn scores half its weight; a fail scores zero.
/// With nothing assessed the ratio is undefined — reported as 0.
let private scoreOf (controls: ControlAssessment list) : int =
    let assessed =
        controls |> List.filter (fun c -> c.Status <> ControlStatus.NotAssessed)

    let possible = assessed |> List.sumBy _.Weight

    if possible = 0 then
        0
    else
        let earned =
            assessed
            |> List.sumBy (fun c ->
                match c.Status with
                | ControlStatus.Pass -> float c.Weight
                | ControlStatus.Warn -> 0.5 * float c.Weight
                | ControlStatus.Fail
                | ControlStatus.NotAssessed -> 0.0)

        int (System.Math.Round(100.0 * earned / float possible))

/// Grade the assessed controls. Deterministic, side-effect-free:
///   * any `Fail`                       ⇒ `NotReady`;
///   * else any `Warn`                  ⇒ `ReadyWithWarnings`;
///   * else (≥1 assessed, none warn/fail) ⇒ `Ready`;
///   * else (nothing assessed)          ⇒ `ReadyWithWarnings` — an empty
///     scorecard cannot attest readiness (mirrors the Phase 177 verdict).
let gradeOf (statuses: ControlStatus list) : ReadinessGrade =
    let anyAssessed = statuses |> List.exists (fun s -> s <> ControlStatus.NotAssessed)

    if List.contains ControlStatus.Fail statuses then
        ReadinessGrade.NotReady
    elif List.contains ControlStatus.Warn statuses then
        ReadinessGrade.ReadyWithWarnings
    elif anyAssessed then
        ReadinessGrade.Ready
    else
        ReadinessGrade.ReadyWithWarnings

/// Assess a control catalog against an aggregated preflight run. **Pure** —
/// no I/O, no new validation. Each catalog control is matched by `.Name`
/// against the `ValidatorOutcome` list; an unmatched control is
/// `NotAssessed` (its validator did not run in this deployment). The report
/// reflects exactly what the underlying validators reported and invents no
/// failure of its own.
let assess (descriptors: ControlDescriptor list) (outcomes: ValidatorOutcome list) : InternetReadinessReport =
    let byName = outcomes |> List.map (fun o -> o.Name, o.Result) |> Map.ofList

    let controls =
        descriptors
        |> List.map (fun d ->
            match Map.tryFind d.Name byName with
            | Some result -> {
                Name = d.Name
                Category = d.Category
                Weight = d.Weight
                Status = statusOfResult result
                Detail = ValidationResult.message result
              }
            | None -> {
                Name = d.Name
                Category = d.Category
                Weight = d.Weight
                Status = ControlStatus.NotAssessed
                Detail = ""
              })

    let inBucket status =
        controls |> List.filter (fun c -> c.Status = status) |> List.map _.Name

    {
        Grade = gradeOf (controls |> List.map _.Status)
        Controls = controls
        Failing = inBucket ControlStatus.Fail
        Warnings = inBucket ControlStatus.Warn
        NotAssessed = inBucket ControlStatus.NotAssessed
        Score = scoreOf controls
    }

/// Assess against the built-in secure-by-default catalog.
let ofOutcomes (outcomes: ValidatorOutcome list) : InternetReadinessReport = assess catalog outcomes

/// Human-readable multi-line rendering for the startup log / a diagnostics
/// surface. Contains no data a caller could not derive from the report.
let render (report: InternetReadinessReport) : string =
    let sb = System.Text.StringBuilder()

    let gradeLabel =
        match report.Grade with
        | ReadinessGrade.Ready -> "READY"
        | ReadinessGrade.ReadyWithWarnings -> "READY (with warnings)"
        | ReadinessGrade.NotReady -> "NOT READY"

    sb.AppendLine(
        sprintf
            "Internet-readiness scorecard — %s (score %d/100, %d controls)"
            gradeLabel
            report.Score
            report.Controls.Length
    )
    |> ignore

    for c in report.Controls do
        let statusLabel =
            match c.Status with
            | ControlStatus.Pass -> "pass"
            | ControlStatus.Warn -> "warn"
            | ControlStatus.Fail -> "fail"
            | ControlStatus.NotAssessed -> "n/a "

        sb.AppendLine(sprintf "  [%s] %-10s %s" statusLabel (ReadinessCategory.label c.Category) c.Name)
        |> ignore

    sb.ToString().TrimEnd()

/// Build the scorecard from an aggregated preflight run and emit it to the
/// logger at a level matching the grade (Info = Ready, Warn otherwise).
/// **Advisory only** — this never aborts startup and reports no failure the
/// underlying `IConfigValidator`s did not already surface (a hard `Error`
/// would have aborted preflight before this ran). Returns the report for a
/// caller that also wants to surface it on a diagnostics API. Call after
/// preflight completes, passing `IPreflightSnapshot.LastRun`.
let logScorecard (logger: ILogger) (outcomes: ValidatorOutcome list) : InternetReadinessReport =
    let report = ofOutcomes outcomes
    let text = render report

    match report.Grade with
    | ReadinessGrade.Ready -> logger.Info text
    | ReadinessGrade.ReadyWithWarnings
    | ReadinessGrade.NotReady -> logger.Warn text

    report

/// Opt-in startup emission — the "behind a compose option" seam. Logs the
/// scorecard only when `enabled`; when `enabled = false` this is a no-op that
/// reads no outcomes and touches no logger, so a deployment that never opts
/// in is byte-for-byte unchanged (GP 11 / GP 13). Wire at the end of compose:
/// `InternetReadinessScorecard.logIfEnabled cfg.EmitInternetReadinessScorecard logger snapshot.LastRun`.
let logIfEnabled (enabled: bool) (logger: ILogger) (outcomes: ValidatorOutcome list) : InternetReadinessReport option =
    if enabled then Some(logScorecard logger outcomes) else None