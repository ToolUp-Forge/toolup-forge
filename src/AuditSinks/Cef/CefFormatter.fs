module ToolUp.Platform.AuditSinks.CefFormat

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 9g.A CEF (Common Event Format) rendering for the
// `ToolUp.AuditSinks.Cef` companion. Pure functions only — no I/O, no
// sockets, no secrets. `CefAuditSink` composes this with
// `CefSyslog` to put lines on the wire.
//
// **Distributed-ready.** Everything here is a pure function of its
// arguments; no state survives a call. Portability rule 4 holds by
// construction.
//
// **The format.** One event renders to one line:
//
//     CEF:0|Vendor|Product|DeviceVersion|SignatureID|Name|Severity|k=v k=v …
//
// The seven pipe-delimited header fields are positional and mandatory;
// everything after the seventh pipe is the *extension* — space-separated
// `key=value` pairs drawn from the CEF dictionary (`rt`, `cat`, `suser`,
// `externalId`, `msg`, `cs1`…`cs6` custom strings with their `…Label`
// twins).
//
// **Escaping is asymmetric between the two halves**, which is the single
// most common source of unparseable CEF:
//   * header fields escape `\` → `\\` and `|` → `\|` (a raw pipe would
//     shift every later field by one position);
//   * extension values escape `\` → `\\`, `=` → `\=` (a raw `=` would
//     read as the start of the next key), and newlines → `\n` / `\r`;
//   * extension *keys* are alphanumeric by spec — anything else is
//     dropped rather than escaped, since receivers key off them.
//
// **Severity is derived, not carried.** The SDK's `AuditEvent` is a DU
// of ~130 cases with per-case payload records and no severity field
// (see `Shared/AuditTypes.fs`). Rather than add one — which would touch
// every payload type and every emission call site for the benefit of one
// companion — this module classifies by event kind into `CefSeverityBand`
// and projects that onto CEF's 0–10 scale. A deployment that disagrees
// with a band overrides the whole mapping via
// `CefSinkSettings.SeverityOverride`.
//
// **The 1023-byte cap.** CEF receivers (ArcSight's smart connectors, the
// QRadar DSM, LogRhythm's syslog pipeline) truncate silently past 1023
// bytes, which corrupts the last key/value pair and — because the cut
// can land inside a `\=` — can make the remainder of the line reparse as
// a different field. `renderLine` therefore packs pairs against an
// explicit byte budget, trims an oversized value on its RAW text before
// escaping (so an escape sequence can never be split), never emits a
// partial key, and appends `cefTruncated=true` so the loss is visible
// downstream rather than inferred.

/// The three deployment-identifying header fields. Ships as
/// `CefDeviceIdentity.defaults` and is overridden per deployment by the
/// `_platform/audit/cef.json` config blob the sink reads on every
/// delivery — a customer SOC generally mandates the exact vendor /
/// product strings its correlation rules key off, and those are an
/// operator concern rather than a code concern.
type CefDeviceIdentity = {
    /// CEF header field 1. The organisation that produced the event —
    /// conventionally the deploying company's name, not the SDK's, since
    /// SOC correlation rules are written per-customer.
    Vendor: string
    /// CEF header field 2. The product emitting the event.
    Product: string
    /// CEF header field 3. Version string of the emitting product.
    DeviceVersion: string
}

module CefDeviceIdentity =
    /// Neutral fallback used when no `_platform/audit/cef.json` blob is
    /// present. Deliberately generic — a deployment that ships these
    /// values to a customer SOC has skipped its configuration step, and
    /// the strings say so plainly rather than masquerading as the
    /// customer's own product.
    let defaults: CefDeviceIdentity = {
        Vendor = "ToolUp"
        Product = "ToolUpPlatform"
        DeviceVersion = "0.1.0"
    }

/// Coarse severity classification of an audit event, projected onto
/// CEF's 0–10 integer scale by `CefSeverityBand.toScore`. Bands (rather
/// than raw integers) keep the ~130-case classification readable and
/// keep the ArcSight low/medium/high/very-high boundaries in one place.
type CefSeverityBand =
    /// Routine, expected activity — logins, reads, successful deliveries.
    /// Scores 2 (ArcSight "low").
    | CefInformational
    /// Ordinary state change with no security weight. Scores 4
    /// (ArcSight "medium", bottom of the band).
    | CefLow
    /// Notable change or a recoverable failure a SOC would want visible.
    /// Scores 6 (ArcSight "medium", top of the band).
    | CefMedium
    /// Security-relevant denial, policy refusal, or exhausted-retry
    /// failure. Scores 8 (ArcSight "high").
    | CefHigh
    /// Irreversible or privilege-altering operation. Scores 10
    /// (ArcSight "very high").
    | CefCritical

