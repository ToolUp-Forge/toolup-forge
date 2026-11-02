module ToolUp.Platform.Tests.InProcess.StjBackwardCompatTests

open System
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson

// ─── STJ migration backward-compat backstop ──────────────────────
//
// The Newtonsoft → STJ migration (forge 0.5.0) introduced
// `ToolUp.Remoting.Json.SystemTextJson.FableConverters` as the
// replacement converter set. The migration doc claims byte-for-byte
// wire-format compat with the prior `FableJsonConverter` for every
// persistence-shaped DU + record — see the per-converter `// Wire
// format (matches FableJsonConverter.fs:NNN-NNN byte-for-byte)`
// comments in `SystemTextJsonConverter.fs` and the master mapping
// in `docs/migrations/fablejsonconverter-to-stj.md` /
// `BYTE-COMPAT-MAP.md`.
//
// Without a backstop, the first production deployment after v0.5.0
// reading a `WebhookSubscription` / `FlagValue` / `ShareTokenClaim`
// blob persisted by 0.4.x could crash silently when:
//   - a converter ordering subtly changed (DU before tuple, etc.),
//   - the encoder switched away from UnsafeRelaxedJsonEscaping,
//   - a record property's wire name diverged from PascalCase / camelCase
//     case-insensitive matching,
//   - a DU's case-name encoding (`{"Case": "X", "Fields": [...]}`)
//     drifted in an STJ release.
//
// This pack pins **the current STJ wire shape** for representative
// persistence DUs + records, and asserts both:
//   1. The serialised JSON byte-string still matches the frozen
//      golden snapshot (a future shape regression breaks the byte
//      comparison loudly).
//   2. A roundtrip through Deserialize + Serialize returns the
//      original value (catches asymmetric serialise/deserialise
//      paths — the "I can write but not read" silent break).
//
// The golden strings are captured by running the test once, copying
// the actual STJ output into the `frozen` value, and re-running.
// This earns a backstop with zero hand-disassembly of the wire
// format — the cost of authoring is one round-trip pass, the value
// is permanent byte-pinning of the persistence surface.

let private opts = FableConverters.shared

// ─── Fixture values ──────────────────────────────────────────────
//
// Stable, hand-constructed instances per persistence type. Each
// `frozen` literal is the byte-for-byte STJ output captured against
// `FableConverters.shared` at v0.5.0 ship time. Drifting the
// fixture re-snapshots the golden — drifting the wire format
// fails the test loudly.

let private flagValueBoolFixture = FlagValue.Bool true

let private flagValueVariantFixture =
    FlagValue.Variant([ "legacy"; "new"; "beta" ], "new")

let private webhookStatusFixture = WebhookStatus.Active

let private webhookSubFixture: WebhookSubscription = {
    SubscriptionId = Guid("11111111-2222-3333-4444-555555555555")
    ScopeId = "team-acme"
    TargetUrl = "https://example.com/hook"
    // Phase 6d.A — the signing secret lives in ISecretStore; the blob
    // carries only the reference. A migrated / freshly-created blob has
    // `Secret = None`. A pre-6d.A blob instead has `"Secret":"<value>"`
    // and no `SecretRef` key; FableConverters reads the missing `SecretRef`
    // back as null and the present `Secret` string back as `Some`, so the
    // dispatcher's inline fallback + the migration keep it readable.
    SecretRef = "_platform/webhooks/11111111222233334444555555555555.secret"
    Secret = None
    EventTypes = [ "FlagChanged"; "JobCompleted" ]
    Status = WebhookStatus.Active
    CreatedBy = "user-001"
    CreatedAt = DateTime(2026, 1, 15, 12, 30, 0, DateTimeKind.Utc)
    ConsecutiveFailures = 0
    // Phase 235 / 6d.A — never-rotated subscription: all grace-window
    // fields are None. FableConverters reads missing option fields back as
    // None, so the additive growth is backward-compatible on the read path.
    PreviousSecretRef = None
    PreviousSecret = None
    PreviousSecretExpiresAt = None
}

let private shareTokenClaimFixture: ShareTokenClaim = {
    TokenId = "tok_abc123"
    ScopeId = "team-acme"
    ResourceKind = "forms.publishable"
    ResourceId = "form-001"
    AttributedHandle = Some "respondent@example.com"
    IssuedBy = "user-001"
    IssuedAt = DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero)
    ExpiresAt = DateTimeOffset(2026, 2, 14, 12, 30, 0, TimeSpan.Zero)
    UseLimit = Some 1
    UsedCount = 0
    Revoked = false
    RateLimit = None
}

let private flagChangedPayloadFixture: FlagChangedPayload = {
    Key = "skuanalysis.new-grid"
    Scope = FlagScope.Team "team-acme"
    Action = FlagChangeAction.Set(FlagValue.Bool true)
    ChangedBy = "user-001"
}

