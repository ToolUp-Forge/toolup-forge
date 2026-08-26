namespace Contoso.Consumer.Preflight

open System

// ─── Phase 727 fixtures — a THIRD-PARTY attribute family ─────────────
//
// The Phase 335 fixture set, extended past the auth markers to the four
// families that ride the same dispatch path. Deliberately named
// identically to the sanctioned markers: a different namespace, and
// (being defined in the test assembly) a different assembly too. Both
// halves are load-bearing — identity matching pins the assembly as well
// as the namespace.

/// Collides with the sharpest marker of the four families: pre-727 this
/// STOPPED an input field being redacted, putting its value verbatim into
/// every emitted audit row and thence into every composed audit sink.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type PiiSafeAttribute() =
    inherit Attribute()

/// Collides with the audit opt-in. Pre-727 a foreign one emitted rows
/// with a kind decoded from a foreign string property.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type AuditAttribute(kindName: string) =
    inherit Attribute()
    member _.KindName = kindName

/// Collides with the rate-limit budget, property names and all — which is
/// what the pre-727 reflective read keyed off.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type RateLimitAttribute(count: int, windowSeconds: int) =
    inherit Attribute()
    member _.Count = count
    member _.WindowSeconds = windowSeconds

/// Collides with the idempotency marker.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type IdempotentAttribute() =
    inherit Attribute()

/// Collides with the auth marker, for the Streaming-diagnostic alignment
/// case (Phase 335 pinned this against the classifier; 727 pins that the
/// streaming diagnostic reaches the same verdict).
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type RequiresRoleAttribute(role: string) =
    inherit Attribute()
    member _.Role = role

/// Collides with a VALIDATION mirror, property name and all. Unlike the
/// four above this one is still honoured, deliberately — see the
/// "validation keeps simple-name matching" tests for the recorded reason.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type MinLengthAttribute(n: int) =
    inherit Attribute()
    member _.MinLength = n

namespace ToolUp.Platform.Tests.InProcess

open System.Collections.Generic
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server