module CefSeverityBand =
    /// Project a band onto the CEF 0–10 severity integer.
    let toScore =
        function
        | CefInformational -> 2
        | CefLow -> 4
        | CefMedium -> 6
        | CefHigh -> 8
        | CefCritical -> 10

    /// Every band, ordered ascending. Handy for a deployment writing its
    /// own override table against the same vocabulary.
    let all = [ CefInformational; CefLow; CefMedium; CefHigh; CefCritical ]

// Classification tables. Membership sets rather than a `match` over the
// DU: the audit DU is append-only and grows most releases, and an
// exhaustive match here would turn every new audit case into a compile
// break in this companion. New cases land in `CefLow` (the "state
// changed, no security weight" default) until someone classifies them —
// a wrong-but-conservative band beats a broken build in a sink.

let private criticalEvents =
    set [
        "DataStoreReset"
        "EncryptionKeyDestroyed"
        "KnowledgeScopeErased"
        "PlatformAdminAssigned"
        "PlatformAdminRevoked"
        "TeamDeleted"
        "TeamOwnershipTransferred"
        "TenantDeprovisioned"
        "ConversationErased"
        "DataSubjectRequest"
    ]

let private highEvents =
    set [
        "ArtifactRejected"
        "AuditEventDecodeFailed"
        "AuditSinkDeadLettered"
        "AuthScopeResolutionFailed"
        "AuthorizationDenied"
        "BeaconRejected"
        "ConfigDrift"
        "DatasetPolicyDenied"
        "EgressBlocked"
        "EncryptionKeyRotated"
        // Phase 551 — grant-policy refusals. High for the same reason
        // `SchemaOnlyAccessAttempted` is: a refusal rate is a leading
        // indicator for credential leak / misconfiguration, and
        // `UnconsentedGrantRefused` with reason `no-grant-record` is the
        // signature of a permission row written outside the guard.
        "GrantPolicyRefused"
        // Phase 552 — the two rows that CHANGE who may reach a
        // counterparty-gated module, plus the tamper alert.
        // `GrantConsentApproved` is High for the same reason
        // `PermissionChanged` is: it is the moment third-party authority
        // becomes grantable. `GrantConsentRevoked` is High because its
        // ABSENCE is what a reviewer looks for when access should have
        // stopped and did not.
        "GrantConsentApproved"
        "GrantConsentRevoked"
        // The forgery signal: a record presenting itself as consent that
        // does not verify — a bad signature, an unregistered key, an
        // algorithm downgrade, a record filed against another subject.
        // Emitted only on those grounds, never for an ordinary revocation
        // or expiry, so its rate is meaningful rather than a baseline.
        "GrantConsentVerificationDenied"
        "KnowledgeOriginalRetrievalDenied"
        "ModelArtifactTransitionDenied"
        "ModelFitGateFailed"
        "ModelScoreRefused"
        "OAuth1aSigningFailed"
        "OAuthRefreshDeadLettered"
        "OAuthRefreshFailed"
        "OAuthRefreshTokenInvalidated"
        "OAuthTokenRefreshFailed"
        "PermissionChanged"
        "RateLimitRefused"
        "SchemaOnlyAccessAttempted"
        "SigningKeyRotated"
        "SurfaceDenied"
        "TeamCreationDenied"
        "TenantOffboardConfirmationRefused"
        "UnconsentedGrantRefused"
    ]

