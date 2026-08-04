// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 515 — IContentScanner ────────────────────────────────────────
//
// A scanning hook at the **upload boundary**, so a regulated deployment
// can have uploaded bytes inspected for malware / disallowed content
// INSIDE the platform, with the verdict landing in the platform's own
// audit trail (GP 6). Without this seam the only place to put a scanner
// is outside — in front of the ingress, or as a sweep over the blob
// container afterwards — and both lose the linkage that makes a refusal
// investigable: which subject uploaded which file, under which scope,
// and what the scanner said about it.
//
// **Deliberately not `IUploadValidator`.** The AssetStore seam (Phase
// 186) cross-checks a declared MIME type against the bytes; it is pure,
// synchronous in spirit, and says so in its own doc comment — *"It is
// deliberately NOT a malware scanner and makes no claim to be one. A
// scanner is a network call to a vendor backend."* That is exactly this
// seam: a network round-trip to a scanning daemon, with an outcome that
// is not merely accept/reject but a THIRD state — "I could not tell you"
// — which a validator has no way to express and which a deployment must
// be able to policy over. Hence a distinct interface, in the shared
// floor, reachable from every upload path rather than one of them.
//
// **No-op default (GP 11 / GP 13).** `AllowAllContentScanner` returns
// `ScanClean` without allocating beyond the returned `Async`. A
// deployment that composes no scanner runs the pre-515 upload path
// byte-for-byte: no scan, no audit row, no policy read.
//
// **Six portability rules (GP 12).** Identity by value (a `byte[]` and a
// file name in, a data verdict out — no handles); async at the boundary;
// no retry/callback semantics leaked (a scanner that retries its backend
// owns that internally); stateless between `Scan` calls, so a scanner
// may be a DI singleton shared across requests; no ordering promise
// across calls; no timing-precision contract.
//
// **PII envelope.** The interface takes the bytes because that is what a
// scanner scans; it returns a *reason string*, never content. Callers
// audit the file name and a digest — never the payload — exactly as the
// other KB events do.

/// Outcome of scanning one uploaded payload.
///
/// Three cases, not two. The `ScanUnavailable` arm is the load-bearing
/// one: a scanner backend that is down, unreachable, or that failed
/// mid-stream has told you nothing about the bytes, and collapsing that
/// into either "clean" or "rejected" is a decision the SEAM must not
/// make on the deployment's behalf — a hospital and a hobby project want
/// opposite answers. `ContentScanPolicy.OnScanError` is where the
/// deployment states which it is.
///
/// Case names are prefixed rather than the bare `Clean` / `Rejected` /
/// `Error` the phase sketched: this type lives in the `ToolUp.Platform`
/// namespace that nearly every server file opens, and an unprefixed
/// `Error` case would shadow `Result.Error` at every pattern-match site
/// downstream of the `open`.
type ScanVerdict =
    /// The scanner inspected the payload and found nothing.
    | ScanClean
    /// The scanner identified the payload as malware or otherwise
    /// disallowed. The reason is a short operator-facing string (a
    /// signature name, a rule id) safe to put in an audit row and in a
    /// refusal shown to the uploader — never payload content.
    | ScanRejected of reason: string
    /// The scanner could not reach a verdict — backend unreachable,
    /// protocol error, timeout. Says nothing about the bytes.
    | ScanUnavailable of reason: string

[<RequireQualifiedAccess>]
module ScanVerdict =

    /// Stable lowercase label for audit rows, log lines and metrics.
    let label (verdict: ScanVerdict) : string =
        match verdict with
        | ScanClean -> "clean"
        | ScanRejected _ -> "rejected"
        | ScanUnavailable _ -> "unavailable"

    /// The reason carried by a non-clean verdict; `None` for `ScanClean`.
    let reason (verdict: ScanVerdict) : string option =
        match verdict with
        | ScanClean -> None
        | ScanRejected r
        | ScanUnavailable r -> Some r

