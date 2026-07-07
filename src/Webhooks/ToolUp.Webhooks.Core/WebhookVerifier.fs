namespace ToolUp.Webhooks

open System
open System.Security.Cryptography
open System.Text

/// Vendor-neutral inbound-webhook signature verification — the
/// generalisation of the Stripe-specific `WebhookSigner` into a
/// scheme-driven verifier. Pure F# + BCL crypto only (GP 1): no vendor
/// SDK, no ASP.NET Core. `verify` consumes `DateTimeOffset.UtcNow`;
/// tests inject a fixed clock via `verifyWith`.
module WebhookVerifier =

    /// Constant-time byte comparison so an attacker can't time-side-
    /// channel the signature.
    let private constantTimeEquals (a: byte[]) (b: byte[]) : bool =
        if a.Length <> b.Length then
            false
        else
            let mutable diff = 0

            for i in 0 .. a.Length - 1 do
                diff <- diff ||| (int a[i] ^^^ int b[i])

            diff = 0

    let private hmac (secret: string) (payload: string) : byte[] =
        use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)
        h.ComputeHash(Encoding.UTF8.GetBytes payload)

    let private encodeSig (encoding: SignatureEncoding) (bytes: byte[]) : string =
        match encoding with
        | HexLower -> Convert.ToHexString(bytes).ToLowerInvariant()
        | Base64 -> Convert.ToBase64String bytes

    /// Strip the scheme's literal prefix from a provided signature value.
    let private stripPrefix (prefix: string option) (value: string) : string =
        match prefix with
        | Some p when value.StartsWith p -> value.Substring p.Length
        | _ -> value

    /// Parse a Stripe-style comma-separated `k=v` header into the `t=`
    /// timestamp and the signature element keyed by `sigKey`.
    let private parseEmbedded (sigKey: string) (header: string) : (int64 * string) option =
        let parts = header.Split(',')

        let tryFind (prefix: string) =
            parts
            |> Array.tryPick (fun p ->
                let trimmed = p.Trim()

                if trimmed.StartsWith prefix then
                    Some(trimmed.Substring prefix.Length)
                else
                    None)

        match tryFind "t=", tryFind (sigKey + "=") with
        | Some t, Some v ->
            match Int64.TryParse t with
            | true, ts -> Some(ts, v)
            | false, _ -> None
        | _ -> None

    /// Verify an inbound webhook against `scheme`. `header` looks a
    /// request header up by name (case-insensitive is the caller's
    /// responsibility). Returns the verified, in-window delivery or a
    /// typed error — fail-closed: any error means do not dispatch.
    let verifyWith
        (now: DateTimeOffset)
        (scheme: WebhookScheme)
        (kind: string)
        (secret: string)
        (body: string)
        (header: string -> string option)
        : Result<VerifiedWebhook, WebhookError> =

        let driftOk (ts: int64) =
            match scheme.MaxDriftSeconds with
            | None -> Ok()
            | Some maxDrift ->
                let drift = now.ToUnixTimeSeconds() - ts

                if abs drift > maxDrift then
                    Error(TimestampDrift drift)
                else
                    Ok()

        // Resolve (timestamp option, providedSignature) per the scheme.
        let resolved: Result<int64 option * string, WebhookError> =
            match header scheme.SignatureHeader with
            | None -> Error MissingSignature
            | Some sigHeader ->
                match scheme.Timestamp with
                | EmbeddedInSignatureHeader ->
                    match parseEmbedded scheme.SignatureElementKey sigHeader with
                    | None -> Error MalformedHeader
                    | Some(ts, v) -> Ok(Some ts, v)
                | TimestampHeader tsHeader ->
                    match
                        header tsHeader
                        |> Option.bind (fun s ->
                            match Int64.TryParse s with
                            | true, v -> Some v
                            | _ -> None)
                    with
                    | None -> Error MalformedHeader
                    | Some ts -> Ok(Some ts, stripPrefix scheme.SignaturePrefix sigHeader)
                | NoTimestamp -> Ok(None, stripPrefix scheme.SignaturePrefix sigHeader)

        match resolved with
        | Error e -> Error e
        | Ok(tsOpt, providedSig) ->
            // Build the signed payload.
            let payloadResult =
                match scheme.SignedPayload, tsOpt with
                | BodyOnly, _ -> Ok body
                | TimestampDotBody, Some ts -> Ok(sprintf "%d.%s" ts body)
                | TimestampDotBody, None -> Error MalformedHeader

            match payloadResult with
            | Error e -> Error e
            | Ok payload ->
                // Freshness check (when a timestamp + window apply).
                let freshness =
                    match tsOpt with
                    | Some ts -> driftOk ts
                    | None -> Ok()

                match freshness with
                | Error e -> Error e
                | Ok() ->
                    let expected = encodeSig scheme.Encoding (hmac secret payload)

                    let sigOk =
                        constantTimeEquals
                            (Encoding.UTF8.GetBytes(providedSig.ToLowerInvariant()))
                            (Encoding.UTF8.GetBytes(expected.ToLowerInvariant()))

                    if not sigOk then
                        Error SignatureMismatch
                    else
                        Ok {
                            Kind = kind
                            Body = body
                            Timestamp = tsOpt
                            EventId = scheme.EventIdHeader |> Option.bind header
                        }

    /// Verify using `DateTimeOffset.UtcNow` as the freshness reference.
    let verify
        (scheme: WebhookScheme)
        (kind: string)
        (secret: string)
        (body: string)
        (header: string -> string option)
        : Result<VerifiedWebhook, WebhookError> =
        verifyWith DateTimeOffset.UtcNow scheme kind secret body header