let private mediumEvents =
    set [
        "AssetDeleted"
        "AuditSinkFailed"
        "ClassifiedFieldRead"
        "ClassifiedFieldWritten"
        "ConversationExported"
        "DatasetDeclassified"
        "DiagnosticBundleAccessed"
        "EncryptionKeyCreated"
        // Phase 22b — a replica confirming it evicted a destroyed key.
        // Not itself destructive (the EncryptionKeyDestroyed that caused
        // it is Critical); Medium because its ABSENCE across a fleet is
        // the security signal a reviewer looks for.
        "EncryptionKeyDestroyAcknowledged"
        "EntityDeleted"
        "FileDeleted"
        // Phase 552 — a consent lodged awaiting the counterparty. Medium,
        // not High: it confers nothing at dispatch, so it is an operations
        // queue rather than an authority change. Its sibling
        // `GrantConsentApproved` is the row that changes access, and it
        // sits in the High table.
        "GrantConsentProposed"
        "HealthStateChanged"
        "MemberRemoved"
        "MemberRoleChanged"
        "NotificationDeliveryFailed"
        "NotificationSilentlySkipped"
        "PasskeyCredentialRemoved"
        "PlatformDocumentDeleted"
        "PremiumRevoked"
        "ShareTokenIssued"
        "ShareTokenRevoked"
        "TeamArchived"
        "TeamInviteRevoked"
        "TenantDataExported"
        "TenantDeprovisionScheduled"
        "TenantLifecycleHookFailed"
    ]

let private informationalEvents =
    set [
        "AdClickRecorded"
        "AdImpressionRecorded"
        "AnalysisRun"
        "AuditSinkDelivered"
        "ConversationCompleted"
        "ConversationStarted"
        "ConversationTurnAppended"
        "KnowledgeOriginalRetrieved"
        "ModelScored"
        "NotificationSent"
        "PeerCallCompleted"
        "RateLimitWaited"
        "RemotingMethodAudited"
        "ShareTokenUsed"
        "UserLoggedIn"
    ]

/// Classify an audit event into a `CefSeverityBand`. Unclassified event
/// types fall to `CefLow` — see the note above the tables on why this is
/// a lookup rather than an exhaustive match.
let severityBandOf (event: AuditEvent) : CefSeverityBand =
    let name = AuditEvent.eventTypeName event

    if criticalEvents.Contains name then CefCritical
    elif highEvents.Contains name then CefHigh
    elif mediumEvents.Contains name then CefMedium
    elif informationalEvents.Contains name then CefInformational
    else CefLow

/// CEF severity integer (0–10) for an audit event.
let severityOf (event: AuditEvent) : int =
    event |> severityBandOf |> CefSeverityBand.toScore

/// Maximum total byte length of a rendered CEF line, per the CEF spec.
/// Receivers truncate silently past this; `renderLine` truncates
/// explicitly instead.
[<Literal>]
let MaxCefLineBytes = 1023

/// Appended as the final extension pair when `renderLine` dropped or
/// shortened anything. Chosen to be a legal CEF key/value pair so a
/// truncated line still parses — the loss is data the receiver can see,
/// not a syntax error it must recover from.
[<Literal>]
let TruncationMarker = "cefTruncated=true"

/// Raw-character ceiling applied to each header field before escaping.
/// Header fields are operator-configured strings; a pathological value
/// would otherwise consume the whole line budget and leave no room for
/// the event itself. Trimming happens BEFORE escaping so a `\|` pair is
/// never split down the middle.
[<Literal>]
let MaxHeaderFieldChars = 96

let private utf8Len (value: string) = Encoding.UTF8.GetByteCount value

/// Trim `value` to at most `maxChars`, never leaving a lone high
/// surrogate at the end (which would render as U+FFFD downstream and, on
/// a strict UTF-8 receiver, reject the datagram).
let private trimChars (maxChars: int) (value: string) =
    if isNull value then
        ""
    elif value.Length <= maxChars then
        value
    else
        let cut =
            if maxChars > 0 && Char.IsHighSurrogate value[maxChars - 1] then
                maxChars - 1
            else
                maxChars

        value.Substring(0, cut)

/// Escape a CEF *header* field: `\` → `\\`, `|` → `\|`. CR / LF are
/// replaced with a space rather than escaped — the header sits before the
/// extension section, where `\n` is a value-escape and would be
/// misread. Nulls render as the empty string.
let escapeHeaderField (value: string) : string =
    if String.IsNullOrEmpty value then
        ""
    else
        let sb = StringBuilder(value.Length + 8)

        for ch in value do
            match ch with
            | '\\' -> sb.Append "\\\\" |> ignore
            | '|' -> sb.Append "\\|" |> ignore
            | '\r'
            | '\n' -> sb.Append ' ' |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()