/// ─── Phase 727 — attribute recognition beyond the auth classifier ────
///
/// Phase 335 moved the dispatch AUTH classifier to CLR type identity, so
/// a forged same-named attribute can no longer impersonate a real marker
/// there. Four other attribute families — and one diagnostic — still
/// recognised their attributes by bare simple name. This pack pins the
/// per-family outcome of that sweep, INCLUDING the family whose assessed
/// answer was "simple name is fine here".
///
/// The severity assessments themselves live beside the code they govern
/// (the module headers in `Audit.fs`, `RateLimit.fs`, `Idempotency.fs`,
/// `Validation.fs`); this pack is the falsifiable half.
///
///   * audit / PII-safety — FIXED (identity + startup refusal). A forged
///     `[<PiiSafe>]` no longer un-redacts an audit payload field; a
///     forged `[<Audit>]` no longer arms emission.
///   * rate limiting — FIXED. A forged budget no longer applies.
///   * idempotency — FIXED. A forged marker no longer arms memoisation.
///   * validation — KEPT on simple name, deliberately, and pinned so the
///     next sweep does not flip it from consistency alone.
///   * the `Streaming.fs` diagnostic — aligned with the classifier, and
///     the tier-shared `[<RateLimit>]` mirror it recognised through
///     NEITHER path is now flagged.
module AttributeRecognitionSweepTests =

    // ── Fixtures: audit ─────────────────────────────────────────────

    /// The un-redaction hole in its purest form.
    type private ForeignPiiInput = {
        [<Contoso.Consumer.Preflight.PiiSafe>]
        Email: string
        Note: string
    }

    type private SanctionedServerPiiInput = {
        [<PiiSafe>]
        Email: string
        Note: string
    }

    type private SanctionedMirrorPiiInput = {
        [<ToolUp.Platform.PiiSafe>]
        Email: string
        Note: string
    }

    type private ForeignAuditApi = {
        [<AllowAnonymous>]
        [<Contoso.Consumer.Preflight.Audit "MoneyMoved">]
        Move: unit -> Async<int>
    }

    type private SanctionedAuditApi = {
        [<AllowAnonymous>]
        [<Audit "MoneyMoved">]
        ServerTier: unit -> Async<int>
        [<AllowAnonymous>]
        [<ToolUp.Platform.Audit "PolicyChanged">]
        Mirror: unit -> Async<int>
    }

    /// An audited method whose INPUT record carries the forged PII marker
    /// — the shape the startup scan has to reach through `inputTypes`.
    type private ForeignPiiOnAuditedInputApi = {
        [<AllowAnonymous>]
        [<Audit "PiiAccessed">]
        Read: ForeignPiiInput -> Async<int>
    }

    // ── Fixtures: rate limiting ─────────────────────────────────────

    type private ForeignRateLimitApi = {
        [<AllowAnonymous>]
        [<Contoso.Consumer.Preflight.RateLimit(3, 60)>]
        Burst: unit -> Async<int>
    }

    type private SanctionedRateLimitApi = {
        [<AllowAnonymous>]
        [<RateLimit(3, RateLimitWindow.perMinute)>]
        ServerTier: unit -> Async<int>
        [<AllowAnonymous>]
        [<ToolUp.Platform.RateLimit(7, ToolUp.Platform.RateLimitSeconds.perMinute)>]
        Mirror: unit -> Async<int>
    }

    // ── Fixtures: idempotency ───────────────────────────────────────

    type private ForeignIdempotentApi = {
        [<AllowAnonymous>]
        [<Contoso.Consumer.Preflight.Idempotent>]
        Charge: unit -> Async<int>
    }

    type private SanctionedIdempotentApi = {
        [<AllowAnonymous>]
        [<Idempotent>]
        ServerTier: unit -> Async<int>
        [<AllowAnonymous>]
        [<ToolUp.Platform.Idempotent>]
        Mirror: unit -> Async<int>
    }

    // ── Fixtures: streaming diagnostic ──────────────────────────────

    /// A streaming method carrying a FORGED auth marker. Pre-727 the
    /// diagnostic reported it as `RequiresRole("Admin")` — a guard this
    /// SDK would never have honoured — while the classifier (post-335)
    /// treated it as foreign. Two matchers, two verdicts.
    type private ForgedGuardStreamApi = {
        [<Contoso.Consumer.Preflight.RequiresRole "Admin">]
        Tail: string -> IAsyncEnumerable<string>
    }

    /// The tier-shared rate-limit mirror on a streaming method — the real
    /// defect the alignment fixed. Pre-727 the matcher tested only the
    /// server-tier type and had no `RateLimitAttribute` name arm, so this
    /// was recognised by NEITHER path: the adapter started and the SSE
    /// short-circuit silently dropped the declared budget.
    type private MirrorRateLimitStreamApi = {
        [<AllowAnonymous>]
        [<ToolUp.Platform.RateLimit(5, ToolUp.Platform.RateLimitSeconds.perMinute)>]
        Tail: string -> IAsyncEnumerable<string>
    }

    // ── Fixtures: validation (the KEPT family) ──────────────────────

    type private ForeignValidatedInput = {
        [<Contoso.Consumer.Preflight.MinLength 5>]
        Name: string
    }

    type private ForeignValidatedApi = {
        [<AllowAnonymous>]
        Submit: ForeignValidatedInput -> Async<int>
    }

    /// The BCL family whose simple names collide but whose PROPERTY names
    /// do not (`Length`, not `MinLength`) — which is what keeps it out of
    /// the engine today. It sits on consumer DTOs for EF Core column
    /// mapping, so honouring it, or refusing to start on it, would both be
    /// wrong.
    type private DataAnnotationsInput = {
        [<System.ComponentModel.DataAnnotations.MinLength 5>]
        Name: string
    }

    type private DataAnnotationsApi = {
        [<AllowAnonymous>]
        Submit: DataAnnotationsInput -> Async<int>
    }

    // ── Helpers ─────────────────────────────────────────────────────

    let private refusalOf (compose: unit -> unit) (label: string) : System.InvalidOperationException =
        try
            compose ()
            failtestf "%s: expected the dispatcher to refuse composition" label
        with :? System.InvalidOperationException as ex ->
            ex

    [<Tests>]
    let tests =
        testList "Phase 727 — attribute recognition beyond the auth classifier" [

            // ══ Audit: [<PiiSafe>] — the data-exposure family ══════════
            test "a forged PiiSafe no longer un-redacts an audit payload field" {
                // The sharpest case of the four. Pre-727 `isPiiSafe`
                // matched on the simple name, so this foreign attribute
                // put the value verbatim into the emitted row — and audit
                // rows leave the deployment through every composed sink.
                let value: ForeignPiiInput = {
                    Email = "alice@example.com"
                    Note = "unmarked"
                }

                let payload = Audit.payloadFromInputRecord typeof<ForeignPiiInput> value

                Expect.equal
                    payload["Email"]
                    "<redacted:String>"
                    "a consumer attribute named PiiSafeAttribute must not defeat redaction — \
                     pre-727 this emitted 'alice@example.com' into the audit row"

                Expect.equal payload["Note"] "<redacted:String>" "the unmarked field is redacted as before"
            }

            test "both sanctioned PiiSafe families still include the field (GP 11)" {
                let server: SanctionedServerPiiInput = {
                    Email = "alice@example.com"
                    Note = "unmarked"
                }

                let mirror: SanctionedMirrorPiiInput = {
                    Email = "alice@example.com"
                    Note = "unmarked"
                }

                let serverPayload =
                    Audit.payloadFromInputRecord typeof<SanctionedServerPiiInput> server

                let mirrorPayload =
                    Audit.payloadFromInputRecord typeof<SanctionedMirrorPiiInput> mirror

                Expect.equal serverPayload["Email"] "alice@example.com" "the server-tier marker still opts the field in"
                Expect.equal mirrorPayload["Email"] "alice@example.com" "the tier-shared mirror still opts the field in"

                Expect.equal
                    serverPayload["Note"]
                    "<redacted:String>"
                    "redaction remains the default for unmarked fields"
            }

            // ══ Audit: [<Audit>] ══════════════════════════════════════
            test "a forged Audit attribute no longer arms emission" {
                let cls = Audit.classify typeof<ForeignAuditApi>

                Expect.isFalse
                    (cls.ContainsKey "Move")
                    "a consumer attribute named AuditAttribute must not decide that a method emits audit rows"
            }

            test "both sanctioned Audit families still classify (GP 11)" {
                let cls = Audit.classify typeof<SanctionedAuditApi>
                Expect.equal cls["ServerTier"] AuditKind.MoneyMoved "the server-tier attribute still classifies"
                Expect.equal cls["Mirror"] AuditKind.PolicyChanged "the tier-shared mirror still classifies"
            }

            test "Audit.foreignMarkers reaches BOTH the API record and an audited input record" {
                let onApi = Audit.foreignMarkers typeof<ForeignAuditApi>

                match onApi with
                | [ (surface, subject, attrType) ] ->
                    Expect.equal surface "audit emission" "the finding names the guard the consumer believes declared"
                    Expect.equal subject "Move" "the finding names the record field"

                    Expect.stringContains
                        attrType
                        "Contoso.Consumer.Preflight.AuditAttribute"
                        "the finding names the offending attribute's namespace-qualified type"
                | other -> failtestf "expected exactly one API-record collision, got %A" other

                // The PII scan reaches through `inputTypes`, so it only
                // sees the input records of methods that are genuinely
                // audited — a different record from the one the [<Audit>]
                // scan walks.
                let onInput = Audit.foreignMarkers typeof<ForeignPiiOnAuditedInputApi>

                match onInput with
                | [ (surface, subject, attrType) ] ->
                    Expect.equal surface "PII-safe audit payload" "the finding names the PII surface"

                    Expect.equal
                        subject
                        "Read input field Email"
                        "the finding names the method AND the input-record field, since the marker is not on the API record"

                    Expect.stringContains
                        attrType
                        "Contoso.Consumer.Preflight.PiiSafeAttribute"
                        "the finding names the offending attribute"
                | other -> failtestf "expected exactly one input-record collision, got %A" other

                Expect.isEmpty
                    (Audit.foreignMarkers typeof<SanctionedAuditApi>)
                    "the sanctioned families are not collisions (GP 11)"
            }

            // ══ Rate limiting ═════════════════════════════════════════
            test "a forged RateLimit budget no longer applies" {
                let cls = RateLimit.classify typeof<ForeignRateLimitApi>

                Expect.isEmpty
                    cls["Burst"]
                    "a consumer attribute named RateLimitAttribute must not impose a budget — its Count / \
                     WindowSeconds need not mean what this evaluator reads them as"
            }

            test "both sanctioned RateLimit families still classify (GP 11)" {
                let cls = RateLimit.classify typeof<SanctionedRateLimitApi>

                match cls["ServerTier"] with
                | [ budget ] ->
                    Expect.equal budget.Count 3 "server-tier count"
                    Expect.equal budget.WindowSeconds 60 "server-tier window"
                | other -> failtestf "ServerTier: expected one budget, got %A" other

                match cls["Mirror"] with
                | [ budget ] ->
                    Expect.equal budget.Count 7 "mirror count normalised to the server-tier shape"
                    Expect.equal budget.WindowSeconds 60 "mirror window"
                | other -> failtestf "Mirror: expected one budget, got %A" other
            }

            // ══ Idempotency ═══════════════════════════════════════════
            test "a forged Idempotent marker no longer arms memoisation" {
                let cls = Idempotency.classify typeof<ForeignIdempotentApi>

                Expect.isFalse
                    (cls.Contains "Charge")
                    "a consumer attribute named IdempotentAttribute must not decide that a response is replayed \
                     instead of the handler running"
            }

            test "both sanctioned Idempotent families still classify (GP 11)" {
                let cls = Idempotency.classify typeof<SanctionedIdempotentApi>
                Expect.isTrue (cls.Contains "ServerTier") "the server-tier marker still arms idempotency"
                Expect.isTrue (cls.Contains "Mirror") "the tier-shared mirror still arms idempotency"
            }

            // ══ The startup refusal through the real composition path ══
            test "Api.make refuses on a forged marker in each fixed family" {
                // Identity matching alone would DISARM these guards
                // silently, which for all three is the sharper direction.
                // The refusal is what makes the tightening safe, so it is
                // pinned through the composition path rather than only at
                // the classifier.
                let auditBuilder (_: HttpContext) : ForeignAuditApi = { Move = fun () -> async { return 1 } }

                let ex =
                    refusalOf (fun () -> ToolUp.Platform.Api.make auditBuilder |> ignore) "forged Audit"

                Expect.stringContains ex.Message "ForeignAuditApi" "the diagnostic names the record"
                Expect.stringContains ex.Message "Move" "the diagnostic names the field"
                Expect.stringContains ex.Message "audit emission" "the diagnostic names the surface"

                Expect.stringContains
                    ex.Message
                    "Contoso.Consumer.Preflight.AuditAttribute"
                    "the diagnostic names the offending attribute, not merely that a guard is absent"

                let rlBuilder (_: HttpContext) : ForeignRateLimitApi = { Burst = fun () -> async { return 1 } }

                let rlEx =
                    refusalOf (fun () -> ToolUp.Platform.Api.make rlBuilder |> ignore) "forged RateLimit"

                Expect.stringContains rlEx.Message "rate limiting" "the rate-limit surface is named"

                Expect.stringContains
                    rlEx.Message
                    "Contoso.Consumer.Preflight.RateLimitAttribute"
                    "the rate-limit diagnostic names the offending attribute"

                let idBuilder (_: HttpContext) : ForeignIdempotentApi = {
                    Charge = fun () -> async { return 1 }
                }

                let idEx =
                    refusalOf (fun () -> ToolUp.Platform.Api.make idBuilder |> ignore) "forged Idempotent"

                Expect.stringContains idEx.Message "idempotency" "the idempotency surface is named"

                Expect.stringContains
                    idEx.Message
                    "Contoso.Consumer.Preflight.IdempotentAttribute"
                    "the idempotency diagnostic names the offending attribute"
            }

            test "the sanctioned families still compose cleanly through Api.make (GP 11)" {
                let auditBuilder (_: HttpContext) : SanctionedAuditApi = {
                    ServerTier = fun () -> async { return 1 }
                    Mirror = fun () -> async { return 2 }
                }

                let rlBuilder (_: HttpContext) : SanctionedRateLimitApi = {
                    ServerTier = fun () -> async { return 1 }
                    Mirror = fun () -> async { return 2 }
                }

                let idBuilder (_: HttpContext) : SanctionedIdempotentApi = {
                    ServerTier = fun () -> async { return 1 }
                    Mirror = fun () -> async { return 2 }
                }

                ToolUp.Platform.Api.make auditBuilder |> ignore
                ToolUp.Platform.Api.make rlBuilder |> ignore
                ToolUp.Platform.Api.make idBuilder |> ignore
            }

            // ══ The streaming diagnostic ══════════════════════════════
            test "a forged auth marker gets the SAME verdict from the classifier and the streaming diagnostic" {
                // The alignment this phase is named for. Both matchers now
                // say "not one of ours"; pre-727 the classifier said that
                // and the diagnostic said "the consumer declared
                // RequiresRole(\"Admin\")".
                let cls = AuthClassifier.classify typeof<ForgedGuardStreamApi>
                Expect.equal cls["Tail"] Unclassified "the classifier does not honour the forged marker"

                Expect.hasLength
                    (AuthClassifier.foreignMarkers typeof<ForgedGuardStreamApi>)
                    1
                    "the classifier reports it as a collision"

                match Streaming.streamingMethodsCarryingUnenforceableAttributes typeof<ForgedGuardStreamApi> with
                | [ (name, attrs) ] ->
                    Expect.equal name "Tail" "the streaming method is still flagged — the refusal is never weakened"

                    Expect.isFalse
                        (attrs |> List.exists _.StartsWith("RequiresRole("))
                        "the diagnostic no longer asserts the consumer declared a guard this SDK would honour"

                    Expect.isTrue
                        (attrs
                         |> List.exists (fun a ->
                             a.Contains "foreign"
                             && a.Contains "Contoso.Consumer.Preflight.RequiresRoleAttribute"))
                        "it is rendered as a name collision, naming the offending type"
                | other -> failtestf "expected the forged marker to still flag the method, got %A" other
            }

            test "a tier-shared RateLimit mirror on a streaming method is flagged" {
                // The one genuine hole behind the alignment: pre-727 this
                // matched NEITHER the server-tier type test nor any name
                // arm, so the adapter started and the SSE short-circuit
                // dropped the budget silently — on exactly the record
                // family (Fable-compiled Core) that carries the mirror.
                match Streaming.streamingMethodsCarryingUnenforceableAttributes typeof<MirrorRateLimitStreamApi> with
                | [ (name, attrs) ] ->
                    Expect.equal name "Tail" "the streaming method is named"
                    Expect.contains attrs "RateLimit(5, 60s)" "the mirror budget is recognised and rendered"
                | other -> failtestf "expected the mirror budget to flag the method, got %A" other
            }

            // ══ Validation — the family that KEEPS simple-name matching ══
            test "validation still honours a same-named consumer attribute (deliberate)" {
                // Recorded verdict, pinned so a later sweep does not flip
                // it from consistency with the other three alone. A forged
                // validator can only ADD a constraint — it cannot grant
                // access, expose a value, or suppress a sibling validator
                // — while tightening here would silently stop validating
                // input for a consumer whose own mirror is picked up
                // today, with no refusal available to say so (the marker
                // names collide with System.ComponentModel.DataAnnotations,
                // which legitimately sits on the same records).
                let cls = Validation.classify typeof<ForeignValidatedApi>

                Expect.isTrue
                    (cls.ContainsKey "Submit")
                    "the validation family recognises a same-named attribute exposing the mirror's property — \
                     see the Phase 727 assessment at the head of Validation.fs for why this is kept"
            }

            test "the BCL DataAnnotations family is NOT honoured, and does not refuse startup" {
                // What keeps the widest collision surface of the four safe
                // is the TYPED property read, not the name: DataAnnotations'
                // MinLengthAttribute exposes `Length`, not `MinLength`.
                // That was true by luck rather than by design, so it is
                // pinned here — an arm added to `tryNormalise` that reads a
                // property name the BCL family also exposes would start
                // honouring BCL attributes, and this is what would say so.
                let cls = Validation.classify typeof<DataAnnotationsApi>

                Expect.isFalse
                    (cls.ContainsKey "Submit")
                    "a DataAnnotations attribute placed for EF Core / MVC reasons must not become an API validator"

                // And the refusal deliberately does not cover validation:
                // refusing here would break a correct deployment on an SDK
                // upgrade, for no defect (GP 11).
                let builder (_: HttpContext) : DataAnnotationsApi =
                    fun (_: DataAnnotationsInput) -> async { return 1 }
                    |> fun submit -> { Submit = submit }

                ToolUp.Platform.Api.make builder |> ignore
            }
        ]