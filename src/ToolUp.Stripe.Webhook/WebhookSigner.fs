namespace ToolUp.Stripe.Webhook

open System
open System.Security.Cryptography
open System.Text

/// Stripe webhook signature verification.
///
/// Algorithm (per https://docs.stripe.com/webhooks/signatures):
///   1. Parse the `Stripe-Signature` header — comma-separated
///      `key=value` pairs; require both `t=<unix>` and `v1=<hex>`.
///   2. Recompute `HMAC-SHA256(timestamp + "." + body, secret)`,
///      hex-encoded lower-case.
///   3. Constant-time compare against the `v1=` value.
///   4. Reject when `|now - timestamp| > 300` (5-minute drift window).
///
/// `verify` consumes `DateTimeOffset.UtcNow` internally; tests use
/// `verifyWith` to inject a fixed clock.
///
/// ## Secret rotation (Phase 464)
///
/// Two shapes, and the choice decides whether a rotation needs a restart:
///
///  * **`verify` / `verifyWith` take the secret by value.** A caller that
///    resolves the secret once at compose and closes over it — which is
///    what `StripeConfig.WebhookSecret` is — keeps verifying against that
///    value for the life of the process. **Rotating the signing secret in
///    the Stripe dashboard therefore requires a restart**, and until it
///    happens every genuine inbound event signed with the new secret is
///    rejected as `SignatureMismatch`: an authenticity failure, not a
///    configuration warning, so the alert names the wrong cause.
///  * **`verifyWithFetcher` / `verifyWithFetcherAt` resolve per call.**
///    Pass a thunk over `ISecretStore.GetSecret` and the next request
///    after the store is updated verifies against the rotated secret. No
///    restart, no redeploy. This is the shape the platform's own
///    credential-rotation rule expects of a secret-bearing component.
///
/// This module holds **no cache** in either shape — deliberately. A cache
/// here would reintroduce the staleness the fetcher exists to remove, in
/// a place no caller could reach to invalidate; a caller fronting a remote
/// vault composes its own short-TTL cache behind the thunk, where it owns
/// the invalidation. (The platform-side counterpart is the webhook
/// registry's cross-instance rotation broadcast, which invalidates a
/// caching `ISecretStore` on every instance rather than caching here.)
module WebhookSigner =
    /// Stripe's freshness window — 5 minutes.
    [<Literal>]
    let private MaxDriftSeconds = 300L

    /// Minimum signing-secret length, in bytes of its UTF-8 encoding. An
    /// HMAC-SHA256 key below the 32-byte SHA-256 block offers less than the
    /// full keyspace, and an EMPTY key is publicly computable — so a blank
    /// `WebhookSecret` (the unset-env-var case) would let anyone forge a
    /// "verified" event. Every real Stripe `whsec_…` secret clears this
    /// floor. Mirrored — not shared — with the identical guard in
    /// `ToolUp.Stripe.TierToken.Token` and the peer auth provider; the three
    /// signing packages are deliberately decoupled (same rationale as the
    /// constant-time compare duplicated across them).
    [<Literal>]
    let private MinSecretBytes = 32

    /// `true` when the secret is present and at least `MinSecretBytes`
    /// bytes — the fail-closed strength gate applied before any HMAC is
    /// computed (GP 4). A well-formed secret is unchanged (GP 11).
    let private secretIsStrong (secret: string) : bool =
        not (String.IsNullOrEmpty secret)
        && Encoding.UTF8.GetByteCount secret >= MinSecretBytes

    /// Constant-time byte comparison so an attacker can't time-side-
    /// channel the secret bit-by-bit.
    let private constantTimeEquals (a: byte[]) (b: byte[]) : bool =
        if a.Length <> b.Length then
            false
        else
            let mutable diff = 0

            for i in 0 .. a.Length - 1 do
                diff <- diff ||| (int a[i] ^^^ int b[i])

            diff = 0

    /// Parse the `Stripe-Signature` header into `(timestamp, v1Hex)`.
    /// Returns `None` when either `t=` or `v1=` is missing or the
    /// `t=` value didn't parse as an integer.
    let private parseHeader (header: string) : (int64 * string) option =
        if header.Length = 0 then
            None
        else
            let parts = header.Split(',')

            let tryFind (prefix: string) =
                parts
                |> Array.tryPick (fun p ->
                    let trimmed = p.Trim()

                    if trimmed.StartsWith prefix then
                        Some(trimmed.Substring prefix.Length)
                    else
                        None)

            match tryFind "t=", tryFind "v1=" with
            | Some t, Some v1 ->
                match Int64.TryParse t with
                | true, ts -> Some(ts, v1)
                | false, _ -> None
            | _ -> None

    /// Verify a Stripe webhook signature. Takes the secret as a string
    /// (the `whsec_…` value); converts to bytes internally.
    let verifyWith
        (now: DateTimeOffset)
        (secret: string)
        (body: string)
        (header: string)
        : Result<VerifiedEvent, WebhookError> =
        // Fail closed on a blank/weak signing secret BEFORE computing the
        // HMAC. An HMAC-SHA256 with an empty key is publicly computable, so
        // without this guard `WebhookSecret = ""` would let a forged
        // `checkout.session.completed` / `invoice.paid` verify. The sibling
        // `TierToken.Token` already guards its key length — this closes the
        // matching omission here.
        if not (secretIsStrong secret) then
            Error SecretMissing
        else

            match parseHeader header with
            | None -> Error MalformedHeader
            | Some(timestamp, providedSig) ->
                let driftSeconds = now.ToUnixTimeSeconds() - timestamp

                if abs driftSeconds > MaxDriftSeconds then
                    Error(TimestampDrift driftSeconds)
                else
                    let payload = sprintf "%d.%s" timestamp body
                    let secretBytes = Encoding.UTF8.GetBytes secret

                    use h = new HMACSHA256(secretBytes)
                    let expected = h.ComputeHash(Encoding.UTF8.GetBytes payload)
                    let expectedHex = Convert.ToHexString(expected).ToLowerInvariant()

                    let sigOk =
                        constantTimeEquals
                            (Encoding.UTF8.GetBytes(providedSig.ToLowerInvariant()))
                            (Encoding.UTF8.GetBytes expectedHex)

                    if sigOk then
                        // Signature is good — decode the body into the typed
                        // event catalogue. A decode failure (unparseable JSON)
                        // surfaces as BodyParseError; an unknown `type` decodes
                        // to StripeEvent.Unknown (never an error).
                        match StripeEvent.decode body with
                        | Ok ev ->
                            Ok {
                                Body = body
                                Timestamp = timestamp
                                Event = ev
                            }
                        | Error msg -> Error(BodyParseError msg)
                    else
                        Error SignatureMismatch

    /// Verify a Stripe webhook signature using
    /// `DateTimeOffset.UtcNow` as the freshness reference.
    ///
    /// Takes the secret **by value**, so the value a caller passes is the
    /// value used. A composition root that reads the secret once and
    /// closes over it — which is what `StripeConfig.WebhookSecret` is —
    /// therefore needs a process restart to pick up a rotated secret.
    /// That is the correct shape when the secret genuinely is fixed for
    /// the process lifetime; `verifyWithFetcher` is the shape for a
    /// secret held in `ISecretStore` and rotated without a redeploy.
    let verify (secret: string) (body: string) (header: string) : Result<VerifiedEvent, WebhookError> =
        verifyWith DateTimeOffset.UtcNow secret body header

    /// Phase 464 — verify with the signing secret resolved **per call**
    /// through `fetchSecret`, rather than captured by value at compose.
    ///
    /// ## Why this overload exists
    ///
    /// `verify` bakes the secret into whatever closure calls it. A
    /// deployment that rotates its Stripe webhook signing secret — in the
    /// Stripe dashboard, then in its own secret store — keeps verifying
    /// against the superseded value until the process restarts, and every
    /// inbound event signed with the NEW secret fails as
    /// `SignatureMismatch`. That surfaces as forged-webhook alerts, not as
    /// a stale-configuration warning, so the cause is not visible from the
    /// symptom. Resolving per call closes the window: the next request
    /// after the store is updated verifies against the new secret.
    ///
    /// `fetchSecret` is typically a thunk over `ISecretStore.GetSecret`.
    /// It runs on the request path, so a caller whose store is a remote
    /// vault should compose its own short-TTL cache behind the thunk —
    /// this module deliberately holds no cache of its own, because a cache
    /// here would reintroduce exactly the staleness the overload exists to
    /// remove, in a place no caller can reach to invalidate.
    ///
    /// A fetcher that returns a blank or weak secret is rejected as
    /// `SecretMissing` by the same fail-closed strength gate `verify`
    /// applies — a store that has lost the key must never degrade into
    /// verifying with an empty HMAC key, which is publicly computable. A
    /// fetcher that THROWS is not caught here: an unavailable secret store
    /// is the caller's failure to classify (a 503 is usually right, and
    /// swallowing it into `SecretMissing` would report a transport outage
    /// as a signature problem).
    ///
    /// Zero new dependencies — `Async` is FSharp.Core, so
    /// `ToolUp.Stripe.Webhook` stays pure F# with no ASP.NET Core
    /// reference.
    let verifyWithFetcherAt
        (now: DateTimeOffset)
        (fetchSecret: unit -> Async<string>)
        (body: string)
        (header: string)
        : Async<Result<VerifiedEvent, WebhookError>> =
        async {
            let! secret = fetchSecret ()
            return verifyWith now secret body header
        }

    /// Phase 464 — `verifyWithFetcherAt` using `DateTimeOffset.UtcNow` as
    /// the freshness reference. The per-call-fetch counterpart to
    /// `verify`; see `verifyWithFetcherAt` for why it exists and what the
    /// fetcher contract is.
    let verifyWithFetcher
        (fetchSecret: unit -> Async<string>)
        (body: string)
        (header: string)
        : Async<Result<VerifiedEvent, WebhookError>> =
        verifyWithFetcherAt DateTimeOffset.UtcNow fetchSecret body header