/// Escape a CEF *extension* value: `\` → `\\`, `=` → `\=`, CR → `\r`,
/// LF → `\n`. The pipe is NOT escaped here — it is only structural in the
/// header — and escaping it would corrupt values that legitimately
/// contain one.
let escapeExtensionValue (value: string) : string =
    if String.IsNullOrEmpty value then
        ""
    else
        let sb = StringBuilder(value.Length + 8)

        for ch in value do
            match ch with
            | '\\' -> sb.Append "\\\\" |> ignore
            | '=' -> sb.Append "\\=" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()

/// Reduce a proposed extension key to the alphanumeric form the CEF
/// dictionary requires. Non-conforming characters are DROPPED, not
/// escaped: a receiver splits the extension on unescaped `=` and
/// whitespace, so a key carrying either is unrecoverable rather than
/// merely ugly. An empty result signals "unusable key" to the caller.
let sanitiseExtensionKey (key: string) : string =
    if String.IsNullOrEmpty key then
        ""
    else
        let sb = StringBuilder(key.Length)

        for ch in key do
            if Char.IsLetterOrDigit ch then
                sb.Append ch |> ignore

        sb.ToString()

let private cefJsonOptions = FableConverters.create ()

let private epoch = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// Deterministic per-envelope deduplication key, emitted as the CEF
/// `externalId` extension.
///
/// **Why deterministic rather than a fresh GUID.** The dispatcher retries
/// a whole batch on `Result.Error`, and the catch-up sweep can re-deliver
/// after a restart — so the same envelope legitimately reaches the SIEM
/// more than once. A random id per emission (the shape `SplunkHec` uses,
/// where Splunk's `_meta.uuid` mostly guards against index-side
/// duplication) makes those redeliveries look like distinct events. A
/// content hash makes them collapse, which is what "batch-idempotent"
/// has to mean for a fire-and-forget syslog transport that cannot report
/// partial acceptance.
let dedupKeyOf (payload: string) (envelope: AuditEnvelope) : string =
    let canonical =
        String.Join(
            "",
            [
                string AuditSchemaVersion.current
                envelope.OccurredAt.ToUniversalTime().Ticks |> string
                envelope.ScopeId
                AuditEnvelope.subjectKindString envelope
                AuditEvent.eventTypeName envelope.Event
                payload
            ]
        )

    canonical
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

/// `dedupKeyOf` over the envelope's own serialised payload. Convenience
/// for callers (and tests) that do not already hold the JSON.
let dedupKey (envelope: AuditEnvelope) : string =
    dedupKeyOf (JsonSerializer.Serialize(envelope.Event, cefJsonOptions)) envelope

/// Subject-derived actor id for the `suser` extension, plus the team id
/// for `cs3` when the subject carries one.
let private subjectFields (envelope: AuditEnvelope) =
    match envelope.Subject with
    | AnonymousAudit sessionId -> sessionId, None
    | UserAudit userId -> userId, None
    | TeamAudit(userId, teamId) -> userId, Some teamId
    | ClaimAudit(tokenId, attributedHandle, _, _) -> attributedHandle |> Option.defaultValue tokenId, None

/// The ordered extension pairs for one envelope, before escaping and
/// before the byte budget is applied. Order is load-bearing: the cheap,
/// high-value correlation fields come first so that a truncated line
/// still carries them, and the unbounded `msg` payload comes last so it
/// is the field that gets shortened.
let extensionPairs (envelope: AuditEnvelope) : (string * string) list =
    let suser, teamId = subjectFields envelope
    let occurredUtc = envelope.OccurredAt.ToUniversalTime()
    let receiptMillis = (occurredUtc - epoch).TotalMilliseconds |> int64
    let payload = JsonSerializer.Serialize(envelope.Event, cefJsonOptions)

    let teamPairs =
        match teamId with
        | Some tid -> [ "cs3Label", "TeamId"; "cs3", tid ]
        | None -> []

    [
        "rt", string receiptMillis
        "externalId", dedupKeyOf payload envelope
        "cat", AuditEvent.eventTypeName envelope.Event
        "suser", suser
        "cs1Label", "ScopeId"
        "cs1", envelope.ScopeId
        "cs2Label", "SubjectKind"
        "cs2", AuditEnvelope.subjectKindString envelope
        "cs4Label", "AuditSchemaVersion"
        "cs4", string AuditSchemaVersion.current
    ]
    @ teamPairs
    @ [ "msg", payload ]

