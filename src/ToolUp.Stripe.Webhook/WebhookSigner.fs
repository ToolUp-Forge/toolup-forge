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
module WebhookSigner =
    /// Stripe's freshness window — 5 minutes.
    [<Literal>]
    let private MaxDriftSeconds = 300L

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
    let verify (secret: string) (body: string) (header: string) : Result<VerifiedEvent, WebhookError> =
        verifyWith DateTimeOffset.UtcNow secret body header