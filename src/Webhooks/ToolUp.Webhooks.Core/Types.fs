namespace ToolUp.Webhooks

/// Encoding of the signature value on the wire.
type SignatureEncoding =
    /// Lower-case hex (Stripe, GitHub `sha256=` hex, most SaaS).
    | HexLower
    /// Base64 (Shopify, some Slack-style schemes).
    | Base64

/// How the signed payload bytes are assembled before HMAC.
type SignedPayload =
    /// HMAC over the raw request body only (GitHub, Shopify, most SaaS).
    | BodyOnly
    /// HMAC over `"{timestamp}.{body}"` (Stripe-style). Requires a
    /// timestamp source.
    | TimestampDotBody

/// Where the freshness timestamp is read from.
type TimestampSource =
    /// No timestamp; freshness is not checked.
    | NoTimestamp
    /// A dedicated header carrying a unix-seconds integer.
    | TimestampHeader of header: string
    /// Embedded in the signature header as a `t=` element alongside the
    /// signature element (Stripe-style `t=...,v1=...`).
    | EmbeddedInSignatureHeader

/// Vendor-neutral HMAC-SHA256 verification scheme expressed entirely as
/// data — a deployment configures one of these per integration rather
/// than re-rolling signature code. Covers the two dominant shapes:
/// simple `HMAC(body)` in a header (optionally with a separate
/// timestamp header for freshness) and Stripe-style `t=,v1=`.
type WebhookScheme = {
    /// Header carrying the signature (e.g. `Stripe-Signature`,
    /// `X-Hub-Signature-256`).
    SignatureHeader: string
    /// For `EmbeddedInSignatureHeader`: the comma-separated element key
    /// holding the signature value (e.g. `"v1"`). Ignored for the
    /// simple shape. The timestamp element is always `"t"` (Stripe).
    SignatureElementKey: string
    /// Literal prefix stripped from the signature value before compare
    /// (e.g. `"sha256="`). `None` = no prefix.
    SignaturePrefix: string option
    Encoding: SignatureEncoding
    SignedPayload: SignedPayload
    Timestamp: TimestampSource
    /// Freshness window in seconds; `None` disables the drift check.
    MaxDriftSeconds: int64 option
    /// Header carrying the unique delivery / event id used for
    /// idempotent dedup. `None` = no header-derived dedup key.
    EventIdHeader: string option
}

module WebhookScheme =
    /// Stripe-style: `Stripe-Signature: t=...,v1=<hex>`, payload
    /// `t.body`, 5-minute window. Pass the `Stripe-Signature` header.
    let stripeStyle: WebhookScheme = {
        SignatureHeader = "Stripe-Signature"
        SignatureElementKey = "v1"
        SignaturePrefix = None
        Encoding = HexLower
        SignedPayload = TimestampDotBody
        Timestamp = EmbeddedInSignatureHeader
        MaxDriftSeconds = Some 300L
        EventIdHeader = None
    }

    /// GitHub-style: `X-Hub-Signature-256: sha256=<hex>` over the raw
    /// body, no timestamp, dedup by the `X-GitHub-Delivery` header.
    let gitHubStyle: WebhookScheme = {
        SignatureHeader = "X-Hub-Signature-256"
        SignatureElementKey = ""
        SignaturePrefix = Some "sha256="
        Encoding = HexLower
        SignedPayload = BodyOnly
        Timestamp = NoTimestamp
        MaxDriftSeconds = None
        EventIdHeader = Some "X-GitHub-Delivery"
    }

/// A signature-verified, in-window inbound webhook ready for dispatch.
type VerifiedWebhook = {
    /// Integration kind (the registry key), taken from the route.
    Kind: string
    /// The raw request body (verified intact).
    Body: string
    /// The freshness timestamp, when the scheme supplied one.
    Timestamp: int64 option
    /// The unique delivery / event id for dedup, when available.
    EventId: string option
}

/// A handler's verdict on a verified delivery.
type WebhookAck =
    /// Processed (or accepted for async processing). Route returns 200.
    | Acknowledged
    /// Deliberately not acted on (e.g. an event type this handler
    /// ignores). Still a 200 — the delivery was valid, just a no-op.
    | Ignored of reason: string

/// A registered receiver for one integration `Kind`. Stateless between
/// invocations (GP 12 rule 4): all state arrives in the `VerifiedWebhook`,
/// so a distributed dispatcher can host it.
type IInboundWebhookHandler =
    /// The integration kind this handler receives (the route segment +
    /// registry key). Identity by value (GP 12 rule 1).
    abstract Kind: string
    /// Handle a verified delivery. Async at the boundary (rule 2).
    abstract Handle: VerifiedWebhook -> Async<WebhookAck>

/// Idempotent dedup seam. `TryClaim` returns `true` the first time a
/// `(kind, eventId)` pair is seen and `false` on a redelivery, so the
/// route acks duplicates without re-invoking the handler. Stateless
/// between calls (GP 12 rule 4) — a distributed impl may serve
/// successive calls from different nodes.
type IWebhookDedupStore =
    abstract TryClaim: kind: string * eventId: string -> Async<bool>