/// Pack `pairs` into a space-separated extension section that fits
/// `budget` bytes. Returns the section plus a flag saying whether
/// anything was dropped or shortened.
///
/// Two invariants make a truncated line safe to parse: a key is either
/// emitted whole with a value or not emitted at all, and an oversized
/// value is cut on its RAW text and escaped afterwards — so the cut can
/// never land between a backslash and the character it escapes.
let private packExtensions (budget: int) (pairs: (string * string) list) : string * bool =
    let sb = StringBuilder()

    let rec pack used remaining =
        match remaining with
        | [] -> false
        | (rawKey, rawValue) :: rest ->
            let key = sanitiseExtensionKey rawKey

            if String.IsNullOrEmpty key then
                pack used rest
            else
                let separator = if used = 0 then "" else " "
                let fixedPart = separator + key + "="
                let fixedLen = utf8Len fixedPart
                let escaped = escapeExtensionValue rawValue
                let wholeLen = fixedLen + utf8Len escaped

                if used + wholeLen <= budget then
                    sb.Append(fixedPart).Append(escaped) |> ignore
                    pack (used + wholeLen) rest
                else
                    // The pair does not fit whole. Emit as much of the
                    // value as the remaining room allows — trimming the
                    // raw text and re-escaping each candidate, so the
                    // emitted escape sequences are always complete — then
                    // stop: everything after this point is dropped, and
                    // the caller stamps the truncation marker.
                    let room = budget - used - fixedLen

                    if room > 0 && not (String.IsNullOrEmpty rawValue) then
                        let mutable cut = min rawValue.Length room

                        while cut > 0 && utf8Len (escapeExtensionValue (trimChars cut rawValue)) > room do
                            cut <- cut - 1

                        if cut > 0 then
                            let trimmed = escapeExtensionValue (trimChars cut rawValue)
                            sb.Append(fixedPart).Append(trimmed) |> ignore

                    true

    let truncated = pack 0 pairs
    sb.ToString(), truncated

/// Render the seven-field CEF header (including its trailing pipe) at an
/// explicit severity. The severity is clamped into 0–10 — a receiver
/// rejects the whole line on an out-of-range value, and silently dropping
/// audit events because a deployment's override table returned 11 is not
/// a failure mode worth preserving.
let renderHeaderAt (identity: CefDeviceIdentity) (severity: int) (envelope: AuditEnvelope) : string =
    let eventType = AuditEvent.eventTypeName envelope.Event

    let field value =
        value |> trimChars MaxHeaderFieldChars |> escapeHeaderField

    sprintf
        "CEF:0|%s|%s|%s|%s|%s|%d|"
        (field identity.Vendor)
        (field identity.Product)
        (field identity.DeviceVersion)
        (field eventType)
        (field eventType)
        (severity |> max 0 |> min 10)

/// Render the seven-field CEF header (including its trailing pipe) using
/// the built-in severity classification.
let renderHeader (identity: CefDeviceIdentity) (envelope: AuditEnvelope) : string =
    renderHeaderAt identity (severityOf envelope.Event) envelope

/// Render one audit envelope as a complete CEF line, capped at
/// `MaxCefLineBytes` with an explicit `cefTruncated=true` marker when
/// anything was dropped or shortened.
///
/// `severityOverride` lets a deployment substitute its own event-kind →
/// 0–10 mapping without forking the companion; `None` uses `severityOf`.
let renderLineWith
    (identity: CefDeviceIdentity)
    (severityOverride: (AuditEvent -> int) option)
    (envelope: AuditEnvelope)
    : string =
    let severity =
        match severityOverride with
        | None -> severityOf envelope.Event
        | Some scoreOf -> scoreOf envelope.Event

    let header = renderHeaderAt identity severity envelope
    let headerBytes = utf8Len header
    let pairs = extensionPairs envelope

    let fullSection, truncatedAtFull =
        packExtensions (MaxCefLineBytes - headerBytes) pairs

    if not truncatedAtFull then
        header + fullSection
    else
        // Re-pack against a budget that reserves room for the marker, so
        // the marker itself never pushes the line over the cap.
        let markerReserve = utf8Len (" " + TruncationMarker)

        let reduced, _ =
            packExtensions (MaxCefLineBytes - headerBytes - markerReserve) pairs

        if String.IsNullOrEmpty reduced then
            header + TruncationMarker
        else
            header + reduced + " " + TruncationMarker

/// Render one audit envelope as a complete CEF line using the built-in
/// severity classification.
let renderLine (identity: CefDeviceIdentity) (envelope: AuditEnvelope) : string = renderLineWith identity None envelope