/// What an upload boundary does when the scanner returns
/// `ScanUnavailable`.
type ScanErrorPolicy =
    /// Admit the upload. The deployment has decided that availability
    /// beats certainty — appropriate where the scanner is defence in
    /// depth behind another control.
    | FailOpenOnScanError
    /// Refuse the upload. The deployment has decided that an unscanned
    /// byte must not enter the corpus.
    | FailClosedOnScanError

/// Deployment policy for the content-scanning seam.
type ContentScanPolicy = {
    /// Applied to `ScanUnavailable` only. A `ScanRejected` verdict is
    /// always a refusal — no policy softens it — and `ScanClean` is
    /// always an admission.
    OnScanError: ScanErrorPolicy
}

[<RequireQualifiedAccess>]
module ContentScanPolicy =

    /// Refuse on an unreachable scanner.
    let failClosed: ContentScanPolicy = { OnScanError = FailClosedOnScanError }

    /// Admit on an unreachable scanner.
    let failOpen: ContentScanPolicy = { OnScanError = FailOpenOnScanError }

    /// **Fail closed.** A deployment that went to the trouble of
    /// composing a scanner has said unscanned content is unacceptable;
    /// silently admitting it the moment the backend blips would make the
    /// control theatre. This default costs nothing on a deployment that
    /// composed no scanner, because `AllowAllContentScanner` never
    /// returns `ScanUnavailable` (GP 11).
    let defaults: ContentScanPolicy = failClosed

/// Scans uploaded bytes before the platform persists them.
///
/// Implementations are DI singletons and MUST be safe to call
/// concurrently from many request threads (GP 12 rule 4 — no state
/// between calls).
type IContentScanner =
    /// Stable identifier for log lines, audit rows and the
    /// `/dev/inspect` panel — e.g. `"allow-all"`, `"clamav"`.
    abstract Name: string

    /// Inspect `bytes`, uploaded under `fileName`. The file name is
    /// advisory context for scanners that key on extension; a scanner
    /// must never trust it as a substitute for inspecting the payload.
    ///
    /// Implementations do not raise: a backend failure is reported as
    /// `ScanUnavailable`, so the caller's policy decides, not an
    /// exception unwinding through the upload path.
    abstract Scan: bytes: byte[] * fileName: string -> Async<ScanVerdict>

/// The no-op default (GP 11 / GP 13): admits everything, touches no
/// network, allocates nothing beyond the returned `Async`. This is what
/// an upload path resolves when the deployment composed no scanner, so
/// the pre-515 behaviour is preserved exactly rather than approximately.
type AllowAllContentScanner() =
    interface IContentScanner with
        member _.Name = "allow-all"
        member _.Scan(_, _) = async { return ScanClean }

[<RequireQualifiedAccess>]
module ContentScan =

    /// The shared "should this upload be refused, and why" decision,
    /// factored here so every upload boundary (KnowledgeBase today,
    /// AssetStore next) reaches the same answer from the same verdict
    /// rather than each re-deriving the fail-open/closed rule.
    ///
    /// Returns the verdict as well as the refusal, because the CALLER
    /// audits: a clean scan is an audit row too (GP 6 — the trail
    /// records what the scanner said, not only when it said no), and the
    /// caller is the only layer that knows the subject and scope.
    let evaluate
        (scanner: IContentScanner)
        (policy: ContentScanPolicy)
        (bytes: byte[])
        (fileName: string)
        : Async<ScanVerdict * string option> =
        async {
            let! verdict = scanner.Scan(bytes, fileName)

            let refusal =
                match verdict with
                | ScanClean -> None
                | ScanRejected reason -> Some(sprintf "content scan rejected the upload — %s" reason)
                | ScanUnavailable reason ->
                    match policy.OnScanError with
                    | FailOpenOnScanError -> None
                    | FailClosedOnScanError ->
                        Some(sprintf "content scan could not complete and this deployment fails closed — %s" reason)

            return verdict, refusal
        }