let private notificationFixture =
    Notification.JobCompleted(Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "Succeeded", Some "/jobs/abc")

// ─── Helpers ─────────────────────────────────────────────────────

let private serialise<'T> (v: 'T) : string = JsonSerializer.Serialize<'T>(v, opts)

let private roundtrip<'T when 'T: equality> (v: 'T) : 'T =
    let json = serialise v
    JsonSerializer.Deserialize<'T>(json, opts)

/// Build one byte-pin + roundtrip test per fixture. `frozen` is the
/// expected STJ output captured at v0.5.0. If the wire shape drifts,
/// `Expect.equal` fails with a side-by-side diff — copy the new
/// actual into `frozen` only after auditing that the new shape is
/// still byte-compat with prior-version persisted blobs.
let private pinTest<'T when 'T: equality> (name: string) (frozen: string) (value: 'T) =
    testList name [
        testCase "wire format matches frozen golden snapshot"
        <| fun _ ->
            let actual = serialise value

            Expect.equal
                actual
                frozen
                "STJ wire shape drifted from the v0.5.0 frozen snapshot — confirm the new shape is byte-compat with prior-version persisted blobs before updating the fixture"

        testCase "Deserialize ∘ Serialize roundtrip preserves value equality"
        <| fun _ ->
            let actual = roundtrip value
            Expect.equal actual value "roundtrip diverged — serialise/deserialise paths are asymmetric"
    ]

// ─── Frozen golden snapshots ─────────────────────────────────────
//
// Each `frozen<TypeName>` literal is the byte-for-byte STJ output
// against `FableConverters.shared` at v0.5.0. The
// `wire format matches frozen golden snapshot` test enforces these.
//
// To re-snapshot a fixture: temporarily change the `Expect.equal` to
// print `actual` (e.g. via `failtest actual`), run the test once,
// copy the printed JSON into the matching `frozen` literal, restore
// the assertion, and verify the test passes.

// Note: the actual STJ wire shape produced by `FableConverters.shared`
// follows the **Fable.SimpleJson** convention — `{"<CaseName>": value}`
// for single-field DU cases, `{"<CaseName>": [field1, field2, ...]}`
// for multi-field, and the bare string `"<CaseName>"` for cases with
// no payload. This is NOT the Newtonsoft `{"Case": "X", "Fields":
// [...]}` shape that some other STJ converters use. The wire shape
// here matches what Fable.SimpleJson on the client emits / accepts,
// which is the byte-pin contract this backstop exists to protect.

let private frozenFlagValueBool = """{"Bool":true}"""

let private frozenFlagValueVariant =
    """{"Variant":[["legacy","new","beta"],"new"]}"""

let private frozenWebhookStatus = "\"Active\""

let private frozenWebhookSub =
    """{"SubscriptionId":"11111111-2222-3333-4444-555555555555","ScopeId":"team-acme","TargetUrl":"https://example.com/hook","SecretRef":"_platform/webhooks/11111111222233334444555555555555.secret","Secret":null,"EventTypes":["FlagChanged","JobCompleted"],"Status":"Active","CreatedBy":"user-001","CreatedAt":"2026-01-15T12:30:00.0000000Z","ConsecutiveFailures":0,"PreviousSecretRef":null,"PreviousSecret":null,"PreviousSecretExpiresAt":null}"""

let private frozenShareTokenClaim =
    """{"TokenId":"tok_abc123","ScopeId":"team-acme","ResourceKind":"forms.publishable","ResourceId":"form-001","AttributedHandle":"respondent@example.com","IssuedBy":"user-001","IssuedAt":"2026-01-15T12:30:00+00:00","ExpiresAt":"2026-02-14T12:30:00+00:00","UseLimit":1,"UsedCount":0,"Revoked":false,"RateLimit":null}"""

let private frozenFlagChangedPayload =
    """{"Key":"skuanalysis.new-grid","Scope":{"Team":"team-acme"},"Action":{"Set":{"Bool":true}},"ChangedBy":"user-001"}"""

let private frozenNotification =
    """{"JobCompleted":["aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","Succeeded","/jobs/abc"]}"""

[<Tests>]
let tests =
    testList "STJ backward-compat (persistence wire shape)" [
        pinTest "FlagValue.Bool" frozenFlagValueBool flagValueBoolFixture
        pinTest "FlagValue.Variant" frozenFlagValueVariant flagValueVariantFixture
        pinTest "WebhookStatus" frozenWebhookStatus webhookStatusFixture
        pinTest "WebhookSubscription" frozenWebhookSub webhookSubFixture
        pinTest "ShareTokenClaim" frozenShareTokenClaim shareTokenClaimFixture
        pinTest "FlagChangedPayload" frozenFlagChangedPayload flagChangedPayloadFixture
        pinTest "Notification.JobCompleted" frozenNotification notificationFixture
    ]